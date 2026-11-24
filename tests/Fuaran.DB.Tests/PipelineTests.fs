module Fuaran.DB.Tests.PipelineTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Tests.Sample

let private check source pipeline =
    PipelineCheck.check model source pipeline

let private refused (r: Result<SchemaKnowledge, PipelineRefusal>) : PipelineRefusal =
    match r with
    | Error e -> e
    | Ok k -> failtestf "expected a refusal, the walk closed on %A" (SchemaWalk.names k)

[<Tests>]
let tests =
    testList
        "pipeline against model"
        [ testCase "a pipeline over modelled columns and a declared relation passes, closed"
          <| fun _ ->
              let pipeline =
                  [ Filter(Binary(Ge, Col "order_date", Param "asOf"))
                    Join(Ref "order_lines", [ "order_id", "order_id" ], Inner)
                    GroupBy(
                        [ "channel" ],
                        [ { Name = "rev"
                            Fn = Sum
                            Of = "line_revenue" } ]
                    )
                    Sort [ Slot.Lit "rev", Desc ]
                    Transform.limit 10 0 ]

              match check (Ref "orders") pipeline with
              | Ok k ->
                  Expect.isTrue (SchemaWalk.isClosed k) "closed"
                  Expect.equal (SchemaWalk.names k) [ "channel"; "rev" ] "the output"
              | Error e -> failtestf "refused: %s" (PipelineCheck.describe e)

          testCase "a column the model does not name is refused, naming it and the members"
          <| fun _ ->
              match refused (check (Ref "orders") [ Filter(Binary(Gt, Col "discount", Lit(Int 0))) ]) with
              | PipelineRefusal.UnknownColumn(step, column, members) ->
                  Expect.equal step 1 "the first step"
                  Expect.equal column "discount" "the column"
                  Expect.equal members [ "order_id"; "customer_id"; "order_date"; "revenue"; "channel" ] "the members"
              | other -> failtestf "expected UnknownColumn, got %A" other

          testCase "a column the steps before have aggregated away is refused at the step that reads it"
          <| fun _ ->
              let pipeline =
                  [ GroupBy(
                        [ "channel" ],
                        [ { Name = "n"
                            Fn = Count
                            Of = "order_id" } ]
                    )
                    Filter(Binary(Gt, Col "revenue", Lit(Int 0))) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.UnknownColumn(2, "revenue", members) ->
                  Expect.equal members [ "channel"; "n" ] "members"
              | other -> failtestf "expected UnknownColumn at step 2, got %A" other

          testCase "a derived column is a member for the steps after it"
          <| fun _ ->
              let pipeline =
                  [ Derive("doubled", Binary(Mul, Col "revenue", Lit(Int 2)))
                    Filter(Binary(Gt, Col "doubled", Lit(Int 0))) ]

              Expect.isOk (check (Ref "orders") pipeline) "admitted"

          testCase "a table the model does not hold is refused"
          <| fun _ ->
              match refused (check (Ref "returns") []) with
              | PipelineRefusal.UnknownTable(0, "returns", tables) ->
                  Expect.equal tables [ "orders"; "order_lines"; "customers" ] "the tables"
              | other -> failtestf "expected UnknownTable, got %A" other

          testCase "a join between tables the model does not relate is refused"
          <| fun _ ->
              match
                  refused (check (Ref "order_lines") [ Join(Ref "customers", [ "order_id", "customer_id" ], Inner) ])
              with
              | PipelineRefusal.UnrelatedJoin(1, "customers", _, relations) ->
                  Expect.isEmpty relations "no relation between order lines and customers"
              | other -> failtestf "expected UnrelatedJoin, got %A" other

          testCase "a join on keys the relation does not declare is refused, naming the relation"
          <| fun _ ->
              match refused (check (Ref "orders") [ Join(Ref "customers", [ "order_id", "customer_id" ], Left) ]) with
              | PipelineRefusal.UnrelatedJoin(1, "customers", _, relations) ->
                  Expect.equal relations [ "customer_id = customer_id" ] "the declared relation"
              | other -> failtestf "expected UnrelatedJoin, got %A" other

          testCase "a relation is usable from either side"
          <| fun _ ->
              Expect.isOk
                  (check (Ref "order_lines") [ Join(Ref "orders", [ "order_id", "order_id" ], Semi) ])
                  "declared orders -> lines, joined lines -> orders"

          testCase "a right-hand key the joined table lacks is refused"
          <| fun _ ->
              match refused (check (Ref "orders") [ Join(Ref "order_lines", [ "order_id", "order_ref" ], Inner) ]) with
              | PipelineRefusal.UnknownColumn(1, "order_ref", members) ->
                  Expect.equal members [ "order_id"; "sku"; "qty"; "line_revenue" ] "the joined table's members"
              | other -> failtestf "expected UnknownColumn, got %A" other

          testCase "an open walk is a refusal at the first read from it"
          <| fun _ ->
              let pipeline =
                  [ Pivot
                        { Index = [ "order_date" ]
                          On = "channel"
                          Values = "revenue"
                          Agg = Sum }
                    Filter(Binary(Gt, Col "web", Lit(Int 0))) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.OpenWalk(2, _) -> ()
              | other -> failtestf "expected OpenWalk at step 2, got %A" other

          testCase "an embedded operand is refused"
          <| fun _ ->
              let embedded = Embedded { Schema = []; Columns = [] }

              Expect.equal
                  (refused (check (Ref "orders") [ Union embedded ]))
                  (PipelineRefusal.EmbeddedOperand 1)
                  "refused"

              Expect.equal (refused (check embedded [])) (PipelineRefusal.EmbeddedOperand 0) "as a source too"

          testCase "a sort key bound at run time is refused until it is substituted"
          <| fun _ ->
              let pipeline = [ Sort [ Slot.Param "by", Asc ] ]

              Expect.equal (refused (check (Ref "orders") pipeline)) (PipelineRefusal.UnboundSortKey(1, "by")) "refused"

              let bound = Transform.substitute (Map.ofList [ "by", Str "revenue" ]) pipeline
              Expect.isOk (check (Ref "orders") bound) "admitted once bound to a member"

              let unbound = Transform.substitute (Map.ofList [ "by", Str "password" ]) pipeline

              match refused (check (Ref "orders") unbound) with
              | PipelineRefusal.UnknownColumn(1, "password", _) -> ()
              | other -> failtestf "expected UnknownColumn, got %A" other

          // ---- lineage (DECISIONS.md D19) ----

          testCase "a join after a rename of the key follows the key to its model column"
          <| fun _ ->
              let pipeline =
                  [ Project [ "order_id", "ref"; "customer_id", "buyer"; "revenue", "revenue" ]
                    Join(Ref "customers", [ "buyer", "customer_id" ], Inner) ]

              Expect.isOk (check (Ref "orders") pipeline) "the renamed key is the relation's column"

          testCase "a join after a grouping that keeps the key as a grouping key is admitted"
          <| fun _ ->
              let pipeline =
                  [ GroupBy(
                        [ "customer_id" ],
                        [ { Name = "spend"
                            Fn = Sum
                            Of = "revenue" } ]
                    )
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Left) ]

              Expect.isOk (check (Ref "orders") pipeline) "the grouping keeps the relation"

          testCase "a grouping that aggregates the key loses the relation, and the refusal says the key is an aggregate"
          <| fun _ ->
              let pipeline =
                  [ GroupBy(
                        [ "channel" ],
                        [ { Name = "customer_id"
                            Fn = Max
                            Of = "customer_id" } ]
                    )
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Left) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.UnresolvedJoinKey(2, "customers", "customer_id", resolved) as r ->
                  Expect.stringContains resolved "aggregate Max of 'customer_id' at step 1" "names the aggregate"
                  Expect.stringContains resolved "'channel'" "names the keys the grouping kept"
                  Expect.stringContains (PipelineCheck.describe r) "join" "described"
              | other -> failtestf "expected UnresolvedJoinKey, got %A" other

          testCase "a grouping that drops the key loses it from the rows"
          <| fun _ ->
              let pipeline =
                  [ GroupBy(
                        [ "channel" ],
                        [ { Name = "spend"
                            Fn = Sum
                            Of = "revenue" } ]
                    )
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Left) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.UnknownColumn(2, "customer_id", members) ->
                  Expect.equal members [ "channel"; "spend" ] "what the grouping kept"
              | other -> failtestf "expected UnknownColumn, got %A" other

          testCase "a derivation named like the key matches no relation, even one that copies it"
          <| fun _ ->
              for e in [ Col "customer_id"; Binary(Add, Col "customer_id", Lit(Int 0)) ] do
                  let pipeline =
                      [ Derive("customer_id", e)
                        Join(Ref "customers", [ "customer_id", "customer_id" ], Inner) ]

                  match refused (check (Ref "orders") pipeline) with
                  | PipelineRefusal.UnresolvedJoinKey(2, "customers", "customer_id", resolved) ->
                      Expect.equal resolved "is derived at step 1" "names the derivation"
                  | other -> failtestf "expected UnresolvedJoinKey, got %A" other

          testCase "a key renamed to the relation's column name, but carrying another column, is refused"
          <| fun _ ->
              let pipeline =
                  [ Project [ "order_id", "customer_id"; "revenue", "revenue" ]
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Inner) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.UnrelatedJoin(2, "customers", _, relations) ->
                  Expect.equal relations [ "customer_id = customer_id" ] "the relation it does not follow"
              | other -> failtestf "expected UnrelatedJoin, got %A" other

          testCase "a joined table's key, suffixed _right, is followed to its own model column"
          <| fun _ ->
              let pipeline =
                  [ Join(Ref "customers", [ "customer_id", "customer_id" ], Inner)
                    Join(Ref "orders", [ "customer_id_right", "customer_id" ], Semi) ]

              Expect.isOk (check (Ref "orders") pipeline) "customers.customer_id relates to orders.customer_id"

          testCase "a union loses every key's model column, and says so"
          <| fun _ ->
              let pipeline =
                  [ Union(Ref "orders")
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Inner) ]

              match refused (check (Ref "orders") pipeline) with
              | PipelineRefusal.UnresolvedJoinKey(2, "customers", "customer_id", resolved) ->
                  Expect.stringContains resolved "union at step 1" "names the union"
              | other -> failtestf "expected UnresolvedJoinKey, got %A" other

          testCase "a grouped operand's key is matched on the left key's lineage, after a rename"
          <| fun _ ->
              let operand =
                  { Name = "per_order"
                    Table = "order_lines"
                    Pipeline =
                      [ GroupBy(
                            [ "order_id" ],
                            [ { Name = "items"
                                Fn = Count
                                Of = "sku" } ]
                        ) ] }

              let renamed =
                  [ Project [ "order_id", "ref"; "revenue", "revenue" ]
                    Join(Ref "per_order", [ "ref", "order_id" ], Left) ]

              Expect.isOk (PipelineCheck.checkWith model [ operand ] (Ref "orders") renamed) "admitted"

              let derived =
                  [ Derive("ref", Col "order_id")
                    Join(Ref "per_order", [ "ref", "order_id" ], Left) ]

              match PipelineCheck.checkWith model [ operand ] (Ref "orders") derived with
              | Error(PipelineRefusal.UnresolvedJoinKey(2, "per_order", "ref", "is derived at step 1")) -> ()
              | other -> failtestf "expected UnresolvedJoinKey, got %A" other

          testCase "the staged shapes pass the check: each join key is followed to its model column"
          <| fun _ ->
              for name, pipeline in Staged.shapes do
                  match PipelineCheck.checkWith Staged.model Staged.operands (Ref "order_headers") pipeline with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s: %s" name (PipelineCheck.describe e)

          testCase "a join between two stages, neither a model table, is refused by name"
          <| fun _ ->
              let pipeline =
                  Staged.orderStage
                  @ [ Join(Ref "line_checks", [ "day", "day"; "store", "store" ], Left) ]

              match PipelineCheck.checkWith Staged.model Staged.operands (Ref "order_headers") pipeline with
              | Error(PipelineRefusal.UnknownTable(_, "line_checks", _)) -> ()
              | other -> failtestf "expected UnknownTable, got %A" other

          testCase "a kmeans step and a bootstrap step are each refused by name, before anything is walked (D22)"
          <| fun _ ->
              let kmeans = KMeans(KMeansSpec.create [ "revenue", Col "revenue" ] 2 7L)

              let bootstrap =
                  Bootstrap(
                      BootstrapSpec.create
                          [ { Name = "rev"
                              Fn = Sum
                              Of = "revenue" } ]
                          20
                          7L
                  )

              // a step the walk would refuse first, were the step kind not refused before the walk
              let unknown = Filter(Binary(Gt, Col "no_such_column", Lit(Int 0)))

              for kind, step in [ "kmeans", kmeans; "bootstrap", bootstrap ] do
                  Expect.equal
                      (refused (check (Ref "orders") [ step; Transform.limit 10 0 ]))
                      (PipelineRefusal.StepNotAdmitted(1, kind))
                      (sprintf "%s alone" kind)

                  Expect.equal
                      (refused (check (Ref "orders") [ unknown; step ]))
                      (PipelineRefusal.StepNotAdmitted(2, kind))
                      (sprintf "%s, ahead of an earlier step's refusal" kind)

                  Expect.stringContains
                      (PipelineCheck.describe (PipelineRefusal.StepNotAdmitted(2, kind)))
                      (sprintf "step 2 is a '%s' step" kind)
                      "the sentence names the step"

                  let clustered =
                      { Name = "clustered"
                        Table = "orders"
                        Pipeline = [ step ] }

                  match
                      PipelineCheck.checkWith
                          model
                          [ clustered ]
                          (Ref "orders")
                          [ Join(Ref "clustered", [ "order_id", "order_id" ], Inner) ]
                  with
                  | Error(PipelineRefusal.Operand("clustered", PipelineRefusal.StepNotAdmitted(1, k))) when k = kind ->
                      ()
                  | other -> failtestf "%s in a grouped operand: %A" kind other ]
