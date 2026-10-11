# Fuaran.DB — decisions (newest first)

## 2026-10-11 — D5: policy before lowering (Phase 1902)

What a subject may read is decided on the tree, before any statement exists, by REFUSAL (the pipeline is not
lowered, and the refusal names the rule, the subject attribute it read and what would satisfy it) and by
REWRITING (steps added to the pipeline, which a reader can inspect). Ten rulings, each with what would reopen it.

1. **Only an admitted pipeline lowers.** `Policy.admit` answers `Admitted`, whose constructor is private to
   `Fuaran.DB`; `FoldSplit.lower` and `FoldSplit.explain` take it, and the statement carries its timeout. The
   ungated lowering is internal to the provider. The placement laws keep their public signature and lower
   through that internal entry point, so a dialect author outside the assembly certifies a dialect with no
   access to an ungated lowering and no `InternalsVisibleTo`; only this repository's suite is granted it, to
   exercise placement on single pipelines. This is neither a public `FoldSplit.Unchecked` (which would make an
   ungated read expressible again) nor an all-admitting policy under the laws (which would make them certify
   placement only for pipelines ending in a page). *Reopen if* a dialect author needs to lower ad hoc pipelines
   outside the laws.
2. **The subject is a value, and the default is deny.** `Subject` is a principal, its attributes and its grants;
   the domain resolves no identity. A grant names a classification and a level, `Aggregate` or `RowLevel`.
   A subject with no grants is refused whole (`DB-GRANT-NONE`), and a column whose classification it holds no
   grant over is unreadable (`DB-GRANT-COLUMN`). A pipeline that reads no column (a projection to nothing)
   still answers a row count under the row policy, to any subject holding some grant. *Reopen if* counting a
   table's rows needs a grant of its own.
