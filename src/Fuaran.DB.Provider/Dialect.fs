namespace Fuaran.DB.Provider

open Fuaran.Core
open Fuaran.Compute

/// The dialect seam (`seam:Fuaran.DB.Provider.Dialect`): what one database engine contributes to a
/// statement, as a record of functions. The provider decides WHAT a statement says — which steps
/// lower, which identifiers and parameters it carries, and its shape as nested selects — and the
/// dialect decides how that is SPELLED. It never sees a request: every string it is handed is an
/// identifier from the model, an alias the provider minted, a parameter marker, or text it rendered
/// itself.
///
/// No dialect ships with the provider (DECISIONS.md D4.6). A dialect is a value of this record, in
/// its own package.
type Dialect =
    {
        /// The dialect's name, for reports.
        Name: string
        /// Quote one identifier. Total over any text: the identifier is a model's physical
        /// identifier or a minted alias, and a dialect escapes whatever its quoting needs.
        QuoteIdentifier: string -> string
        /// The marker for the parameter at this 0-based ordinal.
        ParameterMarker: int -> string
        /// A value of this type entering the statement — a scanned column, or a parameter marker — as
        /// the engine must read it to compare, group and order it as the reference does: identity
        /// where it already does. A dialect whose engine compares strings by a declared collation
        /// re-declares it here, so the comparison the statement makes is the reference's and not the
        /// source's (DECISIONS.md D11).
        ReadValue: ColumnType -> string -> string
        /// Render a scalar function or an operator over its already-rendered arguments. Called only
        /// for a construct `Refuses` admitted. A logical operator's arguments arrive as conditions,
        /// every other argument as a value.
        RenderFunction: Construct -> string list -> string
        /// Render an aggregate over its already-rendered argument, given the type of the column it
        /// aggregates: an engine whose aggregate types differ from the reference's widens or casts
        /// here (DECISIONS.md D8). Called only for an aggregate `Refuses` admitted.
        RenderAggregate: AggFn -> ColumnType -> string -> string
        /// A boolean VALUE read where a condition is required: identity for an engine with a boolean
        /// type, a comparison for one without (DECISIONS.md D8).
        ValueAsCondition: string -> string
        /// A CONDITION read where a boolean value is required, keeping its three values: identity for
        /// an engine with a boolean type, a `CASE` for one without (DECISIONS.md D8).
        ConditionAsValue: string -> string
        /// The clause that orders a select by these rendered keys — NULLS LAST in either direction,
        /// as the reference evaluator orders them — skips the rows `offset` names and takes the rows
        /// `limit` names (both rendered parameter markers).
        RenderPaging: (string * SortDir) list -> string -> string -> string
        /// The dialect's name for a column type, carried on each parameter.
        MapType: ColumnType -> string
        /// `Some reason` for a construct the dialect does not lower. The provider asks before it
        /// emits each one; a refusal places the fold boundary at that step, and the step runs in the
        /// reference evaluator instead.
        Refuses: Construct -> string option
        /// The statement that reads the source's own change counter or log position as one row of
        /// one column, or `None` where the engine keeps none. A source fingerprinted by
        /// `ChangeCounter` is probed through it (`Probe`, DECISIONS.md D6).
        ChangeCounter: string option
    }

