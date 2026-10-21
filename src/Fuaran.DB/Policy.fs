namespace Fuaran.DB

open Fuaran.Core
open Fuaran.Compute

// ============================================================================
//  Policy before lowering (Phase 1902). What a subject may read is decided on
//  the tree, before any statement exists, in two forms and no others:
//
//  REFUSAL — the pipeline is not lowered, and the refusal names the rule, the
//  subject attribute it read, and what would satisfy it.
//
//  REWRITING — the pipeline is changed before lowering, by steps a reader can
//  inspect: a row policy becomes a `Filter` the pipeline cannot remove, and a
//  grouping over an aggregate-only column gains a count and a `Filter` at the
//  model's floor.
//
//  The subject is a VALUE the host supplies: a principal, its attributes and its
//  grants, as data. The domain never resolves an identity.
//
//  Default-deny, stated once: a subject with no grants lowers nothing, and a
//  column whose classification the subject holds no grant over is unreadable.
//  The verdict is `Admitted`, which only `Policy.admit` constructs; the provider
//  lowers nothing else (DECISIONS.md D5).
// ============================================================================

/// How far a grant over a classification reaches.
[<RequireQualifiedAccess>]
type GrantLevel =
    /// A column of the classification may be read only as the input of an aggregate, in groups no
    /// smaller than the model's floor; never row by row.
    | Aggregate
    /// A column of the classification may be read row by row.
    | RowLevel

/// One grant: a classification the model declares, and how far the subject may read it.
type Grant =
    {
        /// The classification granted.
        Classification: string
        /// How far it reaches.
        Level: GrantLevel
    }

/// The subject a pipeline is checked for — a value the host supplies, never one the domain
/// resolves. In a custodian topology the host builds it from a validated peer token, never from
/// the request.
type Subject =
    {
        /// Who the subject is, for the record.
        Principal: string
        /// The subject's attributes, which a row policy's `Param`s name.
        Attributes: Map<string, Cell>
        /// What the subject may read. No grants: nothing lowers.
        Grants: Grant list
    }

/// A model column, as a policy verdict names it.
type ColumnRef =
    {
        /// The table's model name.
        Table: string
        /// The column's model name.
        Column: string
        /// The column's classification.
        Classification: string
    }

/// Where a pipeline uses a column: at one of its steps, counted from 1, or in its result.
[<RequireQualifiedAccess>]
type PolicySite =
    | Step of int
    | Result

/// The cost limits a read is held to: the tightest the model and every table read declare.
type CostLimits =
    {
        /// The row ceiling.
        MaxRows: int
        /// The join cap; `None` when no limit declares one, and then no join is admitted.
        MaxJoins: int option
        /// The grouping-column cap; `None` when no limit declares one, and then no grouping key is admitted.
        MaxGroupKeys: int option
        /// The group floor; `None` when no limit declares one, and then no aggregate over an
        /// aggregate-only column is admitted.
        MinGroupSize: int option
        /// The statement timeout, in seconds.
        TimeoutSeconds: int
    }

/// Why a pipeline is not lowered for a subject — closed: each case is one rule.
[<RequireQualifiedAccess>]
type PolicyRefusal =
    /// The subject holds no grants; nothing lowers.
    | NoGrants of principal: string
    /// The pipeline asks for something the model does not hold.
    | Pipeline of PipelineRefusal
    /// A column is read whose classification the subject holds no grant over. `granted` names the
    /// classifications it does hold.
    | ColumnNotGranted of site: PolicySite * column: ColumnRef * granted: string list
    /// A column the subject may read only in aggregate is used row by row; `usage` names how.
    | AggregateOnly of site: PolicySite * column: ColumnRef * usage: string
    /// An aggregate over an aggregate-only column, and no limit declares a group floor.
    | FloorUndeclared of step: int * column: ColumnRef
    /// A row policy names a subject attribute the subject does not carry. `held` names those it does.
    | AttributeMissing of table: string * policy: string * attribute: string * held: string list
    /// A table with a row policy is brought in by a step the policy cannot be applied after.
    | PolicyUnwrappable of step: int * table: string * policy: string * reason: string
    /// No limit on the model or on any table read declares a row ceiling.
    | RowCeilingUndeclared of tables: string list
    /// No limit on the model or on any table read declares a statement timeout.
    | TimeoutUndeclared of tables: string list
    /// The pipeline does not end in a `Limit` (only steps that cannot add rows may follow it).
    | LimitMissing of ceiling: int
    /// The bounding `Limit`'s row count is bound at run time; the ceiling cannot be checked.
    | LimitUnbound of step: int * param: string
    /// The bounding `Limit` asks for more rows than the ceiling.
    | LimitAboveCeiling of step: int * requested: int * ceiling: int
    /// More joins than the cap; a cap of `None` admits none.
    | TooManyJoins of joins: int * cap: int option
    /// A grouping step names more keys than the cap; a cap of `None` admits none.
    | TooManyGroupKeys of step: int * keys: int * cap: int option

/// What the policy did to admit a pipeline — returned WITH the verdict, so a connection record can
/// carry it.
[<RequireQualifiedAccess>]
type PolicyRule =
    /// Every column the pipeline reads, with the grant level that admitted it.
    | ColumnsGranted of columns: (ColumnRef * GrantLevel) list
    /// A row policy, its subject attributes substituted, applied as the `Filter` at step `at` of the
    /// admitted pipeline (counted from 1): first, for the source; right after the join, for a joined table.
    | RowPolicyApplied of table: string * policy: string * at: int * predicate: ColExpr
    /// The grouping at written step `step` gained a count per aggregate-only column, and a `Filter`
    /// keeping groups of at least `floor`.
    | GroupFloorApplied of step: int * floor: int * columns: ColumnRef list
    /// The cost limits the pipeline was held to, and what it used: the rows its bounding `Limit`
    /// asks for, its joins and its widest grouping.
    | CostAdmitted of rows: int * joins: int * groupKeys: int * limits: CostLimits

