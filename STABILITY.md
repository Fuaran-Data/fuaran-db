# Fuaran.DB — API stability

**Status:** pre-release, on the `0.x` line. The package version is single-sourced from `<Version>` in
`Directory.Build.props` (which `pack.ps1` reads). 0.1.0 is tagged and published on nuget.org, so it names one
contract and is never repacked. The standing version is the one headed DRAFT below: untagged and pinned by
no public consumer, it is a **draft slot**, which an additive change rides and a change of a higher class
advances.

## Versioning policy

Semantic versioning from `1.0.0`. Through the `0.x` line a breaking change advances the minor and an
additive one rides the standing draft. A version that has been tagged, or that a consumer pins from a
public feed, names one contract and is never repacked: a change after that point is a new version.

## What is a contract

- **The public F# surface of `Fuaran.DB`**: the node kinds and their specs, `ModelState`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError`, `TableDrift`, the reported-schema records, the
  policy's `GrantLevel`, `Grant`, `Subject`, `ColumnRef`, `PolicySite`, `CostLimits`, `PolicyRefusal`,
  `PolicyRule` and `Admitted`, the projection's `ProjectedModel`, `ProjectionRefusal`, `ProjectionChange`
  and `ProjectionDrift`, the fingerprint's `FingerprintMethod`, `FingerprintKind`,
  `FingerprintDisclosure`, `TableReading`, `FileReading`, `FingerprintReading` and `FingerprintVerdict`,
  and the functions of `ModelNode`, `LimitSpec`, `Model`, `ModelOps`, `Reads`, `ModelRules`,
  `PipelineCheck`, `Policy`, `ModelProjection` (with `ModelProjection.Laws` and its `Case`), `Fingerprint`,
  `Drift`, `ModelWire` and `AiTools`.
- **The public F# surface of `Fuaran.DB.Provider`**: `Construct`, `Scalar`, `Output`, `Pick`,
  `Relation`, `Parameter`, `Statement`, `Dialect`, `Refusal`, `Bindings`, `SplitReport`, `Lowered`,
  `PlacementCase`, `TableProbe`, `Probe`, `ProbeRefusal`, and the functions of `Relation`, `Statement`,
  `Render`, `Refusal`, `Bindings`, `FoldSplit`, `PlacementLaws` and `Probe`. Every package in the repository is cut at the one version.
- **The public F# surface of `Fuaran.DB.Session`**: `ResultDigest`, `SessionHeader`, `StatementRecord`,
  `WithheldPart`, `SessionOp`, `SchemaEntry`, `Guard`, `Verdict`, `Split`, `Recorded`, `Outcome`,
  `RequestRecord`, `SessionState`, `SessionRejection`, `StatementIdentity`, `SessionReadFault`, `Execute`,
  `RunFailure`, `Session`, `Account`, `ReplayVerdict`, `Signer`, `SignedSession`, and the functions of
  `SessionOps`, `SessionHash`, `Recording`, `SessionWire`, `Session`, `Export` and `StreamLaws`.
- **The public F# surface of `Fuaran.DB.Dialect.SqlServer`** (the `SqlServer` module), **of
  `Fuaran.DB.Dialect.PostgreSql`** (the `PostgreSql` module) **and of
  `Fuaran.DB.Conformance`** (`Pin`, `Fixture`, `HostFailure`, `Host`, `Verdict`, `FixtureReport`,
  `Report`, and the functions of `Fixtures` and `Kit`). `Pin` and `Verdict` are closed unions, closed on
  purpose, as above.
