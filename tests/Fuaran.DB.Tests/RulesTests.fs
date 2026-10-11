module Fuaran.DB.Tests.RulesTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Tests.Sample

let private codes (root: ModelNode) =
    ModelRules.check root |> List.map (fun d -> d.Code, d.Node)

let private expectOnly (code: string) (node: string) (root: ModelNode) =
    Expect.equal (codes root) [ code, Some node ] (sprintf "exactly one %s at %s" code node)

[<Tests>]
let tests =
    testList
        "model rules"
        [ testCase "the worked model is clean"
          <| fun _ -> Expect.isEmpty (ModelRules.check model) "no defects"

          testCase "a join between tables the model does not hold is refused"
          <| fun _ ->
              model
              |> replace "j:orders-lines" (fun n ->
                  match n.Kind with
                  | NodeKind.Join j ->
                      { n with
                          Kind = NodeKind.Join { j with Right = "t:returns" } }
                  | _ -> n)
              |> expectOnly ModelRules.JoinDangling "j:orders-lines"

          testCase "a join keyed on a column of another table is refused"
          <| fun _ ->
              model
              |> replace "j:orders-lines" (fun n ->
                  match n.Kind with
                  | NodeKind.Join j ->
                      { n with
                          Kind =
                              NodeKind.Join
                                  { j with
                                      On = [ "c:orders.order_id", "c:customers.customer_id" ] } }
                  | _ -> n)
              |> expectOnly ModelRules.JoinKeyForeign "j:orders-lines"

          testCase "a join relating columns of different types is refused"
          <| fun _ ->
              model
              |> replace "j:orders-lines" (fun n ->
                  match n.Kind with
                  | NodeKind.Join j ->
                      { n with
                          Kind =
                              NodeKind.Join
                                  { j with
                                      On = [ "c:orders.order_id", "c:lines.sku" ] } }
                  | _ -> n)
              |> expectOnly ModelRules.JoinKeyType "j:orders-lines"

          testCase "a measure over a column of the wrong type is refused"
          <| fun _ ->
              model
              |> replace "x:revenue" (fun n ->
                  { n with
                      Kind =
                          NodeKind.Measure
                              { Name = "revenue"
                                Fn = Mean
                                Column = "c:orders.channel" } })
              |> expectOnly ModelRules.MeasureType "x:revenue"

          testCase "a count over a string column is a measure the model admits"
          <| fun _ ->
              model
              |> replace "x:revenue" (fun n ->
                  { n with
                      Kind =
                          NodeKind.Measure
                              { Name = "channels"
                                Fn = CountDistinct
                                Column = "c:orders.channel" } })
              |> ModelRules.check
              |> fun ds -> Expect.isEmpty ds "admitted"

          testCase "a row policy naming a column the table lacks is refused"
          <| fun _ ->
              let defects =
                  model
                  |> replace "p:orders.web" (fun n ->
                      { n with
                          Kind =
                              NodeKind.RowPolicy
                                  { Name = "web-only"
                                    Predicate = Binary(Eq, Col "sales_channel", Lit(Str "web")) } })
                  |> ModelRules.check

              match defects with
              | [ d ] ->
                  Expect.equal d.Code ModelRules.PolicyColumn "the code"
                  Expect.stringContains d.Message "sales_channel" "names the column"
                  Expect.stringContains d.Message "channel" "names the members"
              | other -> failtestf "expected one defect, got %A" other

          testCase "a classification outside the declared set is refused"
          <| fun _ ->
              model
              |> replace "c:customers.email" (fun _ -> col "c:customers.email" "email" StringType "secret")
              |> expectOnly ModelRules.ClassificationUndeclared "c:customers.email"

          testCase "a limit that is not positive is refused"
          <| fun _ ->
              for bad in [ 0; -1 ] do
                  model
                  |> replace "l:rows" (fun n ->
                      { n with
                          Kind = NodeKind.Limit(LimitSpec.rows "rows" bad) })
                  |> expectOnly ModelRules.LimitNonPositive "l:rows"

          testCase "a cost cap out of range is refused: a negative join or grouping cap, a floor or timeout below one"
          <| fun _ ->
              let rows = LimitSpec.rows "rows" 10

              for bad in
                  [ { rows with MaxJoins = Some -1 }
                    { rows with MaxGroupKeys = Some -1 }
                    { rows with MinGroupSize = Some 0 }
                    { rows with TimeoutSeconds = Some 0 } ] do
                  model
                  |> replace "l:rows" (fun n -> { n with Kind = NodeKind.Limit bad })
                  |> expectOnly ModelRules.LimitNonPositive "l:rows"

              // zero joins and zero grouping keys are caps, not faults
              Expect.isNone
                  (LimitSpec.fault
                      { rows with
                          MaxJoins = Some 0
                          MaxGroupKeys = Some 0 })
                  "a cap of zero admits none"

          testCase "duplicate table and column names are refused"
          <| fun _ ->
              model
              |> replace "t:lines" (fun n ->
                  { n with
                      Kind = NodeKind.Table { Name = "orders"; Physical = "dbo.x" } })
              |> expectOnly ModelRules.NameDuplicate "m:retail"

          testCase "a decoded tree that breaks containment is reported, not trusted"
          <| fun _ ->
              let bad =
                  { model with
                      Children = model.Children @ [ col "c:stray" "stray" IntType "public" ] }

              Expect.equal (codes bad) [ Validator.IllegalChildCode, Some "c:stray" ] "containment" ]
