/// The SQL Server dialect and the conformance kit (Phase 1901): the dialect's spelling, its refusals
/// reached by the kit's fixtures, the kit agreeing with the reference over the in-memory interpreter
/// through both dialects, the emitted fixture set, and the ARMED leg — the kit against a real engine
/// when one is configured, and a visible Pending when none is.
module Fuaran.DB.Tests.SqlServerTests

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Data
open Microsoft.FSharp.Reflection
open Microsoft.Data.SqlClient
open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider
open Fuaran.DB.Dialect
open Fuaran.DB.Conformance

/// The environment variable that arms the engine-backed leg: a connection string to a SQL Server the
/// suite may create and drop a database on.
let serverVariable = "FUARAN_DB_SQLSERVER"

let repoRoot () =
    Path.GetDirectoryName(CorpusTests.fixturesDir ())

/// The in-memory interpreter of a statement's form, as a host: the seed is held, not loaded.
let memoryHost: Host =
    { Seed = fun _ -> Ok()
      Execute =
        fun statement ->
            ReferenceDialect.execute statement Fixtures.physical
            |> Result.mapError HostFailure.Refused }

let private unionCases<'T> () : 'T list =
    FSharpType.GetUnionCases typeof<'T>
    |> Array.filter (fun c -> c.GetFields().Length = 0)
    |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> 'T)
    |> List.ofArray

/// Every function, operator and aggregate of the algebra, and the excluded constructs.
let constructs: Construct list =
    (unionCases<ScalarFn> () |> List.map Construct.Function)
    @ (unionCases<BinOp> () |> List.map Construct.Operator)
    @ (unionCases<AggFn> () |> List.map Construct.Aggregate)
    @ [ Construct.Cast StringType
        Construct.Rounding RoundingMode.HalfEven
        Construct.EmptyList ]

/// Where the boundary falls for a fixture through a dialect.
let boundaryOf (dialect: Dialect) (f: Fixture) : Refusal option =
    match Policy.admit Fixtures.model Kit.subject (Ref f.Source) f.Pipeline with
    | Error r -> failtestf "%s: the policy refused it: %A" f.Id r
    | Ok admitted ->
        match FoldSplit.explain admitted dialect f.Bindings with
        | Error r -> failtestf "%s: no statement: %A" f.Id r
        | Ok split -> split.Boundary

let textOf (dialect: Dialect) (id: string) : string =
    let f = Fixtures.all |> List.find (fun f -> f.Id = id)

    match Policy.admit Fixtures.model Kit.subject (Ref f.Source) f.Pipeline with
    | Error r -> failtestf "%A" r
    | Ok admitted ->
        match FoldSplit.lower admitted dialect f.Bindings with
        | Error r -> failtestf "%A" r
        | Ok l -> l.Statement.Text

let private emittedDir () =
    Path.Combine(repoRoot (), "conformance")

/// Write the emitted fixture set under `conformance/` (`--write-conformance`).
let writeConformance () =
    let utf8 = UTF8Encoding(false)

    for path, text in Kit.emit () do
        let full = Path.Combine(emittedDir (), path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text + "\n", utf8)
        printfn "wrote %s" full

// ---- the engine-backed host: SqlClient, in the suite only ----


let private sqlType (t: ColumnType) : SqlDbType =
    match t with
    | IntType -> SqlDbType.Int
    | FloatType -> SqlDbType.Float
    | BoolType -> SqlDbType.Bit
    | StringType -> SqlDbType.NVarChar
    | DateType -> SqlDbType.Date
    | TimestampType _ -> SqlDbType.DateTime2
    | DecimalType -> SqlDbType.Decimal

let private addParameter (cmd: SqlCommand) (name: string) (ty: ColumnType) (value: Cell) =
    let p = SqlParameter(name, sqlType ty)

    match ty with
    | StringType -> p.Size <- -1
    | TimestampType _ -> p.Scale <- 0uy
    | DecimalType ->
        p.Precision <- 38uy
        p.Scale <- 10uy
    | IntType
    | FloatType
    | BoolType
    | DateType -> ()

    match SqlServer.parameterValue value with
    | Ok v -> p.Value <- v
    | Error e -> failwith e

    cmd.Parameters.Add p |> ignore

let private nonQuery (conn: SqlConnection) (text: string) =
    use cmd = new SqlCommand(text, conn)
    cmd.ExecuteNonQuery() |> ignore

