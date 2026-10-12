namespace Fuaran.DB.Conformance

open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open Fuaran.DB.Provider

/// A difference between an engine and the reference that a fixture may show, with the answers it
/// predicts (DECISIONS.md D8). A fixture whose answer differs from the reference is DIFFERING AS
/// PINNED only when it equals one of the answers a pin it declares predicts; any other difference is
/// unexpected. A pin is checkable, never a waiver.
[<RequireQualifiedAccess>]
type Pin =
    /// Strings compared, grouped and ordered under a case-insensitive collation. Predicts the
    /// reference over the fixture with every string of the seed and the bindings in lower case.
    | CaseInsensitiveCollation
    /// A float sum or mean added up in the engine's order. Predicts the reference over every
    /// permutation of the source table's rows (a source of at most seven rows).
    | FloatSummationOrder
    /// Strings compared and ordered by Unicode code point, where the reference compares UTF-16 code
    /// units: the two part only where a character beyond the Basic Multilingual Plane meets one in
    /// U+E000 to U+FFFF. Predicts the reference over the fixture with every string of the seed and the
    /// bindings re-encoded so that code-unit order is code-point order (DECISIONS.md D11.3); a fixture
    /// declaring it answers no string, since the re-encoded strings are not the engine's.
    | CodePointOrder

/// One fixture: a pipeline over a table of the seed, what it is evaluated under, and what it is there
/// to show.
type Fixture =
    {
        /// A stable id, the emitted file's name.
        Id: string
        /// What the fixture is there to show.
        Description: string
        /// The source table's model name.
        Source: string
        /// The pipeline as written. Every pipeline ends in a `Limit` within the seed's row ceiling,
        /// as the policy requires of any pipeline that lowers.
        Pipeline: Transform list
        /// What it is evaluated under.
        Bindings: Bindings
        /// Whether the answer's row order is part of it: true only for a pipeline ending in a page
        /// whose sort keys are unique over the rows it keeps. Otherwise rows compare as a bag.
        Ordered: bool
        /// The differences the fixture may show.
        Pins: Pin list
        /// The construct the fixture exists to reach, where it exists to reach one: a dialect that
        /// refuses it places the boundary there.
        Reaches: Construct option
    }

