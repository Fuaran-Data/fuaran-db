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
open Fuaran.DB.Dialect

let private seed = 19000
let private iterations = 600

let private model = PlacementLaws.modelOf DecimalType
let private d = ReferenceDialect.dialect

let private lower (pipeline: Transform list) (bindings: Bindings) =
    match FoldSplit.lowerUnchecked model d [] (Ref "orders") pipeline bindings None with
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
                  match FoldSplit.lowerUnchecked model d [] (Ref "orders") pipeline Bindings.none None with
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
                  PlacementLaws.laws
                      d
                      (ReferenceDialect.executeAs
                          { ReferenceDialect.right with
                              NullsFirst = true })
                      seed
                      iterations

              Expect.isFalse (results |> List.head |> _.Passed) "nulls-first ordering is caught"

          testCase
              "the parity law goes red over an engine that answers a keyless aggregate over nothing (its falsifier)"
          <| fun _ ->
              let results =
                  PlacementLaws.laws
                      d
                      (ReferenceDialect.executeAs
                          { ReferenceDialect.right with
                              KeylessAlwaysRow = true })
                      seed
                      iterations

              Expect.isFalse (results |> List.head |> _.Passed) "the missing HAVING is caught"

          testCase "the derived-key law goes red over an engine that computes a derived column wrongly (its falsifier)"
          <| fun _ ->
              // An engine whose CASE takes the branch whose condition fails, and whose COALESCE reads its
              // arms from the last: each computed column keeps its type and changes its values.
              let rec wrong (s: Scalar) : Scalar =
                  match s with
                  | Scalar.Case(cases, otherwise) ->
                      Scalar.Case(cases |> List.map (fun (w, t) -> Scalar.Not w, t), otherwise)
                  | Scalar.Coalesce xs -> Scalar.Coalesce(List.rev xs)
                  | other -> other

              let rec skew (r: Relation) : Relation =
                  match r with
                  | Relation.Derive(input, columns) ->
                      Relation.Derive(skew input, columns |> List.map (fun (s, o) -> wrong s, o))
                  | Relation.Where(input, p) -> Relation.Where(skew input, p)
                  | Relation.Select(input, columns) -> Relation.Select(skew input, columns)
                  | Relation.Aggregate(input, keys, aggregates, nonEmpty) ->
                      Relation.Aggregate(skew input, keys, aggregates, nonEmpty)
                  | Relation.Ordered(input, keys, limit, offset) -> Relation.Ordered(skew input, keys, limit, offset)
                  | Relation.Join(left, right, kind, on, columns) ->
                      Relation.Join(skew left, skew right, kind, on, columns)
                  | Relation.Scan _ -> r

              let skewed: PlacementLaws.Executor =
                  fun statement tables ->
                      ReferenceDialect.execute
                          { statement with
                              Form = skew statement.Form }
                          tables

              // A derived key is one draw among many, so the falsifier reads a longer sample than the laws.
              let results = PlacementLaws.laws d skewed seed (iterations * 3)

              let law =
                  results
                  |> List.find (fun r -> r.Law.StartsWith "a grouping keyed by a column a lowered derive computed")

              Expect.isFalse law.Passed "a derived key computed wrongly is caught"

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

          testCase "a roll-up keyed by derived columns lowers to one statement, and explain names each derive"
          <| fun _ ->
              // The first consumer's shape (DECISIONS.md D15): a filter, a band, a flag read as a type,
              // a default, then a grouping on the three with a sum and a distinct count.
              let pipeline =
                  [ Filter(Binary(Gt, Col "id", Lit(Int 0)))
                    Derive(
                        "band",
                        Case(
                            [ Binary(Ge, Col "fk", Lit(Int 3)), Lit(Str "zq-high")
                              Binary(Ge, Col "fk", Lit(Int 2)), Lit(Str "zq-mid") ],
                            Lit(Str "zq-low")
                        )
                    )
                    Derive("shopper", Case([ Col "b", Lit(Str "zq-new") ], Lit(Str "zq-returning")))
                    Derive("channel", Coalesce [ Col "s"; Lit(Str "zq-unknown") ])
                    Derive("stated", Case([ IsNull(Col "amt"), Lit(Int 0) ], Lit(Int 1)))
                    GroupBy(
                        [ "band"; "shopper"; "channel" ],
                        [ { Name = "spend"; Fn = Sum; Of = "amt" }
                          { Name = "orders"
                            Fn = CountDistinct
                            Of = "id" }
                          { Name = "stating"
                            Fn = Sum
                            Of = "stated" } ]
                    ) ]

              let l = lower pipeline Bindings.none
              Expect.isNone l.Split.Boundary "every planned step lowers"
              Expect.isEmpty l.Suffix "nothing runs locally"

              Expect.equal
                  (FoldSplit.derived l.Split |> List.map snd)
                  [ "band"; "shopper"; "channel"; "stated" ]
                  "explain reports each derive that lowered"

              let said = FoldSplit.describe l.Split |> String.concat "\n"
              Expect.stringContains said "derives the column 'band' in the statement" "describe says it"
              Expect.isFalse (l.Statement.Text.Contains "zq-") "every literal is a parameter"
              Expect.isNone (textOffence model l.Statement) "no request text"

              Expect.equal
                  (Relation.parameters l.Statement.Form)
                  (List.init (List.length l.Statement.Parameters) id)
                  "the parameters are referenced once each, in the order the text renders them"

              Expect.equal
                  (l.Statement.Columns |> List.map (fun o -> o.Name, o.Type))
                  [ "band", StringType
                    "shopper", StringType
                    "channel", StringType
                    "spend", DecimalType
                    "orders", IntType
                    "stating", IntType ]
                  "the result schema, in the pipeline's names"

          testCase "the count law goes red over an engine whose COUNT counts nulls (its falsifier)"
          <| fun _ ->
              let results =
                  PlacementLaws.laws
                      d
                      (ReferenceDialect.executeAs
                          { ReferenceDialect.right with
                              CountsNulls = true })
                      seed
                      iterations

              let law =
                  results
                  |> List.find (fun r -> r.Law.StartsWith "a lowered Count equals the reference's")

              Expect.isFalse law.Passed "a null counted as a value is caught"

          testCase "the grouped-operand law goes red over an engine whose join matches a null key (its falsifier)"
          <| fun _ ->
              // A grouped operand holds one row for the null key, and a left row with a null key must not
              // meet it: an engine that matches the two is caught by the operand law itself.
              let results =
                  PlacementLaws.laws
                      d
                      (ReferenceDialect.executeAs
                          { ReferenceDialect.right with
                              NullKeysMatch = true })
                      seed
                      iterations

              let law =
                  results
                  |> List.find (fun r -> r.Law.StartsWith "a join to a grouped operand on the relation's keys")

              Expect.isFalse law.Passed "a null key matched is caught"

          testCase
              "the first consumer's attribution pipeline lowers to one statement with an empty local suffix, and equals the reference"
          <| fun _ ->
              // Each order left-joined to one attribution row per order (touches grouped on the order key,
              // the most of each field), then rolled up by the attributed value with a sum and a distinct
              // count (DECISIONS.md D16). A filter with a bound value on each side orders the parameters.
              let attribution: Operand =
                  { Name = "attribution"
                    Table = "touches"
                    Pipeline =
                      [ Filter(Binary(Gt, Col "w", Param "p_floor"))
                        GroupBy([ "ref" ], [ { Name = "weight"; Fn = Max; Of = "w" } ]) ] }

              let pipeline =
                  [ Filter(Binary(Ne, Col "s", Param "p_s"))
                    Join(Ref "attribution", [ "id", "ref" ], Left)
                    GroupBy(
                        [ "weight" ],
                        [ { Name = "spend"; Fn = Sum; Of = "amt" }
                          { Name = "customers"
                            Fn = CountDistinct
                            Of = "fk" } ]
                    ) ]

              let bindings =
                  { Bindings.none with
                      Params = Map.ofList [ "p_floor", Float 0.5; "p_s", Str "zq-a" ] }

              match FoldSplit.lowerUnchecked model d [ attribution ] (Ref "orders") pipeline bindings None with
              | Error r -> failtestf "refused whole: %s" (Refusal.describe r)
              | Ok l ->
                  Expect.isNone l.Split.Boundary "every planned step lowers"
                  Expect.isEmpty l.Suffix "nothing runs locally"

                  Expect.equal
                      (FoldSplit.joinedOperands l.Split |> List.map (fun (_, o) -> o.Name))
                      [ "attribution" ]
                      "explain names the operand joined"

                  Expect.stringContains
                      (FoldSplit.describe l.Split |> String.concat "\n")
                      "joins the grouped operand 'attribution' as a derived table"
                      "describe says it"

                  Expect.isNone (textOffence model l.Statement) "no request text"

                  Expect.equal
                      (Relation.parameters l.Statement.Form)
                      (List.init (List.length l.Statement.Parameters) id)
                      "the parameters are referenced once each, in the order the text renders them"

                  Expect.equal
                      (l.Statement.Parameters |> List.map _.Value)
                      [ Str "zq-a"; Float 0.5 ]
                      "the left side's parameters render first, the operand's after them"

                  let c =
                      { (PlacementLaws.sample seed 1 |> List.head) with
                          Source = Ref "orders"
                          Pipeline = pipeline
                          Operands = [ attribution ]
                          Bindings = bindings }

                  match PlacementLaws.placed ReferenceDialect.execute c l with
                  | PlacementLaws.Answered answer ->
                      Expect.equal answer (PlacementLaws.reference c) "the placement equals the reference"
                  | other -> failtestf "%A" other

          testCase "a grouped operand not grouped on exactly the join's keys places the boundary, by name"
          <| fun _ ->
              let operand (steps: Transform list) : Operand =
                  { Name = "touch"
                    Table = "touches"
                    Pipeline = steps }

              let count = { Name = "n"; Fn = Count; Of = "w" }

              let boundaryOf (o: Operand) =
                  match
                      FoldSplit.lowerUnchecked
                          model
                          d
                          [ o ]
                          (Ref "orders")
                          [ Join(Ref "touch", [ "id", "ref" ], Left) ]
                          Bindings.none
                          None
                  with
                  | Ok l -> l.Split.Boundary
                  | Error r -> failtestf "refused whole: %s" (Refusal.describe r)

              for steps, said in
                  [ [ GroupBy([ "ref"; "w" ], [ count ]) ], "could fan out"
                    [ Filter(Not(IsNull(Col "w"))) ], "does not end in a grouping"
                    [ GroupBy([ "ref" ], [ count ]); Derive("m", Col "n") ], "after its last grouping"
                    [ GroupBy([ "ref" ], [ { count with Fn = Median } ]) ], "does not lower"
                    [ Join(Ref "orders", [ "ref", "id" ], Inner); GroupBy([ "ref" ], [ count ]) ], "second source" ] do
                  match boundaryOf (operand steps) with
                  | Some(Refusal.OperandNotLowered(1, "touch", why) as r) ->
                      Expect.stringContains why said (sprintf "%A" steps)
                      Expect.stringContains (Refusal.describe r) "grouped operand 'touch'" "describe names the operand"
                  | other -> failtestf "%A placed %A" steps other

              Expect.isNone (boundaryOf (operand [ GroupBy([ "ref" ], [ count ]) ])) "grouped on the key, it lowers"

          testCase "a derive whose type the schema does not decide places the boundary as it did before"
          <| fun _ ->
              let pipeline =
                  [ Filter(Binary(Gt, Col "id", Lit(Int 0)))
                    Derive("mixed", Case([ Col "b", Lit(Int 1) ], Lit(Str "zq-one"))) ]

              let l = lower pipeline Bindings.none
              let at = List.length l.Split.Plan.Planned
              Expect.equal l.Split.Boundary (Some(Refusal.VerbNotLowered(at, "derive"))) "the refusal it had"
              Expect.isEmpty (FoldSplit.derived l.Split) "no derive lowered"

              Expect.stringContains
                  (FoldSplit.describe l.Split |> String.concat "\n")
                  "a derive whose column type the schema does not decide"
                  "describe names the rule"

          testCase "a cast lowers only along the pairs the subset admits, and a derive in place keeps its position"
          <| fun _ ->
              let boundary (e: ColExpr) =
                  (lower [ Derive("x", e) ] Bindings.none).Split.Boundary

              Expect.isNone (boundary (Cast(FloatType, Col "fk"))) "an int to a float lowers"
              Expect.isNone (boundary (Cast(IntType, Col "b"))) "a bool to an int lowers"
              Expect.isNone (boundary (Cast(StringType, Lit Null))) "a typed null lowers as a parameter"
              Expect.isNone (boundary (Cast(IntType, Col "id"))) "a cast to the operand's own type lowers"

              Expect.equal
                  (boundary (Cast(StringType, Col "fk")))
                  (Some(Refusal.ConstructExcluded(1, Construct.Cast StringType)))
                  "a number formatted as text stays local"

              Expect.equal
                  (boundary (Cast(IntType, Col "amt")))
                  (Some(Refusal.ConstructExcluded(1, Construct.Cast IntType)))
                  "a decimal truncated to an int stays local"

              let l =
                  lower [ Derive("s", Coalesce [ Col "s"; Lit(Str "zq-none") ]) ] Bindings.none

              Expect.equal
                  (l.Statement.Columns |> List.map _.Name)
                  [ "id"; "fk"; "amt"; "s"; "b"; "d"; "big" ]
                  "a derive over a carried name replaces it in place"

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
                  match FoldSplit.lowerUnchecked model d [] source [] Bindings.none None with
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
                      match FoldSplit.lowerUnchecked c.Model d c.Operands c.Source c.Pipeline c.Bindings None with
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

              Expect.equal foreign [] "references outside the provider's boundary"

          // ---- the staged shapes: the acceptance pack (DECISIONS.md D19) ----

          testCase "the join-after-a-grouping law goes red over an engine whose join matches a null key (its falsifier)"
          <| fun _ ->
              // A grouping keeps the null key as one group, and a dimension row may hold a null key: an
              // engine that matches the two is caught by the law over joins after a grouping.
              let results =
                  PlacementLaws.laws
                      d
                      (ReferenceDialect.executeAs
                          { ReferenceDialect.right with
                              NullKeysMatch = true })
                      seed
                      iterations

              let law =
                  results
                  |> List.find (fun r -> r.Law.StartsWith "a join whose left side is a grouping on the relation's key")

              Expect.isFalse law.Passed "a null key matched after a grouping is caught"

          testCase "under the keyed grant, the staged shapes are admitted as written, their presence tests included"
          <| fun _ ->
              // The order stage keeps the orders of a known customer by testing the customer key for
              // presence, and the line stage labels an order without lines by testing the line's order key
              // for null, then sorts by the labels. Phase 41 found both refused under the keyed grant
              // (DECISIONS.md D19.7). Each answers whether a row has a key, never which (D20).
              let keyedAt (table: string) (column: string) =
                  { Table = table
                    Column = column
                    Classification = Sample.Staged.keyed },
                  GrantLevel.Keyed

              for name, pipeline in Sample.Staged.shapes do
                  match Sample.Staged.admit Sample.Staged.subject pipeline with
                  | Ok admitted ->
                      let granted =
                          admitted.Applied
                          |> List.collect (function
                              | PolicyRule.ColumnsGranted cs -> cs
                              | _ -> [])

                      Expect.contains granted (keyedAt "order_headers" "customer_key") (name + ": its presence test")

                      if name = "lines" || name = "new_shopper_products" then
                          Expect.contains granted (keyedAt "order_lines" "order_key") (name + ": the line's key")
                  | Error r -> failtestf "%s: %s" name (Policy.describe r)

          testCase
              "under the keyed grant, five staged shapes lower in full in every dialect, and the sixth's prefix ends at its grain"
          <| fun _ ->
              for dialect in [ d; SqlServer.dialect; PostgreSql.dialect ] do
                  for name, pipeline in Sample.Staged.shapes do
                      match Sample.Staged.admit Sample.Staged.subject pipeline with
                      | Error r -> failtestf "%s: %s" name (Policy.describe r)
                      | Ok admitted ->
                          match FoldSplit.explain admitted dialect Bindings.none with
                          | Error r -> failtestf "%s through %s: %s" name dialect.Name (Refusal.describe r)
                          | Ok report ->
                              let local = FoldSplit.suffix report

                              if name = "quality" then
                                  // The boundary is the first step after the returned grain, and the
                                  // suffix neither groups nor joins: it reads rows already at that grain.
                                  let lastGrouping =
                                      report.Plan.Planned
                                      |> List.findIndexBack (function
                                          | GroupBy _ -> true
                                          | _ -> false)

                                  Expect.isSome report.Boundary "the sixth places its boundary"
                                  Expect.isGreaterThan report.Lowered lastGrouping "the prefix ends at the grain"

                                  Expect.isTrue
                                      (report.Plan.Planned
                                       |> List.skip (lastGrouping + 1)
                                       |> List.take (report.Lowered - lastGrouping - 1)
                                       |> List.forall (function
                                           | Filter _
                                           | Project _ -> true
                                           | _ -> false))
                                      "only the grouping's own filter and projection lower after it"

                                  Expect.isFalse
                                      (local
                                       |> List.exists (function
                                           | GroupBy _
                                           | Join _ -> true
                                           | _ -> false))
                                      "the suffix neither groups nor joins"

                                  Expect.isTrue
                                      (local
                                       |> List.exists (function
                                           | Unpivot _ -> true
                                           | _ -> false))
                                      "the tail unpivot stays local"

                                  let said = FoldSplit.describe report |> String.concat "\n"
                                  Expect.stringContains said "the local suffix is steps" "explain names the suffix"
                              else
                                  Expect.isNone
                                      report.Boundary
                                      (sprintf "%s through %s lowers in full" name dialect.Name)

                                  Expect.isEmpty local (name + ": an empty local suffix")

          testCase "under the keyed grant each staged shape answers exactly what the row-level grant answers (D21)"
          <| fun _ ->
              let answer (holder: Subject) (pipeline: Transform list) =
                  match Sample.Staged.admit holder pipeline with
                  | Ok admitted -> admitted, Sample.Staged.reference admitted
                  | Error r -> failtestf "%s: %s" holder.Principal (Policy.describe r)

              for name, pipeline in Sample.Staged.shapes do
                  let admitted, keyed = answer Sample.Staged.subject pipeline
                  let _, rows = answer Sample.Staged.rowLevel pipeline

                  if name = "shoppers" then
                      // Its grouping counts four keyed columns, three of them chosen by a condition, and
                      // a group with no new shopper, or none returning, has a count of zero for that copy.
                      // The floor still counts the customer key; a zero no longer drops the group (D21).
                      let last =
                          admitted.Applied
                          |> List.choose (function
                              | PolicyRule.GroupFloorApplied(step, _, cs) -> Some(step, cs)
                              | _ -> None)
                          |> List.maxBy fst

                      Expect.equal
                          (snd last |> List.map (fun c -> c.Table, c.Column))
                          [ "order_headers", "customer_key" ]
                          "the floor counts the customer key"

                  match keyed with
                  | Ok t -> Expect.isGreaterThan (Table.rowCount t) 0 (name + ": the keyed grant answers rows")
                  | Error e -> failtestf "%s: the reference refused: %A" name e

                  Expect.isTrue
                      (Fuaran.DB.Conformance.Kit.agrees true keyed rows)
                      (sprintf "%s: %A against %A" name keyed rows)

          testCase
              "a kept group's aggregates over an input with no contributor, in the reference and every dialect (D21)"
          <| fun _ ->
              let execute (s: Statement) =
                  ReferenceDialect.execute s Sample.Staged.physical
                  |> Result.mapError (sprintf "%A")

              for floor in [ 1; 2 ] do
                  let admitted =
                      match Sample.Staged.admitAt floor Sample.Staged.subject Sample.Staged.emptyInputs with
                      | Ok a -> a
                      | Error r -> failtestf "floor %d: %s" floor (Policy.describe r)

                  let expected =
                      match Sample.Staged.reference admitted with
                      | Ok t -> t
                      | Error e -> failtestf "floor %d: the reference refused: %A" floor e

                  Expect.isGreaterThan (Table.rowCount expected) 0 (sprintf "floor %d: groups are kept" floor)

                  let cellsOf (name: string) =
                      expected.Columns
                      |> List.zip (expected.Schema |> List.map _.Name)
                      |> List.find (fun (n, _) -> n = name)
                      |> snd
                      |> Sample.cells

                  // a count over no contributor is zero; a sum, a mean and a pick over nothing are null
                  for name, answer in
                      [ "never_shoppers", Int 0
                        "never_distinct", Int 0
                        "never_count", Int 0
                        "never_sum", Null
                        "never_mean", Null
                        "never_least", Null
                        "never_most", Null ] do
                      for cell in cellsOf name do
                          Expect.equal cell answer (sprintf "floor %d: %s over no contributor" floor name)

                  // above a floor of 1, a kept group has at least the floor's contributors to some input
                  for orders in cellsOf "orders" do
                      match orders with
                      | Int n -> Expect.isGreaterThanOrEqual n floor (sprintf "floor %d: a kept group" floor)
                      | other -> failtestf "floor %d: a count answered %A" floor other

                  for dialect in [ d; SqlServer.dialect; PostgreSql.dialect ] do
                      match Sample.Staged.placed dialect execute admitted with
                      | Error why -> failtestf "floor %d through %s: %s" floor dialect.Name why
                      | Ok(_, answer) ->
                          Expect.isTrue
                              (Fuaran.DB.Conformance.Kit.agrees true answer (Ok expected))
                              (sprintf "floor %d through %s: %A against %A" floor dialect.Name answer expected)

          testCase "the staged shapes' placements equal the reference evaluator in every dialect"
          <| fun _ ->
              let execute (s: Statement) =
                  ReferenceDialect.execute s Sample.Staged.physical
                  |> Result.mapError (sprintf "%A")

              for dialect in [ d; SqlServer.dialect; PostgreSql.dialect ] do
                  for name, pipeline in Sample.Staged.shapes do
                      match Sample.Staged.admit Sample.Staged.subject pipeline with
                      | Error r -> failtestf "%s: %s" name (Policy.describe r)
                      | Ok admitted ->
                          let expected = Sample.Staged.reference admitted

                          match expected with
                          | Ok t -> Expect.isGreaterThan (Table.rowCount t) 0 (name + ": the reference answers rows")
                          | Error e -> failtestf "%s: the reference refused: %A" name e

                          match Sample.Staged.placed dialect execute admitted with
                          | Error why -> failtestf "%s through %s: %s" name dialect.Name why
                          | Ok(_, answer) ->
                              Expect.isTrue
                                  (Fuaran.DB.Conformance.Kit.agrees true answer expected)
                                  (sprintf "%s through %s: %A against %A" name dialect.Name answer expected) ]
