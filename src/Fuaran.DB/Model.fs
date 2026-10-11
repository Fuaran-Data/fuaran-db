namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

// ============================================================================
//  The dataset model (Phase 1899) — the domain's artefact: a typed tree of what
//  a source holds and what may be asked of it.
//
//  The tree is HOMOGENEOUS: every node is `{ Id; Kind; Children }`, so it is
//  addressed, walked and rebuilt through Core's generic tree witness. The kind
//  union is CLOSED and every match over it is exhaustive, so a new kind stops
//  the build at each arm that must decide it.
//
//  Identity is the node's `Id` and is permanent: a rename is an `Amend` that
//  keeps it, and a retired id is never reused (`ModelState.Retired`).
//  References between nodes (a join's tables and key columns, a measure's
//  column) are by id, so a rename never breaks one. A row policy's predicate is
//  a `ColExpr` over the table's MODEL column names — the same names a pipeline
//  reads — so the rules check it the way the pipeline check checks a pipeline.
// ============================================================================

/// The model's root: its name and the closed set of classifications a column may carry.
type ModelSpec =
    {
        /// The model's name.
        Name: string
        /// The classifications a column may carry, declared once; a column classified outside
        /// this set is a `DB-CLASSIFICATION-UNDECLARED` defect.
        Classifications: string list
        /// The classifications whose values a connection record may hold (Phase 1903, DECISIONS.md
        /// D9): a parameter value or a result row is recorded only when every classification it
        /// carries is listed here. Empty by default, and empty means nothing is recorded. Each entry
        /// is one of `Classifications`, or the model is a `DB-RECORDABLE-UNDECLARED` defect.
        Recordable: string list
    }

/// How a host can learn that a source moved without reading it (Phase 2008, DECISIONS.md D6). The
/// model declares the METHOD; whether it is exact or heuristic, and what it can miss, follow from the
/// method (`Fingerprint.disclose`) and are never asserted by an author.
[<RequireQualifiedAccess>]
type FingerprintMethod =
    /// The source's own change counter or log position, read through its dialect. Exact.
    | ChangeCounter
    /// Per table, by id, its row count and the maximum of the change column the model names, by
    /// column id. Every table of the source is named exactly once. Heuristic.
    | RowCountMaxChange of tables: (string * string) list
    /// A file's size and modification time, and its content hash when `contentHash` is set. Exact
    /// with the hash, heuristic without it.
    | FileStat of contentHash: bool

/// A named source — one database, or one schema of one — holding tables.
type SourceSpec =
    {
        /// The source's name.
        Name: string
        /// How the source is fingerprinted, or `None` when it declares no fingerprint: a host then
        /// has no cheap way to learn that it moved, and must read it or work to a schedule.
        Fingerprint: FingerprintMethod option
    }

/// A table: the name a pipeline reads it by, and the physical identifier the source knows it by.
type TableSpec =
    {
        /// The model name — what a pipeline's `Ref` names. Unique across the model.
        Name: string
        /// The source's own identifier for the table. Identifier text emitted to a source comes
        /// from here and never from a request.
        Physical: string
    }

/// A column: its model name, its physical identifier, its type and its classification.
type ColumnSpec =
    {
        /// The model name — what a pipeline reads. Unique within its table.
        Name: string
        /// The source's own identifier for the column; what a drift check compares.
        Physical: string
        /// The column's type, in Core's column model.
        Type: ColumnType
        /// One of the model's declared classifications.
        Classification: string
    }

/// A permitted join: two tables, by id, and the key pairs that relate them, by column id.
/// A pipeline may join two tables only through a relation declared here.
type JoinSpec =
    {
        /// The relation's name.
        Name: string
        /// The left table's id.
        Left: string
        /// The right table's id.
        Right: string
        /// `(left column id, right column id)` pairs; at least one.
        On: (string * string) list
    }

/// A measure: a named aggregate over one column, by id.
type MeasureSpec =
    {
        /// The measure's name.
        Name: string
        /// The aggregate, in the transform algebra's own vocabulary.
        Fn: AggFn
        /// The column's id.
        Column: string
    }

/// A row policy: a predicate over the table's model column names that every read of the table
/// is filtered by.
type RowPolicySpec =
    {
        /// The policy's name.
        Name: string
        /// The predicate, in the transform algebra's own expression tree.
        Predicate: ColExpr
    }

