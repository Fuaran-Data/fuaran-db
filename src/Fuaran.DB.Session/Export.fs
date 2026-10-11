namespace Fuaran.DB.Session

open System
open System.Text
open Fuaran.Core

/// A signer the host supplies: its key id and a function from the bytes to sign to the signature.
/// The domain holds no key and takes no dependency on any signing package; it is a provider AT the
/// witness seam, not a consumer of it.
type Signer =
    {
        /// The key's id, as the verifier will look it up.
        KeyId: string
        /// Sign the bytes.
        Sign: byte[] -> byte[]
    }

/// A signed export: a DSSE envelope over the session's in-toto statement.
type SignedSession =
    {
        /// The payload type, `application/vnd.in-toto+json`.
        PayloadType: string
        /// The statement's bytes.
        Payload: byte[]
        /// The key id and the signature.
        Signatures: (string * byte[]) list
    }

/// Export (`Fuaran.DB.Session.Export`): a session as canonical wire, and as the statement a witness
/// signs — an in-toto Statement v1 whose one subject is the session's head, so the signature covers
/// every record by the chain. The predicate summarises what the record claims and nothing more: that
/// these statements were derived from a tree under a policy, never that a result is correct.
[<RequireQualifiedAccess>]
module Export =

    /// The in-toto statement type.
    [<Literal>]
    let StatementType = "https://in-toto.io/Statement/v1"

    /// The predicate type of a connection record.
    [<Literal>]
    let PredicateType = "https://fuaran-ui.io/attestation/db-session/v0.1"

    /// The DSSE payload type.
    [<Literal>]
    let PayloadType = "application/vnd.in-toto+json"

    /// The session as canonical wire (`SessionWire.write`).
    let wire (records: OpRecord<SessionOp> list) : string = SessionWire.write records

    let private predicate (state: SessionState) (records: OpRecord<SessionOp> list) : JVal =
        let header =
            match state.Header with
            | Some h ->
                [ "session", JStr h.Session
                  "model", JStr h.ModelHash
                  "dialect", JStr(h.Dialect + "@" + h.DialectVersion)
                  "provider", JStr h.ProviderVersion
                  "opened", JStr h.At ]
            | None -> []

        let statements =
            state.Order
            |> List.choose (fun id -> state.Requests.[id].Statement |> Option.map (fun (s, _) -> JStr s.Hash))

        JObj(
            header
            @ [ "records", JInt(List.length records)
                "statements", JArr statements
                "claim",
                JStr "each statement was derived from a dataset-model tree under a policy; no result is claimed correct" ]
        )

    /// The in-toto statement's bytes for a record: canonical JSON, the subject the session's head as a
    /// SHA-256 digest. `Error` for an empty record, which has no head to name.
    let statement (state: SessionState) (records: OpRecord<SessionOp> list) : Result<byte[], string> =
        match records, state.Header with
        | [], _
        | _, None -> Error "an empty session has no head to attest"
        | _, Some h ->
            let head = OpStream.head records

            let json =
                JObj
                    [ "_type", JStr StatementType
                      "subject", JArr [ JObj [ "name", JStr h.Session; "digest", JObj [ "sha256", JStr head ] ] ]
                      "predicateType", JStr PredicateType
                      "predicate", predicate state records ]

            Ok(Encoding.UTF8.GetBytes(Canon.render json))

    /// DSSE's pre-authentication encoding: what a signer signs, binding the payload to its type.
    let pae (payloadType: string) (payload: byte[]) : byte[] =
        let typeBytes = Encoding.UTF8.GetBytes payloadType

        Array.concat
            [ Encoding.UTF8.GetBytes(sprintf "DSSEv1 %d " typeBytes.Length)
              typeBytes
              Encoding.UTF8.GetBytes(sprintf " %d " payload.Length)
              payload ]

    /// Sign a record's statement with each signer passed in.
    let sign
        (signers: Signer list)
        (state: SessionState)
        (records: OpRecord<SessionOp> list)
        : Result<SignedSession, string> =
        statement state records
        |> Result.map (fun payload ->
            let bytes = pae PayloadType payload

            { PayloadType = PayloadType
              Payload = payload
              Signatures = signers |> List.map (fun s -> s.KeyId, s.Sign bytes) })

    /// The DSSE envelope as JSON text, payload and signatures in base64.
    let envelope (signed: SignedSession) : string =
        Canon.render (
            JObj
                [ "payloadType", JStr signed.PayloadType
                  "payload", JStr(Convert.ToBase64String signed.Payload)
                  "signatures",
                  JArr(
                      signed.Signatures
                      |> List.map (fun (k, s) -> JObj [ "keyid", JStr k; "sig", JStr(Convert.ToBase64String s) ])
                  ) ]
        )
