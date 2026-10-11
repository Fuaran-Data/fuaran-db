namespace Fuaran.DB.Session

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider

/// What a host passes to run a statement: the statement and its bound values, the rows back in the
/// statement's column order, or an error the record does NOT carry (a driver's message can quote a
/// value; the record names the failure by code instead).
type Execute = Statement -> Result<Cell list list, string>

/// How a request's run ended, as the host sees it.
[<RequireQualifiedAccess>]
type RunFailure =
    /// The executor failed.
    | Execute of message: string
    /// The rows did not fit the statement.
    | Shape of message: string
    /// The local suffix failed.
    | Evaluate of EvalError
    /// A table carried a value with no canonical text (a non-finite float).
    | Unencodable of message: string

/// A recording session: the record, its folded state, and what the host holds beside it and never
/// publishes — the model, the dialect, the secret results are committed under, the true fingerprint
/// readings, and each request's admitted pipeline and lowering. Built only through `Session`.
type Session =
    private
        { records: OpRecord<SessionOp> list
          state: SessionState
          actor: Actor
          model: ModelNode
          dialect: Dialect
          secret: string
          truth: Map<string, FingerprintReading>
          admitted: Map<string, Admitted>
          lowered: Map<string, Lowered> }

    /// The record, oldest first.
    member s.Records = s.records
    /// The record folded.
    member s.State = s.state
    /// The record's head: the last record's hash, `""` before the first.
    member s.Head = OpStream.head s.records

/// The account a record gives of one request — answered from the stream alone, with no database.
type Account =
    {
        /// The request.
        Request: string
        /// The table it read.
        Table: string
        /// The pipeline as received, and how many of its literals were withheld.
        Pipeline: Transform list * int
        /// The policy's verdict: the rules applied and the pipeline as rewritten, or the refusal.
        Verdict: Verdict option
        /// The statement it ran.
        Statement: StatementRecord option
        /// The schemas the sources had reported when it arrived.
        Schemas: Map<string, SchemaEntry>
        /// The fingerprints that guarded the statement.
        Guards: Guard list
        /// The statement's row count and result digest.
        Returned: (int * ResultDigest) option
        /// The outcome.
        Outcome: Outcome option
        /// When it arrived, ran and ended.
        Received: string
        /// When the statement ran.
        Ran: string option
    }

/// What a replay of one request reached.
[<RequireQualifiedAccess>]
type ReplayVerdict =
    /// The local suffix, run again over the captured rows, reached the recorded result.
    | Reached of digest: string
    /// It reached another result.
    | Diverged of recorded: string * replayed: string
    /// The record cannot replay it: the rows were withheld, never returned, or it did not finish.
    | NotCaptured of reason: string
    /// The suffix failed on the captured rows.
    | Unevaluable of reason: string