/// The cost limits a read is held to: model-wide under the root, or per table under a table. A
/// read is held to the tightest of the limits declared on the model and on every table it reads
/// (DECISIONS.md D5). A cap no limit node declares is not unbounded: what it bounds is refused.
type LimitSpec =
    {
        /// The limit's name.
        Name: string
        /// The most rows a read may return; must be positive. A pipeline ends in a `Limit` within it.
        MaxRows: int
        /// The most joins one pipeline may make; zero or more.
        MaxJoins: int option
        /// The most grouping columns one grouping step may name; zero or more.
        MaxGroupKeys: int option
        /// The fewest contributing rows an aggregate over an aggregate-only column may answer for;
        /// positive. A grouping over such a column is rewritten to drop the smaller groups.
        MinGroupSize: int option
        /// The statement timeout, in seconds; positive. Carried on every statement lowered.
        TimeoutSeconds: int option
    }

/// The rule a limit's numbers are held to, which needs no model to decide.
module LimitSpec =

    /// A limit that declares nothing beyond its row ceiling.
    let rows (name: string) (maxRows: int) : LimitSpec =
        { Name = name
          MaxRows = maxRows
          MaxJoins = None
          MaxGroupKeys = None
          MinGroupSize = None
          TimeoutSeconds = None }

    /// Why the limit's numbers are out of range, or `None`: the row ceiling, the group floor and the
    /// timeout must be positive; the join and grouping caps must not be negative.
    let fault (l: LimitSpec) : string option =
        let below floor what (v: int option) =
            match v with
            | Some n when n < floor ->
                Some(
                    sprintf
                        "limit '%s' declares %s %d; it must be %s"
                        l.Name
                        what
                        n
                        (if floor = 0 then "zero or more" else "positive")
                )
            | Some _
            | None -> None

        [ below 1 "maxRows" (Some l.MaxRows)
          below 0 "maxJoins" l.MaxJoins
          below 0 "maxGroupKeys" l.MaxGroupKeys
          below 1 "minGroupSize" l.MinGroupSize
          below 1 "timeoutSeconds" l.TimeoutSeconds ]
        |> List.tryPick id

/// The model's node kinds — closed.
[<RequireQualifiedAccess>]
type NodeKind =
    | Model of ModelSpec
    | Source of SourceSpec
    | Table of TableSpec
    | Column of ColumnSpec
    | Join of JoinSpec
    | Measure of MeasureSpec
    | RowPolicy of RowPolicySpec
    | Limit of LimitSpec

/// One node of the dataset model. Plain data, so two models compare with `=`.
type ModelNode =
    {
        /// The node's permanent identity.
        Id: string
        /// What the node is.
        Kind: NodeKind
        /// The node's children, in order; empty for a leaf kind.
        Children: ModelNode list
    }

/// The model as an op-stream folds it: the tree, and every id ever retired from it.
type ModelState =
    {
        /// The live model.
        Root: ModelNode
        /// Ids retired from the model. An id here is never minted again, so an id in a later
        /// record always names the one node it named when the record was written.
        Retired: Set<string>
    }

/// The op vocabulary: add, amend, retire. A rename is an `Amend` that keeps identity.
[<RequireQualifiedAccess>]
type ModelOp =
    /// Append `node` (with any children it carries) under `parent`.
    | Add of parent: string * node: ModelNode
    /// Replace a node's payload with one of the SAME kind, keeping its id and its children.
    | Amend of id: string * kind: NodeKind
    /// Remove a node and its subtree from the live model; its ids are never reused.
    | Retire of id: string

/// Why the reducer refused an op — each case names what an author can do instead.
[<RequireQualifiedAccess>]
type ModelRejection =
    /// The op addresses an id the live model does not hold.
    | UnknownNode of id: string * known: string list
    /// An added node carries an id the live model already holds.
    | DuplicateId of id: string
    /// An added node carries an id that was retired; retired ids are never reused.
    | RetiredId of id: string
    /// The parent's kind may not hold the child's kind.
    | IllegalChild of parentKind: string * childKind: string * allowed: string list
    /// An amend would change a node's kind.
    | KindChange of id: string * from: string * into: string
    /// The root cannot be retired.
    | RetireRoot

