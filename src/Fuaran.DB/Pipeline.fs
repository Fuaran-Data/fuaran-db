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
    /// A grouped operand's own pipeline is refused against the model (DECISIONS.md D16): `refusal`
    /// counts its steps within the operand, from 1.
    | Operand of operand: string * refusal: PipelineRefusal
    /// A grouped operand is named where it may not be: by a step other than a join, by a name the
    /// model's tables or another operand already hold, or over a pipeline that brings in a second
    /// source. `reason` says which.
    | OperandMisused of step: int * operand: string * reason: string

/// A grouped operand (DECISIONS.md D16): a named sub-pipeline over one modelled table, which a `Join`
/// brings in as its right side by naming it, `Join(Ref name, on, kind)`. The transform algebra's
/// sources are a table or a name, so a sub-pipeline travels beside the pipeline that joins it, as
/// the reference evaluator's resolver would answer the name: by evaluating the operand's pipeline.
type Operand =
    {
        /// The name a join gives it. No table of the model may hold it.
        Name: string
        /// The model name of the one table it reads.
        Table: string
        /// Its pipeline over that table. It brings in no other source; it lowers with the join that
        /// names it when it ends in a grouping on the joined relation's keys.
        Pipeline: Transform list
    }

/// The grouped operands a pipeline joins.
[<RequireQualifiedAccess>]
module Operand =

    /// The operand `name` names, if any.
    let tryFind (name: string) (operands: Operand list) : Operand option =
        operands |> List.tryFind (fun o -> o.Name = name)

    /// `resolve` with every operand's name answered by the reference evaluator over the operand's
    /// pipeline as written, its table read through `resolve`, under the scalar bindings `env`. This
    /// is what an operand MEANS: a join to it is the reference's join to that table.
    let resolver
        (env: Map<string, Cell>)
        (operands: Operand list)
        (resolve: string -> Result<Table, EvalError>)
        : string -> Result<Table, EvalError> =
        fun name ->
            match tryFind name operands with
            | Some o ->
                resolve o.Table
                |> Result.bind (DataFrame.evalPipelineWithInEnvAsWritten resolve env o.Pipeline)
            | None -> resolve name

    /// The model column each output name of an operand's pipeline carries unchanged from its table,
    /// by model column name: followed through a projection's renames and a grouping's keys, where a
    /// derived, aggregated or windowed column carries none (DECISIONS.md D4.3's provenance, over a
    /// sub-pipeline).
    let provenance (columns: string list) (pipeline: Transform list) : Map<string, string> =
        let step (m: Map<string, string>) (t: Transform) =
            match t with
            | Filter _
            | Sort _
            | Limit _
            | Distinct
            | Define _ -> m
            | Project pairs ->
                pairs
                |> List.choose (fun (src, out) -> Map.tryFind src m |> Option.map (fun c -> out, c))
                |> Map.ofList
            | Derive(name, _) -> Map.remove name m
            | Window spec -> Map.remove spec.As m
            | GroupBy(keys, _) ->
                keys
                |> List.choose (fun k -> Map.tryFind k m |> Option.map (fun c -> k, c))
                |> Map.ofList
            | Join _
            | Union _
            | Intersect _
            | Except _
            | Pivot _
            | Unpivot _
            | Fit _
            | Predict _ -> Map.empty

        pipeline |> List.fold step (columns |> List.map (fun c -> c, c) |> Map.ofList)

