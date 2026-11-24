/// Policy before lowering (Phase 1902): default-deny, column grants, row policy, cost limits and the
/// group floor on worked pipelines; the laws over a generated sample, with sample adequacy over every
/// refusal and every rewrite; the falsifier that turns the row-policy law red; and the gated provider.
module Fuaran.DB.Tests.PolicyTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider

module Rng = Fuaran.Core.ConfRng

let private seed = 19020
let private iterations = 600

// ---- the worked model: staff, departments and projects ----

let private col (table: string) (name: string) (ty: ColumnType) (cls: string) : ModelNode =
    Sample.leaf
        (sprintf "c:%s.%s" table name)
        (NodeKind.Column(ColumnSpec.create name (name.ToUpperInvariant()) ty cls))

let private policy (id: string) (name: string) (predicate: ColExpr) : ModelNode =
    Sample.leaf id (NodeKind.RowPolicy { Name = name; Predicate = predicate })

let private inRegion = Binary(Eq, Col "region", Param "region")

let private staff =
    Sample.table
        "t:staff"
        "staff"
        [ col "staff" "staff_id" IntType "public"
          col "staff" "dept" StringType "internal"
          col "staff" "region" StringType "public"
          col "staff" "salary" DecimalType "personal"
          col "staff" "email" StringType "personal"
          policy "p:staff.region" "own region" inRegion
          Sample.leaf "l:staff" (NodeKind.Limit(LimitSpec.rows "staff rows" 500)) ]

let private depts =
    Sample.table
        "t:depts"
        "depts"
        [ col "depts" "dept" StringType "internal"
          col "depts" "budget" DecimalType "internal"
          col "depts" "region" StringType "public"
          policy "p:depts.region" "department region" inRegion ]

let private projects =
    Sample.table
        "t:projects"
        "projects"
        [ col "projects" "dept" StringType "internal"
          col "projects" "project" StringType "public" ]

let private relation (id: string) (right: string) : ModelNode =
    Sample.leaf
        id
        (NodeKind.Join
            { Name = id
              Left = "t:staff"
              Right = sprintf "t:%s" right
              On = [ "c:staff.dept", sprintf "c:%s.dept" right ] })

let private fullLimit: LimitSpec =
    { Name = "cost"
      MaxRows = 1000
      MaxJoins = Some 2
      MaxGroupKeys = Some 2
      MinGroupSize = Some 3
      TimeoutSeconds = Some 30 }

let private modelWith (limits: LimitSpec list) : ModelNode =
    { Id = "m:people"
      Kind =
        NodeKind.Model
            { Name = "people"
              Classifications = [ "public"; "internal"; "personal" ]
              Recordable = [] }
      Children =
        [ { Id = "s:hr"
            Kind = NodeKind.Source { Name = "hr"; Fingerprint = None }
            Children = [ staff; depts; projects ] }
          relation "j:staff-depts" "depts"
          relation "j:staff-projects" "projects" ]
        @ (limits
           |> List.mapi (fun i l -> Sample.leaf (sprintf "l:model.%d" i) (NodeKind.Limit l))) }

let private model = modelWith [ fullLimit ]

/// The model variants a sample draws from: each removes one declaration, so the refusal that
/// declaration's absence causes is reachable.
let private variants =
    [ "full", model
      "no floor", modelWith [ { fullLimit with MinGroupSize = None } ]
      "no timeout", modelWith [ { fullLimit with TimeoutSeconds = None } ]
      "no joins declared", modelWith [ { fullLimit with MaxJoins = None } ] ]

/// A model with no limit anywhere: no row ceiling, and the staff table's own limit removed.
let private unlimited =
    Sample.replace
        "t:staff"
        (fun t ->
            { t with
                Children =
                    t.Children
                    |> List.filter (fun c ->
                        match c.Kind with
                        | NodeKind.Limit _ -> false
                        | NodeKind.Model _
                        | NodeKind.Source _
                        | NodeKind.Table _
                        | NodeKind.Column _
                        | NodeKind.Join _
                        | NodeKind.Measure _
                        | NodeKind.RowPolicy _ -> true) })
        (modelWith [])

// ---- the subjects ----

let private grant (cls: string) (level: GrantLevel) : Grant = { Classification = cls; Level = level }

let private north = Map.ofList [ "region", Str "north" ]

let private analyst: Subject =
    { Principal = "analyst"
      Attributes = north
      Grants =
        [ grant "public" GrantLevel.RowLevel
          grant "internal" GrantLevel.RowLevel
          grant "personal" GrantLevel.Aggregate ] }

let private hr: Subject =
    { Principal = "hr"
      Attributes = north
      Grants =
        [ grant "public" GrantLevel.RowLevel
          grant "internal" GrantLevel.RowLevel
          grant "personal" GrantLevel.RowLevel ] }

let private outsider: Subject =
    { Principal = "outsider"
      Attributes = Map.ofList [ "region", Str "south" ]
      Grants = [ grant "public" GrantLevel.RowLevel ] }

let private nobody: Subject =
    { Principal = "nobody"
      Attributes = north
      Grants = [] }

let private stranger: Subject =
    { analyst with
        Principal = "stranger"
        Attributes = Map.empty }

/// Reads departments only as keys and in aggregate (Phase 40): a department may key a grouping or a
/// join, and never reaches the result.
let private keyholder: Subject =
    { Principal = "keyholder"
      Attributes = north
      Grants =
        [ grant "public" GrantLevel.RowLevel
          grant "internal" GrantLevel.Keyed
          grant "personal" GrantLevel.Aggregate ] }

let private subjects = [ analyst; hr; outsider; nobody; stranger; keyholder ]

// ---- the rows ----

let private tableOf (schema: (string * ColumnType) list) (rows: Cell list list) : Table =
    { Schema = Sample.fields schema
      Columns =
        schema
        |> List.mapi (fun i (n, ty) -> Sample.column n ty (rows |> List.map (List.item i))) }

let private dec (v: int) = Decimal(string v)

let private tables: Map<string, Table> =
    let staffRows =
        [ for i in 1..14 ->
              let dept =
                  match i % 4 with
                  | 0 -> "eng"
                  | 1 -> "eng"
                  | 2 -> "ops"
                  | _ -> if i < 8 then "sales" else "legal"

              [ Int i
                Str dept
                Str(if i % 3 = 0 then "south" else "north")
                dec (1000 * i)
                Str(sprintf "p%d@example.org" i) ] ]

    Map.ofList
        [ "staff",
          tableOf
              [ "staff_id", IntType
                "dept", StringType
                "region", StringType
                "salary", DecimalType
                "email", StringType ]
              staffRows
          "depts",
          tableOf
              [ "dept", StringType; "budget", DecimalType; "region", StringType ]
              [ [ Str "eng"; dec 500; Str "north" ]
                [ Str "ops"; dec 300; Str "south" ]
                [ Str "sales"; dec 200; Str "north" ]
                [ Str "legal"; dec 100; Str "north" ] ]
          "projects",
          tableOf
              [ "dept", StringType; "project", StringType ]
              [ [ Str "eng"; Str "atlas" ]
                [ Str "eng"; Str "beacon" ]
                [ Str "ops"; Str "cairn" ] ] ]

let private resolveIn (ts: Map<string, Table>) (name: string) =
    match Map.tryFind name ts with
    | Some t -> Ok t
    | None -> Error(UnresolvedSource name)

let private eval (ts: Map<string, Table>) (source: DataSource) (pipeline: Transform list) =
    match source with
    | Ref name ->
        resolveIn ts name
        |> Result.bind (DataFrame.evalPipelineWithInEnv (resolveIn ts) Map.empty pipeline)
    | Embedded t -> DataFrame.evalPipelineWithInEnv (resolveIn ts) Map.empty pipeline t

/// Every table with the rows its row policies exclude for `subject` removed.
let private permitted (m: ModelNode) (subject: Subject) (ts: Map<string, Table>) : Map<string, Table> =
    ts
    |> Map.map (fun name t ->
        match Model.tryTable name m with
        | None -> t
        | Some(node, _) ->
            node.Children
            |> List.choose (fun c ->
                match c.Kind with
                | NodeKind.RowPolicy p -> Some(Filter(ColExpr.substitute subject.Attributes p.Predicate))
                | NodeKind.Model _
                | NodeKind.Source _
                | NodeKind.Table _
                | NodeKind.Column _
                | NodeKind.Join _
                | NodeKind.Measure _
                | NodeKind.Limit _ -> None)
            |> fun filters ->
                match DataFrame.evalPipeline filters t with
                | Ok kept -> kept
                | Error e -> failwithf "a policy did not evaluate over the sample rows: %A" e)

