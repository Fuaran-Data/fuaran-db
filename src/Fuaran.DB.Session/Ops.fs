namespace Fuaran.DB.Session

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

// ============================================================================
//  The connection record (Phase 1903, DECISIONS.md D9) — what passed over a
//  connection, as an op-stream on Core's.
//
//  A database read is a non-deterministic input. The record pushes it into data
//  at the moment it happens: the schema a source reported and its drift verdict,
//  each pipeline received, the policy verdict with the rules applied and the
//  pipeline as rewritten, the split, the fingerprint taken before the statement,
//  the statement by hash with its parameter TYPES, the row count, a digest of the
//  result, and the outcome. Everything after the record is a projection of it.
//
//  Values are the exception. A parameter value, a result row, a literal in a
//  recorded pipeline and a fingerprint's change value are recorded only when
//  every classification the request reads is one the model marks recordable;
//  otherwise the record says so with a `Withheld` op, so an absence in the
//  stream is a statement and never a gap. The reducer enforces the rule: a
//  stream that records a value its own admission says is unrecordable does not
//  fold.
//
//  The record claims that a statement was derived from a tree under a policy.
//  It never claims that a result is correct: the database is outside it.
// ============================================================================

/// A result's digest. `Plain` is SHA-256 over the result's canonical columnar text, and is written
/// only when the result's rows are recordable; anyone holding the rows can check it. `Committed`
/// binds the same text under a secret the host keeps, and is written when they are not: the host
/// can later show that a result is the one recorded, and nobody without the secret can test a
/// guess against it (a plain digest of a one-row count would be one guess away from its value).
[<RequireQualifiedAccess>]
type ResultDigest =
    | Plain of digest: string
    | Committed of digest: string

/// What fixes every statement a session runs, and the moment it opened.
type SessionHeader =
    {
        /// The session's id, chosen by the host.
        Session: string
        /// The model root's id.
        Model: string
        /// `sha256:` over the model's canonical text (`SessionHash.model`).
        ModelHash: string
        /// The classifications the model marks recordable, as it marked them when the session opened.
        Recordable: string list
        /// The dialect's id (`Dialect.Name`).
        Dialect: string
        /// The dialect's version, as the host declares it.
        DialectVersion: string
        /// The provider's version (`SessionHash.providerVersion`).
        ProviderVersion: string
        /// When the session opened, as the host's clock read it.
        At: string
    }

/// A statement as the record holds it: its hash and everything about it that carries no value.
type StatementRecord =
    {
        /// `sha256:` over the text, the parameter names and types, the model's hash, the dialect's id
        /// and version and the provider's version (`SessionHash.statement`).
        Hash: string
        /// The parameterised text. It carries markers, never values.
        Text: string
        /// Each parameter's marker and type, in ordinal order.
        Parameters: (string * ColumnType) list
        /// The result columns, in the pipeline's names, with their types.
        Columns: (string * ColumnType) list
        /// The statement timeout, in seconds.
        TimeoutSeconds: int option
        /// The capture key (`SessionHash.captureKey`), written only when the parameter values are
        /// recordable: the key is computed over them.
        CaptureKey: string option
    }

/// What a `Withheld` op withholds.
[<RequireQualifiedAccess>]
type WithheldPart =
    /// The statement's parameter values.
    | Parameters
    /// The statement's result rows.
    | Rows

