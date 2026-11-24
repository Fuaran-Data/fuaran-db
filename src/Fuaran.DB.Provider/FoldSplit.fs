namespace Fuaran.DB.Provider

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

/// What a pipeline is evaluated under: scalar parameters, list parameters, and a pinned clock. The
/// provider reads no clock of its own; a `Now` with none pinned does not lower (DECISIONS.md D4.5).
type Bindings =
    {
        /// Scalar parameters: `ColExpr.Param`, and the `Slot.Param`s of `Sort` and `Limit`.
        Params: Map<string, Cell>
        /// List parameters: `ColExpr.InParam`.
        Lists: Map<string, Cell list>
        /// The pinned clock, read at most once per grain.
        Clock: ClockWitness option
    }

/// A grouped operand as the split planned it (DECISIONS.md D16).
type OperandPlan =
    {
        /// The name its join gives it.
        Name: string
        /// The model name of its table.
        Table: string
        /// The compute planner's report over its pipeline as admitted and bound: a row policy on its
        /// table is its first step, before its grouping.
        Plan: PlanReport
    }

/// What the fold split decided, and why (`explain`).
type SplitReport =
    {
        /// The compute planner's report over the pipeline as bound: what it read, the pipeline it
        /// planned, and every rewrite it applied or declined. The split reads `Plan.Planned`.
        Plan: PlanReport
        /// How many of the planned steps the statement carries, from the first.
        Lowered: int
        /// The rule that placed the boundary at step `Lowered + 1`, or `None` when every step lowered.
        Boundary: Refusal option
        /// Each grouped operand the pipeline's joins name, as planned.
        Operands: OperandPlan list
    }

/// A lowered pipeline: the statement for the prefix, the planned steps after it, and the report.
type Lowered =
    {
        /// The one statement the prefix lowers to.
        Statement: Statement
        /// The planned steps the reference evaluator runs over the statement's result, in order.
        Suffix: Transform list
        /// What was decided, and why.
        Split: SplitReport
        /// The grouped operands, bound: what a join to one in the suffix reads, by the reference.
        Operands: Operand list
    }

/// Bindings with nothing bound.
[<RequireQualifiedAccess>]
module Bindings =

    /// No parameters, no lists, no clock.
    let none: Bindings =
        { Params = Map.empty
          Lists = Map.empty
          Clock = None }

