/// The reference dialect, for tests only (Phase 1900): an ANSI-shaped rendering, and an in-memory
/// interpreter of the statement's ABSTRACT FORM, so the placement laws need no database.
///
/// The interpreter gives the form SQL's relational meaning where SQL and the reference evaluator
/// part — a keyless aggregate over no rows answers one row unless the form says `NonEmpty`, a join
/// key that is null matches nothing, an ordering is the form's keys with nulls last and nothing else
/// — and borrows the reference evaluator's SCALAR and AGGREGATE semantics through its exposed
/// primitives (`evalExprInRow`, `aggregateCells`, `rowCompareBy`), which is what a dialect certified
/// on the declared subset is held to. It reads only what the statement carries: physical
/// identifiers, minted aliases and parameter ordinals, never a pipeline name.
module Fuaran.DB.Tests.ReferenceDialect

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB.Provider

let private quote (s: string) = "\"" + s.Replace("\"", "\"\"") + "\""

let private operator (op: BinOp) : string =
    match op with
    | Add -> "+"
    | Sub -> "-"
    | Mul -> "*"
    | Div -> "/"
    | Mod -> "%"
    | Eq -> "="
    | Ne -> "<>"
    | Lt -> "<"
    | Le -> "<="
    | Gt -> ">"
    | Ge -> ">="
    | And -> "AND"
    | Or -> "OR"
    | Contains -> "CONTAINS"
    | StartsWith -> "STARTS_WITH"
    | EndsWith -> "ENDS_WITH"

let private render (c: Construct) (args: string list) : string =
    let call (name: string) =
        name + "(" + String.concat ", " args + ")"

    match c, args with
    | Construct.Operator op, [ a; b ] -> "(" + a + " " + operator op + " " + b + ")"
    | Construct.Aggregate CountDistinct, [ a ] -> "COUNT(DISTINCT " + a + ")"
    | Construct.Aggregate fn, _ -> call ((sprintf "%A" fn).ToUpperInvariant())
    | Construct.Function f, _ -> call ((sprintf "%A" f).ToUpperInvariant())
    | Construct.Cast ty, [ a ] -> "CAST(" + a + " AS " + ColumnType.tag ty + ")"
    | _ -> call "UNRENDERABLE"

/// The reference dialect: double-quoted identifiers, `@pN` markers, nulls-last ordering spelled with
/// a null flag, and one declared refusal (the substring operators, which recipe 07 found an engine's
/// pattern matching does not keep).
let dialect: Dialect =
    { Name = "reference"
      QuoteIdentifier = quote
      ParameterMarker = fun i -> "@p" + string i
      ReadValue = fun _ value -> value
      RenderFunction = render
      RenderAggregate = fun fn _ a -> render (Construct.Aggregate fn) [ a ]
      ValueAsCondition = id
      ConditionAsValue = id
      RenderPaging =
        fun keys limit offset ->
            "ORDER BY "
            + (keys
               |> List.collect (fun (k, dir) ->
                   [ "CASE WHEN " + k + " IS NULL THEN 1 ELSE 0 END"
                     k + (if dir = Asc then " ASC" else " DESC") ])
               |> String.concat ", ")
            + " LIMIT "
            + limit
            + " OFFSET "
            + offset
      MapType = ColumnType.tag
      Refuses =
        fun c ->
            match c with
            | Construct.Operator Contains
            | Construct.Operator StartsWith
            | Construct.Operator EndsWith ->
                Some "an engine's pattern matching folds case and reads wildcards (recipe 07)"
            | _ -> None
      // The reference engine keeps no change counter; a source over it is fingerprinted by row count.
      ChangeCounter = None }

// ---- the interpreter ----

/// A relation's rows, with the alias schema they are read by.
type private Rows = { Schema: Schema; Rows: Cell[] list }

