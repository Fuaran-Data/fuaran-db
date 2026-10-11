namespace Fuaran.DB.Session

open Fuaran.Core
open Fuaran.DB
open Fuaran.DB.Provider

/// The five inputs that fix a statement. The reduction from a tree to a statement is pure, so these
/// are all a host needs to say which statement it ran: two hosts holding the same five hold the same
/// statement, and hash it to the same value.
type StatementIdentity =
    {
        /// The parameterised text.
        Text: string
        /// Each parameter's marker and type, in ordinal order.
        Parameters: (string * ColumnType) list
        /// `sha256:` over the model's canonical text.
        ModelHash: string
        /// The dialect's id.
        Dialect: string
        /// The dialect's version.
        DialectVersion: string
        /// The provider's version.
        ProviderVersion: string
    }

/// The hashes the record is keyed by. Every digest is `sha256:` and 64 lowercase hex digits over an
/// injective pre-image (`Hash.canonicalFields`), so two distinct inputs never share one.
[<RequireQualifiedAccess>]
module SessionHash =

    /// `sha256:` over a text's UTF-8 bytes.
    let digest (text: string) : string = "sha256:" + Hash.sha256Hex text

    /// The model's hash: over its canonical wire text.
    let model (root: ModelNode) : string = digest (ModelWire.encodeNode root)

    /// The provider's version, `major.minor.patch`, read from the provider assembly — never typed by a
    /// host, so two hosts on one provider build agree on it.
    let providerVersion: string =
        let v = typeof<Lowered>.Assembly.GetName().Version
        sprintf "%d.%d.%d" v.Major v.Minor v.Build

    /// The statement's hash: over its text, its parameter names and types, the model's hash, the
    /// dialect's id and version and the provider's version. No parameter VALUE enters it.
    let statement (i: StatementIdentity) : string =
        [ "fuaran-db/statement/1"; i.Text; string (List.length i.Parameters) ]
        @ (i.Parameters |> List.collect (fun (name, ty) -> [ name; ColumnType.tag ty ]))
        @ [ i.ModelHash; i.Dialect; i.DialectVersion; i.ProviderVersion ]
        |> Hash.canonicalFields
        |> digest

    /// The identity of a lowered statement under a session's header.
    let identityOf (header: SessionHeader) (s: Statement) : StatementIdentity =
        { Text = s.Text
          Parameters = s.Parameters |> List.map (fun p -> p.Marker, p.Type)
          ModelHash = header.ModelHash
          Dialect = header.Dialect
          DialectVersion = header.DialectVersion
          ProviderVersion = header.ProviderVersion }

    /// A bound cell as two fields of a pre-image — `Query.invocationKey`'s spelling, so the two keys
    /// read alike: a one-letter tag and the scalar rendering.
    let private cellFields (c: Cell) : string * string =
        match c with
        | Int v -> "i", string v
        | Float v -> "f", Canon.canonicalFloat v
        | Bool v -> "b", (if v then "1" else "0")
        | Str v -> "s", v
        | Date v -> "d", v
        | Timestamp v -> "t", v
        | Decimal v -> "m", v
        | Null -> "n", ""

    /// The capture key: `Query.invocationKey`'s pattern — the statement's identity, `#`, and a hash of
    /// the name-sorted bindings, three fields each (marker, cell tag, payload). The same statement with
    /// the same values has the same key, so a captured result answers it. SHA-256 rather than the
    /// invocation key's FNV-1a, so that two distinct bindings sharing a key is not merely unclaimed but
    /// out of reach.
    let captureKey (statementHash: string) (values: (string * Cell) list) : string =
        let fields =
            values
            |> List.sortBy fst
            |> List.collect (fun (name, v) ->
                let tag, payload = cellFields v
                [ name; tag; payload ])

        statementHash
        + "#"
        + Hash.sha256Hex (Hash.canonicalFields ("fuaran-db/capture/1" :: fields))

    /// A table's canonical columnar text, Core's (`ColumnCodec`); `Error` for a table that has none
    /// (a non-finite float).
    let tableText (t: Table) : Result<string, string> =
        ColumnCodec.tryEncode (Embedded t) |> Result.mapError ColumnCodec.errorString

    /// A result's digest: plain over its text when `secret` is `None`, committed under it otherwise.
    let result (secret: string option) (text: string) : ResultDigest =
        match secret with
        | None -> ResultDigest.Plain(digest text)
        | Some s -> ResultDigest.Committed(digest (Hash.canonicalFields [ "fuaran-db/commit/1"; s; text ]))