/// The fold split and the provider (`domain:Fuaran.DB.Provider.FoldSplit`): a PURE function from a
/// dataset model, a dialect, a pipeline over a modelled source and its bindings to one parameterised
/// statement and the steps that remain, or a named refusal. It opens no connection, reads no clock
/// and evaluates nothing.
///
/// The order is fixed (DECISIONS.md D4.2): the bindings are substituted, the compute planner plans
/// the bound pipeline over the source's model schema, and the split walks the PLANNED pipeline,
/// lowering the longest prefix every step of which lowers. The suffix is the rest of the planned
/// pipeline, which the reference evaluator runs over the statement's result.
[<RequireQualifiedAccess>]
module FoldSplit =

    /// The pipeline with its bindings substituted: scalar parameters and slots, list parameters,
    /// and the clock, pinned once per grain. What stays unbound stays a parameter, and refuses
    /// wherever it is evaluated, exactly as the reference refuses it.
    let bind (bindings: Bindings) (pipeline: Transform list) : Transform list =
        let bound =
            pipeline
            |> Transform.substitute bindings.Params
            |> Transform.substituteListParams bindings.Lists

        match bindings.Clock with
        | Some clock -> Transform.substituteNow clock bound
        | None -> bound

    let private sourceTable (model: ModelNode) (source: DataSource) : Result<ModelNode * TableSpec, Refusal> =
        match source with
        | Embedded _ -> Error Refusal.EmbeddedSource
        | Ref name ->
            match Model.tryTable name model with
            | None -> Error(Refusal.UnknownSource(name, Model.tables model |> List.map (fun (_, t) -> t.Name)))
            | Some(node, spec) ->
                if List.isEmpty (Model.columns node) then
                    Error(Refusal.EmptyRow 0)
                else
                    Ok(node, spec)

    /// Lower `pipeline` over `source` against `model` through `dialect`, with no policy: the
    /// placement laws' entry point, internal so that nothing outside this assembly lowers a pipeline
    /// the policy has not admitted (DECISIONS.md D5). `Error` only when no statement can be written
    /// at all (the source is embedded, unmodelled, or has no columns); otherwise the statement for the
    /// longest lowerable prefix of the planned pipeline, the steps left to run locally, and the rule
    /// that stopped the prefix.
    let internal lowerUnchecked
        (model: ModelNode)
        (dialect: Dialect)
        (operands: Operand list)
        (source: DataSource)
        (pipeline: Transform list)
        (bindings: Bindings)
        (timeoutSeconds: int option)
        : Result<Lowered, Refusal> =
        sourceTable model source
        |> Result.map (fun (node, spec) ->
            let bound = bind bindings pipeline
            let report = Plan.explain (Model.schemaOf node) bound

            // Each operand is bound and planned over its own table's schema, as a pipeline is.
            let operands =
                operands
                |> List.map (fun o ->
                    let o =
                        { o with
                            Pipeline = bind bindings o.Pipeline }

                    let schema =
                        Model.tryTable o.Table model
                        |> Option.map (fst >> Model.schemaOf)
                        |> Option.defaultValue []

                    o, Plan.explain schema o.Pipeline)

            let relation, columns, next = Lowering.scan node spec 1

            let built, lowered, boundary =
                Lowering.walk
                    dialect
                    model
                    bindings.Params
                    (operands |> List.map (fun (o, plan) -> o, plan.Planned))
                    { Relation = relation
                      Columns = columns
                      Params = []
                      Next = next }
                    report.Planned

            let form = built.Relation

            { Statement =
                { Text = Render.text dialect (built.Params |> List.map snd) form
                  Parameters =
                    built.Params
                    |> List.mapi (fun i (value, ty) ->
                        { Ordinal = i
                          Marker = dialect.ParameterMarker i
                          Value = value
                          Type = ty
                          DialectType = dialect.MapType ty })
                  Columns = Relation.outputs form
                  Form = form
                  TimeoutSeconds = timeoutSeconds }
              Suffix = List.skip lowered report.Planned
              Split =
                { Plan = report
                  Lowered = lowered
                  Boundary = boundary
                  Operands =
                    operands
                    |> List.map (fun (o, plan) ->
                        { Name = o.Name
                          Table = o.Table
                          Plan = plan }) }
              Operands = operands |> List.map fst })

    /// Lower an ADMITTED pipeline through `dialect`: the pipeline the policy rewrote, over its source,
    /// against its model, with the grouped operands it admitted, the statement carrying the admitted
    /// timeout. A pipeline reaches a statement only through `Policy.admit` or `Policy.admitWith`, so an
    /// ungated read is not expressible (DECISIONS.md D5). `Error` only when no statement can be written
    /// at all (the source is embedded, unmodelled, or has no columns); otherwise the statement for the
    /// longest lowerable prefix of the planned pipeline, the steps left to run locally, and the rule that
    /// stopped the prefix.
    let lower (admitted: Admitted) (dialect: Dialect) (bindings: Bindings) : Result<Lowered, Refusal> =
        lowerUnchecked
            admitted.Model
            dialect
            admitted.Operands
            admitted.Source
            admitted.Pipeline
            bindings
            (Some admitted.TimeoutSeconds)

    /// The split alone: where the boundary falls in the admitted pipeline as planned, and the rule
    /// that put it there.
    let explain (admitted: Admitted) (dialect: Dialect) (bindings: Bindings) : Result<SplitReport, Refusal> =
        lower admitted dialect bindings |> Result.map _.Split

    /// The derives that lowered: each lowered `Derive` step's index (from 1, in the planned pipeline)
    /// and the column it computes in the statement (DECISIONS.md D15).
    let derived (report: SplitReport) : (int * string) list =
        report.Plan.Planned
        |> List.truncate report.Lowered
        |> List.indexed
        |> List.choose (fun (i, t) ->
            match t with
            | Derive(name, _) -> Some(i + 1, name)
            | _ -> None)

    /// The grouped operands joined in the statement: each lowered join's step (from 1, in the planned
    /// pipeline) and the operand's plan (DECISIONS.md D16).
    let joinedOperands (report: SplitReport) : (int * OperandPlan) list =
        report.Plan.Planned
        |> List.truncate report.Lowered
        |> List.indexed
        |> List.choose (fun (i, t) ->
            match t with
            | Join(Ref name, _, _) ->
                report.Operands
                |> List.tryFind (fun o -> o.Name = name)
                |> Option.map (fun o -> i + 1, o)
            | _ -> None)

    /// The local suffix: the planned steps after the boundary, which the reference evaluator runs over
    /// the statement's rows (DECISIONS.md D19). Empty when every planned step lowers. A consumer that
    /// needs the suffix to read only rows at the grain it returns asserts that no step here groups or
    /// joins.
    let suffix (report: SplitReport) : Transform list =
        report.Plan.Planned |> List.skip report.Lowered

    /// A planned step's verb, as `describe` names it.
    let private verb (t: Transform) : string =
        match t with
        | Filter _ -> "filter"
        | Project _ -> "project"
        | Derive(name, _) -> sprintf "derive '%s'" name
        | Join _ -> "join"
        | GroupBy _ -> "groupBy"
        | Sort _ -> "sort"
        | Limit _ -> "limit"
        | Pivot _ -> "pivot"
        | Unpivot _ -> "unpivot"
        | Window _ -> "window"
        | Distinct -> "distinct"
        | Union _ -> "union"
        | Intersect _ -> "intersect"
        | Except _ -> "except"
        | Define _ -> "define"
        | Fit _ -> "fit"
        | Predict _ -> "predict"
        | KMeans _ -> "kmeans"
        | Bootstrap _ -> "bootstrap"

    /// The split as sentences: what the planner rewrote, which steps lower, each derive and each grouped
    /// operand that lowered, where the boundary fell and why, and the local suffix it leaves.
    let describe (report: SplitReport) : string list =
        let planned = List.length report.Plan.Planned

        let rewrites =
            (report.Plan.Applied
             |> List.map (fun a -> sprintf "the planner applied %A at step %d: %s" a.Class (a.At + 1) a.Detail))
            @ (report.Plan.Declined
               |> List.map (fun d -> sprintf "the planner declined %A at step %d: %s" d.Class (d.At + 1) d.Reason))

        let lowered =
            if report.Lowered = 0 then
                "no step lowers: the statement reads the source table, and every planned step runs locally"
            else
                sprintf "steps 1 to %d of %d lower to one statement" report.Lowered planned

        let boundary =
            match report.Boundary with
            | None -> [ "every planned step lowers; nothing runs locally" ]
            | Some r ->
                [ sprintf "the boundary falls at step %d: %s" (Refusal.step r) (Refusal.describe r)
                  sprintf
                      "the local suffix is steps %d to %d, run over the statement's rows: %s"
                      (report.Lowered + 1)
                      planned
                      (suffix report |> List.map verb |> String.concat ", ") ]

        let derives =
            derived report
            |> List.map (fun (step, name) ->
                sprintf
                    "step %d derives the column '%s' in the statement, as a computed column of its select"
                    step
                    name)

        let operands =
            joinedOperands report
            |> List.map (fun (step, o) ->
                sprintf
                    "step %d joins the grouped operand '%s' as a derived table: its %d planned step(s) over table '%s' lower inside it, %s first"
                    step
                    o.Name
                    (List.length o.Plan.Planned)
                    o.Table
                    (match o.Plan.Planned with
                     | Filter _ :: _ -> "a filter"
                     | _ -> "no filter"))

        rewrites @ [ lowered ] @ derives @ operands @ boundary

    /// Run the suffix over the statement's result, in the reference evaluator, under the same
    /// bindings — the second half of the placement. The suffix is already bound, so the clock it
    /// was pinned with is the one the statement was lowered under. A join in the suffix to a grouped
    /// operand reads the operand as the reference does: its pipeline over its table, read through
    /// `resolve`.
    let finish
        (lowered: Lowered)
        (resolve: string -> Result<Table, EvalError>)
        (bindings: Bindings)
        (result: Table)
        : Result<Table, EvalError> =
        DataFrame.evalPipelineWithInEnv
            (Operand.resolver bindings.Params lowered.Operands resolve)
            bindings.Params
            lowered.Suffix
            result
