namespace Fuaran.DB.Provider

open System
open Fuaran.Core
open Fuaran.DB

// ============================================================================
//  The fingerprint probe (Phase 2008, DECISIONS.md D6) — what a host reads, per
//  dialect, to fingerprint a source without reading it.
//
//  The model declares the method; the dialect says whether its engine keeps a
//  change counter; this module turns the two into the statement text a host
//  runs, or a named refusal. It opens no connection. The host runs a probe and
//  hands its rows back (`readTables`), or reads a file's metadata itself, and
//  compares readings with `Fingerprint.compare`.
// ============================================================================

/// One table's probe: the statement that reads its row count and its change column's maximum, as
/// one row of two columns in that order.
type TableProbe =
    {
        /// The table's physical identifier.
        Table: string
        /// The change column's physical identifier.
        ChangeColumn: string
        /// The change column's type, which the maximum carries.
        ChangeType: ColumnType
        /// The statement text, through the dialect. It carries no parameter.
        Text: string
    }

/// What a host reads to fingerprint one source.
[<RequireQualifiedAccess>]
type Probe =
    /// Run this statement; its one value is the change counter's position.
    | Counter of text: string
    /// Run each table's statement and hand the rows to `Probe.readTables`.
    | Tables of TableProbe list
    /// Read the file's size and modification time, and its content hash when `contentHash` is set.
    /// No statement: the file is the source.
    | File of contentHash: bool

/// Why no probe can be written for a source — each case names what to change.
[<RequireQualifiedAccess>]
type ProbeRefusal =
    /// The model holds no source with this id.
    | UnknownSource of source: string * sources: string list
    /// The source declares no fingerprint; a host must read it or work to a schedule.
    | Unfingerprinted of source: string
    /// The source is fingerprinted by change counter and the dialect's engine keeps none.
    | NoChangeCounter of source: string * dialect: string
    /// The source's fingerprint breaks a model rule (the `DB-FINGERPRINT-*` codes).
    | Malformed of source: string * codes: string list
    /// The dialect does not lower the count or the maximum a row-count probe needs.
    | DialectRefuses of construct: Construct * reason: string

