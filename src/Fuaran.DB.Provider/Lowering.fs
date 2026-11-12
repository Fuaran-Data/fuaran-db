namespace Fuaran.DB.Provider

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

// ============================================================================
//  The lowering (Phase 1900) — one planned step at a time onto the statement's
//  abstract form, or the refusal that places the fold boundary there.
//
//  Every match over the transform algebra here is exhaustive with no catch-all,
//  so a verb, an expression form, a function or an aggregate the algebra gains
//  stops this build until it is decided whether it lowers (DECISIONS.md D4).
// ============================================================================

/// One column of the relation built so far.
type internal Col =
    {
        Output: Output
        /// The model column it came from, as `(table id, column id)`, followed through renames and
        /// grouping keys; `None` for an aggregate.
        Origin: (string * string) option
    }

/// The statement so far.
type internal Built =
    {
        Relation: Relation
        Columns: Col list
        /// The parameters bound so far, `(value, type)`, in ordinal order.
        Params: (Cell * ColumnType) list
        /// The next alias number to mint.
        Next: int
    }

[<RequireQualifiedAccess>]
module internal Lowering =

    let private mint (n: int) = "c" + string n

    let schemaOf (cols: Col list) : Schema = cols |> List.map _.Output.Field

    let private members (cols: Col list) = cols |> List.map _.Output.Name

    let private tryCol (name: string) (cols: Col list) =
        cols |> List.tryFind (fun c -> c.Output.Name = name)

    /// The first name a list carries twice.
    let private repeated (names: string list) : string option =
        names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) |> Option.map fst

    /// `f` over the list LEFT TO RIGHT, stopping at the first error: the order parameters are bound in
    /// is the order the text renders their markers in.
    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        let rec go acc =
            function
            | [] -> Ok(List.rev acc)
            | x :: rest ->
                match f x with
                | Ok y -> go (y :: acc) rest
                | Error e -> Error e

        go [] xs

    let private firstError (checks: (unit -> Refusal option) list) : Refusal option =
        checks |> List.tryPick (fun c -> c ())

    /// The scan of one model table: every column, physical identifier read as a minted alias.
    let scan (table: ModelNode) (spec: TableSpec) (next: int) : Relation * Col list * int =
        let columns =
            Model.columns table
            |> List.mapi (fun i (node, c) ->
                c.Physical,
                { Output =
                    { Alias = mint (next + i)
                      Field = c.Field }
                  Origin = Some(table.Id, node.Id) })

        Relation.Scan(spec.Physical, columns |> List.map (fun (p, c) -> p, c.Output)),
        columns |> List.map snd,
        next + List.length columns

    // ---- expressions ----

    /// The direct sub-expressions of an expression.
    let private children (e: ColExpr) : ColExpr list =
        match e with
        | Col _
        | Lit _
        | Param _
        | Now _ -> []
        | Binary(_, a, b)
        | Quotient(a, b, _) -> [ a; b ]
        | Not x
        | IsNull x
        | Cast(_, x)
        | Convert(x, _)
        | Rounded(x, _)
        | InParam(x, _)
        | Interpolate(x, _, _, _) -> [ x ]
        | Arg _ -> []
        | Coalesce xs
        | ApplyFn(_, xs)
        | Call(_, xs) -> xs
        | Case(cases, els) -> (cases |> List.collect (fun (w, t) -> [ w; t ])) @ [ els ]
        | InList(x, items) -> x :: items

    /// Every node of an expression, in preorder.
    let rec private nodes (e: ColExpr) : ColExpr list = e :: (children e |> List.collect nodes)

    /// How a cast lowers (DECISIONS.md D15.2).
    [<RequireQualifiedAccess>]
    type private CastForm =
        /// The operand already has the target type, or is a null the target types: it lowers as the
        /// operand, read at the target type.
        | Retyped
        /// A conversion every dialect is asked about: an int to a float or to a bool, a bool to an int.
        | Converted

    /// How a cast of `x` to `ty` lowers over the schema, or `None` when it is outside the declared
    /// subset. Only the pairs whose meaning no engine reinterprets lower: a string's formatting, a
    /// decimal's scale, a float's truncation and a parsed text are each an engine's own.
    let private castForm (schema: Schema) (ty: ColumnType) (x: ColExpr) : CastForm option =
        match x with
        | Lit Null -> Some CastForm.Retyped
        | _ ->
            match DataFrame.typeOf schema x, ty with
            | Some from, _ when from = ty -> Some CastForm.Retyped
            | Some IntType, (FloatType | BoolType)
            | Some BoolType, IntType -> Some CastForm.Converted
            | _ -> None

    /// The construct outside the provider's declared subset an expression node is, if any
    /// (DECISIONS.md D4.4, D15).
    let private excluded (schema: Schema) (e: ColExpr) : Construct option =
        match e with
        | Cast(ty, x) ->
            match castForm schema ty x with
            | Some _ -> None
            | None -> Some(Construct.Cast ty)
        // A conversion is a cast to float times its factor (Compute D29), so it is a cast here:
        // outside the declared subset, and evaluated locally.
        | Convert _ -> Some(Construct.Cast FloatType)
        | ApplyFn(Lower, _) -> Some(Construct.Function Lower)
        | ApplyFn(Upper, _) -> Some(Construct.Function Upper)
        | ApplyFn(Length, _) -> Some(Construct.Function Length)
        | Quotient(_, _, r)
        | Rounded(_, r) -> Some(Construct.Rounding r.Mode)
        | InList(_, []) -> Some Construct.EmptyList
        | Interpolate _ -> Some Construct.Interpolation
        // The planner expands every call before lowering (Compute D48); one left unexpanded is a
        // pipeline whose definitions the reference refuses, and the totality verdict keeps it local.
        | Call _
        | Arg _
        | ApplyFn _
        | Col _
        | Lit _
        | Param _
        | Now _
        | Binary _
        | Not _
        | IsNull _
        | InParam _
        | Coalesce _
        | Case _
        | InList _ -> None

    /// The construct a dialect is asked about for an expression node, if any.
    let private asked (schema: Schema) (e: ColExpr) : Construct option =
        match e with
        | ApplyFn(f, _) -> Some(Construct.Function f)
        | Binary(op, _, _) -> Some(Construct.Operator op)
        | Cast(ty, x) ->
            match castForm schema ty x with
            | Some CastForm.Converted -> Some(Construct.Cast ty)
            | Some CastForm.Retyped
            | None -> None
        | Col _
        | Lit _
        | Param _
        | Now _
        | Not _
        | IsNull _
        | Convert _
        | Rounded _
        | Quotient _
        | InParam _
        | Interpolate _
        | Call _
        | Arg _
        | Coalesce _
        | Case _
        | InList _ -> None

    /// Lower an expression whose checks have passed. `bind` appends a parameter and answers its
    /// ordinal, or the refusal of its type.
    let rec private lowerExpr
        (step: int)
        (cols: Col list)
        (bind: Cell -> ColumnType option -> Result<int, Refusal>)
        (expected: ColumnType option)
        (e: ColExpr)
        : Result<Scalar, Refusal> =
        let schema = schemaOf cols
        let typeOf = DataFrame.typeOf schema
        let go = lowerExpr step cols bind

        match e with
        | Col c ->
            match tryCol c cols with
            | Some col -> Ok(Scalar.Column col.Output.Alias)
            | None -> Error(Refusal.UnknownColumn(step, c, members cols))
        | Lit cell -> bind cell expected |> Result.map Scalar.Parameter
        | Binary(op, a, b) ->
            let ea, eb =
                match op with
                | And
                | Or -> Some BoolType, Some BoolType
                | Contains
                | StartsWith
                | EndsWith -> Some StringType, Some StringType
                | Add
                | Sub
                | Mul
                | Div
                | Mod
                | Eq
                | Ne
                | Lt
                | Le
                | Gt
                | Ge -> typeOf b, typeOf a

            go ea a
            |> Result.bind (fun la -> go eb b |> Result.map (fun lb -> Scalar.Operator(op, la, lb)))
        | Not x -> go (Some BoolType) x |> Result.map Scalar.Not
        | IsNull x -> go (typeOf x) x |> Result.map Scalar.IsNull
        | Coalesce xs ->
            let whole = typeOf e
            traverse (go whole) xs |> Result.map Scalar.Coalesce
        | Case(cases, els) ->
            let whole = typeOf e

            traverse
                (fun (w, t) ->
                    go (Some BoolType) w
                    |> Result.bind (fun lw -> go whole t |> Result.map (fun lt -> lw, lt)))
                cases
            |> Result.bind (fun lcases -> go whole els |> Result.map (fun lels -> Scalar.Case(lcases, lels)))
        | ApplyFn(f, xs) ->
            traverse (fun x -> go (typeOf x) x) xs
            |> Result.map (fun args -> Scalar.Function(f, args))
        | InList(x, items) ->
            let itemType = items |> List.tryPick typeOf

            go (typeOf x |> Option.orElse itemType) x
            |> Result.bind (fun lx ->
                traverse (go (typeOf x |> Option.orElse itemType)) items
                |> Result.map (fun litems -> Scalar.InList(lx, litems)))
        // The checks before lowering refuse each of these; the arms say what they would refuse, so
        // the lowering is total on its own.
        | Param n
        | InParam(_, n) -> Error(Refusal.UnboundParam(step, n))
        | Cast(ty, x) ->
            match castForm schema ty x with
            | Some CastForm.Retyped -> go (Some ty) x
            | Some CastForm.Converted -> go (typeOf x) x |> Result.map (fun lx -> Scalar.Cast(ty, lx))
            | None -> Error(Refusal.ConstructExcluded(step, Construct.Cast ty))
        | Now g -> Error(Refusal.UnpinnedClock(step, g))
        | Convert _ -> Error(Refusal.ConstructExcluded(step, Construct.Cast FloatType))
        | Quotient(_, _, r)
        | Rounded(_, r) -> Error(Refusal.ConstructExcluded(step, Construct.Rounding r.Mode))
        | Interpolate _ -> Error(Refusal.ConstructExcluded(step, Construct.Interpolation))
        | Call(name, _) -> Error(Refusal.MayRefuse(step, "the call of '" + name + "' reached the lowering unexpanded"))
        | Arg name -> Error(Refusal.MayRefuse(step, "the parameter '" + name + "' is read outside a function body"))

    /// The parameter binder over a statement under construction.
    let private binder (d: Dialect) (step: int) (b: Built) =
        let added = ResizeArray<Cell * ColumnType>()

        let bind (cell: Cell) (expected: ColumnType option) : Result<int, Refusal> =
            match Cell.typeOf cell |> Option.orElse expected with
            | None -> Error(Refusal.Untyped(step, "a null whose type nothing around it fixes"))
            | Some ty ->
                match d.Refuses(Construct.Parameter ty) with
                | Some why -> Error(Refusal.DialectRefuses(step, Construct.Parameter ty, why))
                | None ->
                    added.Add(cell, ty)
                    Ok(List.length b.Params + added.Count - 1)

        bind, (fun () -> b.Params @ List.ofSeq added)

    /// Why an expression over the relation built so far does not lower, before its type and its
    /// totality are asked about: a column the rows do not carry, an unpinned clock, an unbound
    /// parameter, a construct outside the declared subset, or one the dialect refuses.
    let private expressionRefusal (d: Dialect) (step: int) (cols: Col list) (e: ColExpr) : Refusal option =
        let schema = schemaOf cols
        let all = nodes e

        firstError
            [ fun () ->
                  Reads.ofExpr e
                  |> List.tryFind (fun c -> Option.isNone (tryCol c cols))
                  |> Option.map (fun c -> Refusal.UnknownColumn(step, c, members cols))
              fun () ->
                  all
                  |> List.tryPick (fun n ->
                      match n with
                      | Now g -> Some(Refusal.UnpinnedClock(step, g))
                      | _ -> None)
              fun () ->
                  all
                  |> List.tryPick (fun n ->
                      match n with
                      | Param name
                      | InParam(_, name) -> Some(Refusal.UnboundParam(step, name))
                      | _ -> None)
              fun () ->
                  all
                  |> List.tryPick (excluded schema)
                  |> Option.map (fun c -> Refusal.ConstructExcluded(step, c))
              fun () ->
                  all
                  |> List.tryPick (fun n ->
                      asked schema n
                      |> Option.bind (fun c ->
                          d.Refuses c |> Option.map (fun why -> Refusal.DialectRefuses(step, c, why)))) ]

    /// A `Filter` over the relation built so far.
    let private filter (d: Dialect) (step: int) (b: Built) (p: ColExpr) : Result<Built, Refusal> =
        let schema = schemaOf b.Columns

        let check =
            firstError
                [ fun () -> expressionRefusal d step b.Columns p
                  fun () ->
                      if Plan.isTotal schema (Filter p) then
                          None
                      else
                          Some(Refusal.MayRefuse(step, "the planner's totality verdict does not admit the predicate"))
                  fun () ->
                      match DataFrame.typeOf schema p with
                      | Some BoolType -> None
                      | Some ty ->
                          Some(Refusal.Untyped(step, "the predicate is typed " + ColumnType.tag ty + ", not bool"))
                      | None -> Some(Refusal.Untyped(step, "the predicate's type is not decided by the schema")) ]

        match check with
        | Some r -> Error r
        | None ->
            let bind, parameters = binder d step b

            lowerExpr step b.Columns bind (Some BoolType) p
            |> Result.map (fun scalar ->
                { b with
                    Relation = Relation.Where(b.Relation, scalar)
                    Params = parameters () })

    let rec private renumberScalar (f: int -> int) (s: Scalar) : Scalar =
        let go = renumberScalar f

        match s with
        | Scalar.Column _ -> s
        | Scalar.Parameter i -> Scalar.Parameter(f i)
        | Scalar.Function(fn, args) -> Scalar.Function(fn, List.map go args)
        | Scalar.Operator(op, a, b) -> Scalar.Operator(op, go a, go b)
        | Scalar.Not x -> Scalar.Not(go x)
        | Scalar.IsNull x -> Scalar.IsNull(go x)
        | Scalar.Coalesce xs -> Scalar.Coalesce(List.map go xs)
        | Scalar.Case(cases, otherwise) -> Scalar.Case(cases |> List.map (fun (w, t) -> go w, go t), go otherwise)
        | Scalar.InList(x, items) -> Scalar.InList(go x, List.map go items)
        | Scalar.Cast(ty, x) -> Scalar.Cast(ty, go x)

    /// A relation with every parameter ordinal it references mapped through `f`.
    let rec private renumber (f: int -> int) (r: Relation) : Relation =
        match r with
        | Relation.Scan _ -> r
        | Relation.Where(input, p) -> Relation.Where(renumber f input, renumberScalar f p)
        | Relation.Select(input, columns) -> Relation.Select(renumber f input, columns)
        | Relation.Derive(input, columns) ->
            Relation.Derive(renumber f input, columns |> List.map (fun (s, o) -> renumberScalar f s, o))
        | Relation.Join(left, right, kind, on, columns) ->
            Relation.Join(renumber f left, renumber f right, kind, on, columns)
        | Relation.Aggregate(input, keys, aggregates, nonEmpty) ->
            Relation.Aggregate(renumber f input, keys, aggregates, nonEmpty)
        | Relation.Ordered(input, keys, limit, offset) -> Relation.Ordered(renumber f input, keys, f limit, f offset)

    /// The field a `Derive` of `e` named `name` gives its column over the schema, where the schema
    /// decides its type: the compute package's own static walk, so the field is the one the reference
    /// evaluator gives the column, unit included (DECISIONS.md D15.1).
    let private derivedField (schema: Schema) (name: string) (e: ColExpr) : Field option =
        SchemaWalk.ofTransform SchemaWalk.noSources (SchemaWalk.ofSchema schema) (Derive(name, e))
        |> SchemaWalk.columns
        |> List.tryFind (fun c -> c.Name = name)
        |> Option.bind ColumnKnowledge.toField

    /// A `Derive` over the relation built so far: one select carrying every column through and
    /// computing the derived one, in place where the name is already carried, as the reference
    /// evaluator places it (DECISIONS.md D15).
    let private derive (d: Dialect) (step: int) (b: Built) (name: string, e: ColExpr) : Result<Built, Refusal> =
        let schema = schemaOf b.Columns
        let field = derivedField schema name e

        let check =
            firstError
                [ fun () -> expressionRefusal d step b.Columns e
                  fun () ->
                      match field with
                      | Some _ -> None
                      | None -> Some(Refusal.VerbNotLowered(step, "derive"))
                  fun () ->
                      if Plan.isTotal schema (Derive(name, e)) then
                          None
                      else
                          Some(
                              Refusal.MayRefuse(
                                  step,
                                  "the planner's totality verdict does not admit the derived column"
                              )
                          ) ]

        match check, field with
        | Some r, _ -> Error r
        | None, None -> Error(Refusal.VerbNotLowered(step, "derive"))
        | None, Some field ->
            // The computed columns are rendered before the select they read from, so the derive's
            // parameters take the first ordinals and the input's move up past them.
            let bind, parameters = binder d step { b with Params = [] }

            lowerExpr step b.Columns bind (Some field.Type) e
            |> Result.map (fun scalar ->
                let added = parameters ()

                let derived =
                    { Output = { Alias = ""; Field = field }
                      Origin = None }

                let computed =
                    if b.Columns |> List.exists (fun c -> c.Output.Name = name) then
                        b.Columns
                        |> List.map (fun c ->
                            if c.Output.Name = name then
                                scalar, derived
                            else
                                Scalar.Column c.Output.Alias, c)
                    else
                        (b.Columns |> List.map (fun c -> Scalar.Column c.Output.Alias, c))
                        @ [ scalar, derived ]

                let columns =
                    computed
                    |> List.mapi (fun i (s, c) ->
                        s,
                        { c with
                            Output =
                                { c.Output with
                                    Alias = mint (b.Next + i) } })

                { b with
                    Relation =
                        Relation.Derive(
                            renumber (fun i -> i + List.length added) b.Relation,
                            columns |> List.map (fun (s, c) -> s, c.Output)
                        )
                    Columns = columns |> List.map snd
                    Params = added @ b.Params
                    Next = b.Next + List.length columns })

    /// A `Project` over the relation built so far.
    let private project (step: int) (b: Built) (pairs: (string * string) list) : Result<Built, Refusal> =
        if List.isEmpty pairs then
            Error(Refusal.EmptyRow step)
        else
            match pairs |> List.tryFind (fun (src, _) -> Option.isNone (tryCol src b.Columns)) with
            | Some(src, _) -> Error(Refusal.UnknownColumn(step, src, members b.Columns))
            | None ->
                match repeated (pairs |> List.map snd) with
                | Some name -> Error(Refusal.RepeatedName(step, name))
                | None ->
                    let picked =
                        pairs
                        |> List.mapi (fun i (src, out) ->
                            let c = (tryCol src b.Columns).Value

                            c.Output.Alias,
                            { Output =
                                { Alias = mint (b.Next + i)
                                  Field = ColumnSpec.renameField out c.Output.Field }
                              Origin = c.Origin })

                    Ok
                        { b with
                            Relation = Relation.Select(b.Relation, picked |> List.map (fun (a, c) -> a, c.Output))
                            Columns = picked |> List.map snd
                            Next = b.Next + List.length pairs }

    /// A `Join` onto a modelled table along a declared relation.
    let private join
        (d: Dialect)
        (model: ModelNode)
        (step: int)
        (b: Built)
        (source: DataSource, on: (string * string) list, kind: JoinKind)
        : Result<Built, Refusal> =
        let operand =
            match source with
            | Embedded _ -> Error(Refusal.OperandNotModelled(step, "an embedded table"))
            | Ref name ->
                match Model.tryTable name model with
                | Some t -> Ok t
                | None -> Error(Refusal.OperandNotModelled(step, "'" + name + "'"))

        let kindAdmitted =
            match kind with
            | Right
            | Outer -> Error(Refusal.JoinKindNotLowered(step, kind))
            | Inner
            | Left
            | Semi
            | Anti ->
                match d.Refuses(Construct.Join kind) with
                | Some why -> Error(Refusal.DialectRefuses(step, Construct.Join kind, why))
                | None -> Ok()

        kindAdmitted
        |> Result.bind (fun () -> operand)
        |> Result.bind (fun (node, spec) ->
            let rightCols = Model.columns node

            let rightCol (name: string) =
                rightCols |> List.tryFind (fun (_, c) -> c.Name = name)

            match on |> List.tryFind (fun (l, _) -> Option.isNone (tryCol l b.Columns)) with
            | Some(l, _) -> Error(Refusal.UnknownColumn(step, l, members b.Columns))
            | None ->
                match on |> List.tryFind (fun (_, r) -> Option.isNone (rightCol r)) with
                | Some(_, r) -> Error(Refusal.UnknownColumn(step, r, rightCols |> List.map (fun (_, c) -> c.Name)))
                | None ->
                    let undeclared = Error(Refusal.UndeclaredJoin(step, spec.Name, on))

                    let origins =
                        on
                        |> List.map (fun (l, r) -> (tryCol l b.Columns).Value.Origin, fst (rightCol r).Value |> _.Id)

                    if List.isEmpty on || origins |> List.exists (fun (o, _) -> Option.isNone o) then
                        undeclared
                    else
                        let pairs = origins |> List.map (fun (o, r) -> o.Value, r)

                        match pairs |> List.map (fun ((t, _), _) -> t) |> List.distinct with
                        | [ leftTable ] ->
                            let wanted = pairs |> List.map (fun ((_, l), r) -> l, r) |> Set.ofList

                            let declared =
                                Model.joins model
                                |> List.exists (fun (_, j) ->
                                    (j.Left = leftTable && j.Right = node.Id && Set.ofList j.On = wanted)
                                    || (j.Left = node.Id
                                        && j.Right = leftTable
                                        && Set.ofList (j.On |> List.map (fun (a, c) -> c, a)) = wanted))

                            if not declared then
                                undeclared
                            else
                                match
                                    on
                                    |> List.tryFind (fun (l, r) ->
                                        (tryCol l b.Columns).Value.Output.Type <> (snd (rightCol r).Value).Type)
                                with
                                | Some(l, r) ->
                                    Error(
                                        Refusal.Untyped(step, sprintf "join keys '%s' and '%s' are of two types" l r)
                                    )
                                | None ->
                                    let rightRel, rightOut, next = scan node spec b.Next
                                    let leftNames = members b.Columns |> Set.ofList

                                    let rightNamed =
                                        rightOut
                                        |> List.map (fun c ->
                                            if Set.contains c.Output.Name leftNames then
                                                { c with
                                                    Output =
                                                        { c.Output with
                                                            Field =
                                                                ColumnSpec.renameField
                                                                    (c.Output.Name + "_right")
                                                                    c.Output.Field } }
                                            else
                                                c)

                                    let columns =
                                        match kind with
                                        | Semi
                                        | Anti -> b.Columns |> List.map (fun c -> Pick.Left c.Output.Alias, c)
                                        | Inner
                                        | Left
                                        | Right
                                        | Outer ->
                                            (b.Columns |> List.map (fun c -> Pick.Left c.Output.Alias, c))
                                            @ (rightNamed |> List.map (fun c -> Pick.Right c.Output.Alias, c))

                                    match repeated (columns |> List.map (fun (_, c) -> c.Output.Name)) with
                                    | Some name -> Error(Refusal.RepeatedName(step, name))
                                    | None ->
                                        let keyAliases =
                                            on
                                            |> List.map (fun (l, r) ->
                                                (tryCol l b.Columns).Value.Output.Alias,
                                                (rightOut |> List.find (fun c -> c.Output.Name = r)).Output.Alias)

                                        Ok
                                            { b with
                                                Relation =
                                                    Relation.Join(
                                                        b.Relation,
                                                        rightRel,
                                                        kind,
                                                        keyAliases,
                                                        columns |> List.map (fun (p, c) -> p, c.Output)
                                                    )
                                                Columns = columns |> List.map snd
                                                Next = next }
                        | _ -> undeclared)

    /// Whether the reference evaluator types an aggregate over a column of this type without refusing.
    let private aggregateAdmits (fn: AggFn) (ty: ColumnType) : bool =
        match fn with
        | Sum
        | Mean
        | Median
        | StdDev ->
            match ty with
            | IntType
            | FloatType
            | DecimalType -> true
            | BoolType
            | StringType
            | DateType
            | TimestampType _ -> false
        | Min
        | Max
        | Count
        | First
        | Last
        | CountDistinct -> true

    /// The aggregates outside the provider's declared subset (DECISIONS.md D4.4).
    let private aggregateExcluded (fn: AggFn) : bool =
        match fn with
        | Median
        | StdDev
        | First
        | Last -> true
        | Sum
        | Mean
        | Min
        | Max
        | Count
        | CountDistinct -> false

    /// A `GroupBy` over the relation built so far.
    let private groupBy
        (d: Dialect)
        (step: int)
        (b: Built)
        (keys: string list, aggs: Agg list)
        : Result<Built, Refusal> =
        let check =
            firstError
                [ fun () ->
                      if List.isEmpty keys && List.isEmpty aggs then
                          Some(Refusal.EmptyRow step)
                      else
                          None
                  fun () ->
                      (keys @ (aggs |> List.map _.Of))
                      |> List.tryFind (fun c -> Option.isNone (tryCol c b.Columns))
                      |> Option.map (fun c -> Refusal.UnknownColumn(step, c, members b.Columns))
                  fun () ->
                      aggs
                      |> List.tryFind (fun a -> aggregateExcluded a.Fn)
                      |> Option.map (fun a -> Refusal.ConstructExcluded(step, Construct.Aggregate a.Fn))
                  fun () ->
                      aggs
                      |> List.tryPick (fun a ->
                          d.Refuses(Construct.Aggregate a.Fn)
                          |> Option.map (fun why -> Refusal.DialectRefuses(step, Construct.Aggregate a.Fn, why)))
                  fun () ->
                      aggs
                      |> List.tryPick (fun a ->
                          let ty = (tryCol a.Of b.Columns).Value.Output.Type

                          if aggregateAdmits a.Fn ty then
                              None
                          else
                              Some(
                                  Refusal.MayRefuse(
                                      step,
                                      sprintf
                                          "the aggregate %A over a %s column, which the reference refuses"
                                          a.Fn
                                          (ColumnType.tag ty)
                                  )
                              ))
                  fun () ->
                      repeated (keys @ (aggs |> List.map _.Name))
                      |> Option.map (fun name -> Refusal.RepeatedName(step, name)) ]

        match check with
        | Some r -> Error r
        | None ->
            let keyCols = keys |> List.map (fun k -> (tryCol k b.Columns).Value)

            let aggCols =
                aggs
                |> List.mapi (fun i a ->
                    let source = (tryCol a.Of b.Columns).Value

                    a.Fn,
                    source.Output.Alias,
                    let field = Field.create a.Name (DataFrame.aggregateType a.Fn source.Output.Type)

                    { Output =
                        { Alias = mint (b.Next + i)
                          Field =
                            match DataFrame.aggregateUnit a.Fn source.Output.Field.Unit with
                            | Some u -> Field.withUnit u field
                            | None -> field }
                      Origin = None })

            Ok
                { b with
                    Relation =
                        Relation.Aggregate(
                            b.Relation,
                            keyCols |> List.map (fun c -> c.Output.Alias, c.Output),
                            aggCols |> List.map (fun (fn, a, c) -> fn, a, c.Output),
                            List.isEmpty keys
                        )
                    Columns = keyCols @ (aggCols |> List.map (fun (_, _, c) -> c))
                    Next = b.Next + List.length aggs }

    /// A scalar slot's value, or why it has none.
    let private slot (env: Map<string, Cell>) (step: int) (what: string) (s: Slot<'T>) : Result<'T, Refusal> =
        match s with
        | Slot.Lit v -> Ok v
        | Slot.Param name ->
            if Map.containsKey name env then
                Error(Refusal.MayRefuse(step, sprintf "the %s slot is bound to a cell of the wrong shape" what))
            else
                Error(Refusal.UnboundParam(step, name))

    /// The `Sort` at `step` with the `Limit` at `step + 1`: one ordered page.
    let private page
        (d: Dialect)
        (env: Map<string, Cell>)
        (step: int)
        (b: Built)
        (keys: (Slot<string> * SortDir) list, n: Slot<int>, offset: Slot<int>)
        : Result<Built, Refusal> =
        traverse
            (fun (k, dir) ->
                slot env step "sort key" k
                |> Result.bind (fun name ->
                    match tryCol name b.Columns with
                    | Some c -> Ok(c.Output.Alias, dir)
                    | None -> Error(Refusal.UnknownColumn(step, name, members b.Columns))))
            keys
        |> Result.bind (fun resolved ->
            slot env step "limit" n
            |> Result.bind (fun n ->
                slot env step "offset" offset
                |> Result.bind (fun offset ->
                    match d.Refuses(Construct.Parameter IntType) with
                    | Some why -> Error(Refusal.DialectRefuses(step, Construct.Parameter IntType, why))
                    | None ->
                        // A negative count or offset keeps nothing or skips nothing in the reference
                        // (it clamps both at zero); the bound values are clamped the same way, since
                        // an engine may read a negative limit as no limit at all.
                        let at = List.length b.Params

                        Ok
                            { b with
                                Relation = Relation.Ordered(b.Relation, resolved, at, at + 1)
                                Params = b.Params @ [ Int(max 0 n), IntType; Int(max 0 offset), IntType ] })))

    /// Lower the planned steps onto the source's scan, one at a time, until one refuses. Answers the
    /// statement built, how many planned steps it carries, and the refusal that stopped it.
    let walk
        (d: Dialect)
        (model: ModelNode)
        (env: Map<string, Cell>)
        (start: Built)
        (planned: Transform list)
        : Built * int * Refusal option =
        let rec go (step: int) (b: Built) (steps: Transform list) : Built * int * Refusal option =
            let lowered = step - 1

            let next (r: Result<Built, Refusal>) (rest: Transform list) =
                match r with
                | Ok b' -> go (step + 1) b' rest
                | Error refusal -> b, lowered, Some refusal

            match steps with
            | [] -> b, lowered, None
            | t :: rest ->
                match t with
                | Filter p -> next (filter d step b p) rest
                | Project pairs -> next (project step b pairs) rest
                | Join(source, on, kind) -> next (join d model step b (source, on, kind)) rest
                | GroupBy(keys, aggs) -> next (groupBy d step b (keys, aggs)) rest
                | Sort keys ->
                    match rest with
                    | Limit(n, offset) :: after ->
                        match page d env step b (keys, n, offset) with
                        | Error refusal -> b, lowered, Some refusal
                        | Ok b' ->
                            match after with
                            | [] -> b', lowered + 2, None
                            | _ -> b', lowered + 2, Some(Refusal.AfterOrder(step + 2))
                    | _ -> b, lowered, Some(Refusal.UnpairedSort step)
                | Limit _ -> b, lowered, Some(Refusal.UnorderedLimit step)
                | Pivot _ -> b, lowered, Some(Refusal.PivotNeverLowers step)
                | Derive(name, e) -> next (derive d step b (name, e)) rest
                | Window _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "window"))
                | Unpivot _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "unpivot"))
                | Distinct -> b, lowered, Some(Refusal.VerbNotLowered(step, "distinct"))
                | Union _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "union"))
                | Intersect _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "intersect"))
                | Except _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "except"))
                // The planner drops every definition when it expands the pipeline; one that remains
                // belongs to a pipeline whose definitions the reference refuses.
                | Define _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "define"))
                | Fit _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "fit"))
                | Predict _ -> b, lowered, Some(Refusal.VerbNotLowered(step, "predict"))

        go 1 start planned
