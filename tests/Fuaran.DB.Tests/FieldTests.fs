/// A modelled column is a Core `Field` plus its classification (Phase 2, DECISIONS.md D12): the model
/// states a column's unit, label and description once; the wire carries them only when stated; seeding
/// keeps a reported unit; drift reports a stated unit or label change beside a retype; and a pipeline
/// checked against the model meets the evaluator's unit check over the model's fields.
module Fuaran.DB.Tests.FieldTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider
open Fuaran.DB.Session

let private unitOf (text: string) : UnitOfMeasure =
    match Unit.parse text with
    | Ok u -> u
    | Error e -> failtestf "unit '%s' refused: %A" text e

let private kg = unitOf "kg"
let private lb = unitOf "[lb_av]"
let private metre = unitOf "m"
let private km = unitOf "km"

let private measured (id: string) (name: string) (ty: ColumnType) (u: UnitOfMeasure) : ModelNode =
    Sample.leaf
        id
        (NodeKind.Column
            { ColumnSpec.create name (name.ToUpperInvariant()) ty "public" with
                Field = Field.create name ty |> Field.withUnit u })

let private plain (id: string) (name: string) (ty: ColumnType) : ModelNode =
    Sample.leaf id (NodeKind.Column(ColumnSpec.create name (name.ToUpperInvariant()) ty "public"))

/// A freight model: weights in kilograms and in pounds, and two routes whose distances are in metres
/// and in kilometres, related by a declared join on the distance.
let private freight: ModelNode =
    let labelled =
        Sample.leaf
            "c:shipments.weight_kg"
            (NodeKind.Column
                { ColumnSpec.create "weight_kg" "WEIGHT_KG" FloatType "public" with
                    Field =
                        Field.create "weight_kg" FloatType
                        |> Field.withUnit kg
                        |> Field.withLabel "Gross weight"
                        |> Field.withDescription "The shipment's gross weight, packaging included." })

    { Id = "m:freight"
      Kind =
        NodeKind.Model
            { Name = "freight"
              Classifications = [ "public" ]
              Recordable = [] }
      Children =
        [ { Id = "s:logistics"
            Kind =
              NodeKind.Source
                  { Name = "logistics"
                    Fingerprint = None }
            Children =
              [ Sample.table
                    "t:shipments"
                    "shipments"
                    [ plain "c:shipments.id" "id" IntType
                      labelled
                      measured "c:shipments.weight_lb" "weight_lb" FloatType lb
                      plain "c:shipments.note" "note" StringType ]
                Sample.table
                    "t:legs"
                    "legs"
                    [ plain "c:legs.id" "id" IntType
                      measured "c:legs.dist_m" "dist_m" FloatType metre ]
                Sample.table
                    "t:routes"
                    "routes"
                    [ plain "c:routes.id" "id" IntType
                      measured "c:routes.dist_km" "dist_km" FloatType km ] ] }
          Sample.leaf
              "j:shipments-legs"
              (NodeKind.Join
                  { Name = "shipment legs"
                    Left = "t:shipments"
                    Right = "t:legs"
                    On = [ "c:shipments.id", "c:legs.id" ] })
          Sample.leaf
              "j:legs-routes"
              (NodeKind.Join
                  { Name = "leg route by distance"
                    Left = "t:legs"
                    Right = "t:routes"
                    On = [ "c:legs.dist_m", "c:routes.dist_km" ] }) ] }

let private check (source: string) (pipeline: Transform list) =
    PipelineCheck.check freight (Ref source) pipeline

let private reader: Subject =
    { Principal = "reader"
      Attributes = Map.empty
      Grants =
        [ { Classification = "public"
            Level = GrantLevel.RowLevel } ] }

let private reportedShipments (fields: Field list) : ReportedSchema =
    { Tables =
        [ { Name = "dbo.shipments"
            Columns = fields } ] }

/// The freight model's shipments table, seeded alone, so its physical names are the reported ones.
let private seeded =
    Drift.seed
        "m:seeded"
        "seeded"
        "logistics"
        "public"
        (reportedShipments
            [ Field.create "ID" IntType
              Field.create "WEIGHT_KG" FloatType
              |> Field.withUnit kg
              |> Field.withLabel "Gross weight" ])

