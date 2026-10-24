namespace Fuaran.DB.Conformance

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider

/// Why the host could not answer a statement.
[<RequireQualifiedAccess>]
type HostFailure =
    /// The engine answered, and the dialect decoded the answer as the reference's own refusal (an
    /// int32 sum's overflow, for one).
    | Refused of EvalError
    /// The engine or the driver failed: the statement did not run, or its result could not be read.
    | Failed of detail: string

/// What the kit needs from a host: it seeds a database with the seed's tables, keyed and named
/// PHYSICALLY, and runs a statement, answering its rows positionally. The kit opens nothing itself.
type Host =
    {
        /// Create the seed's tables and their rows, replacing any already there.
        Seed: Map<string, Table> -> Result<unit, string>
        /// Run one statement against the seeded database.
        Execute: Statement -> Result<Cell list list, HostFailure>
    }

/// A fixture's verdict.
[<RequireQualifiedAccess>]
type Verdict =
    /// The engine and the reference answered the same.
    | Agreed
    /// The answers differ, and the engine's is one the named pin predicts.
    | DifferingAsPinned of Pin
    /// The answers differ in a way no pin the fixture declares predicts.
    | DifferingUnexpectedly of detail: string

/// One fixture's outcome.
type FixtureReport =
    {
        /// The fixture's id.
        Fixture: string
        /// The verdict.
        Verdict: Verdict
        /// How many planned steps the statement carried.
        Lowered: int
        /// The refusal that placed the boundary, or `None` when every step lowered.
        Boundary: Refusal option
        /// The statement text the host ran, where one was written.
        Statement: string option
    }

/// A kit run.
type Report =
    {
        /// The kit version the fixtures are stamped with.
        Kit: string
        /// The dialect certified.
        Dialect: string
        /// Each fixture's outcome, in fixture order.
        Fixtures: FixtureReport list
    }

