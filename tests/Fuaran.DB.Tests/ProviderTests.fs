/// The provider and the fold split (Phase 1900): the placement laws over the reference dialect, their
/// falsifiers, the split's report on worked pipelines, and the statement's freedom from request text.
module Fuaran.DB.Tests.ProviderTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider

let private seed = 19000
let private iterations = 600

let private model = PlacementLaws.modelOf DecimalType
let private d = ReferenceDialect.dialect

let private lower (pipeline: Transform list) (bindings: Bindings) =
    match FoldSplit.lowerUnchecked model d (Ref "orders") pipeline bindings None with
    | Ok l -> l
    | Error r -> failwithf "refused whole: %s" (Refusal.describe r)

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

/// Every identifier the reference dialect quoted in a text.
let private quoted = Regex("\"((?:[^\"]|\"\")*)\"")
let private minted = Regex(@"^[ct][0-9]+$")

/// The text-level reading of the request-text rule, for the reference dialect's spelling: every
/// quoted identifier is a physical identifier of the model or a minted alias, no string literal is
/// written, and every number outside an identifier is a parameter marker's or one of the provider's
/// own constants (the null flag's 1 and 0, `HAVING COUNT(*) > 0`, `SELECT 1`).
let private textOffence (m: ModelNode) (s: Statement) : string option =
    let physicals =
        Model.tables m
        |> List.collect (fun (node, spec) ->
            spec.Physical :: (Model.columns node |> List.map (fun (_, c) -> c.Physical)))
        |> Set.ofList

    let identifiers =
        quoted.Matches s.Text
        |> Seq.map (fun x -> x.Groups[1].Value.Replace("\"\"", "\""))
        |> List.ofSeq

    let outside = quoted.Replace(s.Text, " ")
    let markers = Regex.Replace(outside, @"@p[0-9]+", " ")

    let numbers =
        Regex.Matches(markers, @"[0-9]+")
        |> Seq.map _.Value
        |> Seq.filter (fun n -> n <> "0" && n <> "1")
        |> List.ofSeq

    match
        identifiers
        |> List.tryFind (fun i -> not (Set.contains i physicals) && not (minted.IsMatch i))
    with
    | Some i -> Some(sprintf "quoted identifier '%s' is neither the model's nor minted" i)
    | None ->
        if outside.Contains "'" then
            Some "the text writes a string literal"
        else
            match numbers with
            | n :: _ -> Some(sprintf "the text writes the number %s outside a marker" n)
            | [] -> None

