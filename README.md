# Fuaran.DB

**The dataset model: what a source holds, and what may be asked of it.**

A query sent from an analytical module to a database is still an attack surface. This domain closes it
the way a renderer closes a display: the query is data, and the host that owns the connection runs only
what it can check.

- **The artefact is the dataset model**: sources, tables, columns with a type and a classification,
  permitted joins, measures, row policies and limits, as a typed tree with operations, validators, a wire
  format and an op-stream. A table, column or join the model does not name does not exist for any query
  checked against it.
- **There is no second query language.** Queries are pipelines in an existing, closed transform algebra.
- **A pure provider lowers them.** The reducing part of a pipeline becomes one parameterised statement per
  dialect, with identifiers taken from the model and values always bound; the rest runs locally.
- **Policy is decided on the tree, before any statement exists**: column grants, row policies, cost limits
  and minimum group sizes, each a named refusal or a visible rewrite.
- **What passed over a connection is recorded** as its own op-stream, so a published figure can name the
  statement, the schema and the moment it came from.

No package here opens a connection: the host supplies the executor.

## Status

Pre-release. `Fuaran.DB` holds the dataset model: its node kinds and op vocabulary, the model rules, the
check of a pipeline against a model, seeding and drift against a reported schema, a canonical wire codec with
its conformance corpus in [`wire-fixtures/`](wire-fixtures/), and an AI-tools surface. It also holds the
policy: for a subject the host supplies, column grants, row policies, cost limits and a minimum group size
decide on the tree whether a pipeline is admitted, refusing by name or rewriting visibly. A source may declare
a fingerprint a host polls to learn that it moved without reading it, each method disclosing whether it is
exact or heuristic and what it can miss; the provider writes each dialect's probe for it.
`Fuaran.DB.Provider` lowers an admitted pipeline, and nothing else: the dialect seam, the fold split that
lowers the longest reducing prefix to one parameterised statement and runs the rest locally, the named
refusals that set where the split falls, and the placement laws a dialect is certified against. No dialect ships yet.
[`DECISIONS.md`](DECISIONS.md) records the decisions taken so far, and [`STABILITY.md`](STABILITY.md) what
is a contract.

## Building

Requires the .NET SDK pinned in [`global.json`](global.json).

```powershell
./run.ps1      # restore tools, format, build, test
./verify.ps1   # the gate
```

## Licence

Apache-2.0. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
