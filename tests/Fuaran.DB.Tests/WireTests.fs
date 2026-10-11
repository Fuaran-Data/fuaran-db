module Fuaran.DB.Tests.WireTests

open Expecto
open Fuaran.Core
open Fuaran.DB
open Fuaran.DB.Tests.Sample

let private codeOf (r: Result<'a, WireError>) : string =
    match r with
    | Error e -> ModelWire.code e
    | Ok v -> failtestf "expected a refusal, decoded %A" v

/// The ops the suite round-trips, one per op kind and every node kind among them.
let sampleOps: ModelOp list =
    [ ModelOp.Add("t:orders", col "c:orders.discount" "discount" DecimalType "public")
      ModelOp.Add("s:sales", lines)
      ModelOp.Amend(
          "c:orders.revenue",
          NodeKind.Column
              { Name = "amount"
                Physical = "REVENUE"
                Type = DecimalType
                Classification = "public" }
      )
      ModelOp.Amend("l:rows", NodeKind.Limit(LimitSpec.rows "rows" 500))
      ModelOp.Retire "j:orders-customers"
      // last, so the vectors before it keep their numbers: a limit declaring every cost cap
      ModelOp.Amend(
          "l:rows",
          NodeKind.Limit
              { Name = "cost"
                MaxRows = 500
                MaxJoins = Some 2
                MaxGroupKeys = Some 0
                MinGroupSize = Some 5
                TimeoutSeconds = Some 30 }
      ) ]

[<Tests>]
let tests =
    testList
        "wire codec"
        [ testCase "a model state round-trips, and re-encodes to the same bytes"
          <| fun _ ->
              let retired =
                  match ModelOps.apply (ModelOp.Retire "t:customers") state with
                  | Ok s -> s
                  | Error e -> failtestf "%A" e

              for s in [ state; retired ] do
                  let text = ModelWire.encodeState s

                  match ModelWire.decodeState text with
                  | Ok back ->
                      Expect.equal back s "the same state"
                      Expect.equal (ModelWire.encodeState back) text "the same bytes"
                  | Error e -> failtestf "%s" (ModelWire.describe e)

          testCase "every op round-trips"
          <| fun _ ->
              for op in sampleOps do
                  match ModelWire.decodeOp (ModelWire.encodeOp op) with
                  | Ok back -> Expect.equal back op "the same op"
                  | Error e -> failtestf "%s" (ModelWire.describe e)

          testCase "the encoding is canonical: keys sorted, $type first"
          <| fun _ ->
              let text = ModelWire.encodeNode (leaf "l:x" (NodeKind.Limit(LimitSpec.rows "x" 5)))

              Expect.equal text """{"$type":"limit","id":"l:x","maxRows":5,"name":"x"}""" "the bytes"

          testCase "a limit's cost caps are optional: written only when declared, and an integer when present"
          <| fun _ ->
              let text =
                  """{"$type":"limit","id":"l:x","maxJoins":1,"maxRows":5,"name":"x","timeoutSeconds":9}"""

              match ModelWire.decodeNode text with
              | Ok n ->
                  Expect.equal
                      n.Kind
                      (NodeKind.Limit
                          { LimitSpec.rows "x" 5 with
                              MaxJoins = Some 1
                              TimeoutSeconds = Some 9 })
                      "the declared caps, and None for the rest"

                  Expect.equal (ModelWire.encodeNode n) text "re-encodes to its own bytes"
              | Error e -> failtestf "%s" (ModelWire.describe e)

              Expect.equal
                  (codeOf (
                      ModelWire.decodeNode """{"$type":"limit","id":"l:x","maxRows":5,"minGroupSize":"5","name":"x"}"""
                  ))
                  "DB-WIRE-WRONG-SHAPE"
                  "a cap that is not an integer"

          testCase "decoding is strict, and each refusal is named"
          <| fun _ ->
              Expect.equal (codeOf (ModelWire.decodeNode "{")) "DB-WIRE-NOT-JSON" "not JSON"

              Expect.equal
                  (codeOf (ModelWire.decodeNode """{"$type":"view","id":"v"}"""))
                  "DB-WIRE-UNKNOWN-KIND"
                  "unknown kind"

              Expect.equal
                  (codeOf (ModelWire.decodeNode """{"$type":"source","name":"s","children":[]}"""))
                  "DB-WIRE-MISSING-FIELD"
                  "missing id"

              Expect.equal
                  (codeOf (ModelWire.decodeNode """{"$type":"limit","id":"l","name":"x","maxRows":5,"note":"y"}"""))
                  "DB-WIRE-UNKNOWN-FIELD"
                  "unknown field"

              Expect.equal
                  (codeOf (ModelWire.decodeNode """{"$type":"limit","id":"l","name":"x","maxRows":5,"children":[]}"""))
                  "DB-WIRE-UNKNOWN-FIELD"
                  "a leaf carries no children"

              Expect.equal
                  (codeOf (
                      ModelWire.decodeNode
                          """{"$type":"column","id":"c","name":"a","physical":"A","type":"money","classification":"p"}"""
                  ))
                  "DB-WIRE-UNKNOWN-COLUMN-TYPE"
                  "unknown column type"

              Expect.equal
                  (codeOf (ModelWire.decodeNode """{"$type":"limit","id":"l","name":"x","maxRows":"5"}"""))
                  "DB-WIRE-WRONG-SHAPE"
                  "a string where an integer belongs"

              Expect.equal
                  (codeOf (ModelWire.decodeState """{"$type":"datasetModel","version":2,"root":{},"retired":[]}"""))
                  "DB-WIRE-UNSUPPORTED-VERSION"
                  "a version this codec does not read"

          testCase "the stream witness decodes what it encodes"
          <| fun _ ->
              for op in sampleOps do
                  Expect.equal (ModelWire.streamWitness.Decode(ModelWire.streamWitness.Encode op)) (Ok op) "round trip" ]
