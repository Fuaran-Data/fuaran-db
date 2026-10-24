namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

// ============================================================================
//  The model projected per peer (Phase 1907). In the custodian topology the
//  provider, the model and the policy run on the far side of a peer call, and
//  the requester holds only the ability to send a pipeline. It builds that
//  pipeline against a PROJECTION: the part of the custodian's model one
//  subject's grants may read, and nothing else. What exists is itself
//  information, so the projection is a function of the readable part alone:
//  two models that differ only in what the subject cannot read project to the
//  same bytes.
//
//  The requester pins the projection and checks a fresh one against the pin
//  (a data snapshot with drift conformance), and checks a pipeline against it
//  before sending, so most refusals happen at the requester and the
//  custodian's refusal is the backstop, not the interface.
// ============================================================================

/// What one subject may know of a custodian's dataset model: the readable sub-model, and how far
/// the subject may read each classification it holds. A projection carries no physical identifier,
/// row policy, limit, fingerprint or recording rule, and no classification the subject is not
/// granted; those are the custodian's business.
type ProjectedModel =
    {
        /// The readable sub-model: a well-formed model whose physical identifiers are its model names.
        Model: ModelNode
        /// One grant per classification the projected model declares, at the subject's level, in the
        /// model's order.
        Grants: Grant list
    }

/// Why a pipeline is refused against a projection, at the requester, before it is sent.
[<RequireQualifiedAccess>]
type ProjectionRefusal =
    /// The rule the custodian's column authorisation applies, decided against the projection.
    | Policy of PolicyRefusal
    /// A use that may read a column the projection withholds from `table`: every column of its rows
    /// (a result, a `Distinct`, a set operation), or a name the withheld columns could also produce
    /// after a join. Projecting the table to named columns first answers it.
    | Withheld of site: PolicySite * table: string

/// One difference between a pinned projection and a fresh one, in the names a pipeline reads.
[<RequireQualifiedAccess>]
type ProjectionChange =
    /// The fresh projection holds a column the pin does not.
    | ColumnAdded of table: string * column: string * ty: ColumnType
    /// The pin holds a column the fresh projection does not.
    | ColumnRemoved of table: string * column: string * ty: ColumnType
    /// A column both hold changed type; safe when `ColumnType.widens from into`.
    | ColumnRetyped of table: string * column: string * from: ColumnType * into: ColumnType
    /// A column both hold changed a metadata member — its unit, label, description or an extension
    /// member — as Core's `SchemaDelta.Amended` reports it (Phase 2). A unit change is breaking: a
    /// pipeline the pin admitted can be refused by the evaluator's unit check under the new unit.
    | ColumnAmended of table: string * column: string * change: FieldChange
    /// A relation, by name, ends and key names, that only the fresh projection declares.
    | RelationAdded of name: string
    /// A relation, by name, ends and key names, that only the pin declares.
    | RelationRemoved of name: string
    /// A measure, by name, aggregate and column, that only the fresh projection declares.
    | MeasureAdded of name: string
    /// A measure, by name, aggregate and column, that only the pin declares.
    | MeasureRemoved of name: string
    /// How far the subject may read a classification changed; `None` is not at all.
    | GrantChanged of classification: string * from: GrantLevel option * into: GrantLevel option

/// The drift verdict between a pinned projection and a fresh one.
type ProjectionDrift =
    {
        /// The pinned projection's hash.
        Pinned: string
        /// The fresh projection's hash.
        Fresh: string
        /// Every change, columns first (by table, in the pin's table order and then the fresh
        /// one's), then relations, measures and grants.
        Changes: ProjectionChange list
        /// The changes that can refuse a pipeline the pin admitted: a removal, a retype that is not a
        /// widening, a lowered grant.
        Breaking: ProjectionChange list
    }