/// A host over one open connection, in a database the caller made for it.
let sqlHost (conn: SqlConnection) : Host =
    { Seed =
        fun tables ->
            try
                for KeyValue(name, t) in tables do
                    nonQuery conn (SqlServer.dropTable name)

                    match SqlServer.createTable name (t.Schema |> List.map (fun f -> f.Name, f.Type)) with
                    | Ok ddl -> nonQuery conn ddl
                    | Error why -> failwith why

                    let insert = SqlServer.insertRow name (t.Schema |> List.map _.Name)

                    for i in 0 .. Table.rowCount t - 1 do
                        use cmd = new SqlCommand(insert, conn)

                        t.Columns
                        |> List.iteri (fun j c -> addParameter cmd (SqlServer.marker j) c.Type (Column.cell i c))

                        cmd.ExecuteNonQuery() |> ignore

                Ok()
            with e ->
                Error e.Message
      Execute =
        fun statement ->
            try
                use cmd = new SqlCommand(statement.Text, conn)
                cmd.CommandTimeout <- defaultArg statement.TimeoutSeconds 30

                for p in statement.Parameters do
                    addParameter cmd p.Marker p.Type p.Value

                use reader = cmd.ExecuteReader()
                let rows = ResizeArray<Result<Cell list, EvalError>>()

                while reader.Read() do
                    rows.Add(
                        statement.Columns
                        |> List.mapi (fun i o -> SqlServer.decodeCell o.Type (reader.GetValue i))
                        |> List.fold
                            (fun acc c ->
                                match acc, c with
                                | Ok cs, Ok c -> Ok(cs @ [ c ])
                                | Error e, _
                                | _, Error e -> Error e)
                            (Ok [])
                    )

                match
                    rows
                    |> Seq.tryPick (fun r ->
                        match r with
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some e -> Error(HostFailure.Refused e)
                | None ->
                    Ok(
                        rows
                        |> Seq.choose (fun r ->
                            match r with
                            | Ok cs -> Some cs
                            | Error _ -> None)
                        |> List.ofSeq
                    )
            with e ->
                Error(HostFailure.Failed e.Message) }

/// The engine's version and the database's collation.
let private describeEngine (conn: SqlConnection) : string * string =
    use cmd =
        new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))",
            conn
        )

    use r = cmd.ExecuteReader()
    r.Read() |> ignore
    r.GetString 0, r.GetString 1