// ---- the pipelines ----

let private page (n: int) = Limit(Slot.Lit n, Slot.Lit 0)
let private str (s: string) = Lit(Str s)
let private agg (name: string) (fn: AggFn) (over: string) : Agg = { Name = name; Fn = fn; Of = over }
let private onDept = [ "dept", "dept" ]

/// The templates a pipeline is drawn from, over `staff`, each with what it is there to reach; `n`
/// is the drawn page size.
let private templates (n: int) : (string * Transform list) list =
    [ "the whole table", [ page n ]
      "public columns", [ Project [ "staff_id", "staff_id"; "region", "region" ]; page n ]
      "an internal column read only in a predicate",
      [ Filter(Binary(Eq, Col "dept", str "eng"))
        Project [ "staff_id", "staff_id" ]
        page n ]
      "a predicate that tries to filter the row policy away",
      [ Filter(Binary(Or, Binary(Ne, Col "region", str "north"), Lit(Bool true)))
        Project [ "staff_id", "staff_id"; "region", "region" ]
        page n ]
      "a personal column renamed", [ Project [ "salary", "pay" ]; page n ]
      "a personal column in a predicate",
      [ Filter(Binary(Gt, Col "salary", Lit(dec 5000)))
        Project [ "staff_id", "staff_id" ]
        page n ]
      "a grouping over a personal column", [ GroupBy([ "dept" ], [ agg "avg" Mean "salary" ]); page n ]
      "two aggregates over a personal column, sorted",
      [ GroupBy([ "dept" ], [ agg "n" Count "salary"; agg "total" Sum "salary" ])
        Sort [ Slot.Lit "dept", Asc ]
        page n ]
      "a keyless aggregate over a personal column", [ GroupBy([], [ agg "total" Sum "salary" ]); page n ]
      "a group's first salary", [ GroupBy([ "dept" ], [ agg "first" First "salary" ]); page n ]
      "a public aggregate", [ GroupBy([ "region" ], [ agg "n" Count "staff_id" ]); page n ]
      "a grouping keyed on an internal column, re-aggregated",
      [ GroupBy([ "dept" ], [ agg "n" Count "staff_id" ])
        GroupBy([], [ agg "depts" Count "dept"; agg "heads" Sum "n" ])
        page n ]
      "a grouping keyed on an internal column, its grain returned",
      [ GroupBy([ "dept" ], [ agg "n" Count "staff_id" ])
        Project [ "n", "n" ]
        page n ]
      "a join keyed on an internal column, its grain returned",
      [ Join(Ref "projects", onDept, Inner)
        Project [ "staff_id", "staff_id" ]
        page n ]
      "an inner join to a policed table",
      [ Join(Ref "depts", onDept, Inner)
        Project [ "staff_id", "staff_id"; "budget", "budget"; "region_right", "dept_region" ]
        page n ]
      "an inner join to a policed table, grouped",
      [ Join(Ref "depts", onDept, Inner)
        GroupBy([ "dept" ], [ agg "n" Count "staff_id" ])
        page n ]
      "an inner join to a policed table, filtered on its column",
      [ Join(Ref "depts", onDept, Inner)
        Filter(Binary(Gt, Col "budget", Lit(dec 150)))
        Project [ "staff_id", "staff_id" ]
        page n ]
      "an inner join to a policed table, its salaries grouped",
      [ Join(Ref "depts", onDept, Inner)
        GroupBy([ "budget" ], [ agg "top" Max "salary" ])
        page n ]
      "a left join to a policed table", [ Join(Ref "depts", onDept, Left); Project [ "staff_id", "staff_id" ]; page n ]
      "a semi join to a policed table", [ Join(Ref "depts", onDept, Semi); Project [ "staff_id", "staff_id" ]; page n ]
      "an inner join whose policy column name is taken",
      [ Derive("region_right", str "x")
        Join(Ref "depts", onDept, Inner)
        Project [ "staff_id", "staff_id" ]
        page n ]
      "a join to an unpoliced table",
      [ Join(Ref "projects", onDept, Inner)
        Project [ "staff_id", "staff_id"; "project", "project" ]
        page n ]
      "three joins",
      [ Join(Ref "projects", onDept, Inner)
        Join(Ref "depts", onDept, Inner)
        Join(Ref "projects", onDept, Semi)
        Project [ "staff_id", "staff_id" ]
        page n ]
      "three grouping keys",
      [ GroupBy([ "dept"; "region"; "staff_id" ], [ agg "n" Count "staff_id" ])
        page n ]
      "no limit", [ Project [ "staff_id", "staff_id" ] ]
      "a bound limit", [ Project [ "staff_id", "staff_id" ]; Limit(Slot.Param "n", Slot.Lit 0) ]
      "a limit before a join",
      [ page n
        Join(Ref "projects", onDept, Inner)
        Project [ "staff_id", "staff_id" ] ]
      "a limit followed by a filter",
      [ Project [ "staff_id", "staff_id"; "region", "region" ]
        page n
        Filter(Binary(Gt, Col "staff_id", Lit(Int 2))) ]
      "a union with a policed table", [ Union(Ref "staff"); Project [ "staff_id", "staff_id" ]; page n ]
      "an unmodelled column", [ Project [ "bogus", "bogus" ]; page n ] ]

type private Case =
    { Label: string
      Variant: string
      Model: ModelNode
      Subject: Subject
      Pipeline: Transform list }