/// The seed and the fixtures: the stated subset a dialect is certified on (`validator:Fuaran.DB.Conformance`).
[<RequireQualifiedAccess>]
module Fixtures =

    let private column (id: string) (name: string) (physical: string) (ty: ColumnType) : ModelNode =
        { Id = id
          Kind =
            NodeKind.Column
                { Name = name
                  Physical = physical
                  Type = ty
                  Classification = "public" }
          Children = [] }

    let private table (id: string) (name: string) (physical: string) (columns: ModelNode list) : ModelNode =
        { Id = id
          Kind = NodeKind.Table { Name = name; Physical = physical }
          Children = columns }

    /// The seed's row ceiling, and the bound every fixture that does not end in a page ends with.
    let ceiling = 100

    /// The seed schema: five tables whose physical identifiers differ from their model names, one
    /// column of every type the columnar model has, one physical identifier carrying a closing
    /// bracket, one declared relation, and a limit that admits every fixture.
    let model: ModelNode =
        { Id = "m:conformance"
          Kind =
            NodeKind.Model
                { Name = "conformance"
                  Classifications = [ "public" ]
                  Recordable = [] }
          Children =
            [ { Id = "s:conformance"
                Kind =
                  NodeKind.Source
                      { Name = "conformance"
                        Fingerprint = None }
                Children =
                  [ table
                        "t:orders"
                        "orders"
                        "CK_ORDER"
                        [ column "c:o:id" "id" "ORD_ID" IntType
                          column "c:o:cust" "cust" "ORD_CUST" IntType
                          column "c:o:qty" "qty" "ORD_QTY" IntType
                          column "c:o:price" "price" "ORD_PRICE" FloatType
                          column "c:o:amount" "amount" "ORD_AMOUNT" DecimalType
                          column "c:o:channel" "channel" "ORD_CHANNEL" StringType
                          column "c:o:flag" "flag" "ORD_FLAG" BoolType
                          column "c:o:day" "day" "ORD_DAY" DateType
                          column "c:o:at" "at" "ORD_AT" TimestampType
                          column "c:o:note" "note" "ORD_NOTE]X" StringType ]
                    table
                        "t:customers"
                        "customers"
                        "CK_CUSTOMER"
                        [ column "c:c:id" "id" "CUST_ID" IntType
                          column "c:c:name" "name" "CUST_NAME" StringType
                          column "c:c:tier" "tier" "CUST_TIER" IntType ]
                    table
                        "t:points"
                        "points"
                        "CK_POINTS"
                        [ column "c:p:id" "id" "PT_ID" IntType
                          column "c:p:pts" "pts" "PT_VALUE" IntType ]
                    table
                        "t:readings"
                        "readings"
                        "CK_READING"
                        [ column "c:r:id" "id" "RD_ID" IntType
                          column "c:r:w" "w" "RD_VALUE" FloatType ]
                    table
                        "t:words"
                        "words"
                        "CK_WORD"
                        [ column "c:w:id" "id" "WD_ID" IntType
                          column "c:w:text" "text" "WD_TEXT" StringType ] ] }
              { Id = "j:order-customer"
                Kind =
                  NodeKind.Join
                      { Name = "order-customer"
                        Left = "t:orders"
                        Right = "t:customers"
                        On = [ "c:o:cust", "c:c:id" ] }
                Children = [] }
              { Id = "l:conformance"
                Kind =
                  NodeKind.Limit
                      { Name = "conformance"
                        MaxRows = ceiling
                        MaxJoins = Some 1
                        MaxGroupKeys = Some 2
                        MinGroupSize = None
                        TimeoutSeconds = Some 30 }
                Children = [] } ] }

    let private tableOf (schema: Schema) (rows: Cell list list) : Table =
        { Schema = schema
          Columns =
            schema
            |> List.mapi (fun j (name, ty) -> Column.create name ty (rows |> List.map (List.item j))) }

    let private dec (text: string) =
        Cell.decimal text |> Option.defaultValue Null

    /// The seed rows, keyed and named by the model. The strings of `orders.channel` are lower case
    /// and distinct under any collation; `customers.name` holds two names that differ only in case,
    /// read by the collation fixture alone.
    let tables: Map<string, Table> =
        let orders =
            tableOf
                [ "id", IntType
                  "cust", IntType
                  "qty", IntType
                  "price", FloatType
                  "amount", DecimalType
                  "channel", StringType
                  "flag", BoolType
                  "day", DateType
                  "at", TimestampType
                  "note", StringType ]
                [ [ Int 1
                    Int 1
                    Int 3
                    Float 2.5
                    dec "10.25"
                    Str "web"
                    Bool true
                    Date "2026-06-01"
                    Timestamp "2026-06-01T08:00:00Z"
                    Str " a\t" ]
                  [ Int 2
                    Int 2
                    Int 5
                    Null
                    dec "7.5"
                    Str "store"
                    Bool false
                    Date "2026-06-02"
                    Timestamp "2026-06-02T09:30:00Z"
                    Str "b" ]
                  [ Int 3
                    Int 1
                    Null
                    Float 4.0
                    Null
                    Str "web"
                    Null
                    Date "2026-06-03"
                    Null
                    Null ]
                  [ Int 4
                    Int 3
                    Int 2
                    Float 1.25
                    dec "3.1"
                    Null
                    Bool true
                    Null
                    Timestamp "2026-06-04T00:00:00Z"
                    Str "c\r\n" ]
                  [ Int 5
                    Null
                    Int 7
                    Float 8.0
                    dec "12"
                    Str "phone"
                    Bool false
                    Date "2026-06-02"
                    Timestamp "2026-06-02T23:59:59Z"
                    Str "a" ]
                  [ Int 6
                    Int 4
                    Int 1
                    Null
                    dec "0.05"
                    Str "store"
                    Bool true
                    Date "2026-06-05"
                    Timestamp "2026-06-05T12:00:00Z"
                    Null ]
                  [ Int 7
                    Int 2
                    Int 4
                    Float 3.5
                    dec "2"
                    Str "web"
                    Bool false
                    Date "2026-06-01"
                    Timestamp "2026-06-01T00:00:01Z"
                    Str "d" ]
                  [ Null
                    Int 1
                    Int 6
                    Float 2.0
                    dec "5.5"
                    Str "phone"
                    Null
                    Date "2026-06-03"
                    Timestamp "2026-06-03T06:00:00Z"
                    Str "e" ] ]

        let customers =
            tableOf
                [ "id", IntType; "name", StringType; "tier", IntType ]
                [ [ Int 1; Str "Ann"; Int 1 ]
                  [ Int 2; Str "ann"; Int 2 ]
                  [ Int 3; Str "Bo"; Null ]
                  [ Int 4; Null; Int 1 ]
                  [ Int 5; Str "Cy"; Int 2 ] ]

        let points =
            tableOf
                [ "id", IntType; "pts", IntType ]
                [ [ Int 1; Int 1500000000 ]; [ Int 2; Int 1000000000 ]; [ Int 3; Int 5 ] ]

        // Added left to right, the two ones are lost against 1e16 differently from any other order.
        let readings =
            tableOf
                [ "id", IntType; "w", FloatType ]
                [ [ Int 1; Float 1e16 ]
                  [ Int 2; Float 1.0 ]
                  [ Int 3; Float -1e16 ]
                  [ Int 4; Float 1.0 ] ]

        // A private-use character of the Basic Multilingual Plane, a character beyond it, and two ASCII
        // letters of different case: ordered by code unit, by code point and under a linguistic
        // collation, the four fall in three different orders (DECISIONS.md D11.3).
        let words =
            tableOf
                [ "id", IntType; "text", StringType ]
                [ [ Int 1; Str "" ]
                  [ Int 2; Str "\U0001F600" ]
                  [ Int 3; Str "a" ]
                  [ Int 4; Str "B" ] ]

        Map.ofList
            [ "orders", orders
              "customers", customers
              "points", points
              "readings", readings
              "words", words ]

    /// The seed as an executor reads it: keyed and named by physical identifier.
    let physical: Map<string, Table> = PlacementLaws.physicalTables model tables

    /// The bindings every fixture is evaluated under: one scalar parameter per use, one list, and the
    /// clock pinned at 2026-06-02.
    let bindings: Bindings =
        { Params = Map.ofList [ "p_channel", Str "web"; "p_name", Str "ann"; "p_private_use", Str "" ]
          Lists = Map.ofList [ "p_channels", [ Str "web"; Str "phone" ] ]
          Clock = Some PlacementLaws.clock }

    let private bound (steps: Transform list) =
        steps @ [ Limit(Slot.Lit ceiling, Slot.Lit 0) ]

    let private agg (name: string) (fn: AggFn) (over: string) : Agg = { Name = name; Fn = fn; Of = over }

    let private page (key: Slot<string>) (dir: SortDir) (n: Slot<int>) (offset: Slot<int>) =
        [ Sort [ key, dir ]; Limit(n, offset) ]

    let private fixture (id: string) (description: string) (source: string) (pipeline: Transform list) : Fixture =
        { Id = id
          Description = description
          Source = source
          Pipeline = bound pipeline
          Bindings = bindings
          Ordered = false
          Pins = []
          Reaches = None }

    let private ordered (id: string) (description: string) (pipeline: Transform list) : Fixture =
        { fixture id description "orders" [] with
            Pipeline = pipeline
            Ordered = true }

    let private reaching (c: Construct) (id: string) (source: string) (pipeline: Transform list) : Fixture =
        { fixture id (sprintf "reaches %A: a dialect that refuses it places the boundary here" c) source pipeline with
            Reaches = Some c }

    let private gt a b = Binary(Gt, a, b)
    let private ge a b = Binary(Ge, a, b)
    let private eq a b = Binary(Eq, a, b)
    let private float (f: float) = Lit(Float f)
    let private int (i: int) = Lit(Int i)
    let private str (s: string) = Lit(Str s)
    let private filter (e: ColExpr) = [ Filter e ]
    let private fn (f: ScalarFn) (args: ColExpr list) = ApplyFn(f, args)

    /// The subset: what a certified dialect agrees with the reference on.
    let subset: Fixture list =
        [ fixture "filter-compare-int" "an integer comparison" "orders" (filter (gt (Col "qty") (int 2)))
          fixture "filter-is-null" "the presence test" "orders" (filter (IsNull(Col "channel")))
          fixture "filter-param" "a bound scalar parameter" "orders" (filter (eq (Col "channel") (Param "p_channel")))
          fixture "filter-in-list-param" "a bound list" "orders" (filter (InParam(Col "channel", "p_channels")))
          fixture
              "filter-not-in-with-null"
              "three-valued membership in a list holding a null"
              "orders"
              (filter (Not(InList(Col "cust", [ int 1; int 3; Lit Null ]))))
          fixture "filter-bool-column" "a boolean column read as a condition" "orders" (filter (Col "flag"))
          fixture "filter-not-bool" "a negated boolean column" "orders" (filter (Not(Col "flag")))
          fixture
              "filter-case-yielding-bool"
              "a CASE whose branches are boolean values"
              "orders"
              (filter (Case([ gt (Col "id") (int 2), Col "flag" ], Lit(Bool false))))
          fixture
              "filter-coalesce-bool"
              "a COALESCE over a boolean column, compared"
              "orders"
              (filter (Binary(Or, IsNull(Col "flag"), eq (Coalesce [ Col "flag"; Lit(Bool true) ]) (Lit(Bool true)))))
          fixture
              "filter-condition-as-value"
              "a comparison read as a boolean value"
              "orders"
              (filter (eq (gt (Col "qty") (int 3)) (Col "flag")))
          fixture
              "filter-date-clock"
              "a date compared with the pinned clock"
              "orders"
              (filter (Binary(Le, Col "day", Now NowGrain.Date)))
          fixture
              "filter-timestamp"
              "a timestamp comparison"
              "orders"
              (filter (ge (Col "at") (Lit(Timestamp "2026-06-02T00:00:00Z"))))
          fixture "filter-decimal" "an exact decimal comparison" "orders" (filter (ge (Col "amount") (Lit(dec "5.5"))))
          fixture
              "filter-float-arithmetic"
              "float arithmetic in a predicate"
              "orders"
              (filter (gt (Binary(Sub, Binary(Mul, Col "price", float 2.0), float 1.0)) (float 5.0)))
          fixture "filter-abs" "ABS" "orders" (filter (gt (fn Abs [ Binary(Sub, Col "price", float 4.0) ]) (float 1.0)))
          fixture
              "filter-floor-ceil"
              "FLOOR and CEILING"
              "orders"
              (filter (
                  Binary(And, ge (fn Floor [ Col "price" ]) (float 2.0), Binary(Le, fn Ceil [ Col "price" ], float 4.0))
              ))
          fixture
              "filter-round"
              "ROUND half away from zero"
              "orders"
              (filter (gt (fn Round [ Col "price" ]) (float 3.0)))
          fixture
              "filter-sqrt-negative"
              "a negative root is null"
              "orders"
              (filter (IsNull(fn Sqrt [ Binary(Sub, Col "price", float 3.0) ])))
          fixture
              "filter-trim"
              "TRIM of the pinned ASCII set"
              "orders"
              (filter (InList(fn Trim [ Col "note" ], [ str "a"; str "c" ])))
          fixture
              "project-bracket"
              "a projection renaming a column whose physical identifier holds a closing bracket"
              "orders"
              [ Project [ "id", "id"; "note", "remark"; "channel", "ch" ] ]
          fixture "join-inner" "an inner join" "orders" [ Join(Ref "customers", [ "cust", "id" ], Inner) ]
          fixture "join-left" "a left join" "orders" [ Join(Ref "customers", [ "cust", "id" ], Left) ]
          fixture "join-semi" "a semi join" "orders" [ Join(Ref "customers", [ "cust", "id" ], Semi) ]
          fixture "join-anti" "an anti join" "orders" [ Join(Ref "customers", [ "cust", "id" ], Anti) ]
          fixture
              "group-by-channel"
              "count, an int sum, a decimal sum and a mean by a string key"
              "orders"
              [ GroupBy(
                    [ "channel" ],
                    [ agg "n" Count "id"
                      agg "qty" Sum "qty"
                      agg "amount" Sum "amount"
                      agg "mean_qty" Mean "qty" ]
                ) ]
          fixture
              "group-by-flag"
              "distinct count, a string minimum and a date maximum by a boolean key"
              "orders"
              [ GroupBy(
                    [ "flag" ],
                    [ agg "channels" CountDistinct "channel"
                      agg "first_channel" Min "channel"
                      agg "last_day" Max "day" ]
                ) ]
          fixture
              "group-min-max-bool"
              "the minimum and maximum of a boolean column"
              "orders"
              [ GroupBy([ "channel" ], [ agg "lo" Min "flag"; agg "hi" Max "flag" ]) ]
          fixture
              "group-keyless-empty"
              "a keyless aggregate over no rows answers none"
              "orders"
              [ Filter(gt (Col "id") (int 100))
                GroupBy([], [ agg "n" Count "id"; agg "amount" Sum "amount" ]) ]
          fixture
              "group-keyless"
              "a keyless aggregate over every row"
              "orders"
              [ GroupBy([], [ agg "n" Count "id"; agg "latest" Max "at"; agg "earliest" Min "day" ]) ]
          fixture
              "sum-int-in-range"
              "an int sum inside int32"
              "points"
              [ Filter(eq (Col "id") (int 3)); GroupBy([], [ agg "total" Sum "pts" ]) ]
          fixture
              "sum-int-overflow"
              "an int sum past int32 is refused, as the reference refuses it"
              "points"
              [ GroupBy([], [ agg "total" Sum "pts" ]) ]
          ordered
              "page-id"
              "a page by a unique key, skipping one row"
              (page (Slot.Lit "id") Asc (Slot.Lit 3) (Slot.Lit 1))
          ordered "page-price-desc" "a descending page" (page (Slot.Lit "price") Desc (Slot.Lit 5) (Slot.Lit 0))
          ordered
              "page-empty"
              "a page of no rows (a negative count, clamped)"
              (page (Slot.Lit "id") Asc (Slot.Lit -1) (Slot.Lit 0))
          ordered
              "filter-then-page"
              "a filter lowered with the page"
              (Filter(Col "flag") :: page (Slot.Lit "qty") Desc (Slot.Lit 2) (Slot.Lit 0)) ]

    /// The pinned differences, each with its fixture (DECISIONS.md D8).
    let differences: Fixture list =
        [ { fixture
                "difference-collation-case"
                "collation and case: a name compared with a bound value, then counted"
                "customers"
                [ Filter(eq (Col "name") (Param "p_name"))
                  GroupBy([], [ agg "n" Count "id" ]) ] with
              Pins = [ Pin.CaseInsensitiveCollation ] }
          ordered
              "difference-null-ordering-asc"
              "null ordering: nulls last ascending"
              (page (Slot.Lit "qty") Asc (Slot.Lit 8) (Slot.Lit 0))
          ordered
              "difference-null-ordering-desc"
              "null ordering: nulls last descending"
              (page (Slot.Lit "qty") Desc (Slot.Lit 8) (Slot.Lit 0))
          { fixture
                "difference-integer-division"
                "integer division: an integer quotient compared"
                "orders"
                (filter (ge (Binary(Div, Col "qty", int 2)) (float 2.0))) with
              Reaches = Some(Construct.Operator Div) }
          { fixture
                "difference-float-summation-order"
                "float summation order: a sum and a mean whose value depends on the order of addition"
                "readings"
                [ GroupBy([], [ agg "total" Sum "w"; agg "mean" Mean "w" ]) ] with
              Pins = [ Pin.FloatSummationOrder ] }
          { fixture
                "difference-decimal-scale-on-division"
                "decimal scale on division: an exact quotient compared"
                "orders"
                (filter (
                    gt
                        (Quotient(
                            Col "amount",
                            int 3,
                            { Scale = Slot.Lit 2
                              Mode = RoundingMode.HalfEven }
                        ))
                        (Lit(dec "2"))
                )) with
              Reaches = Some(Construct.Rounding RoundingMode.HalfEven) }
          { fixture
                "difference-date-truncation"
                "date truncation: the month read from a timestamp"
                "orders"
                (filter (eq (fn DatePart [ str "month"; Col "at" ]) (int 6))) with
              Reaches = Some(Construct.Function DatePart) }
          { fixture
                "difference-collation-order"
                "collation and order: the first three names in order, their ids kept"
                "customers"
                [ Sort [ Slot.Lit "name", Asc ]
                  Limit(Slot.Lit 3, Slot.Lit 0)
                  Project [ "id", "id" ] ] with
              Pins = [ Pin.CaseInsensitiveCollation ] }
          { fixture
                "difference-code-point-order"
                "code point order: strings below a private-use character, counted"
                "words"
                [ Filter(Binary(Lt, Col "text", Param "p_private_use"))
                  GroupBy([], [ agg "n" Count "id" ]) ] with
              Pins = [ Pin.CodePointOrder ] } ]

    /// One fixture per function, operator and aggregate a dialect may refuse, each reaching it where
    /// the reference evaluates it without refusing.
    let refusals: Fixture list =
        let f (c: ScalarFn) id args =
            reaching (Construct.Function c) id "orders" (filter (IsNull(fn c args)))

        let op (o: BinOp) id a b =
            reaching (Construct.Operator o) id "orders" (filter (IsNull(Binary(o, a, b))))

        let ag (a: AggFn) id over =
            reaching (Construct.Aggregate a) id "orders" [ GroupBy([ "channel" ], [ agg "v" a over ]) ]

        [ f Lower "reach-lower" [ Col "channel" ]
          f Upper "reach-upper" [ Col "channel" ]
          f Length "reach-length" [ Col "channel" ]
          f Substr "reach-substr" [ Col "channel"; int 1; int 2 ]
          f DateDiffDays "reach-date-diff-days" [ Col "day"; Col "at" ]
          f Concat "reach-concat" [ Col "channel"; Col "note" ]
          f Replace "reach-replace" [ Col "channel"; str "e"; str "E" ]
          f IndexOf "reach-index-of" [ Col "channel"; str "e" ]
          f Least "reach-least" [ Col "qty"; Col "cust" ]
          f Greatest "reach-greatest" [ Col "qty"; Col "cust" ]
          op Mod "reach-mod" (Col "qty") (int 3)
          op Contains "reach-contains" (Col "channel") (str "e")
          op StartsWith "reach-starts-with" (Col "channel") (str "w")
          op EndsWith "reach-ends-with" (Col "channel") (str "e")
          ag Median "reach-median" "price"
          ag StdDev "reach-std-dev" "price"
          ag First "reach-first" "price"
          ag Last "reach-last" "price"
          reaching (Construct.Cast StringType) "reach-cast" "orders" (filter (IsNull(Cast(StringType, Col "qty"))))
          reaching Construct.EmptyList "reach-empty-list" "orders" (filter (InList(Col "qty", []))) ]

    /// Every fixture of the kit.
    let all: Fixture list = subset @ differences @ refusals