/// The check of a transform pipeline against a dataset model
/// (`validator:Fuaran.DB.PipelineAgainstModel`). Default-deny: a table, column or join the model
/// does not name does not exist for a pipeline checked here. Pure; evaluates nothing.
module PipelineCheck =

    /// Step indices in refusals count from 1; step 0 is the pipeline's source.
    let private sourceStep = 0

    /// A refusal as one sentence.
    let rec describe (r: PipelineRefusal) : string =
        match r with
        | PipelineRefusal.Operand(o, inner) -> sprintf "in the grouped operand '%s', %s" o (describe inner)
        | PipelineRefusal.OperandMisused(s, o, reason) ->
            sprintf "step %d names the grouped operand '%s', which %s" s o reason
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
        // A fit's and a prediction's terms and offset are unit-checked as a derive's expression is,
        // under the evaluator's own labels; a prediction's are evaluated over the rows reaching it.
        | Fit spec ->
            (spec.Predictors |> List.map (fun (n, e) -> "fit term '" + n + "'", e))
            @ (spec.Offset |> Option.map (fun e -> "fit offset", e) |> Option.toList)
            |> List.tryPick (fun (what, e) -> resolve what e)
        | Predict spec ->
            (spec.Fit.Predictors |> List.map (fun (n, e) -> "predict term '" + n + "'", e))
            @ (spec.Fit.Offset |> Option.map (fun e -> "predict offset", e) |> Option.toList)
            |> List.tryPick (fun (what, e) -> resolve what e)
        | Project _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _
        | Define _ -> None

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

    /// What the walk knows of a source as a schema: `None` when it cannot name every column's type.
    let private schemaOfKnowledge (k: SchemaKnowledge) : Schema option =
        let fields = SchemaWalk.columns k |> List.map ColumnKnowledge.toField

        if List.forall Option.isSome fields then
            Some(List.choose id fields)
        else
            None

    /// A join to a grouped operand (DECISIONS.md D16): the operand's pipeline checked against the model
    /// as any pipeline over its table is, then the join's keys along a relation the model declares
    /// between the left side's tables and the operand's table, each right key followed through the
    /// operand's renames and grouping to the model column it carries.
    let private checkOperandStep
        (root: ModelNode)
        (sources: string -> Schema option)
        (operands: Operand list)
        (checkOver: DataSource -> Transform list -> Result<SchemaKnowledge, PipelineRefusal>)
        (walk: Walk)
        (step: int, t: Transform)
        (o: Operand)
        : Result<Walk, PipelineRefusal> =
        let misused (why: string) =
            Error(PipelineRefusal.OperandMisused(step, o.Name, why))

        match t with
        | Join(_, on, _) ->
            if Option.isSome (Model.tryTable o.Name root) then
                misused "shares its name with a table of the model"
            elif operands |> List.filter (fun x -> x.Name = o.Name) |> List.length > 1 then
                misused "more than one operand is named"
            else
                match checkReads step (on |> List.map fst) walk.Knowledge with
                | Some r -> Error r
                | None ->
                    match Model.tryTable o.Table root with
                    | None ->
                        Error(
                            PipelineRefusal.Operand(
                                o.Name,
                                PipelineRefusal.UnknownTable(sourceStep, o.Table, tableNames root)
                            )
                        )
                    | Some(node, _) ->
                        match
                            o.Pipeline
                            |> List.indexed
                            |> List.tryFind (fun (_, s) -> Option.isSome (Reads.operandOf s))
                        with
                        | Some(i, _) ->
                            misused (
                                sprintf
                                    "brings in a second source at its step %d, where an operand reads one table"
                                    (i + 1)
                            )
                        | None ->
                            checkOver (Ref o.Table) o.Pipeline
                            |> Result.mapError (fun e -> PipelineRefusal.Operand(o.Name, e))
                            |> Result.bind (fun out ->
                                let members = SchemaWalk.names out

                                match on |> List.tryFind (fun (_, r) -> not (List.contains r members)) with
                                | Some(_, r) -> Error(PipelineRefusal.UnknownColumn(step, r, members))
                                | None ->
                                    let carried =
                                        Operand.provenance (Model.schemaOf node |> List.map _.Name) o.Pipeline

                                    let candidates =
                                        Model.joins root
                                        |> List.choose (fun (_, j) ->
                                            walk.Tables |> List.tryPick (fun left -> oriented root left node.Id j))

                                    let wanted =
                                        on
                                        |> List.map (fun (l, r) -> Map.tryFind r carried |> Option.map (fun c -> l, c))

                                    let declared =
                                        wanted |> List.forall Option.isSome
                                        && candidates
                                           |> List.exists (fun pairs ->
                                               Set.ofList pairs = Set.ofList (List.choose id wanted))

                                    if not declared then
                                        Error(
                                            PipelineRefusal.UnrelatedJoin(
                                                step,
                                                o.Name,
                                                on,
                                                candidates
                                                |> List.map (fun pairs ->
                                                    pairs
                                                    |> List.map (fun (a, b) -> a + " = " + b)
                                                    |> String.concat ", ")
                                            )
                                        )
                                    else
                                        match
                                            on
                                            |> List.tryPick (fun (l, r) ->
                                                unitsMeet
                                                    step
                                                    ("join key '" + l + "' = '" + r + "'")
                                                    (SchemaWalk.unitOf l walk.Knowledge)
                                                    (SchemaWalk.unitOf r out))
                                        with
                                        | Some r -> Error r
                                        | None ->
                                            let joined (name: string) =
                                                if name = o.Name then
                                                    schemaOfKnowledge out
                                                else
                                                    sources name

                                            Ok
                                                { walk with
                                                    Knowledge = SchemaWalk.ofTransform joined walk.Knowledge t })
        | _ -> misused "only a join may bring in"

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
                    // A prediction's model source must carry every column the prediction reads from it.
                    | Predict spec, Some(modelNode, _) ->
                        let modelMembers = Model.schemaOf modelNode |> List.map _.Name

                        match
                            Reads.ofPredictModel spec
                            |> List.tryFind (fun c -> not (List.contains c modelMembers))
                        with
                        | Some c -> Error(PipelineRefusal.UnknownColumn(step, c, modelMembers))
                        | None -> Ok()
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

    /// Check `pipeline` over `source` against `model`, its joins bringing in `operands` by name
    /// (DECISIONS.md D16). `Ok` carries the walked output knowledge; `Error` the first refusal, in
    /// step order. Nothing is lowered or evaluated.
    let rec checkWith
        (model: ModelNode)
        (operands: Operand list)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<SchemaKnowledge, PipelineRefusal> =
        let sources = SchemaWalk.ofMap (Model.schemas model)

        let step (w: Walk) (i: int, t: Transform) =
            let named =
                match Reads.operandOf t with
                | Some(Ref name) -> Operand.tryFind name operands
                | Some(Embedded _)
                | None -> None

            match named with
            | Some o -> checkOperandStep model sources operands (checkWith model []) w (i, t) o
            | None -> checkStep model sources w (i, t)

        operandTable model sourceStep source
        |> Result.bind (fun (n, _) ->
            let start =
                { Knowledge = SchemaWalk.ofSource sources source
                  Tables = [ n.Id ] }

            pipeline
            |> List.mapi (fun i t -> i + 1, t)
            |> List.fold (fun acc st -> acc |> Result.bind (fun w -> step w st)) (Ok start)
            |> Result.map _.Knowledge)

    /// Check `pipeline` over `source` against `model`, with no grouped operand. `Ok` carries the walked
    /// output knowledge; `Error` the first refusal, in step order. Nothing is lowered or evaluated.
    let check
        (model: ModelNode)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<SchemaKnowledge, PipelineRefusal> =
        checkWith model [] source pipeline
