namespace Fuaran.DB.Session

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

/// The recording rule (DECISIONS.md D9): a value is recorded only when every classification the
/// request reads is one the model marks recordable. What a request reads is the policy's own account
/// of it: every column the admitted pipeline reads (`PolicyRule.ColumnsGranted`) and every column a
/// row policy applied to it names. One decision per request covers its parameter values, its result
/// rows and the literals of its recorded pipelines, because a literal in a pipeline is lowered to a
/// parameter: recording one and withholding the other would publish what the rule withholds.
[<RequireQualifiedAccess>]
module Recording =

    let rec private columnsOf (e: ColExpr) : string list =
        match e with
        | Col c -> [ c ]
        | Lit _
        | Param _
        | Now _ -> []
        | Binary(_, a, b)
        | Quotient(a, b, _) -> columnsOf a @ columnsOf b
        | Not x
        | IsNull x
        | Cast(_, x)
        | Rounded(x, _)
        | InParam(x, _) -> columnsOf x
        | Coalesce xs
        | ApplyFn(_, xs) -> List.collect columnsOf xs
        | Case(cases, els) ->
            (cases |> List.collect (fun (w, t) -> columnsOf w @ columnsOf t))
            @ columnsOf els
        | InList(x, items) -> columnsOf x @ List.collect columnsOf items

    /// The classifications a request reads, sorted and distinct: from the columns the policy granted
    /// and the columns every applied row policy names.
    let reads (model: ModelNode) (applied: PolicyRule list) : string list =
        applied
        |> List.collect (fun rule ->
            match rule with
            | PolicyRule.ColumnsGranted columns -> columns |> List.map (fun (c, _) -> c.Classification)
            | PolicyRule.RowPolicyApplied(table, _, _, predicate) ->
                match Model.tryTable table model with
                | None -> []
                | Some(node, _) ->
                    let named = columnsOf predicate

                    Model.columns node
                    |> List.filter (fun (_, c) -> List.contains c.Name named)
                    |> List.map (fun (_, c) -> c.Classification)
            | PolicyRule.GroupFloorApplied(_, _, columns) -> columns |> List.map _.Classification
            | PolicyRule.CostAdmitted _ -> [])
        |> List.distinct
        |> List.sort

    /// The name a redacted literal takes: a parameter slot no pipeline writes, numbered in order.
    let withheldSlot (n: int) : string = sprintf "withheld:%d" n

    let rec private redactExpr (count: int ref) (e: ColExpr) : ColExpr =
        let go = redactExpr count

        match e with
        | Lit _ ->
            count.Value <- count.Value + 1
            Param(withheldSlot count.Value)
        | Col _
        | Param _
        | Now _ -> e
        | Binary(op, a, b) -> Binary(op, go a, go b)
        | Not x -> Not(go x)
        | IsNull x -> IsNull(go x)
        | Cast(ty, x) -> Cast(ty, go x)
        | Rounded(x, r) -> Rounded(go x, r)
        | InParam(x, n) -> InParam(go x, n)
        | Coalesce xs -> Coalesce(List.map go xs)
        | ApplyFn(fn, xs) -> ApplyFn(fn, List.map go xs)
        | Case(cases, els) -> Case(cases |> List.map (fun (w, t) -> go w, go t), go els)
        | InList(x, items) -> InList(go x, List.map go items)
        | Quotient(a, b, r) -> Quotient(go a, go b, r)

    let private redactSource (count: int ref) (s: DataSource) : DataSource =
        match s with
        | Ref _ -> s
        | Embedded _ ->
            count.Value <- count.Value + 1
            Ref(withheldSlot count.Value)

    let private redactStep (count: int ref) (t: Transform) : Transform =
        match t with
        | Filter p -> Filter(redactExpr count p)
        | Derive(n, e) -> Derive(n, redactExpr count e)
        | Join(s, on, kind) -> Join(redactSource count s, on, kind)
        | Union s -> Union(redactSource count s)
        | Intersect s -> Intersect(redactSource count s)
        | Except s -> Except(redactSource count s)
        | Project _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _ -> t

    /// A pipeline with every literal value and every embedded operand replaced by a numbered withheld
    /// slot, and how many were replaced. What remains is the pipeline's shape: its columns, verbs,
    /// operators and named parameters, none of which is a value read from or bound for a source.
    let redact (pipeline: Transform list) : Transform list * int =
        let count = ref 0
        let redacted = pipeline |> List.map (redactStep count)
        redacted, count.Value

    /// Rules with every applied row policy's predicate redacted the same way, continuing the count.
    let redactRules (start: int) (rules: PolicyRule list) : PolicyRule list * int =
        let count = ref start

        let redacted =
            rules
            |> List.map (fun rule ->
                match rule with
                | PolicyRule.RowPolicyApplied(table, policy, at, predicate) ->
                    PolicyRule.RowPolicyApplied(table, policy, at, redactExpr count predicate)
                | PolicyRule.ColumnsGranted _
                | PolicyRule.GroupFloorApplied _
                | PolicyRule.CostAdmitted _ -> rule)

        redacted, count.Value

    /// A fingerprint reading with the change value of every table whose change column is not
    /// recordable set to `Null`, and the tables so withheld. A counter's position and a file's
    /// metadata are not column values, and are recorded whole.
    let fingerprint
        (model: ModelNode)
        (recordable: string list)
        (m: FingerprintMethod)
        (reading: FingerprintReading)
        : FingerprintReading * string list =
        match m, reading with
        | FingerprintMethod.RowCountMaxChange pairs, FingerprintReading.Tables rows ->
            // the classification of each table's change column, by the table's physical identifier
            let classOf =
                pairs
                |> List.choose (fun (tableId, columnId) ->
                    match ModelNode.tryFind tableId model, Model.tryColumn columnId model with
                    | Some t, Some(_, c) ->
                        match t.Kind with
                        | NodeKind.Table spec -> Some(spec.Physical, c.Classification)
                        | NodeKind.Model _
                        | NodeKind.Source _
                        | NodeKind.Column _
                        | NodeKind.Join _
                        | NodeKind.Measure _
                        | NodeKind.RowPolicy _
                        | NodeKind.Limit _ -> None
                    | _ -> None)
                |> Map.ofList

            let withheld (table: string) =
                match Map.tryFind table classOf with
                | Some c -> not (List.contains c recordable)
                | None -> true

            let kept =
                rows
                |> List.map (fun r -> if withheld r.Table then { r with MaxChange = Null } else r)

            FingerprintReading.Tables kept, rows |> List.filter (fun r -> withheld r.Table) |> List.map _.Table
        | FingerprintMethod.ChangeCounter, _
        | FingerprintMethod.FileStat _, _
        | FingerprintMethod.RowCountMaxChange _, _ -> reading, []