/// The conformance kit (`validator:Fuaran.DB.Conformance`): the fixtures, the reference's answers to
/// them, the runner and the emitted fixture set.
[<RequireQualifiedAccess>]
module Kit =

    /// The kit version every emitted fixture is stamped with: this assembly's version.
    let version: string =
        let v = typeof<Fixture>.Assembly.GetName().Version
        sprintf "%d.%d.%d" v.Major v.Minor v.Build

    /// The subject every fixture is admitted for: every classification the seed declares, row by row.
    let subject: Subject =
        { Principal = "conformance"
          Attributes = Map.empty
          Grants =
            [ { Classification = "public"
                Level = GrantLevel.RowLevel } ] }

    let private case (tables: Map<string, Table>) (bindings: Bindings) (f: Fixture) : PlacementCase =
        { Model = Fixtures.model
          Source = Ref f.Source
          Pipeline = f.Pipeline
          Bindings = bindings
          Tables = tables }

    /// The reference evaluator's answer to a fixture over the seed: the pipeline as written.
    let expected (f: Fixture) : Result<Table, EvalError> =
        PlacementLaws.reference (case Fixtures.tables f.Bindings f)

    let private rows (t: Table) : Cell list list =
        [ for i in 0 .. Table.rowCount t - 1 -> t.Columns |> List.map (Column.cell i) ]

    /// `col` holding `cells` instead. The kit rebuilds a column only from cells of its own type, so
    /// a refusal is a defect in the kit and throws.
    let private refilled (col: Column) (cells: Cell list) : Column =
        match Column.ofCells col.Name col.Type cells with
        | Ok c -> c
        | Error e -> invalidArg col.Name (ColumnCodec.errorString e)

    /// Do two answers agree: the same refusal, or the same schema and the same rows — in order when
    /// the fixture is ordered, as a bag otherwise.
    let agrees (ordered: bool) (a: Result<Table, EvalError>) (b: Result<Table, EvalError>) : bool =
        match a, b with
        | Error x, Error y -> x = y
        | Ok x, Ok y ->
            x.Schema = y.Schema
            && (if ordered then
                    rows x = rows y
                else
                    let bag (t: Table) =
                        rows t
                        |> List.map (fun r -> sprintf "%A" r, r)
                        |> List.sortBy fst
                        |> List.map snd

                    bag x = bag y)
        | Ok _, Error _
        | Error _, Ok _ -> false

    /// A string cell in lower case; any other cell as it is.
    let private foldCase (c: Cell) : Cell =
        match c with
        | Str s -> Str(s.ToLowerInvariant())
        | Int _
        | Float _
        | Bool _
        | Date _
        | Timestamp _
        | Null
        | Decimal _ -> c

    /// A string re-encoded so that ordinal (code-unit) order is code-point order: U+E000 to U+FFFF
    /// move below the surrogates, and the surrogates above them, each range keeping its own order.
    let private codePointKey (c: Cell) : Cell =
        match c with
        | Str s ->
            Str(
                s
                |> String.map (fun ch ->
                    let u = int ch

                    if u >= 0xD800 && u <= 0xDFFF then char (u + 0x2000)
                    elif u >= 0xE000 then char (u - 0x800)
                    else ch)
            )
        | Int _
        | Float _
        | Bool _
        | Date _
        | Timestamp _
        | Null
        | Decimal _ -> c

    /// The reference over a fixture with every cell of the seed and the bindings passed through `f`.
    let private referenceOver (f: Cell -> Cell) (fixture: Fixture) : Result<Table, EvalError> =
        let table (t: Table) =
            { t with
                Columns =
                    t.Columns
                    |> List.map (fun col -> refilled col (Column.toCells col |> List.map f)) }

        let bindings =
            { fixture.Bindings with
                Params = fixture.Bindings.Params |> Map.map (fun _ c -> f c)
                Lists = fixture.Bindings.Lists |> Map.map (fun _ cs -> List.map f cs) }

        PlacementLaws.reference (case (Fixtures.tables |> Map.map (fun _ t -> table t)) bindings fixture)

    let rec private permutations (xs: 'a list) : 'a list list =
        match xs with
        | [] -> [ [] ]
        | _ ->
            xs
            |> List.indexed
            |> List.collect (fun (i, x) ->
                let rest = xs |> List.indexed |> List.filter (fun (j, _) -> j <> i) |> List.map snd
                permutations rest |> List.map (fun p -> x :: p))

    /// The answers a pin predicts for a fixture.
    let predicted (pin: Pin) (f: Fixture) : Result<Table, EvalError> list =
        match pin with
        | Pin.CaseInsensitiveCollation -> [ referenceOver foldCase f ]
        | Pin.CodePointOrder -> [ referenceOver codePointKey f ]
        | Pin.FloatSummationOrder ->
            match Map.tryFind f.Source Fixtures.tables with
            | Some source when Table.rowCount source <= 7 ->
                permutations (rows source)
                |> List.map (fun order ->
                    let permuted =
                        { source with
                            Columns =
                                source.Columns
                                |> List.mapi (fun j col -> refilled col (order |> List.map (List.item j))) }

                    PlacementLaws.reference (case (Map.add f.Source permuted Fixtures.tables) f.Bindings f))
            | Some _
            | None -> []

    let private describeAnswer (a: Result<Table, EvalError>) =
        match a with
        | Ok t -> sprintf "%d row(s) %A" (Table.rowCount t) (rows t)
        | Error e -> sprintf "refused %A" e

    /// Run one fixture through `dialect` and the host's executor, the seed already in place.
    let runFixture (dialect: Dialect) (execute: Statement -> Result<Cell list list, HostFailure>) (f: Fixture) =
        let report verdict lowered boundary statement =
            { Fixture = f.Id
              Verdict = verdict
              Lowered = lowered
              Boundary = boundary
              Statement = statement }

        let expected = expected f

        match Policy.admit Fixtures.model subject (Ref f.Source) f.Pipeline with
        | Error r -> report (Verdict.DifferingUnexpectedly(sprintf "the policy refused the fixture: %A" r)) 0 None None
        | Ok admitted ->
            match FoldSplit.lower admitted dialect f.Bindings with
            | Error r -> report (Verdict.DifferingUnexpectedly(sprintf "no statement was written: %A" r)) 0 None None
            | Ok lowered ->
                let partial v =
                    report v lowered.Split.Lowered lowered.Split.Boundary (Some lowered.Statement.Text)

                let resolve (name: string) =
                    match Map.tryFind name Fixtures.tables with
                    | Some t -> Ok t
                    | None -> Error(UnresolvedSource name)

                let actual =
                    match execute lowered.Statement with
                    | Error(HostFailure.Failed detail) -> Error detail
                    | Error(HostFailure.Refused e) -> Ok(Error e)
                    | Ok cells ->
                        match Statement.toTable lowered.Statement cells with
                        | Error e -> Error(sprintf "the result could not be read: %A" e)
                        | Ok result -> Ok(FoldSplit.finish lowered resolve f.Bindings result)

                match actual with
                | Error detail -> partial (Verdict.DifferingUnexpectedly("the host failed: " + detail))
                | Ok answer ->
                    if agrees f.Ordered answer expected then
                        partial Verdict.Agreed
                    else
                        match
                            f.Pins
                            |> List.tryFind (fun pin -> predicted pin f |> List.exists (agrees f.Ordered answer))
                        with
                        | Some pin -> partial (Verdict.DifferingAsPinned pin)
                        | None ->
                            partial (
                                Verdict.DifferingUnexpectedly(
                                    sprintf
                                        "the engine answered %s; the reference answered %s"
                                        (describeAnswer answer)
                                        (describeAnswer expected)
                                )
                            )

    /// Seed the host, then run every fixture of `fixtures` through `dialect`. `Error` only when the
    /// seed could not be placed.
    let runWith (fixtures: Fixture list) (dialect: Dialect) (host: Host) : Result<Report, string> =
        host.Seed Fixtures.physical
        |> Result.map (fun () ->
            { Kit = version
              Dialect = dialect.Name
              Fixtures = fixtures |> List.map (runFixture dialect host.Execute) })

    /// Seed the host, then run the whole kit through `dialect`.
    let run (dialect: Dialect) (host: Host) : Result<Report, string> = runWith Fixtures.all dialect host

    /// How many fixtures took each verdict: agreed, differing as pinned, differing unexpectedly.
    let counts (report: Report) : int * int * int =
        report.Fixtures
        |> List.fold
            (fun (a, p, u) r ->
                match r.Verdict with
                | Verdict.Agreed -> a + 1, p, u
                | Verdict.DifferingAsPinned _ -> a, p + 1, u
                | Verdict.DifferingUnexpectedly _ -> a, p, u + 1)
            (0, 0, 0)

    /// The report as lines: one per fixture that did not agree, then the counts.
    let describe (report: Report) : string list =
        let a, p, u = counts report

        (report.Fixtures
         |> List.choose (fun r ->
             match r.Verdict with
             | Verdict.Agreed -> None
             | Verdict.DifferingAsPinned pin -> Some(sprintf "%s: differing as pinned (%A)" r.Fixture pin)
             | Verdict.DifferingUnexpectedly detail -> Some(sprintf "%s: DIFFERING UNEXPECTEDLY: %s" r.Fixture detail)))
        @ [ sprintf
                "kit %s, dialect %s: %d fixture(s) — %d agreed, %d differing as pinned, %d differing unexpectedly"
                report.Kit
                report.Dialect
                (List.length report.Fixtures)
                a
                p
                u ]

    // ---- the emitted fixture set ----

    let private tableJson (t: Table) : JVal = ColumnCodec.encodeJson (Embedded t)

    let private answerJson (a: Result<Table, EvalError>) : JVal =
        match a with
        | Ok t -> JObj [ "table", tableJson t ]
        | Error e -> JObj [ "refused", JStr(sprintf "%A" e) ]

    let private stamp (fields: (string * JVal) list) =
        Canonical.write Rendering.CrossHost (JObj(("kit", JStr version) :: fields))

    /// The seed as emitted: the model, and every table keyed and named physically.
    let seedJson () : string =
        stamp
            [ "model", ModelWire.encodeNodeJson Fixtures.model
              "tables", JObj(Fixtures.physical |> Map.toList |> List.map (fun (k, t) -> k, tableJson t)) ]

    /// One fixture as emitted: its pipeline with the bindings substituted, its order, its pins, the
    /// construct it reaches, and the reference's answer over the seed.
    let fixtureJson (f: Fixture) : string =
        stamp (
            [ "id", JStr f.Id
              "description", JStr f.Description
              "source", JStr f.Source
              "pipeline", JArr(FoldSplit.bind f.Bindings f.Pipeline |> List.map DataFrameCodec.encodeTransform)
              "ordered", JBool f.Ordered
              "pins", JArr(f.Pins |> List.map (fun p -> JStr(sprintf "%A" p)))
              "expected", answerJson (expected f) ]
            @ (f.Reaches
               |> Option.map (fun c -> "reaches", JStr(sprintf "%A" c))
               |> Option.toList)
        )

    /// The emitted set, as (relative path, text): a manifest, the seed, and one file per fixture.
    let emit () : (string * string) list =
        [ "manifest.json",
          stamp
              [ "fixtures", JArr(Fixtures.all |> List.map (fun f -> JStr f.Id))
                "count", JInt(List.length Fixtures.all) ]
          "seed.json", seedJson () ]
        @ (Fixtures.all |> List.map (fun f -> "fixtures/" + f.Id + ".json", fixtureJson f))