let rec private toExpr (parameters: Parameter list) (s: Scalar) : ColExpr =
    let go = toExpr parameters

    match s with
    | Scalar.Column a -> Col a
    | Scalar.Parameter i -> Lit (List.item i parameters).Value
    | Scalar.Function(f, args) -> ApplyFn(f, args |> List.map go)
    | Scalar.Operator(op, a, b) -> Binary(op, go a, go b)
    | Scalar.Not x -> Not(go x)
    | Scalar.IsNull x -> IsNull(go x)
    | Scalar.Coalesce xs -> Coalesce(xs |> List.map go)
    | Scalar.Case(cases, otherwise) -> Case(cases |> List.map (fun (w, t) -> go w, go t), go otherwise)
    | Scalar.InList(x, items) -> InList(go x, items |> List.map go)
    | Scalar.Cast(ty, x) -> Cast(ty, go x)

let private index (schema: Schema) (alias: string) =
    schema |> List.findIndex (fun f -> f.Name = alias)

let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    let rec go acc =
        function
        | [] -> Ok(List.rev acc)
        | x :: rest ->
            match f x with
            | Ok y -> go (y :: acc) rest
            | Error e -> Error e

    go [] xs

/// A join key matches when both cells are present and equal.
let private keyEq (a: Cell) (b: Cell) =
    not (Cell.isNull a) && not (Cell.isNull b) && Cell.compare a b = Some 0

