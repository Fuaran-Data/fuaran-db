/// The model projected per peer (Phase 1907): the worked retail model projected for a requester, its
/// wire, its drift verdict, the requester-side check, and the laws over generated cases.
module Fuaran.DB.Tests.ProjectionTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

let private grant (cls: string) (level: GrantLevel) : Grant = { Classification = cls; Level = level }

let private publicRows = [ grant "public" GrantLevel.RowLevel ]

let private personalAggregate =
    [ grant "public" GrantLevel.RowLevel; grant "personal" GrantLevel.Aggregate ]

let private project (grants: Grant list) =
    ModelProjection.project Sample.model grants

let private columnsOf (p: ProjectedModel) (table: string) =
    match Model.tryTable table p.Model with
    | Some(t, _) -> Model.columns t |> List.map (fun (_, c) -> c.Name)
    | None -> []

let private customerCol (name: string) (cls: string) : ColumnRef =
    { Table = "customers"
      Column = name
      Classification = cls }

/// The worked model with everything the public grant cannot read moved: a new personal column, a
/// personal table related to orders, other physical identifiers, row policy, limit and recording rule.
let private twin: ModelNode =
    let moved =
        Sample.model
        |> Sample.replace "c:customers.email" (fun n ->
            match n.Kind with
            | NodeKind.Column c ->
                { n with
                    Kind = NodeKind.Column(c |> ColumnSpec.rename "contact" |> ColumnSpec.retype IntType) }
            | _ -> n)
        |> Sample.replace "p:orders.web" (fun n ->
            { n with
                Kind =
                    NodeKind.RowPolicy
                        { Name = "store-only"
                          Predicate = Binary(Eq, Col "channel", Lit(Str "store")) } })
        |> Sample.replace "l:rows" (fun n ->
            { n with
                Kind = NodeKind.Limit(LimitSpec.rows "rows" 50) })
        |> Sample.replace "t:orders" (fun n ->
            match n.Kind with
            | NodeKind.Table t ->
                { n with
                    Kind = NodeKind.Table { t with Physical = "sales.orders_v2" } }
            | _ -> n)
        |> Sample.replace "m:retail" (fun n ->
            match n.Kind with
            | NodeKind.Model s ->
                { n with
                    Kind = NodeKind.Model { s with Recordable = [ "personal" ] } }
            | _ -> n)

    let hidden =
        Sample.table "t:cards" "cards" [ Sample.col "c:cards.customer_id" "customer_id" IntType "personal" ]

    { moved with
        Children =
            (moved.Children
             |> List.map (fun s ->
                 if s.Id = "s:sales" then
                     { s with
                         Children = s.Children @ [ hidden ] }
                 else
                     s))
            @ [ Sample.leaf
                    "j:customers-cards"
                    (NodeKind.Join
                        { Name = "customer cards"
                          Left = "t:customers"
                          Right = "t:cards"
                          On = [ "c:customers.customer_id", "c:cards.customer_id" ] }) ] }

/// The corpus's accept vectors: the worked model projected for three subjects.
let corpusProjections () : (string * string) list =
    [ "retail-public", ModelProjection.encode (project publicRows)
      "retail-personal-aggregate", ModelProjection.encode (project personalAggregate)
      "retail-nothing", ModelProjection.encode (project []) ]

