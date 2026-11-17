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
//  grouping over an aggregate-only column gains a count and a `Filter` that
//  drops a group with too few contributors, never one with none (D21).
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
    /// A column of the classification may be read as `Aggregate` reads it, and may also key a grouping
    /// or a join, in the pipeline and in a grouped operand; its value never reaches the result, under
    /// any name or derivation, and neither does a row at its grain (DECISIONS.md D18). The answer of a
    /// `Min`, `Max` or `Median` over it is one of its values, so it is held as a key too.
    | Keyed
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
    /// A column the subject may read only as a key or in aggregate is used otherwise; `usage` names how.
    | KeyOnly of site: PolicySite * column: ColumnRef * usage: string
    /// A result column carries the value of a column the subject may read only as a key or in
    /// aggregate, under whatever name and through whatever derivation.
    | KeyInResult of resultColumn: string * column: ColumnRef
    /// The result is still one row per value of a column the subject may read only as a key or in
    /// aggregate: a grouping or join keyed on it is not followed by a grouping that is not.
    | KeyGrain of column: ColumnRef
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
    /// dropping a group in which some count is between 1 and one fewer than `floor` (DECISIONS.md D21).
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
          operands: Operand list
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
    /// The grouped operands its joins bring in (DECISIONS.md D16), each rewritten as the pipeline is:
    /// its table's row policies first, before its grouping, and a grouping over an aggregate-only
    /// column floored. Only the operands the pipeline joins are kept.
    member a.Operands = a.operands
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
        | PolicyRefusal.KeyOnly _ -> "DB-GRANT-KEY-ONLY"
        | PolicyRefusal.KeyInResult _ -> "DB-GRANT-KEY-IN-RESULT"
        | PolicyRefusal.KeyGrain _ -> "DB-GRANT-KEY-GRAIN"
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
          "DB-GRANT-KEY-ONLY"
          "DB-GRANT-KEY-IN-RESULT"
          "DB-GRANT-KEY-GRAIN"
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

    /// A grant level's wire tag, which every codec that carries a level writes.
    let levelTag (l: GrantLevel) : string =
        match l with
        | GrantLevel.Aggregate -> "aggregate"
        | GrantLevel.Keyed -> "keyed"
        | GrantLevel.RowLevel -> "row"

    /// Every level's wire tag, narrowest first.
    let levelTags = [ "aggregate"; "keyed"; "row" ]

    /// The level a wire tag names, or `None` for a tag this version does not know.
    let tryLevel (tag: string) : GrantLevel option =
        [ GrantLevel.Aggregate; GrantLevel.Keyed; GrantLevel.RowLevel ]
        |> List.tryFind (fun l -> levelTag l = tag)

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
        | PolicyRefusal.KeyOnly _
        | PolicyRefusal.KeyInResult _
        | PolicyRefusal.KeyGrain _
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
        | PolicyRefusal.KeyOnly(_, c, _) ->
            sprintf
                "a row-level grant over '%s', or using the column only as a grouping key, a join key or an aggregate's input"
                c.Classification
        | PolicyRefusal.KeyInResult(_, c) ->
            sprintf
                "a result without the column's value: project it away, or fold it into a count, before the result (or a row-level grant over '%s')"
                c.Classification
        | PolicyRefusal.KeyGrain c ->
            sprintf
                "a grouping not keyed on the column after the last grouping or join keyed on it (or a row-level grant over '%s')"
                c.Classification
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
        | PolicyRefusal.KeyOnly(s, c, usage) ->
            sprintf "%s uses %s as %s; the subject may read it only as a key or in aggregate" (site s) (col c) usage
        | PolicyRefusal.KeyInResult(name, c) ->
            sprintf
                "result column '%s' carries the value of %s; the subject may read it only as a key or in aggregate"
                name
                (col c)
        | PolicyRefusal.KeyGrain c ->
            sprintf
                "the result is still one row per value of %s: a grouping or join keyed on it is not followed by a grouping that is not"
                (col c)
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
    /// `Picked` is the part of `Agg` folded into an aggregate whose answer is one of the group's values
    /// (`Min`, `Max`, `Median`): under a keyed grant that answer is a key (D18), under any other it
    /// is an aggregate. `Tested` is the model columns whose presence, and nothing else of them, the
    /// value answers (D20): under a keyed grant that answer is not the key, under any other it is the
    /// column's row value, as it was. `Exact` is the model column a name carries unchanged, through
    /// renames, grouping keys and joins and never through a derivation: the only kind of name whose
    /// presence a presence test answers.
    type private Lin =
        { Raw: Set<ColumnRef>
          Agg: Set<ColumnRef>
          Picked: Set<ColumnRef>
          Tested: Set<ColumnRef>
          Exact: ColumnRef option }

    let private noLin =
        { Raw = Set.empty
          Agg = Set.empty
          Picked = Set.empty
          Tested = Set.empty
          Exact = None }

    let private rawLin (c: ColumnRef) = { noLin with Raw = Set.singleton c }

    let private join (a: Lin) (b: Lin) =
        { Raw = a.Raw + b.Raw
          Agg = a.Agg + b.Agg
          Picked = a.Picked + b.Picked
          Tested = a.Tested + b.Tested
          Exact = if a.Exact = b.Exact then a.Exact else None }

    /// What a name carries that a keyed grant holds as a key's value: its row values and its picks.
    let private asKey (l: Lin) = l.Raw + l.Picked

    /// The names flowing at a step, each with its lineage. `Rest` is the lineage of names a walk
    /// cannot list (after a pivot). `Keys` is the model columns the rows are grouped or joined on
    /// since the last grouping that is not keyed on them: the grain a keyed grant keeps out of the
    /// result (D18).
    type private Flow =
        { Cols: (string * Lin) list
          Rest: Lin option
          Keys: Set<ColumnRef> }

    let private noFlow =
        { Cols = []
          Rest = None
          Keys = Set.empty }

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
        rawLin
            { Table = table
              Column = ""
              Classification = mark }

    /// A table's columns as a read of it starts. Under a projection (`mark`), the table may hold columns
    /// the projection withholds, so a read of it carries them as the walk's `Rest`: a use of every
    /// column of the rows meets them, and a projection to named columns drops them.
    let private tableFlow (mark: string option) (node: ModelNode) (spec: TableSpec) : Flow =
        { Cols =
            Model.columns node
            |> List.map (fun (_, c) ->
                let col =
                    { Table = spec.Name
                      Column = c.Name
                      Classification = c.Classification }

                c.Name, { rawLin col with Exact = Some col })
          Rest = mark |> Option.map (fun m -> withheldLin m spec.Name)
          Keys = Set.empty }

    let private operandFlow
        (mark: string option)
        (model: ModelNode)
        (ops: Map<string, Flow>)
        (source: DataSource)
        : Flow =
        match source with
        | Ref name ->
            // A grouped operand flows out as its own pipeline left it (DECISIONS.md D16).
            match Map.tryFind name ops, Model.tryTable name model with
            | Some flow, _ -> flow
            | None, Some(node, spec) -> tableFlow mark node spec
            | None, None -> noFlow
        | Embedded _ -> noFlow

    /// A right column's name after a join: the evaluator suffixes one that collides with a left name.
    let private rightName (leftNames: string list) (c: string) =
        if List.contains c leftNames then c + "_right" else c

    let private joinRest (a: Lin option) (b: Lin option) : Lin option =
        match a, b with
        | Some x, Some y -> Some(join x y)
        | Some _, None -> a
        | None, _ -> b

    // ---- a presence test: whether a row has a key, never which (D20) ----

    /// The name a presence test asks about, when `e` is one: a null test of the name, or a comparison
    /// (`Eq` or `Ne`) of the name, or of its `Coalesce` with the empty text, against the empty text,
    /// either way round. Its negation is `Not` of one, which the walk below reaches. The set is closed:
    /// a comparison with any other literal, a parameter or a column, a pattern, an ordering, a length
    /// and a membership are not presence tests, and read the name as any expression does.
    let private presenceOf (e: ColExpr) : string option =
        let empty (x: ColExpr) =
            match x with
            | Lit(Str "") -> true
            | _ -> false

        let asked (x: ColExpr) =
            match x with
            | Col n
            | Coalesce [ Col n; Lit(Str "") ] -> Some n
            | _ -> None

        match e with
        | IsNull(Col n) -> Some n
        | Binary((Eq | Ne), a, b) when empty b -> asked a
        | Binary((Eq | Ne), a, b) when empty a -> asked b
        | _ -> None

    /// The lineage of each read of an expression, left to right as `Reads.ofExpr` lists them, except
    /// that inside a condition (`tests`: a filter's predicate, a `Case`'s conditions) a presence test of
    /// a name carrying one model column unchanged is one read answering that column's presence only.
    /// A name a derivation produced is not tested this way, even one whose lineage is a key: its
    /// nulls are the derivation's, and may say anything about the key it read.
    let rec private exprLins (flow: Flow) (tests: bool) (e: ColExpr) : Lin list =
        let within = exprLins flow tests

        let tested =
            if tests then
                presenceOf e |> Option.bind (fun n -> (linOf flow n).Exact)
            else
                None

        match tested with
        | Some c -> [ { noLin with Tested = Set.singleton c } ]
        | None ->
            match e with
            | Col c -> [ linOf flow c ]
            | Lit _
            | Param _
            | Now _
            | Arg _ -> []
            | Binary(_, a, b)
            | Quotient(a, b, _) -> within a @ within b
            | Not x
            | IsNull x
            | Cast(_, x)
            | Convert(x, _)
            | Rounded(x, _)
            | InParam(x, _)
            | Interpolate(x, _, _, _) -> within x
            | Coalesce xs
            | ApplyFn(_, xs)
            | Call(_, xs) -> xs |> List.collect within
            | Case(cases, els) ->
                (cases |> List.collect (fun (w, t) -> exprLins flow true w @ within t))
                @ within els
            | InList(x, items) -> within x @ (items |> List.collect within)

    /// The lineage flowing out of one step.
    let private advance
        (mark: string option)
        (model: ModelNode)
        (ops: Map<string, Flow>)
        (flow: Flow)
        (step: Transform)
        : Flow =
        match step with
        | Filter _
        | Sort _
        | Limit _
        | Distinct
        | Intersect _
        | Except _ -> flow
        | Union source ->
            let other = operandFlow mark model ops source

            { flow with
                Cols = flow.Cols |> List.map (fun (n, l) -> n, join l (linOf other n))
                Keys = flow.Keys + other.Keys }
        | Project pairs ->
            { flow with
                Cols = pairs |> List.map (fun (src, out) -> out, linOf flow src)
                Rest = None }
        | Derive(name, e) ->
            { flow with
                Cols = upsert name (exprLins flow false e |> List.fold join noLin) flow.Cols }
        | GroupBy(keys, aggs) ->
            { Cols =
                (keys |> List.map (fun k -> k, linOf flow k))
                @ (aggs
                   |> List.map (fun a ->
                       let l = linOf flow a.Of

                       let picked =
                           match a.Fn with
                           | Min
                           | Max
                           | Median -> asKey l
                           | Sum
                           | Mean
                           | Count
                           | StdDev
                           | CountDistinct
                           | First
                           | Last -> Set.empty

                       // A presence folded into an aggregate is an aggregate's input like any row value.
                       a.Name,
                       { noLin with
                           Agg = l.Raw + l.Agg + l.Tested
                           Picked = picked }))
              Rest = None
              // A grouping sets the grain: the rows are now one per value of its keys.
              Keys = keys |> List.map (linOf flow >> asKey) |> Set.unionMany }
        | Join(source, on, how) ->
            let other = operandFlow mark model ops source

            let keyed =
                (on |> List.map (fst >> linOf flow >> asKey))
                @ (on |> List.map (snd >> linOf other >> asKey))
                |> Set.unionMany

            match how with
            | Semi
            | Anti -> { flow with Keys = flow.Keys + keyed }
            | Inner
            | Left
            | Right
            | Outer ->
                let leftNames = flow.Cols |> List.map fst

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
                  Rest = joinRest flow.Rest other.Rest
                  Keys = flow.Keys + other.Keys + keyed }
        | Window spec ->
            { flow with
                Cols =
                    upsert
                        spec.As
                        (linOfAll flow (spec.PartitionBy @ (spec.OrderBy |> List.map fst) @ [ spec.Of ]))
                        flow.Cols }
        | Pivot spec ->
            { Cols = spec.Index |> List.map (fun k -> k, linOf flow k)
              Rest = Some(linOfAll flow [ spec.On; spec.Values ])
              Keys = spec.Index |> List.map (linOf flow >> asKey) |> Set.unionMany }
        | Unpivot(idVars, valueVars) ->
            { flow with
                Cols =
                    (idVars |> List.map (fun k -> k, linOf flow k))
                    @ [ "variable", noLin; "value", linOfAll flow valueVars ]
                Rest = None }
        // A definition changes no frame.
        | Define _ -> flow
        // A fit's every statistic is computed from every column it reads, and a categorical term's
        // `term` names carry its levels, which are row values: each output column carries the lineage of
        // everything the fit read. Its tables are closed, so the column names are the evaluator's own.
        | Fit spec ->
            let read = linOfAll flow (Reads.ofStep step |> Result.defaultValue [])
            let keys = spec.GroupBy |> List.map (fun k -> k, linOf flow k)
            let grain = keys |> List.map (snd >> asKey) |> Set.unionMany
            let stats (names: string list) = names |> List.map (fun n -> n, read)

            match spec.Output with
            | FitOutput.Coefficients ->
                { Cols = keys @ stats [ "term"; "estimate"; "std_error"; "t" ] @ [ "unit", noLin ]
                  Rest = None
                  Keys = grain }
            | FitOutput.Summary ->
                let theta =
                    match spec.Family with
                    | FitFamily.NegativeBinomial _ -> [ "theta"; "theta_std_error"; "alternations" ]
                    | _ -> []

                { Cols =
                    keys
                    @ stats (
                        [ "n"
                          "df"
                          "sigma"
                          "r2"
                          "rank"
                          "deviance"
                          "null_deviance"
                          "dispersion"
                          "iterations"
                          "converged" ]
                        @ theta
                    )
                  Rest = None
                  Keys = grain }
            | FitOutput.Fitted ->
                { flow with
                    Cols = upsert "fitted" read flow.Cols }
            | FitOutput.Residuals ->
                { flow with
                    Cols = upsert "residual" read flow.Cols }
        // A prediction appends columns computed from the row's keys and terms AND from every column of
        // the model's source it reads.
        | Predict spec ->
            let other =
                match spec.Model with
                | PredictModel.Training source
                | PredictModel.Coefficients source -> operandFlow mark model ops source

            let read =
                join
                    (linOfAll flow (Reads.ofStep step |> Result.defaultValue []))
                    (linOfAll other (Reads.ofPredictModel spec))

            let appended =
                match spec.Model with
                | PredictModel.Coefficients _ -> [ "predicted" ]
                | PredictModel.Training _ ->
                    [ "predicted"; "se_fit"; "se_pred" ]
                    @ (if Option.isSome spec.Interval then
                           [ "lower"; "upper" ]
                       else
                           [])

            { flow with
                Cols = appended |> List.fold (fun cols n -> upsert n read cols) flow.Cols }

    // ---- how a step uses what it reads ----

    /// How a read is used: carried into another column, folded into an aggregate, keying a grouping
    /// or a join, returned as a named result column, or used row by row (named, for the refusal).
    type private Use =
        | Carry
        | AggInput
        | Key of string
        | Returned of string
        | Expose of string

    let private uses
        (mark: string option)
        (model: ModelNode)
        (ops: Map<string, Flow>)
        (flow: Flow)
        (step: Transform)
        : (Lin * Use) list =
        let names (u: Use) (ns: string list) =
            ns |> List.map (fun n -> linOf flow n, u)

        match step with
        | Filter p -> exprLins flow true p |> List.map (fun l -> l, Expose "a filter predicate")
        | Project pairs -> names Carry (pairs |> List.map fst)
        | Derive(_, e) -> names Carry (Reads.ofExpr e)
        | GroupBy(keys, aggs) ->
            names (Key "a grouping key") keys
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
            let other = operandFlow mark model ops source

            names (Key "a join key") (on |> List.map fst)
            @ (on |> List.map (fun (_, r) -> linOf other r, Key "a join key"))
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
            (everything flow @ everything (operandFlow mark model ops source))
            |> List.map (fun l -> l, Expose "part of a row compared whole")
        | Define _ -> []
        // A fit is not an aggregate the group floor governs: its fitted and residual values are per
        // row, and a coefficient over a small group can pin a row's value. Every column it reads is
        // used row by row.
        | Fit _ -> names (Expose "a model fit's input") (Reads.ofStep step |> Result.defaultValue [])
        | Predict spec ->
            let source =
                match spec.Model with
                | PredictModel.Training s
                | PredictModel.Coefficients s -> s

            let other = operandFlow mark model ops source

            names (Expose "a prediction's input") (Reads.ofStep step |> Result.defaultValue [])
            @ (Reads.ofPredictModel spec
               |> List.map (fun c -> linOf other c, Expose "a prediction's model"))

    /// How far `grants` reach a classification: row by row when any grant over it says so; as a key when
    /// one does and none says more; in aggregate when one does and none says more; and not at all when
    /// none names it. A keyed grant governs an aggregate grant over the same classification (D18): it
    /// is the statement that the value never leaves, so holding both reads the classification keyed.
    let level (grants: Grant list) (classification: string) : GrantLevel option =
        let levels =
            grants
            |> List.filter (fun g -> g.Classification = classification)
            |> List.map _.Level

        [ GrantLevel.RowLevel; GrantLevel.Keyed; GrantLevel.Aggregate ]
        |> List.tryFind (fun l -> List.contains l levels)

    /// Check one use against the grants; `Ok` carries every column it admitted, with its level.
    let private checkUse
        (subject: Subject)
        (at: PolicySite)
        (lin: Lin, how: Use)
        : Result<(ColumnRef * GrantLevel) list, PolicyRefusal> =
        let granted = subject.Grants |> List.map _.Classification |> List.distinct

        // A presence is the column's row value to every level but `Keyed` (D20).
        let rowValues = lin.Raw + lin.Tested

        Set.toList (rowValues + lin.Agg)
        |> List.fold
            (fun acc c ->
                acc
                |> Result.bind (fun seen ->
                    match level subject.Grants c.Classification with
                    | None -> Error(PolicyRefusal.ColumnNotGranted(at, c, granted))
                    | Some GrantLevel.Aggregate when rowValues.Contains c ->
                        match how with
                        | Expose usage
                        | Key usage -> Error(PolicyRefusal.AggregateOnly(at, c, usage))
                        | Returned _ -> Error(PolicyRefusal.AggregateOnly(at, c, "a result column"))
                        | Carry
                        | AggInput -> Ok(seen @ [ c, GrantLevel.Aggregate ])
                    | Some GrantLevel.Keyed when (asKey lin).Contains c ->
                        match how with
                        | Expose usage -> Error(PolicyRefusal.KeyOnly(at, c, usage))
                        | Returned name -> Error(PolicyRefusal.KeyInResult(name, c))
                        | Key _
                        | Carry
                        | AggInput -> Ok(seen @ [ c, GrantLevel.Keyed ])
                    | Some level -> Ok(seen @ [ c, level ])))
            (Ok [])

    /// The columns `lin` carries that a grouping over it must floor: the row values of an aggregate-only
    /// column, its presence included, and a keyed column's values and picks (ruling 5 holds for both
    /// levels, D18). A keyed column's presence is not its value, and needs no floor (D20).
    let private aggregateOnly (subject: Subject) (lin: Lin) : ColumnRef list =
        asKey lin + lin.Tested
        |> Set.toList
        |> List.filter (fun c ->
            match level subject.Grants c.Classification with
            | Some GrantLevel.Aggregate -> (lin.Raw + lin.Tested).Contains c
            | Some GrantLevel.Keyed -> (asKey lin).Contains c
            | Some GrantLevel.RowLevel
            | None -> false)

    /// The written pipeline walked against the grants, step by step: the lineage flowing out of its last
    /// step, every column it admitted, and, per grouping step that aggregates an aggregate-only column,
    /// the `Of` columns that need a count.
    let private walkGrants
        (mark: string option)
        (model: ModelNode)
        (ops: Map<string, Flow>)
        (subject: Subject)
        (start: Flow)
        (pipeline: Transform list)
        : Result<
              Flow * (ColumnRef * GrantLevel) list * Map<int, string list * Agg list * (string * ColumnRef list) list>,
              PolicyRefusal
           >
        =
        pipeline
        |> List.indexed
        |> List.fold
            (fun acc (i, step) ->
                acc
                |> Result.bind (fun (flow, admitted, floors) ->
                    let at = PolicySite.Step(i + 1)

                    uses mark model ops flow step
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
                            | Except _
                            | Define _
                            | Fit _
                            | Predict _ -> floors

                        advance mark model ops flow step, admitted, floors)))
            (Ok(start, [], Map.empty))

    /// The written pipeline walked against the grants, and its result: every column it admitted, and,
    /// per grouping step that aggregates an aggregate-only column, the `Of` columns that need a count.
    let private grants
        (mark: string option)
        (model: ModelNode)
        (ops: Map<string, Flow>)
        (subject: Subject)
        (start: Flow)
        (pipeline: Transform list)
        : Result<
              (ColumnRef * GrantLevel) list * Map<int, string list * Agg list * (string * ColumnRef list) list>,
              PolicyRefusal
           >
        =
        walkGrants mark model ops subject start pipeline
        |> Result.bind (fun (flow, admitted, floors) ->
            // the names a walk cannot list are reported under one name: every column of the rows
            (flow.Cols @ (flow.Rest |> Option.map (fun l -> "*", l) |> Option.toList))
            |> List.fold
                (fun acc (name, l) ->
                    acc
                    |> Result.bind (fun seen ->
                        checkUse subject PolicySite.Result (l, Returned name) |> Result.map ((@) seen)))
                (Ok admitted)
            |> Result.bind (fun admitted ->
                // a keyed column keeps its grain out of the result as well as its value (D18)
                match
                    flow.Keys
                    |> Set.toList
                    |> List.tryFind (fun c -> level subject.Grants c.Classification = Some GrantLevel.Keyed)
                with
                | Some c -> Error(PolicyRefusal.KeyGrain c)
                | None -> Ok(List.distinct admitted, floors)))

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
        // A coefficient table holds a row per group and term, which can outnumber the rows read.
        | Fit spec ->
            match spec.Output with
            | FitOutput.Coefficients -> true
            | FitOutput.Summary
            | FitOutput.Fitted
            | FitOutput.Residuals -> false
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
        | Except _
        | Define _
        | Predict _ -> false

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
        // A fit, and a prediction from its training rows, group by the spec's keys.
        | Fit spec -> List.length spec.GroupBy
        | Predict spec -> List.length spec.Fit.GroupBy
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
        | Except _
        | Define _ -> 0

    let private isJoin (step: Transform) : bool =
        match step with
        | Join _ -> true
        // A prediction pairs each row with its group's model, read from another table by key: a join
        // in all but name, and held to the same cap.
        | Predict _ -> true
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
        | Except _
        | Define _
        | Fit _ -> false

    let private cost
        (model: ModelNode)
        (tables: (ModelNode * TableSpec) list)
        (pipeline: Transform list)
        (operandKeys: (int * int) list)
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

                // A grouped operand's groupings are held to the same cap, at the step of the join that
                // brings it in (DECISIONS.md D16).
                let widest =
                    (pipeline |> List.mapi (fun i s -> i + 1, groupKeys s)) @ operandKeys
                    |> List.tryFind (fun (_, n) -> not (within n limits.MaxGroupKeys))

                if not (within joins limits.MaxJoins) then
                    Error(PolicyRefusal.TooManyJoins(joins, limits.MaxJoins))
                else
                    match widest with
                    | Some(at, n) -> Error(PolicyRefusal.TooManyGroupKeys(at, n, limits.MaxGroupKeys))
                    | None ->
                        match bounding pipeline with
                        | None -> Error(PolicyRefusal.LimitMissing ceiling)
                        | Some(i, Slot.Param p) -> Error(PolicyRefusal.LimitUnbound(i, p))
                        | Some(i, Slot.Lit n) when n > ceiling -> Error(PolicyRefusal.LimitAboveCeiling(i, n, ceiling))
                        | Some(_, Slot.Lit n) ->
                            let keys =
                                (pipeline |> List.map groupKeys) @ (operandKeys |> List.map snd)
                                |> List.fold max 0

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
        | Interpolate(x, knots, kind, outside) -> Interpolate(go x, knots, kind, outside)
        | Call(name, args) -> Call(name, List.map go args)
        | Arg _ -> e

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
                        | Predict _ ->
                            refuse "a prediction reads the table's rows whole as its model, before any filter"
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
                        | Define _
                        | Fit _ -> Ok [])

    // ---- the group floor ----

    let private fresh (taken: string list) (stem: string) : string =
        let rec go (name: string) =
            if List.contains name taken then go (name + "_") else name

        go stem

    /// A grouping rewritten to drop a group for too few contributors, not for none (DECISIONS.md D21):
    /// each counted input has no contributor or at least `floor`, and, above a floor of 1, some input
    /// has at least `floor`, which shows the group has at least `floor` rows. A count of zero is then a
    /// fact about that many rows, of the kind a count at the floor already answers; with one input the
    /// two conditions are one comparison, as they were before D21. At a floor of 1 every group has a row
    /// to show, and the floor drops nothing.
    let private floored (floor: int) (keys: string list) (aggs: Agg list) (needs: (string * ColumnRef list) list) =
        let taken = keys @ (aggs |> List.map _.Name)

        let counts =
            needs
            |> List.mapi (fun j (o, _) ->
                { Name = fresh taken (sprintf "floor_count_%d" (j + 1))
                  Fn = Count
                  Of = o })

        let atFloor (c: Agg) = Binary(Ge, Col c.Name, Lit(Int floor))

        let noneOrAtFloor (c: Agg) =
            Binary(Or, Binary(Eq, Col c.Name, Lit(Int 0)), atFloor c)

        let keep =
            match counts with
            | [ c ] when floor > 1 -> atFloor c
            | _ ->
                (counts |> List.map noneOrAtFloor)
                @ (if floor > 1 then
                       [ counts |> List.map atFloor |> List.reduce (fun a b -> Binary(Or, a, b)) ]
                   else
                       [])
                |> List.reduce (fun a b -> Binary(And, a, b))

        [ GroupBy(keys, aggs @ counts)
          Filter keep
          Project(taken |> List.map (fun n -> n, n)) ]

    // ---- admission ----

    /// A refusal about one of a grouped operand's steps, placed at the step of the join that brings the
    /// operand in: the operand is part of that step (DECISIONS.md D16).
    let private atJoin (j: int) (r: PolicyRefusal) : PolicyRefusal =
        match r with
        | PolicyRefusal.ColumnNotGranted(PolicySite.Step _, c, granted) ->
            PolicyRefusal.ColumnNotGranted(PolicySite.Step j, c, granted)
        | PolicyRefusal.AggregateOnly(PolicySite.Step _, c, usage) ->
            PolicyRefusal.AggregateOnly(PolicySite.Step j, c, usage)
        | PolicyRefusal.KeyOnly(PolicySite.Step _, c, usage) -> PolicyRefusal.KeyOnly(PolicySite.Step j, c, usage)
        | other -> other

    /// A grouped operand the pipeline joins, walked against the grants.
    type private Joined =
        {
            Operand: Operand
            /// The written step of the first join that brings it in.
            At: int
            Node: ModelNode
            Spec: TableSpec
            /// The lineage flowing out of its pipeline, into the join.
            Out: Flow
            /// Every column it admitted, with its level.
            Admitted: (ColumnRef * GrantLevel) list
            /// Its grouping steps that need a floor, by its own step.
            Floors: Map<int, string list * Agg list * (string * ColumnRef list) list>
        }

    /// Every grouped operand `pipeline` joins, in join order, each walked against the grants over its
    /// own table, as any pipeline is: a column it reads must be granted, and an aggregate-only column is
    /// admitted inside its aggregates.
    let private joinedOperands
        (mark: string option)
        (model: ModelNode)
        (subject: Subject)
        (operands: Operand list)
        (pipeline: Transform list)
        : Result<Joined list, PolicyRefusal> =
        pipeline
        |> List.indexed
        |> List.choose (fun (i, s) ->
            match s with
            | Join(Ref name, _, _) -> Operand.tryFind name operands |> Option.map (fun o -> o, i + 1)
            | _ -> None)
        |> List.distinctBy (fun (o, _) -> o.Name)
        |> List.fold
            (fun acc (o, at) ->
                acc
                |> Result.bind (fun seen ->
                    match Model.tryTable o.Table model with
                    | None ->
                        Error(
                            PolicyRefusal.Pipeline(
                                PipelineRefusal.Operand(
                                    o.Name,
                                    PipelineRefusal.UnknownTable(
                                        0,
                                        o.Table,
                                        Model.tables model |> List.map (fun (_, t) -> t.Name)
                                    )
                                )
                            )
                        )
                    | Some(node, spec) ->
                        walkGrants mark model Map.empty subject (tableFlow mark node spec) o.Pipeline
                        |> Result.mapError (atJoin at)
                        |> Result.map (fun (out, admitted, floors) ->
                            seen
                            @ [ { Operand = o
                                  At = at
                                  Node = node
                                  Spec = spec
                                  Out = out
                                  Admitted = admitted
                                  Floors = floors } ])))
            (Ok [])

    /// The column authorisation, the first half of admission: grants held, the pipeline against the
    /// model, then the column grants step by step and the result. `Ok` carries the source table, every
    /// column admitted with its level, the grouping steps that need a floor, and the grouped operands
    /// the pipeline joins.
    let private authorise
        (mark: string option)
        (model: ModelNode)
        (subject: Subject)
        (operands: Operand list)
        (source: DataSource)
        (pipeline: Transform list)
        =
        if List.isEmpty subject.Grants then
            Error(PolicyRefusal.NoGrants subject.Principal)
        else
            PipelineCheck.checkWith model operands source pipeline
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
                joinedOperands mark model subject operands pipeline
                |> Result.bind (fun joined ->
                    let ops = joined |> List.map (fun j -> j.Operand.Name, j.Out) |> Map.ofList

                    grants mark model ops subject (tableFlow mark sourceNode sourceSpec) pipeline
                    |> Result.map (fun (admitted, floors) ->
                        (sourceNode, sourceSpec),
                        List.distinct (admitted @ (joined |> List.collect _.Admitted)),
                        floors,
                        joined)))

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
        authorise None model subject [] source pipeline
        |> Result.map (fun (_, admitted, _, _) -> admitted)

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
        authorise (Some mark) model subject [] source pipeline
        |> Result.map (fun (_, admitted, _, _) -> admitted)

    /// Decide what `subject` may read of `pipeline` over `source`, against `model`, its joins bringing in
    /// `operands` by name (DECISIONS.md D16). `Ok` is the admitted pipeline with every rule applied;
    /// `Error` the first refusal, in this order: grants held, the pipeline and its operands against the
    /// model, the column grants step by step (an operand's at the join that brings it in) and then the
    /// result, the group floor, the row policies, the cost limits.
    ///
    /// A grouped operand is rewritten as a pipeline is: its table's row policies become its first
    /// steps, before its grouping, recorded as `RowPolicyApplied` at the admitted step of the join that
    /// brings it in; a grouping in it over an aggregate-only column is floored, recorded as
    /// `GroupFloorApplied` at the written step of that join. Its join counts once against the join
    /// cap, and its groupings are held to the grouping-column cap at that join.
    let admitWith
        (model: ModelNode)
        (subject: Subject)
        (operands: Operand list)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<Admitted, PolicyRefusal> =
        authorise None model subject operands source pipeline
        |> Result.bind (fun ((sourceNode, sourceSpec), admitted, floors, joined) ->
            let brought =
                pipeline
                |> List.choose Reads.operandOf
                |> List.choose (fun s ->
                    match s with
                    | Ref n -> Model.tryTable n model
                    | Embedded _ -> None)

            let tables =
                ((sourceNode, sourceSpec) :: brought)
                @ (joined |> List.map (fun j -> j.Node, j.Spec))
                |> List.distinctBy (fun (n, _) -> n.Id)

            let start = tableFlow None sourceNode sourceSpec

            let operandKeys =
                joined
                |> List.collect (fun j -> j.Operand.Pipeline |> List.map (fun s -> j.At, groupKeys s))

            let costVerdict, floor = cost model tables pipeline operandKeys

            let needing (at: int -> int) (fs: Map<int, string list * Agg list * (string * ColumnRef list) list>) =
                fs
                |> Map.toList
                |> List.tryPick (fun (step, (_, _, needs)) ->
                    needs |> List.collect snd |> List.tryHead |> Option.map (fun c -> at step, c))

            let first =
                needing id floors
                |> Option.orElse (joined |> List.tryPick (fun j -> needing (fun _ -> j.At) j.Floors))

            (match floor, first with
             | None, Some(step, c) -> Error(PolicyRefusal.FloorUndeclared(step, c))
             | None, None
             | Some _, _ -> Ok(admitted, floors))
            |> Result.bind (fun (admitted, floors) ->
                bound subject sourceSpec sourceNode
                |> Result.bind (fun heads ->
                    joined
                    |> List.fold
                        (fun acc j ->
                            acc
                            |> Result.bind (fun xs ->
                                bound subject j.Spec j.Node |> Result.map (fun h -> xs @ [ j, h ])))
                        (Ok [])
                    |> Result.map (fun operandHeads -> heads, operandHeads))
                |> Result.bind (fun (heads, operandHeads) ->
                    // the flow before each written step, for the post-join names
                    let ops = joined |> List.map (fun j -> j.Operand.Name, j.Out) |> Map.ofList

                    let flows =
                        pipeline
                        |> List.scan (advance None model ops) start
                        |> List.take (List.length pipeline)

                    List.zip flows (pipeline |> List.indexed |> List.map (fun (i, s) -> i + 1, s))
                    |> List.fold
                        (fun acc (flow, st) ->
                            acc
                            |> Result.bind (fun xs ->
                                afterStep model subject flow st |> Result.map (fun a -> xs @ [ a ])))
                        (Ok [])
                    |> Result.map (fun after -> admitted, floors, heads, operandHeads, after)))
            |> Result.bind (fun (admitted, floors, heads, operandHeads, after) ->
                costVerdict
                |> Result.map (fun (rows, joins, keys, limits) ->
                    let headSteps = heads |> List.map (fun (_, p) -> Filter p)

                    let headRules =
                        heads
                        |> List.mapi (fun i (p, pred) ->
                            PolicyRule.RowPolicyApplied(sourceSpec.Name, p, i + 1, pred))

                    let flooredBy (f: int) (fs: Map<int, string list * Agg list * (string * ColumnRef list) list>) =
                        List.indexed
                        >> List.collect (fun (i, step) ->
                            match Map.tryFind (i + 1) fs with
                            | Some(keys, aggs, needs) -> floored f keys aggs needs
                            | None -> [ step ])

                    let floorRule (at: int) (f: int) (needs: (string * ColumnRef list) list) =
                        PolicyRule.GroupFloorApplied(at, f, needs |> List.collect snd |> List.distinct)

                    // the admitted pipeline, its rules, and each written step's admitted position
                    let steps, rules, positions =
                        List.zip (pipeline |> List.indexed) after
                        |> List.fold
                            (fun
                                (steps: Transform list, rules: PolicyRule list, positions: Map<int, int>)
                                ((i, step), policies) ->
                                let positions = Map.add (i + 1) (List.length steps + 1) positions

                                let written, floorRules =
                                    match Map.tryFind (i + 1) floors, limits.MinGroupSize with
                                    | Some(keys, aggs, needs), Some f ->
                                        floored f keys aggs needs, [ floorRule (i + 1) f needs ]
                                    | Some _, None
                                    | None, _ -> [ step ], []

                                let steps = steps @ written

                                let policyRules =
                                    policies
                                    |> List.mapi (fun j (t, p, pred) ->
                                        PolicyRule.RowPolicyApplied(t, p, List.length steps + j + 1, pred))

                                steps @ (policies |> List.map (fun (_, _, pred) -> Filter pred)),
                                rules @ floorRules @ policyRules,
                                positions)
                            (headSteps, [], Map.empty)

                    // each operand rewritten: its row policies first, then its pipeline, floored
                    let admittedOperands, operandRules =
                        operandHeads
                        |> List.map (fun (j, h) ->
                            let pipeline, flooredRules =
                                match limits.MinGroupSize with
                                | Some f ->
                                    flooredBy f j.Floors j.Operand.Pipeline,
                                    j.Floors
                                    |> Map.toList
                                    |> List.map (fun (_, (_, _, needs)) -> floorRule j.At f needs)
                                | None -> j.Operand.Pipeline, []

                            let at = Map.tryFind j.At positions |> Option.defaultValue j.At

                            { j.Operand with
                                Pipeline = (h |> List.map (fun (_, p) -> Filter p)) @ pipeline },
                            (h
                             |> List.map (fun (p, pred) -> PolicyRule.RowPolicyApplied(j.Spec.Name, p, at, pred)))
                            @ flooredRules)
                        |> List.unzip

                    { model = model
                      source = source
                      written = pipeline
                      pipeline = steps
                      operands = admittedOperands
                      applied =
                        [ PolicyRule.ColumnsGranted admitted ]
                        @ headRules
                        @ rules
                        @ List.concat operandRules
                        @ [ PolicyRule.CostAdmitted(rows, joins, keys, limits) ]
                      limits = limits
                      principal = subject.Principal })))

    /// Decide what `subject` may read of `pipeline` over `source`, against `model`: `admitWith` with no
    /// grouped operand. `Ok` is the admitted pipeline with every rule applied; `Error` the first refusal,
    /// in this order: grants held, the pipeline against the model, the column grants step by step and
    /// then the result, the group floor, the row policies, the cost limits.
    let admit
        (model: ModelNode)
        (subject: Subject)
        (source: DataSource)
        (pipeline: Transform list)
        : Result<Admitted, PolicyRefusal> =
        admitWith model subject [] source pipeline
