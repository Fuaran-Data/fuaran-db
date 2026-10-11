module Fuaran.DB.Tests.ModelTests

open Expecto
open Fuaran.Core
open Fuaran.DB
open Fuaran.DB.Tests.Sample

let private expectRejection (expected: ModelRejection) (r: Result<ModelState, ModelRejection>) =
    match r with
    | Error e -> Expect.equal e expected "the named rejection"
    | Ok _ -> failtest "expected a rejection"

[<Tests>]
let tests =
    testList
        "model ops"
        [ testCase "add appends a column under its table"
          <| fun _ ->
              let c = col "c:orders.discount" "discount" DecimalType "public"

              match ModelOps.apply (ModelOp.Add("t:orders", c)) state with
              | Ok s ->
                  let t = ModelNode.tryFind "t:orders" s.Root |> Option.get
                  Expect.equal (List.last t.Children) c "appended last"
              | Error e -> failtestf "refused: %A" e

          testCase "a rename is an amend that keeps identity and every reference to it"
          <| fun _ ->
              let renamed =
                  NodeKind.Column
                      { Name = "amount"
                        Physical = "REVENUE"
                        Type = DecimalType
                        Classification = "public" }

              match ModelOps.apply (ModelOp.Amend("c:orders.revenue", renamed)) state with
              | Ok s ->
                  let node = ModelNode.tryFind "c:orders.revenue" s.Root |> Option.get
                  Expect.equal (ModelNode.name node) "amount" "renamed"
                  // the measure references the column by id, so the rename leaves it valid
                  Expect.isEmpty (ModelRules.check s.Root) "the measure still resolves"
              | Error e -> failtestf "refused: %A" e

          testCase "an amend may not change a node's kind"
          <| fun _ ->
              ModelOps.apply (ModelOp.Amend("c:orders.revenue", NodeKind.Source { Name = "x" })) state
              |> expectRejection (ModelRejection.KindChange("c:orders.revenue", "column", "source"))

          testCase "retire removes the subtree, and its ids are never reused"
          <| fun _ ->
              match ModelOps.apply (ModelOp.Retire "t:customers") state with
              | Ok s ->
                  Expect.isNone (ModelNode.tryFind "c:customers.email" s.Root) "subtree gone"
                  Expect.isTrue (s.Retired.Contains "c:customers.email") "descendants retired too"

                  ModelOps.apply (ModelOp.Add("s:sales", table "t:customers" "customers" [])) s
                  |> expectRejection (ModelRejection.RetiredId "t:customers")
              | Error e -> failtestf "refused: %A" e

          testCase "the root cannot be retired"
          <| fun _ ->
              ModelOps.apply (ModelOp.Retire "m:retail") state
              |> expectRejection ModelRejection.RetireRoot

          testCase "an unknown address is refused naming what exists"
          <| fun _ ->
              match ModelOps.apply (ModelOp.Retire "t:returns") state with
              | Error(ModelRejection.UnknownNode(id, known)) ->
                  Expect.equal id "t:returns" "the id"
                  Expect.contains known "t:orders" "the known ids"
              | other -> failtestf "expected UnknownNode, got %A" other

          testCase "a duplicate id is refused, inside the added subtree too"
          <| fun _ ->
              ModelOps.apply (ModelOp.Add("t:orders", col "c:orders.revenue" "again" IntType "public")) state
              |> expectRejection (ModelRejection.DuplicateId "c:orders.revenue")

              let twice =
                  table "t:new" "new" [ col "c:new.a" "a" IntType "public"; col "c:new.a" "b" IntType "public" ]

              ModelOps.apply (ModelOp.Add("s:sales", twice)) state
              |> expectRejection (ModelRejection.DuplicateId "c:new.a")

          testCase "containment: a column may not sit under the root, nor a table under a table"
          <| fun _ ->
              ModelOps.apply (ModelOp.Add("m:retail", col "c:stray" "stray" IntType "public")) state
              |> expectRejection (
                  ModelRejection.IllegalChild("model", "column", [ "source"; "join"; "measure"; "limit" ])
              )

              let nested = table "t:outer" "outer" [ table "t:inner" "inner" [] ]

              ModelOps.apply (ModelOp.Add("s:sales", nested)) state
              |> expectRejection (ModelRejection.IllegalChild("table", "table", [ "column"; "rowPolicy"; "limit" ]))

          testCase "applyAll stops at the first refusal with its index"
          <| fun _ ->
              let ops =
                  [ ModelOp.Retire "x:revenue"
                    ModelOp.Retire "x:revenue"
                    ModelOp.Retire "l:rows" ]

              match ModelOps.applyAll ops state with
              | Error(i, ModelRejection.UnknownNode("x:revenue", _)) -> Expect.equal i 1 "the second op"
              | other -> failtestf "expected the second op refused, got %A" other

          testCase "the op-stream records, verifies and replays through Core"
          <| fun _ ->
              let sw = ModelWire.streamWitness

              let ops =
                  [ ModelOp.Add("t:orders", col "c:orders.discount" "discount" DecimalType "public")
                    ModelOp.Retire "j:orders-customers" ]

              let appended =
                  (Ok(state, OpStream.empty), ops)
                  ||> List.fold (fun acc op ->
                      acc
                      |> Result.bind (fun (st, recs) ->
                          OpStream.append OpStream.defaultHash sw (Actor.Human "author") op st recs))

              match appended with
              | Ok(final, recs) ->
                  Expect.isTrue (OpStream.verifyChain OpStream.defaultHash sw recs) "the chain verifies"

                  match OpStream.toJsonl sw recs |> OpStream.fromJsonl sw with
                  | Ok back ->
                      match OpStream.replay sw state back with
                      | Ok replayed -> Expect.equal replayed final "replay reaches the same model"
                      | Error e -> failtestf "replay failed: %A" e
                  | Error e -> failtestf "fromJsonl failed: %s" e
              | Error e -> failtestf "append failed: %A" e ]
