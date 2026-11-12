/// The PostgreSQL dialect (Phase 1906): its spelling, its refusals reached by the kit's fixtures, the kit
/// agreeing with the reference over the in-memory interpreter through it, the pin it adds, the source
/// reading that holds the provider's lowering core free of any dialect, and the ARMED leg — the kit against
/// a real PostgreSQL when one is configured, and a visible Pending when none is.
module Fuaran.DB.Tests.PostgreSqlTests

open System
open System.IO
open System.Text.RegularExpressions
open Npgsql
open NpgsqlTypes
open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider
open Fuaran.DB.Dialect
open Fuaran.DB.Conformance

/// The environment variable that arms the engine-backed leg: a connection string to a PostgreSQL server
/// the suite may create and drop a database on.
let serverVariable = "FUARAN_DB_POSTGRESQL"

/// The constructs the PostgreSQL dialect lowers that the SQL Server dialect refuses: the kit's fixture
/// that reaches each lowers it through PostgreSQL instead.
let private mappedHereRefusedThere: Construct list =
    [ Construct.Function Replace
      Construct.Operator Contains
      Construct.Operator StartsWith
      Construct.Operator EndsWith ]

// ---- the provider's lowering core names no dialect ----

/// The provider's lowering core: every file that writes statement text or decides what lowers.
let private coreFiles =
    [ "Statement.fs"; "Dialect.fs"; "Lowering.fs"; "FoldSplit.fs"; "Probe.fs" ]

/// What only a dialect may write: the dialects' names, and the spellings in which engines differ —
/// identifier quotes, parameter marker prefixes, paging, collation, and engine type names.
let private dialectOnly =
    [ "SqlServer"
      "sqlserver"
      "PostgreSql"
      "postgres"
      "T-SQL"
      "["
      "]"
      "`"
      "\\\""
      "@"
      "$"
      "?"
      "LIMIT"
      "OFFSET"
      "FETCH"
      "TOP "
      "NULLS"
      "COLLATE"
      "CAST"
      "bit"
      "nvarchar"
      "bigint"
      "integer"
      "boolean"
      "numeric"
      "double precision" ]

/// The string literals of F# source, comments removed: what the code can write into a statement.
let literalsOf (source: string) : string list =
    let code =
        source.Split('\n')
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
        |> String.concat "\n"

    Regex.Matches(code, "\"((?:[^\"\\\\\\n]|\\\\.)*)\"")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

/// Every literal of a source that holds a dialect-only spelling, with the spelling.
let dialectSpellingsIn (source: string) : (string * string) list =
    literalsOf source
    |> List.collect (fun literal ->
        dialectOnly
        |> List.filter (fun token -> literal.Contains(token: string))
        |> List.map (fun token -> literal, token))

// ---- the engine-backed host: Npgsql, in the suite only ----

let private dbType (t: ColumnType) : NpgsqlDbType =
    match t with
    | IntType -> NpgsqlDbType.Integer
    | FloatType -> NpgsqlDbType.Double
    | BoolType -> NpgsqlDbType.Boolean
    | StringType -> NpgsqlDbType.Text
    | DateType -> NpgsqlDbType.Date
    | TimestampType _ -> NpgsqlDbType.Timestamp
    | DecimalType -> NpgsqlDbType.Numeric

/// A positional parameter: `$n` is the n-th parameter added.
let private addParameter (cmd: NpgsqlCommand) (ty: ColumnType) (value: Cell) =
    let p = NpgsqlParameter()
    p.NpgsqlDbType <- dbType ty

    match PostgreSql.parameterValue value with
    | Ok v -> p.Value <- v
    | Error e -> failwith e

    cmd.Parameters.Add p |> ignore

let private nonQuery (conn: NpgsqlConnection) (text: string) =
    use cmd = new NpgsqlCommand(text, conn)
    cmd.ExecuteNonQuery() |> ignore

