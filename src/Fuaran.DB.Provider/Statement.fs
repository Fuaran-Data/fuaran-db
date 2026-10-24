namespace Fuaran.DB.Provider

open Fuaran.Core
open Fuaran.Compute

// ============================================================================
//  The statement (Phase 1900) — what the provider answers: text, an ordered list
//  of typed parameter cells, the result schema, and the ABSTRACT FORM the text was
//  rendered from.
//
//  The form is the statement as data. Two properties hold of it by construction,
//  and the placement laws hold them of every statement the provider emits:
//
//   * NO VALUE IS TEXT. `Scalar` has no literal case: a value the pipeline names
//     (a literal, a bound parameter, a bound list, a pinned clock reading, a page
//     size) reaches the form only as a `Parameter` ordinal into the statement's
//     parameter list.
//   * NO IDENTIFIER COMES FROM THE REQUEST. A `Scan` names the model's PHYSICAL
//     table and column identifiers; every other identifier is an alias the
//     provider minted (`c1`, `c2`, …). The pipeline's own names — a column's model
//     name, a rename, an aggregate's name — ride `Output.Name`, which is never
//     rendered: the statement's result is read POSITIONALLY into them.
// ============================================================================

/// A construct of the transform algebra a dialect renders and may refuse. The provider asks the
/// dialect about each one it is about to emit, and a refused one places the fold boundary.
[<RequireQualifiedAccess>]
type Construct =
    /// A scalar function.
    | Function of ScalarFn
    /// A binary operator: arithmetic, comparison, logical or substring predicate.
    | Operator of BinOp
    /// An aggregate in a grouping.
    | Aggregate of AggFn
    /// A join of this kind.
    | Join of JoinKind
    /// A parameter of this column type.
    | Parameter of ColumnType
    /// A cast to this type. Outside the provider's declared subset (DECISIONS.md D4.4).
    | Cast of ColumnType
    /// An exact rounding (`Quotient`, `Rounded`) under this mode. Outside the declared subset.
    | Rounding of RoundingMode
    /// A membership test against an empty list, which SQL cannot spell. Outside the declared subset.
    | EmptyList

/// A scalar expression of the statement, over the columns of the relation it is evaluated in.
/// There is no literal case: every value is a parameter.
[<RequireQualifiedAccess>]
type Scalar =
    /// A column of the input relation, by its minted alias.
    | Column of alias: string
    /// The statement's parameter at this 0-based ordinal.
    | Parameter of ordinal: int
    /// A scalar function over its arguments.
    | Function of ScalarFn * Scalar list
    /// A binary operator.
    | Operator of BinOp * Scalar * Scalar
    /// Three-valued negation.
    | Not of Scalar
    /// The presence test; always a boolean.
    | IsNull of Scalar
    /// The first present argument.
    | Coalesce of Scalar list
    /// The first branch whose condition holds, else the otherwise branch.
    | Case of cases: (Scalar * Scalar) list * otherwise: Scalar
    /// Three-valued membership in a non-empty list.
    | InList of Scalar * Scalar list

/// One column a relation produces: the alias the statement knows it by, and its field: the name the
/// pipeline knows it by, its type, and the unit, label and description it carries (DECISIONS.md D12).
/// `Name` is never rendered.
type Output =
    {
        /// The minted alias, unique within the statement.
        Alias: string
        /// The column's field. Its name is the pipeline's (a model name, or a name a step gave it), its
        /// type the reference evaluator's at that step, and its unit the model's or the step's.
        Field: Field
    }

    /// The pipeline's name for the column.
    member o.Name = o.Field.Name
    /// The column's type, as the reference evaluator types it at that step.
    member o.Type = o.Field.Type

/// Where a column of a join's output comes from.
[<RequireQualifiedAccess>]
type Pick =
    /// The left relation's column with this alias.
    | Left of alias: string
    /// The right relation's column with this alias.
    | Right of alias: string