- **The closed unions are closed on purpose.** A case added to `NodeKind`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError`, `TableDrift`, `GrantLevel`, `PolicySite`,
  `PolicyRefusal`, `PolicyRule`, `ProjectionRefusal`, `ProjectionChange`, `FingerprintMethod`, `FingerprintKind`, `FingerprintReading`,
  `FingerprintVerdict`, `ResultDigest`, `WithheldPart`, `SessionOp`, `Verdict`, `Recorded`, `Outcome`,
  `SessionRejection`, `SessionReadFault`, `RunFailure`, `ReplayVerdict`, `Construct`, `Scalar`, `Relation`, `Pick`, `Refusal`, `Probe` or `ProbeRefusal` breaks every exhaustive match a consumer has written, so it is a breaking change,
  never an additive one, whatever it looks like. A function added to the `Dialect` record breaks every
  dialect, for the same reason.
- **A field added to a record** breaks every full-literal construction of it, and is breaking too.
- **The wire format, version 1** (`ModelWire.Version`): the envelope, the `$type` tags, the field
  names, and canonical rendering. The corpus in `wire-fixtures/` is its executable statement; a change
  that moves a vector's bytes is a new wire version.
- **The codes**: every `DB-*` rule code in `ModelRules.codes`, every policy refusal code in
  `Policy.codes`, every `DB-WIRE-*` code from `ModelWire.code`, every `DB-PROBE-*` code from `Probe.code`,
  `DB-PROJECTION-WITHHELD` from `ModelProjection.code`, every `DB-SESSION-*` code from `SessionOps.code`, `SessionWire.faultCode` and a recorded `Failed` op,
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
- **What the record holds, and the session wire.** The recording rule (DECISIONS.md D9.2 to D9.4) is part
  of the security posture: recording a value the rule withholds, or a plain digest where it commits one, is
  a change to the posture and needs a decision first. The session wire (op tags, member names, canonical
  rendering, SHA-256 chain) is stated by `wire-fixtures/session/`, and moving a vector's bytes is a new wire
  version. The statement hash's pre-image is a contract: changing it changes every recorded hash.
- **What a projection carries, and what it admits.** What `ModelProjection.project` keeps and drops
  (DECISIONS.md D10.1 to D10.3) is part of the security posture: carrying anything more is a change to the
  posture and needs a decision first, and so is making `ModelProjection.admits` admit a pipeline it
  refused. The projection wire (`ModelProjection.Version`, the `modelProjection` envelope and its grant
  tags) is stated by `wire-fixtures/projections/`; moving a vector's bytes is a new wire version, and it
  moves every pinned projection's hash.
- **What lowers, and the text it lowers to.** The declared subset (DECISIONS.md D4.4) and the shape of
  the statement's form are a contract: lowering less moves work from the database to the host, and
  lowering more is a claim of parity the placement laws must hold and a decision records first. A change
  to the text a form renders to is classed with the form's own changes.

## Versions

### 0.2.0 — DRAFT

A statically typed `Derive` lowers, and so does a grouping keyed by it (Phase 33, DECISIONS.md D15). 0.1.0 is
tagged, so the change cannot ride it, and two closed unions gain a case, so on the `0.x` line it advances the
minor. Built against the same line as 0.1.0, `Fuaran.Core.*` 0.42.0 and `Fuaran.Compute.DataFrame` 0.44.0.

- **Breaking — `Relation` gains `Derive`**, a select of computed columns (D15.3), and **`Scalar` gains
  `Cast`**, a conversion along a pair the declared subset admits (D15.2). A closed union gaining a case
  breaks every exhaustive match a consumer has written, a dialect's included if it matches the form.
  `Render.text` renders both; no field is added to the `Dialect` record, and a dialect renders a cast
  through `RenderFunction (Construct.Cast ty)`, which it was already total over.
- **The posture, widened and decided (D15)**: the declared subset lowers more. A `Derive` whose column's type
  the schema decides, whose expression is in the subset and which the totality verdict admits is part of
  the statement, and so is a grouping keyed by its column. `Cast` lowers to the operand's own type, from a
  null literal, and from `int` to `float` or `bool` and `bool` to `int`, in a filter as in a derive. A
  derive whose type is undecided places the boundary with the refusal it had, `VerbNotLowered`.
- **The posture, unchanged where it was refused**: every construct D4.4 excludes is still excluded, with its
  refusal name, and the rest of the casts stay local as `ConstructExcluded(Cast ty)`.
- **Both dialects' refusals are narrowed**: `refuses (Construct.Cast ty)` admits `int`, `float` and `bool`
  and refuses every other target by name, where it refused every cast. A dialect that renders through
  `render` spells `CAST(x AS <engine type>)`. The type maps are unchanged.
- **Additive — `FoldSplit.derived`**, each lowered derive's planned step and column; `FoldSplit.describe`
  says each in a sentence, and `Refusal.describe` names the undecided type for a derive.
- **Additive — the placement laws**: a law that a grouping keyed by a derived column equals the reference
  grouping by that expression, with its own sample adequacy; the lowered-verb adequacy now requires a
  `derive`. `PlacementLaws.laws` answers two more `LawResult`s, so a consumer reading results by position
  should read them by law.
- **Additive — the conformance set**: `Fixtures.derives`, 22 fixtures, take the set to 91. Every existing
  fixture keeps its content; every emitted file is re-stamped with the kit version, 0.2.0.

### 0.1.0

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

Riding the draft (Phase 1903), on the same terms: the connection record. One change is BREAKING in shape and
is recorded as such. It would advance the version on a released slot.

- **Breaking — `ModelSpec` gains `Recordable: string list`** (DECISIONS.md D9.1), the classifications whose
  values a connection record may hold. A full-literal construction stops compiling, and `Recordable = []`
  is the model that marks nothing recordable, which is what every model was before.
- **Additive — the wire**: a `model` node may carry `recordable`, written only when it names a
  classification. A model naming none renders to the bytes it rendered before, so every existing vector is
  unchanged; `ops/08-amend.json` is the new one. A present empty array is refused (`DB-WIRE-WRONG-SHAPE`).
- **Additive — the reading helpers**: `WireDecode` (the strict field readers the model codec already used,
  now public so the session codec reads through the same ones) and `ModelWire.fingerprintJson` and
  `fingerprintOf`.
- **The posture, tightened**: a new rule code, `DB-RECORDABLE-UNDECLARED`.
- **Additive — the new package `Fuaran.DB.Session`** (the surface listed above): the op vocabulary and its
  reducer, the statement hash and capture key, the recording rule, the canonical session wire with its
  reader, the recorder (`Session.start`, `reportSchema`, `request`, `lower`, `guard`, `run`), the reader
  (`account`), the capture and replay (`captures`, `replayExecutor`, `replay`), the export (`Export.wire`,
  `statement`, `pae`, `sign`, `envelope`) and the laws (`StreamLaws.laws`). The corpus gains
  `wire-fixtures/session/`: one op per kind, two worked streams, and nine reader refusals.

Riding the draft (Phase 1907), on the same terms: the model projected per peer. Nothing is BREAKING in
shape; every change is an addition, and the dependency set grows by one released package.

- **Additive — the projection**: `ProjectedModel`, `ProjectionRefusal`, `ProjectionChange` and
  `ProjectionDrift`, and the `ModelProjection` module (`project`, `fault`, `admits`, `code`, `describe`,
  `Version`, `encodeJson`, `encode`, `hash`, `decodeJson`, `decode`, `isBreaking`, `drift`) with its laws
  (`ModelProjection.Laws`: `Case`, `classifications`, `sample`, `verdictTags`, `shapeTags`, `laws`).
- **Additive — the policy**: `Policy.columns`, the column authorisation alone, which `Policy.admit` now
  runs first, and `Policy.level`, how far a grant list reaches a classification. `Policy.admit` admits and
  refuses exactly what it did, in the same order.
- **Additive — the wire**: a second envelope, `modelProjection`, version 1. The corpus gains
  `wire-fixtures/projections/`: three accept vectors and eight refusals. Every existing vector, and the
  emitted `conformance/` set, keeps its bytes.
- **Additive — a dependency**: `Fuaran.DB` now references the released `Fuaran.Core.Conformance` 0.36.0,
  which the provider and the session package already reference, for the laws' generator and adequacy
  guard.

Riding the draft (Phase 1906), on the same terms: the second dialect. Three changes are BREAKING in shape
and recorded as such; each would advance the version on a released slot.

- **Breaking — the `Dialect` record gains `ReadValue: ColumnType -> string -> string`** (DECISIONS.md
  D11.1), how a value of a type is read into the statement: the renderer applies it to every scanned column
  and every parameter marker. Every dialect value must name it; identity keeps a dialect's text unchanged,
  and the SQL Server dialect's text is unchanged.
- **Breaking — `Render.text` changes signature**, from `Dialect -> Relation -> string` to
  `Dialect -> ColumnType list -> Relation -> string`: the statement's parameter types, in ordinal order,
  so each marker is read as its type. `FoldSplit` passes them; the statement's `Parameters` and their
  `Marker` are unchanged.
- **Breaking — `Pin` gains `CodePointOrder`** (D11.3), with its prediction in `Kit.predicted`. A closed
  union gaining a case breaks every exhaustive match.
- **Additive — the new package `Fuaran.DB.Dialect.PostgreSql`**: `PostgreSql.dialect`, its parts (`quote`,
  `marker`, `collation`, `mapType`, `columnType`, `readValue`, `refuses`), and what a host needs to bind
  and read (`parameterValue`, `decodeCell`, `createTable`, `dropTable`, `insertRow`). Its refusals and its
  type map are part of its contract, on the SQL Server dialect's terms.
- **Additive — the conformance set**: the seed gains the table `words` and the bindings `p_private_use`;
  two fixtures, `difference-collation-order` and `difference-code-point-order`, take the set to 64. Every
  existing fixture file, and every existing table's encoding in `seed.json`, keeps its bytes (D11.4).

Riding the draft (Phase 2), on the same terms: a modelled column is a Core field (DECISIONS.md D12).
Its changes are BREAKING in shape and recorded as such; each would advance the version on a released
slot.

- **Breaking — the pins move to `Fuaran.Core.*` 0.37.0 and `Fuaran.Compute.DataFrame` 0.40.0** (D12, the
  closing paragraph). Core's `Schema` is a `Field list`, so every surface here that takes or answers a
  `Schema` (`Model.schemaOf`, `physicalSchemaOf`, `schemas`, `ReportedTable.Columns`, `Statement.schema`,
  `SchemaDelta` in `TableDrift.Drifted`) now carries fields, not `(name, type)` pairs.
- **Breaking — `ColumnSpec` is `{ Field; Physical; Classification }`.** A full-literal construction stops
  compiling; `ColumnSpec.create name physical ty classification` builds the column the old literal did, and
  `c.Name` and `c.Type` still read. Additive beside it: `ColumnSpec.rename`, `retype`, `renameField` and
  `physicalField`.
- **Breaking — `PipelineRefusal` gains `UnitMismatch`, `WireError` gains `BadField` and
  `ProjectionChange` gains `ColumnAmended`.** A closed union gaining a case breaks every exhaustive match.
  The new codes are `DB-WIRE-BAD-FIELD` and, through `Policy.admit`, the pipeline refusal it already
  carried.
- **The posture, tightened — the pipeline check runs the evaluator's unit check** (D12.5): a pipeline
  that combines or compares two different stated units, in a filter, a derive, a join key or a set
  operation, is refused before anything is lowered. A model that states no unit admits exactly what it
  admitted before.
- **Additive — the wire**: a `column` node may carry `unit`, `label`, `description` and `ext` in Core's
  field spelling, each written only when stated, and a reported schema in the connection record carries
  them the same way. A column stating none renders to the bytes it rendered before, so every existing
  vector is unchanged; `ops/09-amend.json` and the reject `column-unit-unreadable` are the new ones. A
  decoder older than this draft refuses a column that states any of them, by name.
- **Additive — drift and the AI surface**: seeding keeps a reported field's metadata; `Drift.check` reports
  a unit, label or description the source states differently through `SchemaDelta.Amended`, and never one
  it does not state; the `tables` read tool names a column's unit, label and description where stated.

Riding the draft (2026-10-24), on the same terms: the pins move to Core 0.40.3 and Compute 0.41.0
(DECISIONS.md D12, the amendment).

- **Breaking — the pins move to `Fuaran.Core.*` 0.40.3 and `Fuaran.Compute.DataFrame` 0.41.0.** Compute's
  `ScalarFn`, which `Construct.Function` and `Scalar.Function` carry, gains `Pow`, `Exp`, `Ln` and `Log10`,
  so an exhaustive match over a construct breaks. The law families answer Core 0.40's `LawResult`, which
  carries `Family`, `Kind`, `Evidence` and `Reached`.
- **The posture, tightened — both dialects refuse `Pow`, `Exp`, `Ln` and `Log10` by name.** A pipeline
  using one lowers the prefix before it and evaluates the rest locally.
- **Additive — the conformance set**: four fixtures, `reach-pow`, `reach-exp`, `reach-ln` and
  `reach-log10`, take the set to 68. Every existing fixture file keeps its bytes.
- **Breaking — `createTable` answers `Result<string, string>`** in both dialects, refusing a column no
  engine type holds exactly by name. A host that sends the text straight to its engine now matches first.
- **Additive — `typeRefusal`** in both dialects: why a column type has no exact engine type. A nanosecond
  timestamp is the one case.
- **The posture, changed — a timestamp's DDL follows its unit** (D12, the second amendment): `datetime2(3)`
  and `(6)`, `timestamp(3)` and `(6)` for milliseconds and microseconds, where every timestamp was
  seconds; a nanosecond timestamp is refused as a column and as a parameter. A seconds timestamp's DDL is
  unchanged.
- **Breaking — `Output` is `{ Alias; Field }`** (D12, the third amendment). A full-literal construction
  stops compiling; `o.Name` and `o.Type` still read, through the field. `Statement.schema` and
  `Statement.toTable` now state each column's unit, so the local suffix re-checks a pushed-down result's
  units.
- **The posture, tightened — `DB-JOIN-KEY-UNIT`**: a declared join whose key columns state different units
  is a model defect. A model that states no unit, or one unit on both sides of each key, is unaffected.
- **The posture, changed — a reported timestamp keeps its unit** (D12, the fourth amendment) in both
  dialects' `columnType`: `datetime2(n)` and `timestamp(n)` read as the coarsest unit holding `n` digits,
  a bare `datetime2` as nanoseconds, a bare `timestamp` as microseconds, `datetime` as milliseconds. Each
  read as seconds before, so a model seeded from such a source sees a retype at its next drift check.
- **The posture, changed — a timestamp value travels at its column's unit**: `parameterValue` binds a
  sub-second timestamp exactly (it threw), and `decodeCell` keeps the engine's fraction at the column's
  unit (it truncated to seconds), refusing a value finer than the unit as a named `TypeError`. Signatures
  are unchanged; a seconds timestamp binds and decodes as before.
- **Breaking — `StatementRecord.Columns` is a `Field list`** (D12, the fifth amendment), where it was
  `(string * ColumnType) list`: the statement's schema, units included. A full-literal construction stops
  compiling; `Field.create name ty` builds the column the old pair did. `Session.replay` now runs the
  recorded suffix's unit check over the recorded units.
- **Additive — the connection record's wire**: a statement's result column is Core's field document and
  may carry `unit`, `label`, `description` and `ext`, each written only when stated. A column stating none
  is written as before, so every existing vector keeps its bytes, and a record written before this draft
  still reads (`session/legacy/`). The new vectors are `session/streams/measured.jsonl` and
  `session/measured/ran.json`. A decoder older than this draft refuses a result column that states any of
  them, by name.

Riding the draft (2026-11-05), on the same terms: the Compute pin moves to 0.43.0 (DECISIONS.md D13).

- **Breaking — the pin moves to `Fuaran.Compute.DataFrame` 0.43.0.** Compute's `Transform` gains `Define`,
  `Fit` and `Predict`, its `ColExpr` gains `Arg`, `Call` and `Interpolate`, and its `EvalError` gains six
  cases, so an exhaustive match over any of them breaks.
- **Breaking — `Construct` gains `Interpolation`**, outside the declared subset; both dialects refuse it
  by name. A closed union gaining a case breaks every exhaustive match.
- **Additive — the conformance set**: `reach-interpolation` takes the set to 69. Every existing fixture file
  keeps its bytes.
- **Additive — `Reads.ofPredictModel`**: the columns a prediction reads from its model's source.
- **The posture, widened and decided (D13)**: the pipeline check and `Policy.admit` admit the new verbs
  and expressions under the rules D13 records — a fit's and a prediction's reads used row by row, a
  prediction's model table checked, policed tables refused as a model, a prediction counted as a join.
  Nothing new lowers.

Riding the draft (Phase 32), on the same terms: the release slot (DECISIONS.md D14).

- **No surface change — the dependency line stays at `Fuaran.Core.*` 0.42.0 and
  `Fuaran.Compute.DataFrame` 0.44.0**, the newest published, now carried by one property per producing
  repository (`FuaranCoreVersion`, `FuaranComputeVersion` in `Directory.Build.props`). A consumer on the
  0.40.2 / 0.43.0 line raises to it before taking this one. The surface guard, rendering all six packages
  packed at the previous head and at this one, reports every surface unchanged, so the change rides.
- **The release slot is 0.1.0.** Nothing is tagged, nothing is on a public feed and no consumer pins it from
  one, so the draft is released under its own number: the first `v0.1.0` tag publishes the six packages
  (`.github/workflows/publish-packages.yml`), and this heading drops DRAFT in that act, not before. A change landing
  between now and the tag rides or advances by the rule above, as any other.
