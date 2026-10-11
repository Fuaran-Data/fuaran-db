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
        (NodeKind.Column
            { Name = name
              Physical = name.ToUpperInvariant()
              Type = ty
              Classification = cls })

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
              Classifications = [ "public"; "internal"; "personal" ] }
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

let private subjects = [ analyst; hr; outsider; nobody; stranger ]

// ---- the rows ----

let private tableOf (schema: Schema) (rows: Cell list list) : Table =
    { Schema = schema
      Columns =
        schema
        |> List.mapi (fun i (n, ty) -> Column.create n ty (rows |> List.map (List.item i))) }

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
        { Law = name
          Passed = slot.IsNone
          Counterexample = slot }

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

[<Tests>]
let tests =
    testList
        "policy"
        [ testCase "default-deny: a subject with no grants lowers nothing, whatever the pipeline"
          <| fun _ ->
              for label, pipeline in templates 5 @ [ "nothing at all", [] ] do
                  Expect.equal
                      (refused nobody pipeline)
                      (PolicyRefusal.NoGrants "nobody")
                      (sprintf "%s is refused" label)

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
                      (t.Columns |> List.head |> _.Cells)
                      [ Str "eng"; Str "ops" ]
                      "groups smaller than the floor are gone"
              | Error e -> failtestf "%A" e

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
                      (t.Columns |> List.item 1 |> _.Cells |> List.forall ((=) (Str "north")))
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

              Expect.isFalse floor.Passed "a stripped floor is caught" ]
