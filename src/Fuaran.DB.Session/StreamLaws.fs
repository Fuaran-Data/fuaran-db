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
          Columns = [ Field.create "a" IntType ]
          TimeoutSeconds = Some 30
          CaptureKey = if keyed then Some(SessionHash.digest "s" + "#k") else None }

    /// A pipeline joining the grouped operand `o` on `a`.
    let private joinsOperand: Transform list = [ Join(Ref "o", [ "a", "a" ], Left) ]

    /// The grouped operand `o` over `t`, grouped on `a`, filtered by `n`.
    let private operandOf (n: int) : OperandRecord =
        { Name = "o"
          Table = "t"
          Pipeline =
            [ Filter(Binary(Gt, Col "b", Lit(Int n)))
              GroupBy([ "a" ], [ { Name = "b"; Fn = Max; Of = "b" } ]) ]
          Redacted = 0 }

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
            | 1 ->
                // half the requests join a grouped operand, so the chain mixes both shapes
                if flip = 0 then
                    SessionOp.Received(request, "t", [ Filter(Binary(Gt, Col "a", Lit(Int n))) ], 0, [], at)
                else
                    SessionOp.Received(request, "t", joinsOperand, 0, [ operandOf n ], at)
            | 2 ->
                SessionOp.Admitted(
                    request,
                    "p",
                    (if flip = 0 then [ "public" ] else [ "public"; "personal" ]),
                    [],
                    (if n = 0 then [] else joinsOperand),
                    flip,
                    (if n = 0 then [] else [ operandOf n ])
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
        | SessionRejection.OperandMismatch _ -> text.Contains "record exactly the operands"

    let private rowsText (rows: Cell list list) : string =
        Canonical.write Rendering.CrossHost (JArr(rows |> List.map (fun r -> JArr(r |> List.map SessionWire.cellJson))))

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

    /// The recorded operands law (Phase 36, DECISIONS.md D17): a request's `Received` and `Admitted`
    /// fold exactly when each records the operands its pipeline's joins name among those received, in
    /// join order, each reading the table it was received with. Drawn over requests joining zero, one
    /// or two of three named operands, recorded honestly or with one operand dropped, added, reordered
    /// or moved to another table; the sample must reach both an admitted operand and a refused record.
    let operandLaw (seed: int) (iterations: int) : LawResult =
        let names = [ "o1"; "o2"; "o3" ]

        let operand (name: string) (table: string) : OperandRecord =
            { Name = name
              Table = table
              Pipeline = [ GroupBy([ "a" ], [ { Name = "b"; Fn = Max; Of = "b" } ]) ]
              Redacted = 0 }

        let rec draw (i: int) (rng: ConfRng.T) (admitted: int) (refused: int) : string option =
            if i >= iterations then
                if admitted = 0 then
                    Some "the sample reached no admitted request joining an operand"
                elif refused = 0 then
                    Some "the sample reached no refused operand record"
                else
                    None
            else
                let k, r = ConfRng.intBelow 3 rng

                let picked, r =
                    ConfRng.choose (List.permute (fun j -> (j + i) % 3) names |> List.pairwise) r

                let joinedNames = [ fst picked; snd picked ] |> List.truncate k
                let pipeline = joinedNames |> List.map (fun n -> Join(Ref n, [ "a", "a" ], Left))
                let honest = joinedNames |> List.map (fun n -> operand n "t")
                let how, r = ConfRng.intBelow 5 r

                let recorded =
                    match how, honest with
                    | 1, _ :: rest -> rest
                    | 2, _ -> honest @ [ operand "o3" "t" ]
                    | 3, a :: b :: _ -> [ b; a ]
                    | 4, o :: rest -> { o with Table = "u" } :: rest
                    | _ -> honest

                let expected = recorded = honest

                let folded =
                    apply
                        [ SessionOp.Received("q", "t", pipeline, 0, honest, "t0")
                          SessionOp.Admitted("q", "p", [ "public" ], [], pipeline, 0, recorded) ]

                let ok = Result.isOk folded

                if ok <> expected then
                    Some(
                        sprintf
                            "joins %A recorded as %A: the reducer %s"
                            joinedNames
                            (recorded |> List.map (fun o -> o.Name, o.Table))
                            (if ok then "admitted it" else "refused it")
                    )
                else
                    let admitted' =
                        if ok && not (List.isEmpty honest) then
                            admitted + 1
                        else
                            admitted

                    let refused' = if ok then refused else refused + 1
                    draw (i + 1) r admitted' refused'

        and apply (ops: SessionOp list) =
            SessionOps.applyAll ops state0 |> Result.map ignore

        LawResult.subject
            "session-operands"
            "a request records exactly the grouped operands its pipeline's joins name, in join order, each on its own table"
            iterations
            (draw 0 (ConfRng.ofSeed seed) 0 0)

    /// Every law, over `iterations` draws from `seed`.
    let laws (seed: int) (iterations: int) : LawResult list =
        (Conformance.certifyStream SessionWire.streamWitness streamGen SessionWire.hashFn seed iterations).Results
        @ Conformance.reducer SessionOps.apply streamGen (Some namesAlternatives) seed iterations
        @ Conformance.captureReplayLaws rowsText rowsOf drawRows SessionWire.hashFn seed iterations
        @ [ operandLaw seed iterations ]