[<Tests>]
let tests =
    testList
        "a modelled column is a Core field"
        [ testCase "a column reads its name and type through its field, and renaming or retyping keeps the metadata"
          <| fun _ ->
              let c =
                  { ColumnSpec.create "w" "W" FloatType "public" with
                      Field = Field.create "w" FloatType |> Field.withUnit kg |> Field.withLabel "Weight" }

              Expect.equal (c.Name, c.Type) ("w", FloatType) "the field's name and type"

              let moved = c |> ColumnSpec.rename "weight" |> ColumnSpec.retype DecimalType
              Expect.equal (moved.Name, moved.Type) ("weight", DecimalType) "renamed and retyped"
              Expect.equal (moved.Field.Unit, moved.Field.Label) (Some kg, Some "Weight") "the metadata survives"
              Expect.equal (moved.Physical, moved.Classification) ("W", "public") "the model's own members survive"

              Expect.equal
                  (ColumnSpec.physicalField c)
                  (Field.create "W" FloatType |> Field.withUnit kg |> Field.withLabel "Weight")
                  "the physical field is the same field under the source's name"

          testCase "the model's schema hands a pipeline the fields, units included"
          <| fun _ ->
              let shipments = Model.tryTable "shipments" freight |> Option.get |> fst

              Expect.equal
                  (Model.schemaOf shipments |> List.map (fun f -> f.Name, f.Unit))
                  [ "id", None; "weight_kg", Some kg; "weight_lb", Some lb; "note", None ]
                  "each column's field"

          testCase "a column without metadata encodes to the bytes it encoded before fields carried any"
          <| fun _ ->
              Expect.equal
                  (ModelWire.encodeNode (Sample.col "c:x" "amount" DecimalType "public"))
                  """{"$type":"column","classification":"public","id":"c:x","name":"amount","physical":"AMOUNT","type":"decimal"}"""
                  "no unit, label, description or extension member is written"

          testCase "a column's unit, label and description ride the wire in Core's field spelling and round-trip"
          <| fun _ ->
              let node = ModelNode.tryFind "c:shipments.weight_kg" freight |> Option.get
              let text = ModelWire.encodeNode node

              Expect.stringContains text "\"unit\":\"kg\"" "the unit, as Core renders it"
              Expect.stringContains text "\"label\":\"Gross weight\"" "the label"

              match ModelWire.decodeNode text with
              | Ok back ->
                  Expect.equal back node "the same column"
                  Expect.equal (ModelWire.encodeNode back) text "the same bytes"
              | Error e -> failtestf "%s" (ModelWire.describe e)

              let state = ModelOps.ofRoot freight

              match ModelWire.decodeState (ModelWire.encodeState state) with
              | Ok back -> Expect.equal back state "the whole model, every unit kept"
              | Error e -> failtestf "%s" (ModelWire.describe e)

          testCase "a unit Core cannot read is refused by name"
          <| fun _ ->
              let text =
                  """{"$type":"column","classification":"public","id":"c:x","name":"w","physical":"W","type":"float","unit":"furlong"}"""

              match ModelWire.decodeNode text with
              | Error e -> Expect.equal (ModelWire.code e) "DB-WIRE-BAD-FIELD" "the field codec's refusal"
              | Ok n -> failtestf "decoded %A" n

          testCase "seeding keeps the unit and the label a source reports"
          <| fun _ ->
              let weight =
                  Model.columns (Model.tables seeded |> List.head |> fst)
                  |> List.map snd
                  |> List.find (fun c -> c.Name = "WEIGHT_KG")

              Expect.equal weight.Field.Unit (Some kg) "the reported unit"
              Expect.equal weight.Field.Label (Some "Gross weight") "the reported label"

          testCase "a source that states no unit is silent about it, not reporting its withdrawal"
          <| fun _ ->
              let silent =
                  reportedShipments [ Field.create "ID" IntType; Field.create "WEIGHT_KG" FloatType ]

              Expect.equal
                  (Drift.check seeded "logistics" silent)
                  (Some [ TableDrift.InStep "dbo.shipments" ])
                  "in step"

          testCase "a unit or label the source states differently is drift beside a retype, through SchemaDelta"
          <| fun _ ->
              let moved =
                  reportedShipments
                      [ Field.create "ID" DecimalType
                        Field.create "WEIGHT_KG" FloatType
                        |> Field.withUnit lb
                        |> Field.withLabel "Weight (lb)" ]

              match Drift.check seeded "logistics" moved with
              | Some [ TableDrift.Drifted("dbo.shipments", delta) ] ->
                  Expect.equal delta.Retyped [ "ID", IntType, DecimalType ] "the retype"

                  Expect.equal
                      delta.Amended
                      [ "WEIGHT_KG", FieldChange.Unit(Some kg, Some lb)
                        "WEIGHT_KG", FieldChange.Label(Some "Gross weight", Some "Weight (lb)") ]
                      "the unit and the label, in Core's member order"

                  match Schema.classify [ "WEIGHT_KG" ] delta with
                  | SchemaCompat.Breaking _ -> ()
                  | other -> failtestf "a replaced unit is Core's breaking change, got %A" other
              | other -> failtestf "expected one drifted table, got %A" other

          testCase "a pipeline that adds pounds to kilograms is refused by the evaluator's unit check"
          <| fun _ ->
              match check "shipments" [ Derive("total", Binary(Add, Col "weight_kg", Col "weight_lb")) ] with
              | Error(PipelineRefusal.UnitMismatch(1, at, l, r) as refusal) ->
                  Expect.stringContains at "total" "names the derive"
                  Expect.equal (l, r) ("kg", "[lb_av]") "names both units"
                  Expect.stringContains (PipelineCheck.describe refusal) "unit mismatch" "describes it"
              | other -> failtestf "expected a unit refusal, got %A" other

          testCase "converting one side explicitly is admitted, and the derived column carries the unit"
          <| fun _ ->
              let pipeline =
                  [ Derive("total", Binary(Add, Col "weight_kg", Convert(Col "weight_lb", kg)))
                    Filter(Binary(Gt, Col "total", Lit(Float 10.0))) ]

              match check "shipments" pipeline with
              | Ok k -> Expect.equal (SchemaWalk.unitOf "total" k) (Some kg) "kilograms"
              | Error e -> failtestf "refused: %s" (PipelineCheck.describe e)

          testCase "a column the model states no unit for is unconstrained"
          <| fun _ ->
              match check "shipments" [ Derive("x", Binary(Add, Col "weight_kg", Col "id")) ] with
              | Ok _ -> ()
              | Error e -> failtestf "refused: %s" (PipelineCheck.describe e)

          testCase "a filter comparing kilograms to pounds is refused at its step"
          <| fun _ ->
              match check "shipments" [ Sort []; Filter(Binary(Gt, Col "weight_kg", Col "weight_lb")) ] with
              | Error(PipelineRefusal.UnitMismatch(2, _, "kg", "[lb_av]")) -> ()
              | other -> failtestf "expected a unit refusal at step 2, got %A" other

          testCase "a declared join whose keys state different units is refused by the comparison's rule"
          <| fun _ ->
              match check "legs" [ Join(Ref "routes", [ "dist_m", "dist_km" ], Inner) ] with
              | Error(PipelineRefusal.UnitMismatch(1, at, "m", "km")) ->
                  Expect.stringContains at "dist_m" "names the key"
              | other -> failtestf "expected a unit refusal, got %A" other

              match check "shipments" [ Join(Ref "legs", [ "id", "id" ], Inner) ] with
              | Ok _ -> ()
              | Error e -> failtestf "unitless keys refused: %s" (PipelineCheck.describe e)

          testCase "admission refuses the mixed-unit pipeline before anything is lowered"
          <| fun _ ->
              match
                  Policy.admit
                      freight
                      reader
                      (Ref "shipments")
                      [ Derive("total", Binary(Add, Col "weight_kg", Col "weight_lb")) ]
              with
              | Error(PolicyRefusal.Pipeline(PipelineRefusal.UnitMismatch _)) -> ()
              | other -> failtestf "expected the unit refusal, got %A" other

          testCase "a peer's projection carries the units, and a unit change between pins is breaking drift"
          <| fun _ ->
              let pinned = ModelProjection.project freight reader.Grants

              let relabelled =
                  freight
                  |> Sample.replace "c:shipments.weight_lb" (fun n ->
                      match n.Kind with
                      | NodeKind.Column c ->
                          { n with
                              Kind =
                                  NodeKind.Column
                                      { c with
                                          Field = c.Field |> Field.withUnit kg |> Field.withLabel "Weight" } }
                      | _ -> n)

              let fresh = ModelProjection.project relabelled reader.Grants
              let drift = ModelProjection.drift pinned fresh

              Expect.equal
                  drift.Changes
                  [ ProjectionChange.ColumnAmended("shipments", "weight_lb", FieldChange.Unit(Some lb, Some kg))
                    ProjectionChange.ColumnAmended("shipments", "weight_lb", FieldChange.Label(None, Some "Weight")) ]
                  "the unit and the label"

              Expect.equal
                  drift.Breaking
                  [ ProjectionChange.ColumnAmended("shipments", "weight_lb", FieldChange.Unit(Some lb, Some kg)) ]
                  "only the unit can refuse a pipeline the pin admitted"

          testCase "the AI surface tells a caller a column's unit and label"
          <| fun _ ->
              let tables = AiTools.readTools |> List.find (fun t -> t.Name = "tables")

              let text =
                  tables.Run(ModelOps.ofRoot freight) |> Canonical.write Rendering.CrossHost

              Expect.stringContains text "\"unit\":\"[lb_av]\"" "the unit"
              Expect.stringContains text "\"label\":\"Gross weight\"" "the label"

          testCase "a reported schema's units ride the connection record and round-trip"
          <| fun _ ->
              let op =
                  SessionOp.SchemaReported(
                      "logistics",
                      reportedShipments [ Field.create "WEIGHT_KG" FloatType |> Field.withUnit kg ],
                      None,
                      "2026-10-21T00:00:00Z"
                  )

              let text = SessionWire.encodeOp op
              Expect.stringContains text "\"unit\":\"kg\"" "the unit"

              match SessionWire.decodeOp text with
              | Ok back -> Expect.equal back op "the same op"
              | Error e -> failtestf "%s" (ModelWire.describe e)

          testCase "a declared join whose key columns state different units is a model defect (DB-JOIN-KEY-UNIT)"
          <| fun _ ->
              let defects (root: ModelNode) =
                  ModelRules.check root |> List.map (fun d -> d.Code, d.Node)

              Expect.equal
                  (defects freight)
                  [ ModelRules.JoinKeyUnit, Some "j:legs-routes" ]
                  "metres related to kilometres, and nothing else"

              let inMetres =
                  freight
                  |> Sample.replace "c:routes.dist_km" (fun _ -> measured "c:routes.dist_km" "dist_km" FloatType metre)

              Expect.isEmpty (defects inMetres) "one unit on both sides"

              let unstated =
                  freight
                  |> Sample.replace "c:routes.dist_km" (fun _ -> plain "c:routes.dist_km" "dist_km" FloatType)

              Expect.isEmpty (defects unstated) "a side stating no unit matches anything"

          testCase "the provider's result table carries each column's unit, and the local step re-checks it"
          <| fun _ ->
              let lower (pipeline: Transform list) =
                  match
                      FoldSplit.lowerUnchecked
                          freight
                          ReferenceDialect.dialect
                          (Ref "shipments")
                          pipeline
                          Bindings.none
                          None
                  with
                  | Ok l -> l
                  | Error r -> failtestf "refused whole: %s" (Refusal.describe r)

              let unitsOf (l: Lowered) =
                  Statement.schema l.Statement
                  |> List.map (fun f -> f.Name, f.Unit |> Option.map Unit.render)

              let summed =
                  lower
                      [ Project [ "weight_kg", "w"; "id", "id" ]
                        GroupBy(
                            [ "id" ],
                            [ { Name = "total"; Fn = Sum; Of = "w" }; { Name = "n"; Fn = Count; Of = "w" } ]
                        ) ]

              Expect.isEmpty summed.Suffix "the whole pipeline lowers"

              Expect.equal
                  (unitsOf summed)
                  [ "id", None; "total", Some(Unit.render kg); "n", None ]
                  "a renamed column keeps its unit; a sum states its operand's, a count none"

              // `Derive` is outside the declared subset, so it runs locally over the statement's result.
              let mixed = lower [ Derive("total", Binary(Add, Col "weight_kg", Col "weight_lb")) ]

              Expect.equal
                  (unitsOf mixed |> List.filter (fun (n, _) -> n.StartsWith "weight"))
                  [ "weight_kg", Some(Unit.render kg); "weight_lb", Some(Unit.render lb) ]
                  "the scanned columns carry the model's units"

              match Statement.toTable mixed.Statement [] with
              | Error e -> failtestf "the empty result was not read: %A" e
              | Ok result ->
                  match FoldSplit.finish mixed (fun n -> Error(UnresolvedSource n)) Bindings.none result with
                  | Error(UnitMismatch _) -> ()
                  | other -> failtestf "the local step admitted kilograms plus pounds: %A" other ]