/// The recorder, the reader, the capture and the replay (`domain:Fuaran.DB.Session`).
///
/// The host drives a request through four steps — `request` (the policy), `lower` (the provider),
/// `guard` (a fingerprint taken before the statement), `run` (the statement and the local suffix) —
/// and each appends what happened to the record. Nothing here opens a connection or reads a clock:
/// the host's executor runs the statement, and the host's clock readings arrive as text.
[<RequireQualifiedAccess>]
module Session =

    let private append (s: Session) (ops: SessionOp list) : Result<Session, SessionRejection> =
        ops
        |> List.fold
            (fun acc op ->
                acc
                |> Result.bind (fun (cur: Session) ->
                    OpStream.append SessionWire.hashFn SessionWire.streamWitness cur.actor op cur.state cur.records
                    |> Result.map (fun (state, records) ->
                        { cur with
                            state = state
                            records = records })))
            (Ok s)

    let private header (s: Session) : SessionHeader =
        match s.state.Header with
        | Some h -> h
        | None -> invalidOp "a session is opened when it is built"

    /// Why a step cannot run for a request now: the reducer's own answer, as it would refuse the op.
    let private notReady (s: Session) (id: string) (op: string) : SessionRejection =
        match Map.tryFind id s.state.Requests with
        | None -> SessionRejection.UnknownRequest(id, s.state.Order)
        | Some r -> SessionRejection.OutOfOrder(id, op, SessionOps.next r)

    let private evalCode (e: EvalError) : string =
        let case, _ =
            Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<EvalError>)

        "DB-SESSION-EVAL-" + case.Name.ToUpperInvariant()

    let private unrecordableOf (s: Session) (request: string) : string list =
        match Map.tryFind request s.state.Requests with
        | Some r -> SessionOps.unrecordable (header s).Recordable r
        | None -> []

    /// Open a session over `model`, speaking `dialect` at `dialectVersion`. `secret` is the value the
    /// host keeps and never publishes, under which a withheld result's digest is committed; it should
    /// be random and at least 128 bits, because the commitment is only as hard to test a guess against
    /// as the secret is to guess.
    let start
        (actor: Actor)
        (model: ModelNode)
        (dialect: Dialect)
        (dialectVersion: string)
        (sessionId: string)
        (secret: string)
        (at: string)
        : Result<Session, SessionRejection> =
        let recordable =
            Model.spec model
            |> Option.map _.Recordable
            |> Option.defaultValue []
            |> List.distinct
            |> List.sort

        let opened =
            SessionOp.Opened
                { Session = sessionId
                  Model = model.Id
                  ModelHash = SessionHash.model model
                  Recordable = recordable
                  Dialect = dialect.Name
                  DialectVersion = dialectVersion
                  ProviderVersion = SessionHash.providerVersion
                  At = at }

        append
            { records = OpStream.empty
              state = SessionOps.empty
              actor = actor
              model = model
              dialect = dialect
              secret = secret
              truth = Map.empty
              admitted = Map.empty
              lowered = Map.empty }
            [ opened ]

    /// Record a source's reported schema and its drift verdict against the model.
    let reportSchema (source: string) (reported: ReportedSchema) (at: string) (s: Session) =
        append s [ SessionOp.SchemaReported(source, reported, Drift.check s.model source reported, at) ]

    /// Receive a pipeline over `table` for `subject` and decide it: the policy runs, and the record
    /// gains the pipeline and the verdict. The pipeline and the rules are recorded with their literals
    /// withheld unless every classification the request reads is recordable; a refused request's
    /// reads are unknown, so its pipeline is always recorded withheld.
    let request
        (id: string)
        (subject: Subject)
        (table: string)
        (pipeline: Transform list)
        (at: string)
        (s: Session)
        : Result<Session * Result<Admitted, PolicyRefusal>, SessionRejection> =
        let recordable = (header s).Recordable
        let verdict = Policy.admit s.model subject (Ref table) pipeline

        let keep (reads: string list) =
            List.isEmpty (SessionOps.unrecordableReads recordable reads)

        match verdict with
        | Ok admitted ->
            let reads = Recording.reads s.model admitted.Applied

            let written, writtenRedacted, rules, rewritten, redacted =
                if keep reads then
                    pipeline, 0, admitted.Applied, admitted.Pipeline, 0
                else
                    let w, wn = Recording.redact pipeline
                    let rw, rn = Recording.redact admitted.Pipeline
                    let rules, n = Recording.redactRules rn admitted.Applied
                    w, wn, rules, rw, n

            append
                s
                [ SessionOp.Received(id, table, written, writtenRedacted, at)
                  SessionOp.Admitted(id, subject.Principal, reads, rules, rewritten, redacted) ]
            |> Result.map (fun s' ->
                { s' with
                    admitted = Map.add id admitted s'.admitted },
                verdict)
        | Error refusal ->
            let w, wn = Recording.redact pipeline

            append
                s
                [ SessionOp.Received(id, table, w, wn, at)
                  SessionOp.Refused(id, subject.Principal, Policy.code refusal) ]
            |> Result.map (fun s' -> s', verdict)

    /// Lower an admitted request through the session's dialect and record the split; a pipeline that
    /// does not lower at all is recorded as failed, by the refusal's tag.
    let lower
        (id: string)
        (bindings: Bindings)
        (at: string)
        (s: Session)
        : Result<Session * Result<Lowered, Refusal>, SessionRejection> =
        match Map.tryFind id s.admitted with
        | None -> Error(notReady s id "lowered")
        | Some admitted ->
            match FoldSplit.lower admitted s.dialect bindings with
            | Error r ->
                append s [ SessionOp.Failed(id, "DB-LOWER-" + Refusal.tag r, at) ]
                |> Result.map (fun s' -> s', Error r)
            | Ok lowered ->
                let suffix, redacted =
                    if List.isEmpty (unrecordableOf s id) then
                        lowered.Suffix, 0
                    else
                        Recording.redact lowered.Suffix

                let boundary = lowered.Split.Boundary |> Option.map Refusal.tag

                append s [ SessionOp.Lowered(id, lowered.Split.Lowered, suffix, redacted, boundary) ]
                |> Result.map (fun s' ->
                    { s' with
                        lowered = Map.add id lowered s'.lowered },
                    Ok lowered)

    /// Record a fingerprint of `source` read before request `id`'s statement and guarding it (the
    /// carried task 2008.t3): the method the model declares (`Fingerprint.ofSource`), its kind
    /// (`Fingerprint.disclose`), the reading — from `Probe.forSource` and `Probe.readTables`, or the
    /// host's file metadata — and `Fingerprint.compare`'s verdict against the session's previous
    /// reading of the source. The verdict is computed over the TRUE readings the session holds; the
    /// record withholds a change value whose column is not recordable. `Error None` when the model
    /// declares no fingerprint for the source.
    let guard
        (id: string)
        (source: string)
        (reading: FingerprintReading)
        (at: string)
        (s: Session)
        : Result<Session, SessionRejection option> =
        match Fingerprint.ofSource source s.model with
        | None -> Error None
        | Some m ->
            let verdict =
                Map.tryFind source s.truth
                |> Option.map (fun before -> Fingerprint.compare m before reading)

            let recorded, withheld =
                Recording.fingerprint s.model (header s).Recordable m reading

            append s [ SessionOp.Fingerprinted(id, source, m, Fingerprint.kind m, recorded, withheld, verdict, at) ]
            |> Result.map (fun s' ->
                { s' with
                    truth = Map.add source reading s'.truth })
            |> Result.mapError Some

    /// Run a lowered request: the statement through `execute`, then the local suffix in the reference
    /// evaluator over its result. The record gains the statement by hash with its parameter types, the
    /// values or their withholding, the row count and digest, the rows or their withholding, and the
    /// outcome. `now` is the host's clock, read before the statement and after it returns and after
    /// the suffix; `resolve` answers any other source the suffix names.
    let run
        (id: string)
        (execute: Execute)
        (now: unit -> string)
        (resolve: string -> Result<Table, EvalError>)
        (bindings: Bindings)
        (s: Session)
        : Result<Session * Result<Table, RunFailure>, SessionRejection> =
        match Map.tryFind id s.lowered with
        | None -> Error(notReady s id "ran")
        | Some lowered ->
            let h = header s
            let statement = lowered.Statement
            let hash = SessionHash.statement (SessionHash.identityOf h statement)
            let values = statement.Parameters |> List.map (fun p -> p.Marker, p.Value)
            let unrecordable = unrecordableOf s id
            let recordable = List.isEmpty unrecordable
            let secret = if recordable then None else Some s.secret

            let record =
                { Hash = hash
                  Text = statement.Text
                  Parameters = statement.Parameters |> List.map (fun p -> p.Marker, p.Type)
                  Columns = statement.Columns |> List.map (fun o -> o.Name, o.Type)
                  TimeoutSeconds = statement.TimeoutSeconds
                  CaptureKey =
                    if recordable && not (List.isEmpty values) then
                        Some(SessionHash.captureKey hash values)
                    else
                        None }

            let valueOps =
                match values, recordable with
                | [], _ -> []
                | _, true -> [ SessionOp.ValuesRecorded(id, values) ]
                | _, false -> [ SessionOp.Withheld(id, WithheldPart.Parameters, unrecordable) ]

            let fail (s': Session) (code: string) (failure: RunFailure) =
                append s' [ SessionOp.Failed(id, code, now ()) ]
                |> Result.map (fun s'' -> s'', Error failure)

            append s (SessionOp.Ran(id, record, now ()) :: valueOps)
            |> Result.bind (fun s1 ->
                match execute statement with
                | Error message -> fail s1 "DB-SESSION-EXECUTE-FAILED" (RunFailure.Execute message)
                | Ok rows ->
                    match Statement.toTable statement rows with
                    | Error e -> fail s1 "DB-SESSION-RESULT-SHAPE" (RunFailure.Shape(ColumnCodec.errorString e))
                    | Ok table ->
                        match SessionHash.tableText table with
                        | Error e -> fail s1 "DB-SESSION-RESULT-UNENCODABLE" (RunFailure.Unencodable e)
                        | Ok text ->
                            let rowOps =
                                if recordable then
                                    [ SessionOp.RowsRecorded(id, rows) ]
                                else
                                    [ SessionOp.Withheld(id, WithheldPart.Rows, unrecordable) ]

                            append
                                s1
                                (SessionOp.Returned(id, List.length rows, SessionHash.result secret text, now ())
                                 :: rowOps)
                            |> Result.bind (fun s2 ->
                                match FoldSplit.finish lowered resolve bindings table with
                                | Error e -> fail s2 (evalCode e) (RunFailure.Evaluate e)
                                | Ok final ->
                                    match SessionHash.tableText final with
                                    | Error e -> fail s2 "DB-SESSION-RESULT-UNENCODABLE" (RunFailure.Unencodable e)
                                    | Ok finalText ->
                                        append
                                            s2
                                            [ SessionOp.Finished(
                                                  id,
                                                  Table.rowCount final,
                                                  SessionHash.result secret finalText,
                                                  now ()
                                              ) ]
                                        |> Result.map (fun s3 -> s3, Ok final)))

    /// The account of one request, from a folded record alone.
    let account (state: SessionState) (request: string) : Account option =
        Map.tryFind request state.Requests
        |> Option.map (fun r ->
            { Request = r.Id
              Table = r.Table
              Pipeline = r.Pipeline, r.Redacted
              Verdict = r.Verdict
              Statement = r.Statement |> Option.map fst
              Schemas = r.Schemas
              Guards = r.Guards
              Returned = r.Returned |> Option.map (fun (n, d, _) -> n, d)
              Outcome = r.Outcome
              Received = r.ReceivedAt
              Ran = r.Statement |> Option.map snd })

    /// The captured results of a record, by capture key: each statement whose values and rows the
    /// record holds.
    let captures (state: SessionState) : Map<string, StatementRecord * Cell list list> =
        state.Order
        |> List.choose (fun id ->
            let r = state.Requests.[id]

            match r.Statement, r.Rows with
            | Some(st, _), Some(Recorded.Recorded rows) ->
                // a statement with no parameters is keyed by its hash alone
                let key = st.CaptureKey |> Option.defaultValue (st.Hash + "#")
                Some(key, (st, rows))
            | _ -> None)
        |> Map.ofList

    /// An executor that answers from a record's captures and never reaches a database: the same
    /// statement with the same values gets the captured rows, byte for byte; anything else is refused
    /// by name. A host swaps it in for its real executor to re-run a session.
    let replayExecutor (state: SessionState) : Execute =
        let captured = captures state

        let header =
            match state.Header with
            | Some h -> h
            | None -> invalidArg "state" "the record never opened"

        fun (statement: Statement) ->
            let hash = SessionHash.statement (SessionHash.identityOf header statement)

            let key =
                match statement.Parameters with
                | [] -> hash + "#"
                | ps -> SessionHash.captureKey hash (ps |> List.map (fun p -> p.Marker, p.Value))

            match Map.tryFind key captured with
            | Some(_, rows) -> Ok rows
            | None -> Error("DB-SESSION-CAPTURE-MISS: the record holds no capture for " + key)

    /// Replay one request from the record alone: rebuild its statement's result from the captured rows,
    /// run the recorded local suffix again in the reference evaluator, and compare the digest it
    /// reaches with the recorded one. No executor is taken, so no database can be reached; a suffix
    /// that names another source is refused rather than resolved.
    let replay (state: SessionState) (request: string) : ReplayVerdict =
        match Map.tryFind request state.Requests with
        | None -> ReplayVerdict.NotCaptured "no such request"
        | Some r ->
            match r.Statement, r.Rows, r.Split, r.Outcome with
            | Some(st, _),
              Some(Recorded.Recorded rows),
              Some split,
              Some(Outcome.Finished(_, ResultDigest.Plain recorded, _)) ->
                let columns =
                    st.Columns
                    |> List.mapi (fun j (name, ty) -> Column.create name ty (rows |> List.map (List.item j)))

                let table =
                    { Schema = st.Columns
                      Columns = columns }

                let refuse name = Error(EvalError.UnresolvedSource name)

                match Table.validate table with
                | Error e -> ReplayVerdict.Unevaluable(ColumnCodec.errorString e)
                | Ok() ->
                    match DataFrame.evalPipelineWithInEnv refuse Map.empty split.Suffix table with
                    | Error e -> ReplayVerdict.Unevaluable(evalCode e)
                    | Ok final ->
                        match SessionHash.tableText final with
                        | Error e -> ReplayVerdict.Unevaluable e
                        | Ok text ->
                            let replayed = SessionHash.digest text

                            if replayed = recorded then
                                ReplayVerdict.Reached recorded
                            else
                                ReplayVerdict.Diverged(recorded, replayed)
            | _, Some(Recorded.Withheld cs), _, _ ->
                ReplayVerdict.NotCaptured("the rows were withheld: " + String.concat ", " cs)
            | _, _, _, Some(Outcome.Failed(code, _)) -> ReplayVerdict.NotCaptured("the request failed: " + code)
            | _ -> ReplayVerdict.NotCaptured "the request did not finish"
