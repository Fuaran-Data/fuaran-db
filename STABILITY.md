# Fuaran.DB — API stability

**Status:** pre-release. The package version is single-sourced from `<Version>` in
`src/Fuaran.DB/Fuaran.DB.fsproj`; this document does not restate the number. Nothing has been packed
for release or tagged, so the standing version is a **draft slot**: an additive change rides it, and a
change of a higher class advances it.

## Versioning policy

Semantic versioning from `1.0.0`. Through the `0.x` line a breaking change advances the minor and an
additive one rides the standing draft. A version that has been tagged, or that a consumer pins from a
public feed, names one contract and is never repacked: a change after that point is a new version.

## What is a contract

- **The public F# surface of `Fuaran.DB`**: the node kinds and their specs, `ModelState`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError`, `TableDrift`, the reported-schema records, and the
  functions of `ModelNode`, `Model`, `ModelOps`, `Reads`, `ModelRules`, `PipelineCheck`, `Drift`,
  `ModelWire` and `AiTools`.
- **The closed unions are closed on purpose.** A case added to `NodeKind`, `ModelOp`,
  `ModelRejection`, `PipelineRefusal`, `WireError` or `TableDrift` breaks every exhaustive match a
  consumer has written, so it is a breaking change, never an additive one, whatever it looks like.
- **A field added to a record** breaks every full-literal construction of it, and is breaking too.
- **The wire format, version 1** (`ModelWire.Version`): the envelope, the `$type` tags, the field
  names, and canonical rendering. The corpus in `wire-fixtures/` is its executable statement; a change
  that moves a vector's bytes is a new wire version.
- **The codes**: every `DB-*` rule code in `ModelRules.codes`, every `DB-WIRE-*` code from
  `ModelWire.code`, and the AI surface's tool names and op kinds. A host matches on these; renaming one
  is breaking.
- **What the check admits.** Making `PipelineCheck.check` or the model rules refuse something they
  admitted is breaking for a consumer whose pipelines passed; admitting something they refused is a
  change to the security posture and is recorded as a decision before it ships.

## Versions

### 0.1.0 — DRAFT

The first surface (Phase 1899): the dataset model and its op vocabulary, the model rules, the pipeline
check, seeding and drift, the wire codec with its corpus, and the AI-tools surface. Built against
`Fuaran.Core.*` 0.36.0 and `Fuaran.Compute.DataFrame` 0.38.1.