/// The corpus's reject vectors: texts that decode as a model but are not a projection, and texts the
/// codec refuses outright.
let corpusRejects () : (string * string * string) list =
    let text = ModelProjection.encode (project publicRows)

    let withModel (m: ModelNode) (grants: Grant list) =
        Canon.render (
            Canon.typed
                "modelProjection"
                [ "version", JInt 1
                  "model", ModelWire.encodeNodeJson m
                  "grants",
                  JArr(
                      grants
                      |> List.map (fun g ->
                          JObj
                              [ "classification", JStr g.Classification
                                "level",
                                JStr(
                                    match g.Level with
                                    | GrantLevel.Aggregate -> "aggregate"
                                    | GrantLevel.RowLevel -> "row"
                                ) ])
                  ) ]
        )

    let p = project publicRows

    [ "carries-a-physical-identifier",
      withModel
          (Sample.replace
              "c:orders.revenue"
              (fun n ->
                  match n.Kind with
                  | NodeKind.Column c ->
                      { n with
                          Kind = NodeKind.Column { c with Physical = "REVENUE" } }
                  | _ -> n)
              p.Model)
          p.Grants,
      "DB-WIRE-WRONG-SHAPE"
      "carries-a-row-policy",
      withModel
          (Sample.replace
              "t:orders"
              (fun n ->
                  { n with
                      Children =
                          n.Children
                          @ [ Sample.leaf
                                  "p:orders.web"
                                  (NodeKind.RowPolicy
                                      { Name = "web-only"
                                        Predicate = Binary(Eq, Col "channel", Lit(Str "web")) }) ] })
              p.Model)
          p.Grants,
      "DB-WIRE-WRONG-SHAPE"
      "carries-the-whole-model", withModel Sample.model publicRows, "DB-WIRE-WRONG-SHAPE"
      "grant-without-classification-declared",
      withModel p.Model (p.Grants @ [ grant "personal" GrantLevel.RowLevel ]),
      "DB-WIRE-WRONG-SHAPE"
      "unknown-grant-level", text.Replace("\"level\":\"row\"", "\"level\":\"columns\""), "DB-WIRE-UNKNOWN-KIND"
      "unsupported-version", text.Replace("\"version\":1", "\"version\":2"), "DB-WIRE-UNSUPPORTED-VERSION"
      "missing-grants",
      (match Json.parse text with
       | Ok(JObj fs) -> Canon.render (JObj(fs |> List.filter (fun (k, _) -> k <> "grants")))
       | _ -> failwith "the projection's text is an object"),
      "DB-WIRE-MISSING-FIELD"
      "a-dataset-model-state",
      "{\"$type\":\"datasetModel\",\"retired\":[],\"root\":{\"$type\":\"model\",\"children\":[],\"classifications\":[],\"id\":\"m:empty\",\"name\":\"empty\"},\"version\":1}",
      "DB-WIRE-UNKNOWN-KIND" ]

let private failed (results: LawResult list) =
    results |> List.filter (fun r -> not r.Passed)

let private census (title: string) (results: LawResult list) =
    printfn "%s: %d law(s), %d held" title (List.length results) (results |> List.filter _.Passed |> List.length)

    for r in results do
        printfn
            "  [%s] %s%s"
            (if r.Passed then "held" else "FAILED")
            r.Law
            (r.Counterexample |> Option.map (fun c -> " -- " + c) |> Option.defaultValue "")

