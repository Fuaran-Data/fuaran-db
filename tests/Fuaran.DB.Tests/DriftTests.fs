module Fuaran.DB.Tests.DriftTests

open Expecto
open Fuaran.Core
open Fuaran.DB

let private reported: ReportedSchema =
    { Tables =
        [ { Name = "Orders"
            Columns = Sample.fields [ "OrderId", IntType; "Revenue", DecimalType; "Channel", StringType ] }
          { Name = "Customers"
            Columns = Sample.fields [ "CustomerId", IntType; "Email", StringType ] } ] }

let private seeded = Drift.seed "m:seeded" "seeded" "sales" "unclassified" reported

[<Tests>]
let tests =
    testList
        "seeding and drift"
        [ testCase "a seeded model holds every reported table and column, and is clean"
          <| fun _ ->
              Expect.equal
                  (Model.tables seeded |> List.map (fun (t, _) -> Model.physicalSchemaOf t))
                  (reported.Tables |> List.map _.Columns)
                  "the reported schemas, verbatim"

              Expect.isEmpty (ModelRules.check seeded) "no defects"

          testCase "seeding is deterministic"
          <| fun _ -> Expect.equal (Drift.seed "m:seeded" "seeded" "sales" "unclassified" reported) seeded "same ids"

          testCase "a model held against the schema it was seeded from is in step"
          <| fun _ ->
              Expect.equal
                  (Drift.check seeded "sales" reported)
                  (Some [ TableDrift.InStep "Orders"; TableDrift.InStep "Customers" ])
                  "in step"

          testCase "a column renamed at the source is a named drift whose verdict is Schema.diff's"
          <| fun _ ->
              let moved =
                  { Tables =
                      [ { Name = "Orders"
                          Columns =
                            Sample.fields [ "OrderId", IntType; "NetRevenue", DecimalType; "Channel", StringType ] }
                        { Name = "Customers"
                          Columns = Sample.fields [ "CustomerId", IntType; "Email", StringType ] } ] }

              let modelOrders = Model.tables seeded |> List.head |> fst |> Model.physicalSchemaOf

              match Drift.check seeded "sales" moved with
              | Some [ TableDrift.Drifted("Orders", delta); TableDrift.InStep "Customers" ] ->
                  Expect.equal delta (Schema.diff modelOrders moved.Tables.Head.Columns) "Schema.diff's verdict"
                  Expect.equal delta.Removed (Sample.fields [ "Revenue", DecimalType ]) "the model's column is gone"
                  Expect.equal delta.Added (Sample.fields [ "NetRevenue", DecimalType ]) "the source's new column"
                  Expect.isTrue (Drift.hasDrift [ TableDrift.Drifted("Orders", delta) ]) "drift"
              | other -> failtestf "expected Orders drifted, got %A" other

          testCase "a retyped column, a missing table and an unmodelled table are each named"
          <| fun _ ->
              let moved =
                  { Tables =
                      [ { Name = "Orders"
                          Columns = Sample.fields [ "OrderId", IntType; "Revenue", FloatType; "Channel", StringType ] }
                        { Name = "Returns"
                          Columns = Sample.fields [ "ReturnId", IntType ] } ] }

              match Drift.check seeded "sales" moved with
              | Some [ TableDrift.Drifted("Orders", delta)
                       TableDrift.MissingAtSource "Customers"
                       TableDrift.Unmodelled "Returns" ] ->
                  Expect.equal delta.Retyped [ "Revenue", DecimalType, FloatType ] "retyped"
              | other -> failtestf "unexpected verdicts %A" other

          testCase "an unknown source answers None"
          <| fun _ -> Expect.isNone (Drift.check seeded "warehouse" reported) "no such source"

          testCase "unmodelled tables alone are not drift"
          <| fun _ -> Expect.isFalse (Drift.hasDrift [ TableDrift.InStep "a"; TableDrift.Unmodelled "b" ]) "no drift" ]
