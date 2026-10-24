namespace Fuaran.DB.Session

open System
open System.Globalization
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.WireDecode

/// Why a session's wire text was not read: the classes Core already names. A line that does not
/// scan is Core's `JsonlFault`; on the LAST line it is a torn tail, a write cut off mid-record. A
/// chain that does not verify is Core's `ChainBreak`. A head behind the one the reader was told to
/// expect is Core's `AppendRejection.StaleHead` shape — a rewound head. A stream whose records verify
/// and do not fold is the reducer's own refusal, at its index.
[<RequireQualifiedAccess>]
type SessionReadFault =
    /// The last line does not scan: the tail was torn mid-write.
    | TornTail of JsonlFault
    /// A line before the last does not scan.
    | Malformed of JsonlFault
    /// The chain does not verify.
    | Broken of ChainBreak
    /// The stream's head is not the head the reader expected; the stream stops short of it, or forked.
    | Rewound of expected: string * actual: string
    /// The records verify and the reducer refuses one.
    | Refused of index: int * rejection: SessionRejection

/// The canonical wire codec for the connection record: each op as a `$type`-tagged canonical object
/// (keys sorted, one spelling per value), a session as Core's JSONL chain of them, and a reader that
/// refuses a torn tail, a broken chain or a rewound head by name. Strict: an unknown field, a
/// missing one or an unknown kind is a named refusal, never a default.
[<RequireQualifiedAccess>]
module SessionWire =

    /// The chain's hash: SHA-256, so a session's head is a digest the witness seam can sign.
    let hashFn: HashFn = OpStream.sha256Hash

    // ---- encode ----

    let private strs (xs: string list) = JArr(xs |> List.map JStr)

    // A 64-bit count travels as its decimal text: the wire's integers are 32-bit.
    let private int64Json (n: int64) =
        JStr(n.ToString(CultureInfo.InvariantCulture))

    /// A cell as a typed object. Every payload is text, so a float keeps its exact canonical spelling.
    let cellJson (c: Cell) : JVal =
        match c with
        | Int v -> Canonical.typed "int" [ "v", JInt v ]
        | Float v -> Canonical.typed "float" [ "v", JStr(FloatLayout.canonical v) ]
        | Bool v -> Canonical.typed "bool" [ "v", JBool v ]
        | Str v -> Canonical.typed "string" [ "v", JStr v ]
        | Date v -> Canonical.typed "date" [ "v", JStr v ]
        | Timestamp v -> Canonical.typed "timestamp" [ "v", JStr v ]
        | Decimal v -> Canonical.typed "decimal" [ "v", JStr v ]
        | Null -> Canonical.typed "null" []

    let private typedPair (name: string, ty: ColumnType) =
        JObj [ "name", JStr name; "type", JStr(ColumnType.tag ty) ]

    /// A reported field, or a statement's result column, as Core's field document
    /// (`ColumnCodec.fieldJson`): its name and type, and the unit, label, description and extension
    /// members only when stated, so a field stating none is the `typedPair` it was before fields
    /// carried metadata.
    let private fieldJson (f: Field) = ColumnCodec.fieldJson f

    let private columnRefJson (c: ColumnRef) =
        JObj
            [ "table", JStr c.Table
              "column", JStr c.Column
              "classification", JStr c.Classification ]

    let private levelTag (l: GrantLevel) =
        match l with
        | GrantLevel.Aggregate -> "aggregate"
        | GrantLevel.RowLevel -> "row"

    let private optional (name: string) (v: JVal option) =
        v |> Option.map (fun j -> name, j) |> Option.toList

    let private limitsJson (l: CostLimits) =
        JObj(
            [ "maxRows", JInt l.MaxRows; "timeoutSeconds", JInt l.TimeoutSeconds ]
            @ optional "maxJoins" (l.MaxJoins |> Option.map JInt)
            @ optional "maxGroupKeys" (l.MaxGroupKeys |> Option.map JInt)
            @ optional "minGroupSize" (l.MinGroupSize |> Option.map JInt)
        )

    let private ruleJson (r: PolicyRule) : JVal =
        match r with
        | PolicyRule.ColumnsGranted cols ->
            Canonical.typed
                "columnsGranted"
                [ "columns",
                  JArr(
                      cols
                      |> List.map (fun (c, l) -> JObj [ "column", columnRefJson c; "level", JStr(levelTag l) ])
                  ) ]
        | PolicyRule.RowPolicyApplied(table, policy, at, predicate) ->
            Canonical.typed
                "rowPolicyApplied"
                [ "table", JStr table
                  "policy", JStr policy
                  "at", JInt at
                  "predicate", DataFrameCodec.encodeExpr predicate ]
        | PolicyRule.GroupFloorApplied(step, floor, cols) ->
            Canonical.typed
                "groupFloorApplied"
                [ "step", JInt step
                  "floor", JInt floor
                  "columns", JArr(cols |> List.map columnRefJson) ]
        | PolicyRule.CostAdmitted(rows, joins, keys, limits) ->
            Canonical.typed
                "costAdmitted"
                [ "rows", JInt rows
                  "joins", JInt joins
                  "groupKeys", JInt keys
                  "limits", limitsJson limits ]

    let private pipelineJson (p: Transform list) =
        JArr(p |> List.map DataFrameCodec.encodeTransform)

    let private kindTag (k: FingerprintKind) =
        match k with
        | FingerprintKind.Exact -> "exact"
        | FingerprintKind.Heuristic -> "heuristic"

    let private readingJson (r: FingerprintReading) : JVal =
        match r with
        | FingerprintReading.Counter position -> Canonical.typed "counter" [ "position", JStr position ]
        | FingerprintReading.Tables rows ->
            Canonical.typed
                "tables"
                [ "tables",
                  JArr(
                      rows
                      |> List.map (fun t ->
                          JObj
                              [ "table", JStr t.Table
                                "rows", int64Json t.Rows
                                "maxChange", cellJson t.MaxChange ])
                  ) ]
        | FingerprintReading.File f ->
            Canonical.typed
                "file"
                ([ "size", int64Json f.Size; "modifiedUtcTicks", int64Json f.ModifiedUtcTicks ]
                 @ optional "contentHash" (f.ContentHash |> Option.map JStr))

    let private verdictJson (v: FingerprintVerdict) : JVal =
        match v with
        | FingerprintVerdict.Changed what -> Canonical.typed "changed" [ "what", strs what ]
        | FingerprintVerdict.Unchanged -> Canonical.typed "unchanged" []
        | FingerprintVerdict.NoChangeSeen misses -> Canonical.typed "noChangeSeen" [ "misses", strs misses ]
        | FingerprintVerdict.Incomparable reason -> Canonical.typed "incomparable" [ "reason", JStr reason ]

    let private schemaJson (s: ReportedSchema) : JVal =
        JArr(
            s.Tables
            |> List.map (fun t -> JObj [ "name", JStr t.Name; "columns", JArr(t.Columns |> List.map fieldJson) ])
        )

    let private driftJson (d: TableDrift) : JVal =
        match d with
        | TableDrift.InStep t -> Canonical.typed "inStep" [ "table", JStr t ]
        | TableDrift.Drifted(t, delta) ->
            Canonical.typed "drifted" [ "table", JStr t; "delta", SchemaDeltaCodec.encodeJson delta ]
        | TableDrift.MissingAtSource t -> Canonical.typed "missingAtSource" [ "table", JStr t ]
        | TableDrift.Unmodelled t -> Canonical.typed "unmodelled" [ "table", JStr t ]

    let private digestJson (d: ResultDigest) : JVal =
        match d with
        | ResultDigest.Plain h -> Canonical.typed "plain" [ "digest", JStr h ]
        | ResultDigest.Committed h -> Canonical.typed "committed" [ "digest", JStr h ]

    let private statementJson (s: StatementRecord) : JVal =
        JObj(
            [ "hash", JStr s.Hash
              "text", JStr s.Text
              "parameters", JArr(s.Parameters |> List.map typedPair)
              "columns", JArr(s.Columns |> List.map fieldJson) ]
            @ optional "timeoutSeconds" (s.TimeoutSeconds |> Option.map JInt)
            @ optional "captureKey" (s.CaptureKey |> Option.map JStr)
        )

    let private partTag (p: WithheldPart) =
        match p with
        | WithheldPart.Parameters -> "parameters"
        | WithheldPart.Rows -> "rows"

    /// An op as a `JVal`. Optional members are written only when present, so each value has one spelling.
    let encodeOpJson (op: SessionOp) : JVal =
        let t = SessionOps.tag op

        match op with
        | SessionOp.Opened h ->
            Canonical.typed
                t
                [ "session", JStr h.Session
                  "model", JStr h.Model
                  "modelHash", JStr h.ModelHash
                  "recordable", strs h.Recordable
                  "dialect", JStr h.Dialect
                  "dialectVersion", JStr h.DialectVersion
                  "providerVersion", JStr h.ProviderVersion
                  "at", JStr h.At ]
        | SessionOp.SchemaReported(source, schema, drift, at) ->
            Canonical.typed
                t
                ([ "source", JStr source; "schema", schemaJson schema; "at", JStr at ]
                 @ optional "drift" (drift |> Option.map (List.map driftJson >> JArr)))
        | SessionOp.Received(request, table, pipeline, redacted, at) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "table", JStr table
                  "pipeline", pipelineJson pipeline
                  "redacted", JInt redacted
                  "at", JStr at ]
        | SessionOp.Admitted(request, principal, reads, rules, pipeline, redacted) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "principal", JStr principal
                  "reads", strs reads
                  "rules", JArr(rules |> List.map ruleJson)
                  "pipeline", pipelineJson pipeline
                  "redacted", JInt redacted ]
        | SessionOp.Refused(request, principal, code) ->
            Canonical.typed t [ "request", JStr request; "principal", JStr principal; "code", JStr code ]
        | SessionOp.Lowered(request, lowered, suffix, redacted, boundary) ->
            Canonical.typed
                t
                ([ "request", JStr request
                   "lowered", JInt lowered
                   "suffix", pipelineJson suffix
                   "redacted", JInt redacted ]
                 @ optional "boundary" (boundary |> Option.map JStr))
        | SessionOp.Fingerprinted(request, source, m, kind, reading, withheld, verdict, at) ->
            Canonical.typed
                t
                ([ "request", JStr request
                   "source", JStr source
                   "method", ModelWire.fingerprintJson m
                   "kind", JStr(kindTag kind)
                   "reading", readingJson reading
                   "withheld", strs withheld
                   "at", JStr at ]
                 @ optional "verdict" (verdict |> Option.map verdictJson))
        | SessionOp.Ran(request, statement, at) ->
            Canonical.typed t [ "request", JStr request; "statement", statementJson statement; "at", JStr at ]
        | SessionOp.ValuesRecorded(request, values) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "values", JArr(values |> List.map (fun (n, v) -> JObj [ "name", JStr n; "value", cellJson v ])) ]
        | SessionOp.Returned(request, rows, digest, at) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "rows", JInt rows
                  "digest", digestJson digest
                  "at", JStr at ]
        | SessionOp.RowsRecorded(request, rows) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "rows", JArr(rows |> List.map (fun r -> JArr(r |> List.map cellJson))) ]
        | SessionOp.Withheld(request, part, classifications) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "part", JStr(partTag part)
                  "classifications", strs classifications ]
        | SessionOp.Finished(request, rows, digest, at) ->
            Canonical.typed
                t
                [ "request", JStr request
                  "rows", JInt rows
                  "digest", digestJson digest
                  "at", JStr at ]
        | SessionOp.Failed(request, code, at) ->
            Canonical.typed t [ "request", JStr request; "code", JStr code; "at", JStr at ]

    /// An op's canonical text — what the chain hashes.
    let encodeOp (op: SessionOp) : string =
        Canonical.write Rendering.CrossHost (encodeOpJson op)

    // ---- decode ----

    let private parse (text: string) : Result<JVal, WireError> =
        Json.parse text |> Result.mapError WireError.NotJson

    let private typedOf (path: string) (v: JVal) =
        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs
            return tag, fs
        }

    let private unknown (path: string) (got: string) (expected: string list) : Result<'a, WireError> =
        Error(WireError.UnknownKind(path + ".$type", got, expected))

    let private optField (name: string) (fs: (string * JVal) list) =
        fs |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let private boolField (path: string) (name: string) fs =
        field path name fs
        |> Result.bind (fun v ->
            match v with
            | JBool b -> Ok b
            | _ -> Error(WireError.WrongShape(path + "." + name, "a boolean")))

    let private int64Field (path: string) (name: string) fs : Result<int64, WireError> =
        str path name fs
        |> Result.bind (fun s ->
            match Int64.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, n when n.ToString(CultureInfo.InvariantCulture) = s -> Ok n
            | _ -> Error(WireError.WrongShape(path + "." + name, "a 64-bit integer in canonical decimal text")))

    let private cellTags =
        [ "int"; "float"; "bool"; "string"; "date"; "timestamp"; "decimal"; "null" ]

    /// A cell from its typed object, strictly: the inverse of `cellJson`.
    let cellOf (path: string) (v: JVal) : Result<Cell, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "null" ->
                do! only path [ "$type" ] fs
                return Null
            | "int" ->
                do! only path [ "$type"; "v" ] fs
                let! n = intField path "v" fs
                return Int n
            | "bool" ->
                do! only path [ "$type"; "v" ] fs
                let! b = boolField path "v" fs
                return Bool b
            | "float" ->
                do! only path [ "$type"; "v" ] fs
                let! s = str path "v" fs

                match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, f when FloatLayout.canonical f = s -> return Float f
                | _ -> return! Error(WireError.WrongShape(path + ".v", "a float in canonical text"))
            | "string"
            | "date"
            | "timestamp"
            | "decimal" ->
                do! only path [ "$type"; "v" ] fs
                let! s = str path "v" fs

                return
                    match tag with
                    | "string" -> Str s
                    | "date" -> Date s
                    | "timestamp" -> Timestamp s
                    | _ -> Decimal s
            | other -> return! unknown path other cellTags
        }

    let private columnTypeOf (path: string) (tag: string) : Result<ColumnType, WireError> =
        match ColumnType.ofTag tag with
        | Some t -> Ok t
        | None ->
            Error(
                WireError.UnknownColumnType(
                    path,
                    tag,
                    [ IntType
                      FloatType
                      BoolType
                      StringType
                      DateType
                      (TimestampType TimeUnit.Seconds)
                      DecimalType ]
                    |> List.map ColumnType.tag
                )
            )

    let private typedPairOf (path: string) (v: JVal) : Result<string * ColumnType, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "name"; "type" ] fs
            let! name = str path "name" fs
            let! tag = str path "type" fs
            let! ty = columnTypeOf (path + ".type") tag
            return name, ty
        }

    /// A reported field or a statement's result column, strictly: the inverse of `fieldJson`. The type
    /// is checked here so an unknown one is refused as it always was; the metadata members go through
    /// Core's field codec. A column written as name and type alone, before result columns carried
    /// fields, reads as a field stating nothing.
    let private fieldOf (path: string) (v: JVal) : Result<Field, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "name"; "type"; "unit"; "label"; "description"; "ext" ] fs
            let! _ = str path "name" fs
            let! tag = str path "type" fs
            let! _ = columnTypeOf (path + ".type") tag

            return!
                ColumnCodec.decodeField v
                |> Result.mapError (fun e -> WireError.BadField(path, ColumnCodec.errorString e))
        }

    let private typedPairs (path: string) (name: string) fs =
        arr path name fs
        |> Result.bind (traverse (fun i v -> typedPairOf (sprintf "%s.%s[%d]" path name i) v))

    let private fieldsAt (path: string) (name: string) fs =
        arr path name fs
        |> Result.bind (traverse (fun i v -> fieldOf (sprintf "%s.%s[%d]" path name i) v))

    let private columnRefOf (path: string) (v: JVal) : Result<ColumnRef, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "table"; "column"; "classification" ] fs
            let! table = str path "table" fs
            let! column = str path "column" fs
            let! classification = str path "classification" fs

            return
                { Table = table
                  Column = column
                  Classification = classification }
        }

    let private limitsOf (path: string) (v: JVal) : Result<CostLimits, WireError> =
        result {
            let! fs = fieldsOf path v

            do! only path [ "maxRows"; "timeoutSeconds"; "maxJoins"; "maxGroupKeys"; "minGroupSize" ] fs

            let! maxRows = intField path "maxRows" fs
            let! timeout = intField path "timeoutSeconds" fs
            let! joins = optInt path "maxJoins" fs
            let! keys = optInt path "maxGroupKeys" fs
            let! floor = optInt path "minGroupSize" fs

            return
                { MaxRows = maxRows
                  MaxJoins = joins
                  MaxGroupKeys = keys
                  MinGroupSize = floor
                  TimeoutSeconds = timeout }
        }

    let private exprOf (path: string) (v: JVal) : Result<ColExpr, WireError> =
        DataFrameCodec.decodeExpr v
        |> Result.mapError (fun e -> WireError.BadExpression(path, ColumnCodec.errorString e))

    let private ruleOf (path: string) (v: JVal) : Result<PolicyRule, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "columnsGranted" ->
                do! only path [ "$type"; "columns" ] fs
                let! cols = arr path "columns" fs

                let! granted =
                    cols
                    |> traverse (fun i c ->
                        let p = sprintf "%s.columns[%d]" path i

                        result {
                            let! cfs = fieldsOf p c
                            do! only p [ "column"; "level" ] cfs
                            let! colJson = field p "column" cfs
                            let! col = columnRefOf (p + ".column") colJson
                            let! level = str p "level" cfs

                            match level with
                            | "aggregate" -> return col, GrantLevel.Aggregate
                            | "row" -> return col, GrantLevel.RowLevel
                            | other ->
                                return! Error(WireError.UnknownKind(p + ".level", other, [ "aggregate"; "row" ]))
                        })

                return PolicyRule.ColumnsGranted granted
            | "rowPolicyApplied" ->
                do! only path [ "$type"; "table"; "policy"; "at"; "predicate" ] fs
                let! table = str path "table" fs
                let! policy = str path "policy" fs
                let! at = intField path "at" fs
                let! pj = field path "predicate" fs
                let! predicate = exprOf (path + ".predicate") pj
                return PolicyRule.RowPolicyApplied(table, policy, at, predicate)
            | "groupFloorApplied" ->
                do! only path [ "$type"; "step"; "floor"; "columns" ] fs
                let! step = intField path "step" fs
                let! floor = intField path "floor" fs
                let! cols = arr path "columns" fs
                let! refs = cols |> traverse (fun i c -> columnRefOf (sprintf "%s.columns[%d]" path i) c)
                return PolicyRule.GroupFloorApplied(step, floor, refs)
            | "costAdmitted" ->
                do! only path [ "$type"; "rows"; "joins"; "groupKeys"; "limits" ] fs
                let! rows = intField path "rows" fs
                let! joins = intField path "joins" fs
                let! keys = intField path "groupKeys" fs
                let! lj = field path "limits" fs
                let! limits = limitsOf (path + ".limits") lj
                return PolicyRule.CostAdmitted(rows, joins, keys, limits)
            | other ->
                return! unknown path other [ "columnsGranted"; "rowPolicyApplied"; "groupFloorApplied"; "costAdmitted" ]
        }

    let private pipelineOf (path: string) (name: string) fs : Result<Transform list, WireError> =
        arr path name fs
        |> Result.bind (
            traverse (fun i v ->
                DataFrameCodec.decodeTransform v
                |> Result.mapError (fun e ->
                    WireError.BadExpression(sprintf "%s.%s[%d]" path name i, ColumnCodec.errorString e)))
        )

    let private kindOf (path: string) (tag: string) : Result<FingerprintKind, WireError> =
        match tag with
        | "exact" -> Ok FingerprintKind.Exact
        | "heuristic" -> Ok FingerprintKind.Heuristic
        | other -> Error(WireError.UnknownKind(path, other, [ "exact"; "heuristic" ]))

    let private readingOf (path: string) (v: JVal) : Result<FingerprintReading, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "counter" ->
                do! only path [ "$type"; "position" ] fs
                let! position = str path "position" fs
                return FingerprintReading.Counter position
            | "tables" ->
                do! only path [ "$type"; "tables" ] fs
                let! tables = arr path "tables" fs

                let! rows =
                    tables
                    |> traverse (fun i t ->
                        let p = sprintf "%s.tables[%d]" path i

                        result {
                            let! tfs = fieldsOf p t
                            do! only p [ "table"; "rows"; "maxChange" ] tfs
                            let! table = str p "table" tfs
                            let! count = int64Field p "rows" tfs
                            let! mj = field p "maxChange" tfs
                            let! maxChange = cellOf (p + ".maxChange") mj

                            return
                                { Table = table
                                  Rows = count
                                  MaxChange = maxChange }
                        })

                return FingerprintReading.Tables rows
            | "file" ->
                do! only path [ "$type"; "size"; "modifiedUtcTicks"; "contentHash" ] fs
                let! size = int64Field path "size" fs
                let! ticks = int64Field path "modifiedUtcTicks" fs

                let! hash =
                    match optField "contentHash" fs with
                    | None -> Ok None
                    | Some(JStr h) -> Ok(Some h)
                    | Some _ -> Error(WireError.WrongShape(path + ".contentHash", "a string"))

                return
                    FingerprintReading.File
                        { Size = size
                          ModifiedUtcTicks = ticks
                          ContentHash = hash }
            | other -> return! unknown path other [ "counter"; "tables"; "file" ]
        }

    let private verdictOf (path: string) (v: JVal) : Result<FingerprintVerdict, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "changed" ->
                do! only path [ "$type"; "what" ] fs
                let! what = strList path "what" fs
                return FingerprintVerdict.Changed what
            | "unchanged" ->
                do! only path [ "$type" ] fs
                return FingerprintVerdict.Unchanged
            | "noChangeSeen" ->
                do! only path [ "$type"; "misses" ] fs
                let! misses = strList path "misses" fs
                return FingerprintVerdict.NoChangeSeen misses
            | "incomparable" ->
                do! only path [ "$type"; "reason" ] fs
                let! reason = str path "reason" fs
                return FingerprintVerdict.Incomparable reason
            | other -> return! unknown path other [ "changed"; "unchanged"; "noChangeSeen"; "incomparable" ]
        }

    let private schemaOf (path: string) (v: JVal) : Result<ReportedSchema, WireError> =
        match v with
        | JArr tables ->
            tables
            |> traverse (fun i t ->
                let p = sprintf "%s[%d]" path i

                result {
                    let! fs = fieldsOf p t
                    do! only p [ "name"; "columns" ] fs
                    let! name = str p "name" fs

                    let! columns = fieldsAt p "columns" fs

                    return { Name = name; Columns = columns }
                })
            |> Result.map (fun ts -> { Tables = ts })
        | _ -> Error(WireError.WrongShape(path, "an array"))

    let private driftOf (path: string) (v: JVal) : Result<TableDrift, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "drifted" ->
                do! only path [ "$type"; "table"; "delta" ] fs
                let! table = str path "table" fs
                let! dj = field path "delta" fs

                let! delta =
                    SchemaDeltaCodec.decodeJson dj
                    |> Result.mapError (fun e -> WireError.WrongShape(path + ".delta", ColumnCodec.errorString e))

                return TableDrift.Drifted(table, delta)
            | "inStep"
            | "missingAtSource"
            | "unmodelled" ->
                do! only path [ "$type"; "table" ] fs
                let! table = str path "table" fs

                return
                    match tag with
                    | "inStep" -> TableDrift.InStep table
                    | "missingAtSource" -> TableDrift.MissingAtSource table
                    | _ -> TableDrift.Unmodelled table
            | other -> return! unknown path other [ "inStep"; "drifted"; "missingAtSource"; "unmodelled" ]
        }

    let private digestOf (path: string) (v: JVal) : Result<ResultDigest, WireError> =
        result {
            let! tag, fs = typedOf path v

            match tag with
            | "plain"
            | "committed" ->
                do! only path [ "$type"; "digest" ] fs
                let! d = str path "digest" fs

                return
                    if tag = "plain" then
                        ResultDigest.Plain d
                    else
                        ResultDigest.Committed d
            | other -> return! unknown path other [ "plain"; "committed" ]
        }

    let private optStr (path: string) (name: string) fs : Result<string option, WireError> =
        match optField name fs with
        | None -> Ok None
        | Some(JStr s) -> Ok(Some s)
        | Some _ -> Error(WireError.WrongShape(path + "." + name, "a string"))

    let private statementOf (path: string) (v: JVal) : Result<StatementRecord, WireError> =
        result {
            let! fs = fieldsOf path v

            do! only path [ "hash"; "text"; "parameters"; "columns"; "timeoutSeconds"; "captureKey" ] fs

            let! hash = str path "hash" fs
            let! text = str path "text" fs
            let! parameters = typedPairs path "parameters" fs
            let! columns = fieldsAt path "columns" fs
            let! timeout = optInt path "timeoutSeconds" fs
            let! key = optStr path "captureKey" fs

            return
                { Hash = hash
                  Text = text
                  Parameters = parameters
                  Columns = columns
                  TimeoutSeconds = timeout
                  CaptureKey = key }
        }

    /// The member names each op declares beside `$type`.
    let private members (tag: string) : string list =
        match tag with
        | "opened" ->
            [ "session"
              "model"
              "modelHash"
              "recordable"
              "dialect"
              "dialectVersion"
              "providerVersion"
              "at" ]
        | "schemaReported" -> [ "source"; "schema"; "drift"; "at" ]
        | "received" -> [ "request"; "table"; "pipeline"; "redacted"; "at" ]
        | "admitted" -> [ "request"; "principal"; "reads"; "rules"; "pipeline"; "redacted" ]
        | "refused" -> [ "request"; "principal"; "code" ]
        | "lowered" -> [ "request"; "lowered"; "suffix"; "redacted"; "boundary" ]
        | "fingerprinted" ->
            [ "request"
              "source"
              "method"
              "kind"
              "reading"
              "withheld"
              "verdict"
              "at" ]
        | "ran" -> [ "request"; "statement"; "at" ]
        | "valuesRecorded" -> [ "request"; "values" ]
        | "returned"
        | "finished" -> [ "request"; "rows"; "digest"; "at" ]
        | "rowsRecorded" -> [ "request"; "rows" ]
        | "withheld" -> [ "request"; "part"; "classifications" ]
        | "failed" -> [ "request"; "code"; "at" ]
        | _ -> []

    /// An op from a `JVal`, strictly.
    let decodeOpJson (v: JVal) : Result<SessionOp, WireError> =
        let p = "$"

        result {
            let! tag, fs = typedOf p v

            if not (List.contains tag SessionOps.tags) then
                return! unknown p tag SessionOps.tags
            else
                do! only p ("$type" :: members tag) fs

                match tag with
                | "opened" ->
                    let! session = str p "session" fs
                    let! model = str p "model" fs
                    let! modelHash = str p "modelHash" fs
                    let! recordable = strList p "recordable" fs
                    let! dialect = str p "dialect" fs
                    let! dialectVersion = str p "dialectVersion" fs
                    let! providerVersion = str p "providerVersion" fs
                    let! at = str p "at" fs

                    return
                        SessionOp.Opened
                            { Session = session
                              Model = model
                              ModelHash = modelHash
                              Recordable = recordable
                              Dialect = dialect
                              DialectVersion = dialectVersion
                              ProviderVersion = providerVersion
                              At = at }
                | "schemaReported" ->
                    let! source = str p "source" fs
                    let! sj = field p "schema" fs
                    let! schema = schemaOf (p + ".schema") sj
                    let! at = str p "at" fs

                    let! drift =
                        match optField "drift" fs with
                        | None -> Ok None
                        | Some(JArr ds) ->
                            ds
                            |> traverse (fun i d -> driftOf (sprintf "%s.drift[%d]" p i) d)
                            |> Result.map Some
                        | Some _ -> Error(WireError.WrongShape(p + ".drift", "an array"))

                    return SessionOp.SchemaReported(source, schema, drift, at)
                | "received" ->
                    let! request = str p "request" fs
                    let! table = str p "table" fs
                    let! pipeline = pipelineOf p "pipeline" fs
                    let! redacted = intField p "redacted" fs
                    let! at = str p "at" fs
                    return SessionOp.Received(request, table, pipeline, redacted, at)
                | "admitted" ->
                    let! request = str p "request" fs
                    let! principal = str p "principal" fs
                    let! reads = strList p "reads" fs
                    let! rulesJson = arr p "rules" fs
                    let! rules = rulesJson |> traverse (fun i r -> ruleOf (sprintf "%s.rules[%d]" p i) r)
                    let! pipeline = pipelineOf p "pipeline" fs
                    let! redacted = intField p "redacted" fs
                    return SessionOp.Admitted(request, principal, reads, rules, pipeline, redacted)
                | "refused" ->
                    let! request = str p "request" fs
                    let! principal = str p "principal" fs
                    let! code = str p "code" fs
                    return SessionOp.Refused(request, principal, code)
                | "lowered" ->
                    let! request = str p "request" fs
                    let! lowered = intField p "lowered" fs
                    let! suffix = pipelineOf p "suffix" fs
                    let! redacted = intField p "redacted" fs
                    let! boundary = optStr p "boundary" fs
                    return SessionOp.Lowered(request, lowered, suffix, redacted, boundary)
                | "fingerprinted" ->
                    let! request = str p "request" fs
                    let! source = str p "source" fs
                    let! mj = field p "method" fs
                    let! m = ModelWire.fingerprintOf (p + ".method") mj
                    let! kt = str p "kind" fs
                    let! kind = kindOf (p + ".kind") kt
                    let! rj = field p "reading" fs
                    let! reading = readingOf (p + ".reading") rj
                    let! withheld = strList p "withheld" fs
                    let! at = str p "at" fs

                    let! verdict =
                        match optField "verdict" fs with
                        | None -> Ok None
                        | Some vj -> verdictOf (p + ".verdict") vj |> Result.map Some

                    return SessionOp.Fingerprinted(request, source, m, kind, reading, withheld, verdict, at)
                | "ran" ->
                    let! request = str p "request" fs
                    let! sj = field p "statement" fs
                    let! statement = statementOf (p + ".statement") sj
                    let! at = str p "at" fs
                    return SessionOp.Ran(request, statement, at)
                | "valuesRecorded" ->
                    let! request = str p "request" fs
                    let! vs = arr p "values" fs

                    let! values =
                        vs
                        |> traverse (fun i v ->
                            let vp = sprintf "%s.values[%d]" p i

                            result {
                                let! vfs = fieldsOf vp v
                                do! only vp [ "name"; "value" ] vfs
                                let! name = str vp "name" vfs
                                let! cj = field vp "value" vfs
                                let! cell = cellOf (vp + ".value") cj
                                return name, cell
                            })

                    return SessionOp.ValuesRecorded(request, values)
                | "returned"
                | "finished" ->
                    let! request = str p "request" fs
                    let! rows = intField p "rows" fs
                    let! dj = field p "digest" fs
                    let! digest = digestOf (p + ".digest") dj
                    let! at = str p "at" fs

                    return
                        if tag = "returned" then
                            SessionOp.Returned(request, rows, digest, at)
                        else
                            SessionOp.Finished(request, rows, digest, at)
                | "rowsRecorded" ->
                    let! request = str p "request" fs
                    let! rs = arr p "rows" fs

                    let! rows =
                        rs
                        |> traverse (fun i r ->
                            match r with
                            | JArr cells -> cells |> traverse (fun j c -> cellOf (sprintf "%s.rows[%d][%d]" p i j) c)
                            | _ -> Error(WireError.WrongShape(sprintf "%s.rows[%d]" p i, "an array")))

                    return SessionOp.RowsRecorded(request, rows)
                | "withheld" ->
                    let! request = str p "request" fs
                    let! part = str p "part" fs
                    let! classifications = strList p "classifications" fs

                    match part with
                    | "parameters" -> return SessionOp.Withheld(request, WithheldPart.Parameters, classifications)
                    | "rows" -> return SessionOp.Withheld(request, WithheldPart.Rows, classifications)
                    | other -> return! Error(WireError.UnknownKind(p + ".part", other, [ "parameters"; "rows" ]))
                | _ ->
                    // "failed" — the tag set was checked above
                    let! request = str p "request" fs
                    let! code = str p "code" fs
                    let! at = str p "at" fs
                    return SessionOp.Failed(request, code, at)
        }

    /// An op from canonical text.
    let decodeOp (text: string) : Result<SessionOp, WireError> = parse text |> Result.bind decodeOpJson

    /// Core's stream witness over the record: the reducer and this codec.
    let streamWitness: StreamWitness<SessionOp, SessionState, SessionRejection> =
        { Apply = SessionOps.apply
          Encode = encodeOp
          Decode = fun text -> decodeOp text |> Result.mapError ModelWire.describe }

    /// The session's op stream: the canonical config over the SHA-256 chain hash.
    let stream: Stream<SessionOp, SessionState, SessionRejection> =
        OpStream.canonical hashFn streamWitness

    // ---- the session as wire ----

    /// A session as canonical wire: Core's JSONL, one record per line, no trailing newline.
    let write (records: OpRecord<SessionOp> list) : string = Jsonl.write streamWitness records

    let private recordOf (line: JsonlLine) : Result<OpRecord<SessionOp>, JsonlFault> =
        Jsonl.intField "seq" line
        |> Result.bind (fun seq ->
            Jsonl.actorField "actor" line
            |> Result.bind (fun actor ->
                Jsonl.rawField "op" line
                |> Result.bind (fun raw ->
                    decodeOp raw
                    |> Result.mapError (fun e -> Jsonl.refuse line (ModelWire.describe e)))
                |> Result.bind (fun op ->
                    Jsonl.stringField "prevHash" line
                    |> Result.bind (fun prevHash ->
                        Jsonl.stringField "hash" line
                        |> Result.map (fun hash ->
                            { Seq = seq
                              Actor = actor
                              Op = op
                              PrevHash = prevHash
                              Hash = hash })))))

    /// Read a session's wire, verify its chain and fold it. `expectedHead`, when given, is the head the
    /// reader holds from elsewhere (an attestation, a previous read): a stream whose head is not it is
    /// refused as rewound, because a hash chain alone authenticates every prefix of itself, and a
    /// truncation at a line boundary verifies.
    let read
        (expectedHead: string option)
        (text: string)
        : Result<OpRecord<SessionOp> list * SessionState, SessionReadFault> =
        let lastLine =
            text.Replace("\r\n", "\n").Split('\n')
            |> Array.mapi (fun i l -> i + 1, l)
            |> Array.filter (fun (_, l) -> l.Trim() <> "")
            |> Array.tryLast
            |> Option.map fst
            |> Option.defaultValue 0

        match Jsonl.scanRecords recordOf text with
        | Error f when f.Line = lastLine -> Error(SessionReadFault.TornTail f)
        | Error f -> Error(SessionReadFault.Malformed f)
        | Ok records ->
            match OpStream.firstChainBreak stream records with
            | Some b -> Error(SessionReadFault.Broken b)
            | None ->
                let head = OpStream.head StreamConfig.canonical records

                match expectedHead with
                | Some expected when expected <> head -> Error(SessionReadFault.Rewound(expected, head))
                | _ ->
                    match OpStream.replay streamWitness SessionOps.empty records with
                    | Error(i, e) -> Error(SessionReadFault.Refused(i, e))
                    | Ok state -> Ok(records, state)

    /// The read fault's stable code.
    let faultCode (f: SessionReadFault) : string =
        match f with
        | SessionReadFault.TornTail _ -> "DB-SESSION-TORN-TAIL"
        | SessionReadFault.Malformed _ -> "DB-SESSION-MALFORMED"
        | SessionReadFault.Broken _ -> "DB-SESSION-CHAIN-BROKEN"
        | SessionReadFault.Rewound _ -> "DB-SESSION-REWOUND-HEAD"
        | SessionReadFault.Refused _ -> "DB-SESSION-REFUSED"

    /// The read fault as one sentence.
    let describeFault (f: SessionReadFault) : string =
        match f with
        | SessionReadFault.TornTail j -> "the tail is torn: " + JsonlFault.toString j
        | SessionReadFault.Malformed j -> "a line does not scan: " + JsonlFault.toString j
        | SessionReadFault.Broken b ->
            sprintf "the chain breaks at record %d (%s)" b.Index (ChainBreakReason.toString b.Reason)
        | SessionReadFault.Rewound(expected, actual) ->
            sprintf "the head is %s; the reader holds %s" (if actual = "" then "empty" else actual) expected
        | SessionReadFault.Refused(i, r) -> sprintf "record %d does not fold: %s" i (SessionOps.describe r)
