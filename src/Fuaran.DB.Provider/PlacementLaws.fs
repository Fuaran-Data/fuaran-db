namespace Fuaran.DB.Provider

open System.Text.RegularExpressions
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

/// One generated case: a model, a pipeline over one of its sources, the bindings, and the rows of
/// every modelled table in MODEL names.
type PlacementCase =
    {
        /// The dataset model.
        Model: ModelNode
        /// The pipeline's source.
        Source: DataSource
        /// The pipeline, as written.
        Pipeline: Transform list
        /// What it is evaluated under.
        Bindings: Bindings
        /// Every modelled table's rows, keyed and named by the model.
        Tables: Map<string, Table>
    }

/// The placement laws (`validator:Fuaran.DB.Provider.PlacementLaws`), on the shape of the compute
/// package's `plannerLaws`: over generated (model, pipeline, table) triples, the lowered prefix — run
/// by the executor a dialect is certified with — followed by the local suffix equals the reference
/// evaluator over the pipeline AS WRITTEN, the same table on `Ok` and the same `EvalError` on
/// `Error`.
///
/// The executor is a function, so the laws open no connection: a test supplies an in-memory
/// interpreter of the statement's form, a dialect's conformance run supplies an engine. It receives
/// every modelled table keyed and named PHYSICALLY, as a source would hold it.
[<RequireQualifiedAccess>]
module PlacementLaws =

    /// Run a statement over tables keyed by physical identifier, with physical column names: the
    /// result rows, positionally, or the refusal the engine reports.
    type Executor = Statement -> Map<string, Table> -> Result<Cell list list, EvalError>

    // ---- the model ----

    let private column (id: string) (name: string) (physical: string) (ty: ColumnType) : ModelNode =
        { Id = id
          Kind = NodeKind.Column(ColumnSpec.create name physical ty "public")
          Children = [] }

    /// A table of `(name, type)` columns over `cells`, one cell list per column. The sample draws
    /// only cells its column's type holds, so a refusal is a defect in the sample and throws.
    let private tableOf (columns: (string * ColumnType) list) (cells: Cell list list) : Table =
        { Schema = columns |> List.map (fun (n, ty) -> Field.create n ty)
          Columns =
            List.zip columns cells
            |> List.map (fun ((n, ty), cs) ->
                match Column.ofCells n ty cs with
                | Ok c -> c
                | Error e -> invalidArg n (ColumnCodec.errorString e)) }

    let private table (id: string) (name: string) (physical: string) (columns: ModelNode list) : ModelNode =
        { Id = id
          Kind = NodeKind.Table { Name = name; Physical = physical }
          Children = columns }

    let private relation (id: string) (left: string) (right: string) (on: (string * string) list) : ModelNode =
        { Id = id
          Kind =
            NodeKind.Join
                { Name = id
                  Left = left
                  Right = right
                  On = on }
          Children = [] }

    /// The model every case is drawn over: a fact table, a dimension it relates to, a second fact
    /// table relating to the first, and a table with no columns. Model names differ from physical
    /// identifiers throughout, so a statement that took a name from the request cannot pass by
    /// coincidence. `amount` is the type the measure column is drawn at.
    let modelOf (amount: ColumnType) : ModelNode =
        { Id = "m"
          Kind =
            NodeKind.Model
                { Name = "placement"
                  Classifications = [ "public" ]
                  Recordable = [] }
          Children =
            [ { Id = "src"
                Kind =
                  NodeKind.Source
                      { Name = "warehouse"
                        Fingerprint = None }
                Children =
                  [ table
                        "t:f"
                        "orders"
                        "FACT_ORDER"
                        [ column "c:f:id" "id" "ORD_ID" IntType
                          column "c:f:fk" "fk" "ORD_CUST" IntType
                          column "c:f:amt" "amt" "ORD_AMOUNT" amount
                          column "c:f:s" "s" "ORD_CHANNEL" StringType
                          column "c:f:b" "b" "ORD_FLAG" BoolType
                          column "c:f:d" "d" "ORD_DAY" DateType
                          column "c:f:big" "big" "ORD_POINTS" IntType ]
                    table
                        "t:d"
                        "customers"
                        "DIM_CUSTOMER"
                        [ column "c:d:id" "id" "CUST_ID" IntType
                          column "c:d:name" "name" "CUST_NAME" StringType
                          column "c:d:amt" "amt" "CUST_LIMIT" FloatType ]
                    table
                        "t:a"
                        "touches"
                        "FACT_TOUCH"
                        [ column "c:a:ref" "ref" "TOUCH_ORDER" IntType
                          column "c:a:w" "w" "TOUCH_WEIGHT" FloatType ]
                    table "t:e" "ledger" "EMPTY_LEDGER" [] ] }
              relation "j:order-customer" "t:f" "t:d" [ "c:f:fk", "c:d:id" ]
              relation "j:touch-order" "t:a" "t:f" [ "c:a:ref", "c:f:id" ] ] }

    /// Every modelled table renamed to its physical identifiers — what an executor reads.
    let physicalTables (model: ModelNode) (tables: Map<string, Table>) : Map<string, Table> =
        Model.tables model
        |> List.choose (fun (node, spec) ->
            Map.tryFind spec.Name tables
            |> Option.map (fun t ->
                let rename =
                    Model.columns node |> List.map (fun (_, c) -> c.Name, c.Physical) |> Map.ofList

                let physical (name: string) =
                    Map.tryFind name rename |> Option.defaultValue name

                spec.Physical,
                { Schema = t.Schema |> List.map (fun f -> ColumnSpec.renameField (physical f.Name) f)
                  Columns = t.Columns |> List.map (fun c -> { c with Name = physical c.Name }) }))
        |> Map.ofList

    // ---- the sample ----

    /// The clock every case that pins one pins.
    let clock: ClockWitness =
        fun g ->
            match g with
            | NowGrain.Date -> Date "2026-06-02"
            | NowGrain.Timestamp -> Timestamp "2026-06-02T12:00:00Z"

    let private agg (name: string) (fn: AggFn) (over: string) : Agg = { Name = name; Fn = fn; Of = over }

    let private customersTable (rows: (Cell * Cell * Cell) list) : Table =
        tableOf
            [ "id", IntType; "name", StringType; "amt", FloatType ]
            [ rows |> List.map (fun (a, _, _) -> a)
              rows |> List.map (fun (_, b, _) -> b)
              rows |> List.map (fun (_, _, c) -> c) ]

    /// The step templates a pipeline is drawn from. Each names what it is there to reach.
    let private templates (customers: Table) : Transform list list =
        let page key dir n offset = [ Sort [ key, dir ]; Limit(n, offset) ]

        [
          // filters that lower: total, typed, parameters bound
          [ Filter(Binary(Gt, Col "id", Lit(Int 2))) ]
          [ Filter(IsNull(Col "s")) ]
          [ Filter(Binary(Eq, Col "s", Param "p_s")) ]
          [ Filter(InParam(Col "s", "p_set")) ]
          [ Filter(Binary(Le, Col "d", Now NowGrain.Date)) ]
          [ Filter(Binary(Eq, Col "s", Lit Null)) ]
          [ Filter(Not(InList(Col "fk", [ Lit(Int 1); Lit(Int 3); Lit Null ]))) ]
          [ Filter(Case([ Binary(Gt, Col "id", Lit(Int 2)), Col "b" ], Lit(Bool false))) ]
          [ Filter(Binary(Or, IsNull(Col "b"), Binary(Eq, Coalesce [ Col "b"; Lit(Bool true) ], Lit(Bool true)))) ]
          [ Filter(Binary(Ge, Col "amt", Lit(Int 0))) ]
          // filters that place the boundary
          [ Filter(Binary(Or, Binary(Contains, Col "s", Lit(Str "1")), IsNull(Col "b"))) ]
          [ Filter(Binary(Eq, ApplyFn(Lower, [ Col "s" ]), Lit(Str "zq-abc"))) ]
          [ Filter(Binary(Gt, Cast(IntType, Col "s"), Lit(Int 1))) ]
          [ Filter(Binary(Gt, Binary(Add, Col "big", Col "big"), Lit(Int 0))) ]
          [ Filter(Binary(Lt, Col "d", Lit(Str "zq-day"))) ]
          [ Filter(Col "id") ]
          [ Filter(IsNull(Lit Null)) ]
          [ Filter(InList(Col "id", [])) ]
          [ Filter(Binary(Eq, Col "nope", Lit(Int 1))) ]
          [ Filter(Binary(Eq, Param "p_never", Col "s")) ]
          // projections
          [ Project [ "id", "id"; "fk", "fk"; "s", "req_channel"; "amt", "amt"; "big", "big" ] ]
          [ Project [ "id", "id"; "s", "s"; "b", "b"; "d", "d"; "amt", "amt"; "fk", "fk" ] ]
          [ Project [] ]
          [ Project [ "id", "req_x"; "s", "req_x" ] ]
          // joins
          [ Join(Ref "customers", [ "fk", "id" ], Inner) ]
          [ Join(Ref "customers", [ "fk", "id" ], Left) ]
          [ Join(Ref "customers", [ "fk", "id" ], Semi) ]
          [ Join(Ref "touches", [ "id", "ref" ], Anti) ]
          [ Join(Ref "touches", [ "id", "ref" ], Inner) ]
          [ Join(Ref "customers", [ "fk", "id" ], Outer) ]
          [ Join(Ref "customers", [ "id", "id" ], Inner) ]
          [ Join(Ref "nowhere", [ "fk", "id" ], Inner) ]
          [ Join(Embedded customers, [ "fk", "id" ], Inner) ]
          // groupings
          [ GroupBy([ "s" ], [ agg "req_n" Count "id"; agg "req_total" Sum "amt" ]) ]
          [ GroupBy([], [ agg "req_n" Count "id"; agg "req_last_day" Max "d" ]) ]
          [ GroupBy(
                [ "b" ],
                [ agg "req_u" CountDistinct "s"
                  agg "req_mean" Mean "amt"
                  agg "req_lo" Min "s" ]
            ) ]
          [ GroupBy([ "fk" ], [ agg "req_points" Sum "big" ]) ]
          // a keyless aggregate over all the rows: an int32 sum the statement itself refuses
          [ GroupBy([], [ agg "req_all_points" Sum "big" ]) ]
          // a keyless aggregate over no rows, where SQL answers one row and the reference none
          [ Filter(Binary(Gt, Col "id", Lit(Int 100)))
            GroupBy([], [ agg "req_n" Count "id"; agg "req_sum" Sum "amt" ]) ]
          [ GroupBy([ "s" ], [ agg "req_mid" Median "amt" ]) ]
          [ GroupBy([ "b" ], [ agg "req_bad" Sum "s" ]) ]
          [ GroupBy([ "s" ], [ agg "s" Count "id" ]) ]
          [ GroupBy([], []) ]
          // pages, and the sort and limit that do not pair
          page (Slot.Lit "id") Asc (Slot.Lit 3) (Slot.Lit 0)
          page (Slot.Lit "amt") Desc (Slot.Lit 2) (Slot.Lit 1)
          page (Slot.Param "p_col") Asc (Slot.Param "p_lim") (Slot.Lit 0)
          page (Slot.Lit "id") Desc (Slot.Lit -1) (Slot.Lit -2)
          page (Slot.Lit "nope") Asc (Slot.Lit 2) (Slot.Lit 0)
          page (Slot.Param "p_never_col") Asc (Slot.Lit 2) (Slot.Lit 0)
          page (Slot.Param "p_bad") Asc (Slot.Lit 2) (Slot.Lit 0)
          [ Sort [ Slot.Lit "id", Asc ] ]
          [ Limit(Slot.Lit 2, Slot.Lit 0) ]
          // verbs outside the subset
          [ Derive("req_next", Binary(Add, Col "id", Lit(Int 1))) ]
          [ Pivot
                { Index = [ "b" ]
                  On = "s"
                  Values = "id"
                  Agg = Count } ]
          [ Distinct ]
          [ Window
                { PartitionBy = [ "s" ]
                  OrderBy = [ "id", Asc ]
                  Fn = RowNumber
                  Of = "id"
                  As = "req_rank" } ]
          [ Unpivot([ "id" ], [ "fk" ]) ]
          [ Union(Ref "orders") ] ]

    /// Draw one case from the generator state.
    let private draw (r0: ConfRng.T) : PlacementCase * ConfRng.T =
        let r = ref r0

        let below (n: int) =
            let v, r' = ConfRng.intBelow n r.Value
            r.Value <- r'
            v

        let pick (xs: 'a list) = List.item (below (List.length xs)) xs

        let amount = pick [ IntType; FloatType; DecimalType ]
        let model = modelOf amount

        let amountCell () =
            let v = below 40

            if v % 7 = 0 then
                Null
            else
                match amount with
                | IntType -> Int(v - 10)
                | FloatType -> Float(float (v - 10) / 4.0)
                | _ -> Cell.decimal (sprintf "%d.%02d" (v / 10) (v % 100)) |> Option.defaultValue Null

        let maybe (p: int) (c: unit -> Cell) = if below 100 < p then Null else c ()
        let strings = [ "zq-a"; "zq-12"; "zq-Abc"; "zq1"; "" ]

        let orders =
            let n = below 7

            let rows =
                [ for i in 1..n ->
                      [ (if below 9 = 0 then Null else Int i)
                        maybe 15 (fun () -> Int(1 + below 4))
                        amountCell ()
                        maybe 20 (fun () -> Str(pick strings))
                        maybe 25 (fun () -> Bool(below 2 = 0))
                        maybe 15 (fun () -> Date(pick [ "2026-06-01"; "2026-06-02"; "2026-06-03" ]))
                        (if below 3 = 0 then Int 1500000000 else Int(below 100)) ] ]

            let schema =
                [ "id", IntType
                  "fk", IntType
                  "amt", amount
                  "s", StringType
                  "b", BoolType
                  "d", DateType
                  "big", IntType ]

            tableOf schema (schema |> List.mapi (fun j _ -> rows |> List.map (List.item j)))

        let customers =
            customersTable
                [ for i in 1 .. below 5 ->
                      (if below 8 = 0 then Null else Int i),
                      maybe 20 (fun () -> Str(pick [ "zq-Ann"; "zq-Bo"; "zq-Cy" ])),
                      maybe 20 (fun () -> Float(float (below 50) / 2.0)) ]

        let touches =
            let rows =
                [ for _ in 1 .. below 5 ->
                      maybe 15 (fun () -> Int(1 + below 6)), maybe 15 (fun () -> Float(float (below 9) / 3.0)) ]

            tableOf [ "ref", IntType; "w", FloatType ] [ rows |> List.map fst; rows |> List.map snd ]

        let ledger = tableOf [] []

        let tables =
            Map.ofList
                [ "orders", orders
                  "customers", customers
                  "touches", touches
                  "ledger", ledger ]

        let source =
            match below 100 with
            | v when v < 84 -> Ref "orders"
            | v when v < 90 -> Ref "touches"
            | v when v < 94 -> Ref "ledger"
            | v when v < 97 -> Ref "nowhere"
            | _ -> Embedded orders

        let menu = templates customers
        let steps = [ for _ in 1 .. below 5 -> pick menu ] |> List.concat

        let bindings =
            { Params =
                Map.ofList
                    [ if below 10 > 0 then
                          "p_s", Str "zq-a"
                      if below 10 > 0 then
                          "p_col", Str(pick [ "id"; "amt"; "s" ])
                      if below 10 > 0 then
                          "p_lim", Int(below 4)
                      "p_bad", Int 5 ]
              Lists =
                if below 10 > 0 then
                    Map.ofList [ "p_set", [ Str "zq-a"; Str "zq-12" ] ]
                else
                    Map.empty
              Clock = if below 4 > 0 then Some clock else None }

        { Model = model
          Source = source
          Pipeline = steps
          Bindings = bindings
          Tables = tables },
        r.Value

    /// `count` cases from `seed`, reproducibly.
    let sample (seed: int) (count: int) : PlacementCase list =
        let r = ref (ConfRng.ofSeed seed)

        [ for _ in 1..count do
              let c, r' = draw r.Value
              r.Value <- r'
              c ]

    // ---- the two placements ----

    /// The reference: the whole pipeline as written, over the source's rows.
    let reference (c: PlacementCase) : Result<Table, EvalError> =
        let resolve (name: string) =
            match Map.tryFind name c.Tables with
            | Some t -> Ok t
            | None -> Error(UnresolvedSource name)

        let pipeline =
            let listed = Transform.substituteListParams c.Bindings.Lists c.Pipeline

            match c.Bindings.Clock with
            | Some clk -> Transform.substituteNow clk listed
            | None -> listed

        let input =
            match c.Source with
            | Embedded t -> Ok t
            | Ref name -> resolve name

        input
        |> Result.bind (DataFrame.evalPipelineWithInEnvAsWritten resolve c.Bindings.Params pipeline)

    /// What a placement answered: the table or the refusal it reported, or why it could not finish.
    type Placed =
        /// The statement ran and the suffix finished: the answer, or the reference's own refusal.
        | Answered of Result<Table, EvalError>
        /// The statement's result could not be read back as its schema.
        | Unreadable of ColumnError

    /// The split placement: the statement through `execute`, then the suffix locally.
    let placed (execute: Executor) (c: PlacementCase) (lowered: Lowered) : Placed =
        let resolve (name: string) =
            match Map.tryFind name c.Tables with
            | Some t -> Ok t
            | None -> Error(UnresolvedSource name)

        match execute lowered.Statement (physicalTables c.Model c.Tables) with
        | Error e -> Answered(Error e)
        | Ok rows ->
            match Statement.toTable lowered.Statement rows with
            | Error e -> Unreadable e
            | Ok result -> Answered(FoldSplit.finish lowered resolve c.Bindings result)

    // ---- the statement carries no request text ----

    let private minted = Regex(@"^c[0-9]+$")

    /// Why a statement is not built from the model and parameters alone, or `None`: every physical
    /// identifier is the model's, every other identifier a minted alias, the parameters referenced
    /// exactly once each and in order, and neither a pipeline name that is not a model identifier
    /// nor a string value appears in the text.
    let requestTextIn (dialect: Dialect) (model: ModelNode) (s: Statement) : string option =
        let physicals =
            Model.tables model
            |> List.collect (fun (node, spec) ->
                spec.Physical :: (Model.columns node |> List.map (fun (_, c) -> c.Physical)))
            |> Set.ofList

        let rec outputNames (r: Relation) : string list =
            let own = Relation.outputs r |> List.map _.Name

            match r with
            | Relation.Scan _ -> own
            | Relation.Where(input, _)
            | Relation.Select(input, _)
            | Relation.Aggregate(input, _, _, _)
            | Relation.Ordered(input, _, _, _) -> own @ outputNames input
            | Relation.Join(left, right, _, _, _) -> own @ outputNames left @ outputNames right

        let foreignPhysical =
            Relation.physicalIdentifiers s.Form
            |> List.tryFind (fun p -> not (Set.contains p physicals))

        let foreignAlias =
            Relation.aliases s.Form |> List.tryFind (fun a -> not (minted.IsMatch a))

        let ordinals = Relation.parameters s.Form
        let expected = List.init (List.length s.Parameters) id

        let quotedNames =
            outputNames s.Form
            |> List.distinct
            |> List.filter (fun n -> not (Set.contains n physicals) && not (minted.IsMatch n))
            |> List.tryFind (fun n -> s.Text.Contains(dialect.QuoteIdentifier n))

        let values =
            s.Parameters
            |> List.tryPick (fun p ->
                match p.Value with
                | Str v when v <> "" && s.Text.Contains v -> Some v
                | _ -> None)

        match foreignPhysical, foreignAlias, quotedNames, values with
        | Some p, _, _, _ -> Some(sprintf "the identifier '%s' is not a physical identifier of the model" p)
        | _, Some a, _, _ -> Some(sprintf "the alias '%s' was not minted by the provider" a)
        | _, _, Some n, _ -> Some(sprintf "the pipeline name '%s' appears in the text as an identifier" n)
        | _, _, _, Some v -> Some(sprintf "the value '%s' appears in the text" v)
        | None, None, None, None ->
            if ordinals <> expected then
                Some(sprintf "the parameters are referenced as %A, not once each in order %A" ordinals expected)
            else
                None

    // ---- the laws ----

    let private family = "PlacementLaws"

    let rec private verbsOf (r: Relation) : string list =
        match r with
        | Relation.Scan _ -> []
        | Relation.Where(input, _) -> "filter" :: verbsOf input
        | Relation.Select(input, _) -> "project" :: verbsOf input
        | Relation.Join(left, right, kind, _, _) -> sprintf "join %A" kind :: (verbsOf left @ verbsOf right)
        | Relation.Aggregate(input, keys, _, _) ->
            (if List.isEmpty keys then "keyless groupBy" else "groupBy") :: verbsOf input
        | Relation.Ordered(input, _, _, _) -> "sort-limit page" :: verbsOf input

    /// The laws, over `iterations` cases drawn from `seed`, with `dialect` lowering and `execute`
    /// running its statements.
    let laws (dialect: Dialect) (execute: Executor) (seed: int) (iterations: int) : LawResult list =
        let refusals = System.Collections.Generic.Dictionary<string, int>()
        let verbs = System.Collections.Generic.Dictionary<string, int>()
        let sides = System.Collections.Generic.Dictionary<string, int>()
        let errors = System.Collections.Generic.Dictionary<string, int>()

        let bump (d: System.Collections.Generic.Dictionary<string, int>) (k: string) =
            d[k] <-
                (match d.TryGetValue k with
                 | true, n -> n + 1
                 | _ -> 1)

        let note (slot: string option ref) (text: string) =
            if slot.Value.IsNone then
                slot.Value <- Some text

        let parityR: string option ref = ref None
        let requestR: string option ref = ref None
        let coherentR: string option ref = ref None
        let purityR: string option ref = ref None
        let totalityR: string option ref = ref None

        sample seed iterations
        |> List.iteri (fun i c ->
            let at = sprintf "seed=%d iter=%d" seed i

            let attempt (f: unit -> 'a) : Result<'a, string> =
                try
                    Ok(f ())
                with ex ->
                    Error ex.Message

            match attempt (fun () -> FoldSplit.lowerUnchecked c.Model dialect c.Source c.Pipeline c.Bindings None) with
            | Error m -> note totalityR (sprintf "%s: the provider threw: %s" at m)
            | Ok(Error whole) ->
                bump refusals (Refusal.tag whole)

                let consistent =
                    match whole, c.Source with
                    | Refusal.EmbeddedSource, Embedded _ -> true
                    | Refusal.UnknownSource(name, _), Ref r -> name = r && Option.isNone (Model.tryTable r c.Model)
                    | Refusal.EmptyRow 0, Ref r ->
                        Model.tryTable r c.Model
                        |> Option.exists (fun (n, _) -> List.isEmpty (Model.columns n))
                    | _ -> false

                if not consistent then
                    note coherentR (sprintf "%s: a whole refusal %A does not describe the source %A" at whole c.Source)
            | Ok(Ok lowered) ->
                let split = lowered.Split
                let planned = split.Plan.Planned

                match split.Boundary with
                | Some r -> bump refusals (Refusal.tag r)
                | None -> ()

                verbsOf lowered.Statement.Form |> List.iter (bump verbs)

                // The keyless aggregate the statement carries over no rows is where SQL answers a row
                // the reference does not; the sample must reach it for the `NonEmpty` pin to be tested.
                planned
                |> List.take split.Lowered
                |> List.indexed
                |> List.iter (fun (k, step) ->
                    match step with
                    | GroupBy([], _) ->
                        match
                            reference
                                { c with
                                    Pipeline = List.take k planned },
                            reference c
                        with
                        | Ok t, Ok _ when Table.rowCount t = 0 -> bump sides "keyless aggregate over no rows"
                        | _ -> ()
                    | _ -> ())

                if split.Plan.Planned <> split.Plan.Written then
                    bump sides "planner rewrote"

                bump
                    sides
                    (match split.Lowered, lowered.Suffix with
                     | 0, [] -> "nothing to lower"
                     | 0, _ -> "nothing lowered"
                     | _, [] -> "all lowered"
                     | _ -> "split")

                let reportCoherent =
                    split.Lowered + List.length lowered.Suffix = List.length planned
                    && lowered.Suffix = List.skip split.Lowered planned
                    && (match split.Boundary with
                        | Some r -> Refusal.step r = split.Lowered + 1 && not (List.isEmpty lowered.Suffix)
                        | None -> List.isEmpty lowered.Suffix)

                if not reportCoherent then
                    note coherentR (sprintf "%s: the report does not account for the planned pipeline (%A)" at split)

                match requestTextIn dialect c.Model lowered.Statement with
                | Some why -> note requestR (sprintf "%s: %s; text: %s" at why lowered.Statement.Text)
                | None -> ()

                if
                    FoldSplit.lowerUnchecked c.Model dialect c.Source c.Pipeline c.Bindings None
                    <> Ok lowered
                then
                    note purityR (sprintf "%s: lowering twice answered two statements" at)

                match attempt (fun () -> placed execute c lowered), attempt (fun () -> reference c) with
                | Error m, _
                | _, Error m -> note totalityR (sprintf "%s: a placement threw: %s" at m)
                | Ok(Unreadable e), _ ->
                    note parityR (sprintf "%s: the statement's result could not be read back: %A" at e)
                | Ok(Answered split'), Ok whole ->
                    (match whole with
                     | Error _ -> bump errors "reference refused"
                     | Ok _ -> ())

                    (match execute lowered.Statement (physicalTables c.Model c.Tables) with
                     | Error _ -> bump errors "statement refused"
                     | Ok _ -> ())

                    let agree =
                        match split', whole with
                        | Ok a, Ok b -> ColumnCodec.encode (Embedded a) = ColumnCodec.encode (Embedded b)
                        | Error a, Error b -> a = b
                        | _ -> false

                    if not agree then
                        note
                            parityR
                            (sprintf
                                "%s: split %A, reference %A (pipeline=%A planned=%A lowered=%d boundary=%A)"
                                at
                                split'
                                whole
                                c.Pipeline
                                planned
                                split.Lowered
                                split.Boundary))

        let counts (d: System.Collections.Generic.Dictionary<string, int>) (keys: string list) =
            keys
            |> List.map (fun k ->
                k,
                (match d.TryGetValue k with
                 | true, n -> n
                 | _ -> 0))

        // A dialect that refuses nothing the sample draws cannot be asked to reach its own refusal.
        let drawn =
            [ Construct.Operator Contains
              Construct.Operator Gt
              Construct.Operator Eq
              Construct.Aggregate CountDistinct
              Construct.Aggregate Sum
              Construct.Join Semi
              Construct.Parameter StringType ]

        let refusalTags =
            if drawn |> List.exists (fun c -> Option.isSome (dialect.Refuses c)) then
                Refusal.allTags
            else
                Refusal.allTags |> List.filter (fun t -> t <> "dialectRefuses")

        [ { Law = "the lowered prefix followed by the local suffix equals the reference as written, errors included"
            Passed = parityR.Value.IsNone
            Counterexample = parityR.Value }
          { Law = "every statement is built from the model and its parameters alone: no request text"
            Passed = requestR.Value.IsNone
            Counterexample = requestR.Value }
          { Law = "the split report accounts for every planned step, and names the step the boundary falls at"
            Passed = coherentR.Value.IsNone
            Counterexample = coherentR.Value }
          { Law = "the provider is pure: lowering twice answers the same statement"
            Passed = purityR.Value.IsNone
            Counterexample = purityR.Value }
          { Law = "the provider, the executor and the reference are total (never throw)"
            Passed = totalityR.Value.IsNone
            Counterexample = totalityR.Value }
          SampleAdequacy.reached family "refusal" seed (counts refusals refusalTags)
          SampleAdequacy.reached
              family
              "lowered verb"
              seed
              (counts
                  verbs
                  [ "filter"
                    "project"
                    "join Inner"
                    "join Left"
                    "join Semi"
                    "join Anti"
                    "groupBy"
                    "keyless groupBy"
                    "sort-limit page" ])
          SampleAdequacy.reached
              family
              "side of the split"
              seed
              (counts
                  sides
                  [ "all lowered"
                    "split"
                    "nothing lowered"
                    "planner rewrote"
                    "keyless aggregate over no rows" ])
          SampleAdequacy.reached
              family
              "refused placement"
              seed
              (counts errors [ "reference refused"; "statement refused" ]) ]
