namespace Fuaran.DB

open Fuaran.Core

// ============================================================================
//  The source fingerprint (Phase 2008, DECISIONS.md D6) — how a host learns
//  that a source moved without reading it.
//
//  The model declares a source's METHOD (`SourceSpec.Fingerprint`). What the
//  method can be trusted for follows from the method and is disclosed here, so
//  no author can call a heuristic exact. The host owns the connection or the
//  file: it reads the fingerprint (a provider's probe says what to read) and
//  hands the READING over. Nothing here asks a source. Two readings taken under
//  one method compare to a verdict a host can act on.
// ============================================================================

/// How far a fingerprint can be trusted.
[<RequireQualifiedAccess>]
type FingerprintKind =
    /// Every change to the data moves it. An unmoved exact fingerprint means the data did not change.
    | Exact
    /// It can miss a change. An unmoved heuristic fingerprint means no change was SEEN, and its
    /// disclosed misses say what could have happened unseen; a host backs it with a schedule.
    | Heuristic

/// What a method can be trusted for: its kind, the changes it can miss, and the moves it can report
/// when the data did not change.
type FingerprintDisclosure =
    {
        /// Exact or heuristic.
        Kind: FingerprintKind
        /// The changes the method can fail to see. Empty exactly when the kind is `Exact`.
        Misses: string list
        /// The ways it can move when the data did not change: a re-run that was not needed, never a
        /// result that is wrong.
        OverReports: string list
    }

/// One table's row count and the maximum of its change column, as the host read them.
type TableReading =
    {
        /// The table's physical identifier, as its probe names it.
        Table: string
        /// The number of rows.
        Rows: int64
        /// The change column's maximum; `Null` for an empty table or a column holding only nulls.
        MaxChange: Cell
    }

/// A file's metadata, as the host read it.
type FileReading =
    {
        /// The size in bytes.
        Size: int64
        /// The last-modified instant, in UTC ticks (100-nanosecond intervals since 0001-01-01), at
        /// whatever resolution the file system keeps.
        ModifiedUtcTicks: int64
        /// The content hash, `sha256:` and 64 lowercase hex digits, when the host computed it.
        ContentHash: string option
    }

/// A fingerprint as a host read it — data, from whatever read it.
[<RequireQualifiedAccess>]
type FingerprintReading =
    /// A change counter's or log position's value, as the source reports it, in text.
    | Counter of position: string
    /// Each table's row count and maximum change value.
    | Tables of TableReading list
    /// A file's size, modification time and optional content hash.
    | File of FileReading

/// What two readings under one method say about the source between them.
[<RequireQualifiedAccess>]
type FingerprintVerdict =
    /// The fingerprint moved; each entry says where. Under any method, a re-run is warranted.
    | Changed of what: string list
    /// An exact fingerprint did not move: the data did not change.
    | Unchanged
    /// A heuristic fingerprint did not move. These are the changes it could have missed.
    | NoChangeSeen of misses: string list
    /// The two readings cannot be compared under the method: a reading of the wrong shape, readings
    /// over different tables, or a hash the method needs and a reading lacks. A host treats it as
    /// `Changed` and takes fresh readings.
    | Incomparable of reason: string

