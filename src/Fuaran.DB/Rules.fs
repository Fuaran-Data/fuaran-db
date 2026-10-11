namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

/// The model's validators (`validator:Fuaran.DB.ModelRules`) — rule families over Core's validator
/// registry, each defect carrying a named code. Every family is total over any tree, including one
/// that arrived decoded or merged rather than built through the reducer.
module ModelRules =

    /// A join declaration names a table or key column the model does not hold.
    [<Literal>]
    let JoinDangling = "DB-JOIN-DANGLING"

    /// A join's key column does not belong to the side's table.
    [<Literal>]
    let JoinKeyForeign = "DB-JOIN-KEY-FOREIGN"

    /// A join's key pair relates columns of different types.
    [<Literal>]
    let JoinKeyType = "DB-JOIN-KEY-TYPE"

    /// A join declares no key pair.
    [<Literal>]
    let JoinNoKeys = "DB-JOIN-NO-KEYS"

    /// A measure names a column the model does not hold.
    [<Literal>]
    let MeasureDangling = "DB-MEASURE-DANGLING"

    /// A numeric aggregate over a non-numeric column.
    [<Literal>]
    let MeasureType = "DB-MEASURE-TYPE"

    /// A row policy's predicate reads a column its table lacks.
    [<Literal>]
    let PolicyColumn = "DB-POLICY-COLUMN"

    /// A column classified outside the model's declared set.
    [<Literal>]
    let ClassificationUndeclared = "DB-CLASSIFICATION-UNDECLARED"

    /// A limit whose numbers are out of range: a row ceiling, group floor or timeout that is not
    /// positive, or a join or grouping cap below zero (`LimitSpec.fault`).
    [<Literal>]
    let LimitNonPositive = "DB-LIMIT-NONPOSITIVE"

    /// Two tables share a model name, or two columns of one table do.
    [<Literal>]
    let NameDuplicate = "DB-NAME-DUPLICATE"

    /// The tree's root is not a model node.
    [<Literal>]
    let RootNotModel = "DB-ROOT-NOT-MODEL"

    /// Every code a model rule can report.
    let codes =
        [ JoinDangling
          JoinKeyForeign
          JoinKeyType
          JoinNoKeys
          MeasureDangling
          MeasureType
          PolicyColumn
          ClassificationUndeclared
          LimitNonPositive
          NameDuplicate
          RootNotModel ]

    let private defect code (message: string) (node: string) related : Defect<string> =
        { Defect.create code Severity.Error message (Some node) with
            Family = "fuaran-db"
            Related = related }

    /// The aggregates the evaluator refuses over a non-numeric column — the same four, so a measure
    /// the model admits is one the evaluator will compute.
    let numericOnly (fn: AggFn) : bool =
        match fn with
        | Sum
        | Mean
        | Median
        | StdDev -> true
        | Min
        | Max
        | Count
        | First
        | Last
        | CountDistinct -> false

    let private isNumeric (t: ColumnType) : bool =
        match t with
        | IntType
        | FloatType
        | DecimalType -> true
        | BoolType
        | StringType
        | DateType
        | TimestampType -> false

    let private joinRules (root: ModelNode) : Defect<string> list =
        let tableIds = Model.tables root |> List.map (fun (n, _) -> n.Id) |> Set.ofList

        Model.joins root
        |> List.collect (fun (n, j) ->
            let tablesExist =
                [ j.Left; j.Right ]
                |> List.filter (tableIds.Contains >> not)
                |> List.map (fun t ->
                    defect
                        JoinDangling
                        (sprintf "join '%s' names table '%s', which the model does not hold" j.Name t)
                        n.Id
                        [])

            let noKeys =
                if List.isEmpty j.On then
                    [ defect JoinNoKeys (sprintf "join '%s' declares no key pair" j.Name) n.Id [] ]
                else
                    []

            let keyDefects =
                j.On
                |> List.collect (fun (l, r) ->
                    let side (colId: string) (tableId: string) =
                        match Model.tryColumn colId root with
                        | None ->
                            Error(
                                defect
                                    JoinDangling
                                    (sprintf "join '%s' names column '%s', which the model does not hold" j.Name colId)
                                    n.Id
                                    []
                            )
                        | Some(t, c) when t.Id <> tableId && tableIds.Contains tableId ->
                            Error(
                                defect
                                    JoinKeyForeign
                                    (sprintf
                                        "join '%s' keys on column '%s', which belongs to table '%s', not '%s'"
                                        j.Name
                                        colId
                                        t.Id
                                        tableId)
                                    n.Id
                                    [ colId ]
                            )
                        | Some(_, c) -> Ok c

                    match side l j.Left, side r j.Right with
                    | Ok lc, Ok rc when lc.Type <> rc.Type ->
                        [ defect
                              JoinKeyType
                              (sprintf
                                  "join '%s' relates '%s' (%s) to '%s' (%s)"
                                  j.Name
                                  lc.Name
                                  (ColumnType.tag lc.Type)
                                  rc.Name
                                  (ColumnType.tag rc.Type))
                              n.Id
                              [ l; r ] ]
                    | Ok _, Ok _ -> []
                    | a, b ->
                        [ a; b ]
                        |> List.choose (fun x ->
                            match x with
                            | Error d -> Some d
                            | Ok _ -> None))

            tablesExist @ noKeys @ keyDefects)

    let private measureRules (root: ModelNode) : Defect<string> list =
        ModelNode.preorder root
        |> List.collect (fun n ->
            match n.Kind with
            | NodeKind.Measure m ->
                match Model.tryColumn m.Column root with
                | None ->
                    [ defect
                          MeasureDangling
                          (sprintf "measure '%s' is over column '%s', which the model does not hold" m.Name m.Column)
                          n.Id
                          [] ]
                | Some(_, c) when numericOnly m.Fn && not (isNumeric c.Type) ->
                    [ defect
                          MeasureType
                          (sprintf
                              "measure '%s' is a numeric aggregate over '%s', a %s column; it needs int, float or decimal"
                              m.Name
                              c.Name
                              (ColumnType.tag c.Type))
                          n.Id
                          [ m.Column ] ]
                | Some _ -> []
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> [])

    let private tableRules (root: ModelNode) : Defect<string> list =
        Model.tables root
        |> List.collect (fun (t, spec) ->
            let names = Model.columns t |> List.map (fun (_, c) -> c.Name)

            let policies =
                t.Children
                |> List.collect (fun p ->
                    match p.Kind with
                    | NodeKind.RowPolicy rp ->
                        Reads.ofExpr rp.Predicate
                        |> List.distinct
                        |> List.filter (fun c -> not (List.contains c names))
                        |> List.map (fun c ->
                            defect
                                PolicyColumn
                                (sprintf
                                    "row policy '%s' reads column '%s', which table '%s' lacks; its columns are: %s"
                                    rp.Name
                                    c
                                    spec.Name
                                    (String.concat ", " names))
                                p.Id
                                [ t.Id ])
                    | NodeKind.Model _
                    | NodeKind.Source _
                    | NodeKind.Table _
                    | NodeKind.Column _
                    | NodeKind.Join _
                    | NodeKind.Measure _
                    | NodeKind.Limit _ -> [])

            let duplicates =
                names
                |> List.countBy id
                |> List.filter (fun (_, k) -> k > 1)
                |> List.map (fun (c, _) ->
                    defect NameDuplicate (sprintf "table '%s' names column '%s' more than once" spec.Name c) t.Id [])

            policies @ duplicates)

    let private modelRules (root: ModelNode) : Defect<string> list =
        let rootDefects, declared =
            match Model.spec root with
            | Some s -> [], Some(Set.ofList s.Classifications)
            | None ->
                [ defect
                      RootNotModel
                      (sprintf "the root is a '%s', not a model" (ModelNode.kindTag root.Kind))
                      root.Id
                      [] ],
                None

        let perNode =
            ModelNode.preorder root
            |> List.collect (fun n ->
                match n.Kind with
                | NodeKind.Column c ->
                    match declared with
                    | Some set when not (set.Contains c.Classification) ->
                        [ defect
                              ClassificationUndeclared
                              (sprintf
                                  "column '%s' is classified '%s'; the model declares: %s"
                                  c.Name
                                  c.Classification
                                  (String.concat ", " (Set.toList set)))
                              n.Id
                              [] ]
                    | Some _
                    | None -> []
                | NodeKind.Limit l ->
                    LimitSpec.fault l
                    |> Option.map (fun m -> defect LimitNonPositive m n.Id [])
                    |> Option.toList
                | NodeKind.Model _
                | NodeKind.Source _
                | NodeKind.Table _
                | NodeKind.Join _
                | NodeKind.Measure _
                | NodeKind.RowPolicy _ -> [])

        let tableNames =
            Model.tables root
            |> List.countBy (fun (_, t) -> t.Name)
            |> List.filter (fun (_, k) -> k > 1)
            |> List.map (fun (t, _) ->
                defect NameDuplicate (sprintf "the model names table '%s' more than once" t) root.Id [])

        rootDefects @ perNode @ tableNames

    let private family (id: string) (run: ModelNode -> Defect<string> list) : RuleFamily<ModelNode, string> =
        { Id = id
          Run = fun _ root -> run root }

    /// The registry: containment, then the domain's own families, in that order.
    let registry: Validator.RuleRegistry<ModelNode, string> =
        { Families =
            [ Validator.containment (fun tag -> Some(ModelNode.allowedChildren tag))
              family "model" modelRules
              family "tables" tableRules
              family "joins" joinRules
              family "measures" measureRules ] }

    /// Every defect the registry finds in a model.
    let check (root: ModelNode) : Defect<string> list =
        Validator.runAll ModelNode.witness registry root