/// An admitted pipeline: the only value the provider lowers. Constructed by `Policy.admit` alone.
type Admitted =
    private
        { model: ModelNode
          source: DataSource
          written: Transform list
          pipeline: Transform list
          applied: PolicyRule list
          limits: CostLimits
          principal: string }

    /// The model the pipeline was checked against.
    member a.Model = a.model
    /// The pipeline's source.
    member a.Source = a.source
    /// The pipeline as the request wrote it.
    member a.Written = a.written
    /// The pipeline to lower: the written one, rewritten by every rule that rewrites.
    member a.Pipeline = a.pipeline
    /// Every rule applied, in the order applied.
    member a.Applied = a.applied
    /// The cost limits it was held to.
    member a.Limits = a.limits
    /// The statement timeout, in seconds, every statement lowered from it carries.
    member a.TimeoutSeconds = a.limits.TimeoutSeconds
    /// The principal it was admitted for.
    member a.Principal = a.principal

/// Policy before lowering: column grants (`validator:Fuaran.DB.Policy.ColumnGrants`), cost limits
/// (`validator:Fuaran.DB.Policy.CostLimits`), row policy (`domain:Fuaran.DB.Policy.RowRewrite`) and
/// minimum group size (`domain:Fuaran.DB.Policy.MinimumGroupSize`). Pure; evaluates nothing.
[<RequireQualifiedAccess>]
module Policy =

    // ---- the vocabulary a reader matches on ----

    /// The rule code of a refusal; stable, and what a host matches on.
    let code (r: PolicyRefusal) : string =
        match r with
        | PolicyRefusal.NoGrants _ -> "DB-GRANT-NONE"
        | PolicyRefusal.Pipeline _ -> "DB-PIPELINE"
        | PolicyRefusal.ColumnNotGranted _ -> "DB-GRANT-COLUMN"
        | PolicyRefusal.AggregateOnly _ -> "DB-GRANT-AGGREGATE-ONLY"
        | PolicyRefusal.FloorUndeclared _ -> "DB-GROUP-FLOOR-UNDECLARED"
        | PolicyRefusal.AttributeMissing _ -> "DB-ROW-POLICY-ATTRIBUTE"
        | PolicyRefusal.PolicyUnwrappable _ -> "DB-ROW-POLICY-UNWRAPPABLE"
        | PolicyRefusal.RowCeilingUndeclared _ -> "DB-COST-CEILING-UNDECLARED"
        | PolicyRefusal.TimeoutUndeclared _ -> "DB-COST-TIMEOUT-UNDECLARED"
        | PolicyRefusal.LimitMissing _ -> "DB-COST-LIMIT-MISSING"
        | PolicyRefusal.LimitUnbound _ -> "DB-COST-LIMIT-UNBOUND"
        | PolicyRefusal.LimitAboveCeiling _ -> "DB-COST-LIMIT-CEILING"
        | PolicyRefusal.TooManyJoins _ -> "DB-COST-JOINS"
        | PolicyRefusal.TooManyGroupKeys _ -> "DB-COST-GROUP-KEYS"

    /// Every refusal code, in declaration order.
    let codes =
        [ "DB-GRANT-NONE"
          "DB-PIPELINE"
          "DB-GRANT-COLUMN"
          "DB-GRANT-AGGREGATE-ONLY"
          "DB-GROUP-FLOOR-UNDECLARED"
          "DB-ROW-POLICY-ATTRIBUTE"
          "DB-ROW-POLICY-UNWRAPPABLE"
          "DB-COST-CEILING-UNDECLARED"
          "DB-COST-TIMEOUT-UNDECLARED"
          "DB-COST-LIMIT-MISSING"
          "DB-COST-LIMIT-UNBOUND"
          "DB-COST-LIMIT-CEILING"
          "DB-COST-JOINS"
          "DB-COST-GROUP-KEYS" ]

    /// The kind of an applied rule: what a sample must reach. A row policy is told apart by where
    /// it is applied, because the two places are two rewrites.
    let ruleTag (r: PolicyRule) : string =
        match r with
        | PolicyRule.ColumnsGranted _ -> "columns granted"
        | PolicyRule.RowPolicyApplied _ -> "row policy"
        | PolicyRule.GroupFloorApplied _ -> "group floor"
        | PolicyRule.CostAdmitted _ -> "cost admitted"

    /// Every applied-rule kind.
    let ruleTags = [ "columns granted"; "row policy"; "group floor"; "cost admitted" ]

    let private site (s: PolicySite) =
        match s with
        | PolicySite.Step n -> sprintf "step %d" n
        | PolicySite.Result -> "the result"

    let private col (c: ColumnRef) =
        sprintf "column '%s.%s' (classified '%s')" c.Table c.Column c.Classification

    let private listed (xs: string list) =
        if List.isEmpty xs then "none" else String.concat ", " xs

    let private cap (c: int option) =
        match c with
        | Some n -> sprintf "the cap is %d" n
        | None -> "no limit declares a cap, so none is admitted"

    /// The subject attribute a refusal read: the grants, a named attribute, or nothing of the
    /// subject's at all (a model declaration decided it).
    let attributeRead (r: PolicyRefusal) : string =
        match r with
        | PolicyRefusal.NoGrants _
        | PolicyRefusal.ColumnNotGranted _
        | PolicyRefusal.AggregateOnly _
        | PolicyRefusal.FloorUndeclared _ -> "the subject's grants"
        | PolicyRefusal.AttributeMissing(_, _, a, _) -> sprintf "the subject attribute '%s'" a
        | PolicyRefusal.Pipeline _
        | PolicyRefusal.PolicyUnwrappable _
        | PolicyRefusal.RowCeilingUndeclared _
        | PolicyRefusal.TimeoutUndeclared _
        | PolicyRefusal.LimitMissing _
        | PolicyRefusal.LimitUnbound _
        | PolicyRefusal.LimitAboveCeiling _
        | PolicyRefusal.TooManyJoins _
        | PolicyRefusal.TooManyGroupKeys _ -> "no subject attribute: the model's declarations decide it"

    /// What would satisfy the rule.
    let remedy (r: PolicyRefusal) : string =
        match r with
        | PolicyRefusal.NoGrants _ -> "a grant over at least the classifications the pipeline reads"
        | PolicyRefusal.Pipeline _ -> "a pipeline over the tables, columns and relations the model declares"
        | PolicyRefusal.ColumnNotGranted(_, c, _) ->
            sprintf "a grant over '%s', or a pipeline that does not read the column" c.Classification
        | PolicyRefusal.AggregateOnly(_, c, _) ->
            sprintf "a row-level grant over '%s', or reading the column only as an aggregate's input" c.Classification
        | PolicyRefusal.FloorUndeclared _ -> "a limit declaring minGroupSize, or a row-level grant"
        | PolicyRefusal.AttributeMissing(_, _, a, _) -> sprintf "a subject carrying the attribute '%s'" a
        | PolicyRefusal.PolicyUnwrappable _ ->
            "an inner join to the table, along a declared relation, or a pipeline that does not bring it in"
        | PolicyRefusal.RowCeilingUndeclared _ -> "a limit on the model or on a table read"
        | PolicyRefusal.TimeoutUndeclared _ -> "a limit declaring timeoutSeconds on the model or on a table read"
        | PolicyRefusal.LimitMissing c ->
            sprintf "a final Limit of at most %d rows, followed only by steps that cannot add rows" c
        | PolicyRefusal.LimitUnbound(_, p) -> sprintf "the Limit's count substituted for param '%s' first" p
        | PolicyRefusal.LimitAboveCeiling(_, _, c) -> sprintf "a Limit of at most %d rows" c
        | PolicyRefusal.TooManyJoins _ -> "fewer joins, or a limit declaring a higher maxJoins"
        | PolicyRefusal.TooManyGroupKeys _ -> "fewer grouping columns, or a limit declaring a higher maxGroupKeys"

    /// What the rule found, as a sentence.
    let private finding (r: PolicyRefusal) : string =
        match r with
        | PolicyRefusal.NoGrants p -> sprintf "subject '%s' holds no grants, and a subject with none reads nothing" p
        | PolicyRefusal.Pipeline p -> PipelineCheck.describe p
        | PolicyRefusal.ColumnNotGranted(s, c, granted) ->
            sprintf "%s reads %s; the subject is granted: %s" (site s) (col c) (listed granted)
        | PolicyRefusal.AggregateOnly(s, c, usage) ->
            sprintf "%s uses %s as %s; the subject may read it only in aggregate" (site s) (col c) usage
        | PolicyRefusal.FloorUndeclared(s, c) ->
            sprintf "step %d aggregates %s, readable only in aggregate, and no limit declares a group floor" s (col c)
        | PolicyRefusal.AttributeMissing(t, p, a, held) ->
            sprintf
                "row policy '%s' on table '%s' reads subject attribute '%s'; the subject carries: %s"
                p
                t
                a
                (listed held)
        | PolicyRefusal.PolicyUnwrappable(s, t, p, why) ->
            sprintf "step %d brings in table '%s', whose row policy '%s' cannot be applied there: %s" s t p why
        | PolicyRefusal.RowCeilingUndeclared ts ->
            sprintf "no limit on the model or on the tables read (%s) declares a row ceiling" (listed ts)
        | PolicyRefusal.TimeoutUndeclared ts ->
            sprintf "no limit on the model or on the tables read (%s) declares a statement timeout" (listed ts)
        | PolicyRefusal.LimitMissing c ->
            sprintf "the pipeline's rows are not bounded by a final Limit; the ceiling is %d" c
        | PolicyRefusal.LimitUnbound(s, p) -> sprintf "step %d limits rows by param '%s', bound at run time" s p
        | PolicyRefusal.LimitAboveCeiling(s, n, c) -> sprintf "step %d asks for %d rows; the ceiling is %d" s n c
        | PolicyRefusal.TooManyJoins(n, c) -> sprintf "the pipeline makes %d join(s); %s" n (cap c)
        | PolicyRefusal.TooManyGroupKeys(s, n, c) -> sprintf "step %d groups by %d column(s); %s" s n (cap c)

    /// A refusal as one sentence: the rule, what it found, the subject attribute it read, and what
    /// would satisfy it.
    let describe (r: PolicyRefusal) : string =
        sprintf "%s: %s (read %s); satisfied by %s" (code r) (finding r) (attributeRead r) (remedy r)

    // ---- column lineage: which model columns a name at a step carries ----

    /// The model columns a name carries: as row values (`Raw`), and folded into an aggregate (`Agg`).
    type private Lin =
        { Raw: Set<ColumnRef>
          Agg: Set<ColumnRef> }

    let private noLin = { Raw = Set.empty; Agg = Set.empty }

    let private join (a: Lin) (b: Lin) =
        { Raw = a.Raw + b.Raw
          Agg = a.Agg + b.Agg }

    /// The names flowing at a step, each with its lineage. `Rest` is the lineage of names a walk
    /// cannot list (after a pivot).
    type private Flow =
        { Cols: (string * Lin) list
          Rest: Lin option }

    let private linOf (flow: Flow) (name: string) : Lin =
        match flow.Cols |> List.tryFind (fun (n, _) -> n = name) with
        | Some(_, l) -> l
        | None -> defaultArg flow.Rest noLin

    let private linOfAll (flow: Flow) (names: string list) : Lin =
        names |> List.map (linOf flow) |> List.fold join noLin

    let private everything (flow: Flow) : Lin list =
        (flow.Cols |> List.map snd) @ Option.toList flow.Rest

    let private upsert (name: string) (lin: Lin) (cols: (string * Lin) list) =
        if cols |> List.exists (fun (n, _) -> n = name) then
            cols |> List.map (fun (n, l) -> if n = name then n, lin else n, l)
        else
            cols @ [ name, lin ]

    /// The lineage of the columns a projection withholds from `table`: one column no grant can name,
    /// classified `mark`, which the projection's own grants never hold (Phase 1907).
    let private withheldLin (mark: string) (table: string) : Lin =
        { Raw =
            Set.singleton
                { Table = table
                  Column = ""
                  Classification = mark }
          Agg = Set.empty }

    /// A table's columns as a read of it starts. Under a projection (`mark`), the table may hold columns
    /// the projection withholds, so a read of it carries them as the walk's `Rest`: a use of every
    /// column of the rows meets them, and a projection to named columns drops them.
    let private tableFlow (mark: string option) (node: ModelNode) (spec: TableSpec) : Flow =
        { Cols =
            Model.columns node
            |> List.map (fun (_, c) ->
                c.Name,
                { Raw =
                    Set.singleton
                        { Table = spec.Name
                          Column = c.Name
                          Classification = c.Classification }
                  Agg = Set.empty })
          Rest = mark |> Option.map (fun m -> withheldLin m spec.Name) }

    let private operandFlow (mark: string option) (model: ModelNode) (source: DataSource) : Flow =
        match source with
        | Ref name ->
            match Model.tryTable name model with
            | Some(node, spec) -> tableFlow mark node spec
            | None -> { Cols = []; Rest = None }
        | Embedded _ -> { Cols = []; Rest = None }

    /// A right column's name after a join: the evaluator suffixes one that collides with a left name.
    let private rightName (leftNames: string list) (c: string) =
        if List.contains c leftNames then c + "_right" else c

    let private joinRest (a: Lin option) (b: Lin option) : Lin option =
        match a, b with
        | Some x, Some y -> Some(join x y)
        | Some _, None -> a
        | None, _ -> b

    /// The lineage flowing out of one step.
    let private advance (mark: string option) (model: ModelNode) (flow: Flow) (step: Transform) : Flow =
        match step with
        | Filter _
        | Sort _
        | Limit _
        | Distinct
        | Intersect _
        | Except _ -> flow
        | Union source ->
            let other = operandFlow mark model source

            { flow with
                Cols = flow.Cols |> List.map (fun (n, l) -> n, join l (linOf other n)) }
        | Project pairs ->
            { Cols = pairs |> List.map (fun (src, out) -> out, linOf flow src)
              Rest = None }
        | Derive(name, e) ->
            { flow with
                Cols = upsert name (linOfAll flow (Reads.ofExpr e)) flow.Cols }
        | GroupBy(keys, aggs) ->
            { Cols =
                (keys |> List.map (fun k -> k, linOf flow k))
                @ (aggs
                   |> List.map (fun a ->
                       let l = linOf flow a.Of
                       a.Name, { Raw = Set.empty; Agg = l.Raw + l.Agg }))
              Rest = None }
        | Join(source, _, how) ->
            match how with
            | Semi
            | Anti -> flow
            | Inner
            | Left
            | Right
            | Outer ->
                let leftNames = flow.Cols |> List.map fst
                let other = operandFlow mark model source

                // Under a projection the withheld columns take part in the evaluator's renaming too,
                // so a right column's name may, over the full table, name a withheld column instead:
                // any name when the left rows carry withheld columns, and a `_right` name when the
                // right table does. Such a name carries the withheld lineage, so reading it is refused.
                let taint (name: string) (l: Lin) =
                    match mark with
                    | None -> l
                    | Some _ ->
                        [ flow.Rest; (if name.EndsWith "_right" then other.Rest else None) ]
                        |> List.choose id
                        |> List.fold join l

                { Cols =
                    flow.Cols
                    @ (other.Cols
                       |> List.map (fun (n, l) ->
                           let named = rightName leftNames n
                           named, taint named l))
                  Rest = joinRest flow.Rest other.Rest }
        | Window spec ->
            { flow with
                Cols =
                    upsert
                        spec.As
                        (linOfAll flow (spec.PartitionBy @ (spec.OrderBy |> List.map fst) @ [ spec.Of ]))
                        flow.Cols }
        | Pivot spec ->
            { Cols = spec.Index |> List.map (fun k -> k, linOf flow k)
              Rest = Some(linOfAll flow [ spec.On; spec.Values ]) }
        | Unpivot(idVars, valueVars) ->
            { Cols =
                (idVars |> List.map (fun k -> k, linOf flow k))
                @ [ "variable", noLin; "value", linOfAll flow valueVars ]
              Rest = None }

    // ---- how a step uses what it reads ----

    /// How a read is used: carried into another column, folded into an aggregate, or used row by
    /// row (named, for the refusal).
    type private Use =
        | Carry
        | AggInput
        | Expose of string

    let private uses (mark: string option) (model: ModelNode) (flow: Flow) (step: Transform) : (Lin * Use) list =
        let names (u: Use) (ns: string list) =
            ns |> List.map (fun n -> linOf flow n, u)

        match step with
        | Filter p -> names (Expose "a filter predicate") (Reads.ofExpr p)
        | Project pairs -> names Carry (pairs |> List.map fst)
        | Derive(_, e) -> names Carry (Reads.ofExpr e)
        | GroupBy(keys, aggs) ->
            names (Expose "a grouping key") keys
            @ (aggs
               |> List.map (fun a ->
                   linOf flow a.Of,
                   match a.Fn with
                   | First
                   | Last -> Expose "the first or last value of a group, one row's value"
                   | Sum
                   | Mean
                   | Min
                   | Max
                   | Count
                   | Median
                   | StdDev
                   | CountDistinct -> AggInput))
        | Join(source, on, _) ->
            let other = operandFlow mark model source

            names (Expose "a join key") (on |> List.map fst)
            @ (on |> List.map (fun (_, r) -> linOf other r, Expose "a join key"))
        | Window spec ->
            names (Expose "a window's input") (spec.PartitionBy @ (spec.OrderBy |> List.map fst) @ [ spec.Of ])
        | Pivot spec -> names (Expose "a pivot's input") (spec.Index @ [ spec.On; spec.Values ])
        | Unpivot(idVars, valueVars) -> names Carry (idVars @ valueVars)
        | Sort keys ->
            keys
            |> List.choose (fun (slot, _) ->
                match slot with
                | Slot.Lit c -> Some(linOf flow c, Expose "a sort key")
                | Slot.Param _ -> None)
        | Distinct -> everything flow |> List.map (fun l -> l, Expose "part of a row compared whole")
        | Limit _ -> []
        | Union source
        | Intersect source
        | Except source ->
            (everything flow @ everything (operandFlow mark model source))
            |> List.map (fun l -> l, Expose "part of a row compared whole")

    /// How far `grants` reach a classification: row by row when any grant over it says so, in aggregate
    /// when one does and none says more, and not at all when none names it.
    let level (grants: Grant list) (classification: string) : GrantLevel option =
        let levels =
            grants
            |> List.filter (fun g -> g.Classification = classification)
            |> List.map _.Level

        if List.contains GrantLevel.RowLevel levels then
            Some GrantLevel.RowLevel
        elif List.isEmpty levels then
            None
        else
            Some GrantLevel.Aggregate

    /// Check one use against the grants; `Ok` carries every column it admitted, with its level.
    let private checkUse
        (subject: Subject)
        (at: PolicySite)
        (lin: Lin, how: Use)
        : Result<(ColumnRef * GrantLevel) list, PolicyRefusal> =
        let granted = subject.Grants |> List.map _.Classification |> List.distinct

        Set.toList (lin.Raw + lin.Agg)
        |> List.fold
            (fun acc c ->
                acc
                |> Result.bind (fun seen ->
                    match level subject.Grants c.Classification with
                    | None -> Error(PolicyRefusal.ColumnNotGranted(at, c, granted))
                    | Some GrantLevel.Aggregate when lin.Raw.Contains c ->
                        match how with
                        | Expose usage -> Error(PolicyRefusal.AggregateOnly(at, c, usage))
                        | Carry
                        | AggInput -> Ok(seen @ [ c, GrantLevel.Aggregate ])
                    | Some level -> Ok(seen @ [ c, level ])))
            (Ok [])

    let private aggregateOnly (subject: Subject) (lin: Lin) : ColumnRef list =
        lin.Raw
        |> Set.toList
        |> List.filter (fun c -> level subject.Grants c.Classification = Some GrantLevel.Aggregate)

    /// The written pipeline walked against the grants: every column it admitted, and, per grouping
    /// step that aggregates an aggregate-only column, the `Of` columns that need a count.
    let private grants
        (mark: string option)
        (model: ModelNode)
        (subject: Subject)
        (start: Flow)
        (pipeline: Transform list)
        : Result<
              (ColumnRef * GrantLevel) list * Map<int, string list * Agg list * (string * ColumnRef list) list>,
              PolicyRefusal
           >
        =
        let folded =
            pipeline
            |> List.indexed
            |> List.fold
                (fun acc (i, step) ->
                    acc
                    |> Result.bind (fun (flow, admitted, floors) ->
                        let at = PolicySite.Step(i + 1)

                        uses mark model flow step
                        |> List.fold
                            (fun inner u ->
                                inner
                                |> Result.bind (fun seen -> checkUse subject at u |> Result.map ((@) seen)))
                            (Ok admitted)
                        |> Result.map (fun admitted ->
                            let floors =
                                match step with
                                | GroupBy(keys, aggs) ->
                                    let needs =
                                        aggs
                                        |> List.map _.Of
                                        |> List.distinct
                                        |> List.choose (fun o ->
                                            match aggregateOnly subject (linOf flow o) with
                                            | [] -> None
                                            | cs -> Some(o, cs))

                                    if List.isEmpty needs then
                                        floors
                                    else
                                        Map.add (i + 1) (keys, aggs, needs) floors
                                | Filter _
                                | Project _
                                | Derive _
                                | Join _
                                | Window _
                                | Pivot _
                                | Unpivot _
                                | Sort _
                                | Distinct
                                | Limit _
                                | Union _
                                | Intersect _
                                | Except _ -> floors

                            advance mark model flow step, admitted, floors)))
                (Ok(start, [], Map.empty))

        folded
        |> Result.bind (fun (flow, admitted, floors) ->
            everything flow
            |> List.fold
                (fun acc l ->
                    acc
                    |> Result.bind (fun seen ->
                        checkUse subject PolicySite.Result (l, Expose "a result column")
                        |> Result.map ((@) seen)))
                (Ok admitted)
            |> Result.map (fun admitted -> List.distinct admitted, floors))

    // ---- cost limits ----

    let private limitsOf (node: ModelNode) : LimitSpec list =
        node.Children
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.Limit l -> Some l
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _ -> None)

    let private tightest (values: int list) : int option =
        if List.isEmpty values then None else Some(List.min values)

    /// Whether a step can return more rows than reach it. Only these may not follow the bounding `Limit`.
    let private grows (step: Transform) : bool =
        match step with
        | Join(_, _, how) ->
            match how with
            | Inner
            | Left
            | Right
            | Outer -> true
            | Semi
            | Anti -> false
        | Union _
        | Unpivot _ -> true
        | Filter _
        | Project _
        | Derive _
        | GroupBy _
        | Window _
        | Pivot _
        | Sort _
        | Distinct
        | Limit _
        | Intersect _
        | Except _ -> false

    /// The bounding `Limit`: the last one, with only non-growing steps after it, and its 1-based step.
    let private bounding (pipeline: Transform list) : (int * Slot<int>) option =
        let rec back (steps: (int * Transform) list) =
            match steps with
            | [] -> None
            | (i, Limit(n, _)) :: _ -> Some(i + 1, n)
            | (_, step) :: rest -> if grows step then None else back rest

        back (pipeline |> List.indexed |> List.rev)

    let private groupKeys (step: Transform) : int =
        match step with
        | GroupBy(keys, _) -> List.length keys
        | Pivot spec -> List.length spec.Index
        | Filter _
        | Project _
        | Derive _
        | Join _
        | Window _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _
        | Union _
        | Intersect _
        | Except _ -> 0

    let private isJoin (step: Transform) : bool =
        match step with
        | Join _ -> true
        | Filter _
        | Project _
        | Derive _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _
        | Union _
        | Intersect _
        | Except _ -> false

    let private cost
        (model: ModelNode)
        (tables: (ModelNode * TableSpec) list)
        (pipeline: Transform list)
        : Result<int * int * int * CostLimits, PolicyRefusal> * int option =
        let declared = limitsOf model @ (tables |> List.collect (fun (n, _) -> limitsOf n))

        let names = tables |> List.map (fun (_, t) -> t.Name)
        let floor = tightest (declared |> List.choose _.MinGroupSize)

        let verdict =
            match tightest (declared |> List.map _.MaxRows), tightest (declared |> List.choose _.TimeoutSeconds) with
            | None, _ -> Error(PolicyRefusal.RowCeilingUndeclared names)
            | _, None -> Error(PolicyRefusal.TimeoutUndeclared names)
            | Some ceiling, Some timeout ->
                let limits =
                    { MaxRows = ceiling
                      MaxJoins = tightest (declared |> List.choose _.MaxJoins)
                      MaxGroupKeys = tightest (declared |> List.choose _.MaxGroupKeys)
                      MinGroupSize = floor
                      TimeoutSeconds = timeout }

                let within (n: int) (c: int option) =
                    n = 0
                    || (match c with
                        | Some c -> n <= c
                        | None -> false)

                let joins = pipeline |> List.filter isJoin |> List.length

                let widest =
                    pipeline
                    |> List.indexed
                    |> List.tryFind (fun (_, s) -> not (within (groupKeys s) limits.MaxGroupKeys))

                if not (within joins limits.MaxJoins) then
                    Error(PolicyRefusal.TooManyJoins(joins, limits.MaxJoins))
                else
                    match widest with
                    | Some(i, s) -> Error(PolicyRefusal.TooManyGroupKeys(i + 1, groupKeys s, limits.MaxGroupKeys))
                    | None ->
                        match bounding pipeline with
                        | None -> Error(PolicyRefusal.LimitMissing ceiling)
                        | Some(i, Slot.Param p) -> Error(PolicyRefusal.LimitUnbound(i, p))
                        | Some(i, Slot.Lit n) when n > ceiling -> Error(PolicyRefusal.LimitAboveCeiling(i, n, ceiling))
                        | Some(_, Slot.Lit n) ->
                            let keys = pipeline |> List.map groupKeys |> List.fold max 0
                            Ok(n, joins, keys, limits)

        verdict, floor

    // ---- row policy ----

    /// An expression with its column names mapped — how a joined table's policy follows the
    /// evaluator's renaming of a colliding column.
    let rec private renameCols (f: string -> string) (e: ColExpr) : ColExpr =
        let go = renameCols f

        match e with
        | Col c -> Col(f c)
        | Lit _
        | Param _
        | Now _ -> e
        | Binary(op, a, b) -> Binary(op, go a, go b)
        | Not x -> Not(go x)
        | IsNull x -> IsNull(go x)
        | Cast(ty, x) -> Cast(ty, go x)
        | Convert(x, u) -> Convert(go x, u)
        | Rounded(x, r) -> Rounded(go x, r)
        | InParam(x, n) -> InParam(go x, n)
        | Coalesce xs -> Coalesce(List.map go xs)
        | ApplyFn(fn, xs) -> ApplyFn(fn, List.map go xs)
        | Case(cases, els) -> Case(cases |> List.map (fun (w, t) -> go w, go t), go els)
        | InList(x, items) -> InList(go x, List.map go items)
        | Quotient(a, b, r) -> Quotient(go a, go b, r)

    let private policiesOf (node: ModelNode) : RowPolicySpec list =
        node.Children
        |> List.choose (fun n ->
            match n.Kind with
            | NodeKind.RowPolicy p -> Some p
            | NodeKind.Model _
            | NodeKind.Source _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.Limit _ -> None)

    /// A table's row policies with the subject's attributes substituted, or the attribute missing.
    let private bound
        (subject: Subject)
        (table: TableSpec)
        (node: ModelNode)
        : Result<(string * ColExpr) list, PolicyRefusal> =
        policiesOf node
        |> List.fold
            (fun acc p ->
                acc
                |> Result.bind (fun done' ->
                    let pred = ColExpr.substitute subject.Attributes p.Predicate

                    match ColExpr.paramsOf pred with
                    | a :: _ ->
                        Error(
                            PolicyRefusal.AttributeMissing(
                                table.Name,
                                p.Name,
                                a,
                                subject.Attributes |> Map.toList |> List.map fst
                            )
                        )
                    | [] -> Ok(done' @ [ p.Name, pred ])))
            (Ok [])

    /// The filters to apply right after written step `step`, which brings in `source`: the table's
    /// policies over the post-join names, for an inner join; a refusal for any other step.
    let private afterStep
        (model: ModelNode)
        (subject: Subject)
        (flow: Flow)
        (index: int, step: Transform)
        : Result<(string * string * ColExpr) list, PolicyRefusal> =
        match Reads.operandOf step with
        | None -> Ok []
        | Some(Embedded _) -> Ok []
        | Some(Ref name) ->
            match Model.tryTable name model with
            | None -> Ok []
            | Some(node, spec) ->
                bound subject spec node
                |> Result.bind (fun policies ->
                    match policies with
                    | [] -> Ok []
                    | (first, _) :: _ ->
                        let refuse why =
                            Error(PolicyRefusal.PolicyUnwrappable(index, spec.Name, first, why))

                        match step with
                        | Join(_, _, Inner) ->
                            let leftNames = flow.Cols |> List.map fst

                            // A predicate column whose suffixed name the left side already holds
                            // would be named twice after the join; the policy could not say which.
                            let collides =
                                policies
                                |> List.collect (fun (_, pred) -> Reads.ofExpr pred)
                                |> List.exists (fun c ->
                                    List.contains c leftNames && List.contains (c + "_right") leftNames)

                            if collides then
                                refuse "a column's joined name is already taken, so the policy cannot name it"
                            else
                                Ok(
                                    policies
                                    |> List.map (fun (p, pred) -> spec.Name, p, renameCols (rightName leftNames) pred)
                                )
                        | Join(_, _, how) ->
                            refuse (
                                sprintf
                                    "a %A join keeps or tests rows of the table that a filter after it cannot remove"
                                    how
                            )
                        | Union _
                        | Intersect _
                        | Except _ ->
                            refuse "a set operation compares or adds the table's rows whole, before any filter"
                        | Filter _
                        | Project _
                        | Derive _
                        | GroupBy _
                        | Window _
                        | Pivot _
                        | Unpivot _
                        | Sort _
                        | Distinct
                        | Limit _ -> Ok [])

    // ---- the group floor ----

    let private fresh (taken: string list) (stem: string) : string =
        let rec go (name: string) =
            if List.contains name taken then go (name + "_") else name

        go stem

    /// A grouping rewritten to keep groups of at least `floor` contributors per aggregate-only column.
    let private floored (floor: int) (keys: string list) (aggs: Agg list) (needs: (string * ColumnRef list) list) =
        let taken = keys @ (aggs |> List.map _.Name)

        let counts =
            needs
            |> List.mapi (fun j (o, _) ->
                { Name = fresh taken (sprintf "floor_count_%d" (j + 1))
                  Fn = Count
                  Of = o })

        let keep =
            counts
            |> List.map (fun c -> Binary(Ge, Col c.Name, Lit(Int floor)))
            |> List.reduce (fun a b -> Binary(And, a, b))

        [ GroupBy(keys, aggs @ counts)
          Filter keep
          Project(taken |> List.map (fun n -> n, n)) ]

    // ---- admission ----

    /// The column authorisation, the first half of admission: grants held, the pipeline against the
    /// model, then the column grants step by step and the result. `Ok` carries the source table, every
    /// column admitted with its level, and the grouping steps that need a floor.
    let private authorise
        (mark: string option)
        (model: ModelNode)
        (subject: Subject)
        (source: DataSource)
        (pipeline: Transform list)
        =
        if List.isEmpty subject.Grants then
            Error(PolicyRefusal.NoGrants subject.Principal)
        else
            PipelineCheck.check model source pipeline
            |> Result.mapError PolicyRefusal.Pipeline
            |> Result.bind (fun _ ->
                match source with
                | Embedded _ -> Error(PolicyRefusal.Pipeline(PipelineRefusal.EmbeddedOperand 0))
                | Ref name ->
                    match Model.tryTable name model with
                    | None ->
                        Error(
                            PolicyRefusal.Pipeline(
                                PipelineRefusal.UnknownTable(
                                    0,
                                    name,
                                    Model.tables model |> List.map (fun (_, t) -> t.Name)
                                )
                            )
                        )
                    | Some(node, spec) -> Ok(node, spec))
            |> Result.bind (fun (sourceNode, sourceSpec) ->
                grants mark model subject (tableFlow mark sourceNode sourceSpec) pipeline
                |> Result.map (fun (admitted, floors) -> (sourceNode, sourceSpec), admitted, floors))

    /// The column authorisation alone (`validator:Fuaran.DB.Policy.ColumnGrants`): whether `subject`'s
    /// grants admit every column `pipeline` reads over `source`, and how far. `Ok` carries each column
    /// admitted with its level; `Error` the first refusal, in `admit`'s order. It applies no cost, group
    /// floor or row policy, so a pipeline it admits may still be refused by `admit`.
    let columns
        (model: ModelNode)
        (subject: Subject)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<(ColumnRef * GrantLevel) list, PolicyRefusal> =
        authorise None model subject source pipeline
        |> Result.map (fun (_, admitted, _) -> admitted)

    /// The column authorisation over a projection (Phase 1907): as `columns`, except that every table
    /// may hold columns the projection withholds. Their lineage carries one column classified `mark`,
    /// which the caller chooses so that no grant names it: a use that may read one is refused as
    /// `ColumnNotGranted` with that column, which the caller reports as withheld.
    let internal columnsWithheld
        (mark: string)
        (model: ModelNode)
        (subject: Subject)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<(ColumnRef * GrantLevel) list, PolicyRefusal> =
        authorise (Some mark) model subject source pipeline
        |> Result.map (fun (_, admitted, _) -> admitted)

    /// Decide what `subject` may read of `pipeline` over `source`, against `model`. `Ok` is the
    /// admitted pipeline with every rule applied; `Error` the first refusal, in this order: grants
    /// held, the pipeline against the model, the column grants step by step and then the result,
    /// the group floor, the row policies, the cost limits.
    let admit
        (model: ModelNode)
        (subject: Subject)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<Admitted, PolicyRefusal> =
        authorise None model subject source pipeline
        |> Result.bind (fun ((sourceNode, sourceSpec), admitted, floors) ->
            let operands =
                pipeline
                |> List.choose Reads.operandOf
                |> List.choose (fun s ->
                    match s with
                    | Ref n -> Model.tryTable n model
                    | Embedded _ -> None)

            let tables =
                ((sourceNode, sourceSpec) :: operands) |> List.distinctBy (fun (n, _) -> n.Id)

            let start = tableFlow None sourceNode sourceSpec
            let costVerdict, floor = cost model tables pipeline

            let first =
                floors
                |> Map.toList
                |> List.tryPick (fun (step, (_, _, needs)) ->
                    needs |> List.collect snd |> List.tryHead |> Option.map (fun c -> step, c))

            (match floor, first with
             | None, Some(step, c) -> Error(PolicyRefusal.FloorUndeclared(step, c))
             | None, None
             | Some _, _ -> Ok(admitted, floors))
            |> Result.bind (fun (admitted, floors) ->
                bound subject sourceSpec sourceNode
                |> Result.bind (fun heads ->
                    // the flow before each written step, for the post-join names
                    let flows =
                        pipeline
                        |> List.scan (advance None model) start
                        |> List.take (List.length pipeline)

                    List.zip flows (pipeline |> List.indexed |> List.map (fun (i, s) -> i + 1, s))
                    |> List.fold
                        (fun acc (flow, st) ->
                            acc
                            |> Result.bind (fun xs ->
                                afterStep model subject flow st |> Result.map (fun a -> xs @ [ a ])))
                        (Ok [])
                    |> Result.map (fun after -> admitted, floors, heads, after)))
            |> Result.bind (fun (admitted, floors, heads, after) ->
                costVerdict
                |> Result.map (fun (rows, joins, keys, limits) ->
                    let headSteps = heads |> List.map (fun (_, p) -> Filter p)

                    let headRules =
                        heads
                        |> List.mapi (fun i (p, pred) ->
                            PolicyRule.RowPolicyApplied(sourceSpec.Name, p, i + 1, pred))

                    let steps, rules =
                        List.zip (pipeline |> List.indexed) after
                        |> List.fold
                            (fun (steps: Transform list, rules: PolicyRule list) ((i, step), policies) ->
                                let written, floorRules =
                                    match Map.tryFind (i + 1) floors, limits.MinGroupSize with
                                    | Some(keys, aggs, needs), Some f ->
                                        floored f keys aggs needs,
                                        [ PolicyRule.GroupFloorApplied(
                                              i + 1,
                                              f,
                                              needs |> List.collect snd |> List.distinct
                                          ) ]
                                    | Some _, None
                                    | None, _ -> [ step ], []

                                let steps = steps @ written

                                let policyRules =
                                    policies
                                    |> List.mapi (fun j (t, p, pred) ->
                                        PolicyRule.RowPolicyApplied(t, p, List.length steps + j + 1, pred))

                                steps @ (policies |> List.map (fun (_, _, pred) -> Filter pred)),
                                rules @ floorRules @ policyRules)
                            (headSteps, [])

                    { model = model
                      source = source
                      written = pipeline
                      pipeline = steps
                      applied =
                        [ PolicyRule.ColumnsGranted admitted ]
                        @ headRules
                        @ rules
                        @ [ PolicyRule.CostAdmitted(rows, joins, keys, limits) ]
                      limits = limits
                      principal = subject.Principal })))
