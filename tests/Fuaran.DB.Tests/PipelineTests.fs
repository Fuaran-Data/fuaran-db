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
              | other -> failtestf "expected UnknownColumn, got %A" other ]
