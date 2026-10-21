namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

/// Why a wire text is not a dataset model (or a model op). Each case carries the JSON path it
/// names; `ModelWire.code` gives the stable code a conformance vector asserts.
[<RequireQualifiedAccess>]
type WireError =
    | NotJson of detail: string
    | MissingField of path: string * field: string
    | UnknownField of path: string * field: string
    | WrongShape of path: string * expected: string
    | UnknownKind of path: string * got: string * expected: string list
    | UnknownColumnType of path: string * got: string * expected: string list
    | UnknownAggregate of path: string * got: string * expected: string list
    | BadExpression of path: string * detail: string
    /// A column's field metadata (its unit, label, description or extension members) that Core's
    /// field codec refuses — an unparseable unit, a non-string label (Phase 2).
    | BadField of path: string * detail: string
    | UnsupportedVersion of got: int * supported: int list

/// The strict reading helpers every wire codec in the domain decodes through: a field is present,
/// of its kind, and no field outside a shape's declared set is admitted. Each failure names its JSON
/// path as a `WireError`.
module WireDecode =

    let fieldsOf (path: string) (v: JVal) : Result<(string * JVal) list, WireError> =
        match v with
        | JObj fs -> Ok fs
        | _ -> Error(WireError.WrongShape(path, "an object"))

    let only (path: string) (allowed: string list) (fs: (string * JVal) list) : Result<unit, WireError> =
        match fs |> List.tryFind (fun (k, _) -> not (List.contains k allowed)) with
        | Some(k, _) -> Error(WireError.UnknownField(path, k))
        | None -> Ok()

    let field (path: string) (name: string) (fs: (string * JVal) list) : Result<JVal, WireError> =
        match fs |> List.tryFind (fun (k, _) -> k = name) with
        | Some(_, v) -> Ok v
        | None -> Error(WireError.MissingField(path, name))

    let str (path: string) (name: string) fs : Result<string, WireError> =
        field path name fs
        |> Result.bind (fun v ->
            match v with
            | JStr s -> Ok s
            | _ -> Error(WireError.WrongShape(path + "." + name, "a string")))

    let intField (path: string) (name: string) fs : Result<int, WireError> =
        field path name fs
        |> Result.bind (fun v ->
            match v with
            | JInt i -> Ok i
            | _ -> Error(WireError.WrongShape(path + "." + name, "an integer")))

    /// An optional integer field: absent is None; present must be an integer.
    let optInt (path: string) (name: string) (fs: (string * JVal) list) : Result<int option, WireError> =
        match fs |> List.tryFind (fun (k, _) -> k = name) with
        | None -> Ok None
        | Some(_, JInt i) -> Ok(Some i)
        | Some _ -> Error(WireError.WrongShape(path + "." + name, "an integer"))

    let arr (path: string) (name: string) fs : Result<JVal list, WireError> =
        field path name fs
        |> Result.bind (fun v ->
            match v with
            | JArr xs -> Ok xs
            | _ -> Error(WireError.WrongShape(path + "." + name, "an array")))

    let traverse (f: int -> 'a -> Result<'b, WireError>) (xs: 'a list) : Result<'b list, WireError> =
        xs
        |> List.indexed
        |> List.fold (fun acc (i, x) -> acc |> Result.bind (fun ys -> f i x |> Result.map (fun y -> y :: ys))) (Ok [])
        |> Result.map List.rev

    let strList (path: string) (name: string) fs : Result<string list, WireError> =
        arr path name fs
        |> Result.bind (
            traverse (fun i v ->
                match v with
                | JStr s -> Ok s
                | _ -> Error(WireError.WrongShape(sprintf "%s.%s[%d]" path name i, "a string")))
        )

    /// The `Result` computation expression the decoders are written in.
    type ResultBuilder() =
        member _.Bind(r: Result<'a, WireError>, f: 'a -> Result<'b, WireError>) = Result.bind f r
        member _.Return(x: 'a) : Result<'a, WireError> = Ok x
        member _.ReturnFrom(r: Result<'a, WireError>) = r

    let result = ResultBuilder()

/// The canonical wire codec for the dataset model, its state and its ops. Canonical: object keys
/// sorted (Core's `Canon.render`), `$type`-tagged, one spelling per value, so equal models render
/// to equal bytes. Strict: an unknown field, a missing one, or a kind the vocabulary does not
/// declare is a named refusal, never a default.
module ModelWire =

    /// The wire version this codec writes and the only one it reads.
    [<Literal>]
    let Version = 1

    /// The stable code of a wire error.
    let code (e: WireError) : string =
        match e with
        | WireError.NotJson _ -> "DB-WIRE-NOT-JSON"
        | WireError.MissingField _ -> "DB-WIRE-MISSING-FIELD"
        | WireError.UnknownField _ -> "DB-WIRE-UNKNOWN-FIELD"
        | WireError.WrongShape _ -> "DB-WIRE-WRONG-SHAPE"
        | WireError.UnknownKind _ -> "DB-WIRE-UNKNOWN-KIND"
        | WireError.UnknownColumnType _ -> "DB-WIRE-UNKNOWN-COLUMN-TYPE"
        | WireError.UnknownAggregate _ -> "DB-WIRE-UNKNOWN-AGGREGATE"
        | WireError.BadExpression _ -> "DB-WIRE-BAD-EXPRESSION"
        | WireError.BadField _ -> "DB-WIRE-BAD-FIELD"
        | WireError.UnsupportedVersion _ -> "DB-WIRE-UNSUPPORTED-VERSION"

    /// A wire error as one sentence.
    let describe (e: WireError) : string =
        match e with
        | WireError.NotJson d -> "not JSON: " + d
        | WireError.MissingField(p, f) -> sprintf "%s: missing field '%s'" p f
        | WireError.UnknownField(p, f) -> sprintf "%s: field '%s' is not part of this shape" p f
        | WireError.WrongShape(p, x) -> sprintf "%s: expected %s" p x
        | WireError.UnknownKind(p, g, xs) -> sprintf "%s: kind '%s' is not one of %s" p g (String.concat ", " xs)
        | WireError.UnknownColumnType(p, g, xs) ->
            sprintf "%s: column type '%s' is not one of %s" p g (String.concat ", " xs)
        | WireError.UnknownAggregate(p, g, xs) ->
            sprintf "%s: aggregate '%s' is not one of %s" p g (String.concat ", " xs)
        | WireError.BadExpression(p, d) -> sprintf "%s: %s" p d
        | WireError.BadField(p, d) -> sprintf "%s: %s" p d
        | WireError.UnsupportedVersion(g, xs) ->
            sprintf "version %d is not supported; this codec reads %s" g (xs |> List.map string |> String.concat ", ")

    // ---- aggregates: the transform algebra's own tags ----

    let aggTag (fn: AggFn) : string =
        match fn with
        | Sum -> "sum"
        | Mean -> "mean"
        | Min -> "min"
        | Max -> "max"
        | Count -> "count"
        | Median -> "median"
        | StdDev -> "stddev"
        | First -> "first"
        | Last -> "last"
        | CountDistinct -> "countDistinct"

    let private allAggs =
        [ Sum; Mean; Min; Max; Count; Median; StdDev; First; Last; CountDistinct ]

    let private aggOf (tag: string) : AggFn option =
        allAggs |> List.tryFind (fun a -> aggTag a = tag)

    // ---- encode ----

    let private strs (xs: string list) = JArr(xs |> List.map JStr)

    /// The members of Core's field document that carry a field's metadata — beside `name` and `type`,
    /// which a column writes itself.
    let private metadataMembers = [ "unit"; "label"; "description"; "ext" ]

    /// A field's stated metadata as Core's field codec writes it: the members of
    /// `ColumnCodec.fieldJson` beyond `name` and `type`, none when the field states none.
    let metadataJson (f: Field) : (string * JVal) list =
        match ColumnCodec.fieldJson f with
        | JObj members -> members |> List.filter (fun (k, _) -> List.contains k metadataMembers)
        | _ -> []

    /// A fingerprint method as a typed object.
    let fingerprintJson (m: FingerprintMethod) : JVal =
        match m with
        | FingerprintMethod.ChangeCounter -> Canon.typed "changeCounter" []
        | FingerprintMethod.RowCountMaxChange pairs ->
            Canon.typed
                "rowCountMaxChange"
                [ "tables", JArr(pairs |> List.map (fun (t, c) -> JObj [ "table", JStr t; "column", JStr c ])) ]
        | FingerprintMethod.FileStat hash -> Canon.typed "fileStat" [ "contentHash", JBool hash ]

    /// A kind's tag and its payload fields.
    let private kindFields (k: NodeKind) : (string * JVal) list =
        match k with
        | NodeKind.Model s ->
            // The recordable set is written only when it names something, so a model that declares
            // none renders to the bytes it rendered before the field existed (DECISIONS.md D9).
            [ "name", JStr s.Name; "classifications", strs s.Classifications ]
            @ (if List.isEmpty s.Recordable then
                   []
               else
                   [ "recordable", strs s.Recordable ])
        | NodeKind.Source s ->
            // The fingerprint is written only when declared, so a source that declares none renders
            // to the bytes it rendered before the field existed (DECISIONS.md D6).
            [ "name", JStr s.Name ]
            @ (s.Fingerprint
               |> Option.map (fun m -> "fingerprint", fingerprintJson m)
               |> Option.toList)
        | NodeKind.Table s -> [ "name", JStr s.Name; "physical", JStr s.Physical ]
        | NodeKind.Column s ->
            // The field's metadata members are Core's field document's own (`ColumnCodec.fieldJson`),
            // written only when stated, so a column stating none renders to the bytes it rendered
            // before they existed (DECISIONS.md D12).
            [ "name", JStr s.Name
              "physical", JStr s.Physical
              "type", JStr(ColumnType.tag s.Type)
              "classification", JStr s.Classification ]
            @ metadataJson s.Field
        | NodeKind.Join s ->
            [ "name", JStr s.Name
              "left", JStr s.Left
              "right", JStr s.Right
              "on", JArr(s.On |> List.map (fun (l, r) -> JObj [ "left", JStr l; "right", JStr r ])) ]
        | NodeKind.Measure s -> [ "name", JStr s.Name; "fn", JStr(aggTag s.Fn); "column", JStr s.Column ]
        | NodeKind.RowPolicy s -> [ "name", JStr s.Name; "predicate", DataFrameCodec.encodeExpr s.Predicate ]
        | NodeKind.Limit s ->
            // The four caps are optional and written only when declared, so a limit that declares
            // none renders to the bytes it rendered before they existed (DECISIONS.md D5).
            let optional (name: string) (v: int option) =
                v |> Option.map (fun n -> name, JInt n) |> Option.toList

            [ "name", JStr s.Name; "maxRows", JInt s.MaxRows ]
            @ optional "maxJoins" s.MaxJoins
            @ optional "maxGroupKeys" s.MaxGroupKeys
            @ optional "minGroupSize" s.MinGroupSize
            @ optional "timeoutSeconds" s.TimeoutSeconds

    let private isContainerTag (tag: string) =
        not (List.isEmpty (ModelNode.allowedChildren tag))

    let rec private nodeJson (n: ModelNode) : JVal =
        let tag = ModelNode.kindTag n.Kind

        let children =
            if isContainerTag tag then
                [ "children", JArr(n.Children |> List.map nodeJson) ]
            else
                []

        Canon.typed tag (("id", JStr n.Id) :: kindFields n.Kind @ children)

    /// A node (and its subtree) as a `JVal`. A leaf kind writes no `children` field.
    let encodeNodeJson (n: ModelNode) : JVal = nodeJson n

    /// A model state as a `JVal`: the versioned envelope.
    let encodeStateJson (s: ModelState) : JVal =
        Canon.typed
            "datasetModel"
            [ "version", JInt Version
              "root", nodeJson s.Root
              "retired", strs (Set.toList s.Retired) ]

    /// An op as a `JVal`.
    let encodeOpJson (op: ModelOp) : JVal =
        match op with
        | ModelOp.Add(parent, node) -> Canon.typed "add" [ "parent", JStr parent; "node", nodeJson node ]
        | ModelOp.Amend(id, kind) ->
            Canon.typed
                "amend"
                [ "id", JStr id
                  "kind", Canon.typed (ModelNode.kindTag kind) (kindFields kind) ]
        | ModelOp.Retire id -> Canon.typed "retire" [ "id", JStr id ]

    /// A node's canonical text.
    let encodeNode (n: ModelNode) : string = Canon.render (nodeJson n)

    /// A model state's canonical text.
    let encodeState (s: ModelState) : string = Canon.render (encodeStateJson s)

    /// An op's canonical text — what the op-stream hashes.
    let encodeOp (op: ModelOp) : string = Canon.render (encodeOpJson op)

    // ---- decode ----

    open WireDecode

    /// The payload fields a kind declares, beside `$type`.
    let private payloadFields (tag: string) : string list =
        match tag with
        | "model" -> [ "name"; "classifications"; "recordable" ]
        | "source" -> [ "name"; "fingerprint" ]
        | "table" -> [ "name"; "physical" ]
        | "column" -> [ "name"; "physical"; "type"; "classification" ] @ metadataMembers
        | "join" -> [ "name"; "left"; "right"; "on" ]
        | "measure" -> [ "name"; "fn"; "column" ]
        | "rowPolicy" -> [ "name"; "predicate" ]
        | "limit" ->
            [ "name"
              "maxRows"
              "maxJoins"
              "maxGroupKeys"
              "minGroupSize"
              "timeoutSeconds" ]
        | _ -> []

    let private pairOf (path: string) (v: JVal) : Result<string * string, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "left"; "right" ] fs
            let! l = str path "left" fs
            let! r = str path "right" fs
            return l, r
        }

    let private tablePairOf (path: string) (v: JVal) : Result<string * string, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "table"; "column" ] fs
            let! t = str path "table" fs
            let! c = str path "column" fs
            return t, c
        }

    /// A fingerprint method from its typed object, strictly: the inverse of `fingerprintJson`.
    let fingerprintOf (path: string) (v: JVal) : Result<FingerprintMethod, WireError> =
        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs

            match tag with
            | "changeCounter" ->
                do! only path [ "$type" ] fs
                return FingerprintMethod.ChangeCounter
            | "rowCountMaxChange" ->
                do! only path [ "$type"; "tables" ] fs
                let! tables = arr path "tables" fs
                let! pairs = tables |> traverse (fun i t -> tablePairOf (sprintf "%s.tables[%d]" path i) t)
                return FingerprintMethod.RowCountMaxChange pairs
            | "fileStat" ->
                do! only path [ "$type"; "contentHash" ] fs
                let! hash = field path "contentHash" fs

                match hash with
                | JBool b -> return FingerprintMethod.FileStat b
                | _ -> return! Error(WireError.WrongShape(path + ".contentHash", "a boolean"))
            | other ->
                return!
                    Error(
                        WireError.UnknownKind(
                            path + ".$type",
                            other,
                            [ "changeCounter"; "rowCountMaxChange"; "fileStat" ]
                        )
                    )
        }

    let private kindOf (path: string) (tag: string) fs : Result<NodeKind, WireError> =
        match tag with
        | "model" ->
            result {
                let! name = str path "name" fs
                let! cls = strList path "classifications" fs

                // Absent is the empty set; an empty set is written by omission, so a present empty
                // array is a second spelling of one value and is refused.
                let! recordable =
                    match fs |> List.tryFind (fun (k, _) -> k = "recordable") with
                    | None -> Ok []
                    | Some(_, JArr []) ->
                        Error(WireError.WrongShape(path + ".recordable", "a non-empty array (an empty set is omitted)"))
                    | Some _ -> strList path "recordable" fs

                return
                    NodeKind.Model
                        { Name = name
                          Classifications = cls
                          Recordable = recordable }
            }
        | "source" ->
            result {
                let! name = str path "name" fs

                let! fingerprint =
                    match fs |> List.tryFind (fun (k, _) -> k = "fingerprint") with
                    | None -> Ok None
                    | Some(_, v) -> fingerprintOf (path + ".fingerprint") v |> Result.map Some

                return
                    NodeKind.Source
                        { Name = name
                          Fingerprint = fingerprint }
            }
        | "table" ->
            result {
                let! name = str path "name" fs
                let! physical = str path "physical" fs
                return NodeKind.Table { Name = name; Physical = physical }
            }
        | "column" ->
            result {
                let! name = str path "name" fs
                let! physical = str path "physical" fs
                let! tyTag = str path "type" fs
                let! cls = str path "classification" fs

                let! _ =
                    match ColumnType.ofTag tyTag with
                    | Some ty -> Ok ty
                    | None -> Error(WireError.UnknownColumnType(path + ".type", tyTag, ColumnType.allTags))

                let! field =
                    JObj(
                        [ "name", JStr name; "type", JStr tyTag ]
                        @ (fs |> List.filter (fun (k, _) -> List.contains k metadataMembers))
                    )
                    |> ColumnCodec.decodeField
                    |> Result.mapError (fun e -> WireError.BadField(path, ColumnCodec.errorString e))

                return
                    NodeKind.Column
                        { Field = field
                          Physical = physical
                          Classification = cls }
            }
        | "join" ->
            result {
                let! name = str path "name" fs
                let! left = str path "left" fs
                let! right = str path "right" fs
                let! on = arr path "on" fs
                let! pairs = on |> traverse (fun i v -> pairOf (sprintf "%s.on[%d]" path i) v)

                return
                    NodeKind.Join
                        { Name = name
                          Left = left
                          Right = right
                          On = pairs }
            }
        | "measure" ->
            result {
                let! name = str path "name" fs
                let! fnTag = str path "fn" fs
                let! column = str path "column" fs

                let! fn =
                    match aggOf fnTag with
                    | Some fn -> Ok fn
                    | None -> Error(WireError.UnknownAggregate(path + ".fn", fnTag, allAggs |> List.map aggTag))

                return
                    NodeKind.Measure
                        { Name = name
                          Fn = fn
                          Column = column }
            }
        | "rowPolicy" ->
            result {
                let! name = str path "name" fs
                let! pred = field path "predicate" fs

                let! expr =
                    DataFrameCodec.decodeExpr pred
                    |> Result.mapError (fun e -> WireError.BadExpression(path + ".predicate", sprintf "%A" e))

                return NodeKind.RowPolicy { Name = name; Predicate = expr }
            }
        | "limit" ->
            result {
                let! name = str path "name" fs
                let! maxRows = intField path "maxRows" fs
                let! maxJoins = optInt path "maxJoins" fs
                let! maxGroupKeys = optInt path "maxGroupKeys" fs
                let! minGroupSize = optInt path "minGroupSize" fs
                let! timeoutSeconds = optInt path "timeoutSeconds" fs

                return
                    NodeKind.Limit
                        { Name = name
                          MaxRows = maxRows
                          MaxJoins = maxJoins
                          MaxGroupKeys = maxGroupKeys
                          MinGroupSize = minGroupSize
                          TimeoutSeconds = timeoutSeconds }
            }
        | other -> Error(WireError.UnknownKind(path + ".$type", other, ModelNode.allKindTags))

    let rec private decodeNodeAt (path: string) (v: JVal) : Result<ModelNode, WireError> =
        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs
            let container = isContainerTag tag

            let allowed =
                [ "$type"; "id" ]
                @ payloadFields tag
                @ (if container then [ "children" ] else [])

            let! kind = kindOf path tag fs
            do! only path allowed fs
            let! id = str path "id" fs

            let! children =
                if container then
                    arr path "children" fs
                    |> Result.bind (traverse (fun i c -> decodeNodeAt (sprintf "%s.children[%d]" path i) c))
                else
                    Ok []

            return
                { Id = id
                  Kind = kind
                  Children = children }
        }

    let private parse (text: string) : Result<JVal, WireError> =
        Json.parse text |> Result.mapError WireError.NotJson

    /// A node (and its subtree) from a `JVal`.
    let decodeNodeJson (v: JVal) : Result<ModelNode, WireError> = decodeNodeAt "$" v

    /// A node from a `JVal` nested at `path`, so a refusal names where in the enclosing text it is.
    let internal decodeNodeAtPath (path: string) (v: JVal) : Result<ModelNode, WireError> = decodeNodeAt path v

    /// A model state from a `JVal`.
    let decodeStateJson (v: JVal) : Result<ModelState, WireError> =
        let path = "$"

        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs

            if tag <> "datasetModel" then
                return! Error(WireError.UnknownKind(path + ".$type", tag, [ "datasetModel" ]))
            else
                do! only path [ "$type"; "version"; "root"; "retired" ] fs
                let! version = intField path "version" fs

                if version <> Version then
                    return! Error(WireError.UnsupportedVersion(version, [ Version ]))
                else
                    let! rootJson = field path "root" fs
                    let! root = decodeNodeAt "$.root" rootJson
                    let! retired = strList path "retired" fs

                    return
                        { Root = root
                          Retired = Set.ofList retired }
        }

    /// An op from a `JVal`.
    let decodeOpJson (v: JVal) : Result<ModelOp, WireError> =
        let path = "$"

        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs

            match tag with
            | "add" ->
                do! only path [ "$type"; "parent"; "node" ] fs
                let! parent = str path "parent" fs
                let! nodeJson = field path "node" fs
                let! node = decodeNodeAt "$.node" nodeJson
                return ModelOp.Add(parent, node)
            | "amend" ->
                do! only path [ "$type"; "id"; "kind" ] fs
                let! id = str path "id" fs
                let! kindJson = field path "kind" fs
                let kp = "$.kind"
                let! kfs = fieldsOf kp kindJson
                let! ktag = str kp "$type" kfs
                let! kind = kindOf kp ktag kfs
                do! only kp ("$type" :: payloadFields ktag) kfs
                return ModelOp.Amend(id, kind)
            | "retire" ->
                do! only path [ "$type"; "id" ] fs
                let! id = str path "id" fs
                return ModelOp.Retire id
            | other -> return! Error(WireError.UnknownKind(path + ".$type", other, [ "add"; "amend"; "retire" ]))
        }

    /// A node from canonical text.
    let decodeNode (text: string) : Result<ModelNode, WireError> =
        parse text |> Result.bind decodeNodeJson

    /// A model state from canonical text.
    let decodeState (text: string) : Result<ModelState, WireError> =
        parse text |> Result.bind decodeStateJson

    /// An op from canonical text.
    let decodeOp (text: string) : Result<ModelOp, WireError> = parse text |> Result.bind decodeOpJson

    /// Core's stream witness over the model: the reducer and this codec. A record's hash folds the
    /// op's canonical text, so a stream verifies on any host that renders the same bytes.
    let streamWitness: StreamWitness<ModelOp, ModelState, ModelRejection> =
        { Apply = ModelOps.apply
          Encode = encodeOp
          Decode = fun text -> decodeOp text |> Result.mapError describe }