/// The statement's abstract form: a relation built step by step, each step wrapping the last. Every
/// case is one `SELECT` when rendered.
[<RequireQualifiedAccess>]
type Relation =
    /// The model table with this PHYSICAL identifier, its physical columns read as these outputs.
    | Scan of table: string * columns: (string * Output) list
    /// The rows of the input whose predicate holds.
    | Where of Relation * Scalar
    /// The input's columns, by alias, kept or renamed as these outputs, in this order.
    | Select of Relation * columns: (string * Output) list
    /// A join on `(left alias, right alias)` key equalities. A null key matches nothing. `Semi` and
    /// `Anti` keep the left columns only.
    | Join of
        left: Relation *
        right: Relation *
        kind: JoinKind *
        on: (string * string) list *
        columns: (Pick * Output) list
    /// One row per distinct key, keys then aggregates. `NonEmpty` drops the one row a keyless
    /// aggregate over no rows answers in SQL, which the reference evaluator does not answer.
    | Aggregate of Relation * keys: (string * Output) list * aggregates: (AggFn * string * Output) list * nonEmpty: bool
    /// The input ordered by the keys — nulls last in either direction, as the reference orders them —
    /// then `offset` rows skipped and `limit` taken, both parameter ordinals.
    | Ordered of Relation * keys: (string * SortDir) list * limit: int * offset: int

/// One bound parameter of a statement.
type Parameter =
    {
        /// Its 0-based position in the statement's parameter list.
        Ordinal: int
        /// The marker the text carries for it, as the dialect writes one.
        Marker: string
        /// The value.
        Value: Cell
        /// The value's column type, decided by the value or, for a null, by the expression around it.
        Type: ColumnType
        /// The dialect's name for that type.
        DialectType: string
    }

/// A statement: the text a dialect rendered, its parameters in marker order, the result schema in
/// the pipeline's names (read positionally), and the form the text was rendered from.
type Statement =
    {
        /// The statement text. Every identifier in it is a physical identifier of the model or an
        /// alias the provider minted; every value is a parameter marker.
        Text: string
        /// The parameters, in ordinal order.
        Parameters: Parameter list
        /// The result columns, in the order the statement selects them.
        Columns: Output list
        /// The abstract form `Text` renders.
        Form: Relation
        /// The statement timeout, in seconds, from the cost limits the pipeline was admitted under
        /// (DECISIONS.md D5). `Some` on every statement `FoldSplit.lower` answers; `None` only
        /// from the placement laws, which certify where the split falls and admit nothing.
        TimeoutSeconds: int option
    }

