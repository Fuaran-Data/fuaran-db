/// A worked dataset model — a small retail source — that the suites check against.
module Fuaran.DB.Tests.Sample

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

let col (id: string) (name: string) (ty: ColumnType) (cls: string) : ModelNode =
    { Id = id
      Kind = NodeKind.Column(ColumnSpec.create name (name.ToUpperInvariant()) ty cls)
      Children = [] }

/// A schema of `(name, type)` entries stating no metadata.
let fields (entries: (string * ColumnType) list) : Schema =
    entries |> List.map (fun (n, ty) -> Field.create n ty)

/// A column of `cells`; a cell its type cannot hold fails the test that built it.
let column (name: string) (ty: ColumnType) (cells: Cell list) : Column =
    match Column.ofCells name ty cells with
    | Ok c -> c
    | Error e -> failwithf "%s: %s" name (ColumnCodec.errorString e)

/// A column's cells, in row order.
let cells (c: Column) : Cell list = Column.toCells c

let table (id: string) (name: string) (children: ModelNode list) : ModelNode =
    { Id = id
      Kind =
        NodeKind.Table
            { Name = name
              Physical = "dbo." + name }
      Children = children }

let leaf (id: string) (kind: NodeKind) : ModelNode = { Id = id; Kind = kind; Children = [] }

let orders =
    table
        "t:orders"
        "orders"
        [ col "c:orders.order_id" "order_id" IntType "public"
          col "c:orders.customer_id" "customer_id" IntType "public"
          col "c:orders.order_date" "order_date" DateType "public"
          col "c:orders.revenue" "revenue" DecimalType "public"
          col "c:orders.channel" "channel" StringType "public"
          leaf
              "p:orders.web"
              (NodeKind.RowPolicy
                  { Name = "web-only"
                    Predicate = Binary(Eq, Col "channel", Lit(Str "web")) }) ]

let lines =
    table
        "t:lines"
        "order_lines"
        [ col "c:lines.order_id" "order_id" IntType "public"
          col "c:lines.sku" "sku" StringType "public"
          col "c:lines.qty" "qty" IntType "public"
          col "c:lines.line_revenue" "line_revenue" DecimalType "public" ]

let customers =
    table
        "t:customers"
        "customers"
        [ col "c:customers.customer_id" "customer_id" IntType "public"
          col "c:customers.email" "email" StringType "personal"
          col "c:customers.segment" "segment" StringType "public" ]

let model: ModelNode =
    { Id = "m:retail"
      Kind =
        NodeKind.Model
            { Name = "retail"
              Classifications = [ "public"; "personal" ]
              Recordable = [] }
      Children =
        [ { Id = "s:sales"
            Kind = NodeKind.Source { Name = "sales"; Fingerprint = None }
            Children = [ orders; lines; customers ] }
          leaf
              "j:orders-lines"
              (NodeKind.Join
                  { Name = "order lines"
                    Left = "t:orders"
                    Right = "t:lines"
                    On = [ "c:orders.order_id", "c:lines.order_id" ] })
          leaf
              "j:orders-customers"
              (NodeKind.Join
                  { Name = "order customer"
                    Left = "t:orders"
                    Right = "t:customers"
                    On = [ "c:orders.customer_id", "c:customers.customer_id" ] })
          leaf
              "x:revenue"
              (NodeKind.Measure
                  { Name = "revenue"
                    Fn = Sum
                    Column = "c:orders.revenue" })
          leaf "l:rows" (NodeKind.Limit(LimitSpec.rows "rows" 100000)) ] }

let state: ModelState = ModelOps.ofRoot model

/// Replace the node carrying `id` anywhere in `root`.
let rec replace (id: string) (f: ModelNode -> ModelNode) (root: ModelNode) : ModelNode =
    if root.Id = id then
        f root
    else
        { root with
            Children = root.Children |> List.map (replace id f) }

