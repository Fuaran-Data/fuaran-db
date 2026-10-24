/// The source fingerprint (Phase 2008): the model declares a method, its kind and blind spots follow
/// from it, a dialect's probe says what a host reads, and two readings compare to a verdict. Per
/// dialect: a change detected, no change, and each heuristic's documented miss, shown as a change
/// the readings cannot see.
module Fuaran.DB.Tests.FingerprintTests

open Expecto
open Fuaran.Core
open Fuaran.DB
open Fuaran.DB.Provider
open Fuaran.DB.Tests.Sample

// ---- a small shop source with a change column per table ----

let private items =
    table
        "t:items"
        "items"
        [ col "c:items.id" "id" IntType "public"
          col "c:items.price" "price" DecimalType "public"
          col "c:items.updated_at" "updated_at" (TimestampType TimeUnit.Seconds) "public" ]

let private stock =
    table
        "t:stock"
        "stock"
        [ col "c:stock.sku" "sku" StringType "public"
          col "c:stock.qty" "qty" IntType "public"
          col "c:stock.version" "version" IntType "public" ]

let private rowCounts =
    FingerprintMethod.RowCountMaxChange [ "t:items", "c:items.updated_at"; "t:stock", "c:stock.version" ]

let private shop (fingerprint: FingerprintMethod option) : ModelNode =
    { Id = "m:shop"
      Kind =
        NodeKind.Model
            { Name = "shop"
              Classifications = [ "public" ]
              Recordable = [] }
      Children =
        [ { Id = "s:shop"
            Kind =
              NodeKind.Source
                  { Name = "shop"
                    Fingerprint = fingerprint }
            Children = [ items; stock ] }
          { Id = "s:other"
            Kind = NodeKind.Source { Name = "other"; Fingerprint = None }
            Children = [ table "t:other" "other" [ col "c:other.at" "at" (TimestampType TimeUnit.Seconds) "public" ] ] } ] }

/// An engine that keeps a change counter: the reference dialect with one.
let private counterDialect: Dialect =
    { ReferenceDialect.dialect with
        Name = "reference-counter"
        ChangeCounter = Some "SELECT CHANGE_COUNTER()" }

let private fingerprintDefects (root: ModelNode) =
    ModelRules.check root
    |> List.filter (fun d -> d.Code.StartsWith "DB-FINGERPRINT-")
    |> List.map _.Code

let private ts (s: string) = Timestamp s

let private dec (s: string) = Cell.decimal s |> Option.get

// ---- what a host does: run the probes against the data it holds ----

/// A table as an engine holds it: rows of cells, with the change column at `changeAt`.
type private HeldTable = { Rows: Cell list list; ChangeAt: int }

/// What `SELECT COUNT(*), MAX(change) FROM table` answers over a held table.
let private answer (t: HeldTable) : Cell list =
    let changes =
        t.Rows
        |> List.map (fun r -> r[t.ChangeAt])
        |> List.filter (fun c -> not (Cell.isNull c))

    let max =
        if List.isEmpty changes then
            Null
        else
            changes
            |> List.sortWith (fun a b -> Cell.compare a b |> Option.defaultValue 0)
            |> List.last

    [ Int(List.length t.Rows); max ]

let private probes (d: Dialect) =
    match Probe.forSource d "s:shop" (shop (Some rowCounts)) with
    | Ok(Probe.Tables ps) -> ps
    | other -> failtestf "expected table probes, got %A" other

/// Read both tables through the dialect's probes.
let private readShop (d: Dialect) (itemsHeld: HeldTable) (stockHeld: HeldTable) : FingerprintReading =
    match Probe.readTables (probes d) [ answer itemsHeld; answer stockHeld ] with
    | Ok r -> r
    | Error e -> failtestf "%s" e

let private items0 =
    { Rows =
        [ [ Int 1; dec "9.50"; ts "2026-10-01T09:00:00Z" ]
          [ Int 2; dec "4.00"; ts "2026-10-02T09:00:00Z" ] ]
      ChangeAt = 2 }

