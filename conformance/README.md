# The conformance fixture set

What `Fuaran.DB.Conformance` certifies a dialect on, as emitted by the repository's suite
(`dotnet run --project tests/Fuaran.DB.Tests -- --write-conformance`). Every file is stamped with the kit
version it was emitted by, and the suite fails when a file here is not what the kit emits.

- `manifest.json` names every fixture.
- `seed.json` is the seed: the dataset model, and each table's rows keyed and named by physical identifier,
  in the column codec's canonical encoding.
- `fixtures/<id>.json` is one fixture: its source table, its pipeline with the bindings substituted (a
  binding is a value, and a value becomes a statement parameter either way), whether its row order is part
  of its answer, the differences it may show (`pins`), the construct it exists to reach where it reaches
  one, and the reference evaluator's answer over the seed. A fixture whose pipeline joins a grouped operand
  (`DECISIONS.md` D16) also carries `operands`: each a `name` the join names as its source, the `source` table
  it reads, and its `pipeline` with the bindings substituted. The operand means the reference's answer to that
  pipeline over that table, so a host resolves the join's source by evaluating it.

A host in another language seeds the tables, lowers each pipeline through its own provider and dialect,
runs the statement, finishes the suffix, and compares with `expected`: in order when `ordered` is true, as a
bag otherwise. An answer that differs is differing as pinned only when it is one a declared pin predicts.