/// A staged retail model and the first consumer's pipeline shapes, ported as structure (DECISIONS.md D19):
/// order headers, order lines, customers and web orders, related on an order key and a customer key, each
/// key classified `pseudonymous`. An order stage and a shopper stage are groupings that keep their key;
/// joins follow those keys before and after a rename; the stages join grouped operands; and each shape
/// re-aggregates to the grain it returns.
module Staged =

    let private modelled (table: string) (name: string) (ty: ColumnType) (cls: string) : ModelNode =
        { Id = sprintf "c:%s.%s" table name
          Kind = NodeKind.Column(ColumnSpec.create name ("SHOP_" + name.ToUpperInvariant()) ty cls)
          Children = [] }

    let private shopTable (id: string) (name: string) (physical: string) (columns: ModelNode list) : ModelNode =
        { Id = id
          Kind = NodeKind.Table { Name = name; Physical = physical }
          Children = columns }

    /// The classification both keys carry.
    let keyed = "pseudonymous"

    let private relation (id: string) (left: string) (right: string) (on: string * string) : ModelNode =
        leaf
            id
            (NodeKind.Join
                { Name = id
                  Left = left
                  Right = right
                  On = [ on ] })

    let model: ModelNode =
        { Id = "m:shop"
          Kind =
            NodeKind.Model
                { Name = "shop"
                  Classifications = [ "public"; keyed ]
                  Recordable = [] }
          Children =
            [ { Id = "s:shop"
                Kind = NodeKind.Source { Name = "shop"; Fingerprint = None }
                Children =
                  [ shopTable
                        "t:headers"
                        "order_headers"
                        "SHOP_HEADERS"
                        [ modelled "headers" "order_key" StringType keyed
                          modelled "headers" "customer_key" StringType keyed
                          modelled "headers" "order_day" DateType "public"
                          modelled "headers" "store" StringType "public"
                          modelled "headers" "spend" DecimalType "public"
                          modelled "headers" "units" DecimalType "public"
                          modelled "headers" "first_online" IntType "public"
                          modelled "headers" "counts_toward" IntType "public"
                          modelled "headers" "staff_gift" IntType "public" ]
                    shopTable
                        "t:lines"
                        "order_lines"
                        "SHOP_LINES"
                        [ modelled "lines" "order_key" StringType keyed
                          modelled "lines" "category" StringType "public"
                          modelled "lines" "product" StringType "public"
                          modelled "lines" "quantity" DecimalType "public"
                          modelled "lines" "line_spend" DecimalType "public" ]
                    shopTable
                        "t:customers"
                        "customers"
                        "SHOP_CUSTOMERS"
                        [ modelled "customers" "customer_key" StringType keyed
                          modelled "customers" "region" StringType "public"
                          modelled "customers" "country" StringType "public" ]
                    shopTable
                        "t:web"
                        "web_orders"
                        "SHOP_WEB"
                        [ modelled "web" "order_ref" StringType keyed
                          modelled "web" "campaign" StringType "public"
                          modelled "web" "channel" StringType "public"
                          modelled "web" "origin" StringType "public" ] ] }
              relation "j:headers-lines" "t:headers" "t:lines" ("c:headers.order_key", "c:lines.order_key")
              relation
                  "j:headers-customers"
                  "t:headers"
                  "t:customers"
                  ("c:headers.customer_key", "c:customers.customer_key")
              relation "j:headers-web" "t:headers" "t:web" ("c:headers.order_key", "c:web.order_ref")
              leaf
                  "l:shop"
                  (NodeKind.Limit
                      { Name = "shop"
                        MaxRows = 100000
                        MaxJoins = Some 4
                        MaxGroupKeys = Some 12
                        MinGroupSize = Some 1
                        TimeoutSeconds = Some 600 }) ] }

    /// A subject holding the keys at Phase 40's level, and every other column row by row: the subject
    /// every shape is admitted, lowered and run under (DECISIONS.md D20).
    let subject: Subject =
        { Principal = "staged"
          Attributes = Map.empty
          Grants =
            [ { Classification = "public"
                Level = GrantLevel.RowLevel }
              { Classification = keyed
                Level = GrantLevel.Keyed } ] }

    let private dec (text: string) =
        Cell.decimal text |> Option.defaultValue Null

    let private tableOf (columns: (string * ColumnType) list) (rows: Cell list list) : Table =
        { Schema = fields columns
          Columns =
            columns
            |> List.mapi (fun j (name, ty) -> column name ty (rows |> List.map (List.item j))) }

    let private s (text: string) = Str text

    /// The rows, by model table name: an order spread over two header rows, a header row with no
    /// customer key, an order whose customer the customers table does not hold, a customer key the
    /// customers table holds twice, an order with no lines, a line with no quantity, two web-order rows
    /// for one order (one of them blank), and a gifted order the order stage drops.
    let tables: Map<string, Table> =
        let headers =
            tableOf
                [ "order_key", StringType
                  "customer_key", StringType
                  "order_day", DateType
                  "store", StringType
                  "spend", DecimalType
                  "units", DecimalType
                  "first_online", IntType
                  "counts_toward", IntType
                  "staff_gift", IntType ]
                [ [ s "o1"
                    s "k1"
                    Date "2026-11-01"
                    s "north"
                    dec "20"
                    dec "2"
                    Int 1
                    Int 1
                    Int 0 ]
                  [ s "o1"
                    s "k1"
                    Date "2026-11-01"
                    s "north"
                    dec "20"
                    dec "2"
                    Int 1
                    Int 1
                    Null ]
                  [ s "o2"
                    s "k2"
                    Date "2026-11-01"
                    s "north"
                    dec "75.5"
                    dec "3"
                    Int 0
                    Int 1
                    Int 0 ]
                  [ s "o3"
                    s "k1"
                    Date "2026-11-02"
                    s "south"
                    Null
                    dec "1"
                    Int 0
                    Int 1
                    Int 0 ]
                  [ s "o4"
                    s "k3"
                    Date "2026-11-02"
                    s "south"
                    dec "320"
                    Null
                    Int 1
                    Int 1
                    Int 0 ]
                  [ s "o5"
                    Null
                    Date "2026-11-02"
                    s "south"
                    dec "5"
                    dec "1"
                    Int 1
                    Int 1
                    Int 0 ]
                  [ s "o6"
                    s "k9"
                    Date "2026-11-01"
                    s "north"
                    dec "-4"
                    dec "1"
                    Int 1
                    Int 1
                    Int 0 ]
                  [ s "o7"
                    s "k2"
                    Date "2026-11-01"
                    s "north"
                    dec "1200"
                    dec "9"
                    Int 0
                    Int 1
                    Int 1 ]
                  [ s "o8"
                    s "k3"
                    Date "2026-11-02"
                    s "south"
                    dec "40"
                    dec "2"
                    Int 1
                    Null
                    Int 0 ] ]

        let lines =
            tableOf
                [ "order_key", StringType
                  "category", StringType
                  "product", StringType
                  "quantity", DecimalType
                  "line_spend", DecimalType ]
                [ [ s "o1"; s "food"; s "tea"; dec "1"; dec "8" ]
                  [ s "o1"; s "food"; s "jam"; dec "1"; dec "12" ]
                  [ s "o2"; s "home"; Null; dec "3"; dec "75.5" ]
                  [ s "o3"; Null; s "mug"; Null; Null ]
                  [ s "o4"; s "home"; s "lamp"; dec "2"; dec "320" ]
                  [ s "o9"; s "food"; s "tea"; dec "1"; dec "8" ] ]

        let customers =
            tableOf
                [ "customer_key", StringType; "region", StringType; "country", StringType ]
                [ [ s "k1"; s "home"; s "uk" ]
                  [ s "k2"; s "abroad"; Null ]
                  [ s "k3"; Null; s "fr" ]
                  [ s "k3"; s "abroad"; s "fr" ] ]

        let web =
            tableOf
                [ "order_ref", StringType
                  "campaign", StringType
                  "channel", StringType
                  "origin", StringType ]
                [ [ s "o1"; s "spring"; s "mail"; s "list" ]
                  [ s "o1"; s ""; s "search"; s "" ]
                  [ s "o2"; s "spring"; s "social"; Null ]
                  [ s "o4"; Null; s "mail"; s "list" ] ]

        Map.ofList
            [ "order_headers", headers
              "order_lines", lines
              "customers", customers
              "web_orders", web ]

    let private agg (name: string) (fn: AggFn) (over: string) : Agg = { Name = name; Fn = fn; Of = over }
    let private text (v: string) = Lit(Str v)
    let private whole (n: int) = Lit(Int n)
    let private eq a b = Binary(Eq, a, b)
    let private both a b = Binary(And, a, b)
    let private orUnknown (e: ColExpr) = Coalesce [ e; text "unknown" ]
    let private onlyWhere (condition: ColExpr) (value: ColExpr) = Case([ condition, value ], Lit Null)
    let private amount (v: string) = Lit(dec v)

    /// The shopper stage: one row per customer key, the most of each attribute.
    let shoppers: Operand =
        { Name = "shoppers"
          Table = "customers"
          Pipeline = [ GroupBy([ "customer_key" ], [ agg "region" Max "region"; agg "country" Max "country" ]) ] }

    /// One attribution per order: a blank read as no value, the most of each field, and how many rows.
    let attribution: Operand =
        let picked = [ "campaign"; "channel"; "origin" ]

        { Name = "attribution"
          Table = "web_orders"
          Pipeline =
            [ for c in picked -> Derive(c, Case([ eq (Col c) (text ""), Lit Null ], Col c)) ]
            @ [ GroupBy(
                    [ "order_ref" ],
                    [ for c in picked -> agg c Max c ]
                    @ [ agg "attribution_rows" Count "order_ref" ]
                ) ] }

    /// The first online orders each customer key holds, among the orders that count.
    let firstOrders: Operand =
        { Name = "first_orders"
          Table = "order_headers"
          Pipeline =
            [ Filter(
                  both (eq (Coalesce [ Col "counts_toward"; whole 0 ]) (whole 1)) (eq (Col "first_online") (whole 1))
              )
              GroupBy([ "customer_key" ], [ agg "first_orders" CountDistinct "order_key" ]) ] }

    let operands: Operand list = [ shoppers; attribution; firstOrders ]

    let private spendBand (spend: ColExpr) =
        let below (bound: string) = Binary(Lt, spend, amount bound)

        Case(
            [ IsNull spend, text "not stated"
              below "0", text "refund"
              below "50", text "under 50"
              below "250", text "50 to 250" ],
            text "250 and over"
        )

    /// The order stage, over the order headers: one row per counted order of a known customer, a
    /// grouping that keeps both keys; then its shopper's attributes and its one attribution, joined on
    /// those keys; then each key renamed.
    let orderStage: Transform list =
        let flag name = Coalesce [ Col name; whole 0 ]

        [ Filter(
              both
                  (both (eq (flag "counts_toward") (whole 1)) (Not(eq (flag "staff_gift") (whole 1))))
                  (Binary(Ne, Coalesce [ Col "customer_key"; text "" ], text ""))
          )
          GroupBy(
              [ "order_key"; "customer_key" ],
              [ for c in [ "order_day"; "store"; "spend"; "units"; "first_online" ] -> agg c Max c ]
          )
          Join(Ref "shoppers", [ "customer_key", "customer_key" ], Left)
          Join(Ref "attribution", [ "order_key", "order_ref" ], Left)
          Derive("shopper_type", Case([ eq (Col "first_online") (whole 1), text "new" ], text "returning"))
          Derive("region", orUnknown (Col "region"))
          Derive("country", orUnknown (Col "country")) ]
        @ [ for c in [ "channel"; "origin"; "campaign" ] ->
                Derive(c, Case([ IsNull(Col "attribution_rows"), text "no match" ], orUnknown (Col c))) ]
        @ [ Derive("spend_band", spendBand (Col "spend"))
            Project
                [ "order_key", "order_id"
                  "customer_key", "shopper"
                  "customer_key_right", "shopper_ref"
                  "order_day", "day"
                  "store", "store"
                  "spend", "spend"
                  "units", "units"
                  "shopper_type", "shopper_type"
                  "region", "region"
                  "country", "country"
                  "channel", "channel"
                  "origin", "origin"
                  "campaign", "campaign"
                  "spend_band", "spend_band"
                  "attribution_rows", "attribution_rows" ] ]

    /// The line stage, after the order stage: each order's lines joined on the renamed order key, or one
    /// unknown line of nothing for an order without any.
    let lineStage: Transform list =
        let noLine = IsNull(Col "order_key")

        [ Join(Ref "order_lines", [ "order_id", "order_key" ], Left)
          Derive("category", Case([ noLine, text "unknown" ], orUnknown (Col "category")))
          Derive("product", Case([ noLine, text "unknown" ], orUnknown (Col "product")))
          Derive("quantity", Case([ noLine, amount "0" ], Col "quantity"))
          Derive("line_spend", Case([ noLine, amount "0" ], Col "line_spend"))
          Project(
              [ "order_id"
                "shopper"
                "day"
                "store"
                "spend"
                "shopper_type"
                "region"
                "country"
                "category"
                "product"
                "quantity"
                "line_spend" ]
              |> List.map (fun c -> c, c)
          ) ]

    /// A page over the returned grain, sorted by every key so the order is the answer's.
    let private page (keys: string list) =
        [ Sort(keys |> List.map (fun k -> Slot.Lit k, Asc))
          Limit(Slot.Lit 1000, Slot.Lit 0) ]

    /// The checks the quality shape returns, one row per day, store and check.
    let checks =
        [ "base_rows"
          "base_orders"
          "matched_orders"
          "matched_spend"
          "first_order_repeats" ]

    /// The six shapes, by name: each a pipeline over the order headers that re-aggregates to the grain it
    /// returns. The first five end in a page; the sixth ends in an unpivot, outside the subset.
    let shapes: (string * Transform list) list =
        [ "orders",
          orderStage
          @ [ GroupBy(
                  [ "day"; "store"; "shopper_type"; "spend_band" ],
                  [ agg "orders" Count "order_id"
                    agg "revenue" Sum "spend"
                    agg "units" Sum "units"
                    agg "spend_orders" Count "spend"
                    agg "unit_orders" Count "units"
                    agg "least_spend" Min "spend"
                    agg "most_spend" Max "spend" ]
              ) ]
          @ page [ "day"; "store"; "shopper_type"; "spend_band" ]
          "lines",
          orderStage
          @ lineStage
          @ [ GroupBy(
                  [ "day"; "store"; "category"; "product" ],
                  [ agg "line_spend" Sum "line_spend"
                    agg "quantity" Sum "quantity"
                    agg "orders" CountDistinct "order_id" ]
              ) ]
          @ page [ "day"; "store"; "category"; "product" ]
          "shoppers",
          orderStage
          @ [ Derive("spend_shopper", onlyWhere (Not(IsNull(Col "spend"))) (Col "shopper"))
              Derive("new_shopper", onlyWhere (eq (Col "shopper_type") (text "new")) (Col "shopper"))
              Derive("returning_shopper", onlyWhere (eq (Col "shopper_type") (text "returning")) (Col "shopper"))
              GroupBy(
                  [ "day"; "store"; "region"; "country" ],
                  [ agg "shoppers" CountDistinct "shopper"
                    agg "spend_shoppers" CountDistinct "spend_shopper"
                    agg "new_shoppers" CountDistinct "new_shopper"
                    agg "returning_shoppers" CountDistinct "returning_shopper" ]
              ) ]
          @ page [ "day"; "store"; "region"; "country" ]
          "new_shopper_products",
          orderStage
          @ lineStage
          @ [ Filter(eq (Col "shopper_type") (text "new"))
              GroupBy(
                  [ "day"; "store"; "category"; "product" ],
                  [ agg "orders" CountDistinct "order_id"
                    agg "units" Sum "quantity"
                    agg "revenue" Sum "line_spend" ]
              ) ]
          @ page [ "day"; "store"; "category"; "product" ]
          "campaigns",
          orderStage
          @ [ GroupBy(
                  [ "day"; "store"; "channel"; "origin"; "campaign" ],
                  [ agg "orders" Count "order_id"; agg "revenue" Sum "spend" ]
              ) ]
          @ page [ "day"; "store"; "channel"; "origin"; "campaign" ]
          "quality",
          orderStage
          @ [ Join(Ref "first_orders", [ "shopper_ref", "customer_key" ], Left)
              Derive("base_row", whole 1)
              Derive("matched_order", Case([ Not(IsNull(Col "attribution_rows")), whole 1 ], whole 0))
              Derive("matched_spend", onlyWhere (Not(IsNull(Col "attribution_rows"))) (Col "spend"))
              Derive(
                  "repeated_first",
                  Case(
                      [ both (eq (Col "shopper_type") (text "new")) (Binary(Gt, Col "first_orders", whole 1)), whole 1 ],
                      whole 0
                  )
              )
              GroupBy(
                  [ "day"; "store" ],
                  [ agg "base_rows" Sum "base_row"
                    agg "base_orders" Count "order_id"
                    agg "matched_orders" Sum "matched_order"
                    agg "matched_spend" Sum "matched_spend"
                    agg "first_order_repeats" Sum "repeated_first" ]
              ) ]
          @ [ for c in checks -> Derive(c, Cast(DecimalType, Col c)) ]
          @ [ Unpivot([ "day"; "store" ], checks)
              Project [ "day", "day"; "store", "store"; "variable", "check"; "value", "value" ] ]
          @ page [ "day"; "store"; "check" ] ]

    /// The same subject with the keys held row by row: the grant whose answers the keyed grant's are
    /// compared with (DECISIONS.md D20).
    let rowLevel: Subject =
        { subject with
            Grants = subject.Grants |> List.map (fun g -> { g with Level = GrantLevel.RowLevel }) }

    /// A shape by name.
    let shape (name: string) : Transform list =
        shapes |> List.find (fun (n, _) -> n = name) |> snd

    /// The policy over a shape for `holder`.
    let admit (holder: Subject) (pipeline: Transform list) : Result<Admitted, PolicyRefusal> =
        Policy.admitWith model holder operands (Ref "order_headers") pipeline

    /// The staged model with its group floor at `floor`.
    let withFloor (floor: int) : ModelNode =
        replace
            "l:shop"
            (fun n ->
                match n.Kind with
                | NodeKind.Limit l ->
                    { n with
                        Kind = NodeKind.Limit { l with MinGroupSize = Some floor } }
                | _ -> n)
            model

    /// The policy over a pipeline for `holder`, against the staged model with its floor at `floor`.
    let admitAt (floor: int) (holder: Subject) (pipeline: Transform list) : Result<Admitted, PolicyRefusal> =
        Policy.admitWith (withFloor floor) holder operands (Ref "order_headers") pipeline

    /// Not a staged shape: a probe of what a kept group answers over an input with no contributor
    /// (DECISIONS.md D21). Per store, the orders and the shoppers, and every aggregate over a copy of the
    /// shopper key and of the spend that no row holds: no order has a spend below minus a million.
    let emptyInputs: Transform list =
        let never = Binary(Lt, Col "spend", amount "-1000000")

        orderStage
        @ [ Derive("never_shopper", onlyWhere never (Col "shopper"))
            Derive("never_spend", onlyWhere never (Col "spend"))
            GroupBy(
                [ "store" ],
                [ agg "orders" Count "order_id"
                  agg "shoppers" Count "shopper"
                  agg "never_shoppers" Count "never_shopper"
                  agg "never_distinct" CountDistinct "never_shopper"
                  agg "never_count" Count "never_spend"
                  agg "never_sum" Sum "never_spend"
                  agg "never_mean" Mean "never_spend"
                  agg "never_least" Min "never_spend"
                  agg "never_most" Max "never_spend" ]
            ) ]
        @ page [ "store" ]

    /// What an engine leg runs, by name, with the floor each is admitted at: the six shapes at the staged
    /// model's own floor, and the empty-input probe at a floor of 1 and of 2 (DECISIONS.md D21).
    let engineRuns: (string * int * Transform list) list =
        [ for name, pipeline in shapes -> name, 1, pipeline ]
        @ [ "empty inputs at a floor of 1", 1, emptyInputs
            "empty inputs at a floor of 2", 2, emptyInputs ]

    /// The reference evaluator's answer to an admitted shape: its pipeline and operands as admitted.
    let reference (admitted: Admitted) : Result<Table, EvalError> =
        Fuaran.DB.Provider.PlacementLaws.reference
            { Model = model
              Source = Ref "order_headers"
              Pipeline = admitted.Pipeline
              Operands = admitted.Operands
              Bindings = Fuaran.DB.Provider.Bindings.none
              Tables = tables }

    /// The rows keyed and named by physical identifier, as an engine or the interpreter reads them.
    let physical: Map<string, Table> =
        Fuaran.DB.Provider.PlacementLaws.physicalTables model tables

    /// An admitted shape lowered through `dialect`, its statement run by `execute`, and its local suffix
    /// finished: the placement's answer, beside the split it was placed by.
    let placed
        (dialect: Fuaran.DB.Provider.Dialect)
        (execute: Fuaran.DB.Provider.Statement -> Result<Cell list list, string>)
        (admitted: Admitted)
        : Result<Fuaran.DB.Provider.Lowered * Result<Table, EvalError>, string> =
        let resolve (name: string) =
            match Map.tryFind name tables with
            | Some t -> Ok t
            | None -> Error(UnresolvedSource name)

        match Fuaran.DB.Provider.FoldSplit.lower admitted dialect Fuaran.DB.Provider.Bindings.none with
        | Error r -> Error(Fuaran.DB.Provider.Refusal.describe r)
        | Ok lowered ->
            execute lowered.Statement
            |> Result.bind (fun cells ->
                match Fuaran.DB.Provider.Statement.toTable lowered.Statement cells with
                | Error e -> Error(sprintf "the result could not be read: %A" e)
                | Ok result ->
                    Ok(
                        lowered,
                        Fuaran.DB.Provider.FoldSplit.finish lowered resolve Fuaran.DB.Provider.Bindings.none result
                    ))

    /// The returned grain of a shape: the keys of its last grouping.
    let grainOf (name: string) : string list =
        shapes
        |> List.find (fun (n, _) -> n = name)
        |> snd
        |> List.choose (function
            | GroupBy(keys, _) -> Some keys
            | _ -> None)
        |> List.last
