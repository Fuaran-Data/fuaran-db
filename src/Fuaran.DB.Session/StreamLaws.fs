namespace Fuaran.DB.Session

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

/// The connection record's laws (`validator:Fuaran.DB.Session.StreamLaws`): Core's stream laws over
/// the session witness (an intact chain verifies, a tampered op is detected, replay re-derives the
/// live state, the JSONL round trip verifies), Core's reducer laws (totality, replay determinism,
/// refusals that name what is admitted instead), and Core's capture-replay laws over the captured
/// rows' codec (record-then-replay is byte-identical, tampering is detected). Each family carries
/// Core's sample-adequacy guards, so a generator that never reached the case a law reads is red.
[<RequireQualifiedAccess>]
module StreamLaws =

    /// The state the generator draws against: a session opened over a model that marks `public`
    /// recordable and `personal` not.
    let state0: SessionState =
        { SessionOps.empty with
            Header =
                Some
                    { Session = "laws"
                      Model = "m:laws"
                      ModelHash = SessionHash.digest "laws"
                      Recordable = [ "public" ]
                      Dialect = "reference"
                      DialectVersion = "1"
                      ProviderVersion = SessionHash.providerVersion
                      At = "2026-01-01T00:00:00Z" } }

    let private pick (rng: ConfRng.T) (xs: 'a list) : 'a * ConfRng.T = ConfRng.choose xs rng

    let private statementOf (withParams: bool) (keyed: bool) : StatementRecord =
        { Hash = SessionHash.digest "s"
          Text = "SELECT a FROM t WHERE a = @p0"
          Parameters = if withParams then [ "@p0", IntType ] else []
          Columns = [ "a", IntType ]
          TimeoutSeconds = Some 30
          CaptureKey = if keyed then Some(SessionHash.digest "s" + "#k") else None }

    /// One op drawn over five requests and every op kind, so a chain mixes ops the reducer admits with
    /// ops it refuses.
    let genOp (rng: ConfRng.T) : SessionOp * ConfRng.T =
        let request, r = pick rng [ "q0"; "q1"; "q2"; "q3"; "q4" ]
        let which, r = ConfRng.intBelow 14 r
        let flip, r = ConfRng.intBelow 2 r
        let n, r = ConfRng.intBelow 3 r
        let personal = [ "personal" ]
        let at = sprintf "2026-01-01T00:00:%02dZ" n

        let op =
            match which with
            | 0 ->
                SessionOp.SchemaReported(
                    "s",
                    { Tables =
                        [ { Name = "t"
                            Columns = [ Field.create "a" IntType ] } ] },
                    (if flip = 0 then None else Some [ TableDrift.InStep "t" ]),
                    at
                )
            | 1 -> SessionOp.Received(request, "t", [ Filter(Binary(Gt, Col "a", Lit(Int n))) ], 0, at)
            | 2 ->
                SessionOp.Admitted(
                    request,
                    "p",
                    (if flip = 0 then [ "public" ] else [ "public"; "personal" ]),
                    [],
                    [],
                    flip
                )
            | 3 -> SessionOp.Refused(request, "p", "DB-GRANT-NONE")
            | 4 -> SessionOp.Lowered(request, n, [], 0, None)
            | 5 ->
                SessionOp.Fingerprinted(
                    request,
                    "s",
                    FingerprintMethod.ChangeCounter,
                    FingerprintKind.Exact,
                    FingerprintReading.Counter(string n),
                    [],
                    (if flip = 0 then None else Some FingerprintVerdict.Unchanged),
                    at
                )
            | 6 -> SessionOp.Ran(request, statementOf (n > 0) (flip = 0), at)
            | 7 -> SessionOp.ValuesRecorded(request, [ "@p0", Int n ])
            | 8 ->
                SessionOp.Returned(
                    request,
                    n,
                    (if flip = 0 then
                         ResultDigest.Plain "p"
                     else
                         ResultDigest.Committed "c"),
                    at
                )
            | 9 -> SessionOp.RowsRecorded(request, List.replicate n [ Int n ])
            | 10 ->
                SessionOp.Withheld(
                    request,
                    (if flip = 0 then
                         WithheldPart.Parameters
                     else
                         WithheldPart.Rows),
                    personal
                )
            | 11 ->
                SessionOp.Finished(
                    request,
                    n,
                    (if flip = 0 then
                         ResultDigest.Plain "p"
                     else
                         ResultDigest.Committed "c"),
                    at
                )
            | 12 -> SessionOp.Failed(request, "DB-SESSION-EXECUTE-FAILED", at)
            | _ -> SessionOp.Opened(state0.Header.Value)

        op, r

    /// The generator Core's stream laws draw from.
    let streamGen: StreamGen<SessionOp, SessionState> = { State0 = state0; Op = genOp }

    /// Does a refusal name what is admitted instead? An out-of-order op names the ops its request
    /// admits, an unknown request names those received, and every other refusal names its remedy.
    let namesAlternatives (r: SessionRejection) : bool =
        let text = SessionOps.describe r

        match r with
        | SessionRejection.OutOfOrder _ -> text.Contains "it admits:"
        | SessionRejection.UnknownRequest _ -> text.Contains "received:"
        | SessionRejection.NotOpened _
        | SessionRejection.AlreadyOpened _
        | SessionRejection.DuplicateRequest _
        | SessionRejection.NotRecordable _
        | SessionRejection.WithheldMismatch _
        | SessionRejection.DigestForm _
        | SessionRejection.VerdictMismatch _
        | SessionRejection.RowsMismatch _ -> text <> ""

    let private rowsText (rows: Cell list list) : string =
        Canon.render (JArr(rows |> List.map (fun r -> JArr(r |> List.map SessionWire.cellJson))))

    let private rowsOf (text: string) : Result<Cell list list, string> =
        match Json.parse text with
        | Ok(JArr rows) ->
            rows
            |> List.fold
                (fun acc row ->
                    acc
                    |> Result.bind (fun done' ->
                        match row with
                        | JArr cells ->
                            cells
                            |> List.fold
                                (fun a c ->
                                    a
                                    |> Result.bind (fun xs ->
                                        SessionWire.cellOf "$" c
                                        |> Result.map (fun x -> xs @ [ x ])
                                        |> Result.mapError ModelWire.describe))
                                (Ok [])
                            |> Result.map (fun r -> done' @ [ r ])
                        | _ -> Error "a row is not an array"))
                (Ok [])
        | Ok _ -> Error "the rows are not an array"
        | Error e -> Error e

    let private drawRows (rng: ConfRng.T) : Cell list list * ConfRng.T =
        let n, r = ConfRng.intBelow 4 rng
        let v, r = ConfRng.intBelow 1000 r

        let cells =
            [ Int v
              Float(float v / 8.0)
              Str(sprintf "s%d" v)
              Bool(v % 2 = 0)
              Decimal(sprintf "%d.25" v)
              Date "2026-01-02"
              Timestamp "2026-01-02T03:04:05Z"
              Null ]

        List.init n (fun i -> cells |> List.permute (fun j -> (j + i) % cells.Length)), r

    /// Every law, over `iterations` draws from `seed`.
    let laws (seed: int) (iterations: int) : LawResult list =
        (Conformance.certifyStream SessionWire.streamWitness streamGen SessionWire.hashFn seed iterations).Results
        @ Conformance.reducer SessionOps.apply streamGen (Some namesAlternatives) seed iterations
        @ Conformance.captureReplayLaws rowsText rowsOf drawRows SessionWire.hashFn seed iterations
