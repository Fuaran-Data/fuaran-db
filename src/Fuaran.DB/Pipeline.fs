namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

/// Why a pipeline is refused against a model — each case names what the model does hold.
[<RequireQualifiedAccess>]
type PipelineRefusal =
    /// A step reads a column the rows reaching it do not carry. `members` is the walked column
    /// set at that step: the model's columns, as renamed, derived and aggregated by the steps
    /// before it.
    | UnknownColumn of step: int * column: string * members: string list
    /// A source names a table the model does not hold.
    | UnknownTable of step: int * table: string * tables: string list
    /// A join between tables the model does not relate on those keys. `relations` names the
    /// relations the model declares for the joined table, as `left.col = right.col` pairs.
    | UnrelatedJoin of step: int * table: string * on: (string * string) list * relations: string list
    /// The walk lost the closed column set before this step read from it: a membership verdict
    /// needs an absence to be a fact, and an open set can only confirm. `reason` is the walk's.
    | OpenWalk of step: int * reason: string
    /// An embedded table, rather than a modelled source, is brought into the pipeline. The check
    /// is about what a source holds, and an embedded table is not held by one.
    | EmbeddedOperand of step: int
    /// A sort key is bound at run time; a request never chooses an identifier the model has not
    /// vouched for. Substitute the binding first and check the result.
    | UnboundSortKey of step: int * param: string
    /// The evaluator's unit check (Phase 2, over Compute's `DataFrame.resolveUnits`) refuses the
    /// step over the model's fields: `at` names what met (the step and expression, a join key, a
    /// set-operation column) and `left` / `right` the two units, as their canonical text. A column
    /// the model states no unit for is unconstrained and never refused.
    | UnitMismatch of step: int * at: string * left: string * right: string

