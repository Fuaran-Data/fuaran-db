# Fuaran.DB — decisions (newest first)

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
