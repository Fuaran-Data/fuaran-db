# Fuaran.DB — API stability

**Status:** pre-release. The package version is single-sourced from `<Version>` in
`Directory.Build.props` (which `pack.ps1` reads); this document does not restate the number. Nothing has been packed
for release or tagged, so the standing version is a **draft slot**: an additive change rides it, and a
change of a higher class advances it.

## Versioning policy

Semantic versioning from `1.0.0`. Through the `0.x` line a breaking change advances the minor and an
additive one rides the standing draft. A version that has been tagged, or that a consumer pins from a
public feed, names one contract and is never repacked: a change after that point is a new version.

## What is a contract

- **The public F# surface of `Fuaran.DB`**: the node kinds and their specs, `ModelState`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError`, `TableDrift`, the reported-schema records, the
  policy's `GrantLevel`, `Grant`, `Subject`, `ColumnRef`, `PolicySite`, `CostLimits`, `PolicyRefusal`,
  `PolicyRule` and `Admitted`, the fingerprint's `FingerprintMethod`, `FingerprintKind`,
  `FingerprintDisclosure`, `TableReading`, `FileReading`, `FingerprintReading` and `FingerprintVerdict`,
  and the functions of `ModelNode`, `LimitSpec`, `Model`, `ModelOps`, `Reads`, `ModelRules`,
  `PipelineCheck`, `Policy`, `Fingerprint`, `Drift`, `ModelWire` and `AiTools`.
- **The public F# surface of `Fuaran.DB.Provider`**: `Construct`, `Scalar`, `Output`, `Pick`,
  `Relation`, `Parameter`, `Statement`, `Dialect`, `Refusal`, `Bindings`, `SplitReport`, `Lowered`,
  `PlacementCase`, `TableProbe`, `Probe`, `ProbeRefusal`, and the functions of `Relation`, `Statement`,
  `Render`, `Refusal`, `Bindings`, `FoldSplit`, `PlacementLaws` and `Probe`. Every package in the repository is cut at the one version.
- **The public F# surface of `Fuaran.DB.Dialect.SqlServer`** (the `SqlServer` module) **and of
  `Fuaran.DB.Conformance`** (`Pin`, `Fixture`, `HostFailure`, `Host`, `Verdict`, `FixtureReport`,
  `Report`, and the functions of `Fixtures` and `Kit`). `Pin` and `Verdict` are closed unions, closed on
  purpose, as above.