[<Tests>]
let tests =
    testList
        "provider"
        [ testCase "the placement laws hold over the reference dialect, with every refusal, verb and side reached"
          <| fun _ ->
              let results = PlacementLaws.laws d ReferenceDialect.execute seed iterations
              census "PlacementLaws (reference dialect)" results
              Expect.isEmpty (failed results) "every placement law holds"

          testCase "a repeated output name is refused by the lowering and by the reference alike"
          <| fun _ ->
              // The two samples that reach `repeatedName` in the placement laws. The reference has
              // refused both since compute 0.41.0 (its D35); the lowering refuses them by name, so
              // the boundary falls at the step and the suffix meets the reference's own refusal.
              let pipelines =
                  [ [ Project [ "id", "req_x"; "s", "req_x" ] ], "req_x"
                    [ GroupBy([ "s" ], [ { Name = "s"; Fn = Count; Of = "id" } ]) ], "s" ]

              let case0 = PlacementLaws.sample seed 1 |> List.head

              for pipeline, name in pipelines do
                  match FoldSplit.lowerUnchecked model d (Ref "orders") pipeline Bindings.none None with
                  | Ok l ->
                      match l.Split.Boundary with
                      | Some(Refusal.RepeatedName(_, n)) -> Expect.equal n name "the lowering names the column"
                      | other -> failtestf "%A: the lowering placed no repeated-name boundary: %A" pipeline other
                  | Error(Refusal.RepeatedName(_, n)) -> Expect.equal n name "the lowering names the column"
                  | Error r -> failtestf "%A: the lowering refused otherwise: %s" pipeline (Refusal.describe r)

                  let c =
                      { case0 with
                          Source = Ref "orders"
                          Pipeline = pipeline
                          Bindings = Bindings.none }

                  match PlacementLaws.reference c with
                  | Error(TypeError msg) -> Expect.stringContains msg name "the reference names the column"
                  | other -> failtestf "%A: the reference did not refuse it: %A" pipeline other

          testCase "the parity law goes red over an engine that orders nulls first (its falsifier)"
          <| fun _ ->
              let results =
                  PlacementLaws.laws d (ReferenceDialect.executeWith true false) seed iterations

              Expect.isFalse (results |> List.head |> _.Passed) "nulls-first ordering is caught"

          testCase
              "the parity law goes red over an engine that answers a keyless aggregate over nothing (its falsifier)"
          <| fun _ ->
              let results =
                  PlacementLaws.laws d (ReferenceDialect.executeWith false true) seed iterations

              Expect.isFalse (results |> List.head |> _.Passed) "the missing HAVING is caught"

          testCase "a pipeline carrying a pivot lowers its prefix, leaves the pivot local, and explain says so"
          <| fun _ ->
              let pipeline =
                  [ Filter(Binary(Gt, Col "id", Lit(Int 0)))
                    GroupBy([ "b"; "s" ], [ { Name = "n"; Fn = Count; Of = "id" } ])
                    Pivot
                        { Index = [ "b" ]
                          On = "s"
                          Values = "n"
                          Agg = Sum } ]

              let l = lower pipeline Bindings.none
              // The planner prunes the columns the grouping does not read, so the planned pipeline
              // carries a projection the written one does not.
              let planned = List.length l.Split.Plan.Planned
              Expect.equal l.Split.Lowered (planned - 1) "every planned step before the pivot lowers"
              Expect.equal l.Split.Boundary (Some(Refusal.PivotNeverLowers planned)) "the pivot places the boundary"
              Expect.equal l.Suffix [ List.last pipeline ] "the pivot alone is the suffix"
              let said = FoldSplit.describe l.Split |> String.concat "\n"
              Expect.stringContains said "pivot, which never lowers" "explain names the rule"

              Expect.stringContains
                  said
                  (sprintf "steps 1 to %d of %d lower" (planned - 1) planned)
                  "explain names the prefix"

          testCase "the planner runs first: a filter it moves ahead of a sort lowers with the page"
          <| fun _ ->
              let pipeline =
                  [ Transform.sortBy [ "id", Asc ]
                    Filter(Binary(Gt, Col "id", Lit(Int 1)))
                    Transform.limit 2 0 ]

              let l = lower pipeline Bindings.none
              Expect.isNonEmpty l.Split.Plan.Applied "the planner rewrote"
              Expect.equal l.Split.Lowered 3 "every planned step lowers"
              Expect.isNone l.Split.Boundary "nothing runs locally"

          testCase "a clock lowers only when pinned, and then as a parameter"
          <| fun _ ->
              let pipeline = [ Filter(Binary(Le, Col "d", Now NowGrain.Date)) ]
              let unpinned = lower pipeline Bindings.none

              Expect.equal
                  unpinned.Split.Boundary
                  (Some(Refusal.UnpinnedClock(1, NowGrain.Date)))
                  "an unpinned clock places the boundary"

              let pinned =
                  lower
                      pipeline
                      { Bindings.none with
                          Clock = Some PlacementLaws.clock }

              Expect.isNone pinned.Split.Boundary "a pinned clock lowers"

              Expect.equal
                  (pinned.Statement.Parameters |> List.map (fun p -> p.Value, p.Type))
                  [ Date "2026-06-02", DateType ]
                  "the reading is a typed parameter"

          testCase "values bind as typed parameters, a null typed by what it is compared with, a page clamped"
          <| fun _ ->
              let pipeline =
                  [ Filter(Binary(And, Binary(Eq, Col "s", Param "p"), Not(Binary(Eq, Col "fk", Lit Null))))
                    Filter(InParam(Col "s", "set"))
                    Sort [ Slot.Param "col", Desc ]
                    Limit(Slot.Lit -3, Slot.Lit 1) ]

              let bindings =
                  { Bindings.none with
                      Params = Map.ofList [ "p", Str "zq-a"; "col", Str "amt" ]
                      Lists = Map.ofList [ "set", [ Str "zq-a"; Str "zq-b" ] ] }

              let l = lower pipeline bindings
              Expect.isNone l.Split.Boundary "every step lowers"

              Expect.equal
                  (l.Statement.Parameters |> List.map (fun p -> p.Value, p.Type))
                  [ Str "zq-a", StringType
                    Null, IntType
                    Str "zq-a", StringType
                    Str "zq-b", StringType
                    Int 0, IntType
                    Int 1, IntType ]
                  "each value in marker order, with its type"

          testCase "the statement renders as nested selects over physical identifiers and minted aliases"
          <| fun _ ->
              let l =
                  lower
                      [ Filter(Binary(Gt, Col "id", Lit(Int 2)))
                        Join(Ref "customers", [ "fk", "id" ], Inner)
                        GroupBy([ "name" ], [ { Name = "total"; Fn = Sum; Of = "amt" } ]) ]
                      Bindings.none

              Expect.equal
                  l.Statement.Text
                  ("SELECT \"c9\" AS \"c9\", SUM(\"c3\") AS \"c11\" FROM (SELECT \"t2\".\"c1\" AS \"c1\", "
                   + "\"t2\".\"c2\" AS \"c2\", \"t2\".\"c3\" AS \"c3\", \"t2\".\"c4\" AS \"c4\", \"t2\".\"c5\" AS \"c5\", "
                   + "\"t2\".\"c6\" AS \"c6\", \"t2\".\"c7\" AS \"c7\", \"t4\".\"c8\" AS \"c8\", \"t4\".\"c9\" AS \"c9\", "
                   + "\"t4\".\"c10\" AS \"c10\" FROM (SELECT \"c1\", \"c2\", \"c3\", \"c4\", \"c5\", \"c6\", \"c7\" FROM "
                   + "(SELECT \"ORD_ID\" AS \"c1\", \"ORD_CUST\" AS \"c2\", \"ORD_AMOUNT\" AS \"c3\", \"ORD_CHANNEL\" AS \"c4\", "
                   + "\"ORD_FLAG\" AS \"c5\", \"ORD_DAY\" AS \"c6\", \"ORD_POINTS\" AS \"c7\" FROM \"FACT_ORDER\") AS \"t3\" "
                   + "WHERE (\"c1\" > @p0)) AS \"t2\" INNER JOIN (SELECT \"CUST_ID\" AS \"c8\", \"CUST_NAME\" AS \"c9\", "
                   + "\"CUST_LIMIT\" AS \"c10\" FROM \"DIM_CUSTOMER\") AS \"t4\" ON \"t2\".\"c2\" = \"t4\".\"c8\") AS \"t1\" "
                   + "GROUP BY \"c9\"")
                  "the rendered text"

              Expect.equal
                  (l.Statement.Columns |> List.map (fun o -> o.Name, o.Type))
                  [ "name", StringType; "total", DecimalType ]
                  "the result schema, in the pipeline's names"

          testCase "a source the model cannot read from is refused whole"
          <| fun _ ->
              let refused (source: DataSource) =
                  match FoldSplit.lowerUnchecked model d source [] Bindings.none None with
                  | Error r -> Some r
                  | Ok _ -> None

              Expect.equal (refused (Embedded Table.empty)) (Some Refusal.EmbeddedSource) "embedded"

              Expect.equal
                  (refused (Ref "nowhere"))
                  (Some(Refusal.UnknownSource("nowhere", [ "orders"; "customers"; "touches"; "ledger" ])))
                  "unmodelled"

              Expect.equal (refused (Ref "ledger")) (Some(Refusal.EmptyRow 0)) "no columns"

          testCase "a join is admitted only along a declared relation, followed through a rename"
          <| fun _ ->
              let renamed =
                  lower
                      [ Project [ "id", "order_id"; "fk", "customer" ]
                        Join(Ref "customers", [ "customer", "id" ], Left) ]
                      Bindings.none

              Expect.isNone renamed.Split.Boundary "the renamed key still names the declared relation"

              let undeclared =
                  lower [ Join(Ref "customers", [ "id", "id" ], Inner) ] Bindings.none

              Expect.equal
                  undeclared.Split.Boundary
                  (Some(Refusal.UndeclaredJoin(1, "customers", [ "id", "id" ])))
                  "a relation the model does not declare"

          testCase "no statement over the sample carries text taken from a request"
          <| fun _ ->
              let offences =
                  PlacementLaws.sample seed iterations
                  |> List.choose (fun c ->
                      match FoldSplit.lowerUnchecked c.Model d c.Source c.Pipeline c.Bindings None with
                      | Error _ -> None
                      | Ok l ->
                          textOffence c.Model l.Statement
                          |> Option.orElse (PlacementLaws.requestTextIn d c.Model l.Statement)
                          |> Option.map (fun why -> why, l.Statement.Text))

              Expect.equal offences [] "every identifier is the model's or minted, every value a marker"

          testCase "the request-text checks catch a name and a value smuggled into a statement (their falsifier)"
          <| fun _ ->
              let l =
                  lower [ Filter(Binary(Eq, Col "s", Lit(Str "zq-a"))); Project [ "s", "req_channel" ] ] Bindings.none

              let s = l.Statement
              Expect.isNone (PlacementLaws.requestTextIn d model s) "the honest statement passes"
              Expect.isNone (textOffence model s) "and its text"

              let withName =
                  { s with
                      Text = s.Text.Replace("\"c8\"", "\"req_channel\"") }

              let withValue =
                  { s with
                      Text = s.Text.Replace("@p0", "'zq-a'") }

              let foreignScan =
                  { s with
                      Form = Relation.Scan("users", []) }

              Expect.isSome (PlacementLaws.requestTextIn d model withName) "a pipeline name used as an identifier"
              Expect.isSome (textOffence model withName) "and in the text"
              Expect.isSome (PlacementLaws.requestTextIn d model withValue) "a value written into the text"
              Expect.isSome (textOffence model withValue) "and as a literal"
              Expect.isSome (PlacementLaws.requestTextIn d model foreignScan) "a table the model does not hold"

          testCase "the provider references only FSharp.Core, Fuaran.Core, the compute packages and Fuaran.DB"
          <| fun _ ->
              let root = Path.GetDirectoryName(CorpusTests.fixturesDir ())

              let text =
                  File.ReadAllText(Path.Combine(root, "src", "Fuaran.DB.Provider", "Fuaran.DB.Provider.fsproj"))

              let references =
                  Regex.Matches(text, @"<(PackageReference|ProjectReference)\s[^>]*(Include|Update)\s*=\s*""([^""]+)""")
                  |> Seq.map (fun m ->
                      let target = m.Groups[3].Value

                      if m.Groups[1].Value = "ProjectReference" then
                          Path.GetFileNameWithoutExtension(target.Replace('\\', '/'))
                      else
                          target)
                  |> List.ofSeq

              Expect.isNonEmpty references "the scan read the project"

              let foreign =
                  references
                  |> List.filter (fun id ->
                      not (
                          id = "FSharp.Core"
                          || id = "Fuaran.DB"
                          || id.StartsWith "Fuaran.Core."
                          || id.StartsWith "Fuaran.Compute."
                      ))

              Expect.equal foreign [] "references outside the provider's boundary" ]
