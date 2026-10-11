namespace Fuaran.DB

open Fuaran.Core

/// The AI-tools introspection surface over the dataset model: read tools a model uses to read a
/// dataset model, the op catalogue it emits from, one fast-path pattern, the write policy and the
/// rejection guidance — Core's `AiSurfaceWitness`, filled for this domain.
module AiTools =

    let private outlineOf (n: ModelNode) : JVal =
        let rec go (n: ModelNode) =
            JObj
                [ "id", JStr n.Id
                  "kind", JStr(ModelNode.kindTag n.Kind)
                  "name", JStr(ModelNode.name n)
                  "children", JArr(n.Children |> List.map go) ]

        go n

    let private tablesOf (root: ModelNode) : JVal =
        Model.tables root
        |> List.map (fun (t, spec) ->
            let policies =
                t.Children
                |> List.choose (fun c ->
                    match c.Kind with
                    | NodeKind.RowPolicy p -> Some(JStr p.Name)
                    | NodeKind.Model _
                    | NodeKind.Source _
                    | NodeKind.Table _
                    | NodeKind.Column _
                    | NodeKind.Join _
                    | NodeKind.Measure _
                    | NodeKind.Limit _ -> None)

            JObj
                [ "id", JStr t.Id
                  "name", JStr spec.Name
                  "columns",
                  JArr(
                      Model.columns t
                      |> List.map (fun (c, cs) ->
                          JObj
                              [ "id", JStr c.Id
                                "name", JStr cs.Name
                                "type", JStr(ColumnType.tag cs.Type)
                                "classification", JStr cs.Classification ])
                  )
                  "rowPolicies", JArr policies ])
        |> JArr

    let private relationsOf (root: ModelNode) : JVal =
        let tableName (id: string) =
            Model.tables root
            |> List.tryPick (fun (n, t) -> if n.Id = id then Some t.Name else None)
            |> Option.defaultValue id

        let columnName (id: string) =
            Model.tryColumn id root
            |> Option.map (fun (_, c) -> c.Name)
            |> Option.defaultValue id

        Model.joins root
        |> List.map (fun (n, j) ->
            JObj
                [ "id", JStr n.Id
                  "name", JStr j.Name
                  "left", JStr(tableName j.Left)
                  "right", JStr(tableName j.Right)
                  "on", JArr(j.On |> List.map (fun (l, r) -> JStr(columnName l + " = " + columnName r))) ])
        |> JArr

    let private defectsOf (root: ModelNode) : JVal =
        ModelRules.check root
        |> List.map (fun d ->
            JObj
                [ "code", JStr d.Code
                  "node", (d.Node |> Option.map JStr |> Option.defaultValue (JStr ""))
                  "message", JStr d.Message ])
        |> JArr

    /// The read tools, in catalogue order.
    let readTools: ReadTool<ModelState> list =
        [ { Name = "outline"
            Description = "The model as a tree of ids, kinds and names."
            Run = fun s -> outlineOf s.Root }
          { Name = "tables"
            Description = "Every table with its columns (name, type, classification) and its row policies."
            Run = fun s -> tablesOf s.Root }
          { Name = "relations"
            Description = "The joins the model permits, by table and column name."
            Run = fun s -> relationsOf s.Root }
          { Name = "defects"
            Description = "Every defect the model rules find, with its code and node."
            Run = fun s -> defectsOf s.Root } ]

    let private schemaOf (required: string list) : JVal =
        JObj
            [ "type", JStr "object"
              "required", JArr(required |> List.map JStr)
              "properties", JObj(required |> List.map (fun r -> r, JObj [ "type", JStr "string" ])) ]

    /// The op catalogue.
    let opKinds: OpKindCard list =
        [ { Kind = "add"
            Description = "Append a node (and its children) under a parent; the id must be new and never retired."
            Schema = schemaOf [ "parent"; "node" ] }
          { Kind = "amend"
            Description = "Replace a node's payload with one of the same kind, keeping its id. A rename is an amend."
            Schema = schemaOf [ "id"; "kind" ] }
          { Kind = "retire"
            Description = "Remove a node and its subtree; its ids are never reused."
            Schema = schemaOf [ "id" ] } ]

    /// An op's catalogue kind.
    let kindOfOp (op: ModelOp) : string =
        match op with
        | ModelOp.Add _ -> "add"
        | ModelOp.Amend _ -> "amend"
        | ModelOp.Retire _ -> "retire"

    /// The model rules that need no model to decide: a payload that breaks one is wrong wherever
    /// it lands.
    let private payloadFault (k: NodeKind) : RejectionGuidance option =
        match k with
        | NodeKind.Limit l -> LimitSpec.fault l |> Option.map (fun m -> { Message = m; Alternatives = [] })
        | NodeKind.Join j when List.isEmpty j.On ->
            Some
                { Message = sprintf "join '%s' declares no key pair" j.Name
                  Alternatives = [] }
        | NodeKind.Join _
        | NodeKind.Model _
        | NodeKind.Source _
        | NodeKind.Table _
        | NodeKind.Column _
        | NodeKind.Measure _
        | NodeKind.RowPolicy _ -> None

    /// The write policy. An op whose payload breaks a model rule that needs no model to decide is
    /// denied, with the rule as its guidance. A retirement needs approval, because what a retired
    /// node governed stops existing for every pipeline checked against the model. Everything else
    /// is allowed; the reducer and the rules decide the rest.
    let decide (_actor: string) (op: ModelOp) : PolicyDecision =
        let fault =
            match op with
            | ModelOp.Add(_, node) -> ModelNode.preorder node |> List.tryPick (fun n -> payloadFault n.Kind)
            | ModelOp.Amend(_, kind) -> payloadFault kind
            | ModelOp.Retire _ -> None

        match fault, op with
        | Some g, _ -> PolicyDecision.Deny g
        | None, ModelOp.Retire _ -> PolicyDecision.NeedsApproval
        | None, ModelOp.Add _
        | None, ModelOp.Amend _ -> PolicyDecision.Allow

    /// A reducer rejection as guidance that names the alternatives.
    let explain (r: ModelRejection) : RejectionGuidance =
        match r with
        | ModelRejection.UnknownNode(id, known) ->
            { Message = sprintf "the model holds no node '%s'" id
              Alternatives = known }
        | ModelRejection.DuplicateId id ->
            { Message = sprintf "the model already holds a node '%s'; choose a new id" id
              Alternatives = [] }
        | ModelRejection.RetiredId id ->
            { Message = sprintf "'%s' was retired and is never reused; choose a new id" id
              Alternatives = [] }
        | ModelRejection.IllegalChild(p, c, allowed) ->
            { Message = sprintf "a %s may not hold a %s" p c
              Alternatives = allowed }
        | ModelRejection.KindChange(id, from, into) ->
            { Message =
                sprintf "'%s' is a %s and an amend cannot make it a %s; retire it and add a new node" id from into
              Alternatives = [ from ] }
        | ModelRejection.RetireRoot ->
            { Message = "the model's root cannot be retired"
              Alternatives = [] }

    /// The fast-path pattern bank.
    let patterns: PatternCard<ModelOp> list =
        [ { Name = "retire"
            Title = "Retire a node by id"
            PromptAnchors = [ "retire {id}" ]
            Emit =
              fun args ->
                  match args |> List.tryFind (fun (k, _) -> k = "id") with
                  | Some(_, id) when id <> "" -> Ok [ ModelOp.Retire id ]
                  | Some _
                  | None -> Error "retire needs an 'id' argument naming the node" } ]

    /// The surface.
    let surface: AiSurfaceWitness<ModelState, ModelOp, ModelRejection> =
        { ReadTools = readTools
          OpKinds = opKinds
          KindOfOp = kindOfOp
          Patterns = patterns
          Decide = decide
          Apply = ModelOps.apply
          Explain = explain }