/// The op vocabulary — closed. Every op but `Opened` and `SchemaReported` names the request it
/// belongs to; a request's ops arrive in the order the reducer admits (`SessionOps.next`).
[<RequireQualifiedAccess>]
type SessionOp =
    /// The session opened. First, and once.
    | Opened of SessionHeader
    /// A source reported its schema, and the drift verdict against the model; `drift` is `None` when
    /// the model holds no source of that name.
    | SchemaReported of source: string * schema: ReportedSchema * drift: TableDrift list option * at: string
    /// A pipeline arrived over a table. `redacted` counts the literals replaced by withheld slots
    /// (`Recording.redact`); zero means the pipeline is verbatim.
    | Received of request: string * table: string * pipeline: Transform list * redacted: int * at: string
    /// The policy admitted it: for whom, the classifications it reads, every rule applied, and the
    /// pipeline as rewritten — redacted as `Received` is.
    | Admitted of
        request: string *
        principal: string *
        reads: string list *
        rules: PolicyRule list *
        pipeline: Transform list *
        redacted: int
    /// The policy refused it, by the refusal's code.
    | Refused of request: string * principal: string * code: string
    /// The fold split: how many planned steps the statement carries, the steps left to run locally,
    /// and the code of the rule that placed the boundary, if any.
    | Lowered of request: string * lowered: int * suffix: Transform list * redacted: int * boundary: string option
    /// A fingerprint read before the statement and guarding it: the source, its method and kind, the
    /// reading, the tables whose change value was withheld, and the verdict against the session's
    /// previous reading of the source (`None` for the first).
    | Fingerprinted of
        request: string *
        source: string *
        method: FingerprintMethod *
        kind: FingerprintKind *
        reading: FingerprintReading *
        withheld: string list *
        verdict: FingerprintVerdict option *
        at: string
    /// The statement ran.
    | Ran of request: string * statement: StatementRecord * at: string
    /// The statement's parameter values, by marker.
    | ValuesRecorded of request: string * values: (string * Cell) list
    /// The statement returned: its row count and the digest of its result.
    | Returned of request: string * rows: int * digest: ResultDigest * at: string
    /// The statement's result rows, positionally in its columns.
    | RowsRecorded of request: string * rows: Cell list list
    /// A refusal to record: what, and the classifications read that the model does not mark recordable.
    | Withheld of request: string * part: WithheldPart * classifications: string list
    /// The local suffix ran over the statement's result: the final row count and digest.
    | Finished of request: string * rows: int * digest: ResultDigest * at: string
    /// The request stopped, by a code that carries no value.
    | Failed of request: string * code: string * at: string

/// A source's reported schema and drift verdict, as last recorded.
type SchemaEntry =
    {
        /// What the source reported.
        Schema: ReportedSchema
        /// The drift verdict, or `None` for a source the model does not hold.
        Drift: TableDrift list option
        /// When.
        At: string
    }

/// A fingerprint reading as the record holds it.
type Guard =
    {
        /// The source.
        Source: string
        /// Its method.
        Method: FingerprintMethod
        /// Exact or heuristic.
        Kind: FingerprintKind
        /// The reading, with any withheld change value as `Null`.
        Reading: FingerprintReading
        /// The tables whose change value was withheld.
        Withheld: string list
        /// The verdict against the previous reading, `None` for the first.
        Verdict: FingerprintVerdict option
        /// When it was read.
        At: string
    }

/// The policy's verdict on a request.
[<RequireQualifiedAccess>]
type Verdict =
    /// Admitted: for whom, the classifications read, the rules applied, the pipeline as rewritten
    /// and how many of its literals were redacted.
    | Admitted of
        principal: string *
        reads: string list *
        rules: PolicyRule list *
        pipeline: Transform list *
        redacted: int
    /// Refused, by code.
    | Refused of principal: string * code: string

/// The fold split as recorded.
type Split =
    {
        /// Planned steps the statement carries.
        Lowered: int
        /// The steps run locally.
        Suffix: Transform list
        /// Literals redacted from the suffix.
        Redacted: int
        /// The code of the rule that placed the boundary.
        Boundary: string option
    }

/// A value the record either holds or says it withheld.
[<RequireQualifiedAccess>]
type Recorded<'T> =
    | Recorded of 'T
    | Withheld of classifications: string list

/// How a request ended.
[<RequireQualifiedAccess>]
type Outcome =
    | Finished of rows: int * digest: ResultDigest * at: string
    | Failed of code: string * at: string

/// Everything the record holds about one request.
type RequestRecord =
    {
        /// The request's id.
        Id: string
        /// The table it read.
        Table: string
        /// The pipeline as written, redacted as recorded.
        Pipeline: Transform list
        /// Literals redacted from it.
        Redacted: int
        /// When it arrived.
        ReceivedAt: string
        /// The schemas the session had recorded when it arrived, by source.
        Schemas: Map<string, SchemaEntry>
        /// The policy's verdict.
        Verdict: Verdict option
        /// The fold split.
        Split: Split option
        /// The fingerprints that guarded its statement, in order.
        Guards: Guard list
        /// The statement and when it ran.
        Statement: (StatementRecord * string) option
        /// Its parameter values.
        Values: Recorded<(string * Cell) list> option
        /// The statement's row count, result digest and when it returned.
        Returned: (int * ResultDigest * string) option
        /// Its result rows.
        Rows: Recorded<Cell list list> option
        /// How it ended.
        Outcome: Outcome option
    }