let rec private run
    (tables: Map<string, Table>)
    (parameters: Parameter list)
    (nullsFirst: bool)
    (keylessAlwaysRow: bool)
    (r: Relation)
    : Result<Rows, EvalError> =
    let go = run tables parameters nullsFirst keylessAlwaysRow

    let schemaOf (outputs: Output list) =
        outputs |> List.map (fun o -> Field.create o.Alias o.Type)

    match r with
    | Relation.Scan(table, columns) ->
        match Map.tryFind table tables with
        | None -> Error(UnresolvedSource table)
        | Some t ->
            let cells =
                columns
                |> List.map (fun (physical, _) ->
                    Column.toCells (t.Columns |> List.find (fun c -> c.Name = physical))
                    |> List.toArray)

            Ok
                { Schema = columns |> List.map snd |> schemaOf
                  Rows = [ for i in 0 .. Table.rowCount t - 1 -> cells |> List.map (fun c -> c[i]) |> List.toArray ] }
    | Relation.Where(input, predicate) ->
        go input
        |> Result.bind (fun rows ->
            let e = toExpr parameters predicate

            rows.Rows
            |> traverse (fun row ->
                DataFrame.evalExprInRow Map.empty rows.Schema (List.ofArray row) e
                |> Result.map (fun c -> if c = Bool true then Some row else None))
            |> Result.map (fun kept -> { rows with Rows = List.choose id kept }))
    | Relation.Select(input, columns) ->
        go input
        |> Result.map (fun rows ->
            let at = columns |> List.map (fun (a, _) -> index rows.Schema a)

            { Schema = columns |> List.map snd |> schemaOf
              Rows =
                rows.Rows
                |> List.map (fun row -> at |> List.map (fun i -> row[i]) |> List.toArray) })
    | Relation.Derive(input, columns) ->
        go input
        |> Result.bind (fun rows ->
            let exprs = columns |> List.map (fun (s, _) -> toExpr parameters s)

            rows.Rows
            |> traverse (fun row ->
                exprs
                |> traverse (DataFrame.evalExprInRow Map.empty rows.Schema (List.ofArray row))
                |> Result.map List.toArray)
            |> Result.map (fun out ->
                { Schema = columns |> List.map snd |> schemaOf
                  Rows = out }))
    | Relation.Join(left, right, kind, on, columns) ->
        go left
        |> Result.bind (fun l ->
            go right
            |> Result.map (fun rr ->
                let keys = on |> List.map (fun (a, b) -> index l.Schema a, index rr.Schema b)

                let matches (lrow: Cell[]) (rrow: Cell[]) =
                    keys |> List.forall (fun (i, j) -> keyEq lrow[i] rrow[j])

                let pick (lrow: Cell[]) (rrow: Cell[] option) =
                    columns
                    |> List.map (fun (p, o) ->
                        match p with
                        | Pick.Left a -> lrow[index l.Schema a]
                        | Pick.Right a ->
                            match rrow with
                            | Some row -> row[index rr.Schema a]
                            | None -> Null)
                    |> List.toArray

                let rows =
                    l.Rows
                    |> List.collect (fun lrow ->
                        let hits = rr.Rows |> List.filter (matches lrow)

                        match kind with
                        | Semi -> if List.isEmpty hits then [] else [ pick lrow None ]
                        | Anti -> if List.isEmpty hits then [ pick lrow None ] else []
                        | Inner -> hits |> List.map (fun h -> pick lrow (Some h))
                        | Left
                        | Right
                        | Outer ->
                            if List.isEmpty hits then
                                [ pick lrow None ]
                            else
                                hits |> List.map (fun h -> pick lrow (Some h)))

                { Schema = columns |> List.map snd |> schemaOf
                  Rows = rows }))
    | Relation.Aggregate(input, keys, aggregates, nonEmpty) ->
        go input
        |> Result.bind (fun rows ->
            let keyAt = keys |> List.map (fun (a, _) -> index rows.Schema a)

            let groups =
                rows.Rows
                |> List.groupBy (fun row -> keyAt |> List.map (fun i -> Cell.token row[i]))
                |> List.map snd

            // SQL answers one row for a keyless aggregate over no rows; `NonEmpty` drops it.
            let groups =
                if List.isEmpty keys && List.isEmpty groups && (keylessAlwaysRow || not nonEmpty) then
                    [ [] ]
                else
                    groups

            groups
            |> traverse (fun members ->
                let keyCells =
                    match members with
                    | first :: _ -> keyAt |> List.map (fun i -> first[i])
                    | [] -> keyAt |> List.map (fun _ -> Null)

                aggregates
                |> traverse (fun (fn, a, _) ->
                    let i = index rows.Schema a
                    DataFrame.aggregateCells fn rows.Schema[i].Type (members |> List.map (fun row -> row[i])))
                |> Result.map (fun cells -> keyCells @ cells |> List.toArray))
            |> Result.map (fun out ->
                { Schema =
                    (keys |> List.map snd) @ (aggregates |> List.map (fun (_, _, o) -> o))
                    |> schemaOf
                  Rows = out }))
    | Relation.Ordered(input, keys, limit, offset) ->
        go input
        |> Result.map (fun rows ->
            let count (ordinal: int) =
                match (List.item ordinal parameters).Value with
                | Int n -> n
                | _ -> 0

            let order =
                if nullsFirst then
                    // A deliberately wrong engine, for the laws' falsifier: nulls first ascending.
                    fun (a: Cell list) (b: Cell list) ->
                        let flagged (row: Cell list) =
                            keys
                            |> List.map (fun (k, _) ->
                                if Cell.isNull row[index rows.Schema k] then
                                    Int 0
                                else
                                    Int 1)

                        match compare (flagged a) (flagged b) with
                        | 0 -> DataFrame.rowCompareBy rows.Schema keys a b
                        | c -> c
                else
                    DataFrame.rowCompareBy rows.Schema keys

            let sorted =
                rows.Rows
                |> List.map List.ofArray
                |> List.sortWith order
                |> List.map List.toArray

            { rows with
                Rows =
                    sorted
                    |> List.skip (min (count offset) sorted.Length)
                    |> List.truncate (count limit) })

/// Run a statement's form over tables keyed and named physically: its rows, positionally.
let executeWith (nullsFirst: bool) (keylessAlwaysRow: bool) : PlacementLaws.Executor =
    fun statement tables ->
        run tables statement.Parameters nullsFirst keylessAlwaysRow statement.Form
        |> Result.map (fun rows -> rows.Rows |> List.map List.ofArray)

/// The reference engine.
let execute: PlacementLaws.Executor = executeWith false false