/// The node-level vocabulary: kind tags, the containment grammar, and the witnesses.
module ModelNode =

    /// The kind's wire tag — stable, and what the witness reports as `KindTag`.
    let kindTag (k: NodeKind) : string =
        match k with
        | NodeKind.Model _ -> "model"
        | NodeKind.Source _ -> "source"
        | NodeKind.Table _ -> "table"
        | NodeKind.Column _ -> "column"
        | NodeKind.Join _ -> "join"
        | NodeKind.Measure _ -> "measure"
        | NodeKind.RowPolicy _ -> "rowPolicy"
        | NodeKind.Limit _ -> "limit"

    /// Every kind tag, in declaration order.
    let allKindTags =
        [ "model"
          "source"
          "table"
          "column"
          "join"
          "measure"
          "rowPolicy"
          "limit" ]

    /// The containment grammar: the kinds each kind may hold. A leaf kind holds none.
    let allowedChildren (parentTag: string) : string list =
        match parentTag with
        | "model" -> [ "source"; "join"; "measure"; "limit" ]
        | "source" -> [ "table" ]
        | "table" -> [ "column"; "rowPolicy"; "limit" ]
        | _ -> []

    /// The node's name, whatever its kind.
    let name (n: ModelNode) : string =
        match n.Kind with
        | NodeKind.Model s -> s.Name
        | NodeKind.Source s -> s.Name
        | NodeKind.Table s -> s.Name
        | NodeKind.Column s -> s.Name
        | NodeKind.Join s -> s.Name
        | NodeKind.Measure s -> s.Name
        | NodeKind.RowPolicy s -> s.Name
        | NodeKind.Limit s -> s.Name

    /// Whether the node's kind may hold children.
    let isContainer (n: ModelNode) : bool =
        not (List.isEmpty (allowedChildren (kindTag n.Kind)))

    /// Core's id witness over the model's string ids.
    let idWitness: IdWitness<string> =
        { ToString = id
          OfString = id
          Equals = (=) }

    /// Core's node witness over the model.
    let witness: NodeWitness<ModelNode, string> =
        { Id = fun n -> n.Id
          KindTag = fun n -> kindTag n.Kind
          Children = fun n -> n.Children
          ReplaceChildren = fun n cs -> { n with Children = cs } }

    /// Every node, in preorder.
    let preorder (root: ModelNode) : ModelNode list = Tree.preorder witness root

    /// The node carrying `id`, if the tree holds one.
    let tryFind (id: string) (root: ModelNode) : ModelNode option =
        preorder root |> List.tryFind (fun n -> n.Id = id)

/// Reading the model as a reader of it would: its tables, their columns, their schemas.
module Model =

    /// A model with no sources, no relations and no measures.
    let empty (id: string) (spec: ModelSpec) : ModelNode =
        { Id = id
          Kind = NodeKind.Model spec
          Children = [] }

    /// The model's root spec. A tree whose root is not a `Model` node answers `None`.
    let spec (root: ModelNode) : ModelSpec option =
        match root.Kind with
        | NodeKind.Model s -> Some s
        | NodeKind.Source _
        | NodeKind.Table _
        | NodeKind.Column _
        | NodeKind.Join _
        | NodeKind.Measure _
        | NodeKind.RowPolicy _
        | NodeKind.Limit _ -> None

    /// Every table node with its spec, in preorder.
    let tables (root: ModelNode) : (ModelNode * TableSpec) list =
        ModelNode.preorder root
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Table t -> Some(n, t)
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

    /// A table node's columns, with their specs, in order.
    let columns (table: ModelNode) : (ModelNode * ColumnSpec) list =
        table.Children
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Column c -> Some(n, c)
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

    /// The table a pipeline names by `name`, if the model holds exactly that name.
    let tryTable (name: string) (root: ModelNode) : (ModelNode * TableSpec) option =
        tables root |> List.tryFind (fun (_, t) -> t.Name = name)

    /// A table's schema in MODEL names — what a pipeline over it reads.
    let schemaOf (table: ModelNode) : Schema =
        columns table |> List.map (fun (_, c) -> c.Name, c.Type)

    /// A table's schema in PHYSICAL names — what its source reports.
    let physicalSchemaOf (table: ModelNode) : Schema =
        columns table |> List.map (fun (_, c) -> c.Physical, c.Type)

    /// Every table's model schema, keyed by its model name — the `sources` a schema walk reads.
    let schemas (root: ModelNode) : Map<string, Schema> =
        tables root |> List.map (fun (n, t) -> t.Name, schemaOf n) |> Map.ofList

    /// The table node holding the column with `columnId`, with the column's spec.
    let tryColumn (columnId: string) (root: ModelNode) : (ModelNode * ColumnSpec) option =
        tables root
        |> List.tryPick (fun (t, _) ->
            columns t
            |> List.tryPick (fun (c, spec) -> if c.Id = columnId then Some(t, spec) else None))

    /// The declared relations, in preorder.
    let joins (root: ModelNode) : (ModelNode * JoinSpec) list =
        ModelNode.preorder root
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Join j -> Some(n, j)
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