/// The projection of a dataset model per subject (`domain:Fuaran.DB.ModelProjection`): the function,
/// its hash, its drift verdict, the requester-side check, and its canonical wire. Pure.
module ModelProjection =

    let private rank (l: GrantLevel option) =
        match l with
        | None -> 0
        | Some GrantLevel.Aggregate -> 1
        | Some GrantLevel.RowLevel -> 2

    let private column (readable: Set<string>) (n: ModelNode) : ModelNode option =
        match n.Kind with
        | NodeKind.Column c when readable.Contains c.Classification ->
            Some
                { n with
                    Kind = NodeKind.Column { c with Physical = c.Name }
                    Children = [] }
        | NodeKind.Column _
        | NodeKind.Model _
        | NodeKind.Source _
        | NodeKind.Table _
        | NodeKind.Join _
        | NodeKind.Measure _
        | NodeKind.RowPolicy _
        | NodeKind.Limit _ -> None

    let private table (readable: Set<string>) (n: ModelNode) : ModelNode option =
        match n.Kind with
        | NodeKind.Table t ->
            match n.Children |> List.choose (column readable) with
            | [] -> None
            | columns ->
                Some
                    { n with
                        Kind = NodeKind.Table { t with Physical = t.Name }
                        Children = columns }
        | NodeKind.Model _
        | NodeKind.Source _
        | NodeKind.Column _
        | NodeKind.Join _
        | NodeKind.Measure _
        | NodeKind.RowPolicy _
        | NodeKind.Limit _ -> None

    /// The projection of `model` for a subject holding `grants`: the classifications the model declares
    /// and the grants reach; the tables with a column of one of them, holding just those columns; the
    /// sources holding such a table; the relations whose tables and key columns all survive; the
    /// measures whose column survives. Physical identifiers become model names; row policies, limits,
    /// fingerprints and the recording rule are dropped. Grants over nothing the model declares carry
    /// nothing. A subject with no grants projects to the empty model.
    let project (model: ModelNode) (grants: Grant list) : ProjectedModel =
        let spec =
            Model.spec model
            |> Option.defaultValue
                { Name = ""
                  Classifications = []
                  Recordable = [] }

        let held =
            spec.Classifications
            |> List.distinct
            |> List.choose (fun c -> Policy.level grants c |> Option.map (fun l -> { Classification = c; Level = l }))

        let readable = held |> List.map _.Classification |> Set.ofList

        let source (n: ModelNode) =
            match n.Kind with
            | NodeKind.Source s ->
                match n.Children |> List.choose (table readable) with
                | [] -> None
                | tables ->
                    Some
                        { n with
                            Kind = NodeKind.Source { s with Fingerprint = None }
                            Children = tables }
            | NodeKind.Model _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None

        let isRoot = Option.isSome (Model.spec model)
        let sources = if isRoot then model.Children |> List.choose source else []

        let kept =
            sources
            |> List.collect (fun s -> s :: s.Children @ (s.Children |> List.collect _.Children))
            |> List.map _.Id
            |> Set.ofList

        let children =
            if not isRoot then
                []
            else
                model.Children
                |> List.choose (fun n ->
                    match n.Kind with
                    | NodeKind.Source _ -> sources |> List.tryFind (fun s -> s.Id = n.Id)
                    | NodeKind.Join j when
                        kept.Contains j.Left
                        && kept.Contains j.Right
                        && j.On |> List.forall (fun (l, r) -> kept.Contains l && kept.Contains r)
                        ->
                        Some n
                    | NodeKind.Measure m when kept.Contains m.Column -> Some n
                    | NodeKind.Join _
                    | NodeKind.Measure _
                    | NodeKind.Model _
                    | NodeKind.Table _
                    | NodeKind.Column _
                    | NodeKind.RowPolicy _
                    | NodeKind.Limit _ -> None)

        { Model =
            { Id = model.Id
              Kind =
                NodeKind.Model
                    { Name = spec.Name
                      Classifications = held |> List.map _.Classification
                      Recordable = [] }
              Children = children }
          Grants = held }

    /// Why `p` is not a projection, or `None`: a projection is the projection of its own model under
    /// its own grants, and a well-formed model.
    let fault (p: ProjectedModel) : string option =
        if project p.Model p.Grants <> p then
            Some
                "it is not its own projection: it carries a physical identifier, a row policy, a limit, a fingerprint, a recording rule, a part no grant reads, or grants not one per declared classification in order"
        else
            match ModelRules.check p.Model with
            | [] -> None
            | d :: _ -> Some(sprintf "its model is not well formed: %s: %s" d.Code d.Message)

    // ---- the requester-side check ----

    /// A classification no grant of `p` names, for the withheld columns' lineage.
    let private markFor (p: ProjectedModel) : string =
        let taken = p.Grants |> List.map _.Classification |> Set.ofList

        let rec fresh (stem: string) =
            if taken.Contains stem then fresh (stem + "'") else stem

        fresh "withheld"

    /// Check `pipeline` over `source` against the projection, as the custodian's column authorisation
    /// would for the subject the projection is of, with every table taken to hold columns the
    /// projection withholds. `Ok` carries every column admitted with its level. A pipeline admitted
    /// here is one the custodian's column authorisation admits, with the same columns at the same
    /// levels (`Laws`); the custodian may still refuse it on cost, group size or row policy.
    let admits
        (p: ProjectedModel)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<(ColumnRef * GrantLevel) list, ProjectionRefusal> =
        let mark = markFor p

        let subject =
            { Principal = "the projection's subject"
              Attributes = Map.empty
              Grants = p.Grants }

        Policy.columnsWithheld mark p.Model subject source pipeline
        |> Result.mapError (fun r ->
            match r with
            | PolicyRefusal.ColumnNotGranted(site, c, _) when c.Classification = mark ->
                ProjectionRefusal.Withheld(site, c.Table)
            | other -> ProjectionRefusal.Policy other)

    /// The stable code of a projection refusal: the policy's own, or `DB-PROJECTION-WITHHELD`.
    let code (r: ProjectionRefusal) : string =
        match r with
        | ProjectionRefusal.Policy p -> Policy.code p
        | ProjectionRefusal.Withheld _ -> "DB-PROJECTION-WITHHELD"

    /// A projection refusal as one sentence.
    let describe (r: ProjectionRefusal) : string =
        match r with
        | ProjectionRefusal.Policy p -> Policy.describe p
        | ProjectionRefusal.Withheld(site, t) ->
            sprintf
                "DB-PROJECTION-WITHHELD: %s may read a column the projection withholds from table '%s'; satisfied by projecting '%s' to named columns before using every column of its rows, and by reading no name a join could give a withheld column"
                (match site with
                 | PolicySite.Step n -> sprintf "step %d" n
                 | PolicySite.Result -> "the result")
                t
                t

    // ---- the wire ----

    /// The wire version this codec writes and the only one it reads.
    [<Literal>]
    let Version = 1

    let private levelTag (l: GrantLevel) =
        match l with
        | GrantLevel.Aggregate -> "aggregate"
        | GrantLevel.RowLevel -> "row"

    /// A projection as a `JVal`: `{"$type":"modelProjection","version":1,"model","grants"}`.
    let encodeJson (p: ProjectedModel) : JVal =
        Canonical.typed
            "modelProjection"
            [ "version", JInt Version
              "model", ModelWire.encodeNodeJson p.Model
              "grants",
              JArr(
                  p.Grants
                  |> List.map (fun g ->
                      JObj [ "classification", JStr g.Classification; "level", JStr(levelTag g.Level) ])
              ) ]

    /// A projection's canonical text: keys sorted, one spelling per value.
    let encode (p: ProjectedModel) : string =
        Canonical.write Rendering.CrossHost (encodeJson p)

    /// The projection's hash: `sha256:` over its canonical text. Two subjects whose grants read the
    /// same part of one model, or of two models that differ only outside it, share it.
    let hash (p: ProjectedModel) : string = "sha256:" + Hash.sha256Hex (encode p)

    open WireDecode

    let private grantOf (path: string) (v: JVal) : Result<Grant, WireError> =
        result {
            let! fs = fieldsOf path v
            do! only path [ "classification"; "level" ] fs
            let! c = str path "classification" fs
            let! tag = str path "level" fs

            match tag with
            | "aggregate" ->
                return
                    { Classification = c
                      Level = GrantLevel.Aggregate }
            | "row" ->
                return
                    { Classification = c
                      Level = GrantLevel.RowLevel }
            | other -> return! Error(WireError.UnknownKind(path + ".level", other, [ "aggregate"; "row" ]))
        }

    /// A projection from a `JVal`. Strict as the model's codec, and more: a text that decodes but is
    /// not a projection (`fault`) is refused as `WrongShape` at `$`.
    let decodeJson (v: JVal) : Result<ProjectedModel, WireError> =
        let path = "$"

        result {
            let! fs = fieldsOf path v
            let! tag = str path "$type" fs

            if tag <> "modelProjection" then
                return! Error(WireError.UnknownKind(path + ".$type", tag, [ "modelProjection" ]))
            else
                do! only path [ "$type"; "version"; "model"; "grants" ] fs
                let! version = intField path "version" fs

                if version <> Version then
                    return! Error(WireError.UnsupportedVersion(version, [ Version ]))
                else
                    let! modelJson = field path "model" fs
                    let! model = ModelWire.decodeNodeAtPath "$.model" modelJson
                    let! grantsJson = arr path "grants" fs
                    let! grants = traverse (fun i g -> grantOf (sprintf "$.grants[%d]" i) g) grantsJson
                    let p = { Model = model; Grants = grants }

                    match fault p with
                    | Some why -> return! Error(WireError.WrongShape(path, "a model projection; " + why))
                    | None -> return p
        }

    /// A projection from its text.
    let decode (text: string) : Result<ProjectedModel, WireError> =
        Json.parse text |> Result.mapError WireError.NotJson |> Result.bind decodeJson

    // ---- drift ----

    let private tablesOf (p: ProjectedModel) : (string * Schema) list =
        Model.tables p.Model |> List.map (fun (n, t) -> t.Name, Model.schemaOf n)

    let private columnName (p: ProjectedModel) (id: string) =
        match Model.tryColumn id p.Model with
        | Some(t, c) -> ModelNode.name t + "." + c.Name
        | None -> id

    let private tableName (p: ProjectedModel) (id: string) =
        ModelNode.tryFind id p.Model
        |> Option.map ModelNode.name
        |> Option.defaultValue id

    /// A relation as a pipeline meets it: its name, its tables and its key columns, by name.
    let private relations (p: ProjectedModel) : (string * string) list =
        Model.joins p.Model
        |> List.map (fun (_, j) ->
            j.Name,
            sprintf
                "%s|%s|%s|%s"
                j.Name
                (tableName p j.Left)
                (tableName p j.Right)
                (j.On
                 |> List.map (fun (l, r) -> columnName p l + "=" + columnName p r)
                 |> String.concat ","))

    /// A measure as a pipeline meets it: its name, its aggregate and its column, by name.
    let private measures (p: ProjectedModel) : (string * string) list =
        ModelNode.preorder p.Model
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Measure m ->
                Some(m.Name, sprintf "%s|%s|%s" m.Name (ModelWire.aggTag m.Fn) (columnName p m.Column))
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> None)

    let private keyed
        (added: string -> ProjectionChange)
        (removed: string -> ProjectionChange)
        (pinned: (string * string) list)
        (fresh: (string * string) list)
        =
        let p = pinned |> List.map snd |> Set.ofList
        let f = fresh |> List.map snd |> Set.ofList

        (pinned
         |> List.filter (fun (_, k) -> not (f.Contains k))
         |> List.map (fst >> removed))
        @ (fresh
           |> List.filter (fun (_, k) -> not (p.Contains k))
           |> List.map (fst >> added))

    /// Whether a change can refuse a pipeline the pin admitted.
    let isBreaking (c: ProjectionChange) : bool =
        match c with
        | ProjectionChange.ColumnAdded _
        | ProjectionChange.RelationAdded _
        | ProjectionChange.MeasureAdded _ -> false
        | ProjectionChange.ColumnRemoved _
        | ProjectionChange.RelationRemoved _
        | ProjectionChange.MeasureRemoved _ -> true
        | ProjectionChange.ColumnRetyped(_, _, from, into) -> not (ColumnType.widens from into)
        | ProjectionChange.ColumnAmended(_, _, change) ->
            match change with
            | FieldChange.Unit _ -> true
            | FieldChange.Label _
            | FieldChange.Description _
            | FieldChange.Ext _ -> false
        | ProjectionChange.GrantChanged(_, from, into) -> rank into < rank from

    /// The drift verdict of a fresh projection against a pinned one, in the names a pipeline reads:
    /// columns added, removed, retyped and amended per table (`Schema.diff`; a rename is a removal and an
    /// addition, and an order change is none), relations and measures added and removed, and grant
    /// levels changed. `Breaking` is the changes `isBreaking` holds.
    let drift (pinned: ProjectedModel) (fresh: ProjectedModel) : ProjectionDrift =
        let before = tablesOf pinned
        let after = tablesOf fresh

        let names =
            (before |> List.map fst)
            @ (after
               |> List.map fst
               |> List.filter (fun t -> not (List.exists (fun (b, _) -> b = t) before)))

        let schemaIn (ts: (string * Schema) list) (t: string) =
            ts
            |> List.tryFind (fun (n, _) -> n = t)
            |> Option.map snd
            |> Option.defaultValue []

        let columns =
            names
            |> List.collect (fun t ->
                let d = Schema.diff (schemaIn before t) (schemaIn after t)

                (d.Removed
                 |> List.map (fun f -> ProjectionChange.ColumnRemoved(t, f.Name, f.Type)))
                @ (d.Retyped
                   |> List.map (fun (c, from, into) -> ProjectionChange.ColumnRetyped(t, c, from, into)))
                @ (d.Amended
                   |> List.map (fun (c, change) -> ProjectionChange.ColumnAmended(t, c, change)))
                @ (d.Added |> List.map (fun f -> ProjectionChange.ColumnAdded(t, f.Name, f.Type))))

        let grants =
            (pinned.Grants @ fresh.Grants)
            |> List.map _.Classification
            |> List.distinct
            |> List.choose (fun c ->
                let from = Policy.level pinned.Grants c
                let into = Policy.level fresh.Grants c

                if from = into then
                    None
                else
                    Some(ProjectionChange.GrantChanged(c, from, into)))

        let changes =
            columns
            @ keyed ProjectionChange.RelationAdded ProjectionChange.RelationRemoved (relations pinned) (relations fresh)
            @ keyed ProjectionChange.MeasureAdded ProjectionChange.MeasureRemoved (measures pinned) (measures fresh)
            @ grants

        { Pinned = hash pinned
          Fresh = hash fresh
          Changes = changes
          Breaking = changes |> List.filter isBreaking }

    /// The projection's laws (`validator:Fuaran.DB.ModelProjection.Laws`), over generated models,
    /// grants and pipelines: the projection is total, pure and idempotent, monotone in the grants,
    /// and a well-formed model; it never carries what it withholds, and two models that differ only
    /// there project to the same bytes; its wire round-trips; more grants are never a breaking drift;
    /// and a pipeline the projection admits is one the custodian's column authorisation admits, with
    /// the same columns at the same levels. Each verdict the laws read carries Core's sample-adequacy
    /// guard, so a generator that never reached it is red.
    module Laws =

        /// One generated case: the custodian's model; a twin that differs from it only in what the
        /// grants cannot read; the subject's grants and a wider set; a pipeline over one source.
        type Case =
            { Model: ModelNode
              Twin: ModelNode
              Grants: Grant list
              Wider: Grant list
              Source: DataSource
              Pipeline: Transform list }

        let private family = "ModelProjectionLaws"

        /// The classifications every generated model declares; no one is a substring of another, so a
        /// withheld one is found in a projection's text only if it is there.
        let classifications = [ "cls-open"; "cls-staff"; "cls-secret" ]

        let private tableNames = [ "tbl_x"; "tbl_y"; "tbl_z" ]

        // Names the evaluator's join suffix can collide with are drawn on purpose.
        let private columnNames = [ "a"; "b"; "v"; "w"; "a_right"; "k_right" ]

        let private types = [ IntType; FloatType; StringType; DecimalType ]

        let private leaf (id: string) (kind: NodeKind) : ModelNode = { Id = id; Kind = kind; Children = [] }

        let private column (table: string) (i: int) (name: string) (ty: ColumnType) (cls: string) : ModelNode =
            leaf
                ("c:" + table + "." + name)
                (NodeKind.Column(ColumnSpec.create name (sprintf "PHYS_%s_%d" (name.ToUpperInvariant()) i) ty cls))

        let private draw (r0: ConfRng.T) : Case * ConfRng.T =
            let r = ref r0

            let below (n: int) =
                let v, r' = ConfRng.intBelow n r.Value
                r.Value <- r'
                v

            let chance (k: int) = below k = 0
            let pick (xs: 'a list) = List.item (below (List.length xs)) xs

            let shuffled (xs: 'a list) =
                let s, r' = ConfRng.shuffle xs r.Value
                r.Value <- r'
                s

            let tables =
                tableNames
                |> List.take (2 + below 2)
                |> List.mapi (fun i t ->
                    // a third of the tables hold a column named as the join would suffix another of
                    // theirs, ahead of it and classified apart from it, so a withheld one can take a
                    // readable one's joined name
                    let extra =
                        if chance 3 then
                            let apart = shuffled classifications
                            [ "a_right", pick types, List.item 0 apart; "a", pick types, List.item 1 apart ]
                        else
                            shuffled columnNames
                            |> List.take (1 + below 4)
                            |> List.map (fun n -> n, pick types, pick classifications)

                    let columns =
                        column t i "k" IntType (pick classifications)
                        :: (extra |> List.map (fun (n, ty, cls) -> column t i n ty cls))

                    let names = "k" :: (extra |> List.map (fun (n, _, _) -> n))

                    let policy =
                        if chance 3 then
                            [ leaf
                                  ("p:" + t)
                                  (NodeKind.RowPolicy
                                      { Name = "only-" + t
                                        Predicate = Binary(Eq, Col(pick names), Lit(Int 1)) }) ]
                        else
                            []

                    let limit =
                        if chance 4 then
                            [ leaf ("l:" + t) (NodeKind.Limit(LimitSpec.rows ("rows-" + t) (10 + below 90))) ]
                        else
                            []

                    { Id = "t:" + t
                      Kind =
                        NodeKind.Table
                            { Name = t
                              Physical = sprintf "PHYS_T%d" i }
                      Children = columns @ policy @ limit })

            let main, aux =
                if List.length tables = 3 && chance 3 then
                    List.take 2 tables, [ List.last tables ]
                else
                    tables, []

            let source (id: string) (name: string) (ts: ModelNode list) =
                { Id = id
                  Kind =
                    NodeKind.Source
                        { Name = name
                          Fingerprint =
                            if chance 3 then
                                Some FingerprintMethod.ChangeCounter
                            else
                                None }
                  Children = ts }

            let relations =
                [ for i, a in List.indexed tables do
                      for j, b in List.indexed tables do
                          if i < j && not (chance 3) then
                              let second =
                                  match
                                      Model.columns a |> List.tryFind (fun (_, c) -> c.Name = "a"),
                                      Model.columns b |> List.tryFind (fun (_, c) -> c.Name = "a")
                                  with
                                  | Some(ca, sa), Some(cb, sb) when sa.Type = sb.Type && chance 3 -> [ ca.Id, cb.Id ]
                                  | _ -> []

                              yield
                                  leaf
                                      (sprintf "j:%d-%d" i j)
                                      (NodeKind.Join
                                          { Name = sprintf "rel_%d_%d" i j
                                            Left = a.Id
                                            Right = b.Id
                                            On =
                                              ("c:" + ModelNode.name a + ".k", "c:" + ModelNode.name b + ".k") :: second }) ]

            let measures =
                tables
                |> List.indexed
                |> List.filter (fun _ -> chance 2)
                |> List.map (fun (i, t) ->
                    let numeric =
                        Model.columns t
                        |> List.filter (fun (_, c) ->
                            match c.Type with
                            | IntType
                            | FloatType
                            | DecimalType -> true
                            | _ -> false)

                    let c, fn =
                        match numeric with
                        | [] -> fst (List.head (Model.columns t)), Count
                        | xs -> fst (pick xs), Sum

                    leaf
                        (sprintf "x:%d" i)
                        (NodeKind.Measure
                            { Name = sprintf "msr_%d" i
                              Fn = fn
                              Column = c.Id }))

            let model =
                { Id = "m:custodian"
                  Kind =
                    NodeKind.Model
                        { Name = "custodian"
                          Classifications = classifications
                          Recordable = classifications |> List.filter (fun _ -> chance 2) }
                  Children =
                    [ source "s:main" "main" main ]
                    @ (if List.isEmpty aux then
                           []
                       else
                           [ source "s:aux" "aux" aux ])
                    @ relations
                    @ measures
                    @ (if chance 2 then
                           [ leaf "l:model" (NodeKind.Limit(LimitSpec.rows "rows" 1000)) ]
                       else
                           []) }

            let grants =
                [ for c in classifications do
                      match below 3 with
                      | 0 -> ()
                      | 1 ->
                          yield
                              { Classification = c
                                Level = GrantLevel.Aggregate }
                      | _ ->
                          yield
                              { Classification = c
                                Level = GrantLevel.RowLevel }

                          if chance 4 then
                              yield
                                  { Classification = c
                                    Level = GrantLevel.Aggregate } ]
                @ (if chance 6 then
                       [ { Classification = "cls-elsewhere"
                           Level = GrantLevel.RowLevel } ]
                   else
                       [])

            let wider =
                grants
                @ [ { Classification = pick classifications
                      Level = pick [ GrantLevel.Aggregate; GrantLevel.RowLevel ] } ]

            // The twin: every physical identifier, policy, limit, fingerprint and recording rule moved,
            // and, where a classification is unreadable, a column of it in every table and a table of
            // it related to the first.
            let unreadable =
                classifications |> List.filter (fun c -> Policy.level grants c |> Option.isNone)

            let rec twin (n: ModelNode) : ModelNode =
                let kind =
                    match n.Kind with
                    | NodeKind.Model s ->
                        NodeKind.Model
                            { s with
                                Recordable = classifications |> List.except s.Recordable }
                    | NodeKind.Source s ->
                        NodeKind.Source
                            { s with
                                Fingerprint =
                                    match s.Fingerprint with
                                    | Some _ -> None
                                    | None -> Some(FingerprintMethod.FileStat true) }
                    | NodeKind.Table t ->
                        NodeKind.Table
                            { t with
                                Physical = t.Physical + "_TWIN" }
                    | NodeKind.Column c ->
                        NodeKind.Column
                            { c with
                                Physical = c.Physical + "_TWIN" }
                    | NodeKind.RowPolicy p ->
                        NodeKind.RowPolicy
                            { p with
                                Predicate = Binary(Ne, Col "k", Lit(Int 2)) }
                    | NodeKind.Limit l -> NodeKind.Limit { l with MaxRows = l.MaxRows + 1 }
                    | NodeKind.Join _
                    | NodeKind.Measure _ -> n.Kind

                let hidden =
                    match n.Kind, unreadable with
                    | NodeKind.Table t, cls :: _ -> [ column t.Name 9 "zz_hidden" StringType cls ]
                    | _ -> []

                { n with
                    Kind = kind
                    Children = (n.Children |> List.map twin) @ hidden }

            let twinModel =
                match unreadable with
                | [] -> twin model
                | cls :: _ ->
                    let t = twin model

                    let hiddenTable =
                        { Id = "t:tbl_hidden"
                          Kind =
                            NodeKind.Table
                                { Name = "tbl_hidden"
                                  Physical = "PHYS_HIDDEN" }
                          Children = [ column "tbl_hidden" 8 "k" IntType cls ] }

                    let first = List.head tables

                    let related =
                        leaf
                            "j:hidden"
                            (NodeKind.Join
                                { Name = "rel_hidden"
                                  Left = first.Id
                                  Right = hiddenTable.Id
                                  On = [ "c:" + ModelNode.name first + ".k", "c:tbl_hidden.k" ] })

                    { t with
                        Children =
                            (t.Children
                             |> List.map (fun s ->
                                 if s.Id = "s:main" then
                                     { s with
                                         Children = s.Children @ [ hiddenTable ] }
                                 else
                                     s))
                            @ [ related ] }

            // The pipeline: over one table, its steps reading names from anywhere in the model, often a
            // first projection to readable columns, and joins along declared relations.
            let start = pick tables

            let readableOf (t: ModelNode) =
                Model.columns t
                |> List.filter (fun (_, c) -> Policy.level grants c.Classification |> Option.isSome)
                |> List.map (fun (_, c) -> c.Name)

            let allNames =
                (tables |> List.collect (Model.columns >> List.map (fun (_, c) -> c.Name)))
                @ [ "k"; "nope" ]
                |> List.distinct

            let nameOf (id: string) =
                ModelNode.tryFind id model
                |> Option.map ModelNode.name
                |> Option.defaultValue id

            let relatedTo =
                Model.joins model
                |> List.choose (fun (_, j) ->
                    if j.Left = start.Id then
                        Some(j.Right, j.On |> List.map (fun (l, r) -> nameOf l, nameOf r))
                    elif j.Right = start.Id then
                        Some(j.Left, j.On |> List.map (fun (l, r) -> nameOf r, nameOf l))
                    else
                        None)

            let joinStep () =
                match relatedTo with
                | [] -> Distinct
                | xs ->
                    let other, on = pick xs
                    Join(Ref(nameOf other), on, pick [ Inner; Inner; Left; Semi; Anti ])

            // A join the requester can have admitted: the left rows named first, keys included, and
            // the joined rows named after it. Half the time the left rows are not named first, which
            // the projection must refuse whenever the right table names a column the left may hide.
            let namedJoin () =
                match relatedTo with
                | [] -> None
                | xs ->
                    let other, on = pick xs
                    let readable = readableOf start
                    let keys = on |> List.map fst

                    if keys |> List.forall (fun k -> List.contains k readable) then
                        let left =
                            if chance 2 then
                                keys @ readable
                            else
                                keys @ (shuffled readable |> List.take (below (min 2 (List.length readable))))
                            |> List.distinct

                        let right =
                            tables
                            |> List.tryFind (fun t -> t.Id = other)
                            |> Option.map readableOf
                            |> Option.defaultValue []

                        // the right names as the join gives them, suffixed where the left rows hold one
                        let renamed =
                            right |> List.map (fun n -> if List.contains n left then n + "_right" else n)

                        let suffixed = renamed |> List.filter (fun n -> n.EndsWith "_right")

                        let after =
                            if not (List.isEmpty suffixed) && chance 2 then
                                [ pick suffixed ]
                            else
                                shuffled (List.distinct (left @ renamed)) |> List.take 1

                        let named =
                            if chance 2 then
                                [ Project(left |> List.map (fun n -> n, n)) ]
                            else
                                []

                        Some(
                            named
                            @ [ Join(Ref(nameOf other), on, pick [ Inner; Left ])
                                Project(after |> List.map (fun n -> n, n)) ]
                        )
                    else
                        None

            let step () =
                match below 11 with
                | 0 ->
                    let ns = shuffled allNames |> List.take (1 + below 2)
                    Project(ns |> List.map (fun n -> n, n))
                | 1 -> Filter(Binary(Gt, Col(pick allNames), Lit(Int 0)))
                | 2
                | 3 -> joinStep ()
                | 4 ->
                    GroupBy(
                        [ pick allNames ],
                        [ { Name = "n"
                            Fn = Count
                            Of = pick allNames }
                          { Name = "s"
                            Fn = Sum
                            Of = pick allNames } ]
                    )
                | 5 -> Distinct
                | 6 -> Sort [ Slot.Lit(pick allNames), Asc ]
                | 7 -> Limit(Slot.Lit 10, Slot.Lit 0)
                | 8 -> Derive("d", Col(pick allNames))
                | 9 -> Union(Ref(ModelNode.name (pick tables)))
                | _ ->
                    GroupBy(
                        [],
                        [ { Name = "n"
                            Fn = Count
                            Of = pick allNames } ]
                    )

            let projected =
                match readableOf start with
                | [] -> []
                | rs when chance 2 ->
                    let keep = shuffled rs |> List.take (1 + below (min 2 (List.length rs)))

                    let keep =
                        if List.contains "k" rs && chance 2 then
                            "k" :: keep |> List.distinct
                        else
                            keep

                    [ Project(keep |> List.map (fun n -> n, n)) ]
                | _ -> []

            // An aggregate the requester can have admitted: a count of a readable column, grouped by
            // at most one column the grants reach row by row.
            let aggregated () =
                match readableOf start with
                | [] -> None
                | readable ->
                    let rowLevel =
                        Model.columns start
                        |> List.filter (fun (_, c) -> Policy.level grants c.Classification = Some GrantLevel.RowLevel)
                        |> List.map (fun (_, c) -> c.Name)

                    let keys =
                        match rowLevel with
                        | [] -> []
                        | xs -> if chance 2 then [ pick xs ] else []

                    Some
                        [ GroupBy(
                              keys,
                              [ { Name = "n"
                                  Fn = Count
                                  Of = pick readable } ]
                          ) ]

            let pipeline =
                match below 6 with
                | 0
                | 1 -> namedJoin ()
                | 2 -> aggregated ()
                | _ -> None
                |> Option.map (fun steps -> steps @ List.init (below 2) (fun _ -> step ()))
                |> Option.defaultWith (fun () -> projected @ List.init (below 4) (fun _ -> step ()))

            { Model = model
              Twin = twinModel
              Grants = grants
              Wider = wider
              Source = Ref(ModelNode.name start)
              Pipeline = pipeline },
            r.Value

        /// `count` cases from `seed`, deterministically.
        let sample (seed: int) (count: int) : Case list =
            let rec go (n: int) (rng: ConfRng.T) (acc: Case list) =
                if n = 0 then
                    List.rev acc
                else
                    let c, rng' = draw rng
                    go (n - 1) rng' (c :: acc)

            go count (ConfRng.ofSeed seed) []

        let private ids (p: ProjectedModel) =
            ModelNode.preorder p.Model |> List.map _.Id |> Set.ofList

        /// What the projection of `c` must not carry, as text that must not appear in its encoding:
        /// every physical identifier, row policy, limit, fingerprint and recording rule, every
        /// classification the grants do not reach, and every table none of whose columns they do.
        let private forbidden (c: Case) : string list =
            let unread =
                classifications
                |> List.filter (fun cls -> Policy.level c.Grants cls |> Option.isNone)

            let unreadTables =
                Model.tables c.Model
                |> List.filter (fun (t, _) ->
                    Model.columns t
                    |> List.forall (fun (_, col) -> List.contains col.Classification unread))
                |> List.collect (fun (t, spec) -> [ "\"" + t.Id + "\""; "\"" + spec.Name + "\"" ])

            [ "PHYS_"; "\"rowPolicy\""; "\"limit\""; "\"fingerprint\""; "\"recordable\"" ]
            @ (unread |> List.map (fun cls -> "\"" + cls + "\""))
            @ unreadTables

        let private widens (pipeline: Transform list) =
            pipeline
            |> List.exists (fun s ->
                match s with
                | Join(_, _, (Inner | Left | Right | Outer)) -> true
                | _ -> false)

        let private bump (d: System.Collections.Generic.Dictionary<string, int>) k =
            d[k] <-
                (match d.TryGetValue k with
                 | true, v -> v + 1
                 | _ -> 1)

        let private counts (d: System.Collections.Generic.Dictionary<string, int>) keys =
            keys
            |> List.map (fun k ->
                k,
                (match d.TryGetValue k with
                 | true, v -> v
                 | _ -> 0))

        /// The agreement law's verdicts, each of which the sample must reach.
        let verdictTags =
            [ "both admit"
              "both admit a join"
              "both admit an aggregate-only column"
              "withheld, the custodian admits"
              "withheld, the custodian refuses"
              "refused, the custodian refuses" ]

        /// The case shapes the sample must reach.
        let shapeTags =
            [ "wider grants read more"
              "no readable classification"
              "an aggregate-only grant"
              "a table withheld whole"
              "a relation withheld" ]

        /// Run the laws over `iterations` cases from `seed`.
        let laws (seed: int) (iterations: int) : LawResult list =
            let slot () = ref (None: string option)

            let note (s: string option ref) (text: string) =
                if s.Value.IsNone then
                    s.Value <- Some text

            let total = slot ()
            let pure' = slot ()
            let idempotent = slot ()
            let wellFormed = slot ()
            let monotone = slot ()
            let withholds = slot ()
            let invariant = slot ()
            let wire = slot ()
            let drifts = slot ()
            let agrees = slot ()

            let verdicts = System.Collections.Generic.Dictionary<string, int>()
            let shapes = System.Collections.Generic.Dictionary<string, int>()

            sample seed iterations
            |> List.iteri (fun i c ->
                let at = sprintf "seed=%d iter=%d" seed i

                match
                    (try
                        let p = project c.Model c.Grants
                        Ok(p, project c.Model c.Grants, project c.Model c.Wider, admits p c.Source c.Pipeline)
                     with ex ->
                         Error ex.Message)
                with
                | Error m -> note total (sprintf "%s: threw %s" at m)
                | Ok(p, again, wide, verdict) ->
                    if p <> again then
                        note pure' (sprintf "%s: two projections differ" at)

                    if project p.Model p.Grants <> p || project p.Model c.Grants <> p then
                        note idempotent (sprintf "%s: projecting the projection moved it: %s" at (encode p))

                    match ModelRules.check c.Model with
                    | d :: _ -> note wellFormed (sprintf "%s: the generator drew an ill-formed model: %s" at d.Message)
                    | [] ->
                        match fault p with
                        | Some why -> note wellFormed (sprintf "%s: %s: %s" at why (encode p))
                        | None -> ()

                    let lowered =
                        classifications
                        |> List.exists (fun cls ->
                            rank (Policy.level wide.Grants cls) < rank (Policy.level p.Grants cls))

                    if not (Set.isSubset (ids p) (ids wide)) || lowered then
                        note monotone (sprintf "%s: wider grants lost something: %s vs %s" at (encode p) (encode wide))

                    let text = encode p

                    match forbidden c |> List.filter (fun f -> text.Contains f) with
                    | [] -> ()
                    | f :: _ -> note withholds (sprintf "%s: the projection carries %s: %s" at f text)

                    if List.isEmpty p.Grants && not (List.isEmpty p.Model.Children) then
                        note withholds (sprintf "%s: no readable classification, yet not the empty model: %s" at text)

                    let twinText = encode (project c.Twin c.Grants)

                    if twinText <> text then
                        note invariant (sprintf "%s: the twin projects differently: %s vs %s" at text twinText)

                    match decode text with
                    | Ok back when back = p && encode back = text -> ()
                    | Ok _ -> note wire (sprintf "%s: the round trip moved: %s" at text)
                    | Error e -> note wire (sprintf "%s: refused its own text: %s: %s" at (ModelWire.describe e) text)

                    let self = drift p p
                    let up = drift p wide
                    let down = drift wide p

                    if not (List.isEmpty self.Changes) then
                        note drifts (sprintf "%s: a projection drifts from itself: %A" at self.Changes)
                    elif not (List.isEmpty up.Breaking) then
                        note drifts (sprintf "%s: more grants drifted breaking: %A" at up.Breaking)
                    elif List.isEmpty down.Breaking <> (p = wide) then
                        note
                            drifts
                            (sprintf "%s: fewer grants drift breaking iff the projections differ: %A" at down.Changes)

                    if p <> wide then
                        bump shapes "wider grants read more"

                    if List.isEmpty p.Grants then
                        bump shapes "no readable classification"

                    if p.Grants |> List.exists (fun g -> g.Level = GrantLevel.Aggregate) then
                        bump shapes "an aggregate-only grant"

                    if List.length (Model.tables p.Model) < List.length (Model.tables c.Model) then
                        bump shapes "a table withheld whole"

                    if List.length (Model.joins p.Model) < List.length (Model.joins c.Model) then
                        bump shapes "a relation withheld"

                    let subject =
                        { Principal = "requester"
                          Attributes = Map.empty
                          Grants = c.Grants }

                    match verdict, Policy.columns c.Model subject c.Source c.Pipeline with
                    | Ok mine, Ok theirs when Set.ofList mine = Set.ofList theirs ->
                        bump verdicts "both admit"

                        if widens c.Pipeline then
                            bump verdicts "both admit a join"

                        if mine |> List.exists (fun (_, l) -> l = GrantLevel.Aggregate) then
                            bump verdicts "both admit an aggregate-only column"
                    | Ok mine, Ok theirs ->
                        note agrees (sprintf "%s: the columns differ: %A vs %A over %A" at mine theirs c.Pipeline)
                    | Ok _, Error r ->
                        note
                            agrees
                            (sprintf
                                "%s: the projection admits what the custodian refuses: %s over %A in %s"
                                at
                                (Policy.describe r)
                                c.Pipeline
                                (ModelWire.encodeNode c.Model))
                    | Error(ProjectionRefusal.Withheld _), Ok _ -> bump verdicts "withheld, the custodian admits"
                    | Error(ProjectionRefusal.Withheld _), Error _ -> bump verdicts "withheld, the custodian refuses"
                    // The converse of agreement is not claimed, so a policy refusal the custodian
                    // would not make is no failure; the sample reaches none, and none is demanded.
                    | Error(ProjectionRefusal.Policy _), Ok _ -> ()
                    | Error(ProjectionRefusal.Policy _), Error _ -> bump verdicts "refused, the custodian refuses")

            let law name (s: string option ref) =
                LawResult.subject family name iterations s.Value

            [ law "the projection is total: it never throws" total
              law "the projection is pure: projecting twice answers the same projection" pure'
              law "the projection is idempotent: a projection is its own projection" idempotent
              law "a well-formed model projects to a well-formed model that is a projection" wellFormed
              law "the projection is monotone in the grants: more grants never remove a part or lower a level" monotone
              law
                  "the projection never carries a physical identifier, row policy, limit, fingerprint, recording rule, unreadable classification or unreadable table"
                  withholds
              law "two models that differ only in what the grants cannot read project to the same bytes" invariant
              law "the projection's wire decodes to the projection and re-encodes to its own bytes" wire
              law
                  "a projection does not drift from itself, more grants never drift breaking, and fewer drift breaking iff they read less"
                  drifts
              law
                  "a pipeline the projection admits is one the custodian's column authorisation admits, with the same columns at the same levels"
                  agrees
              SampleAdequacy.reached family "verdict" seed (counts verdicts verdictTags)
              SampleAdequacy.reached family "shape" seed (counts shapes shapeTags) ]