/// Writing a source's probe through a dialect, and reading its rows back.
[<RequireQualifiedAccess>]
module Probe =

    /// The refusal's code.
    let code (r: ProbeRefusal) : string =
        match r with
        | ProbeRefusal.UnknownSource _ -> "DB-PROBE-UNKNOWN-SOURCE"
        | ProbeRefusal.Unfingerprinted _ -> "DB-PROBE-UNFINGERPRINTED"
        | ProbeRefusal.NoChangeCounter _ -> "DB-PROBE-NO-CHANGE-COUNTER"
        | ProbeRefusal.Malformed _ -> "DB-PROBE-MALFORMED"
        | ProbeRefusal.DialectRefuses _ -> "DB-PROBE-DIALECT-REFUSES"

    let private sources (root: ModelNode) : string list =
        ModelNode.preorder root
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Source _ -> Some n.Id
            | NodeKind.Model _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

    let private fingerprintCodes =
        Set.ofList
            [ ModelRules.FingerprintDangling
              ModelRules.FingerprintForeign
              ModelRules.FingerprintType
              ModelRules.FingerprintCoverage ]

    let private tableProbe
        (d: Dialect)
        (root: ModelNode)
        (sourceId: string)
        (tableId: string, columnId: string)
        : Result<TableProbe, ProbeRefusal> =
        let refused =
            [ Construct.Aggregate Count; Construct.Aggregate Max ]
            |> List.tryPick (fun c -> d.Refuses c |> Option.map (fun why -> c, why))

        match
            Model.tables root |> List.tryFind (fun (n, _) -> n.Id = tableId), Model.tryColumn columnId root, refused
        with
        | None, _, _
        | _, None, _ -> Error(ProbeRefusal.Malformed(sourceId, [ ModelRules.FingerprintDangling ]))
        | Some _, Some _, Some(c, why) -> Error(ProbeRefusal.DialectRefuses(c, why))
        | Some(_, table), Some(_, column), None ->
            let physical = table.Physical
            let q = d.QuoteIdentifier

            Ok
                { Table = physical
                  ChangeColumn = column.Physical
                  ChangeType = column.Type
                  Text =
                    "SELECT "
                    + d.RenderFunction (Construct.Aggregate Count) [ "*" ]
                    + " AS "
                    + q "rows"
                    + ", "
                    + d.RenderFunction (Construct.Aggregate Max) [ q column.Physical ]
                    + " AS "
                    + q "maxChange"
                    + " FROM "
                    + q physical }

    /// The probe for one source of the model, through `d`. Total: a source that cannot be probed is
    /// a named refusal.
    let forSource (d: Dialect) (sourceId: string) (root: ModelNode) : Result<Probe, ProbeRefusal> =
        match Fingerprint.trySource sourceId root with
        | None -> Error(ProbeRefusal.UnknownSource(sourceId, sources root))
        | Some { Fingerprint = None } -> Error(ProbeRefusal.Unfingerprinted sourceId)
        | Some { Fingerprint = Some FingerprintMethod.ChangeCounter } ->
            match d.ChangeCounter with
            | Some text -> Ok(Probe.Counter text)
            | None -> Error(ProbeRefusal.NoChangeCounter(sourceId, d.Name))
        | Some { Fingerprint = Some(FingerprintMethod.FileStat hash) } -> Ok(Probe.File hash)
        | Some { Fingerprint = Some(FingerprintMethod.RowCountMaxChange pairs) } ->
            let codes =
                ModelRules.check root
                |> List.filter (fun x -> fingerprintCodes.Contains x.Code && x.Node = Some sourceId)
                |> List.map _.Code
                |> List.distinct

            if not (List.isEmpty codes) then
                Error(ProbeRefusal.Malformed(sourceId, codes))
            else
                pairs
                |> List.fold
                    (fun acc pair ->
                        acc
                        |> Result.bind (fun ps -> tableProbe d root sourceId pair |> Result.map (fun p -> p :: ps)))
                    (Ok [])
                |> Result.map (List.rev >> Probe.Tables)

    /// Whether this dialect offers a source a cheaper honest fingerprint than the one the model
    /// declares, and why. A database's own change counter is one statement and exact, so wherever
    /// the dialect keeps one it is advised over row counts or over no fingerprint at all. Advice
    /// only: the model's declaration is what probes, and a counter the dialect lacks is refused by
    /// `forSource` rather than advised around.
    let advise (d: Dialect) (sourceId: string) (root: ModelNode) : string option =
        match Fingerprint.trySource sourceId root, d.ChangeCounter with
        | Some { Fingerprint = None }, Some _
        | Some { Fingerprint = Some(FingerprintMethod.RowCountMaxChange _) }, Some _ ->
            Some(
                sprintf
                    "dialect '%s' keeps a change counter: declare ChangeCounter for source '%s', one statement and exact where row counts are heuristic"
                    d.Name
                    sourceId
            )
        | Some { Fingerprint = Some FingerprintMethod.ChangeCounter }, _
        | Some { Fingerprint = Some(FingerprintMethod.FileStat _) }, _
        | Some _, None
        | None, _ -> None

    /// A count an engine returned: an int, or an integral decimal for a count past an int's range
    /// (read through the cell's canonical token, `m:` and the decimal's canonical text).
    let private rowCount (c: Cell) : int64 option =
        match c with
        | Int n when n >= 0 -> Some(int64 n)
        | Decimal _ ->
            match Int64.TryParse((Cell.token c).Substring 2) with
            | true, n when n >= 0L -> Some n
            | _ -> None
        | Int _
        | Null
        | Float _
        | Bool _
        | Str _
        | Date _
        | Timestamp _ -> None

    /// A table probe's rows, one result per probe in the probes' order, as a reading. Each result
    /// is one row of two cells: a non-negative integral count and the maximum, which is `Null` or a
    /// cell of the change column's type.
    let readTables (probes: TableProbe list) (results: Cell list list) : Result<FingerprintReading, string> =
        if List.length probes <> List.length results then
            Error(sprintf "%d probes and %d results" (List.length probes) (List.length results))
        else
            List.zip probes results
            |> List.fold
                (fun acc (p, row) ->
                    acc
                    |> Result.bind (fun rs ->
                        match row with
                        | [ count; max ] ->
                            match rowCount count, Cell.typeOf max with
                            | None, _ ->
                                Error(
                                    sprintf "table '%s': the count '%s' is not a row count" p.Table (Cell.token count)
                                )
                            | Some _, Some t when t <> p.ChangeType ->
                                Error(
                                    sprintf
                                        "table '%s': the maximum is a %s, and change column '%s' is a %s"
                                        p.Table
                                        (ColumnType.tag t)
                                        p.ChangeColumn
                                        (ColumnType.tag p.ChangeType)
                                )
                            | Some n, _ ->
                                Ok(
                                    { Table = p.Table
                                      Rows = n
                                      MaxChange = max }
                                    :: rs
                                )
                        | _ -> Error(sprintf "table '%s': a result is one row of two cells" p.Table)))
                (Ok [])
            |> Result.map (List.rev >> FingerprintReading.Tables)