/// A session's state: what the reducer folds the stream into.
type SessionState =
    {
        /// The header, once opened.
        Header: SessionHeader option
        /// The last schema each source reported.
        Sources: Map<string, SchemaEntry>
        /// The last fingerprint read of each source.
        Readings: Map<string, Guard>
        /// Every request, by id.
        Requests: Map<string, RequestRecord>
        /// Request ids in arrival order.
        Order: string list
    }

/// Why the reducer refused an op — each case names what an author can do instead.
[<RequireQualifiedAccess>]
type SessionRejection =
    /// An op before `Opened`.
    | NotOpened of op: string
    /// A second `Opened`.
    | AlreadyOpened of session: string
    /// A request id already received.
    | DuplicateRequest of request: string
    /// An op for a request never received; `known` names those that were.
    | UnknownRequest of request: string * known: string list
    /// An op the request's stage does not admit; `expected` names those it does.
    | OutOfOrder of request: string * op: string * expected: string list
    /// A value recorded although the request reads classifications the model does not mark recordable.
    | NotRecordable of request: string * classifications: string list
    /// A `Withheld` whose classifications are not exactly the request's unrecordable reads.
    | WithheldMismatch of request: string * recorded: string list * unrecordable: string list
    /// A digest or capture key of the wrong form for the request's recordability.
    | DigestForm of request: string * expected: string
    /// A fingerprint verdict that is not `Fingerprint.compare`'s against the previous reading.
    | VerdictMismatch of request: string * source: string * expected: FingerprintVerdict option
    /// Recorded rows that do not match the statement: their count or their width.
    | RowsMismatch of request: string * detail: string