/// The reducer: one op over a model state, or a typed refusal. Total.
module ModelOps =

    /// A model state with nothing retired.
    let ofRoot (root: ModelNode) : ModelState = { Root = root; Retired = Set.empty }

    let private known (root: ModelNode) =
        ModelNode.preorder root |> List.map _.Id

    let rec private mapNode (f: ModelNode -> ModelNode) (target: string) (n: ModelNode) : ModelNode =
        if n.Id = target then
            f n
        else
            { n with
                Children = n.Children |> List.map (mapNode f target) }

    let rec private removeNode (target: string) (n: ModelNode) : ModelNode =
        { n with
            Children =
                n.Children
                |> List.filter (fun c -> c.Id <> target)
                |> List.map (removeNode target) }

    /// The first containment breach inside a subtree being added, parent before child.
    let rec private firstIllegal (n: ModelNode) : ModelRejection option =
        let pk = ModelNode.kindTag n.Kind
        let allowed = ModelNode.allowedChildren pk

        n.Children
        |> List.tryPick (fun c ->
            let ck = ModelNode.kindTag c.Kind

            if List.contains ck allowed then
                firstIllegal c
            else
                Some(ModelRejection.IllegalChild(pk, ck, allowed)))

    /// Apply one op. Refuses an unknown address, an id the model holds or once held, a child its
    /// parent may not hold, an amend that changes kind, and retiring the root.
    let apply (op: ModelOp) (state: ModelState) : Result<ModelState, ModelRejection> =
        let root = state.Root

        match op with
        | ModelOp.Add(parent, node) ->
            match ModelNode.tryFind parent root with
            | None -> Error(ModelRejection.UnknownNode(parent, known root))
            | Some p ->
                let pk = ModelNode.kindTag p.Kind
                let ck = ModelNode.kindTag node.Kind
                let allowed = ModelNode.allowedChildren pk
                let incoming = ModelNode.preorder node |> List.map _.Id
                let live = known root |> Set.ofList

                let duplicateInside =
                    incoming
                    |> List.countBy id
                    |> List.tryFind (fun (_, n) -> n > 1)
                    |> Option.map fst

                match
                    incoming |> List.tryFind live.Contains,
                    incoming |> List.tryFind state.Retired.Contains,
                    duplicateInside
                with
                | Some dup, _, _ -> Error(ModelRejection.DuplicateId dup)
                | None, Some retired, _ -> Error(ModelRejection.RetiredId retired)
                | None, None, Some dup -> Error(ModelRejection.DuplicateId dup)
                | None, None, None ->
                    if not (List.contains ck allowed) then
                        Error(ModelRejection.IllegalChild(pk, ck, allowed))
                    else
                        match firstIllegal node with
                        | Some breach -> Error breach
                        | None ->
                            Ok
                                { state with
                                    Root =
                                        mapNode
                                            (fun p ->
                                                { p with
                                                    Children = p.Children @ [ node ] })
                                            parent
                                            root }
        | ModelOp.Amend(target, kind) ->
            match ModelNode.tryFind target root with
            | None -> Error(ModelRejection.UnknownNode(target, known root))
            | Some n ->
                let from = ModelNode.kindTag n.Kind
                let into = ModelNode.kindTag kind

                if from <> into then
                    Error(ModelRejection.KindChange(target, from, into))
                else
                    Ok
                        { state with
                            Root = mapNode (fun n -> { n with Kind = kind }) target root }
        | ModelOp.Retire target ->
            if target = root.Id then
                Error ModelRejection.RetireRoot
            else
                match ModelNode.tryFind target root with
                | None -> Error(ModelRejection.UnknownNode(target, known root))
                | Some n ->
                    let gone = ModelNode.preorder n |> List.map _.Id |> Set.ofList

                    Ok
                        { Root = removeNode target root
                          Retired = Set.union state.Retired gone }

    /// Fold a list of ops, stopping at the first refusal with its index.
    let applyAll (ops: ModelOp list) (state: ModelState) : Result<ModelState, int * ModelRejection> =
        ops
        |> List.indexed
        |> List.fold
            (fun acc (i, op) -> acc |> Result.bind (fun s -> apply op s |> Result.mapError (fun r -> i, r)))
            (Ok state)
