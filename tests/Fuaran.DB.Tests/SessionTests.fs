/// The connection record (Phase 1903): the op vocabulary and its laws, the statement hash, the
/// recording rule read off the emitted wire, the capture and the replay, the reader's refusals, the
/// fingerprint guarding each statement (the carried task 2008.t3), and the export a witness signs.
module Fuaran.DB.Tests.SessionTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider
open Fuaran.DB.Session
open Fuaran.DB.Tests.Sample

// ---- the worked session ----

/// Values the record must never carry: a classified address in the source, a literal compared with
/// it in a request, and the classified change column's maximum.
let private secretEmail = "alice.secret@example.org"
let private needle = "needle.literal@example.org"
let private secretChange = "2031-07-14T09:26:53Z"

let private withChange (cls: string) =
    table
        "t:customers"
        "customers"
        [ col "c:customers.customer_id" "customer_id" IntType "public"
          col "c:customers.email" "email" StringType "personal"
          col "c:customers.updated_at" "updated_at" (TimestampType TimeUnit.Seconds) cls ]

/// The worked model: orders are public, a customer's address is personal, and the model marks only
/// `public` recordable. `customerChange` classifies the customers' change column.
let modelWith (recordable: string list) (customerChange: string) : ModelNode =
    { Id = "m:shop"
      Kind =
        NodeKind.Model
            { Name = "shop"
              Classifications = [ "public"; "personal" ]
              Recordable = recordable }
      Children =
        [ { Id = "s:shop"
            Kind =
              NodeKind.Source
                  { Name = "shop"
                    Fingerprint =
                      Some(
                          FingerprintMethod.RowCountMaxChange
                              [ "t:orders", "c:orders.updated_at"; "t:customers", "c:customers.updated_at" ]
                      ) }
            Children =
              [ table
                    "t:orders"
                    "orders"
                    [ col "c:orders.order_id" "order_id" IntType "public"
                      col "c:orders.customer_id" "customer_id" IntType "public"
                      col "c:orders.amount" "amount" IntType "public"
                      col "c:orders.channel" "channel" StringType "public"
                      col "c:orders.updated_at" "updated_at" (TimestampType TimeUnit.Seconds) "public" ]
                withChange customerChange ] }
          leaf
              "l:cost"
              (NodeKind.Limit
                  { Name = "cost"
                    MaxRows = 1000
                    MaxJoins = Some 1
                    MaxGroupKeys = Some 2
                    MinGroupSize = None
                    TimeoutSeconds = Some 30 }) ] }

let model = modelWith [ "public" ] "personal"

let private analyst: Subject =
    { Principal = "analyst"
      Attributes = Map.empty
      Grants =
        [ { Classification = "public"
            Level = GrantLevel.RowLevel }
          { Classification = "personal"
            Level = GrantLevel.RowLevel } ] }

let private tableOf (schema: (string * ColumnType) list) (rows: Cell list list) : Table =
    { Schema = Sample.fields schema
      Columns =
        schema
        |> List.mapi (fun j (n, ty) -> Sample.column n ty (rows |> List.map (List.item j))) }

/// The source as an engine holds it, by model table name.
let private held: Map<string, Table> =
    Map.ofList
        [ "orders",
          tableOf
              [ "order_id", IntType
                "customer_id", IntType
                "amount", IntType
                "channel", StringType
                "updated_at", (TimestampType TimeUnit.Seconds) ]
              [ [ Int 1; Int 7; Int 40; Str "web"; Timestamp "2026-10-01T08:00:00Z" ]
                [ Int 2; Int 7; Int 5; Str "shop"; Timestamp "2026-10-02T08:00:00Z" ]
                [ Int 3; Int 8; Int 25; Str "web"; Timestamp "2026-10-03T08:00:00Z" ]
                [ Int 4; Int 9; Int 60; Str "retail"; Timestamp "2026-10-04T08:00:00Z" ] ]
          "customers",
          tableOf
              [ "customer_id", IntType
                "email", StringType
                "updated_at", (TimestampType TimeUnit.Seconds) ]
              [ [ Int 7; Str secretEmail; Timestamp secretChange ]
                [ Int 8; Str needle; Timestamp "2026-10-05T08:00:00Z" ] ] ]

/// The engine: the reference dialect's interpreter over the held tables, counting its calls.
let private engine (m: ModelNode) (calls: int ref) : Execute =
    let tables = PlacementLaws.physicalTables m held

    fun statement ->
        calls.Value <- calls.Value + 1
        ReferenceDialect.execute statement tables |> Result.mapError (sprintf "%A")