/// The reducer and the stages it admits.
[<RequireQualifiedAccess>]
module SessionOps =

    /// The op's wire tag.
    let tag (op: SessionOp) : string =
        match op with
        | SessionOp.Opened _ -> "opened"
        | SessionOp.SchemaReported _ -> "schemaReported"
        | SessionOp.Received _ -> "received"
        | SessionOp.Admitted _ -> "admitted"
        | SessionOp.Refused _ -> "refused"
        | SessionOp.Lowered _ -> "lowered"
        | SessionOp.Fingerprinted _ -> "fingerprinted"
        | SessionOp.Ran _ -> "ran"
        | SessionOp.ValuesRecorded _ -> "valuesRecorded"
        | SessionOp.Returned _ -> "returned"
        | SessionOp.RowsRecorded _ -> "rowsRecorded"
        | SessionOp.Withheld _ -> "withheld"
        | SessionOp.Finished _ -> "finished"
        | SessionOp.Failed _ -> "failed"

    /// Every op tag.
    let tags =
        [ "opened"
          "schemaReported"
          "received"
          "admitted"
          "refused"
          "lowered"
          "fingerprinted"
          "ran"
          "valuesRecorded"
          "returned"
          "rowsRecorded"
          "withheld"
          "finished"
          "failed" ]

    /// The state before any op.
    let empty: SessionState =
        { Header = None
          Sources = Map.empty
          Readings = Map.empty
          Requests = Map.empty
          Order = [] }

    /// The classifications in `reads` that `recordable` does not hold, sorted and distinct; empty
    /// means the values may be recorded (the recording rule, DECISIONS.md D9.2).
    let unrecordableReads (recordable: string list) (reads: string list) : string list =
        reads
        |> List.filter (fun c -> not (List.contains c recordable))
        |> List.distinct
        |> List.sort

    /// The classifications a request reads that `recordable` does not hold; empty for a request not
    /// admitted.
    let unrecordable (recordable: string list) (r: RequestRecord) : string list =
        match r.Verdict with
        | Some(Verdict.Admitted(_, reads, _, _, _)) -> unrecordableReads recordable reads
        | Some(Verdict.Refused _)
        | None -> []

    /// The ops a request admits next, by tag; empty once it has ended.
    let next (r: RequestRecord) : string list =
        let failed = "failed"

        match r.Verdict, r.Outcome with
        | _, Some _ -> []
        | None, None -> [ "admitted"; "refused" ]
        | Some(Verdict.Refused _), None -> []
        | Some(Verdict.Admitted _), None ->
            match r.Split, r.Statement, r.Values, r.Returned, r.Rows with
            | None, _, _, _, _ -> [ "lowered"; failed ]
            | Some _, None, _, _, _ -> [ "fingerprinted"; "ran"; failed ]
            | Some _, Some(s, _), None, _, _ when not (List.isEmpty s.Parameters) ->
                [ "valuesRecorded"; "withheld"; failed ]
            | Some _, Some _, _, None, _ -> [ "returned"; failed ]
            | Some _, Some _, _, Some _, None -> [ "rowsRecorded"; "withheld"; failed ]
            | Some _, Some _, _, Some _, Some _ -> [ "finished"; failed ]

    let private requestOf (op: SessionOp) : string option =
        match op with
        | SessionOp.Opened _
        | SessionOp.SchemaReported _ -> None
        | SessionOp.Received(r, _, _, _, _)
        | SessionOp.Admitted(r, _, _, _, _, _)
        | SessionOp.Refused(r, _, _)
        | SessionOp.Lowered(r, _, _, _, _)
        | SessionOp.Fingerprinted(r, _, _, _, _, _, _, _)
        | SessionOp.Ran(r, _, _)
        | SessionOp.ValuesRecorded(r, _)
        | SessionOp.Returned(r, _, _, _)
        | SessionOp.RowsRecorded(r, _)
        | SessionOp.Withheld(r, _, _)
        | SessionOp.Finished(r, _, _, _)
        | SessionOp.Failed(r, _, _) -> Some r

    /// The verdict a reading earns against the previous one, when the record can compute it: both
    /// readings whole. `None` with `false` when it cannot (a change value was withheld either side).
    let expectedVerdict
        (previous: Guard option)
        (m: FingerprintMethod)
        (reading: FingerprintReading)
        (withheld: string list)
        =
        match previous with
        | None -> true, None
        | Some p when List.isEmpty p.Withheld && List.isEmpty withheld ->
            true, Some(Fingerprint.compare m p.Reading reading)
        | Some _ -> false, None

    let private applyToRequest
        (header: SessionHeader)
        (state: SessionState)
        (r: RequestRecord)
        (op: SessionOp)
        : Result<SessionState, SessionRejection> =
        let tag = tag op
        let allowed = next r

        if not (List.contains tag allowed) then
            Error(SessionRejection.OutOfOrder(r.Id, tag, allowed))
        else
            let put (r': RequestRecord) =
                Ok
                    { state with
                        Requests = Map.add r.Id r' state.Requests }

            let unrecordable = unrecordable header.Recordable r

            let digestFits (d: ResultDigest) =
                match d, unrecordable with
                | ResultDigest.Plain _, [] -> true
                | ResultDigest.Committed _, _ :: _ -> true
                | ResultDigest.Plain _, _ :: _
                | ResultDigest.Committed _, [] -> false

            let digestExpected =
                if List.isEmpty unrecordable then
                    "a plain digest: the result is recordable"
                else
                    "a committed digest: the result reads " + String.concat ", " unrecordable

            match op with
            | SessionOp.Opened _
            | SessionOp.SchemaReported _
            | SessionOp.Received _ -> Error(SessionRejection.OutOfOrder(r.Id, tag, allowed))
            | SessionOp.Admitted(_, principal, reads, rules, pipeline, redacted) ->
                put
                    { r with
                        Verdict =
                            Some(
                                Verdict.Admitted(principal, List.distinct reads |> List.sort, rules, pipeline, redacted)
                            ) }
            | SessionOp.Refused(_, principal, code) ->
                put
                    { r with
                        Verdict = Some(Verdict.Refused(principal, code)) }
            | SessionOp.Lowered(_, lowered, suffix, redacted, boundary) ->
                put
                    { r with
                        Split =
                            Some
                                { Lowered = lowered
                                  Suffix = suffix
                                  Redacted = redacted
                                  Boundary = boundary } }
            | SessionOp.Fingerprinted(_, source, m, kind, reading, withheld, verdict, at) ->
                let previous = Map.tryFind source state.Readings
                let checkable, expected = expectedVerdict previous m reading withheld

                if checkable && verdict <> expected then
                    Error(SessionRejection.VerdictMismatch(r.Id, source, expected))
                else
                    let guard =
                        { Source = source
                          Method = m
                          Kind = kind
                          Reading = reading
                          Withheld = withheld
                          Verdict = verdict
                          At = at }

                    Ok
                        { state with
                            Readings = Map.add source guard state.Readings
                            Requests = Map.add r.Id { r with Guards = r.Guards @ [ guard ] } state.Requests }
            | SessionOp.Ran(_, statement, at) ->
                match statement.CaptureKey, unrecordable with
                | Some _, _ :: _ ->
                    Error(SessionRejection.DigestForm(r.Id, "no capture key: the parameter values are not recordable"))
                | None, [] when not (List.isEmpty statement.Parameters) ->
                    Error(SessionRejection.DigestForm(r.Id, "a capture key: the parameter values are recordable"))
                | _ ->
                    put
                        { r with
                            Statement = Some(statement, at)
                            Values =
                                if List.isEmpty statement.Parameters then
                                    Some(Recorded.Recorded [])
                                else
                                    None }
            | SessionOp.ValuesRecorded(_, values) ->
                if not (List.isEmpty unrecordable) then
                    Error(SessionRejection.NotRecordable(r.Id, unrecordable))
                else
                    put
                        { r with
                            Values = Some(Recorded.Recorded values) }
            | SessionOp.Withheld(_, part, classifications) ->
                let recorded = classifications |> List.distinct |> List.sort

                if List.isEmpty unrecordable || recorded <> unrecordable then
                    Error(SessionRejection.WithheldMismatch(r.Id, classifications, unrecordable))
                else
                    match part, r.Values, r.Returned with
                    | WithheldPart.Parameters, None, _ ->
                        put
                            { r with
                                Values = Some(Recorded.Withheld recorded) }
                    | WithheldPart.Rows, Some _, Some _ ->
                        put
                            { r with
                                Rows = Some(Recorded.Withheld recorded) }
                    | WithheldPart.Parameters, Some _, _
                    | WithheldPart.Rows, _, _ -> Error(SessionRejection.OutOfOrder(r.Id, tag, allowed))
            | SessionOp.Returned(_, rows, digest, at) ->
                if not (digestFits digest) then
                    Error(SessionRejection.DigestForm(r.Id, digestExpected))
                elif rows < 0 then
                    Error(SessionRejection.RowsMismatch(r.Id, "a negative row count"))
                else
                    put
                        { r with
                            Returned = Some(rows, digest, at) }
            | SessionOp.RowsRecorded(_, rows) ->
                let width =
                    r.Statement
                    |> Option.map (fun (s, _) -> List.length s.Columns)
                    |> Option.defaultValue 0

                let count = r.Returned |> Option.map (fun (n, _, _) -> n) |> Option.defaultValue 0

                if not (List.isEmpty unrecordable) then
                    Error(SessionRejection.NotRecordable(r.Id, unrecordable))
                elif List.length rows <> count then
                    Error(
                        SessionRejection.RowsMismatch(
                            r.Id,
                            sprintf "%d row(s) recorded; the statement returned %d" (List.length rows) count
                        )
                    )
                elif rows |> List.exists (fun row -> List.length row <> width) then
                    Error(SessionRejection.RowsMismatch(r.Id, sprintf "a row not %d cell(s) wide" width))
                else
                    put
                        { r with
                            Rows = Some(Recorded.Recorded rows) }
            | SessionOp.Finished(_, rows, digest, at) ->
                if not (digestFits digest) then
                    Error(SessionRejection.DigestForm(r.Id, digestExpected))
                elif rows < 0 then
                    Error(SessionRejection.RowsMismatch(r.Id, "a negative row count"))
                else
                    put
                        { r with
                            Outcome = Some(Outcome.Finished(rows, digest, at)) }
            | SessionOp.Failed(_, code, at) ->
                put
                    { r with
                        Outcome = Some(Outcome.Failed(code, at)) }

    /// The reducer. Pure and total: every refusal is a `SessionRejection`, never an exception.
    let apply (op: SessionOp) (state: SessionState) : Result<SessionState, SessionRejection> =
        match state.Header, op with
        | None, SessionOp.Opened h -> Ok { state with Header = Some h }
        | Some h, SessionOp.Opened _ -> Error(SessionRejection.AlreadyOpened h.Session)
        | None, other -> Error(SessionRejection.NotOpened(tag other))
        | Some _, SessionOp.SchemaReported(source, schema, drift, at) ->
            Ok
                { state with
                    Sources =
                        Map.add
                            source
                            { Schema = schema
                              Drift = drift
                              At = at }
                            state.Sources }
        | Some _, SessionOp.Received(request, table, pipeline, redacted, at) ->
            if Map.containsKey request state.Requests then
                Error(SessionRejection.DuplicateRequest request)
            else
                let record =
                    { Id = request
                      Table = table
                      Pipeline = pipeline
                      Redacted = redacted
                      ReceivedAt = at
                      Schemas = state.Sources
                      Verdict = None
                      Split = None
                      Guards = []
                      Statement = None
                      Values = None
                      Returned = None
                      Rows = None
                      Outcome = None }

                Ok
                    { state with
                        Requests = Map.add request record state.Requests
                        Order = state.Order @ [ request ] }
        | Some h, other ->
            match requestOf other with
            | None -> Error(SessionRejection.OutOfOrder("", tag other, []))
            | Some request ->
                match Map.tryFind request state.Requests with
                | None -> Error(SessionRejection.UnknownRequest(request, state.Order))
                | Some r -> applyToRequest h state r other

    /// Every op in order; the first refusal and its index.
    let applyAll (ops: SessionOp list) (state: SessionState) : Result<SessionState, int * SessionRejection> =
        ops
        |> List.indexed
        |> List.fold
            (fun acc (i, op) -> acc |> Result.bind (fun s -> apply op s |> Result.mapError (fun e -> i, e)))
            (Ok state)

    /// The rejection's stable code.
    let code (r: SessionRejection) : string =
        match r with
        | SessionRejection.NotOpened _ -> "DB-SESSION-NOT-OPENED"
        | SessionRejection.AlreadyOpened _ -> "DB-SESSION-ALREADY-OPENED"
        | SessionRejection.DuplicateRequest _ -> "DB-SESSION-DUPLICATE-REQUEST"
        | SessionRejection.UnknownRequest _ -> "DB-SESSION-UNKNOWN-REQUEST"
        | SessionRejection.OutOfOrder _ -> "DB-SESSION-OUT-OF-ORDER"
        | SessionRejection.NotRecordable _ -> "DB-SESSION-NOT-RECORDABLE"
        | SessionRejection.WithheldMismatch _ -> "DB-SESSION-WITHHELD-MISMATCH"
        | SessionRejection.DigestForm _ -> "DB-SESSION-DIGEST-FORM"
        | SessionRejection.VerdictMismatch _ -> "DB-SESSION-VERDICT-MISMATCH"
        | SessionRejection.RowsMismatch _ -> "DB-SESSION-ROWS-MISMATCH"

    /// The rejection as one sentence, naming what is admitted instead.
    let describe (r: SessionRejection) : string =
        let listed (xs: string list) =
            if List.isEmpty xs then "nothing" else String.concat ", " xs

        match r with
        | SessionRejection.NotOpened op -> sprintf "'%s' before the session opened; the first op is 'opened'" op
        | SessionRejection.AlreadyOpened s -> sprintf "session '%s' is already open" s
        | SessionRejection.DuplicateRequest q ->
            sprintf "request '%s' was already received; a request id is used once" q
        | SessionRejection.UnknownRequest(q, known) -> sprintf "no request '%s'; received: %s" q (listed known)
        | SessionRejection.OutOfOrder(q, op, expected) ->
            sprintf "'%s' is not admitted for request '%s' now; it admits: %s" op q (listed expected)
        | SessionRejection.NotRecordable(q, cs) ->
            sprintf "request '%s' reads %s, which the model does not mark recordable; withhold instead" q (listed cs)
        | SessionRejection.WithheldMismatch(q, recorded, unrecordable) ->
            sprintf
                "request '%s' withholds for %s; its unrecordable reads are %s"
                q
                (listed recorded)
                (listed unrecordable)
        | SessionRejection.DigestForm(q, expected) -> sprintf "request '%s': expected %s" q expected
        | SessionRejection.VerdictMismatch(q, source, expected) ->
            sprintf "request '%s': the verdict on source '%s' is not the comparison's (%A)" q source expected
        | SessionRejection.RowsMismatch(q, detail) -> sprintf "request '%s': %s" q detail