[<Tests>]
let tests =
    testList
        "model projection"
        [ testCase "a subject with no grants projects to the empty model, and says nothing about what the model holds"
          <| fun _ ->
              let none = project []

              Expect.equal
                  none.Model
                  (Model.empty
                      "m:retail"
                      { Name = "retail"
                        Classifications = []
                        Recordable = [] })
                  "the empty model"

              Expect.isEmpty none.Grants "no grants"

              Expect.equal
                  (ModelProjection.encode (ModelProjection.project twin []))
                  (ModelProjection.encode none)
                  "the twin's too"

              Expect.equal
                  (project [ grant "secret" GrantLevel.RowLevel ])
                  none
                  "a grant over nothing the model declares reads nothing"

          testCase "a public grant reads the public columns, the relations and measures over them, and nothing else"
          <| fun _ ->
              let p = project publicRows

              Expect.equal (columnsOf p "customers") [ "customer_id"; "segment" ] "email is withheld"

              Expect.equal
                  (columnsOf p "orders")
                  [ "order_id"; "customer_id"; "order_date"; "revenue"; "channel" ]
                  "every orders column"

              Expect.equal
                  (Model.joins p.Model |> List.map (fun (n, _) -> n.Id))
                  [ "j:orders-lines"; "j:orders-customers" ]
                  "both relations"

              Expect.equal (Model.spec p.Model |> Option.map _.Classifications) (Some [ "public" ]) "one classification"
              Expect.equal p.Grants publicRows "one grant"
              Expect.isEmpty (ModelRules.check p.Model) "a well-formed model"

              for t, spec in Model.tables p.Model do
                  Expect.equal spec.Physical spec.Name "a table's physical identifier is its model name"

                  for _, c in Model.columns t do
                      Expect.equal c.Physical c.Name "a column's physical identifier is its model name"

          testCase "a personal-only grant reads the email column alone, and no relation or measure"
          <| fun _ ->
              let p = project [ grant "personal" GrantLevel.RowLevel ]
              Expect.equal (Model.tables p.Model |> List.map (fun (_, t) -> t.Name)) [ "customers" ] "one table"
              Expect.equal (columnsOf p "customers") [ "email" ] "one column"
              Expect.isEmpty (Model.joins p.Model) "no relation survives"
              Expect.isEmpty (ModelRules.check p.Model) "a well-formed model"

          testCase "over the wire: no physical identifier, row policy, limit, recording rule or withheld classification"
          <| fun _ ->
              let text = ModelProjection.encode (project publicRows)

              for absent in
                  [ "dbo."
                    "ORDER_ID"
                    "web-only"
                    "rowPolicy"
                    "\"limit\""
                    "\"rows\""
                    "personal"
                    "email"
                    "recordable" ] do
                  Expect.isFalse (text.Contains absent) (sprintf "the text carries no '%s'" absent)

          testCase "two custodians' models that differ only in what the subject cannot read project to the same bytes"
          <| fun _ ->
              Expect.notEqual twin Sample.model "the models differ"

              Expect.equal
                  (ModelProjection.encode (ModelProjection.project twin publicRows))
                  (ModelProjection.encode (project publicRows))
                  "the same bytes"

              Expect.equal
                  (ModelProjection.hash (ModelProjection.project twin publicRows))
                  (ModelProjection.hash (project publicRows))
                  "the same hash"

              Expect.notEqual
                  (ModelProjection.encode (ModelProjection.project twin personalAggregate))
                  (ModelProjection.encode (project personalAggregate))
                  "a subject who reads the difference sees it"

          testCase "the hash is sha256 over the canonical text, and moves with what the subject reads"
          <| fun _ ->
              let h = ModelProjection.hash (project publicRows)
              Expect.isTrue (h.StartsWith "sha256:" && h.Length = 71) "sha256: and 64 hex digits"
              Expect.equal h ("sha256:" + Hash.sha256Hex (ModelProjection.encode (project publicRows))) "over the text"
              Expect.notEqual h (ModelProjection.hash (project personalAggregate)) "a wider read, another hash"

          testCase
              "the requester-side check refuses a whole-table read the projection cannot vouch for, and admits it projected"
          <| fun _ ->
              let p = project publicRows

              Expect.equal
                  (ModelProjection.admits p (Ref "customers") [])
                  (Error(ProjectionRefusal.Withheld(PolicySite.Result, "customers")))
                  "the result may carry a withheld column"

              let projected = [ Project [ "customer_id", "customer_id"; "segment", "segment" ] ]

              match ModelProjection.admits p (Ref "customers") projected with
              | Ok cols ->
                  Expect.equal
                      (Set.ofList cols)
                      (Set.ofList
                          [ customerCol "customer_id" "public", GrantLevel.RowLevel
                            customerCol "segment" "public", GrantLevel.RowLevel ])
                      "the named columns"

                  let subject =
                      { Principal = "peer"
                        Attributes = Map.empty
                        Grants = publicRows }

                  Expect.equal
                      (Policy.columns Sample.model subject (Ref "customers") projected
                       |> Result.map Set.ofList)
                      (Ok(Set.ofList cols))
                      "the custodian agrees"
              | Error r -> failtestf "refused: %s" (ModelProjection.describe r)

              Expect.equal
                  (ModelProjection.admits p (Ref "customers") [ Project [ "email", "email" ] ]
                   |> Result.mapError ModelProjection.code)
                  (Error "DB-PIPELINE")
                  "a withheld column does not exist for the requester"

          testCase "an aggregate-only grant is carried, so the requester refuses a row-level read before sending it"
          <| fun _ ->
              let p = project personalAggregate

              Expect.equal
                  (ModelProjection.admits p (Ref "customers") [ Project [ "email", "email" ] ]
                   |> Result.mapError ModelProjection.code)
                  (Error "DB-GRANT-AGGREGATE-ONLY")
                  "the custodian's own rule, at the requester"

          testCase "a join brings in the right table's withheld columns, and a name they could take is refused"
          <| fun _ ->
              let p = project publicRows

              let join =
                  [ Project [ "order_id", "order_id"; "customer_id", "customer_id" ]
                    Join(Ref "customers", [ "customer_id", "customer_id" ], Inner) ]

              Expect.equal
                  (ModelProjection.admits p (Ref "orders") join
                   |> Result.mapError ModelProjection.code)
                  (Error "DB-PROJECTION-WITHHELD")
                  "the joined rows carry customers' withheld columns"

              Expect.isOk
                  (ModelProjection.admits
                      p
                      (Ref "orders")
                      (join @ [ Project [ "order_id", "order_id"; "segment", "segment" ] ]))
                  "named after the join, admitted"

              let unprojected =
                  [ Join(Ref "customers", [ "customer_id", "customer_id" ], Inner)
                    Project [ "segment", "segment" ] ]

              // The projection cannot say that orders hides nothing, since saying so would tell the
              // requester something about what it holds; so the left rows are named first.
              Expect.equal
                  (ModelProjection.admits p (Ref "orders") unprojected)
                  (Error(ProjectionRefusal.Withheld(PolicySite.Step 2, "orders")))
                  "unnamed left rows may hide a column that takes a right column's name"

          testCase "a right column's joined name a withheld right column also takes is refused, as the custodian would"
          <| fun _ ->
              // Over the full table the withheld 'a_right' precedes 'a', whose joined name is also
              // 'a_right'; the custodian reads the withheld one, so the requester may not read the name.
              let col t n ty cls =
                  Sample.col ("c:" + t + "." + n) n ty cls

              let model =
                  { Id = "m:collide"
                    Kind =
                      NodeKind.Model
                          { Name = "collide"
                            Classifications = [ "open"; "secret" ]
                            Recordable = [] }
                    Children =
                      [ { Id = "s:s"
                          Kind = NodeKind.Source { Name = "s"; Fingerprint = None }
                          Children =
                            [ Sample.table "t:l" "l" [ col "l" "k" IntType "open"; col "l" "a" IntType "open" ]
                              Sample.table
                                  "t:r"
                                  "r"
                                  [ col "r" "k" IntType "open"
                                    col "r" "a_right" StringType "secret"
                                    col "r" "a" IntType "open" ] ] }
                        Sample.leaf
                            "j:l-r"
                            (NodeKind.Join
                                { Name = "l r"
                                  Left = "t:l"
                                  Right = "t:r"
                                  On = [ "c:l.k", "c:r.k" ] }) ] }

              let grants = [ grant "open" GrantLevel.RowLevel ]

              let pipeline =
                  [ Project [ "k", "k"; "a", "a" ]
                    Join(Ref "r", [ "k", "k" ], Inner)
                    Project [ "a_right", "a_right" ] ]

              let subject =
                  { Principal = "peer"
                    Attributes = Map.empty
                    Grants = grants }

              Expect.isError (Policy.columns model subject (Ref "l") pipeline) "the custodian reads the withheld column"

              Expect.equal
                  (ModelProjection.admits (ModelProjection.project model grants) (Ref "l") pipeline)
                  (Error(ProjectionRefusal.Withheld(PolicySite.Step 3, "r")))
                  "so the requester refuses the name"

          testCase "drift: added columns and widenings are safe; removals, narrowings and lowered grants break"
          <| fun _ ->
              let pinned = project publicRows

              let retype (id: string) (ty: ColumnType) (m: ModelNode) =
                  m
                  |> Sample.replace id (fun n ->
                      match n.Kind with
                      | NodeKind.Column c ->
                          { n with
                              Kind = NodeKind.Column(ColumnSpec.retype ty c) }
                      | _ -> n)

              let self = ModelProjection.drift pinned pinned
              Expect.isEmpty self.Changes "a pin does not drift from itself"
              Expect.equal self.Pinned self.Fresh "one hash"

              let widened =
                  ModelProjection.drift
                      pinned
                      (ModelProjection.project (retype "c:lines.qty" FloatType Sample.model) publicRows)

              Expect.equal
                  widened.Changes
                  [ ProjectionChange.ColumnRetyped("order_lines", "qty", IntType, FloatType) ]
                  "one retype"

              Expect.isEmpty widened.Breaking "int to float widens"

              let narrowed =
                  ModelProjection.drift
                      pinned
                      (ModelProjection.project (retype "c:orders.revenue" IntType Sample.model) publicRows)

              Expect.equal
                  narrowed.Breaking
                  [ ProjectionChange.ColumnRetyped("orders", "revenue", DecimalType, IntType) ]
                  "decimal to int does not"

              let wider = ModelProjection.drift pinned (project personalAggregate)

              Expect.equal
                  wider.Changes
                  [ ProjectionChange.ColumnAdded("customers", "email", StringType)
                    ProjectionChange.GrantChanged("personal", None, Some GrantLevel.Aggregate) ]
                  "a column and a grant added"

              Expect.isEmpty wider.Breaking "more is never breaking"

              let narrower = ModelProjection.drift (project personalAggregate) pinned

              Expect.equal
                  narrower.Breaking
                  [ ProjectionChange.ColumnRemoved("customers", "email", StringType)
                    ProjectionChange.GrantChanged("personal", Some GrantLevel.Aggregate, None) ]
                  "less is"

              let unrelated =
                  Sample.model
                  |> Sample.replace "j:orders-customers" (fun n ->
                      match n.Kind with
                      | NodeKind.Join j ->
                          { n with
                              Kind = NodeKind.Join { j with Name = "buyer" } }
                      | _ -> n)

              Expect.equal
                  (ModelProjection.drift pinned (ModelProjection.project unrelated publicRows)).Breaking
                  [ ProjectionChange.RelationRemoved "order customer" ]
                  "a relation renamed is one removed"

          testCase "the wire round-trips, and refuses a text that is not a projection"
          <| fun _ ->
              for name, text in corpusProjections () do
                  match ModelProjection.decode text with
                  | Ok p -> Expect.equal (ModelProjection.encode p) text name
                  | Error e -> failtestf "%s: %s" name (ModelWire.describe e)

              for name, text, code in corpusRejects () do
                  match ModelProjection.decode text with
                  | Error e -> Expect.equal (ModelWire.code e) code name
                  | Ok _ -> failtestf "%s decoded" name

          testCase "the projection laws hold over the sample, with every verdict and shape reached"
          <| fun _ ->
              let results = ModelProjection.Laws.laws 1907 2000
              census "ModelProjectionLaws" results
              Expect.isEmpty (failed results) "every projection law holds"

          testCase "the laws are not vacuous: a projection that leaks a hidden column breaks them"
          <| fun _ ->
              // A requester-side check that forgot the withheld columns: the same column authorisation
              // over the projection, every table taken as whole. The agreement law must catch it.
              let leaky (c: ModelProjection.Laws.Case) =
                  let p = ModelProjection.project c.Model c.Grants

                  let subject =
                      { Principal = "requester"
                        Attributes = Map.empty
                        Grants = p.Grants }

                  Policy.columns p.Model subject c.Source c.Pipeline,
                  Policy.columns c.Model { subject with Grants = c.Grants } c.Source c.Pipeline

              let disagreements =
                  ModelProjection.Laws.sample 1907 600
                  |> List.filter (fun c ->
                      match leaky c with
                      | Ok _, Error _ -> true
                      | _ -> false)

              Expect.isNonEmpty disagreements "a check blind to withheld columns admits what the custodian refuses" ]