/// Rendering a statement's abstract form through a dialect: nested selects, one per relation.
[<RequireQualifiedAccess>]
module Render =

    let private joinKeyword (kind: JoinKind) : string =
        match kind with
        | Inner -> "INNER JOIN"
        | Left -> "LEFT JOIN"
        | Right -> "RIGHT JOIN"
        | Outer -> "FULL JOIN"
        // The filtering joins render as EXISTS; this arm is not reached by the renderer below.
        | Semi
        | Anti -> "INNER JOIN"

    /// The text of one relation. `next` mints the derived-table aliases (`t1`, `t2`, …); `marker`
    /// spells the parameter at an ordinal, read as its type.
    let rec private relation (d: Dialect) (marker: int -> string) (next: unit -> string) (r: Relation) : string =
        let q = d.QuoteIdentifier

        // A scalar is rendered in one of two positions: as a CONDITION (a `WHERE`, a `CASE` branch's
        // test, a logical operator's operand) or as a VALUE (everywhere else). A form that yields a
        // condition read as a value, or the reverse, passes through the dialect's conversion, which is
        // identity for an engine with a boolean type.
        let isCondition (s: Scalar) : bool =
            match s with
            | Scalar.Operator((Eq | Ne | Lt | Le | Gt | Ge | And | Or | Contains | StartsWith | EndsWith), _, _)
            | Scalar.Not _
            | Scalar.IsNull _
            | Scalar.InList _ -> true
            | Scalar.Operator((Add | Sub | Mul | Div | Mod), _, _)
            | Scalar.Column _
            | Scalar.Parameter _
            | Scalar.Function _
            | Scalar.Coalesce _
            | Scalar.Case _ -> false

        let rec value (s: Scalar) : string =
            if isCondition s then
                d.ConditionAsValue(conditionForm s)
            else
                valueForm s

        and condition (s: Scalar) : string =
            if isCondition s then
                conditionForm s
            else
                d.ValueAsCondition(valueForm s)

        /// A condition-yielding form, rendered as a condition.
        and conditionForm (s: Scalar) : string =
            match s with
            | Scalar.Operator((And | Or) as op, a, b) ->
                d.RenderFunction (Construct.Operator op) [ condition a; condition b ]
            | Scalar.Not x -> "(NOT " + condition x + ")"
            | Scalar.IsNull x -> "(" + value x + " IS NULL)"
            | Scalar.InList(x, items) ->
                "(" + value x + " IN (" + (items |> List.map value |> String.concat ", ") + "))"
            | Scalar.Operator _
            | Scalar.Column _
            | Scalar.Parameter _
            | Scalar.Function _
            | Scalar.Coalesce _
            | Scalar.Case _ -> valueForm s

        /// A value-yielding form (and a comparison, whose operands are values), rendered as written.
        and valueForm (s: Scalar) : string =
            match s with
            | Scalar.Column a -> q a
            | Scalar.Parameter i -> marker i
            | Scalar.Function(f, args) -> d.RenderFunction (Construct.Function f) (args |> List.map value)
            | Scalar.Operator(op, a, b) -> d.RenderFunction (Construct.Operator op) [ value a; value b ]
            | Scalar.Coalesce xs -> "COALESCE(" + (xs |> List.map value |> String.concat ", ") + ")"
            | Scalar.Case(cases, otherwise) ->
                "(CASE"
                + (cases
                   |> List.map (fun (w, t) -> " WHEN " + condition w + " THEN " + value t)
                   |> String.concat "")
                + " ELSE "
                + value otherwise
                + " END)"
            | Scalar.Not _
            | Scalar.IsNull _
            | Scalar.InList _ -> conditionForm s

        let columnsOf (input: Relation) =
            Relation.outputs input |> List.map (fun o -> q o.Alias) |> String.concat ", "

        let from (input: Relation) =
            let alias = next ()
            "(" + relation d marker next input + ") AS " + q alias, alias

        match r with
        | Relation.Scan(table, columns) ->
            "SELECT "
            + (columns
               |> List.map (fun (p, o) -> d.ReadValue o.Type (q p) + " AS " + q o.Alias)
               |> String.concat ", ")
            + " FROM "
            + q table
        | Relation.Where(input, predicate) ->
            let src, _ = from input
            "SELECT " + columnsOf input + " FROM " + src + " WHERE " + condition predicate
        | Relation.Select(input, columns) ->
            let src, _ = from input

            "SELECT "
            + (columns
               |> List.map (fun (a, o) -> q a + " AS " + q o.Alias)
               |> String.concat ", ")
            + " FROM "
            + src
        | Relation.Join(left, right, kind, on, columns) ->
            let lsrc, la = from left
            let rsrc, ra = from right

            let condition =
                on
                |> List.map (fun (l, r) -> q la + "." + q l + " = " + q ra + "." + q r)
                |> String.concat " AND "

            let picked =
                columns
                |> List.map (fun (p, o) ->
                    match p with
                    | Pick.Left a -> q la + "." + q a + " AS " + q o.Alias
                    | Pick.Right a -> q ra + "." + q a + " AS " + q o.Alias)
                |> String.concat ", "

            match kind with
            | Semi
            | Anti ->
                "SELECT "
                + picked
                + " FROM "
                + lsrc
                + " WHERE "
                + (if kind = Anti then "NOT " else "")
                + "EXISTS (SELECT 1 FROM "
                + rsrc
                + " WHERE "
                + condition
                + ")"
            | Inner
            | Left
            | Right
            | Outer ->
                "SELECT "
                + picked
                + " FROM "
                + lsrc
                + " "
                + joinKeyword kind
                + " "
                + rsrc
                + " ON "
                + condition
        | Relation.Aggregate(input, keys, aggregates, nonEmpty) ->
            let src, _ = from input

            let typeOf (a: string) =
                (Relation.outputs input |> List.find (fun o -> o.Alias = a)).Type

            let selected =
                (keys |> List.map (fun (a, o) -> q a + " AS " + q o.Alias))
                @ (aggregates
                   |> List.map (fun (fn, a, o) -> d.RenderAggregate fn (typeOf a) (q a) + " AS " + q o.Alias))

            "SELECT "
            + String.concat ", " selected
            + " FROM "
            + src
            + (if List.isEmpty keys then
                   ""
               else
                   " GROUP BY " + (keys |> List.map (fun (a, _) -> q a) |> String.concat ", "))
            + (if nonEmpty then " HAVING COUNT(*) > 0" else "")
        | Relation.Ordered(input, keys, limit, offset) ->
            let src, _ = from input

            "SELECT "
            + columnsOf input
            + " FROM "
            + src
            + " "
            + d.RenderPaging (keys |> List.map (fun (a, dir) -> q a, dir)) (marker limit) (marker offset)

    /// The statement text of a form, through a dialect, its parameters typed by `parameterTypes` in
    /// ordinal order: each marker is read as its parameter's type (`ReadValue`). Pure: the same form,
    /// types and dialect render the same text.
    let text (d: Dialect) (parameterTypes: ColumnType list) (form: Relation) : string =
        let n = ref 0

        let next () =
            n.Value <- n.Value + 1
            "t" + string n.Value

        let types = Array.ofList parameterTypes

        let marker (i: int) =
            let m = d.ParameterMarker i

            if i >= 0 && i < types.Length then
                d.ReadValue types[i] m
            else
                m

        relation d marker next form