/// The host's clock: a fresh sequence of instants per session.
let private clock () : unit -> string =
    let n = ref 0

    fun () ->
        n.Value <- n.Value + 1
        sprintf "2026-10-11T10:00:%02dZ" n.Value

let private actor = Actor.Human "host"

/// The public request: orders over a threshold, newest first, then a local filter the reference
/// dialect refuses to lower (pattern matching), so the run has a suffix to evaluate.
let publicPipeline (threshold: int) : Transform list =
    [ Filter(Binary(Gt, Col "amount", Lit(Int threshold)))
      Sort [ Slot.Lit "order_id", Asc ]
      Limit(Slot.Lit 100, Slot.Lit 0)
      Filter(Binary(StartsWith, Col "channel", Lit(Str "w"))) ]

/// The personal request: a customer by address.
let personalPipeline: Transform list =
    [ Filter(Binary(Eq, Col "email", Lit(Str needle)))
      Sort [ Slot.Lit "customer_id", Asc ]
      Limit(Slot.Lit 10, Slot.Lit 0) ]

let private ok (what: string) (r: Result<'a, 'e>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: %A" what e

let private readingOf (m: ModelNode) (orders: Table) (customers: Table) : FingerprintReading =
    let answer (t: Table) (changeAt: string) =
        let col = t.Columns |> List.find (fun c -> c.Name = changeAt)

        let max =
            (Column.toCells col)
            |> List.filter (fun c -> not (Cell.isNull c))
            |> List.sortWith (fun a b -> Cell.compare a b |> Option.defaultValue 0)
            |> List.tryLast
            |> Option.defaultValue Null

        [ Int(Table.rowCount t); max ]

    match Probe.forSource ReferenceDialect.dialect "s:shop" m with
    | Ok(Probe.Tables probes) ->
        Probe.readTables probes [ answer orders "updated_at"; answer customers "updated_at" ]
        |> ok "readTables"
    | other -> failtestf "expected table probes, got %A" other

/// Drive the worked session: a schema report, the public request guarded by a fingerprint and run,
/// a second fingerprint, the personal request run, and a refused request. `execute` is the engine,
/// or a record's replay executor.
let drive (m: ModelNode) (execute: Execute) : Session =
    let now = clock ()

    let schema: ReportedSchema =
        { Tables =
            [ { Name = "dbo.orders"
                Columns =
                  Sample.fields
                      [ "ORDER_ID", IntType
                        "CUSTOMER_ID", IntType
                        "AMOUNT", IntType
                        "CHANNEL", StringType
                        "UPDATED_AT", TimestampType TimeUnit.Seconds ] }
              { Name = "dbo.customers"
                Columns =
                  Sample.fields
                      [ "CUSTOMER_ID", IntType
                        "EMAIL", StringType
                        "UPDATED_AT", (TimestampType TimeUnit.Seconds) ] } ] }

    let resolve name = Error(EvalError.UnresolvedSource name)

    let s0 =
        Session.start actor m ReferenceDialect.dialect "1.0" "session-1" "a-secret-the-host-keeps" (now ())
        |> ok "start"

    let s1 = s0 |> Session.reportSchema "shop" schema (now ()) |> ok "schema"

    let s2, _ =
        s1
        |> Session.request "q1" analyst "orders" (publicPipeline 10) (now ())
        |> ok "request q1"

    let s3, _ = s2 |> Session.lower "q1" Bindings.none (now ()) |> ok "lower q1"

    let s4 =
        s3
        |> Session.guard "q1" "s:shop" (readingOf m held.["orders"] held.["customers"]) (now ())
        |> ok "guard q1"

    let s5, _ = s4 |> Session.run "q1" execute now resolve Bindings.none |> ok "run q1"

    let s6, _ =
        s5
        |> Session.request "q2" analyst "customers" personalPipeline (now ())
        |> ok "request q2"

    let s7, _ = s6 |> Session.lower "q2" Bindings.none (now ()) |> ok "lower q2"

    let s8 =
        s7
        |> Session.guard "q2" "s:shop" (readingOf m held.["orders"] held.["customers"]) (now ())
        |> ok "guard q2"

    let s9, _ = s8 |> Session.run "q2" execute now resolve Bindings.none |> ok "run q2"

    let nobody =
        { analyst with
            Principal = "nobody"
            Grants = [] }

    let s10, _ =
        s9
        |> Session.request "q3" nobody "orders" (publicPipeline 1) (now ())
        |> ok "request q3"

    s10

let private opsOf (s: Session) = s.Records |> List.map _.Op

let private seed = 19030
let private iterations = 300

let private hmac (key: string) : Signer =
    { KeyId = "host-key"
      Sign = fun bytes -> (new HMACSHA256(Encoding.UTF8.GetBytes key)).ComputeHash bytes }

[<Tests>]
let tests =
    testList
        "connection record"
        [ testCase "Core's stream, reducer and capture-replay laws hold at the session witness"
          <| fun _ ->
              let laws = StreamLaws.laws seed iterations
              printfn "census (session): %d laws, %d held" laws.Length (laws |> List.filter _.Passed |> List.length)

              for l in laws do
                  printfn "  [%s] %s" (if l.Passed then "held" else "FAILED") l.Law

              Expect.isGreaterThanOrEqual laws.Length 9 "every family ran"

              for l in laws do
                  Expect.isTrue l.Passed (sprintf "%s: %A" l.Law l.Counterexample)

          testCase
              "the stream laws are not vacuous: a reducer that admits everything breaks replay determinism or the chain"
          <| fun _ ->
              // falsifier: a witness whose encoding drops the request id cannot tell two tampered ops
              // apart, so the tamper law goes red
              let lossy =
                  { SessionWire.streamWitness with
                      Encode = fun op -> SessionOps.tag op }

              let laws =
                  Conformance.streamLaws lossy StreamLaws.streamGen SessionWire.hashFn seed 100

              Expect.isTrue (laws |> List.exists (fun l -> not l.Passed)) "a lossy encoding is caught"

          testCase "the statement hash: two hosts given the same five inputs produce one hash, and each input moves it"
          <| fun _ ->
              let hashOf (s: Session) (request: string) =
                  (Session.account s.State request |> Option.get).Statement
                  |> Option.get
                  |> _.Hash

              // two hosts, two sessions, the same model, dialect, versions and pipeline
              let a = drive model (engine model (ref 0))
              let b = drive model (engine model (ref 0))
              Expect.equal (hashOf a "q1") (hashOf b "q1") "one statement, one hash"

              // a different literal is a different VALUE, not a different statement
              let now = clock ()

              let other =
                  Session.start actor model ReferenceDialect.dialect "1.0" "session-2" "k" (now ())
                  |> ok "start"
                  |> Session.request "q1" analyst "orders" (publicPipeline 30) (now ())
                  |> ok "request"
                  |> fst
                  |> Session.lower "q1" Bindings.none (now ())
                  |> ok "lower"
                  |> fst
                  |> Session.run
                      "q1"
                      (engine model (ref 0))
                      now
                      (fun n -> Error(EvalError.UnresolvedSource n))
                      Bindings.none
                  |> ok "run"
                  |> fst

              Expect.equal (hashOf other "q1") (hashOf a "q1") "the values are not in the hash"

              let identity: StatementIdentity =
                  { Text = "SELECT \"A\" FROM \"T\" WHERE \"A\" > @p0"
                    Parameters = [ "@p0", IntType ]
                    ModelHash = SessionHash.model model
                    Dialect = "reference"
                    DialectVersion = "1.0"
                    ProviderVersion = SessionHash.providerVersion }

              let h = SessionHash.statement identity
              Expect.equal (SessionHash.statement { identity with Text = identity.Text }) h "pure"

              let moved =
                  [ { identity with
                        Text = identity.Text + " " }
                    { identity with
                        Parameters = [ "@p1", IntType ] }
                    { identity with
                        Parameters = [ "@p0", DecimalType ] }
                    { identity with
                        ModelHash = SessionHash.model (modelWith [] "personal") }
                    { identity with Dialect = "sqlserver" }
                    { identity with DialectVersion = "1.1" }
                    { identity with
                        ProviderVersion = "9.9.9" } ]

              for m in moved do
                  Expect.notEqual (SessionHash.statement m) h (sprintf "%A moves the hash" m)

              Expect.stringStarts h "sha256:" "a typed digest"
              Expect.equal h.Length (7 + 64) "64 hex digits"

          testCase "the recording rule: values only under a recordable classification, and a refusal to record is an op"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let ops = opsOf s

              let has (f: SessionOp -> bool) = ops |> List.exists f

              // the public request records its values, its rows and plain digests, and has a capture key
              Expect.isTrue
                  (has (fun o ->
                      match o with
                      | SessionOp.ValuesRecorded("q1", _ :: _) -> true
                      | _ -> false))
                  "q1's values are recorded"

              Expect.isTrue
                  (has (fun o ->
                      match o with
                      | SessionOp.RowsRecorded("q1", _) -> true
                      | _ -> false))
                  "q1's rows are recorded"

              Expect.isTrue
                  (has (fun o ->
                      match o with
                      | SessionOp.Returned("q1", _, ResultDigest.Plain _, _) -> true
                      | _ -> false))
                  "q1's digest is plain"

              let q1 = (Session.account s.State "q1").Value
              Expect.isSome q1.Statement.Value.CaptureKey "q1 is captured"

              // the personal request withholds both, by ops naming the classification, and commits its digests
              Expect.isTrue
                  (has (fun o -> o = SessionOp.Withheld("q2", WithheldPart.Parameters, [ "personal" ])))
                  "q2's values withheld"

              Expect.isTrue
                  (has (fun o -> o = SessionOp.Withheld("q2", WithheldPart.Rows, [ "personal" ])))
                  "q2's rows withheld"

              Expect.isFalse
                  (has (fun o ->
                      match o with
                      | SessionOp.ValuesRecorded("q2", _)
                      | SessionOp.RowsRecorded("q2", _) -> true
                      | _ -> false))
                  "nothing of q2 recorded"

              Expect.isTrue
                  (has (fun o ->
                      match o with
                      | SessionOp.Finished("q2", _, ResultDigest.Committed _, _) -> true
                      | _ -> false))
                  "q2's digest is committed"

              let q2 = (Session.account s.State "q2").Value
              Expect.isNone q2.Statement.Value.CaptureKey "no key over withheld values"
              Expect.isTrue (snd q2.Pipeline > 0) "q2's pipeline is recorded with its literal withheld"

              // the default is not recorded: a model marking nothing recordable records no value at all
              let none =
                  drive (modelWith [] "personal") (engine (modelWith [] "personal") (ref 0))

              Expect.isFalse
                  (opsOf none
                   |> List.exists (fun o ->
                       match o with
                       | SessionOp.ValuesRecorded _
                       | SessionOp.RowsRecorded _ -> true
                       | _ -> false))
                  "nothing is recorded by default"

          testCase "the reducer refuses a stream that records a value its own admission says is unrecordable"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let state = s.State

              // q2 ran with its values withheld; a forged record of them does not fold, nor does a
              // plain digest of its result, nor a withholding for the public request
              let forged =
                  { state with
                      Requests =
                          state.Requests
                          |> Map.add
                              "q2"
                              { state.Requests.["q2"] with
                                  Values = None
                                  Returned = None
                                  Rows = None
                                  Outcome = None } }

              match SessionOps.apply (SessionOp.ValuesRecorded("q2", [ "@p0", Str needle ])) forged with
              | Error(SessionRejection.NotRecordable(_, [ "personal" ])) -> ()
              | other -> failtestf "expected NotRecordable, got %A" other

              let ran =
                  { forged with
                      Requests =
                          forged.Requests
                          |> Map.add
                              "q2"
                              { forged.Requests.["q2"] with
                                  Values = Some(Recorded.Withheld [ "personal" ]) } }

              match SessionOps.apply (SessionOp.Returned("q2", 1, ResultDigest.Plain "sha256:x", "t")) ran with
              | Error(SessionRejection.DigestForm _) -> ()
              | other -> failtestf "expected DigestForm, got %A" other

              let reopenedQ1 =
                  { state with
                      Requests =
                          state.Requests
                          |> Map.add
                              "q1"
                              { state.Requests.["q1"] with
                                  Rows = None
                                  Outcome = None } }

              match SessionOps.apply (SessionOp.Withheld("q1", WithheldPart.Rows, [ "public" ])) reopenedQ1 with
              | Error(SessionRejection.WithheldMismatch _) -> ()
              | other -> failtestf "expected WithheldMismatch, got %A" other

          testCase "no classified value appears in any emitted wire: the test reads the wire, not the code"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let wire = Export.wire s.Records

              let statement =
                  Export.statement s.State s.Records |> ok "statement" |> Encoding.UTF8.GetString

              let envelope =
                  Export.sign [ hmac "k" ] s.State s.Records |> ok "sign" |> Export.envelope

              let decoded =
                  let j = Json.parse envelope |> ok "envelope json"

                  match j with
                  | JObj fs ->
                      match fs |> List.find (fun (k, _) -> k = "payload") |> snd with
                      | JStr b64 -> Encoding.UTF8.GetString(Convert.FromBase64String b64)
                      | _ -> failtest "payload"
                  | _ -> failtest "envelope"

              for text in [ wire; statement; envelope; decoded ] do
                  for secret in [ secretEmail; needle; secretChange ] do
                      Expect.isFalse (text.Contains secret) (sprintf "'%s' is not in the emitted wire" secret)

              // the probe is live: the public values ARE in the wire, so the absence above is the rule
              Expect.stringContains wire "\"v\":40" "a public row is recorded"
              Expect.stringContains wire "2026-10-04T08:00:00Z" "a public change value is recorded"

              // and the probe fails when the rule is lifted: marking `personal` recordable puts them there
              let all = modelWith [ "public"; "personal" ] "personal"
              let lifted = Export.wire (drive all (engine all (ref 0))).Records
              Expect.stringContains lifted needle "the literal, once recordable, is recorded"
              Expect.stringContains lifted secretChange "the change value, once recordable, is recorded"

          testCase
              "a capture key per statement: a captured session replays byte-identically and never reaches the database"
          <| fun _ ->
              let calls = ref 0
              let recorded = drive model (engine model calls)
              Expect.equal calls.Value 2 "the engine ran both statements"

              let replayCalls = ref 0
              let replayer = Session.replayExecutor recorded.State

              let counted: Execute =
                  fun st ->
                      replayCalls.Value <- replayCalls.Value + 1
                      replayer st

              let replayed = drive model counted

              // q1 replays from its capture, byte for byte; q2's rows were withheld, so it has none and
              // its replay is refused by name rather than sent to a database
              let q1Ops (s: Session) =
                  opsOf s
                  |> List.filter (fun o ->
                      match o with
                      | SessionOp.Ran("q1", _, _)
                      | SessionOp.Returned("q1", _, _, _)
                      | SessionOp.RowsRecorded("q1", _)
                      | SessionOp.Finished("q1", _, _, _) -> true
                      | _ -> false)
                  |> List.map SessionWire.encodeOp

              Expect.equal (q1Ops replayed) (q1Ops recorded) "the captured result replays byte-identically"
              Expect.equal calls.Value 2 "the replay never reached the engine"

              Expect.isTrue
                  (opsOf replayed
                   |> List.exists (fun o ->
                       match o with
                       | SessionOp.Failed("q2", "DB-SESSION-EXECUTE-FAILED", _) -> true
                       | _ -> false))
                  "an uncaptured statement is refused, not executed"

              // a fully recordable session replays whole: the same bytes end to end
              let all = modelWith [ "public"; "personal" ] "personal"
              let first = drive all (engine all (ref 0))
              let again = drive all (Session.replayExecutor first.State)
              Expect.equal (Export.wire again.Records) (Export.wire first.Records) "the whole record, byte for byte"

          testCase "a replay of a captured session evaluates every local suffix again and reaches the recorded results"
          <| fun _ ->
              let all = modelWith [ "public"; "personal" ] "personal"
              let s = drive all (engine all (ref 0))
              let _, state = SessionWire.read (Some s.Head) (Export.wire s.Records) |> ok "read"

              // q1 has a local suffix (the pattern filter the dialect refuses); the replay runs it again
              let q1 = state.Requests.["q1"]
              Expect.isNonEmpty q1.Split.Value.Suffix "q1 has a local suffix"

              for request in [ "q1"; "q2" ] do
                  match Session.replay state request with
                  | ReplayVerdict.Reached _ -> ()
                  | other -> failtestf "%s: %A" request other

              // falsifier: a record whose final digest is not the suffix's result is caught
              let tampered =
                  { state with
                      Requests =
                          state.Requests
                          |> Map.add
                              "q1"
                              { q1 with
                                  Outcome =
                                      Some(Outcome.Finished(9, ResultDigest.Plain(SessionHash.digest "other"), "t")) } }

              match Session.replay tampered "q1" with
              | ReplayVerdict.Diverged _ -> ()
              | other -> failtestf "expected Diverged, got %A" other

              // a withheld result has nothing to replay, and says so
              let partial = drive model (engine model (ref 0))

              match Session.replay partial.State "q2" with
              | ReplayVerdict.NotCaptured reason -> Expect.stringContains reason "withheld" "named"
              | other -> failtestf "expected NotCaptured, got %A" other

          testCase
              "a reader answers for any result, without a database: pipeline, rules, statement, schema fingerprint, rows and when"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let _, state = SessionWire.read (Some s.Head) (Export.wire s.Records) |> ok "read"
              let a = Session.account state "q1" |> Option.get

              Expect.equal (fst a.Pipeline) (publicPipeline 10) "the pipeline, verbatim: q1 is recordable"

              match a.Verdict with
              | Some(Verdict.Admitted("analyst", reads, rules, _, 0)) ->
                  Expect.equal reads [ "public" ] "what it read"

                  Expect.isTrue
                      (rules
                       |> List.exists (fun r ->
                           match r with
                           | PolicyRule.CostAdmitted _ -> true
                           | _ -> false))
                      "the rules applied"
              | other -> failtestf "verdict %A" other

              Expect.isSome a.Statement "the statement"
              Expect.isTrue (a.Schemas.ContainsKey "shop") "the schema reported before it"

              Expect.isFalse
                  (a.Schemas.["shop"].Drift.Value
                   |> List.exists (fun d ->
                       match d with
                       | TableDrift.InStep _ -> false
                       | _ -> true))
                  "the reported schema is in step with the model"

              Expect.equal (List.length a.Guards) 1 "the fingerprint that guarded it"
              Expect.equal (fst a.Returned.Value) 3 "three orders over 10 returned"

              match a.Outcome with
              | Some(Outcome.Finished(2, ResultDigest.Plain _, at)) -> Expect.stringStarts at "2026-10-11T10:00:" "when"
              | other -> failtestf "outcome %A" other

              Expect.equal a.Received "2026-10-11T10:00:03Z" "when it arrived"

              // a refused request is answered too
              match (Session.account state "q3").Value.Verdict with
              | Some(Verdict.Refused("nobody", code)) -> Expect.equal code "DB-GRANT-NONE" "refused by code"
              | other -> failtestf "q3 %A" other

          testCase
              "2008.t3: each statement is recorded beside the fingerprint that guarded it, and the verdict is Fingerprint.compare's"
          <| fun _ ->
              // the change column of customers public, so every reading is recorded whole
              let open' = modelWith [ "public" ] "public"
              let s = drive open' (engine open' (ref 0))
              let q1 = (Session.account s.State "q1").Value
              let q2 = (Session.account s.State "q2").Value
              let m = Fingerprint.ofSource "s:shop" open' |> Option.get
              let expected = readingOf open' held.["orders"] held.["customers"]

              match q1.Guards, q2.Guards with
              | [ g1 ], [ g2 ] ->
                  Expect.equal g1.Source "s:shop" "the source id"
                  Expect.equal g1.Method m "the method, Fingerprint.ofSource's"
                  Expect.equal g1.Kind (Fingerprint.disclose m).Kind "the kind, Fingerprint.disclose's"
                  Expect.equal g1.Reading expected "the reading, from Probe.forSource and Probe.readTables"
                  Expect.isNone g1.Verdict "the first reading has no previous to compare with"

                  Expect.equal
                      g2.Verdict
                      (Some(Fingerprint.compare m g1.Reading g2.Reading))
                      "Fingerprint.compare's verdict"
              | other -> failtestf "guards %A" other

              // the guard sits in the stream before the statement it guarded
              let ops = opsOf s |> List.map SessionOps.tag
              let guardAt = ops |> List.findIndex ((=) "fingerprinted")
              let ranAt = ops |> List.findIndex ((=) "ran")
              Expect.isLessThan guardAt ranAt "read before the statement"

              // a forged verdict does not fold
              let forged =
                  SessionOp.Fingerprinted(
                      "q3x",
                      "s:shop",
                      m,
                      FingerprintKind.Heuristic,
                      expected,
                      [],
                      Some(FingerprintVerdict.Changed [ "forged" ]),
                      "t"
                  )

              let withQ3x =
                  SessionOps.applyAll
                      [ SessionOp.Received("q3x", "orders", [], 0, "t")
                        SessionOp.Admitted("q3x", "p", [ "public" ], [], [], 0)
                        SessionOp.Lowered("q3x", 0, [], 0, None) ]
                      s.State
                  |> ok "q3x"

              match SessionOps.apply forged withQ3x with
              | Error(SessionRejection.VerdictMismatch _) -> ()
              | other -> failtestf "expected VerdictMismatch, got %A" other

              // with the change column classified, the change value is withheld and named, and the
              // verdict the host computed over the true readings is still recorded
              let s' = drive model (engine model (ref 0))
              let g = (Session.account s'.State "q2").Value.Guards |> List.exactlyOne
              Expect.equal g.Withheld [ "dbo.customers" ] "the withheld table is named"

              match g.Reading with
              | FingerprintReading.Tables rows ->
                  Expect.equal (rows |> List.find (fun r -> r.Table = "dbo.customers")).MaxChange Null "withheld"
              | other -> failtestf "%A" other

              Expect.equal
                  g.Verdict
                  (Some(FingerprintVerdict.NoChangeSeen (Fingerprint.disclose m).Misses))
                  "the true verdict"

          testCase "the reader refuses a torn tail, a broken chain and a rewound head, by Core's classes"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let wire = Export.wire s.Records

              // intact, with and without the head held from elsewhere
              SessionWire.read None wire |> ok "intact" |> ignore
              SessionWire.read (Some s.Head) wire |> ok "intact at its head" |> ignore

              // a write cut off mid-record
              match SessionWire.read None (wire.Substring(0, wire.Length - 20)) with
              | Error(SessionReadFault.TornTail f) -> Expect.equal f.Line (List.length s.Records) "the last line"
              | other -> failtestf "expected TornTail, got %A" other

              // a whole record dropped: the prefix verifies, and only the held head catches it
              let lines = wire.Split('\n')
              let shorter = String.Join("\n", lines |> Array.take (lines.Length - 1))
              SessionWire.read None shorter |> ok "a prefix verifies on its own" |> ignore

              match SessionWire.read (Some s.Head) shorter with
              | Error(SessionReadFault.Rewound(expected, _)) -> Expect.equal expected s.Head "names the held head"
              | other -> failtestf "expected Rewound, got %A" other

              // a record altered in place
              let altered = wire.Replace("\"principal\":\"analyst\"", "\"principal\":\"admin\"")
              Expect.notEqual altered wire "the probe altered something"

              match SessionWire.read None altered with
              | Error(SessionReadFault.Broken b) -> Expect.equal b.Reason ChainBreakReason.HashMismatch "Core's reason"
              | other -> failtestf "expected Broken, got %A" other

          testCase "export: canonical wire, and the in-toto statement a witness signs, with the signer passed in"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let payload = Export.statement s.State s.Records |> ok "statement"
              let text = Encoding.UTF8.GetString payload
              Expect.stringContains text ("\"sha256\":\"" + s.Head + "\"") "the subject is the session's head"
              Expect.stringContains text Export.PredicateType "the predicate type"
              Expect.stringContains text "\"_type\":\"https://in-toto.io/Statement/v1\"" "an in-toto statement"
              Expect.equal s.Head.Length 64 "a SHA-256 head"

              let signed = Export.sign [ hmac "k1"; hmac "k2" ] s.State s.Records |> ok "sign"
              let pae = Export.pae Export.PayloadType payload
              Expect.equal signed.Payload payload "the payload is the statement"

              Expect.equal
                  (signed.Signatures |> List.map snd)
                  [ (hmac "k1").Sign pae; (hmac "k2").Sign pae ]
                  "each signer signed DSSE's encoding of the statement"

              Expect.stringStarts
                  (Encoding.UTF8.GetString pae)
                  (sprintf "DSSEv1 %d application/vnd.in-toto+json %d " Export.PayloadType.Length payload.Length)
                  "DSSE's pre-authentication encoding"

              Expect.isError (Export.statement SessionOps.empty []) "an empty session has no head"

          testCase "every op round-trips through the codec and re-encodes to the same bytes"
          <| fun _ ->
              let s = drive model (engine model (ref 0))
              let all = modelWith [ "public"; "personal" ] "personal"
              let s' = drive all (engine all (ref 0))
              let ops = opsOf s @ opsOf s'
              let tags = ops |> List.map SessionOps.tag |> List.distinct |> List.sort

              Expect.containsAll
                  tags
                  (SessionOps.tags |> List.filter ((<>) "failed"))
                  "every op kind but failure is exercised"

              for op in ops @ [ SessionOp.Failed("q", "DB-SESSION-EXECUTE-FAILED", "t") ] do
                  let text = SessionWire.encodeOp op

                  match SessionWire.decodeOp text with
                  | Ok back ->
                      Expect.equal back op "the same op"
                      Expect.equal (SessionWire.encodeOp back) text "the same bytes"
                  | Error e -> failtestf "%s: %s" text (ModelWire.describe e)

          testCase "the model's recordable set: written only when it names something, and held to the declared set"
          <| fun _ ->
              let spec r =
                  NodeKind.Model
                      { Name = "m"
                        Classifications = [ "public" ]
                        Recordable = r }

              let none = ModelWire.encodeOp (ModelOp.Amend("m", spec []))
              Expect.isFalse (none.Contains "recordable") "an empty set is omitted"
              let some = ModelWire.encodeOp (ModelOp.Amend("m", spec [ "public" ]))
              Expect.stringContains some "\"recordable\":[\"public\"]" "a set is written"
              Expect.equal (ModelWire.decodeOp some) (Ok(ModelOp.Amend("m", spec [ "public" ]))) "and read"

              match ModelWire.decodeOp (some.Replace("[\"public\"]", "[]")) with
              | Error e -> Expect.equal (ModelWire.code e) "DB-WIRE-WRONG-SHAPE" "an empty array is a second spelling"
              | Ok _ -> failtest "an explicit empty set is refused"

              let undeclared =
                  modelWith [ "public"; "secret" ] "personal"
                  |> ModelRules.check
                  |> List.map _.Code

              Expect.contains
                  undeclared
                  ModelRules.RecordableUndeclared
                  "a recordable classification the model does not declare"

              Expect.isEmpty (ModelRules.check model |> List.map _.Code) "the worked model is clean" ]

// ---- the corpus vectors (`--write-fixtures`) ----

/// The recordable variant of the worked model: every classification marked recordable.
let private allRecordable = modelWith [ "public"; "personal" ] "personal"

let private worked () = drive model (engine model (ref 0))

let private recordable () =
    drive allRecordable (engine allRecordable (ref 0))

/// One op per kind, the first of each in the worked sessions, as `NN-<kind>` and its canonical text.
let corpusOps () : (string * string) list =
    let failed =
        // a replay of the worked session with nothing captured fails its first statement by name
        drive model (fun _ -> Error "no capture") |> opsOf

    let ops = opsOf (worked ()) @ opsOf (recordable ()) @ failed

    SessionOps.tags
    |> List.mapi (fun i tag ->
        let op = ops |> List.find (fun o -> SessionOps.tag o = tag)
        sprintf "%02d-%s" (i + 1) tag, SessionWire.encodeOp op)

/// The worked sessions as canonical wire.
let corpusStreams () : (string * string) list =
    [ "worked", Export.wire (worked ()).Records
      "recordable", Export.wire (recordable ()).Records ]

/// Reader refusals: `(name, as, input, expected head, code)`.
let corpusRejects () : (string * string * string * string option * string) list =
    let s = worked ()
    let wire = Export.wire s.Records
    let lines = wire.Split('\n')
    let shorter = String.Join("\n", lines |> Array.take (lines.Length - 1))
    let altered = wire.Replace("\"principal\":\"analyst\"", "\"principal\":\"admin\"")

    // a chain that verifies and records a value its own admission withholds
    let refused =
        let header = s.State.Header.Value

        let ops =
            [ SessionOp.Opened header
              SessionOp.Received("q", "customers", [], 0, "t")
              SessionOp.Admitted("q", "p", [ "personal" ], [], [], 0)
              SessionOp.Lowered("q", 1, [], 0, None)
              SessionOp.Ran(
                  "q",
                  { Hash = SessionHash.digest "s"
                    Text = "SELECT 1"
                    Parameters = [ "@p0", StringType ]
                    Columns = [ "email", StringType ]
                    TimeoutSeconds = Some 30
                    CaptureKey = None },
                  "t"
              )
              SessionOp.ValuesRecorded("q", [ "@p0", Str "x" ]) ]

        ops
        |> List.fold (fun recs op -> OpStream.appendChainOnly SessionWire.stream actor op recs) OpStream.empty
        |> Export.wire

    [ "torn-tail", "session", wire.Substring(0, wire.Length - 20), None, "DB-SESSION-TORN-TAIL"
      "rewound-head", "session", shorter, Some s.Head, "DB-SESSION-REWOUND-HEAD"
      "chain-broken", "session", altered, None, "DB-SESSION-CHAIN-BROKEN"
      "refused-not-recordable", "session", refused, None, "DB-SESSION-REFUSED"
      "unknown-op", "session-op", """{"$type":"deleted","request":"q"}""", None, "DB-WIRE-UNKNOWN-KIND"
      "unknown-field",
      "session-op",
      """{"$type":"refused","code":"c","principal":"p","request":"q","rows":1}""",
      None,
      "DB-WIRE-UNKNOWN-FIELD"
      "missing-field",
      "session-op",
      """{"$type":"refused","principal":"p","request":"q"}""",
      None,
      "DB-WIRE-MISSING-FIELD"
      "float-not-canonical",
      "session-op",
      """{"$type":"valuesRecorded","request":"q","values":[{"name":"@p0","value":{"$type":"float","v":"1.50"}}]}""",
      None,
      "DB-WIRE-WRONG-SHAPE"
      "int64-not-canonical",
      "session-op",
      """{"$type":"fingerprinted","at":"t","kind":"exact","method":{"$type":"fileStat","contentHash":false},"reading":{"$type":"file","modifiedUtcTicks":"007","size":"1"},"request":"q","source":"s","withheld":[]}""",
      None,
      "DB-WIRE-WRONG-SHAPE" ]