let private stock0 =
    { Rows = [ [ Str "A"; Int 5; Int 1 ]; [ Str "B"; Int 7; Int 2 ] ]
      ChangeAt = 2 }

let private file (size: int64) (ticks: int64) (hash: string option) =
    FingerprintReading.File
        { Size = size
          ModifiedUtcTicks = ticks
          ContentHash = hash }

let private sha (text: string) =
    let bytes = System.Text.Encoding.UTF8.GetBytes text

    "sha256:"
    + (System.Security.Cryptography.SHA256.HashData bytes |> System.Convert.ToHexString).ToLowerInvariant()

let private isNoChangeSeen v =
    match v with
    | FingerprintVerdict.NoChangeSeen misses -> not (List.isEmpty misses)
    | FingerprintVerdict.Changed _
    | FingerprintVerdict.Unchanged
    | FingerprintVerdict.Incomparable _ -> false

let private isChanged v =
    match v with
    | FingerprintVerdict.Changed what -> not (List.isEmpty what)
    | FingerprintVerdict.NoChangeSeen _
    | FingerprintVerdict.Unchanged
    | FingerprintVerdict.Incomparable _ -> false

let private isIncomparable v =
    match v with
    | FingerprintVerdict.Incomparable _ -> true
    | FingerprintVerdict.Changed _
    | FingerprintVerdict.NoChangeSeen _
    | FingerprintVerdict.Unchanged -> false