let private sample (seed: int) (count: int) : Case list =
    let mutable r = Rng.ofSeed seed

    let pick (xs: 'a list) =
        let x, r' = Rng.choose xs r
        r <- r'
        x

    [ for _ in 1..count do
          let n = pick [ 5; 50; 400; 2000 ]
          let label, pipeline = pick (templates n)
          let variant, m = pick (variants @ [ "no limits", unlimited ])
          let subject = pick subjects

          { Label = label
            Variant = variant
            Model = m
            Subject = subject
            Pipeline = pipeline } ]

let private admitCase (c: Case) =
    Policy.admit c.Model c.Subject (Ref "staff") c.Pipeline

/// Which rewrites an admission applied: the row policy at the source, after a join, and the floor.
let private rewritesOf (a: Admitted) : string list =
    a.Applied
    |> List.choose (fun r ->
        match r with
        | PolicyRule.RowPolicyApplied(t, _, _, _) when t = "staff" -> Some "row policy at the source"
        | PolicyRule.RowPolicyApplied _ -> Some "row policy after a join"
        | PolicyRule.GroupFloorApplied _ -> Some "group floor"
        | PolicyRule.ColumnsGranted _
        | PolicyRule.CostAdmitted _ -> None)

let private rewriteKinds =
    [ "row policy at the source"; "row policy after a join"; "group floor" ]

/// The laws over the sample. `tamper` rewrites an admitted pipeline before it is evaluated — the
/// identity for the laws as they stand, and a stripping of the row policy for their falsifier.
let private laws (tamper: Admitted -> Transform list) (seed: int) (count: int) : LawResult list =
    let codes = System.Collections.Generic.Dictionary<string, int>()
    let rewrites = System.Collections.Generic.Dictionary<string, int>()

    let bump (d: System.Collections.Generic.Dictionary<string, int>) k =
        d[k] <-
            (match d.TryGetValue k with
             | true, v -> v + 1
             | _ -> 1)

    let mutable total: string option = None
    let mutable pure': string option = None
    let mutable inModel: string option = None
    let mutable survives: string option = None
    let mutable lowers: string option = None
    let mutable floored: string option = None

    let note (slot: string option) (text: string) = if slot.IsNone then Some text else slot

    sample seed count
    |> List.iteri (fun i c ->
        let at =
            sprintf "seed=%d iter=%d (%s, %s, %s)" seed i c.Label c.Variant c.Subject.Principal

        match
            (try
                Ok(admitCase c)
             with ex ->
                 Error ex.Message)
        with
        | Error m -> total <- note total (sprintf "%s: admit threw %s" at m)
        | Ok verdict ->
            if admitCase c <> verdict then
                pure' <- note pure' (sprintf "%s: two admissions differ" at)

            match verdict with
            | Error r -> bump codes (Policy.code r)
            | Ok a ->
                rewritesOf a |> List.distinct |> List.iter (bump rewrites)

                match PipelineCheck.check c.Model (Ref "staff") a.Pipeline with
                | Error r ->
                    inModel <-
                        note inModel (sprintf "%s: the rewrite left the model: %s" at (PipelineCheck.describe r))
                | Ok _ -> ()

                let pipeline = tamper a

                // The floor is never dropped: for a subject who reads salaries only in aggregate,
                // every grouping that aggregates one is followed by a Filter at the floor on a
                // count of it. Decided here from the subject and the column, not from the rules.
                let aggregateOnly =
                    c.Subject.Grants |> List.exists (fun g -> g.Classification = "personal")
                    && not (
                        c.Subject.Grants
                        |> List.exists (fun g -> g.Classification = "personal" && g.Level = GrantLevel.RowLevel)
                    )

                if aggregateOnly then
                    let floor = a.Limits.MinGroupSize

                    for step, next in List.pairwise pipeline @ [ List.last pipeline, Limit(Slot.Lit 0, Slot.Lit 0) ] do
                        match step with
                        | GroupBy(_, aggs) when aggs |> List.exists (fun x -> x.Of = "salary") ->
                            let counted =
                                match next, floor with
                                | Filter(Binary(Ge, Col n, Lit(Int f))), Some floor ->
                                    f = floor
                                    && aggs |> List.exists (fun x -> x.Name = n && x.Fn = Count && x.Of = "salary")
                                | _ -> false

                            if not counted then
                                floored <-
                                    note floored (sprintf "%s: a salary grouping without its floor: %A" at pipeline)
                        | _ -> ()

                // The row policy survives: rows it excludes change nothing the pipeline answers.
                let over = eval tables (Ref "staff") pipeline
                let without = eval (permitted c.Model c.Subject tables) (Ref "staff") pipeline

                if over <> without then
                    survives <-
                        note
                            survives
                            (sprintf "%s: rows the row policy excludes changed the answer: %A vs %A" at over without)

                // The admitted pipeline lowers, carries the timeout, and the placement answers what
                // the admitted pipeline answers.
                match FoldSplit.lower a ReferenceDialect.dialect Bindings.none with
                | Error r -> lowers <- note lowers (sprintf "%s: refused whole: %s" at (Refusal.describe r))
                | Ok l ->
                    if l.Statement.TimeoutSeconds <> Some a.TimeoutSeconds then
                        lowers <- note lowers (sprintf "%s: the statement carries %A" at l.Statement.TimeoutSeconds)

                    let case: PlacementCase =
                        { Model = c.Model
                          Source = Ref "staff"
                          Pipeline = a.Pipeline
                          Operands = []
                          Bindings = Bindings.none
                          Tables = tables }

                    match PlacementLaws.placed ReferenceDialect.execute case l with
                    | PlacementLaws.Answered answer when answer = eval tables (Ref "staff") a.Pipeline -> ()
                    | other -> lowers <- note lowers (sprintf "%s: the placement answered %A" at other))

    let counts (d: System.Collections.Generic.Dictionary<string, int>) keys =
        keys
        |> List.map (fun k ->
            k,
            (match d.TryGetValue k with
             | true, v -> v
             | _ -> 0))

    let law name (slot: string option) =
        LawResult.subject "PolicyLaws" name count slot

    [ law "admission is total: it never throws" total
      law "admission is pure: admitting twice answers the same verdict" pure'
      law "every admitted pipeline, rewrites included, checks against the model" inModel
      law "a row policy survives every admitted pipeline: rows it excludes change nothing" survives
      law "an aggregate over an aggregate-only column is always followed by its floor" floored
      law "every admitted pipeline lowers, carries its timeout, and its placement answers the admitted pipeline" lowers
      SampleAdequacy.reached "PolicyLaws" "refusal" seed (counts codes Policy.codes)
      SampleAdequacy.reached "PolicyLaws" "rewrite" seed (counts rewrites rewriteKinds) ]

let private failed (results: LawResult list) =
    results |> List.filter (fun r -> not r.Passed)

let private census (title: string) (results: LawResult list) =
    printfn "%s: %d law(s), %d held" title (List.length results) (results |> List.filter _.Passed |> List.length)

    for r in results do
        printfn
            "  [%s] %s%s"
            (if r.Passed then "held" else "FAILED")
            r.Law
            (r.Counterexample |> Option.map (fun c -> " -- " + c) |> Option.defaultValue "")

// ---- the worked cases ----

let private admit (subject: Subject) (pipeline: Transform list) =
    Policy.admit model subject (Ref "staff") pipeline

let private refused (subject: Subject) (pipeline: Transform list) : PolicyRefusal =
    match admit subject pipeline with
    | Error r -> r
    | Ok a -> failwithf "admitted: %A" a.Pipeline

let private admitted (subject: Subject) (pipeline: Transform list) : Admitted =
    match admit subject pipeline with
    | Ok a -> a
    | Error r -> failwithf "refused: %s" (Policy.describe r)

let private staffCol (name: string) (cls: string) : ColumnRef =
    { Table = "staff"
      Column = name
      Classification = cls }

let private deptCol (table: string) : ColumnRef =
    { Table = table
      Column = "dept"
      Classification = "internal" }

/// Staff grouped on the department, the grouped operand the keyed cases join (Phase 40).
let private payroll: Operand =
    { Name = "payroll"
      Table = "staff"
      Pipeline = [ GroupBy([ "dept" ], [ agg "paid" Sum "salary"; agg "heads" Count "staff_id" ]) ] }

/// The keyed cases' re-aggregation: one row per region, the department counted.
let private keyedTail: Transform list =
    [ GroupBy([ "region" ], [ agg "total" Sum "budget"; agg "depts" Count "dept" ])
      page 5 ]

/// Departments joined on their key to their staff grouped on it, then re-aggregated to the region.
let private keyedSteps (before: Transform list) : Transform list =
    before
    @ [ Join(Ref "payroll", onDept, Inner)
        GroupBy([ "region" ], [ agg "paid" Sum "paid"; agg "depts" Count "dept" ])
        page 5 ]

let private keyedAdmit (subject: Subject) (steps: Transform list) =
    Policy.admitWith model subject [ payroll ] (Ref "depts") steps

[<Tests>]
let tests =
    testList
        "policy"
        [ testCase "a kmeans step and a bootstrap step are refused by name before any rule reads them (D22)"
          <| fun _ ->
              // `salary` is aggregate-only for the analyst: were the step walked, a rule would refuse
              // the column instead of the step.
              let kmeans = KMeans(KMeansSpec.create [ "salary", Col "salary" ] 2 7L)

              let bootstrap =
                  Bootstrap(
                      BootstrapSpec.create
                          [ { Name = "pay"
                              Fn = Mean
                              Of = "salary" } ]
                          20
                          7L
                  )

              for kind, step in [ "kmeans", kmeans; "bootstrap", bootstrap ] do
                  let r = refused analyst [ step; page 5 ]

                  Expect.equal r (PolicyRefusal.Pipeline(PipelineRefusal.StepNotAdmitted(1, kind))) kind
                  Expect.equal (Policy.code r) "DB-PIPELINE" "under the pipeline's code"
                  Expect.stringContains (Policy.describe r) (sprintf "'%s'" kind) "the sentence names the step"

          testCase "default-deny: a subject with no grants lowers nothing, whatever the pipeline"
          <| fun _ ->
              for label, pipeline in templates 5 @ [ "nothing at all", [] ] do
                  Expect.equal
                      (refused nobody pipeline)
                      (PolicyRefusal.NoGrants "nobody")
                      (sprintf "%s is refused" label)

          testCase "a fit is no aggregate: every column it reads is used row by row, and a coefficient carries them"
          <| fun _ ->
              let fit (output: FitOutput) =
                  Fit
                      { FitSpec.ols "salary" [ "staff_id", Col "staff_id" ] with
                          GroupBy = [ "dept" ]
                          Output = output }

              Expect.equal
                  (refused analyst [ fit FitOutput.Summary; page 5 ])
                  (PolicyRefusal.AggregateOnly(PolicySite.Step 1, staffCol "salary" "personal", "a model fit's input"))
                  "an aggregate-only response is not fitted"

              Expect.equal
                  (refused outsider [ fit FitOutput.Coefficients; page 5 ])
                  (PolicyRefusal.ColumnNotGranted(PolicySite.Step 1, staffCol "salary" "personal", [ "public" ]))
                  "an ungranted response is not fitted"

              admitted hr [ fit FitOutput.Coefficients; page 5 ] |> ignore

          testCase "a prediction reads its model's table: a policed one is refused, an unmodelled column is refused"
          <| fun _ ->
              let predict (source: string) (term: string) =
                  Predict(PredictSpec.ofTraining (FitSpec.ols "budget" [ term, Col term ]) (Ref source))

              match refused hr [ predict "depts" "dept"; page 5 ] with
              | PolicyRefusal.PolicyUnwrappable(1, "depts", _, _) -> ()
              | other -> failtestf "a policed model table is refused as %A" other

              match refused hr [ predict "projects" "dept"; page 5 ] with
              | PolicyRefusal.Pipeline(PipelineRefusal.UnknownColumn(1, "budget", _)) -> ()
              | other -> failtestf "a model column the table lacks is refused as %A" other

          testCase
              "a column read only inside a predicate is checked, and the refusal names column, classification and grants"
          <| fun _ ->
              let r =
                  refused
                      outsider
                      [ Filter(Binary(Eq, Col "dept", str "eng"))
                        Project [ "staff_id", "staff_id" ]
                        page 5 ]

              Expect.equal
                  r
                  (PolicyRefusal.ColumnNotGranted(PolicySite.Step 1, staffCol "dept" "internal", [ "public" ]))
                  "the predicate's read is refused"

              let said = Policy.describe r
              Expect.stringContains said "DB-GRANT-COLUMN" "names the rule"
              Expect.stringContains said "the subject's grants" "names the attribute read"
              Expect.stringContains said "a grant over 'internal'" "names what would satisfy it"

          testCase "a column that reaches the result unread is a read: a pipeline with no projection reads every column"
          <| fun _ ->
              match refused outsider [ page 5 ] with
              | PolicyRefusal.ColumnNotGranted(PolicySite.Result, c, _) ->
                  Expect.equal c.Classification "internal" "the first ungranted result column"
              | other -> failtestf "refused as %A" other

          testCase "a column read under another name is still that column: through a rename and through a join's suffix"
          <| fun _ ->
              Expect.equal
                  (refused outsider [ Project [ "salary", "pay" ]; page 5 ])
                  (PolicyRefusal.ColumnNotGranted(PolicySite.Step 1, staffCol "salary" "personal", [ "public" ]))
                  "the rename reads salary"

              let joined =
                  refused
                      analyst
                      [ Join(Ref "depts", onDept, Inner)
                        Derive("x", Col "staff_id")
                        Filter(Binary(Gt, Col "x", Lit(Int 0)))
                        Project [ "x", "x"; "salary", "s" ]
                        Filter(Binary(Gt, Col "s", Lit(dec 0)))
                        page 5 ]

              Expect.equal
                  joined
                  (PolicyRefusal.AggregateOnly(PolicySite.Step 5, staffCol "salary" "personal", "a filter predicate"))
                  "the renamed salary in a later predicate"

          testCase "an aggregate-only column selected row by row is refused unless the grant says row-level"
          <| fun _ ->
              Expect.equal
                  (refused analyst [ Project [ "staff_id", "staff_id"; "salary", "salary" ]; page 5 ])
                  (PolicyRefusal.AggregateOnly(PolicySite.Result, staffCol "salary" "personal", "a result column"))
                  "the analyst may not see a salary"

              let a =
                  admitted hr [ Project [ "staff_id", "staff_id"; "salary", "salary" ]; page 5 ]

              Expect.contains
                  a.Applied
                  (PolicyRule.ColumnsGranted
                      [ staffCol "staff_id" "public", GrantLevel.RowLevel
                        staffCol "salary" "personal", GrantLevel.RowLevel ])
                  "the row-level grant admits it, and says so"

          testCase "a grouping over an aggregate-only column gains a count and a Filter at the model's floor"
          <| fun _ ->
              let a =
                  admitted analyst [ GroupBy([ "dept" ], [ agg "avg" Mean "salary" ]); page 50 ]

              Expect.equal
                  a.Pipeline
                  [ Filter(Binary(Eq, Col "region", str "north"))
                    GroupBy([ "dept" ], [ agg "avg" Mean "salary"; agg "floor_count_1" Count "salary" ])
                    Filter(Binary(Ge, Col "floor_count_1", Lit(Int 3)))
                    Project [ "dept", "dept"; "avg", "avg" ]
                    page 50 ]
                  "the rewrite is a pipeline a reader can inspect"

              Expect.contains
                  a.Applied
                  (PolicyRule.GroupFloorApplied(1, 3, [ staffCol "salary" "personal" ]))
                  "the floor is among the rules applied"

              // north staff: eng 1,4,5,8,13 (5), ops 2,10,14 (3), sales 7 (1), legal 11 (1)
              match eval tables (Ref "staff") a.Pipeline with
              | Ok t ->
                  Expect.equal
                      (t.Columns |> List.head |> Sample.cells)
                      [ Str "eng"; Str "ops" ]
                      "groups smaller than the floor are gone"
              | Error e -> failtestf "%A" e

          testCase "the floor drops a group for too few contributors to an input, never for none (D21)"
          <| fun _ ->
              // north staff: eng 1,4,5,8,13 (5), ops 2,10,14 (3), sales 7 (1), legal 11 (1)
              let after (n: int) = Binary(Gt, Col "staff_id", Lit(Int n))

              let counting (withAll: bool) (copies: (string * ColExpr) list) =
                  [ for name, c in copies -> Derive(name, Case([ c, Col "salary" ], Lit Null)) ]
                  @ [ GroupBy(
                          [ "dept" ],
                          (if withAll then [ agg "all" Count "salary" ] else [])
                          @ [ for name, _ in copies -> agg name Mean name ]
                      )
                      page 50 ]

              let kept (m: ModelNode) (pipeline: Transform list) =
                  match Policy.admit m analyst (Ref "staff") pipeline with
                  | Error r -> failtestf "refused: %s" (Policy.describe r)
                  | Ok a ->
                      match eval tables (Ref "staff") a.Pipeline with
                      | Ok t -> t.Columns |> List.head |> Sample.cells |> List.sort
                      | Error e -> failtestf "%A" e

              let none = counting true [ "none", after 20 ]
              let late = counting true [ "late", after 9 ]
              let allNone = counting false [ "none", after 20; "never", after 30 ]

              // at a floor of 3
              Expect.equal
                  (kept model none)
                  [ Str "eng"; Str "ops" ]
                  "none to one input and at least three to another: kept; one to every input: dropped"

              Expect.equal (kept model late) [] "one or two contributors to any input drop the group"

              Expect.equal
                  (kept model allNone)
                  []
                  "no input with the floor's contributors: nothing shows the group has three rows, so it is dropped"

              match Policy.admit model analyst (Ref "staff") none with
              | Error r -> failtestf "refused: %s" (Policy.describe r)
              | Ok a ->
                  let count (n: int) = Col(sprintf "floor_count_%d" n)
                  let atFloor n = Binary(Ge, count n, Lit(Int 3))

                  let noneOrAtFloor n =
                      Binary(Or, Binary(Eq, count n, Lit(Int 0)), atFloor n)

                  Expect.contains
                      a.Pipeline
                      (Filter(
                          Binary(And, Binary(And, noneOrAtFloor 1, noneOrAtFloor 2), Binary(Or, atFloor 1, atFloor 2))
                      ))
                      "each count is none or at the floor, and some count is at the floor"

              // at a floor of 1 the floor drops nothing
              let atOne = modelWith [ { fullLimit with MinGroupSize = Some 1 } ]
              let everyDept = [ Str "eng"; Str "legal"; Str "ops"; Str "sales" ]

              for name, pipeline in [ "none", none; "late", late; "all none", allNone ] do
                  Expect.equal (kept atOne pipeline) everyDept (name + ": every group at a floor of 1")

              // A zero over fewer rows than the floor is about them (D21.2): one row per group, counting a copy
              // of a keyed department chosen by its own value. Kept, a zero would say the row's department is
              // not legal; at a floor of 3 every such group is dropped.
              let byValue =
                  [ Derive("legal_dept", Case([ Binary(Eq, Col "dept", str "legal"), Col "dept" ], Lit Null))
                    GroupBy([ "staff_id" ], [ agg "legal" Count "legal_dept" ])
                    page 50 ]

              let groups (m: ModelNode) =
                  match Policy.admit m keyholder (Ref "staff") byValue with
                  | Error r -> failtestf "refused: %s" (Policy.describe r)
                  | Ok a ->
                      match eval tables (Ref "staff") a.Pipeline with
                      | Ok t -> Table.rowCount t
                      | Error e -> failtestf "%A" e

              Expect.equal (groups model) 0 "a one-row group is dropped for its zero at a floor of 3"
              Expect.equal (groups atOne) 10 "at a floor of 1 each north row is its own group"

          testCase "without a declared floor, an aggregate over an aggregate-only column is refused"
          <| fun _ ->
              let m = modelWith [ { fullLimit with MinGroupSize = None } ]

              Expect.equal
                  (Policy.admit m analyst (Ref "staff") [ GroupBy([ "dept" ], [ agg "avg" Mean "salary" ]); page 5 ])
                  (Error(PolicyRefusal.FloorUndeclared(1, staffCol "salary" "personal")))
                  "no floor, no aggregate"

          testCase "the row policy wraps the source, and a pipeline that filters it away cannot remove it"
          <| fun _ ->
              let escape =
                  [ Filter(Binary(Or, Binary(Ne, Col "region", str "north"), Lit(Bool true)))
                    Project [ "staff_id", "staff_id"; "region", "region" ]
                    page 50 ]

              let a = admitted analyst escape

              Expect.equal (List.head a.Pipeline) (Filter(Binary(Eq, Col "region", str "north"))) "the policy is first"
              Expect.equal a.Written escape "the written pipeline is kept beside it"

              match eval tables (Ref "staff") a.Pipeline with
              | Ok t ->
                  Expect.isTrue
                      (t.Columns |> List.item 1 |> Sample.cells |> List.forall ((=) (Str "north")))
                      "only the subject's region"
              | Error e -> failtestf "%A" e

          testCase "a joined table's row policy is applied right after an inner join, under the joined name"
          <| fun _ ->
              let a =
                  admitted
                      analyst
                      [ Join(Ref "depts", onDept, Inner)
                        Project [ "staff_id", "staff_id" ]
                        page 50 ]

              Expect.equal
                  (List.item 2 a.Pipeline)
                  (Filter(Binary(Eq, Col "region_right", str "north")))
                  "the department's policy follows the join"

              Expect.contains
                  a.Applied
                  (PolicyRule.RowPolicyApplied(
                      "depts",
                      "department region",
                      3,
                      Binary(Eq, Col "region_right", str "north")
                  ))
                  "and is recorded where it sits"

          testCase "a policed table brought in where no filter can follow is refused"
          <| fun _ ->
              for how in [ Left; Semi; Anti ] do
                  match
                      refused analyst [ Join(Ref "depts", onDept, how); Project [ "staff_id", "staff_id" ]; page 5 ]
                  with
                  | PolicyRefusal.PolicyUnwrappable(1, "depts", "department region", _) -> ()
                  | other -> failtestf "%A join refused as %A" how other

          testCase "a row policy reading an attribute the subject lacks is refused, naming the attribute"
          <| fun _ ->
              let r = refused stranger [ Project [ "staff_id", "staff_id" ]; page 5 ]
              Expect.equal r (PolicyRefusal.AttributeMissing("staff", "own region", "region", [])) "region is missing"
              Expect.stringContains (Policy.describe r) "the subject attribute 'region'" "the attribute read is named"

          testCase "cost limits: the final Limit, its ceiling, the join and grouping caps, each a named refusal"
          <| fun _ ->
              let ids = Project [ "staff_id", "staff_id" ]
              Expect.equal (refused hr [ ids ]) (PolicyRefusal.LimitMissing 500) "no limit"

              Expect.equal
                  (refused hr [ ids; page 501 ])
                  (PolicyRefusal.LimitAboveCeiling(2, 501, 500))
                  "the table's ceiling is tighter"

              Expect.equal
                  (refused hr [ ids; Limit(Slot.Param "n", Slot.Lit 0) ])
                  (PolicyRefusal.LimitUnbound(2, "n"))
                  "a bound count"

              Expect.equal
                  (refused hr [ page 5; Join(Ref "projects", onDept, Inner); ids ])
                  (PolicyRefusal.LimitMissing 500)
                  "a join after the limit can add rows"

              Expect.equal
                  (refused
                      hr
                      [ Join(Ref "projects", onDept, Inner)
                        Join(Ref "depts", onDept, Inner)
                        Join(Ref "projects", onDept, Semi)
                        ids
                        page 5 ])
                  (PolicyRefusal.TooManyJoins(3, Some 2))
                  "three joins"

              Expect.equal
                  (refused
                      hr
                      [ GroupBy([ "dept"; "region"; "staff_id" ], [ agg "n" Count "staff_id" ])
                        page 5 ])
                  (PolicyRefusal.TooManyGroupKeys(1, 3, Some 2))
                  "three keys"

              Expect.equal
                  (Policy.admit unlimited hr (Ref "staff") [ ids; page 5 ])
                  (Error(PolicyRefusal.RowCeilingUndeclared [ "staff" ]))
                  "no ceiling anywhere"

          testCase "the verdict carries the rewritten pipeline and every rule applied, and the statement its timeout"
          <| fun _ ->
              let a = admitted analyst [ Project [ "staff_id", "staff_id" ]; page 20 ]

              Expect.equal
                  a.Applied
                  [ PolicyRule.ColumnsGranted [ staffCol "staff_id" "public", GrantLevel.RowLevel ]
                    PolicyRule.RowPolicyApplied("staff", "own region", 1, Binary(Eq, Col "region", str "north"))
                    PolicyRule.CostAdmitted(
                        20,
                        0,
                        0,
                        { MaxRows = 500
                          MaxJoins = Some 2
                          MaxGroupKeys = Some 2
                          MinGroupSize = Some 3
                          TimeoutSeconds = 30 }
                    ) ]
                  "the rules, in the order applied"

              match FoldSplit.lower a ReferenceDialect.dialect Bindings.none with
              | Ok l -> Expect.equal l.Statement.TimeoutSeconds (Some 30) "the timeout rides the statement"
              | Error r -> failtestf "%s" (Refusal.describe r)

          testCase "every refusal names its rule, the subject attribute it read and what would satisfy it"
          <| fun _ ->
              let all =
                  sample seed iterations
                  |> List.choose (fun c ->
                      match admitCase c with
                      | Error r -> Some r
                      | Ok _ -> None)

              for r in all do
                  let said = Policy.describe r
                  Expect.stringStarts said (Policy.code r) "the rule first"
                  Expect.stringContains said (Policy.attributeRead r) "the attribute read"
                  Expect.stringContains said (Policy.remedy r) "what would satisfy it"

          testCase
              "a grouped operand is admitted as a pipeline over its table: its grants, its row policy before its grouping, its floor, one join"
          <| fun _ ->
              // Departments left-joined to their staff grouped on the department (DECISIONS.md D16).
              let payroll: Operand =
                  { Name = "payroll"
                    Table = "staff"
                    Pipeline = [ GroupBy([ "dept" ], [ agg "paid" Sum "salary"; agg "heads" Count "staff_id" ]) ] }

              let ownRegion = Filter(Binary(Eq, Col "region", str "north"))

              match
                  Policy.admitWith
                      model
                      analyst
                      [ payroll ]
                      (Ref "depts")
                      [ Join(Ref "payroll", onDept, Left); page 50 ]
              with
              | Error r -> failtestf "refused: %s" (Policy.describe r)
              | Ok a ->
                  Expect.equal
                      (a.Operands |> List.map _.Pipeline)
                      [ [ ownRegion
                          GroupBy(
                              [ "dept" ],
                              [ agg "paid" Sum "salary"
                                agg "heads" Count "staff_id"
                                agg "floor_count_1" Count "salary" ]
                          )
                          Filter(Binary(Ge, Col "floor_count_1", Lit(Int 3)))
                          Project [ "dept", "dept"; "paid", "paid"; "heads", "heads" ] ] ]
                      "the operand's table policy comes first, before its grouping, and the grouping is floored"

                  Expect.contains
                      a.Applied
                      (PolicyRule.RowPolicyApplied("staff", "own region", 2, Binary(Eq, Col "region", str "north")))
                      "recorded at the admitted step of the join that brings the operand in"

                  Expect.contains
                      a.Applied
                      (PolicyRule.GroupFloorApplied(1, 3, [ staffCol "salary" "personal" ]))
                      "the operand's floor, at the written step of its join"

                  Expect.isTrue
                      (a.Applied
                       |> List.exists (fun r ->
                           match r with
                           | PolicyRule.CostAdmitted(_, 1, _, _) -> true
                           | _ -> false))
                      "the operand's join counts once against the join cap"

                  match FoldSplit.lower a ReferenceDialect.dialect Bindings.none with
                  | Error r -> failtestf "no statement: %s" (Refusal.describe r)
                  | Ok l ->
                      let operandPlan = l.Split.Operands |> List.exactlyOne
                      let planned = operandPlan.Plan.Planned

                      Expect.isLessThan
                          (planned |> List.findIndex (fun t -> t = ownRegion))
                          (planned
                           |> List.findIndex (fun t ->
                               match t with
                               | GroupBy _ -> true
                               | _ -> false))
                          "explain shows the policy inside the operand, before its grouping"

                      Expect.stringContains
                          (FoldSplit.describe l.Split |> String.concat "\n")
                          "joins the grouped operand 'payroll' as a derived table"
                          "describe names the operand join"

                      Expect.equal
                          (l.Split.Boundary |> Option.map Refusal.tag)
                          (Some "unorderedLimit")
                          "everything before the bounding limit lowers, the operand with its join"

                      let case: PlacementCase =
                          { Model = model
                            Source = Ref "depts"
                            Pipeline = a.Pipeline
                            Operands = a.Operands
                            Bindings = Bindings.none
                            Tables = tables }

                      let expected =
                          resolveIn tables "depts"
                          |> Result.bind (
                              DataFrame.evalPipelineWithInEnv
                                  (Operand.resolver Map.empty a.Operands (resolveIn tables))
                                  Map.empty
                                  a.Pipeline
                          )

                      match PlacementLaws.placed ReferenceDialect.execute case l with
                      | PlacementLaws.Answered answer -> Expect.equal answer expected "the placement is the reference's"
                      | other -> failtestf "the placement answered %A" other

          testCase "a grouped operand is refused by the rule it breaks, at the step of the join that brings it in"
          <| fun _ ->
              let op (name: string) (table: string) (steps: Transform list) : Operand =
                  { Name = name
                    Table = table
                    Pipeline = steps }

              let byDept = GroupBy([ "dept" ], [ agg "heads" Count "staff_id" ])

              let refusedWith (m: ModelNode) (subject: Subject) (o: Operand) (steps: Transform list) =
                  match Policy.admitWith m subject [ o ] (Ref "depts") (steps @ [ page 5 ]) with
                  | Error r -> r
                  | Ok _ -> failtestf "%s was admitted" o.Name

              let joined (o: Operand) = [ Join(Ref o.Name, onDept, Left) ]

              let clerk: Subject =
                  { analyst with
                      Principal = "clerk"
                      Grants = [ grant "public" GrantLevel.RowLevel; grant "internal" GrantLevel.RowLevel ] }

              Expect.equal
                  (refusedWith
                      model
                      clerk
                      (op "paid" "staff" [ GroupBy([ "dept" ], [ agg "paid" Sum "salary" ]) ])
                      (joined (op "paid" "" [])))
                  (PolicyRefusal.ColumnNotGranted(
                      PolicySite.Step 1,
                      staffCol "salary" "personal",
                      [ "public"; "internal" ]
                  ))
                  "a column the operand reads must be granted"

              Expect.equal
                  (refusedWith
                      model
                      analyst
                      (op "mailed" "staff" [ Filter(Binary(Eq, Col "email", str "x")); byDept ])
                      (joined (op "mailed" "" [])))
                  (PolicyRefusal.AggregateOnly(PolicySite.Step 1, staffCol "email" "personal", "a filter predicate"))
                  "an aggregate-only column is admitted only inside the operand's aggregates"

              Expect.equal
                  (refusedWith
                      (modelWith [ { fullLimit with MinGroupSize = None } ])
                      analyst
                      (op "paid" "staff" [ GroupBy([ "dept" ], [ agg "paid" Sum "salary" ]) ])
                      (joined (op "paid" "" [])))
                  (PolicyRefusal.FloorUndeclared(1, staffCol "salary" "personal"))
                  "no floor, no aggregate, inside an operand as outside"

              let pipelineRefusal (o: Operand) (steps: Transform list) =
                  match refusedWith model hr o steps with
                  | PolicyRefusal.Pipeline p -> p
                  | other -> failtestf "%s refused as %A" o.Name other

              match pipelineRefusal (op "heads" "staff" [ byDept ]) [ Union(Ref "heads") ] with
              | PipelineRefusal.OperandMisused(1, "heads", why) -> Expect.stringContains why "only a join" "a union"
              | other -> failtestf "%A" other

              match pipelineRefusal (op "projects" "staff" [ byDept ]) (joined (op "projects" "" [])) with
              | PipelineRefusal.OperandMisused(1, "projects", why) -> Expect.stringContains why "table" "a table's name"
              | other -> failtestf "%A" other

              match
                  pipelineRefusal
                      (op "wide" "staff" [ Join(Ref "projects", onDept, Inner); byDept ])
                      (joined (op "wide" "" []))
              with
              | PipelineRefusal.OperandMisused(1, "wide", why) -> Expect.stringContains why "second source" "one table"
              | other -> failtestf "%A" other

              match pipelineRefusal (op "lost" "nowhere" [ byDept ]) (joined (op "lost" "" [])) with
              | PipelineRefusal.Operand("lost", PipelineRefusal.UnknownTable(0, "nowhere", _)) -> ()
              | other -> failtestf "%A" other

              match
                  pipelineRefusal
                      (op "heads" "staff" [ GroupBy([ "dept" ], [ agg "heads" Count "nope" ]) ])
                      (joined (op "heads" "" []))
              with
              | PipelineRefusal.Operand("heads", PipelineRefusal.UnknownColumn(1, "nope", _)) -> ()
              | other -> failtestf "%A" other

              match
                  pipelineRefusal
                      (op "regional" "staff" [ GroupBy([ "region" ], [ agg "heads" Count "staff_id" ]) ])
                      [ Join(Ref "regional", [ "region", "region" ], Left) ]
              with
              | PipelineRefusal.UnrelatedJoin(1, "regional", _, _) -> ()
              | other -> failtestf "%A" other

              // A key renamed inside the operand is followed to the relation's column.
              match
                  Policy.admitWith
                      model
                      hr
                      [ op
                            "units"
                            "staff"
                            [ Project [ "dept", "unit"; "staff_id", "staff_id" ]
                              GroupBy([ "unit" ], [ agg "heads" Count "staff_id" ]) ] ]
                      (Ref "depts")
                      [ Join(Ref "units", [ "dept", "unit" ], Left); page 5 ]
              with
              | Ok _ -> ()
              | Error r -> failtestf "a renamed key was refused: %s" (Policy.describe r)

          testCase "the policy laws hold over the sample, with every refusal and every rewrite reached"
          <| fun _ ->
              let results = laws (fun a -> a.Pipeline) seed iterations
              census "PolicyLaws" results
              Expect.isEmpty (failed results) "every policy law holds"

          testCase "the row-policy law goes red when the policy is stripped (its falsifier)"
          <| fun _ ->
              let stripped (a: Admitted) =
                  let heads =
                      a.Applied
                      |> List.filter (fun r ->
                          match r with
                          | PolicyRule.RowPolicyApplied(t, _, _, _) -> t = "staff"
                          | PolicyRule.ColumnsGranted _
                          | PolicyRule.GroupFloorApplied _
                          | PolicyRule.CostAdmitted _ -> false)
                      |> List.length

                  List.skip heads a.Pipeline

              let results = laws stripped seed iterations

              let survival =
                  results |> List.find (fun r -> r.Law.StartsWith "a row policy survives")

              Expect.isFalse survival.Passed "a stripped policy is caught"

          testCase "the floor law goes red when the floor filter is stripped (its falsifier)"
          <| fun _ ->
              let stripped (a: Admitted) =
                  a.Pipeline
                  |> List.filter (fun s ->
                      match s with
                      | Filter(Binary(Ge, Col n, _)) -> not (n.StartsWith "floor_count")
                      | _ -> true)

              let results = laws stripped seed iterations

              let floor =
                  results
                  |> List.find (fun r -> r.Law.StartsWith "an aggregate over an aggregate-only")

              Expect.isFalse floor.Passed "a stripped floor is caught"

          // ---- the keyed level (Phase 40, DECISIONS.md D18) ----

          testCase
              "a keyed grant admits a grouping and a join keyed on the column, re-aggregated to a grain without it; an aggregate grant refuses it"
          <| fun _ ->
              let a =
                  match keyedAdmit keyholder (keyedSteps []) with
                  | Ok a -> a
                  | Error r -> failtestf "refused: %s" (Policy.describe r)

              let granted =
                  a.Applied
                  |> List.collect (fun r ->
                      match r with
                      | PolicyRule.ColumnsGranted cs -> cs
                      | _ -> [])

              Expect.contains granted (deptCol "depts", GrantLevel.Keyed) "the join key, at the keyed level"
              Expect.contains granted (deptCol "staff", GrantLevel.Keyed) "the operand's grouping key, too"

              // the re-aggregation counts the key, so ruling 5's floor holds over it as over a salary
              Expect.isTrue
                  (a.Applied
                   |> List.exists (fun r ->
                       match r with
                       | PolicyRule.GroupFloorApplied(2, 3, cs) -> List.contains (deptCol "depts") cs
                       | _ -> false))
                  "the keyed column's count is floored"

              let aggregator =
                  { keyholder with
                      Principal = "aggregator"
                      Grants =
                          [ grant "public" GrantLevel.RowLevel
                            grant "internal" GrantLevel.Aggregate
                            grant "personal" GrantLevel.Aggregate ] }

              match keyedAdmit aggregator (keyedSteps []) with
              | Error r ->
                  Expect.equal
                      r
                      (PolicyRefusal.AggregateOnly(PolicySite.Step 1, deptCol "staff", "a grouping key"))
                      "refused by name under the aggregate grant, inside the operand"

                  Expect.equal (Policy.code r) "DB-GRANT-AGGREGATE-ONLY" "its code"
              | Ok _ -> failtest "admitted under an aggregate grant"

          testCase
              "the key in the result is refused under any name, through any derivation, naming the result column and the model column"
          <| fun _ ->
              let inResult (steps: Transform list) =
                  match keyedAdmit keyholder steps with
                  | Error r -> r
                  | Ok a -> failtestf "admitted: %A" a.Pipeline

              Expect.equal
                  (inResult
                      [ Join(Ref "payroll", onDept, Inner)
                        Project [ "dept", "dept"; "paid", "paid" ]
                        page 5 ])
                  (PolicyRefusal.KeyInResult("dept", deptCol "depts"))
                  "the key itself"

              Expect.equal
                  (inResult
                      [ Join(Ref "payroll", onDept, Inner)
                        Project [ "dept", "d"; "paid", "paid" ]
                        page 5 ])
                  (PolicyRefusal.KeyInResult("d", deptCol "depts"))
                  "the key renamed"

              Expect.equal
                  (inResult
                      [ Join(Ref "payroll", onDept, Inner)
                        Derive("tag", Coalesce [ Col "dept_right"; str "none" ])
                        Project [ "tag", "tag"; "paid", "paid" ]
                        page 5 ])
                  (PolicyRefusal.KeyInResult("tag", deptCol "staff"))
                  "the operand's key, derived"

              let r =
                  inResult [ Join(Ref "payroll", onDept, Inner); Project [ "dept", "d" ]; page 5 ]

              Expect.equal (Policy.code r) "DB-GRANT-KEY-IN-RESULT" "its code"
              Expect.stringContains (Policy.describe r) "result column 'd'" "the result column, named"
              Expect.stringContains (Policy.describe r) "'depts.dept'" "the model column, named"

          testCase "a keyed column is refused in every use but a key or an aggregate's input, by name"
          <| fun _ ->
              let refusedAs (steps: Transform list) (usage: string) =
                  Expect.equal
                      (match keyedAdmit keyholder (steps @ keyedTail) with
                       | Error r -> r
                       | Ok a -> failtestf "admitted: %A" a.Pipeline)
                      (PolicyRefusal.KeyOnly(PolicySite.Step 1, deptCol "depts", usage))
                      usage

              refusedAs [ Filter(Binary(Eq, Col "dept", str "eng")) ] "a filter predicate"
              refusedAs [ Sort [ Slot.Lit "dept", Asc ] ] "a sort key"
              refusedAs [ Distinct ] "part of a row compared whole"

              for fn in [ First; Last ] do
                  Expect.equal
                      (match keyedAdmit keyholder [ GroupBy([ "region" ], [ agg "f" fn "dept" ]); page 5 ] with
                       | Error r -> r
                       | Ok a -> failtestf "admitted: %A" a.Pipeline)
                      (PolicyRefusal.KeyOnly(
                          PolicySite.Step 1,
                          deptCol "depts",
                          "the first or last value of a group, one row's value"
                      ))
                      (sprintf "%A" fn)

              Expect.equal
                  (Policy.code (PolicyRefusal.KeyOnly(PolicySite.Step 1, deptCol "depts", "x")))
                  "DB-GRANT-KEY-ONLY"
                  "its code"

          testCase "a key's Min, Max or Median is a key: it may key a grouping and never reaches the result"
          <| fun _ ->
              for fn in [ Min; Max; Median ] do
                  match keyedAdmit keyholder [ GroupBy([ "region" ], [ agg "m" fn "dept" ]); page 5 ] with
                  | Error r ->
                      Expect.equal r (PolicyRefusal.KeyInResult("m", deptCol "depts")) (sprintf "%A returned" fn)
                  | Ok _ -> failtestf "%A of a key returned" fn

                  match
                      keyedAdmit
                          keyholder
                          [ GroupBy([ "region" ], [ agg "m" fn "dept" ])
                            GroupBy([], [ agg "n" CountDistinct "m" ])
                            page 5 ]
                  with
                  | Ok _ -> ()
                  | Error r -> failtestf "%A counted: %s" fn (Policy.describe r)

              // the other aggregates answer no group member: they leave, floored, as under Aggregate
              for fn in [ Count; CountDistinct; Sum; Mean; StdDev ] do
                  match keyedAdmit keyholder [ GroupBy([ "region" ], [ agg "a" fn "budget" ]); page 5 ] with
                  | Ok _ -> ()
                  | Error r -> failtestf "%A: %s" fn (Policy.describe r)

          testCase "a result still at the key's grain is refused: a grouping or join keyed on it is re-grouped first"
          <| fun _ ->
              let grain (steps: Transform list) =
                  match keyedAdmit keyholder (steps @ [ page 5 ]) with
                  | Error r -> r
                  | Ok a -> failtestf "admitted: %A" a.Pipeline

              Expect.equal
                  (grain [ GroupBy([ "dept" ], [ agg "b" Sum "budget" ]); Project [ "b", "b" ] ])
                  (PolicyRefusal.KeyGrain(deptCol "depts"))
                  "one row per department, the department withheld"

              Expect.equal
                  (grain [ Join(Ref "payroll", onDept, Inner); Project [ "paid", "paid" ] ])
                  (PolicyRefusal.KeyGrain(deptCol "depts"))
                  "a join keyed on it"

              Expect.equal
                  (grain [ Join(Ref "payroll", onDept, Semi); Project [ "region", "region" ] ])
                  (PolicyRefusal.KeyGrain(deptCol "depts"))
                  "a semi join keyed on it, a membership test on the key"

              Expect.equal
                  (grain
                      [ Join(Ref "payroll", onDept, Inner)
                        GroupBy([ "region"; "dept" ], [ agg "p" Sum "paid" ])
                        Project [ "region", "region"; "p", "p" ] ])
                  (PolicyRefusal.KeyGrain(deptCol "depts"))
                  "a regrouping still keyed on it"

              Expect.equal (Policy.code (PolicyRefusal.KeyGrain(deptCol "depts"))) "DB-GRANT-KEY-GRAIN" "its code"

              // a grouping that is not keyed on it clears the grain
              match keyedAdmit keyholder (keyedSteps []) with
              | Ok _ -> ()
              | Error r -> failtestf "refused: %s" (Policy.describe r)

          testCase
              "a keyed column's presence may be tested in a filter or a case's condition, and the test is not the key"
          <| fun _ ->
              let admitted (label: string) (steps: Transform list) =
                  match keyedAdmit keyholder steps with
                  | Ok a ->
                      let granted =
                          a.Applied
                          |> List.collect (function
                              | PolicyRule.ColumnsGranted cs -> cs
                              | _ -> [])

                      Expect.contains granted (deptCol "depts", GrantLevel.Keyed) (label + ": at the keyed level")
                  | Error r -> failtestf "%s: %s" label (Policy.describe r)

              let blank = str ""
              let dept = Col "dept"

              // the closed set (D20.1), each as a filter predicate
              for label, test in
                  [ "a null test", IsNull dept
                    "its negation", Not(IsNull dept)
                    "equal to the empty text", Binary(Eq, dept, blank)
                    "unequal to the empty text, written the other way round", Binary(Ne, blank, dept)
                    "its coalesce with the empty text, against the empty text",
                    Binary(Ne, Coalesce [ dept; blank ], blank)
                    "beside a predicate on another column",
                    Binary(And, Not(IsNull dept), Binary(Eq, Col "region", str "north")) ] do
                  admitted label (Filter test :: keyedTail)

              // a label chosen by a presence test may be grouped on, sorted by and returned
              let label = Derive("staffed", Case([ IsNull dept, str "no" ], str "yes"))

              admitted
                  "a label grouped on and sorted by"
                  [ label
                    GroupBy([ "staffed" ], [ agg "total" Sum "budget" ])
                    Sort [ Slot.Lit "staffed", Asc ]
                    page 5 ]

              admitted
                  "a label returned row by row"
                  [ label; Project [ "region", "region"; "staffed", "staffed" ]; page 5 ]

              admitted
                  "a condition mixing a presence test with another column"
                  [ Derive(
                        "band",
                        Case([ Binary(And, Not(IsNull dept), Binary(Eq, Col "region", str "north")), str "a" ], str "b")
                    )
                    Project [ "band", "band" ]
                    page 5 ]

              // the joined side's key, carried unchanged into the rows, is tested the same way
              admitted
                  "the joined side's key"
                  [ Join(Ref "payroll", onDept, Left)
                    Derive("paid_band", Case([ IsNull(Col "dept_right"), str "unpaid" ], str "paid"))
                    GroupBy([ "paid_band" ], [ agg "total" Sum "budget"; agg "depts" Count "dept" ])
                    Sort [ Slot.Lit "paid_band", Asc ]
                    page 5 ]

          testCase "every other way of reading a keyed column through a test is refused by name"
          <| fun _ ->
              let refusal (steps: Transform list) =
                  match keyedAdmit keyholder steps with
                  | Error r -> r
                  | Ok a -> failtestf "admitted: %A" a.Pipeline

              let keyOnlyAt (step: int) (usage: string) =
                  PolicyRefusal.KeyOnly(PolicySite.Step step, deptCol "depts", usage)

              let blank = str ""
              let dept = Col "dept"

              // what a test reveals beyond present or absent stays a predicate on the key (D20.1)
              for label, test in
                  [ "equal to a non-empty literal", Binary(Eq, dept, str "eng")
                    "unequal to a non-empty literal", Binary(Ne, dept, str "eng")
                    "its coalesce compared with a non-empty literal", Binary(Eq, Coalesce [ dept; blank ], str "eng")
                    "its coalesce with another value", Binary(Eq, Coalesce [ dept; str "eng" ], blank)
                    "its coalesce with another column", Binary(Eq, Coalesce [ dept; Col "region" ], blank)
                    "a parameter", Binary(Eq, dept, Param "dept")
                    "an ordering against the empty text", Binary(Gt, dept, blank)
                    "a pattern", Binary(StartsWith, dept, str "e")
                    "a length", Binary(Eq, ApplyFn(Length, [ dept ]), Lit(Int 0))
                    "a membership", InList(dept, [ blank ])
                    "a null test of an expression over it", IsNull(ApplyFn(Upper, [ dept ]))
                    "a presence test of a case that answers the key",
                    Binary(Eq, Case([ IsNull dept, blank ], dept), blank) ] do
                  Expect.equal (refusal (Filter test :: keyedTail)) (keyOnlyAt 1 "a filter predicate") label

              // two keys compared outside a join
              Expect.equal
                  (refusal
                      [ Join(Ref "payroll", onDept, Inner)
                        Filter(Binary(Eq, dept, Col "dept_right"))
                        GroupBy([ "region" ], [ agg "depts" Count "dept" ])
                        page 5 ])
                  (keyOnlyAt 2 "a filter predicate")
                  "two keys compared outside a join"

              // a derivation's nulls are its own: a presence test of a derived name is a test of what it read
              for label, derived in
                  [ "an identity derivation", dept
                    "a derivation null exactly when the key is one value",
                    Case([ Binary(Eq, dept, str "eng"), Lit Null ], str "x") ] do
                  Expect.equal
                      (refusal (Derive("d", derived) :: Filter(IsNull(Col "d")) :: keyedTail))
                      (keyOnlyAt 2 "a filter predicate")
                      label

              // a pick of the key is the key (D18.4), and its presence is not tested this way
              Expect.equal
                  (refusal
                      [ GroupBy([ "region" ], [ agg "m" Max "dept" ])
                        Filter(IsNull(Col "m"))
                        GroupBy([], [ agg "n" Count "region" ])
                        page 5 ])
                  (keyOnlyAt 2 "a filter predicate")
                  "the presence of a key's pick"

              // the bit is not the key, and the branches still are: a branch that answers the key returns it
              let returned (derived: ColExpr) =
                  refusal [ Derive("x", derived); Project [ "region", "region"; "x", "x" ]; page 5 ]

              for label, derived in
                  [ "a case whose branch answers the key", Case([ IsNull dept, str "none" ], dept)
                    "a case answering the key when it is present", Case([ Not(IsNull dept), dept ], str "none")
                    "the key's coalesce", Coalesce [ dept; blank ]
                    "a null test outside a condition", IsNull dept
                    "a case on a non-presence condition", Case([ Binary(Eq, dept, str "eng"), str "a" ], str "b") ] do
                  Expect.equal (returned derived) (PolicyRefusal.KeyInResult("x", deptCol "depts")) label

              // and sorting by a label whose branch answers the key sorts by the key
              Expect.equal
                  (refusal
                      [ Derive("x", Case([ IsNull dept, str "none" ], dept))
                        Sort [ Slot.Lit "x", Asc ]
                        GroupBy([ "region" ], [ agg "n" Count "x" ])
                        page 5 ])
                  (keyOnlyAt 2 "a sort key")
                  "a sort by a branch that answers the key"

              // a presence filter does not reset a grain (D18.3)
              Expect.equal
                  (refusal
                      [ GroupBy([ "dept" ], [ agg "b" Sum "budget" ])
                        Filter(Not(IsNull dept))
                        Project [ "b", "b" ]
                        page 5 ])
                  (PolicyRefusal.KeyGrain(deptCol "depts"))
                  "the key's grain, filtered on presence"

          testCase "under an aggregate grant a presence test is refused as it was"
          <| fun _ ->
              let aggregator =
                  { keyholder with
                      Principal = "aggregator"
                      Grants =
                          [ grant "public" GrantLevel.RowLevel
                            grant "internal" GrantLevel.Aggregate
                            grant "personal" GrantLevel.Aggregate ] }

              let refusal (steps: Transform list) =
                  match keyedAdmit aggregator steps with
                  | Error r -> r
                  | Ok a -> failtestf "admitted: %A" a.Pipeline

              Expect.equal
                  (refusal
                      [ Filter(Not(IsNull(Col "dept")))
                        GroupBy([ "region" ], [ agg "n" Count "dept" ])
                        page 5 ])
                  (PolicyRefusal.AggregateOnly(PolicySite.Step 1, deptCol "depts", "a filter predicate"))
                  "a presence filter"

              Expect.equal
                  (refusal
                      [ Derive("staffed", Case([ IsNull(Col "dept"), str "no" ], str "yes"))
                        Project [ "staffed", "staffed" ]
                        page 5 ])
                  (PolicyRefusal.AggregateOnly(PolicySite.Result, deptCol "depts", "a result column"))
                  "a label chosen by a presence test, returned"

              // a presence counted is a row value counted, so the floor still applies as it did
              match
                  keyedAdmit
                      aggregator
                      [ Derive("staffed", Case([ IsNull(Col "dept"), Lit(Int 0) ], Lit(Int 1)))
                        GroupBy([ "region" ], [ agg "staffed" Sum "staffed" ])
                        page 5 ]
              with
              | Ok a ->
                  Expect.isTrue
                      (a.Applied
                       |> List.exists (function
                           | PolicyRule.GroupFloorApplied(_, _, cs) -> List.contains (deptCol "depts") cs
                           | _ -> false))
                      "the floor counts the department"
              | Error r -> failtestf "refused: %s" (Policy.describe r)

          testCase "a keyed grant governs an aggregate grant over the same classification; a row-level one governs both"
          <| fun _ ->
              let held (levels: GrantLevel list) =
                  Policy.level (levels |> List.map (grant "internal")) "internal"

              Expect.equal (held [ GrantLevel.Aggregate; GrantLevel.Keyed ]) (Some GrantLevel.Keyed) "keyed governs"
              Expect.equal (held [ GrantLevel.Keyed; GrantLevel.Aggregate ]) (Some GrantLevel.Keyed) "in either order"

              Expect.equal
                  (held [ GrantLevel.Keyed; GrantLevel.RowLevel ])
                  (Some GrantLevel.RowLevel)
                  "row level governs"

              Expect.equal (held []) None "none"

              for l in [ GrantLevel.Aggregate; GrantLevel.Keyed; GrantLevel.RowLevel ] do
                  Expect.equal (Policy.tryLevel (Policy.levelTag l)) (Some l) "its tag reads back"

              Expect.equal Policy.levelTags [ "aggregate"; "keyed"; "row" ] "the tags, narrowest first"
              Expect.equal (Policy.tryLevel "masked") None "an unknown tag" ]