/// A host over one open connection, in a database the caller made for it.
let pgHost (conn: NpgsqlConnection) : Host =
    { Seed =
        fun tables ->
            try
                for KeyValue(name, t) in tables do
                    nonQuery conn (PostgreSql.dropTable name)

                    match PostgreSql.createTable name (t.Schema |> List.map (fun f -> f.Name, f.Type)) with
                    | Ok ddl -> nonQuery conn ddl
                    | Error why -> failwith why

                    let insert = PostgreSql.insertRow name (t.Schema |> List.map _.Name)

                    for i in 0 .. Table.rowCount t - 1 do
                        use cmd = new NpgsqlCommand(insert, conn)
                        t.Columns |> List.iter (fun c -> addParameter cmd c.Type (Column.cell i c))
                        cmd.ExecuteNonQuery() |> ignore

                Ok()
            with e ->
                Error e.Message
      Execute =
        fun statement ->
            try
                use cmd = new NpgsqlCommand(statement.Text, conn)
                cmd.CommandTimeout <- defaultArg statement.TimeoutSeconds 30

                for p in statement.Parameters do
                    addParameter cmd p.Type p.Value

                use reader = cmd.ExecuteReader()
                let rows = ResizeArray<Result<Cell list, EvalError>>()

                while reader.Read() do
                    rows.Add(
                        statement.Columns
                        |> List.mapi (fun i o -> PostgreSql.decodeCell o.Type (reader.GetValue i))
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

/// The engine's version and the database's collation and its provider.
let private describeEngine (conn: NpgsqlConnection) : string * string =
    use cmd =
        new NpgsqlCommand(
            // `datlocprovider` is a `"char"`, which `||` cannot join to text without a cast; under the ICU
            // provider the locale is `datlocale` (PostgreSQL 17), and `datcollate` is only the libc fallback.
            "SELECT version(), coalesce(datlocale, datcollate) || ' (' || datlocprovider::text || '), encoding ' || pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database()",
            conn
        )

    use r = cmd.ExecuteReader()
    r.Read() |> ignore
    r.GetString 0, r.GetString 1

/// The kit against the configured engine, in a database created for the run and dropped after it.
/// `linguistic` creates the database under the ICU root locale, whose order is not the reference's, so a
/// dialect that does not re-declare the collation it reads strings under is caught.
let runAgainstServer (dialect: Dialect) (linguistic: bool) (connectionString: string) : Report * string * string =
    let database =
        "fuaran_db_conformance_" + Guid.NewGuid().ToString("N").Substring(0, 12)

    use admin = new NpgsqlConnection(connectionString)
    admin.Open()

    nonQuery
        admin
        ("CREATE DATABASE "
         + PostgreSql.quote database
         + " TEMPLATE template0 ENCODING 'UTF8'"
         + (if linguistic then
                " LOCALE_PROVIDER icu ICU_LOCALE 'und'"
            else
                ""))

    try
        let builder = NpgsqlConnectionStringBuilder(connectionString)
        builder.Database <- database
        builder.Pooling <- false
        use conn = new NpgsqlConnection(builder.ConnectionString)
        conn.Open()
        let version, collation = describeEngine conn

        match Kit.run dialect (pgHost conn) with
        | Ok report -> report, version, collation
        | Error e -> failtestf "the seed could not be placed: %s" e
    finally
        NpgsqlConnection.ClearAllPools()
        nonQuery admin ("DROP DATABASE " + PostgreSql.quote database + " WITH (FORCE)")

/// One instant per unit the engine holds, as ticks past 2026-06-02T09:30:15 and its canonical text.
let private timestampsByUnit =
    [ TimeUnit.Seconds, 0L, "2026-06-02T09:30:15Z"
      TimeUnit.Milliseconds, 1_230_000L, "2026-06-02T09:30:15.123Z"
      TimeUnit.Microseconds, 1_234_560L, "2026-06-02T09:30:15.123456Z" ]

/// Per unit, in a database made for the run: a column created at the unit reports the unit back through
/// `format_type`, and an instant bound at the unit reads back whole.
let timestampRoundTrip (connectionString: string) =
    let database = "fuaran_db_units_" + Guid.NewGuid().ToString("N").Substring(0, 12)

    use admin = new NpgsqlConnection(connectionString)
    admin.Open()

    nonQuery
        admin
        ("CREATE DATABASE "
         + PostgreSql.quote database
         + " TEMPLATE template0 ENCODING 'UTF8'")

    try
        let builder = NpgsqlConnectionStringBuilder(connectionString)
        builder.Database <- database
        builder.Pooling <- false
        use conn = new NpgsqlConnection(builder.ConnectionString)
        conn.Open()

        for unit, _, text in timestampsByUnit do
            let ty = TimestampType unit
            let table = "at_" + ColumnType.tag ty

            match PostgreSql.createTable table [ "at", ty ] with
            | Ok ddl -> nonQuery conn ddl
            | Error why -> failtestf "%A: refused: %s" unit why

            use insert = new NpgsqlCommand(PostgreSql.insertRow table [ "at" ], conn)
            addParameter insert ty (Timestamp text)
            insert.ExecuteNonQuery() |> ignore

            use reported =
                new NpgsqlCommand(
                    "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = $1::regclass AND attname = 'at'",
                    conn
                )

            reported.Parameters.Add(NpgsqlParameter(Value = PostgreSql.quote table))
            |> ignore

            let engineType = reported.ExecuteScalar() :?> string
            Expect.equal (PostgreSql.columnType engineType) (Some ty) (sprintf "%s reports %A" engineType unit)

            use select =
                new NpgsqlCommand("SELECT " + PostgreSql.quote "at" + " FROM " + PostgreSql.quote table, conn)

            Expect.equal
                (PostgreSql.decodeCell ty (select.ExecuteScalar()))
                (Ok(Timestamp text))
                (sprintf "%A reads back whole" unit)
    finally
        NpgsqlConnection.ClearAllPools()
        nonQuery admin ("DROP DATABASE " + PostgreSql.quote database + " WITH (FORCE)")

/// One line per fixture: its verdict, how many planned steps the engine ran, and where the boundary fell.
/// The record a certification cites, so every fixture is named, not only those that differed.
let fixtureLines (report: Report) : string list =
    report.Fixtures
    |> List.map (fun r ->
        let verdict =
            match r.Verdict with
            | Verdict.Agreed -> "agreed"
            | Verdict.DifferingAsPinned pin -> sprintf "differing as pinned (%A)" pin
            | Verdict.DifferingUnexpectedly detail -> sprintf "DIFFERING UNEXPECTEDLY: %s" detail

        let boundary =
            match r.Boundary with
            | None -> "every step lowered"
            | Some b -> sprintf "boundary %A" b

        sprintf "%s: %s; %d step(s) on the engine, %s" r.Fixture verdict r.Lowered boundary)

/// The fixtures a report shows differing as the case-insensitive pin predicts.
let private caseInsensitive (report: Report) : string list =
    report.Fixtures
    |> List.choose (fun r ->
        match r.Verdict with
        | Verdict.DifferingAsPinned Pin.CaseInsensitiveCollation -> Some r.Fixture
        | _ -> None)

/// The in-memory interpreter with every string of the seed and the statement's parameters re-encoded
/// by `f`: an engine that compares strings by another order.
let private reencodingHost (f: string -> string) : Host =
    let cell (c: Cell) =
        match c with
        | Str s -> Str(f s)
        | other -> other

    let tables =
        Fixtures.physical
        |> Map.map (fun _ t ->
            { t with
                Columns =
                    t.Columns
                    |> List.map (fun c -> Sample.column c.Name c.Type ((Column.toCells c) |> List.map cell)) })

    { SqlServerTests.memoryHost with
        Execute =
            fun s ->
                let s' =
                    { s with
                        Parameters = s.Parameters |> List.map (fun p -> { p with Value = cell p.Value }) }

                ReferenceDialect.execute s' tables |> Result.mapError HostFailure.Refused }

[<Tests>]
let tests =
    let text = SqlServerTests.textOf PostgreSql.dialect

    testList
        "postgresql dialect"
        [ testCase "identifiers are double-quoted with a quote doubled, parameters are positional from $1"
          <| fun _ ->
              Expect.equal (PostgreSql.quote "a\"b") "\"a\"\"b\"" "quote doubled"
              Expect.equal (PostgreSql.quote "ORD_NOTE]X") "\"ORD_NOTE]X\"" "a bracket needs nothing"
              Expect.equal (PostgreSql.marker 0) "$1" "positional, from one"
              Expect.equal (PostgreSql.marker 3) "$4" "ordinal three"
              Expect.stringContains (text "project-bracket") "\"ORD_NOTE]X\"" "the physical identifier"

          testCase "a string is read under the C collation, a column at its scan and a parameter at its marker"
          <| fun _ ->
              let t = text "filter-param"
              Expect.stringContains t "(\"ORD_CHANNEL\" COLLATE \"C\") AS " "the scanned string column"
              Expect.stringContains t "($1 COLLATE \"C\")" "the string parameter"
              Expect.stringContains t "\"ORD_ID\" AS " "an int column as it is"
              Expect.isFalse (t.Contains "\"ORD_ID\" COLLATE") "no collation on an int"
              Expect.stringContains (text "filter-compare-int") "> $1)" "an int parameter as it is"

          testCase "a page orders nulls last natively and pages by LIMIT and OFFSET"
          <| fun _ ->
              Expect.stringContains (text "page-id") "ORDER BY \"c1\" ASC NULLS LAST LIMIT $1 OFFSET $2" "ascending"

              Expect.stringContains
                  (text "page-price-desc")
                  "ORDER BY \"c4\" DESC NULLS LAST LIMIT $1 OFFSET $2"
                  "descending"

          testCase "booleans are conditions as they are, and aggregates are spelled for PostgreSQL's types"
          <| fun _ ->
              Expect.isTrue ((text "filter-bool-column").EndsWith "WHERE \"c7\"") "a boolean column is a condition"

              Expect.stringContains
                  (text "filter-condition-as-value")
                  "((\"c3\" > $1) = \"c7\")"
                  "a condition is a value"

              let g = text "group-by-channel"
              Expect.stringContains g "count(\"c1\")" "count"
              Expect.stringContains g "sum(\"c3\")" "an int sum, a bigint in the engine"
              Expect.stringContains g "avg(CAST(\"c3\" AS double precision))" "mean"
              let b = text "group-min-max-bool"
              Expect.stringContains b "bool_and(\"c7\")" "boolean minimum"
              Expect.stringContains b "bool_or(\"c7\")" "boolean maximum"

          testCase "rounding is spelled as the reference rounds, and the reference rounds that way"
          <| fun _ ->
              Expect.stringContains
                  (text "filter-round")
                  "(sign(\"c4\") * floor(abs(\"c4\") + 0.5))"
                  "sign times the floor of the magnitude plus a half"

              // The spelling's premise: the reference rounds a float as sign(x) * floor(|x| + 0.5) in
              // float arithmetic, which is not .NET's own away-from-zero rounding at 0.49999999999999994.
              for x in
                  [ 0.49999999999999994
                    2.4999999999999996
                    -0.49999999999999994
                    2.5
                    -2.5
                    4503599627370497.0 ] do
                  let spelled = float (sign x) * floor (abs x + 0.5)

                  Expect.equal
                      (DataFrame.evalExprInRow
                          Map.empty
                          (Sample.fields [ "x", FloatType ])
                          [ Float x ]
                          (ApplyFn(Round, [ Col "x" ])))
                      (Ok(Float spelled))
                      (x.ToString "R")

          testCase "a timestamp column's DDL keeps its unit's precision, and a nanosecond one is refused by name"
          <| fun _ ->
              for unit, spelled in
                  [ TimeUnit.Seconds, "timestamp(0) without time zone"
                    TimeUnit.Milliseconds, "timestamp(3) without time zone"
                    TimeUnit.Microseconds, "timestamp(6) without time zone" ] do
                  let ty = TimestampType unit

                  match PostgreSql.createTable "t" [ "at", ty ] with
                  | Ok ddl -> Expect.stringContains ddl (" " + spelled + " NULL") (sprintf "%A" unit)
                  | Error why -> failtestf "%A: refused: %s" unit why

                  Expect.isNone (PostgreSql.typeRefusal ty) (sprintf "%A is held" unit)
                  Expect.isNone (PostgreSql.refuses (Construct.Parameter ty)) (sprintf "a %A parameter lowers" unit)

              let ns = TimestampType TimeUnit.Nanoseconds

              match PostgreSql.createTable "t" [ "id", IntType; "at", ns ] with
              | Ok ddl -> failtestf "a nanosecond column was created: %s" ddl
              | Error why ->
                  Expect.stringContains why "'at'" "the refusal names the column"
                  Expect.stringContains why "nanosecond" "and why"

              Expect.isSome (PostgreSql.typeRefusal ns) "the type map refuses it"
              Expect.isSome (PostgreSql.refuses (Construct.Parameter ns)) "a nanosecond parameter is refused"

          testCase "a timestamp keeps its unit: read from the engine type, bound and decoded at the unit's fraction"
          <| fun _ ->
              let at = DateTime(2026, 6, 2, 9, 30, 15)

              for unit, ticks, text in timestampsByUnit do
                  let ty = TimestampType unit
                  let value = at.AddTicks ticks

                  Expect.equal
                      (PostgreSql.columnType (PostgreSql.mapType ty))
                      (Some ty)
                      (sprintf "%s reads as %A" (PostgreSql.mapType ty) unit)

                  Expect.equal
                      (PostgreSql.decodeCell ty (box value))
                      (Ok(Timestamp text))
                      (sprintf "%A decodes whole" unit)

                  match PostgreSql.parameterValue (Timestamp text) with
                  | Ok(:? DateTime as d) ->
                      Expect.equal d value (sprintf "%A binds whole" unit)
                      Expect.equal d.Kind DateTimeKind.Unspecified "a timestamp without time zone"
                  | other -> failtestf "%A: expected a DateTime, got %A" unit other

              Expect.equal
                  (PostgreSql.decodeCell (TimestampType TimeUnit.Milliseconds) (box (at.AddTicks 1_200_000L)))
                  (Ok(Timestamp "2026-06-02T09:30:15.12Z"))
                  "the minimal text: no trailing zero"

              Expect.equal
                  (PostgreSql.columnType "timestamp without time zone")
                  (Some(TimestampType TimeUnit.Microseconds))
                  "a bare timestamp is six digits"

              Expect.equal
                  (PostgreSql.columnType "timestamp(2)")
                  (Some(TimestampType TimeUnit.Milliseconds))
                  "two digits need milliseconds"

              match PostgreSql.decodeCell (TimestampType TimeUnit.Seconds) (box (at.AddTicks 5_000_000L)) with
              | Error(TypeError why) -> Expect.stringContains why "finer than the column's timestamp" "named"
              | other -> failtestf "a half second was not refused at seconds: %A" other

              Expect.isError
                  (PostgreSql.parameterValue (Timestamp "2026-06-02T09:30:15.1234567Z"))
                  "finer than a microsecond"

          testCase "the type map: every column type to an engine type and back"
          <| fun _ ->
              for t in
                  [ IntType
                    FloatType
                    BoolType
                    StringType
                    DateType
                    (TimestampType TimeUnit.Seconds)
                    (TimestampType TimeUnit.Milliseconds)
                    (TimestampType TimeUnit.Microseconds)
                    DecimalType ] do
                  Expect.equal (PostgreSql.columnType (PostgreSql.mapType t)) (Some t) (ColumnType.tag t)

              Expect.equal (PostgreSql.columnType "character varying(20)") (Some StringType) "varchar"
              Expect.equal (PostgreSql.columnType "NUMERIC(18,2)") (Some DecimalType) "numeric"

              Expect.equal
                  (PostgreSql.columnType "timestamp(3) without time zone")
                  (Some(TimestampType TimeUnit.Milliseconds))
                  "timestamp"

              Expect.equal (PostgreSql.columnType "bigint") None "bigint has no column"
              Expect.equal (PostgreSql.columnType "timestamp with time zone") None "an offset"
              Expect.equal (PostgreSql.columnType "timestamptz") None "an offset, abbreviated"
              Expect.equal (PostgreSql.columnType "character(3)") None "padded"

          testCase "a parameter binds as the driver types it, and a result cell decodes into its column type"
          <| fun _ ->
              Expect.equal (PostgreSql.parameterValue (Date "2026-06-02")) (Ok(box (DateOnly(2026, 6, 2)))) "date"

              match PostgreSql.parameterValue (Timestamp "2026-06-02T09:30:00Z") with
              | Ok(:? DateTime as d) ->
                  Expect.equal d.Kind DateTimeKind.Unspecified "a timestamp without time zone"
                  Expect.equal d (DateTime(2026, 6, 2, 9, 30, 0)) "the instant"
              | other -> failtestf "expected a DateTime, got %A" other

              Expect.equal (PostgreSql.decodeCell IntType (box 7L)) (Ok(Int 7)) "a bigint count in range"

              Expect.equal
                  (PostgreSql.decodeCell IntType (box 2500000005L))
                  (Error(OverflowError "sum overflowed int32: 2500000005"))
                  "past int32, the reference's refusal"

              Expect.equal (PostgreSql.decodeCell DecimalType (box 12.5000m)) (Ok(Decimal "12.5")) "canonical"
              Expect.equal (PostgreSql.decodeCell DateType (box (DateOnly(2026, 6, 2)))) (Ok(Date "2026-06-02")) "date"
              Expect.equal (PostgreSql.decodeCell DateType (box (DateTime(2026, 6, 2)))) (Ok(Date "2026-06-02")) "date"

              Expect.equal
                  (PostgreSql.decodeCell (TimestampType TimeUnit.Seconds) (box (DateTime(2026, 6, 2, 9, 30, 0))))
                  (Ok(Timestamp "2026-06-02T09:30:00Z"))
                  "timestamp"

              Expect.equal (PostgreSql.decodeCell BoolType (box DBNull.Value)) (Ok Null) "null"
              Expect.isError (PostgreSql.decodeCell IntType (box "seven")) "a shape the type does not admit"

          testCase "every refusal the dialect declares is named, and a kit fixture reaches each at lowering"
          <| fun _ ->
              let reached =
                  Fixtures.all
                  |> List.choose (fun f ->
                      match SqlServerTests.boundaryOf PostgreSql.dialect f with
                      | Some(Refusal.DialectRefuses(_, c, _))
                      | Some(Refusal.ConstructExcluded(_, c)) -> Some c
                      | _ -> None)
                  |> Set.ofList

              let refused =
                  SqlServerTests.constructs
                  |> List.filter (fun c -> (PostgreSql.refuses c).IsSome)

              Expect.isNonEmpty refused "the dialect refuses something"

              for c in refused do
                  Expect.isTrue (Set.contains c reached) (sprintf "no fixture reaches the refused %A" c)

          testCase "every fixture naming a construct reaches it through PostgreSQL, or PostgreSQL lowers it"
          <| fun _ ->
              for f in Fixtures.all do
                  match f.Reaches with
                  | None -> ()
                  | Some c when List.contains c mappedHereRefusedThere ->
                      Expect.isNone (PostgreSql.refuses c) (sprintf "%s: %A is lowered here" f.Id c)

                      Expect.isSome
                          (SqlServer.refuses c)
                          (sprintf "%s: %A is a construct the two dialects part on" f.Id c)

                      match SqlServerTests.boundaryOf PostgreSql.dialect f with
                      | Some(Refusal.DialectRefuses _ as r)
                      | Some(Refusal.ConstructExcluded _ as r) -> failtestf "%s stopped at %A" f.Id r
                      | _ -> ()
                  | Some c ->
                      match SqlServerTests.boundaryOf PostgreSql.dialect f with
                      | Some(Refusal.DialectRefuses(_, r, _))
                      | Some(Refusal.ConstructExcluded(_, r)) when r = c -> ()
                      | other -> failtestf "%s: expected the boundary at %A, got %A" f.Id c other

          testCase "the subset lowers: no fixture of the subset or the derives stops at a dialect refusal"
          <| fun _ ->
              for f in Fixtures.subset @ Fixtures.derives do
                  match SqlServerTests.boundaryOf PostgreSql.dialect f with
                  | Some(Refusal.DialectRefuses _ as r) -> failtestf "%s stopped at %A" f.Id r
                  | _ -> ()

          testCase "every derive fixture lowers up to its closing limit, through both dialects"
          <| fun _ ->
              // The closing `Limit` has no sort before it, so it is the boundary; everything before it,
              // the derives and the groupings keyed by them included, is in the statement (D15). The one
              // exception is the derive whose arms the exact typer keeps apart: its cells decide its
              // type, so it places the boundary with the refusal every derive had before D15.
              for dialect in [ SqlServer.dialect; PostgreSql.dialect ] do
                  for f in Fixtures.derives do
                      match f.Id, SqlServerTests.boundaryOf dialect f with
                      | "derive-coalesce-mixed", Some(Refusal.VerbNotLowered(1, "derive")) -> ()
                      | "derive-coalesce-mixed", other ->
                          failtestf "derive-coalesce-mixed through %s: the boundary is %A" dialect.Name other
                      | _, Some(Refusal.UnorderedLimit _) -> ()
                      | _, other -> failtestf "%s through %s: the boundary is %A" f.Id dialect.Name other

          testCase "the kit agrees with the reference over the in-memory interpreter, through the PostgreSQL dialect"
          <| fun _ ->
              match Kit.run PostgreSql.dialect SqlServerTests.memoryHost with
              | Error e -> failtest e
              | Ok report ->
                  Kit.describe report
                  |> List.iter (printfn "conformance (postgresql, in memory): %s")

                  Expect.equal (Kit.counts report) (List.length Fixtures.all, 0, 0) "every fixture agreed"

          testCase
              "the code point pin is checkable: a code-point answer is predicted, another wrong answer is unexpected"
          <| fun _ ->
              let f = Fixtures.all |> List.find (fun f -> f.Id = "difference-code-point-order")

              // An engine comparing by code point: the same answer as the reference over strings whose
              // code-unit order is their code-point order.
              let byCodePoint =
                  reencodingHost (fun s ->
                      s
                      |> String.map (fun ch ->
                          let u = int ch

                          if u >= 0xD800 && u <= 0xDFFF then char (u + 0x2000)
                          elif u >= 0xE000 then char (u - 0x800)
                          else ch))

              match Kit.runWith [ f ] PostgreSql.dialect byCodePoint with
              | Ok r ->
                  Expect.equal
                      (r.Fixtures |> List.map _.Verdict)
                      [ Verdict.DifferingAsPinned Pin.CodePointOrder ]
                      "a code-point engine"
              | Error e -> failtest e

              Expect.notEqual
                  (Kit.predicted Pin.CodePointOrder f)
                  [ Kit.expected f ]
                  "the fixture's answer depends on the order"

              let wrong: Host =
                  { SqlServerTests.memoryHost with
                      Execute = fun _ -> Ok [ [ Int 0 ] ] }

              match Kit.runWith [ f ] PostgreSql.dialect wrong with
              | Ok r ->
                  match r.Fixtures |> List.map _.Verdict with
                  | [ Verdict.DifferingUnexpectedly _ ] -> ()
                  | other -> failtestf "expected an unexpected difference, got %A" other
              | Error e -> failtest e

          testCase
              "the collation order fixture is the case-insensitive pin's to predict, and a case-folding engine shows it"
          <| fun _ ->
              let f = Fixtures.all |> List.find (fun f -> f.Id = "difference-collation-order")

              Expect.notEqual
                  (Kit.predicted Pin.CaseInsensitiveCollation f)
                  [ Kit.expected f ]
                  "the fixture's answer depends on case"

              match Kit.runWith [ f ] PostgreSql.dialect (reencodingHost (fun s -> s.ToLowerInvariant())) with
              | Ok r ->
                  Expect.equal
                      (r.Fixtures |> List.map _.Verdict)
                      [ Verdict.DifferingAsPinned Pin.CaseInsensitiveCollation ]
                      "a case-folding engine"
              | Error e -> failtest e

          testCase "the provider's lowering core names no dialect: no literal it can write is a dialect's spelling"
          <| fun _ ->
              // The checker is checked first: it finds a dialect's spelling, and only in code.
              Expect.equal
                  (dialectSpellingsIn "let page = \"LIMIT \" + n\n// \"OFFSET\" in a comment is not code")
                  [ "LIMIT ", "LIMIT" ]
                  "the checker sees a literal, not a comment"

              for file in coreFiles do
                  let source =
                      File.ReadAllText(Path.Combine(SqlServerTests.repoRoot (), "src", "Fuaran.DB.Provider", file))

                  Expect.isNonEmpty (literalsOf source) (sprintf "%s was read" file)
                  Expect.equal (dialectSpellingsIn source) [] (sprintf "%s writes a dialect's spelling" file)

          // THE ARMED LEG. With a server configured it runs the kit against it and fails on any unexpected
          // difference; with none it is Pending — reported as ignored, never failed — and the run's output
          // says which it was.
          (match Environment.GetEnvironmentVariable serverVariable with
           | null
           | "" ->
               printfn
                   "conformance leg (postgresql): PENDING — no server configured; set %s to a connection string to arm it"
                   serverVariable

               ptestCase (sprintf "PENDING: the kit against a real PostgreSQL (set %s to arm it)" serverVariable) ignore
           | connectionString ->
               testList
                   "ARMED"
                   [ // A timestamp keeps its unit through the engine: a column created at each unit reports
                     // that unit back, and an instant bound at it reads back whole.
                     testCase "a timestamp column at each unit reports its unit and round-trips its fraction"
                     <| fun _ -> timestampRoundTrip connectionString

                     testCase "the kit against the configured PostgreSQL agrees, or differs only as pinned"
                     <| fun _ ->
                         let report, version, collation =
                             runAgainstServer PostgreSql.dialect false connectionString

                         let a, p, u = Kit.counts report

                         printfn "conformance leg (postgresql): ARMED — engine %s, collation %s" version collation

                         fixtureLines report
                         |> List.iter (printfn "conformance (postgresql, engine, fixture): %s")

                         Kit.describe report
                         |> List.iter (printfn "conformance (postgresql, engine): %s")

                         printfn
                             "conformance leg (postgresql): %d agreed, %d differing as pinned, %d differing unexpectedly"
                             a
                             p
                             u

                         Expect.equal u 0 "no fixture differs unexpectedly"
                         Expect.equal (caseInsensitive report) [] "strings compare case-sensitively here"

                     // The falsifier's converse (Phase 4): over the same linguistic database the certified
                     // dialect still agrees, so it is the re-declared `COLLATE "C"` that turns the run green,
                     // not something the database happens to share with the reference.
                     testCase "the kit over a linguistic collation agrees for the dialect that re-declares it"
                     <| fun _ ->
                         let report, _, collation = runAgainstServer PostgreSql.dialect true connectionString

                         let a, p, u = Kit.counts report

                         printfn
                             "conformance leg (postgresql): certified dialect over collation %s: %d agreed, %d differing as pinned, %d differing unexpectedly"
                             collation
                             a
                             p
                             u

                         Expect.equal u 0 "no fixture differs unexpectedly over a linguistic database"
                         Expect.equal (caseInsensitive report) [] "strings compare case-sensitively there too"

                     // The falsifier: a dialect that reads strings under the database's own collation is
                     // caught over a linguistic one.
                     testCase "the kit goes red over a linguistic collation for a dialect that does not re-declare it"
                     <| fun _ ->
                         let broken =
                             { PostgreSql.dialect with
                                 Name = "postgresql-broken"
                                 ReadValue = fun _ value -> value }

                         let report, _, collation = runAgainstServer broken true connectionString
                         let a, p, u = Kit.counts report

                         printfn
                             "conformance leg (postgresql): falsifier over collation %s: %d agreed, %d differing as pinned, %d differing unexpectedly"
                             collation
                             a
                             p
                             u

                         Kit.describe report
                         |> List.iter (printfn "conformance (postgresql, falsifier): %s")

                         Expect.isTrue
                             (u > 0 || not (List.isEmpty (caseInsensitive report)))
                             "a linguistic order is not the reference's" ]) ]