/// Disclosing, finding and comparing fingerprints.
[<RequireQualifiedAccess>]
module Fingerprint =

    /// What a method can be trusted for.
    let disclose (m: FingerprintMethod) : FingerprintDisclosure =
        match m with
        | FingerprintMethod.ChangeCounter ->
            { Kind = FingerprintKind.Exact
              Misses = []
              OverReports =
                [ "it moves on every write the counter covers, including writes that leave the data as it was"
                  "a counter that covers more than this source (a whole database or server log) moves on writes to the rest of it"
                  "a restore or a failover to a different history can move it without a change, and is taken as one" ] }
        | FingerprintMethod.RowCountMaxChange _ ->
            { Kind = FingerprintKind.Heuristic
              Misses =
                [ "an update that does not advance the table's change column"
                  "a delete and an insert between two readings whose new change value does not exceed the old maximum: the count and the maximum both stand"
                  "changes within the change column's resolution of the maximum (a timestamp keeps whole seconds, a date whole days)"
                  "a change column written from a clock behind the one that wrote the maximum" ]
              OverReports = [ "an update that advances the change column and leaves every other value as it was" ] }
        | FingerprintMethod.FileStat true ->
            { Kind = FingerprintKind.Exact
              Misses = []
              OverReports = [] }
        | FingerprintMethod.FileStat false ->
            { Kind = FingerprintKind.Heuristic
              Misses =
                [ "a rewrite that keeps the size and lands within the file system's modification-time resolution"
                  "a rewrite whose writer keeps the size and restores the modification time (an archive extraction, a copy that preserves times)" ]
              OverReports = [ "a write or touch that moves the modification time and leaves the content as it was" ] }

    /// The method's kind.
    let kind (m: FingerprintMethod) : FingerprintKind = (disclose m).Kind

    /// The source node carrying `sourceId`, with its spec.
    let trySource (sourceId: string) (root: ModelNode) : SourceSpec option =
        ModelNode.tryFind sourceId root
        |> Option.bind (fun n ->
            match n.Kind with
            | NodeKind.Source s -> Some s
            | NodeKind.Model _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

    /// The method the model declares for a source; `None` for an unknown source or one that
    /// declares none.
    let ofSource (sourceId: string) (root: ModelNode) : FingerprintMethod option =
        trySource sourceId root |> Option.bind _.Fingerprint

    let private tables (rs: TableReading list) = rs |> List.map _.Table

    let private compareTables (before: TableReading list) (after: TableReading list) : FingerprintVerdict option =
        let names = tables before

        if List.distinct names <> names || List.distinct (tables after) <> tables after then
            Some(FingerprintVerdict.Incomparable "a reading names one table more than once")
        elif List.sort names <> List.sort (tables after) then
            Some(
                FingerprintVerdict.Incomparable(
                    sprintf
                        "the readings cover different tables: %s against %s"
                        (String.concat ", " names)
                        (String.concat ", " (tables after))
                )
            )
        else
            let byName = after |> List.map (fun r -> r.Table, r) |> Map.ofList

            let moved =
                before
                |> List.collect (fun b ->
                    let a = byName[b.Table]

                    [ if a.Rows <> b.Rows then
                          yield sprintf "table '%s': rows %d -> %d" b.Table b.Rows a.Rows
                      if Cell.token a.MaxChange <> Cell.token b.MaxChange then
                          yield
                              sprintf
                                  "table '%s': change maximum %s -> %s"
                                  b.Table
                                  (Cell.token b.MaxChange)
                                  (Cell.token a.MaxChange) ])

            if List.isEmpty moved then
                None
            else
                Some(FingerprintVerdict.Changed moved)

    /// What two readings, the earlier first, say about the source under `m`. Total: a reading the
    /// method cannot compare is `Incomparable`, never a silent `Unchanged`.
    let compare (m: FingerprintMethod) (before: FingerprintReading) (after: FingerprintReading) : FingerprintVerdict =
        let unmoved () =
            match kind m with
            | FingerprintKind.Exact -> FingerprintVerdict.Unchanged
            | FingerprintKind.Heuristic -> FingerprintVerdict.NoChangeSeen (disclose m).Misses

        let shape (r: FingerprintReading) =
            match r with
            | FingerprintReading.Counter _ -> "a counter"
            | FingerprintReading.Tables _ -> "table counts"
            | FingerprintReading.File _ -> "a file's metadata"

        let wrongShape () =
            FingerprintVerdict.Incomparable(
                sprintf "the method does not read %s and %s together" (shape before) (shape after)
            )

        match m, before, after with
        | FingerprintMethod.ChangeCounter, FingerprintReading.Counter b, FingerprintReading.Counter a ->
            if a = b then
                unmoved ()
            else
                FingerprintVerdict.Changed [ sprintf "counter '%s' -> '%s'" b a ]
        | FingerprintMethod.RowCountMaxChange _, FingerprintReading.Tables b, FingerprintReading.Tables a ->
            compareTables b a |> Option.defaultWith unmoved
        | FingerprintMethod.FileStat true, FingerprintReading.File b, FingerprintReading.File a ->
            match b.ContentHash, a.ContentHash with
            | Some hb, Some ha ->
                if hb = ha then
                    unmoved ()
                else
                    FingerprintVerdict.Changed [ "content hash differs" ]
            | None, _
            | _, None -> FingerprintVerdict.Incomparable "the method reads a content hash, and a reading carries none"
        | FingerprintMethod.FileStat false, FingerprintReading.File b, FingerprintReading.File a ->
            let moved =
                [ if a.Size <> b.Size then
                      yield sprintf "size %d -> %d" b.Size a.Size
                  if a.ModifiedUtcTicks <> b.ModifiedUtcTicks then
                      yield "modification time moved" ]

            if List.isEmpty moved then
                unmoved ()
            else
                FingerprintVerdict.Changed moved
        | FingerprintMethod.ChangeCounter, _, _
        | FingerprintMethod.RowCountMaxChange _, _, _
        | FingerprintMethod.FileStat _, _, _ -> wrongShape ()
