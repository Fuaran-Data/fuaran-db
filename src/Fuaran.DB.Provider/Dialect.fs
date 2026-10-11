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
        /// Render a function, an operator or an aggregate over its already-rendered arguments. Called
        /// only for a construct `Refuses` admitted.
        RenderFunction: Construct -> string list -> string
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

    /// The text of one relation. `next` mints the derived-table aliases (`t1`, `t2`, …).
    let rec private relation (d: Dialect) (next: unit -> string) (r: Relation) : string =
        let q = d.QuoteIdentifier

        let rec scalar (s: Scalar) : string =
            match s with
            | Scalar.Column a -> q a
            | Scalar.Parameter i -> d.ParameterMarker i
            | Scalar.Function(f, args) -> d.RenderFunction (Construct.Function f) (args |> List.map scalar)
            | Scalar.Operator(op, a, b) -> d.RenderFunction (Construct.Operator op) [ scalar a; scalar b ]
            | Scalar.Not x -> "(NOT " + scalar x + ")"
            | Scalar.IsNull x -> "(" + scalar x + " IS NULL)"
            | Scalar.Coalesce xs -> "COALESCE(" + (xs |> List.map scalar |> String.concat ", ") + ")"
            | Scalar.Case(cases, otherwise) ->
                "(CASE"
                + (cases
                   |> List.map (fun (w, t) -> " WHEN " + scalar w + " THEN " + scalar t)
                   |> String.concat "")
                + " ELSE "
                + scalar otherwise
                + " END)"
            | Scalar.InList(x, items) ->
                "("
                + scalar x
                + " IN ("
                + (items |> List.map scalar |> String.concat ", ")
                + "))"

        let columnsOf (input: Relation) =
            Relation.outputs input |> List.map (fun o -> q o.Alias) |> String.concat ", "

        let from (input: Relation) =
            let alias = next ()
            "(" + relation d next input + ") AS " + q alias, alias

        match r with
        | Relation.Scan(table, columns) ->
            "SELECT "
            + (columns
               |> List.map (fun (p, o) -> q p + " AS " + q o.Alias)
               |> String.concat ", ")
            + " FROM "
            + q table
        | Relation.Where(input, predicate) ->
            let src, _ = from input
            "SELECT " + columnsOf input + " FROM " + src + " WHERE " + scalar predicate
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

            let selected =
                (keys |> List.map (fun (a, o) -> q a + " AS " + q o.Alias))
                @ (aggregates
                   |> List.map (fun (fn, a, o) ->
                       d.RenderFunction (Construct.Aggregate fn) [ q a ] + " AS " + q o.Alias))

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
            + d.RenderPaging
                (keys |> List.map (fun (a, dir) -> q a, dir))
                (d.ParameterMarker limit)
                (d.ParameterMarker offset)

    /// The statement text of a form, through a dialect. Pure: the same form and dialect render the
    /// same text.
    let text (d: Dialect) (form: Relation) : string =
        let n = ref 0

        let next () =
            n.Value <- n.Value + 1
            "t" + string n.Value

        relation d next form
