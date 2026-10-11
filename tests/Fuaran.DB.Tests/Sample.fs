/// A worked dataset model — a small retail source — that the suites check against.
module Fuaran.DB.Tests.Sample

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

let col (id: string) (name: string) (ty: ColumnType) (cls: string) : ModelNode =
    { Id = id
      Kind =
        NodeKind.Column
            { Name = name
              Physical = name.ToUpperInvariant()
              Type = ty
              Classification = cls }
      Children = [] }

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
