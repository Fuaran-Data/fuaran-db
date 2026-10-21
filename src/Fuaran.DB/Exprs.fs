namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

/// What a step of the transform algebra READS, by column name. Written here as closed matches over
/// `ColExpr` and `Transform` with no catch-all, so a verb or expression form the algebra gains stops
/// this build until it is decided what the new form reads — never a silent "reads nothing".
module Reads =

    /// Every column name an expression reads, left to right, with duplicates.
    let rec ofExpr (e: ColExpr) : string list =
        match e with
        | Col c -> [ c ]
        | Lit _
        | Param _
        | Now _ -> []
        | Binary(_, a, b) -> ofExpr a @ ofExpr b
        | Not x
        | IsNull x
        | Cast(_, x)
        | Convert(x, _)
        | Rounded(x, _) -> ofExpr x
        | InParam(x, _) -> ofExpr x
        | Coalesce xs
        | ApplyFn(_, xs) -> xs |> List.collect ofExpr
        | Case(cases, els) -> (cases |> List.collect (fun (w, t) -> ofExpr w @ ofExpr t)) @ ofExpr els
        | InList(x, items) -> ofExpr x @ (items |> List.collect ofExpr)
        | Quotient(a, b, _) -> ofExpr a @ ofExpr b

    /// What one step reads from the rows flowing INTO it. A sort key bound at run time is `Error`
    /// with the param's name: its column is chosen by the request, and a request never chooses an
    /// identifier the model has not vouched for.
    let ofStep (step: Transform) : Result<string list, string> =
        match step with
        | Filter p -> Ok(ofExpr p)
        | Derive(_, e) -> Ok(ofExpr e)
        | Project pairs -> Ok(pairs |> List.map fst)
        | GroupBy(keys, aggs) -> Ok(keys @ (aggs |> List.map _.Of))
        | Join(_, on, _) -> Ok(on |> List.map fst)
        | Window spec -> Ok(spec.PartitionBy @ (spec.OrderBy |> List.map fst) @ [ spec.Of ])
        | Pivot spec -> Ok(spec.Index @ [ spec.On; spec.Values ])
        | Unpivot(idVars, valueVars) -> Ok(idVars @ valueVars)
        | Sort keys ->
            keys
            |> List.fold
                (fun acc (slot, _) ->
                    acc
                    |> Result.bind (fun cols ->
                        match slot with
                        | Slot.Lit c -> Ok(cols @ [ c ])
                        | Slot.Param p -> Error p))
                (Ok [])
        | Distinct
        | Limit _
        | Union _
        | Intersect _
        | Except _ -> Ok []

    /// The other source a step brings in, if any.
    let operandOf (step: Transform) : DataSource option =
        match step with
        | Join(source, _, _)
        | Union source
        | Intersect source
        | Except source -> Some source
        | Filter _
        | Derive _
        | Project _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _ -> None