/// Reading a relation and a statement.
[<RequireQualifiedAccess>]
module Relation =

    /// The columns a relation produces, in order.
    let rec outputs (r: Relation) : Output list =
        match r with
        | Relation.Scan(_, columns) -> columns |> List.map snd
        | Relation.Where(input, _) -> outputs input
        | Relation.Select(_, columns) -> columns |> List.map snd
        | Relation.Join(_, _, _, _, columns) -> columns |> List.map snd
        | Relation.Aggregate(_, keys, aggregates, _) ->
            (keys |> List.map snd) @ (aggregates |> List.map (fun (_, _, o) -> o))
        | Relation.Ordered(input, _, _, _) -> outputs input

    /// Every physical identifier a relation names — its scanned tables and their columns — in order.
    let rec physicalIdentifiers (r: Relation) : string list =
        match r with
        | Relation.Scan(table, columns) -> table :: (columns |> List.map fst)
        | Relation.Where(input, _)
        | Relation.Select(input, _)
        | Relation.Aggregate(input, _, _, _)
        | Relation.Ordered(input, _, _, _) -> physicalIdentifiers input
        | Relation.Join(left, right, _, _, _) -> physicalIdentifiers left @ physicalIdentifiers right

    let rec private scalarAliases (s: Scalar) : string list =
        match s with
        | Scalar.Column a -> [ a ]
        | Scalar.Parameter _ -> []
        | Scalar.Function(_, args)
        | Scalar.Coalesce args -> args |> List.collect scalarAliases
        | Scalar.Operator(_, a, b) -> scalarAliases a @ scalarAliases b
        | Scalar.Not x
        | Scalar.IsNull x -> scalarAliases x
        | Scalar.Case(cases, otherwise) ->
            (cases |> List.collect (fun (w, t) -> scalarAliases w @ scalarAliases t))
            @ scalarAliases otherwise
        | Scalar.InList(x, items) -> scalarAliases x @ (items |> List.collect scalarAliases)

    /// Every alias a relation mints or reads, with duplicates.
    let rec aliases (r: Relation) : string list =
        let minted = outputs r |> List.map _.Alias

        let read =
            match r with
            | Relation.Scan _ -> []
            | Relation.Where(input, p) -> aliases input @ scalarAliases p
            | Relation.Select(input, columns) -> aliases input @ (columns |> List.map fst)
            | Relation.Join(left, right, _, on, columns) ->
                aliases left
                @ aliases right
                @ (on |> List.collect (fun (a, b) -> [ a; b ]))
                @ (columns
                   |> List.map (fun (p, _) ->
                       match p with
                       | Pick.Left a
                       | Pick.Right a -> a))
            | Relation.Aggregate(input, keys, aggregates, _) ->
                aliases input
                @ (keys |> List.map fst)
                @ (aggregates |> List.map (fun (_, a, _) -> a))
            | Relation.Ordered(input, keys, _, _) -> aliases input @ (keys |> List.map fst)

        minted @ read

    let rec private scalarParameters (s: Scalar) : int list =
        match s with
        | Scalar.Column _ -> []
        | Scalar.Parameter i -> [ i ]
        | Scalar.Function(_, args)
        | Scalar.Coalesce args -> args |> List.collect scalarParameters
        | Scalar.Operator(_, a, b) -> scalarParameters a @ scalarParameters b
        | Scalar.Not x
        | Scalar.IsNull x -> scalarParameters x
        | Scalar.Case(cases, otherwise) ->
            (cases |> List.collect (fun (w, t) -> scalarParameters w @ scalarParameters t))
            @ scalarParameters otherwise
        | Scalar.InList(x, items) -> scalarParameters x @ (items |> List.collect scalarParameters)

    /// Every parameter ordinal a relation references, with duplicates, in the order the text
    /// renders them.
    let rec parameters (r: Relation) : int list =
        match r with
        | Relation.Scan _ -> []
        | Relation.Where(input, p) -> parameters input @ scalarParameters p
        | Relation.Select(input, _)
        | Relation.Aggregate(input, _, _, _) -> parameters input
        | Relation.Join(left, right, _, _, _) -> parameters left @ parameters right
        | Relation.Ordered(input, _, limit, offset) -> parameters input @ [ limit; offset ]

/// Reading a statement's result.
[<RequireQualifiedAccess>]
module Statement =

    /// The result schema in the pipeline's names, each column's unit and metadata included: what the
    /// local suffix reads, so the evaluator re-checks a pushed-down result's units.
    let schema (s: Statement) : Schema = s.Columns |> List.map _.Field

    /// The statement's rows as a table in the pipeline's names, read POSITIONALLY: the i-th cell of
    /// every row is the i-th column. A row of another width, or a cell its column's type does not
    /// admit, is refused by name rather than read.
    let toTable (s: Statement) (rows: Cell list list) : Result<Table, ColumnError> =
        let width = List.length s.Columns

        match rows |> List.indexed |> List.tryFind (fun (_, r) -> List.length r <> width) with
        | Some(i, r) ->
            Error(
                ColumnError.MalformedShape(
                    sprintf "result row %d carries %d cell(s); the statement selects %d" i (List.length r) width
                )
            )
        | None ->
            let columns =
                s.Columns
                |> List.mapi (fun j o -> Column.ofCells o.Name o.Type (rows |> List.map (fun r -> List.item j r)))
                |> List.fold (fun acc c -> acc |> Result.bind (fun cs -> c |> Result.map (fun c -> c :: cs))) (Ok [])
                |> Result.map List.rev

            columns
            |> Result.bind (fun columns ->
                let table = { Schema = schema s; Columns = columns }
                Table.validate table |> Result.map (fun () -> table))