3. **A read is followed to the model column it came from.** Every step's reads are walked with each name's
   lineage (renames, derivations, aggregates, a join's `_right` suffix), so a column read only inside a
   predicate counts, a renamed one is still itself, and every column of the RESULT is a read: a pipeline with
   no projection reads everything. A join's right keys are reads; `Distinct` and the set operations read every
   column of both rows they compare. What a rewrite reads (a row policy, a floor count) is the model's read,
   not the subject's.
4. **Aggregate-only means aggregate input only.** A column the subject holds at `Aggregate` may be carried by a
   `Project` or `Derive` and folded into a `GroupBy` aggregate other than `First` and `Last` (each answers one
   row's value). Any row-wise use is refused (`DB-GRANT-AGGREGATE-ONLY`) and named: a predicate, a grouping or
   join key, a sort key, a window or pivot input, a row compared whole, a result column. That includes a filter
   ahead of an aggregate, which is conservative. *Reopen if* a filter on an aggregate-only column ahead of a
   floored aggregate is wanted, with the argument that the floor covers it.
5. **The group floor is a rewrite.** A grouping that aggregates an aggregate-only column gains one `Count` per
   such input column (the evaluator's `Count` counts present cells, so it is the number of contributors), a
   `Filter` keeping groups at or above the model's floor, and a `Project` back to the written columns. Without a
   declared floor it is refused (`DB-GROUP-FLOOR-UNDECLARED`). An aggregate over already-aggregated columns needs
   no second floor. `Min` and `Max` over a group at the floor still answer one row's value; that is recorded, not
   prevented. *Reopen* with a model-declared rule naming the aggregates a classification admits.
6. **A row policy is a model predicate over the subject's attributes.** A policy's `Param`s name subject
   attributes and are substituted as values before the policy is applied, so a request's bindings never reach
   it; an attribute the subject lacks is refused, naming it (`DB-ROW-POLICY-ATTRIBUTE`). The source's policies
   are the first steps of the admitted pipeline. A joined table's are a `Filter` immediately after an INNER join,
   under the names the join gives its columns, which is equivalent to filtering the table first. A table with a
   policy brought in by any other join kind or by a set operation is refused (`DB-ROW-POLICY-UNWRAPPABLE`): a
   `DataSource` has no wrapped form, and a filter after a left, semi or anti join or a set operation cannot remove
   the rows that decided it. *Reopen when* the transform algebra gains an operand that wraps a source.
7. **Cost limits are model declarations on the limit node.** `LimitSpec` gains `MaxJoins`, `MaxGroupKeys`,
   `MinGroupSize` and `TimeoutSeconds`, each optional and written on the wire only when declared, so no existing
   vector moved. A read is held to the tightest of the limits on the model and on every table it reads. The row
   ceiling and the timeout are required (`DB-COST-CEILING-UNDECLARED`, `DB-COST-TIMEOUT-UNDECLARED`); a join or
   grouping cap no limit declares admits none (`DB-COST-JOINS`, `DB-COST-GROUP-KEYS`). The pipeline must end in a
   `Limit` within the ceiling, with only steps that cannot add rows after it (`DB-COST-LIMIT-MISSING`); its count
   must be literal (`DB-COST-LIMIT-UNBOUND`) and within the ceiling (`DB-COST-LIMIT-CEILING`). Joins are counted
   per `Join` step; grouping keys per `GroupBy` and per `Pivot` index. *Reopen if* set operations need a cap of
   their own.
8. **The verdict carries its record.** `Admitted` holds the written pipeline, the rewritten one, the limits, and
   every rule applied in order: the columns granted with the level that admitted each, each row policy with its
   substituted predicate and its position, each floor, and the cost admitted. Phase 1903's connection record
   carries it as data.
9. **The refusals are one closed union with stable codes** (`Policy.codes`), decided in a fixed order: grants
   held, the pipeline against the model (`DB-PIPELINE`, wrapping the check's own refusal), the column grants
   step by step and then the result, the group floor, the row policies, the cost limits. `Policy.describe`
   says the rule, what it found, the subject attribute it read (`attributeRead`) and what would satisfy it
   (`remedy`).
10. **The laws live in the suite, over a generated sample with adequacy.** Over (model variant, subject,
    pipeline) cases: admission is total and pure; every admitted pipeline checks against the model; a row
    policy survives every admitted pipeline (the rows it excludes change nothing the pipeline answers, a
    pipeline that tries to filter it away among them); every aggregate over an aggregate-only column is
    followed by its floor; every admitted pipeline lowers with its timeout and its placement answers what it
    answers. Sample adequacy demands every refusal code and every rewrite (the row policy at the source and
    after a join, and the floor), and was seen red at six iterations. The row-policy law and the floor law were
    seen red with the policy and the floor stripped; those falsifiers are permanent tests. *Reopen* to publish
    the laws as a package validator when a host composes its own policy.

## 2026-10-11 — D4: the provider and the fold split (Phase 1900)

The second package, `Fuaran.DB.Provider`: a pure function from a dataset model, a dialect, a pipeline and its
bindings to one parameterised statement and the steps left to run locally, or a named refusal. Ten rulings,
each with what would reopen it.

1. **The provider is pure and references nothing but its substrate.** `FSharp.Core`, `Fuaran.DB`,
   `Fuaran.Compute.DataFrame` 0.38.1 and `Fuaran.Core.Conformance` 0.36.0 (for the law vocabulary); it opens no
   connection, reads no clock and references no ADO type. A test reads the project file and goes red on any
   other reference. *Reopen only* by a decision that names the coupling and why a seam cannot carry it.
2. **Bind, plan, split, in that order.** `FoldSplit.lower` substitutes the bindings (scalar parameters and
   slots, list parameters, and the clock once per grain), runs the compute planner (`Plan.explain`) over the
   source's model schema, and walks the PLANNED pipeline, lowering the longest prefix every step of which
   lowers. The planner's reorders therefore reach the split: a filter it moves ahead of a sort lowers with
   the page. The suffix is the rest of the planned pipeline; `FoldSplit.finish` runs it in the reference
   evaluator over the statement's result. `explain` answers the planner's report, how many planned steps
   lowered, and the refusal that placed the boundary, and `describe` says it in sentences.
   - **Reduce or preserve.** Every verb in the subset keeps or drops rows, except a join, which can fan out.
     A join is admitted only along a relation the model declares, which is the model author's statement of
     how the tables relate; the model has no cardinality to check a fan-out against. *Reopen if* `JoinSpec`
     gains a cardinality: a many-to-many join then places the boundary.
3. **The statement is data first and text second.** A `Statement` is the text, the parameters in marker
   order (each a cell with its column type and the dialect's name for it), the result columns, and the
   abstract `Relation` the text was rendered from, one nested select per step. The result is read
   POSITIONALLY into the pipeline's names (`Statement.toTable`), so no pipeline name is ever emitted.
   - **No value is text**: `Scalar` has no literal case. A literal, a bound parameter, a bound list, a
     pinned clock reading and a page's count and offset all become parameters. A parameter's type is its
     cell's; a null takes the type of what it is compared with, and a null nothing fixes places the
     boundary (`Untyped`).
   - **No identifier comes from the request** (the plan's D7): a scan names the model's PHYSICAL table and
     column identifiers, and every other identifier is minted (`c<n>` for columns, `t<n>` for derived
     tables). A join's keys are followed through renames to the model columns they came from, so a
     renamed key still names its declared relation (this is the column provenance D2.6 named as its
     reopening; the pipeline check is unchanged).
4. **The declared subset** (the plan's D5), with the evidence of recipe 07 (`fuaran-core#278`, the pushdown
   measurement: its difference list and friction findings, measured on SQLite at Core 0.30.0) beside each
   exclusion. A construct outside it places the boundary and runs locally, where the reference
   says what it means.
   - **Lowered:** `Filter` over a predicate the planner's totality verdict admits and that is statically
     boolean (`Col`, values, `Binary`, `Not`, `IsNull`, `Coalesce`, `Case`, `InList`, `ApplyFn`); `Project`;
     `Join` of kind `Inner`, `Left`, `Semi` or `Anti` along a declared relation; `GroupBy` with `Count`,
     `CountDistinct`, `Sum`, `Mean`, `Min` and `Max`; `Sort` immediately followed by `Limit`, as one page that
     ends the statement. The recipe lowered this list plus `Derive` with 0 mismatches over 418 pipelines
     under its full pin set.
   - **`Pivot` never lowers**: it would turn data values into identifiers (the plan's D6 and sixth finding;
     the recipe's chart case and `orders-by-segment` both stop at it).
   - **`Derive`, `Window`, `Distinct`, `Unpivot`, `Union`, `Intersect`, `Except` do not lower.** `Derive`:
     20 lines, "a derived column's type over no rows or only nulls" (`probe-derived-type-empty`, `gen-1008`,
     F2); compute's D5 (0.38.1) now types a decided derive statically, which removes most of that class.
     *Reopen* for `Derive` on that evidence. `Window`: the recipe did not lower it (`top-lines-per-category`
     stops before it, section 8), and its frame is order-sensitive. `Distinct`: its identity is the
     canonical token (`Int 1` is not `Float 1.0`, NaN is one value), which SQL's does not share; not
     measured. `Unpivot` and the set operations grow or combine rows and were not lowered by the recipe.
   - **A `Limit` with no `Sort` before it, a `Sort` with no `Limit` after it, and any step after a page do
     not lower**: "row order (a relation is unordered)", 10 lines (`gen-1021`), and "an order-sensitive local
     step after an unordered statement", 3 lines (`probe-order-sensitive-suffix`); the recipe's split stopped
     34 pipelines at an unordered limit and 83 at a sort.
   - **`Right` and `Outer` joins do not lower**: the recipe did not lower them (4 pipelines, its
     `JoinKindNotLowered`), and they append unmatched right rows after the left ones, an order a relation
     does not keep.
   - **`Median`, `StdDev`, `First` and `Last` do not lower**: `Median` was refused by the recipe (10
     pipelines at an aggregate); `First` and `Last` read row order; `StdDev` is the population deviation here
     and engines differ on which they compute (not measured).
   - **`Cast` does not lower**: "type affinity (the engine coerces where the reference refuses)", 12 lines
     (`probe-type-affinity`, `gen-1011`), and 4 pipelines stopped at a cast or a clock.
   - **`Lower` and `Upper` do not lower**: "case folding beyond ASCII", 3 lines (`probe-case-folding`,
     `gen-1147`, `gen-1260`). **`Length` does not lower**: "string length unit", 1 line
     (`probe-length-unit`).
   - **`Quotient` and `Rounded` do not lower**: the recipe's pin predates the decimal type and exact
     rounding, so decimal scale, the difference the plan's D8 names, was not reachable. *Reopen* when a
     dialect's conformance run measures it.
   - **A membership test against an empty list does not lower**: SQL cannot spell `IN ()`.
   - **A predicate the totality verdict does not admit does not lower** (`MayRefuse`): an engine coerces or
     widens where the reference refuses (type affinity, 12 lines; integer width, 1 line,
     `probe-int-overflow`), so the step runs where the reference reports it.
   - **The substring operators are the DIALECT's call, not the subset's**: "pattern matching", 7 lines,
     pinned on SQLite and predicted refused on T-SQL, so `Contains`, `StartsWith` and `EndsWith` lower unless
     a dialect refuses them.
5. **Pinned in the form, not by the dialect**, each a difference the recipe measured: nulls sort last in
   either direction (5 lines, `probe-null-ordering`, `gen-1188`), which every order key of a page states
   and `RenderPaging` must spell; a keyless aggregate over no rows answers none (6 lines,
   `probe-empty-global-aggregate`), which `Aggregate`'s `NonEmpty` states and the renderer spells as
   `HAVING COUNT(*) > 0`; a negative count or offset (4 lines, `probe-negative-limit`) is clamped to zero at
   bind, as the reference clamps it; a repeated column name (1 line, `probe-repeated-name`, F1) places the
   boundary, because a result is read back by name.
6. **The remaining differences are a dialect's to pin or exclude** (Phase 1901): collation (2 lines),
   integer division (1), float summation order (2), sort stability under ties (1), decimal scale, and the
   integer width of a pushed `Sum` (1). The last is the one place a lowered prefix can refuse: the reference
   dialect refuses it as the reference does, and a real dialect pins it where it decodes the result.
   Parity is claimed as written over the reference dialect, which keeps arrival order; over a real engine
   the claim is the conformance subset Phase 1901 states (bag parity unless a page ends the statement,
   unique sort keys under a limit).
7. **The clock and the parameters lower only when bound.** A `Now` with no clock pinned, and a parameter
   the bindings do not hold, place the boundary; the step then refuses locally exactly as the reference
   does (`UnpinnedClock`, `UnboundParam`). A slot bound to a cell of the wrong shape is `MayRefuse`.
8. **The dialect seam is a record of functions** (`seam:Fuaran.DB.Provider.Dialect`): quote an identifier,
   write a parameter marker, render a function (scalar functions, operators and aggregates, over rendered
   arguments), render paging (an ordering with nulls last, then a count and an offset), map a column type,
   and declare what it refuses (`Construct -> string option`, asked before each construct is emitted; a
   refusal places the boundary). No dialect ships in this phase. *Reopen* the record's shape when the first
   dialect finds a construct it cannot spell through it; it rides the draft until then.
9. **The refusal union has a case per reason**, nineteen, and three of them refuse the pipeline whole
   (an embedded source, an unmodelled source, a source with no columns); the rest place the boundary.
   Each names the step and what the model or the bindings do hold, and `Refusal.describe` names the rule.
10. **The placement laws are the validator** (`validator:Fuaran.DB.Provider.PlacementLaws`), on the shape of
    the compute package's `plannerLaws`: over generated (model, pipeline, table) triples, the statement run by
    an EXECUTOR the caller supplies, followed by the local suffix, equals the reference evaluator over the
    pipeline as written, the same table on `Ok` and the same `EvalError` on `Error`; every statement is built
    from the model and its parameters alone; the report accounts for every planned step; lowering is pure;
    nothing throws. Sample adequacy demands every refusal (the dialect's own only when it refuses something
    the sample draws), every lowered verb and join kind, every side of the split, a keyless aggregate over no
    rows, a refusal from the reference and one from the statement. The executor is a function, so the laws
    open no connection: the suite's reference dialect interprets the form in memory, with SQL's relational
    meaning and the reference's scalar and aggregate semantics, and an engine-backed run is Phase 1901's
    kit. The parity law was seen red over an engine that orders nulls first and over one that answers a
    keyless aggregate over no rows, and the request-text law over a scan that named model names; those
    falsifiers are permanent tests.

## 2026-10-11 — D3: the licence is Apache-2.0

**Decided by the operator, 2026-10-11**, closing the licence half of D1.1 when Phase 1899 closed.

1. **Apache-2.0.** `LICENSE` and `NOTICE` are at the root, and every package carries
   `PackageLicenseExpression` from `Directory.Build.props`, so a new package inherits it.
2. **Visibility is unchanged.** The repository stays private; making it public, and publishing any package
   beyond a local feed, are separate and deliberate acts.

## 2026-10-11 — D2: the dataset model (Phase 1899)

The first package, `Fuaran.DB`: the domain's artefact as a typed tree, its ops, its validators, the check of a
pipeline against it, seeding and drift, a canonical wire and an AI-tools surface. Ten rulings, each with what
would reopen it.

1. **The artefact is the dataset model, as a closed union over a homogeneous tree.** Eight kinds: model,
   source, table, column, join, measure, row policy, limit. Every node is `{ Id; Kind; Children }`, so Core's
   generic tree witness addresses and rebuilds it, and the containment grammar is data (`model` holds sources,
   joins, measures and limits; `source` holds tables; `table` holds columns, row policies and limits; the rest
   are leaves). Every match over the union is exhaustive with no catch-all, and `FS0025` is an error, so a new
   kind stops the build at each arm that must decide it. *Reopen if* a kind needs children the grammar cannot
   express as a parent-to-child list.
2. **Identity is permanent.** A node's `Id` never changes; a rename is an `Amend` that keeps it; a retired id is
   never minted again (`ModelState.Retired`), so an id in any later record names one node. References between
   nodes — a join's tables and key columns, a measure's column — are by id, so a rename breaks none of them. A
   row policy's predicate is the exception: it is a `ColExpr` over the table's *model* column names, the names
   a pipeline reads, so a rename that strands it is reported (`DB-POLICY-COLUMN`) rather than silently
   followed. *Reopen if* predicates need to survive renames, which would mean an id-addressed expression form.
3. **The op vocabulary is add, amend, retire.** `Add` appends a node, with any subtree, under a parent;
   `Amend` replaces a payload with one of the same kind; `Retire` removes a subtree. The reducer refuses an
   unknown address (naming the ids that exist), a duplicate or retired id, a child the parent's kind may not
   hold (naming what it may), a kind change, and retiring the root. A retirement does **not** cascade: a join
   or measure left pointing at a retired node is reported by the rules (`DB-JOIN-DANGLING`,
   `DB-MEASURE-DANGLING`), so what a retirement strands is visible and reviewable. *Reopen if* an ordered
   insert is needed; `Add` appends only.
4. **No second query language.** Row policies are `ColExpr`, measures name an `AggFn`, and a pipeline is a
   `Transform list`, all from the compute packages, unchanged. What a step *reads* is computed here
   (`Reads.ofStep`, `Reads.ofExpr`) by closed matches over the algebra, because the compute package's own
   reads walk is private; the closed match means a verb the algebra gains stops this build until it is decided
   what it reads. *Reopen when* the compute package publishes a reads walk: this one is then replaced, not
   kept beside it.
5. **The model rules are a Core validator registry with named codes.** Containment (`TREE-ILLEGALCHILD`),
   then `DB-ROOT-NOT-MODEL`, `DB-CLASSIFICATION-UNDECLARED`, `DB-LIMIT-NONPOSITIVE`, `DB-NAME-DUPLICATE`
   (table names model-wide, column names per table), `DB-POLICY-COLUMN`, `DB-JOIN-DANGLING`,
   `DB-JOIN-KEY-FOREIGN`, `DB-JOIN-KEY-TYPE`, `DB-JOIN-NO-KEYS`, `DB-MEASURE-DANGLING` and `DB-MEASURE-TYPE`.
   A measure's type rule is the evaluator's own: `sum`, `mean`, `median` and `stddev` need an int, float or
   decimal column, and every other aggregate takes any. Every family is total over any tree, so a decoded or
   merged tree is checked as surely as one the reducer built.
6. **A pipeline is checked against the model, default-deny** (`PipelineCheck.check`). The pipeline's source
   and every operand must be a modelled table by name; each step's reads must be members of the column set
   `SchemaWalk` derives at that step, seeded from the model's schemas; a join must match a declared relation
   between the joined table and a table already in the pipeline, in either direction, on exactly the declared
   key pairs in model names. Refusals name the step, the column or table, and the members.
   - **An open walk is a refusal at the first read from it.** An open column set can confirm a member and
     never refute one, and an absence must be a fact here. A step that opens the walk (a `Pivot`) is not
     itself refused: its own reads were checked against a closed set, and a pipeline that reads nothing after
     it asks no question the walk cannot answer.
   - **An embedded table is refused** as a source or operand: the check is about what a source holds.
   - **A sort key bound at run time is refused until substituted**: a request never chooses an identifier the
     model has not vouched for. Substituted, it is checked like any other read.
   - **A key renamed by a `Project` before a join refuses the join.** Conservative by construction: matching
     through a rename would need column provenance the walk does not carry.
   *Reopen if* a later phase needs provenance through projections; that is a walk extension, not a relaxation.
7. **Seeding and drift work in physical names.** `Drift.seed` builds one source of tables and columns from a
   reported schema, named as the source names them, under one declared classification, with ids derived from
   source, table and column, so seeding twice mints the same ids. Relations, measures, policies and limits are
   authored, never inferred. `Drift.check` gives one verdict per table: `InStep`, `Drifted` with
   `Schema.diff`'s delta over physical names, `MissingAtSource`, or `Unmodelled`. An unmodelled table is
   reported and is not drift: a model need not name everything a source holds.
8. **The wire is canonical, versioned and strict.** A state is `{"$type":"datasetModel","version":1,"root",
   "retired"}`; nodes and ops are `$type`-tagged, keys sorted by Core's canonical render, so equal values
   render to equal bytes. A container always writes `children`, a leaf never does; an unknown field, a missing
   field, an unknown kind, column type or aggregate, a malformed predicate and any other version are each a
   named refusal (`DB-WIRE-*`). The conformance corpus in `wire-fixtures/` follows the manifest pattern: the
   manifest's counts are authoritative and self-enumerated, accept vectors re-encode to their own bytes, and
   reject vectors carry their code.
9. **The AI-tools surface is Core's `AiSurfaceWitness`.** Four read tools (`outline`, `tables`, `relations`,
   `defects`), the three op kinds, one fast-path pattern (retire by id), the reducer, and guidance that names
   alternatives. The policy denies an op whose payload breaks a rule that needs no model to decide (a
   non-positive limit, a join with no keys), puts every retirement up for approval, and allows the rest.
10. **Dependencies are released Core and compute packages from the public feed, and nothing else.**
    `Fuaran.Core.*` 0.36.0 and `Fuaran.Compute.DataFrame` 0.38.1, pinned in the project files;
    a fresh restore from nuget.org alone resolves the whole graph. A test reads every project file in the
    repository and goes red on any reference to a `Fuaran.UI.*`, `Fuaran.Program.*` or `ToolUp.*` package or
    project. *Reopen only* by a decision that names the coupling and why a seam cannot carry it.

11. **One version, one pack script.** `<Version>` lives in `Directory.Build.props` and nowhere else;
    `pack.ps1` packs `Fuaran.DB` into a local folder feed (by default the shared one three directories up)
    and refuses to rewrite a slot already holding that version unless told to (`-QuietRepack` by hand,
    `-Sweep` from a multi-package sweep). A folder feed keys on id and version, so a silent same-version
    repack swaps the contract under every consumer pinned to it.

## 2026-10-03 — D1: the repository

**Decided by the operator, 2026-10-03.** The domain gets a repository of its own, private for now, in the
organisation that holds the data vertical.

1. **Private, and authored to public standard from the first commit.** No private project, product or
   workspace name appears in any file or commit message. The licence is not yet chosen; choosing it, and any
   change of visibility, are separate and deliberate acts.
2. **The workspace baseline.** `main` from the first commit; F# 10 on .NET 10, the SDK pinned in
   `global.json`; Fantomas pinned as a local tool; `run.ps1` at the root as the one-command entry point and
   `verify.ps1` as the gate. Until the solution exists, `run.ps1` states that nothing was built or tested.
3. **The design decisions are not restated here yet.** The domain's proposed decisions (the dataset model as
   the artefact, no second query language, a pure provider, policy before lowering, the connection record,
   read-only first) are recorded here as each lands with the code that implements it.