- **The closed unions are closed on purpose.** A case added to `NodeKind`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError`, `TableDrift`, `GrantLevel`, `PolicySite`,
  `PolicyRefusal`, `PolicyRule`, `FingerprintMethod`, `FingerprintKind`, `FingerprintReading`,
  `FingerprintVerdict`, `Construct`, `Scalar`, `Relation`, `Pick`, `Refusal`, `Probe` or `ProbeRefusal` breaks every exhaustive match a consumer has written, so it is a breaking change,
  never an additive one, whatever it looks like. A function added to the `Dialect` record breaks every
  dialect, for the same reason.
- **A field added to a record** breaks every full-literal construction of it, and is breaking too.
- **The wire format, version 1** (`ModelWire.Version`): the envelope, the `$type` tags, the field
  names, and canonical rendering. The corpus in `wire-fixtures/` is its executable statement; a change
  that moves a vector's bytes is a new wire version.
- **The codes**: every `DB-*` rule code in `ModelRules.codes`, every policy refusal code in
  `Policy.codes`, every `DB-WIRE-*` code from `ModelWire.code`, every `DB-PROBE-*` code from `Probe.code`,
  and the AI surface's tool names and op kinds. A host matches on these; renaming one
  is breaking.
- **What the check admits.** Making `PipelineCheck.check` or the model rules refuse something they
  admitted is breaking for a consumer whose pipelines passed; admitting something they refused is a
  change to the security posture and is recorded as a decision before it ships. The same holds for
  `Policy.admit`, and for each rewrite it applies: dropping a rewrite, or admitting a pipeline it
  refused, is a change to the posture and needs a decision first.
- **Only an admitted pipeline lowers.** `FoldSplit.lower` and `FoldSplit.explain` take the policy's
  `Admitted`, which `Policy.admit` alone constructs. Adding a public entry point that lowers a pipeline
  the policy has not admitted is a change to the posture, never an addition.
- **What a fingerprint discloses.** A method's kind and its disclosed misses are a contract a host trusts
  a schedule to: calling a method exact that was heuristic, or dropping a disclosed miss, is a change to the
  posture and needs a decision first. Adding a disclosed miss or over-report is a correction and rides.
- **What lowers, and the text it lowers to.** The declared subset (DECISIONS.md D4.4) and the shape of
  the statement's form are a contract: lowering less moves work from the database to the host, and
  lowering more is a claim of parity the placement laws must hold and a decision records first. A change
  to the text a form renders to is classed with the form's own changes.

## Versions

### 0.1.0 — DRAFT

The first surface (Phase 1899): the dataset model and its op vocabulary, the model rules, the pipeline
check, seeding and drift, the wire codec with its corpus, and the AI-tools surface. Built against
`Fuaran.Core.*` 0.36.0 and `Fuaran.Compute.DataFrame` 0.38.1.

Additive, riding the draft (Phase 1900): the new package `Fuaran.DB.Provider` — the statement and its
abstract form, the dialect seam, the refusal union, the fold split with `explain`, and the placement
laws. No dialect ships with it. `Fuaran.DB`'s own surface is unchanged.

Riding the draft (Phase 1902), which no feed holds and nothing has tagged or pinned, so no consumer's
contract moves. Two of its changes are BREAKING in shape and are recorded as such: they would advance
the version on a released slot.

- **Breaking — `FoldSplit.lower` and `FoldSplit.explain` change signature**: from
  `model -> dialect -> source -> pipeline -> bindings` to `Admitted -> dialect -> bindings`. The ungated
  lowering is internal to the provider; the placement laws keep their public signature and lower
  through it, so a dialect is certified without being given an ungated entry point.
- **Breaking — two records gain fields**: `LimitSpec` gains the optional `MaxJoins`, `MaxGroupKeys`,
  `MinGroupSize` and `TimeoutSeconds`, and `Statement` gains `TimeoutSeconds`. A full-literal
  construction of either stops compiling; `LimitSpec.rows` builds a limit that declares only its row
  ceiling.
- **Additive — the policy**: `Policy.admit` and its vocabulary (the types listed above, `Policy.codes`,
  `describe`, `attributeRead`, `remedy`, `ruleTag`), and `LimitSpec.fault`, which the model rules and the
  AI surface now share.
- **Additive — the wire**: a `limit` node may carry `maxJoins`, `maxGroupKeys`, `minGroupSize` and
  `timeoutSeconds`, each written only when declared. A limit declaring none renders to the bytes it
  rendered before, so every existing vector is unchanged; `ops/06-amend.json` is the new one. A
  decoder older than this draft refuses a limit that declares any of them, by name.
- **The posture, tightened**: `DB-LIMIT-NONPOSITIVE` now also reports a negative join or grouping cap
  and a group floor or timeout below one.

Riding the draft (Phase 2008), on the same terms: the source fingerprint. Two changes are BREAKING in
shape and recorded as such.

- **Breaking — `SourceSpec` gains `Fingerprint: FingerprintMethod option`.** A full-literal construction
  stops compiling; `Fingerprint = None` is the source that declares none, as every source did before.
- **Breaking — the `Dialect` record gains `ChangeCounter: string option`**, the statement reading the
  engine's own change counter, or `None`. Every dialect value must name it.
- **Additive — the fingerprint**: `FingerprintMethod` and the reading and verdict types above, the
  `Fingerprint` module (`disclose`, `kind`, `trySource`, `ofSource`, `compare`), the provider's `Probe`
  module (`forSource`, `advise`, `readTables`, `code`) with `TableProbe`, `Probe` and `ProbeRefusal`, and
  the AI surface's `sources` read tool.
- **Additive — the wire**: a `source` node may carry `fingerprint`, a typed object (`changeCounter`,
  `rowCountMaxChange` with its `tables` pairs, or `fileStat` with `contentHash`), written only when
  declared. A source declaring none renders to the bytes it rendered before, so every existing vector is
  unchanged; `ops/07-amend.json` is the new one. The kind is never on the wire: it follows from the method.
- **The posture, tightened**: four new rule codes, `DB-FINGERPRINT-DANGLING`, `-FOREIGN`, `-TYPE` and
  `-COVERAGE`, refuse a row-count fingerprint that names what the model does not hold, reaches outside its
  source, reads a change column of a type that does not advance, or leaves a table out.

Riding the draft (Phase 1901), on the same terms: the first dialect and the conformance kit. One change
is BREAKING in shape and recorded as such; it would advance the version on a released slot.

- **Breaking — the `Dialect` record gains three fields** (DECISIONS.md D8.1, the reopening D4.8 named):
  `RenderAggregate: AggFn -> ColumnType -> string -> string`, which renders an aggregate given the type of
  the column it aggregates, and `ValueAsCondition` and `ConditionAsValue: string -> string`, which convert
  between a boolean value and a condition for an engine with no boolean type (identity for one with it).
  Every dialect value must name them. `RenderFunction` is no longer called for an aggregate, and the
  renderer now places each scalar in a condition or a value position; for a dialect whose two conversions
  are identity, every statement's text is unchanged.
- **Additive — the new package `Fuaran.DB.Dialect.SqlServer`**: `SqlServer.dialect`, its parts (`quote`,
  `marker`, `mapType`, `columnType`, `refuses`), and what a host needs to bind and read (`parameterValue`,
  `decodeCell`, `createTable`, `dropTable`, `insertRow`). Its refusals and its type map are part of its
  contract: refusing more moves work to the host, and mapping more is a claim of parity the conformance
  kit must hold first.
- **Additive — the new package `Fuaran.DB.Conformance`**: `Pin`, `Fixture`, `HostFailure`, `Host`,
  `Verdict`, `FixtureReport`, `Report`, the `Fixtures` module (the seed and the fixtures) and the `Kit`
  module (`version`, `subject`, `expected`, `agrees`, `predicted`, `runFixture`, `runWith`, `run`,
  `counts`, `describe`, the emission). The fixtures are a contract: a fixture removed, or its expected
  answer changed, is a change to what a certified dialect has been shown to agree on, and the emitted set
  in `conformance/` is its executable statement.