[<Tests>]
let tests =
    testList
        "source fingerprint"
        [ testList
              "disclosure"
              [ testCase "each method's kind follows from the method, and only a heuristic discloses misses"
                <| fun _ ->
                    for m, kind in
                        [ FingerprintMethod.ChangeCounter, FingerprintKind.Exact
                          rowCounts, FingerprintKind.Heuristic
                          FingerprintMethod.FileStat true, FingerprintKind.Exact
                          FingerprintMethod.FileStat false, FingerprintKind.Heuristic ] do
                        let d = Fingerprint.disclose m
                        Expect.equal d.Kind kind (sprintf "%A" m)

                        Expect.equal
                            (List.isEmpty d.Misses)
                            (kind = FingerprintKind.Exact)
                            (sprintf "%A: misses are disclosed exactly when it is heuristic" m)

                testCase "the model's declaration is read back per source"
                <| fun _ ->
                    let m = shop (Some FingerprintMethod.ChangeCounter)
                    Expect.equal (Fingerprint.ofSource "s:shop" m) (Some FingerprintMethod.ChangeCounter) "declared"
                    Expect.equal (Fingerprint.ofSource "s:other" m) None "none declared"
                    Expect.equal (Fingerprint.ofSource "t:items" m) None "not a source"

                testCase "the sources read tool shows each source's method, kind and blind spots"
                <| fun _ ->
                    let tool = AiTools.readTools |> List.find (fun t -> t.Name = "sources")

                    let text =
                        Canonical.write Rendering.CrossHost (tool.Run(ModelOps.ofRoot (shop (Some rowCounts))))

                    Expect.stringContains text "\"kind\":\"heuristic\"" "the kind"
                    Expect.stringContains text "does not advance the table's change column" "a miss"
                    Expect.stringContains text "\"fingerprint\":\"none\"" "a source declaring none says so" ]

          testList
              "model rules"
              [ testCase "a declaration covering every table with a change column is clean"
                <| fun _ ->
                    for m in
                        [ Some rowCounts
                          Some FingerprintMethod.ChangeCounter
                          Some(FingerprintMethod.FileStat true)
                          None ] do
                        Expect.isEmpty (fingerprintDefects (shop m)) (sprintf "%A" m)

                testCase "a table left out, or named twice, is a coverage defect"
                <| fun _ ->
                    Expect.equal
                        (fingerprintDefects (
                            shop (Some(FingerprintMethod.RowCountMaxChange [ "t:items", "c:items.updated_at" ]))
                        ))
                        [ ModelRules.FingerprintCoverage ]
                        "stock is left out"

                    Expect.equal
                        (fingerprintDefects (
                            shop (
                                Some(
                                    FingerprintMethod.RowCountMaxChange
                                        [ "t:items", "c:items.updated_at"
                                          "t:items", "c:items.id"
                                          "t:stock", "c:stock.version" ]
                                )
                            )
                        ))
                        [ ModelRules.FingerprintCoverage ]
                        "items named twice"

                testCase "a dangling id, a foreign table or column, and a change column of the wrong type are named"
                <| fun _ ->
                    let with' extra =
                        shop (
                            Some(
                                FingerprintMethod.RowCountMaxChange(
                                    [ "t:items", "c:items.updated_at"; "t:stock", "c:stock.version" ] @ [ extra ]
                                )
                            )
                        )

                    Expect.contains
                        (fingerprintDefects (with' ("t:nowhere", "c:x")))
                        ModelRules.FingerprintDangling
                        "table"

                    Expect.contains
                        (fingerprintDefects (with' ("t:other", "c:other.at")))
                        ModelRules.FingerprintForeign
                        "another source's table"

                    Expect.equal
                        (fingerprintDefects (
                            shop (
                                Some(
                                    FingerprintMethod.RowCountMaxChange
                                        [ "t:items", "c:stock.version"; "t:stock", "c:stock.version" ]
                                )
                            )
                        ))
                        [ ModelRules.FingerprintForeign ]
                        "another table's column"

                    Expect.equal
                        (fingerprintDefects (
                            shop (
                                Some(
                                    FingerprintMethod.RowCountMaxChange
                                        [ "t:items", "c:items.price"; "t:stock", "c:stock.version" ]
                                )
                            )
                        ))
                        [ ModelRules.FingerprintType ]
                        "a decimal does not advance with a change"

                    Expect.equal
                        (fingerprintDefects (
                            shop (
                                Some(
                                    FingerprintMethod.RowCountMaxChange
                                        [ "t:items", "c:items.gone"; "t:stock", "c:stock.version" ]
                                )
                            )
                        ))
                        [ ModelRules.FingerprintDangling ]
                        "a column the model does not hold" ]

          testList
              "wire"
              [ testCase "every method round-trips on a source, and a source declaring none writes no field"
                <| fun _ ->
                    for m in
                        [ None
                          Some FingerprintMethod.ChangeCounter
                          Some rowCounts
                          Some(FingerprintMethod.FileStat true)
                          Some(FingerprintMethod.FileStat false) ] do
                        let node = shop m
                        let text = ModelWire.encodeNode node

                        match ModelWire.decodeNode text with
                        | Ok back -> Expect.equal back node (sprintf "%A" m)
                        | Error e -> failtestf "%s" (ModelWire.describe e)

                        Expect.equal
                            (text.Contains "\"fingerprint\"")
                            (Option.isSome m)
                            "the field is written only when declared"

                testCase "a malformed fingerprint is refused with a named code"
                <| fun _ ->
                    let code (text: string) =
                        match ModelWire.decodeNode text with
                        | Ok n -> failtestf "decoded %A" n
                        | Error e -> ModelWire.code e

                    let source (fp: string) =
                        "{\"$type\":\"source\",\"children\":[],\"fingerprint\":"
                        + fp
                        + ",\"id\":\"s\",\"name\":\"s\"}"

                    Expect.equal (code (source "{\"$type\":\"vibes\"}")) "DB-WIRE-UNKNOWN-KIND" "an unknown method"

                    Expect.equal
                        (code (source "{\"$type\":\"fileStat\",\"contentHash\":\"yes\"}"))
                        "DB-WIRE-WRONG-SHAPE"
                        "a hash flag that is not a boolean"

                    Expect.equal
                        (code (source "{\"$type\":\"changeCounter\",\"exact\":true}"))
                        "DB-WIRE-UNKNOWN-FIELD"
                        "a kind is never asserted on the wire"

                    Expect.equal
                        (code (source "{\"$type\":\"rowCountMaxChange\",\"tables\":[{\"table\":\"t\"}]}"))
                        "DB-WIRE-MISSING-FIELD"
                        "a pair without its column" ]

          testList
              "a dialect with a change counter (exact)"
              [ testCase "its probe is the dialect's counter statement"
                <| fun _ ->
                    Expect.equal
                        (Probe.forSource counterDialect "s:shop" (shop (Some FingerprintMethod.ChangeCounter)))
                        (Ok(Probe.Counter "SELECT CHANGE_COUNTER()"))
                        "the dialect's text"

                testCase "a change is detected, and no change is unchanged"
                <| fun _ ->
                    let m = FingerprintMethod.ChangeCounter
                    let before = FingerprintReading.Counter "0/16B3740"

                    Expect.isTrue
                        (isChanged (Fingerprint.compare m before (FingerprintReading.Counter "0/16B3790")))
                        "moved"

                    Expect.equal
                        (Fingerprint.compare m before (FingerprintReading.Counter "0/16B3740"))
                        FingerprintVerdict.Unchanged
                        "unmoved and exact: the data did not change"

                testCase "it misses nothing; a write that leaves the data as it was is its documented over-report"
                <| fun _ ->
                    // an UPDATE setting a value to itself still advances the engine's counter
                    let d = Fingerprint.disclose FingerprintMethod.ChangeCounter
                    Expect.isEmpty d.Misses "exact"
                    Expect.isNonEmpty d.OverReports "and honest about re-runs it causes"

                    Expect.isTrue
                        (isChanged (
                            Fingerprint.compare
                                FingerprintMethod.ChangeCounter
                                (FingerprintReading.Counter "41")
                                (FingerprintReading.Counter "42")
                        ))
                        "a no-op write reads as a change"

                testCase "it advises the counter over row counts, and the counterless dialect does not"
                <| fun _ ->
                    Expect.isSome (Probe.advise counterDialect "s:shop" (shop (Some rowCounts))) "cheaper and exact"
                    Expect.isSome (Probe.advise counterDialect "s:shop" (shop None)) "better than none"

                    Expect.isNone
                        (Probe.advise counterDialect "s:shop" (shop (Some FingerprintMethod.ChangeCounter)))
                        "already the counter"

                    Expect.isNone
                        (Probe.advise ReferenceDialect.dialect "s:shop" (shop (Some rowCounts)))
                        "nothing to offer" ]

          testList
              "a dialect without one (row count and maximum change, heuristic)"
              [ testCase "a counter declared over it is refused by name"
                <| fun _ ->
                    match
                        Probe.forSource ReferenceDialect.dialect "s:shop" (shop (Some FingerprintMethod.ChangeCounter))
                    with
                    | Error(ProbeRefusal.NoChangeCounter("s:shop", "reference") as r) ->
                        Expect.equal (Probe.code r) "DB-PROBE-NO-CHANGE-COUNTER" "the code"
                    | other -> failtestf "expected NoChangeCounter, got %A" other

                testCase "its probe reads each table's count and maximum through the dialect"
                <| fun _ ->
                    Expect.equal
                        (probes ReferenceDialect.dialect |> List.map _.Text)
                        [ "SELECT COUNT(*) AS \"rows\", MAX(\"UPDATED_AT\") AS \"maxChange\" FROM \"dbo.items\""
                          "SELECT COUNT(*) AS \"rows\", MAX(\"VERSION\") AS \"maxChange\" FROM \"dbo.stock\"" ]
                        "physical identifiers, quoted by the dialect"

                testCase "a malformed declaration, an unknown source or none at all is refused"
                <| fun _ ->
                    let refusal m src =
                        match Probe.forSource ReferenceDialect.dialect src m with
                        | Error r -> Probe.code r
                        | Ok p -> failtestf "probed %A" p

                    Expect.equal
                        (refusal
                            (shop (Some(FingerprintMethod.RowCountMaxChange [ "t:items", "c:items.updated_at" ])))
                            "s:shop")
                        "DB-PROBE-MALFORMED"
                        "stock left out"

                    Expect.equal (refusal (shop None) "s:shop") "DB-PROBE-UNFINGERPRINTED" "none declared"
                    Expect.equal (refusal (shop None) "s:nowhere") "DB-PROBE-UNKNOWN-SOURCE" "unknown"

                testCase "a dialect that does not lower the maximum is refused with its reason"
                <| fun _ ->
                    let picky =
                        { ReferenceDialect.dialect with
                            Refuses =
                                fun c ->
                                    match c with
                                    | Construct.Aggregate Max -> Some "no MAX here"
                                    | _ -> None }

                    match Probe.forSource picky "s:shop" (shop (Some rowCounts)) with
                    | Error(ProbeRefusal.DialectRefuses(Construct.Aggregate Max, "no MAX here")) -> ()
                    | other -> failtestf "expected DialectRefuses, got %A" other

                testCase "a change is detected: an insert, a delete, an update advancing the change column"
                <| fun _ ->
                    let before = readShop ReferenceDialect.dialect items0 stock0

                    let inserted =
                        { items0 with
                            Rows = items0.Rows @ [ [ Int 3; dec "1.00"; ts "2026-10-03T09:00:00Z" ] ] }

                    let deleted =
                        { stock0 with
                            Rows = List.take 1 stock0.Rows }

                    let updated =
                        { stock0 with
                            Rows = [ [ Str "A"; Int 4; Int 3 ]; [ Str "B"; Int 7; Int 2 ] ] }

                    for after in
                        [ readShop ReferenceDialect.dialect inserted stock0
                          readShop ReferenceDialect.dialect items0 deleted
                          readShop ReferenceDialect.dialect items0 updated ] do
                        Expect.isTrue (isChanged (Fingerprint.compare rowCounts before after)) (sprintf "%A" after)

                testCase "no change is no change seen, with the misses disclosed beside it"
                <| fun _ ->
                    let before = readShop ReferenceDialect.dialect items0 stock0
                    let after = readShop ReferenceDialect.dialect items0 stock0

                    Expect.equal
                        (Fingerprint.compare rowCounts before after)
                        (FingerprintVerdict.NoChangeSeen (Fingerprint.disclose rowCounts).Misses)
                        "heuristic: never 'unchanged'"

                testCase "documented miss: an update that does not advance the change column"
                <| fun _ ->
                    let repriced =
                        { items0 with
                            Rows = [ [ Int 1; dec "12.00"; ts "2026-10-01T09:00:00Z" ]; items0.Rows[1] ] }

                    Expect.notEqual repriced items0 "the data changed"

                    Expect.isTrue
                        (isNoChangeSeen (
                            Fingerprint.compare
                                rowCounts
                                (readShop ReferenceDialect.dialect items0 stock0)
                                (readShop ReferenceDialect.dialect repriced stock0)
                        ))
                        "and the fingerprint did not see it"

                testCase "documented miss: a delete and an insert whose change value does not exceed the maximum"
                <| fun _ ->
                    let swapped =
                        { items0 with
                            Rows = [ [ Int 9; dec "3.00"; ts "2026-09-30T09:00:00Z" ]; items0.Rows[1] ] }

                    Expect.isTrue
                        (isNoChangeSeen (
                            Fingerprint.compare
                                rowCounts
                                (readShop ReferenceDialect.dialect items0 stock0)
                                (readShop ReferenceDialect.dialect swapped stock0)
                        ))
                        "count and maximum both stand"

                testCase "documented miss: a change within the change column's resolution of the maximum"
                <| fun _ ->
                    // the latest row is rewritten in the same second its change value already names
                    let sameSecond =
                        { items0 with
                            Rows = [ items0.Rows[0]; [ Int 2; dec "4.50"; ts "2026-10-02T09:00:00Z" ] ] }

                    Expect.isTrue
                        (isNoChangeSeen (
                            Fingerprint.compare
                                rowCounts
                                (readShop ReferenceDialect.dialect items0 stock0)
                                (readShop ReferenceDialect.dialect sameSecond stock0)
                        ))
                        "a timestamp keeps whole seconds"

                testCase "a probe's rows that are not a count and a maximum of the change type are refused"
                <| fun _ ->
                    let ps = probes ReferenceDialect.dialect
                    Expect.isError (Probe.readTables ps [ [ Str "two"; Null ]; [ Int 0; Null ] ]) "a count"
                    Expect.isError (Probe.readTables ps [ [ Int 2; Int 5 ]; [ Int 0; Null ] ]) "a maximum's type"
                    Expect.isError (Probe.readTables ps [ [ Int 2; Null ] ]) "one result per probe"
                    Expect.isOk (Probe.readTables ps [ [ Int 0; Null ]; [ Int 0; Null ] ]) "empty tables" ]

          testList
              "a file source (no statement)"
              [ testCase "its probe asks the host for the file's metadata, whatever the dialect"
                <| fun _ ->
                    for d in [ ReferenceDialect.dialect; counterDialect ] do
                        Expect.equal
                            (Probe.forSource d "s:shop" (shop (Some(FingerprintMethod.FileStat true))))
                            (Ok(Probe.File true))
                            d.Name

                testCase "with the hash: a change is detected, and a touch is no change"
                <| fun _ ->
                    let m = FingerprintMethod.FileStat true
                    let before = file 11L 100L (Some(sha "hello world"))

                    Expect.isTrue
                        (isChanged (Fingerprint.compare m before (file 11L 200L (Some(sha "hello wordl")))))
                        "content moved"

                    Expect.equal
                        (Fingerprint.compare m before (file 11L 200L (Some(sha "hello world"))))
                        FingerprintVerdict.Unchanged
                        "the time moved, the content did not: exact"

                testCase "without the hash: a change is detected, and no change is no change seen"
                <| fun _ ->
                    let m = FingerprintMethod.FileStat false
                    let before = file 11L 100L None
                    Expect.isTrue (isChanged (Fingerprint.compare m before (file 12L 200L None))) "size and time moved"
                    Expect.isTrue (isNoChangeSeen (Fingerprint.compare m before (file 11L 100L None))) "unmoved"

                testCase "documented miss: a same-size rewrite that keeps the modification time, which the hash sees"
                <| fun _ ->
                    // e.g. an archive extraction that restores the original time
                    let before = file 11L 100L (Some(sha "hello world"))
                    let after = file 11L 100L (Some(sha "hello wordl"))

                    Expect.isTrue
                        (isNoChangeSeen (Fingerprint.compare (FingerprintMethod.FileStat false) before after))
                        "without the hash: not seen"

                    Expect.isTrue
                        (isChanged (Fingerprint.compare (FingerprintMethod.FileStat true) before after))
                        "with it: seen" ]

          testList
              "incomparable readings"
              [ testCase "a reading the method does not read, different tables, or a missing hash"
                <| fun _ ->
                    Expect.isTrue
                        (isIncomparable (
                            Fingerprint.compare
                                FingerprintMethod.ChangeCounter
                                (FingerprintReading.Counter "1")
                                (file 1L 1L None)
                        ))
                        "shapes"

                    let one =
                        FingerprintReading.Tables
                            [ { Table = "a"
                                Rows = 1L
                                MaxChange = Int 1 } ]

                    let other =
                        FingerprintReading.Tables
                            [ { Table = "b"
                                Rows = 1L
                                MaxChange = Int 1 } ]

                    Expect.isTrue (isIncomparable (Fingerprint.compare rowCounts one other)) "tables"

                    Expect.isTrue
                        (isIncomparable (
                            Fingerprint.compare (FingerprintMethod.FileStat true) (file 1L 1L None) (file 1L 1L None)
                        ))
                        "no hash" ] ]