/// The check of a transform pipeline against a dataset model
/// (`validator:Fuaran.DB.PipelineAgainstModel`). Default-deny: a table, column or join the model
/// does not name does not exist for a pipeline checked here. Pure; evaluates nothing.
module PipelineCheck =

    /// Step indices in refusals count from 1; step 0 is the pipeline's source.
    let private sourceStep = 0

    /// A refusal as one sentence.
    let describe (r: PipelineRefusal) : string =
        match r with
        | PipelineRefusal.UnknownColumn(s, c, members) ->
            sprintf
                "step %d reads column '%s', which is not a member; the members at that step are: %s"
                s
                c
                (String.concat ", " members)
        | PipelineRefusal.UnknownTable(s, t, tables) ->
            sprintf
                "step %d names table '%s', which the model does not hold; it holds: %s"
                s
                t
                (String.concat ", " tables)
        | PipelineRefusal.UnrelatedJoin(s, t, on, relations) ->
            sprintf
                "step %d joins table '%s' on %s, a relation the model does not declare; it declares: %s"
                s
                t
                (on |> List.map (fun (a, b) -> a + " = " + b) |> String.concat ", ")
                (if List.isEmpty relations then
                     "none for that table"
                 else
                     String.concat "; " relations)
        | PipelineRefusal.OpenWalk(s, reason) ->
            sprintf "step %d reads from a column set the walk cannot close: %s" s reason
        | PipelineRefusal.EmbeddedOperand s -> sprintf "step %d brings in an embedded table, not a modelled source" s
        | PipelineRefusal.UnboundSortKey(s, p) ->
            sprintf "step %d sorts by param '%s', bound at run time; substitute it and check again" s p
        | PipelineRefusal.UnitMismatch(s, at, l, r) ->
            sprintf "step %d: unit mismatch in %s: %s against %s; convert one side explicitly" s at l r

    /// A declared relation as the column-name pairs a pipeline would write, oriented so the
    /// joined (right) table is `right`. `None` when the relation does not involve `right` and
    /// `left`, or names a column the model does not hold.
    let private oriented
        (root: ModelNode)
        (left: string)
        (right: string)
        (j: JoinSpec)
        : (string * string) list option =
        let name (colId: string) =
            Model.tryColumn colId root |> Option.map (fun (_, c) -> c.Name)

        let pairs (flip: bool) =
            j.On
            |> List.map (fun (l, r) ->
                match name l, name r with
                | Some a, Some b -> Some(if flip then (b, a) else (a, b))
                | _ -> None)
            |> fun xs ->
                if xs |> List.forall Option.isSome then
                    Some(xs |> List.choose id)
                else
                    None

        if j.Left = left && j.Right = right then pairs false
        elif j.Left = right && j.Right = left then pairs true
        else None

    let private tableNames (root: ModelNode) =
        Model.tables root |> List.map (fun (_, t) -> t.Name)

    type private Walk =
        {
            Knowledge: SchemaKnowledge
            /// The ids of the tables whose rows the pipeline has brought in so far.
            Tables: string list
        }

    let private membersAt (k: SchemaKnowledge) = SchemaWalk.names k

    /// Every read of one step, checked against the knowledge reaching it.
    let private checkReads (step: int) (reads: string list) (k: SchemaKnowledge) : PipelineRefusal option =
        if List.isEmpty reads then
            None
        else
            match k with
            | SchemaKnowledge.AtLeast(_, reason) -> Some(PipelineRefusal.OpenWalk(step, reason))
            | SchemaKnowledge.Closed _ ->
                reads
                |> List.tryFind (fun c -> not (SchemaWalk.has c k))
                |> Option.map (fun c -> PipelineRefusal.UnknownColumn(step, c, membersAt k))

    /// The fields the knowledge reaching a step states, each with its unit — what the evaluator's
    /// unit check reads. An undecided type stands in as a string: the check reads units, not types.
    let private fieldsAt (k: SchemaKnowledge) : Schema =
        SchemaWalk.columns k |> List.map (ColumnKnowledge.toFieldAs StringType)

    /// Two stated units must be one unit where two columns meet as one (a join key, a set
    /// operation's column pair); an unconstrained side matches anything — the evaluator's rule.
    let private unitsMeet (step: int) (at: string) (l: UnitOfMeasure option) (r: UnitOfMeasure option) =
        match l, r with
        | Some lu, Some ru when Unit.render lu <> Unit.render ru ->
            Some(PipelineRefusal.UnitMismatch(step, at, Unit.render lu, Unit.render ru))
        | _ -> None

    /// The evaluator's unit check over one step, decided over the model's fields before any row:
    /// a filter's predicate and a derive's expression through `DataFrame.resolveUnits`, a join's
    /// key pairs and a set operation's column pairs by the comparison's rule.
    let private checkUnits
        (step: int)
        (t: Transform)
        (k: SchemaKnowledge)
        (operand: (ModelNode * TableSpec) option)
        : PipelineRefusal option =
        let resolve (what: string) (e: ColExpr) =
            match DataFrame.resolveUnits what (fieldsAt k) e with
            | Ok _ -> None
            | Error(UnitMismatch(at, _, l, r)) -> Some(PipelineRefusal.UnitMismatch(step, at, l, r))
            | Error other -> Some(PipelineRefusal.UnitMismatch(step, DataFrame.errorString other, "", ""))

        let operandUnit (c: string) =
            operand
            |> Option.bind (fun (n, _) -> Model.schemaOf n |> List.tryFind (fun f -> f.Name = c))
            |> Option.bind _.Unit

        match t with
        | Filter p -> resolve "filter" p
        | Derive(name, e) -> resolve ("derive '" + name + "'") e
        | Join(_, on, _) ->
            on
            |> List.tryPick (fun (l, r) ->
                unitsMeet step ("join key '" + l + "' = '" + r + "'") (SchemaWalk.unitOf l k) (operandUnit r))
        | Union _
        | Intersect _
        | Except _ ->
            match operand with
            | None -> None
            | Some(n, _) ->
                // Columns pair by position; a width mismatch is the evaluator's shape refusal, not a
                // unit's, so only equal widths are paired here.
                let left = SchemaWalk.columns k
                let right = Model.schemaOf n

                if List.length left <> List.length right then
                    None
                else
                    List.zip left right
                    |> List.tryPick (fun (l, r) -> unitsMeet step ("set column '" + l.Name + "'") l.Unit r.Unit)
        | Project _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _ -> None

    /// The modelled table an operand names, or why it is refused.
    let private operandTable
        (root: ModelNode)
        (step: int)
        (source: DataSource)
        : Result<ModelNode * TableSpec, PipelineRefusal> =
        match source with
        | Embedded _ -> Error(PipelineRefusal.EmbeddedOperand step)
        | Ref name ->
            match Model.tryTable name root with
            | Some t -> Ok t
            | None -> Error(PipelineRefusal.UnknownTable(step, name, tableNames root))

    let private checkStep
        (root: ModelNode)
        (sources: string -> Schema option)
        (walk: Walk)
        (step: int, t: Transform)
        : Result<Walk, PipelineRefusal> =
        let reads =
            Reads.ofStep t
            |> Result.mapError (fun p -> PipelineRefusal.UnboundSortKey(step, p))

        let operand =
            match Reads.operandOf t with
            | None -> Ok None
            | Some source -> operandTable root step source |> Result.map Some

        match reads, operand with
        | Error r, _
        | _, Error r -> Error r
        | Ok reads, Ok operand ->
            match checkReads step reads walk.Knowledge with
            | Some r -> Error r
            | None ->
                let joinCheck =
                    match t, operand with
                    | Join(_, on, _), Some(rightNode, rightSpec) ->
                        let rightMembers = Model.schemaOf rightNode |> List.map _.Name

                        match on |> List.tryFind (fun (_, r) -> not (List.contains r rightMembers)) with
                        | Some(_, r) -> Error(PipelineRefusal.UnknownColumn(step, r, rightMembers))
                        | None ->
                            let wanted = on |> Set.ofList

                            let candidates =
                                Model.joins root
                                |> List.choose (fun (_, j) ->
                                    walk.Tables |> List.tryPick (fun left -> oriented root left rightNode.Id j))

                            if candidates |> List.exists (fun pairs -> Set.ofList pairs = wanted) then
                                Ok()
                            else
                                let described =
                                    candidates
                                    |> List.map (fun pairs ->
                                        pairs |> List.map (fun (a, b) -> a + " = " + b) |> String.concat ", ")

                                Error(PipelineRefusal.UnrelatedJoin(step, rightSpec.Name, on, described))
                    | _ -> Ok()

                joinCheck
                |> Result.bind (fun () ->
                    match checkUnits step t walk.Knowledge operand with
                    | Some r -> Error r
                    | None -> Ok())
                |> Result.map (fun () ->
                    { Knowledge = SchemaWalk.ofTransform sources walk.Knowledge t
                      Tables =
                        match operand with
                        | Some(n, _) when not (List.contains n.Id walk.Tables) -> walk.Tables @ [ n.Id ]
                        | Some _
                        | None -> walk.Tables })

    /// Check `pipeline` over `source` against `model`. `Ok` carries the walked output knowledge;
    /// `Error` the first refusal, in step order. Nothing is lowered or evaluated.
    let check
        (model: ModelNode)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<SchemaKnowledge, PipelineRefusal> =
        let sources = SchemaWalk.ofMap (Model.schemas model)

        operandTable model sourceStep source
        |> Result.bind (fun (n, _) ->
            let start =
                { Knowledge = SchemaWalk.ofSource sources source
                  Tables = [ n.Id ] }

            pipeline
            |> List.mapi (fun i t -> i + 1, t)
            |> List.fold (fun acc st -> acc |> Result.bind (fun w -> checkStep model sources w st)) (Ok start)
            |> Result.map _.Knowledge)