/// The kit against the configured engine, in a database created for the run and dropped after it.
let runAgainstServer (dialect: Dialect) (connectionString: string) : Report * string * string =
    let database =
        "fuaran_db_conformance_" + Guid.NewGuid().ToString("N").Substring(0, 12)

    use master = new SqlConnection(connectionString)
    master.Open()
    nonQuery master ("CREATE DATABASE " + SqlServer.quote database)

    try
        let builder = SqlConnectionStringBuilder(connectionString)
        builder.InitialCatalog <- database
        builder.Pooling <- false
        use conn = new SqlConnection(builder.ConnectionString)
        conn.Open()
        let version, collation = describeEngine conn

        match Kit.run dialect (sqlHost conn) with
        | Ok report -> report, version, collation
        | Error e -> failtestf "the seed could not be placed: %s" e
    finally
        SqlConnection.ClearAllPools()

        nonQuery
            master
            ("ALTER DATABASE "
             + SqlServer.quote database
             + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE")

        nonQuery master ("DROP DATABASE " + SqlServer.quote database)

[<Tests>]
let tests =
    testList
        "sqlserver dialect and conformance kit"
        [ testCase "identifiers are bracketed with a closing bracket doubled, parameters are named"
          <| fun _ ->
              Expect.equal (SqlServer.quote "ORD_NOTE]X") "[ORD_NOTE]]X]" "closing bracket doubled"
              Expect.equal (SqlServer.quote "a[b") "[a[b]" "an opening bracket needs nothing"
              Expect.equal (SqlServer.marker 3) "@p3" "named marker"
              let text = textOf SqlServer.dialect "project-bracket"
              Expect.stringContains text "[ORD_NOTE]]X]" "the physical identifier, escaped"

          testCase "a boolean column read as a condition is compared, and a condition read as a value is a CASE"
          <| fun _ ->
              Expect.stringContains
                  (textOf SqlServer.dialect "filter-bool-column")
                  "WHERE ([c7] = 1)"
                  "bit as condition"

              Expect.stringContains
                  (textOf SqlServer.dialect "filter-condition-as-value")
                  "(CASE WHEN ([c3] > @p0) THEN CAST(1 AS bit) WHEN NOT ([c3] > @p0) THEN CAST(0 AS bit) END)"
                  "condition as value"

              Expect.isTrue ((textOf ReferenceDialect.dialect "filter-bool-column").EndsWith "WHERE \"c7\"") "identity"

          testCase "a page orders nulls last and fetches by parameter, and a page of no rows keeps none"
          <| fun _ ->
              let text = textOf SqlServer.dialect "page-id"
              Expect.stringContains text "ORDER BY CASE WHEN [c1] IS NULL THEN 1 ELSE 0 END, [c1] ASC" "nulls last"
              Expect.stringContains text "OFFSET @p1 ROWS FETCH NEXT (SELECT CASE WHEN @p0 > 0" "offset and fetch"
              Expect.stringContains text "WHERE @p0 > 0 ORDER BY" "no rows when the count is zero"

          testCase "an int sum is widened to bigint, a mean is a float, a bit minimum goes through tinyint"
          <| fun _ ->
              let text = textOf SqlServer.dialect "group-by-channel"
              Expect.stringContains text "SUM(CAST([c3] AS bigint))" "int sum widened"
              Expect.stringContains text "SUM([c5])" "decimal sum as is"
              Expect.stringContains text "AVG(CAST([c3] AS float))" "mean"

              Expect.stringContains
                  (textOf SqlServer.dialect "group-min-max-bool")
                  "CAST(MIN(CAST([c7] AS tinyint)) AS bit)"
                  "bit min"

          testCase "a timestamp column's DDL keeps its unit's precision, and a nanosecond one is refused by name"
          <| fun _ ->
              for unit, spelled in
                  [ TimeUnit.Seconds, "datetime2(0)"
                    TimeUnit.Milliseconds, "datetime2(3)"
                    TimeUnit.Microseconds, "datetime2(6)" ] do
                  let ty = TimestampType unit

                  match SqlServer.createTable "t" [ "at", ty ] with
                  | Ok ddl -> Expect.stringContains ddl (" " + spelled + " NULL") (sprintf "%A" unit)
                  | Error why -> failtestf "%A: refused: %s" unit why

                  Expect.isNone (SqlServer.typeRefusal ty) (sprintf "%A is held" unit)
                  Expect.isNone (SqlServer.refuses (Construct.Parameter ty)) (sprintf "a %A parameter lowers" unit)

              let ns = TimestampType TimeUnit.Nanoseconds

              match SqlServer.createTable "t" [ "id", IntType; "at", ns ] with
              | Ok ddl -> failtestf "a nanosecond column was created: %s" ddl
              | Error why ->
                  Expect.stringContains why "'at'" "the refusal names the column"
                  Expect.stringContains why "nanosecond" "and why"

              Expect.isSome (SqlServer.typeRefusal ns) "the type map refuses it"
              Expect.isSome (SqlServer.refuses (Construct.Parameter ns)) "a nanosecond parameter is refused"

          testCase "the type map: every column type to an engine type and back"
          <| fun _ ->
              for t in
                  [ IntType
                    FloatType
                    BoolType
                    StringType
                    DateType
                    (TimestampType TimeUnit.Seconds)
                    DecimalType ] do
                  Expect.equal (SqlServer.columnType (SqlServer.mapType t)) (Some t) (ColumnType.tag t)

              Expect.equal (SqlServer.mapType DecimalType) "decimal(38, 10)" "the exact decimal"
              Expect.equal (SqlServer.columnType "NUMERIC(18,2)") (Some DecimalType) "numeric reads as decimal"
              Expect.equal (SqlServer.columnType "bigint") None "bigint has no column"

          testCase "a result cell decodes into its column type, and a widened sum past int32 is the reference's refusal"
          <| fun _ ->
              Expect.equal (SqlServer.decodeCell IntType (box 7L)) (Ok(Int 7)) "in range"

              Expect.equal
                  (SqlServer.decodeCell IntType (box 2500000005L))
                  (Error(OverflowError "sum overflowed int32: 2500000005"))
                  "past int32"

              Expect.equal (SqlServer.decodeCell DecimalType (box 12.5000000000m)) (Ok(Decimal "12.5")) "canonical"
              Expect.equal (SqlServer.decodeCell DateType (box (DateTime(2026, 6, 2)))) (Ok(Date "2026-06-02")) "date"

              Expect.equal
                  (SqlServer.decodeCell (TimestampType TimeUnit.Seconds) (box (DateTime(2026, 6, 2, 9, 30, 0))))
                  (Ok(Timestamp "2026-06-02T09:30:00Z"))
                  "timestamp"

              Expect.equal (SqlServer.decodeCell BoolType (box DBNull.Value)) (Ok Null) "null"
              Expect.isError (SqlServer.decodeCell IntType (box "seven")) "a shape the type does not admit"

          testCase "the reference's own sum overflow is what the engine-backed answer decodes to"
          <| fun _ ->
              let f = Fixtures.all |> List.find (fun f -> f.Id = "sum-int-overflow")
              Expect.equal (Kit.expected f) (Error(OverflowError "sum overflowed int32: 2500000005")) "pinned wording"

          testCase "every refusal the dialect declares is named, and a kit fixture reaches each at lowering"
          <| fun _ ->
              let reached =
                  Fixtures.all
                  |> List.choose (fun f ->
                      match boundaryOf SqlServer.dialect f with
                      | Some(Refusal.DialectRefuses(_, c, _))
                      | Some(Refusal.ConstructExcluded(_, c)) -> Some c
                      | _ -> None)
                  |> Set.ofList

              let refused = constructs |> List.filter (fun c -> (SqlServer.refuses c).IsSome)
              Expect.isNonEmpty refused "the dialect refuses something"

              for c in refused do
                  Expect.isTrue (Set.contains c reached) (sprintf "no fixture reaches the refused %A" c)

          testCase "every fixture that names a construct to reach reaches it through the SQL Server dialect"
          <| fun _ ->
              for f in Fixtures.all do
                  match f.Reaches with
                  | None -> ()
                  | Some c ->
                      match boundaryOf SqlServer.dialect f with
                      | Some(Refusal.DialectRefuses(_, r, _))
                      | Some(Refusal.ConstructExcluded(_, r)) when r = c -> ()
                      | other -> failtestf "%s: expected the boundary at %A, got %A" f.Id c other

          testCase "the subset lowers: no fixture of the subset stops at a dialect refusal"
          <| fun _ ->
              for f in Fixtures.subset do
                  match boundaryOf SqlServer.dialect f with
                  | Some(Refusal.DialectRefuses _ as r) -> failtestf "%s stopped at %A" f.Id r
                  | _ -> ()

          testCase "the kit agrees with the reference over the in-memory interpreter, through the reference dialect"
          <| fun _ ->
              match Kit.run ReferenceDialect.dialect memoryHost with
              | Error e -> failtest e
              | Ok report ->
                  Kit.describe report
                  |> List.iter (printfn "conformance (reference, in memory): %s")

                  Expect.equal (Kit.counts report) (List.length Fixtures.all, 0, 0) "every fixture agreed"

          testCase "the kit agrees with the reference over the in-memory interpreter, through the SQL Server dialect"
          <| fun _ ->
              match Kit.run SqlServer.dialect memoryHost with
              | Error e -> failtest e
              | Ok report ->
                  Kit.describe report
                  |> List.iter (printfn "conformance (sqlserver, in memory): %s")

                  Expect.equal (Kit.counts report) (List.length Fixtures.all, 0, 0) "every fixture agreed"

          testCase "a pin is checkable: a case-folded answer is predicted, another wrong answer is unexpected"
          <| fun _ ->
              let f = Fixtures.all |> List.find (fun f -> f.Id = "difference-collation-case")

              let folding: Host =
                  { memoryHost with
                      Execute =
                          fun s ->
                              let folded =
                                  Fixtures.physical
                                  |> Map.map (fun _ t ->
                                      { t with
                                          Columns =
                                              t.Columns
                                              |> List.map (fun c ->
                                                  Sample.column
                                                      c.Name
                                                      c.Type
                                                      ((Column.toCells c)
                                                       |> List.map (fun x ->
                                                           match x with
                                                           | Str v -> Str(v.ToLowerInvariant())
                                                           | other -> other))) })

                              ReferenceDialect.execute s folded |> Result.mapError HostFailure.Refused }

              match Kit.runWith [ f ] SqlServer.dialect folding with
              | Ok r ->
                  Expect.equal
                      (r.Fixtures |> List.map _.Verdict)
                      [ Verdict.DifferingAsPinned Pin.CaseInsensitiveCollation ]
                      "a case-insensitive engine"
              | Error e -> failtest e

              let wrong: Host =
                  { memoryHost with
                      Execute = fun _ -> Ok [ [ Int 42 ] ] }

              match Kit.runWith [ f ] SqlServer.dialect wrong with
              | Ok r ->
                  match r.Fixtures |> List.map _.Verdict with
                  | [ Verdict.DifferingUnexpectedly _ ] -> ()
                  | other -> failtestf "expected an unexpected difference, got %A" other
              | Error e -> failtest e

          testCase "the float summation pin predicts every order of the source rows, and the reference's is among them"
          <| fun _ ->
              let f =
                  Fixtures.all |> List.find (fun f -> f.Id = "difference-float-summation-order")

              let predicted = Kit.predicted Pin.FloatSummationOrder f
              Expect.equal (List.length predicted) 24 "four rows, every order"
              Expect.isTrue (predicted |> List.exists (Kit.agrees false (Kit.expected f))) "the reference's own order"

              Expect.isGreaterThan
                  (predicted |> List.distinct |> List.length)
                  1
                  "the fixture's sum does depend on the order"

          testCase "the emitted fixture set under conformance/ is what the kit emits, stamped with the kit version"
          <| fun _ ->
              let utf8 = UTF8Encoding(false)
              let emitted = Kit.emit ()

              for path, text in emitted do
                  let full = Path.Combine(emittedDir (), path)

                  Expect.isTrue
                      (File.Exists full)
                      (sprintf "%s is missing: run the suite with --write-conformance" path)

                  Expect.equal (File.ReadAllText(full, utf8)) (text + "\n") (sprintf "%s is stale" path)
                  Expect.stringContains text (sprintf "\"kit\":\"%s\"" Kit.version) "stamped"

              let onDisk =
                  Directory.GetFiles(Path.Combine(emittedDir (), "fixtures"))
                  |> Array.map Path.GetFileName
                  |> Set.ofArray

              Expect.equal
                  onDisk
                  (emitted
                   |> List.choose (fun (p, _) ->
                       if p.StartsWith "fixtures/" then
                           Some(p.Substring 9)
                       else
                           None)
                   |> Set.ofList)
                  "no fixture file the kit no longer emits"

          testCase "the dialect and the kit reference no database driver"
          <| fun _ ->
              for project in
                  [ "Fuaran.DB.Dialect.SqlServer"
                    "Fuaran.DB.Dialect.PostgreSql"
                    "Fuaran.DB.Conformance" ] do
                  let text =
                      File.ReadAllText(Path.Combine(repoRoot (), "src", project, project + ".fsproj"))

                  let references =
                      Regex.Matches(
                          text,
                          @"<(PackageReference|ProjectReference)\s[^>]*(Include|Update)\s*=\s*""([^""]+)"""
                      )
                      |> Seq.map (fun m ->
                          let target = m.Groups[3].Value

                          if m.Groups[1].Value = "ProjectReference" then
                              Path.GetFileNameWithoutExtension(target.Replace('\\', '/'))
                          else
                              target)
                      |> List.ofSeq

                  Expect.equal (references |> List.sort) [ "FSharp.Core"; "Fuaran.DB.Provider" ] project

          // THE ARMED LEG. With a server configured it runs the kit against it and fails on any
          // unexpected difference; with none it is Pending — reported as ignored, never failed — and the
          // run's output says which it was.
          (match Environment.GetEnvironmentVariable serverVariable with
           | null
           | "" ->
               printfn
                   "conformance leg (sqlserver): PENDING — no server configured; set %s to a connection string to arm it"
                   serverVariable

               ptestCase (sprintf "PENDING: the kit against a real SQL Server (set %s to arm it)" serverVariable) ignore
           | connectionString ->
               testList
                   "ARMED"
                   [ testCase "the kit against the configured SQL Server agrees, or differs only as pinned"
                     <| fun _ ->
                         let report, version, collation = runAgainstServer SqlServer.dialect connectionString
                         let a, p, u = Kit.counts report

                         printfn "conformance leg (sqlserver): ARMED — engine %s, collation %s" version collation
                         Kit.describe report |> List.iter (printfn "conformance (sqlserver, engine): %s")

                         printfn
                             "conformance leg (sqlserver): %d agreed, %d differing as pinned, %d differing unexpectedly"
                             a
                             p
                             u

                         Expect.equal u 0 "no fixture differs unexpectedly"

                     // The falsifier: a dialect that reads a bit as a condition is caught by the engine.
                     testCase "the kit goes red over the engine for a dialect that reads a bit as a condition"
                     <| fun _ ->
                         let broken =
                             { SqlServer.dialect with
                                 Name = "sqlserver-broken"
                                 ValueAsCondition = id }

                         let report, _, _ = runAgainstServer broken connectionString
                         let _, _, u = Kit.counts report
                         Expect.isGreaterThan u 0 "the engine refuses a bit where a condition is required" ]) ]
