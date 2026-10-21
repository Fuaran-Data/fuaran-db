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

    /// The model marks a classification recordable that it does not declare (Phase 1903).
    [<Literal>]
    let RecordableUndeclared = "DB-RECORDABLE-UNDECLARED"

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

    /// A source's fingerprint names a table or column the model does not hold.
    [<Literal>]
    let FingerprintDangling = "DB-FINGERPRINT-DANGLING"

    /// A source's fingerprint names a table of another source, or a change column of another table.
    [<Literal>]
    let FingerprintForeign = "DB-FINGERPRINT-FOREIGN"

    /// A fingerprint's change column is not a timestamp, date or int column, so its maximum does
    /// not advance with a change.
    [<Literal>]
    let FingerprintType = "DB-FINGERPRINT-TYPE"

    /// A row-count fingerprint leaves a table of its source out, or names one more than once. A
    /// change to a table it leaves out would never move it.
    [<Literal>]
    let FingerprintCoverage = "DB-FINGERPRINT-COVERAGE"

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
          RecordableUndeclared
          LimitNonPositive
          NameDuplicate
          RootNotModel
          FingerprintDangling
          FingerprintForeign
          FingerprintType
          FingerprintCoverage ]

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
        | TimestampType _ -> false

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
            | Some s ->
                let declared = Set.ofList s.Classifications

                s.Recordable
                |> List.distinct
                |> List.filter (fun c -> not (declared.Contains c))
                |> List.map (fun c ->
                    defect
                        RecordableUndeclared
                        (sprintf
                            "the model marks '%s' recordable but declares only: %s"
                            c
                            (String.concat ", " (Set.toList declared)))
                        root.Id
                        []),
                Some declared
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

    /// A change column's maximum advances with a change only on an ordered type that a writer
    /// stamps: a timestamp, a date or an int version.
    let private isChangeType (t: ColumnType) : bool =
        match t with
        | TimestampType _
        | DateType
        | IntType -> true
        | FloatType
        | DecimalType
        | BoolType
        | StringType -> false

    let private isTable (n: ModelNode) : bool =
        match n.Kind with
        | NodeKind.Table _ -> true
        | NodeKind.Model _
        | NodeKind.Source _
        | NodeKind.Column _
        | NodeKind.Join _
        | NodeKind.Measure _
        | NodeKind.RowPolicy _
        | NodeKind.Limit _ -> false

    /// One `(table id, change column id)` pair of a row-count fingerprint, held to the source.
    let private pairDefects
        (root: ModelNode)
        (src: ModelNode)
        (name: string)
        (own: ModelNode list)
        (tid: string, cid: string)
        : Defect<string> list =
        match own |> List.tryFind (fun t -> t.Id = tid) with
        | None ->
            match ModelNode.tryFind tid root with
            | None ->
                [ defect
                      FingerprintDangling
                      (sprintf "source '%s' fingerprints table '%s', which the model does not hold" name tid)
                      src.Id
                      [] ]
            | Some _ ->
                [ defect
                      FingerprintForeign
                      (sprintf "source '%s' fingerprints '%s', which is not one of its tables" name tid)
                      src.Id
                      [ tid ] ]
        | Some t ->
            match Model.columns t |> List.tryFind (fun (c, _) -> c.Id = cid) with
            | Some(_, c) when isChangeType c.Type -> []
            | Some(_, c) ->
                [ defect
                      FingerprintType
                      (sprintf
                          "source '%s' reads change column '%s', a %s column; it needs timestamp, date or int"
                          name
                          c.Name
                          (ColumnType.tag c.Type))
                      src.Id
                      [ cid ] ]
            | None ->
                match ModelNode.tryFind cid root with
                | None ->
                    [ defect
                          FingerprintDangling
                          (sprintf "source '%s' reads change column '%s', which the model does not hold" name cid)
                          src.Id
                          [ tid ] ]
                | Some _ ->
                    [ defect
                          FingerprintForeign
                          (sprintf
                              "source '%s' reads change column '%s', which is not a column of table '%s'"
                              name
                              cid
                              tid)
                          src.Id
                          [ tid; cid ] ]

    let private sourceRules (root: ModelNode) : Defect<string> list =
        ModelNode.preorder root
        |> List.collect (fun src ->
            match src.Kind with
            | NodeKind.Source { Name = name
                                Fingerprint = Some(FingerprintMethod.RowCountMaxChange pairs) } ->
                let own = src.Children |> List.filter isTable
                let counts = pairs |> List.countBy fst |> Map.ofList

                let coverage =
                    own
                    |> List.choose (fun t ->
                        match Map.tryFind t.Id counts with
                        | None ->
                            Some(
                                defect
                                    FingerprintCoverage
                                    (sprintf
                                        "source '%s' fingerprints by row count but not table '%s'; a change to it would never move the fingerprint"
                                        name
                                        (ModelNode.name t))
                                    src.Id
                                    [ t.Id ]
                            )
                        | Some 1 -> None
                        | Some _ ->
                            Some(
                                defect
                                    FingerprintCoverage
                                    (sprintf
                                        "source '%s' fingerprints table '%s' more than once"
                                        name
                                        (ModelNode.name t))
                                    src.Id
                                    [ t.Id ]
                            ))

                (pairs |> List.collect (pairDefects root src name own)) @ coverage
            | NodeKind.Source _
            | NodeKind.Model _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> [])

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
              family "measures" measureRules
              family "sources" sourceRules ] }

    /// Every defect the registry finds in a model.
    let check (root: ModelNode) : Defect<string> list =
        Validator.runAll ModelNode.witness registry root
