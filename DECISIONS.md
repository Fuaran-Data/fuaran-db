# Fuaran.DB — decisions (newest first)

## 2026-10-21 — D12: a modelled column is a Core field (Phase 2)

A column of the dataset model was a record of its own: a name, a physical identifier, a type and a
classification. Core's schema entry is now a `Field` (fuaran-core#427), carrying a unit, a label, a
description and namespaced extension members, and the evaluator checks units through every pipeline
(fuaran-core#429). A model that kept its own column record would duplicate the first and miss the
second. Five rulings, each with what would reopen it.

1. **A modelled column is a `Field` plus what only the model knows.** `ColumnSpec` is
   `{ Field; Physical; Classification }`. The field's `Name` is the model name, and its type, unit, label
   and description are the column's; `c.Name` and `c.Type` read through it. `ColumnSpec.create`,
   `rename`, `retype` and `physicalField` build and refine one, carrying the metadata across, because
   Core's `Field` has no rename builder. `Model.schemaOf` and `physicalSchemaOf` hand out the fields
   themselves, so whatever reads a model's schema (the pipeline check, drift, the projection) reads the
   units without being told to. *Reopen if* Core's `Field` gains a member the model must not pass on.
2. **The classification stays a member of the column record. It does not ride the field's extension
   map.** Four reasons. The classification is governance the policy reads against a closed, declared
   set (`DB-CLASSIFICATION-UNDECLARED`), and a typed member keeps that check where it is; an extension
   value is a string Core preserves and never interprets. The field is what a pipeline carries into the
   evaluator, and a classification is not meaning a derived column should inherit or a result should
   show. A reported schema never states one, so an extension key would surface in every drift check as
   an `Ext` change Core classifies `Unknown`. And the per-peer projection withholds and rewrites
   classifications on its own terms (D10), which it can only do to a member it owns. *Reopen if* a
   second domain needs to read a column's classification from the field alone, which would mean a
   namespaced key (`fuaran.db.classification`) beside, not instead of, this member.
3. **The wire writes the field's metadata in Core's own spelling, and only when stated.** A `column` node
   carries `unit` (Core's canonical unit text), `label`, `description` and `ext` exactly as
   `ColumnCodec.fieldJson` writes them, and reads them back through `ColumnCodec.decodeField`; a refusal
   there is the new `DB-WIRE-BAD-FIELD`. A column stating none renders to the bytes it rendered before,
   so every existing vector is unchanged and `ops/09-amend.json` is the new one. The connection record's
   reported schema is written the same way, so a reported unit survives a session. The AI surface's
   `tables` tool names a column's unit, label and description where the model states them. *Reopen if*
   Core's field document changes spelling.
4. **Seeding keeps a reported field whole; drift reports only what the source states differently.**
   `Drift.seed` takes each reported field as the column's field, so a reported unit and label are the
   model's from the start. `Drift.check` is still `Schema.diff` over physical names, so a unit, label or
   description change arrives as Core's `SchemaDelta.Amended` beside a retype. Before the diff, a
   reported field's UNSTATED members are filled from the model: a source that reports no unit (most
   catalogues report none) is silent about the unit, not withdrawing it, and is not drift. The
   projection's drift gains `ProjectionChange.ColumnAmended`; a unit change there is breaking, because a
   pipeline the pin admitted can be refused under the new unit, and a label or description change is
   not. *Reopen if* a source can positively report that a column has NO unit; the overlay would then
   need a way to tell "states none" from "says nothing".
5. **A pipeline checked against the model meets the evaluator's unit check, before anything is lowered.**
   `PipelineCheck` asks Compute's `DataFrame.resolveUnits` about every filter and derive, over the fields
   the schema walk carries at that step (seeded from the model, so the model's units are the input's).
   A join's key pairs and a set operation's column pairs are held to the comparison's rule (two stated
   units must be one unit; an unconstrained side matches anything), which is the evaluator's own. A
   refusal is the new `PipelineRefusal.UnitMismatch`, naming the step, what met and both units, and
   `Policy.admit` surfaces it as it surfaces every pipeline refusal, so the provider never lowers a
   pipeline that adds pounds to kilograms. A conversion is an explicit `Convert`. The provider lowers
   none: a conversion is a cast to float times a factor, outside its declared subset, so it runs in the
   local suffix. *Reopen if* the schema walk gains its own unit verdict; this check is then replaced by
   it, not kept beside it.

**The pins move to Core 0.37.0 and Compute 0.40.0, not to Core's newest.** Compute 0.40.0 is the newest
published Compute and the first with the unit check. It was built against Core 0.37.0, the first Core
with `Field`. Its assembly references Core's `Canon`, which Core 0.39.0 removed, so Core 0.40.1 beside
it compiles and then fails to load the expression codec this package's wire uses. The pins move to
Core's newest when a published Compute is built against it. Moving them crosses two other Core breaks,
each handled where it lands: a `Column` is built through `Column.ofCells` (a cell its type cannot hold
is refused, never thrown, on every path that reads recorded or engine data), and `TimestampType` carries
a `TimeUnit`. A reported timestamp is `TimestampType TimeUnit.Seconds`, the unit both dialects' DDL
already spells (`datetime2(0)`, `timestamp(0)`). The finer units map to the same DDL for now, which is
a known gap.

**Amended 2026-10-24: the pins move to Core 0.40.3 and Compute 0.41.0.** This supersedes the closing
paragraph's choice of Core 0.37.0, on the condition it named: Compute 0.41.0, published on nuget.org, is
built against Core 0.40.2, so a published Compute is now built against a Core past 0.39.0's removal of
`Canon`. The `Fuaran.Core.*` pins take 0.40.3, the newest published Core, rather than 0.40.2, because the
full suite and both conformance legs are green on it, and a patch release beside the 0.40.2 Compute was
built on is what the pin rule asks for. The raise crosses three Core breaks and three Compute ones, each
handled where it lands:

- Core's `Canon` is gone (0.39.0): every render is `Canonical.write Rendering.CrossHost` and every tagged
  object `Canonical.typed`, which write the same bytes, so no vector and no emitted fixture moved.
- An op stream is one `Stream` value (0.39.0): the connection record's stream is `SessionWire.stream`, the
  canonical config over SHA-256, the same chain as before, and its wire is `Jsonl`'s.
- `LawResult` gained `Family`, `Kind`, `Evidence` and `Reached` (0.40.0): the laws here build their results
  with `LawResult.subject`, the iteration count as evidence.
- Compute's `ScalarFn` gained `Pow`, `Exp`, `Ln` and `Log10`. Both dialects refuse the four by name, and the
  kit gains a fixture reaching each (`reach-pow`, `reach-exp`, `reach-ln`, `reach-log10`), so the set is 68.
  None is lowered to an engine function: the reference answers null at a domain edge (a zero or negative
  logarithm, a negative base under a fractional exponent) and is host-rounded within 2 ulp, and nothing
  here yet pins an engine to either. Lowering one is a later phase, with a pinned conformance fixture
  showing the engine agrees. D7's survey gains the four as pinned rows.
- Schema-walk knowledge carries an exactness mark (`Pinned | HostRounded`). Nothing here matches on it.
- `Project`, `GroupBy`, `Unpivot` and `Pivot` refuse an output that names a column twice (compute D35).
  The lowering already refused a repeated output name (`Refusal.RepeatedName`, D4), so the two engines
  agree. The two placement-law samples that repeat a name stay as they are: they are the only samples
  that reach `repeatedName`, and renaming them leaves the refusal guard red. A test pins that the lowering
  and the reference refuse both, naming the column.

*Reopen if* a published Compute is built against a Core past 0.40.3; the pins then move again, on the same
terms.

**Amended 2026-10-24: a timestamp column's DDL follows its unit.** This closes the closing paragraph's
known gap. Each dialect spells a timestamp at the fractional digits its unit states: `datetime2(0)`, `(3)`
and `(6)` in SQL Server, `timestamp(0)`, `(3)` and `(6)` in PostgreSQL, for seconds, milliseconds and
microseconds. A nanosecond timestamp is refused by name in both (`typeRefusal`): SQL Server's `datetime2`
keeps seven digits and PostgreSQL's `timestamp` six, so either would lose the last digits. `createTable`
answers a `Result`, refusing the first such column by name, and a nanosecond parameter is a refused
construct, so the provider places it in the local suffix rather than declare a type that truncates it.
Two things stay as they were. A reported timestamp reads as seconds whatever its precision, as this
entry decided. A timestamp cell still travels as seconds text through `parameterValue` and `decodeCell`.
*Reopen if* a cell carries a sub-second value; the value path then follows the unit as the DDL now does.

**Amended 2026-10-24: units reach the model rules and the provider's result.** Two places D12.5 left
unit-blind now see units.

- A declared join whose key columns state different units is a model defect, `DB-JOIN-KEY-UNIT`, beside
  `DB-JOIN-KEY-TYPE`, on D12.5's rule: two stated units must be one unit, and a side stating none matches
  anything. Such a model was refused by every pipeline check that used the join; it is now refused once,
  where the author declared it.
- A statement's output column carries its field (`Output.Field`), so `Statement.schema`, and the table
  `Statement.toTable` reads a result into, state each column's unit. A scanned column carries the model's
  field, a projected or a join's renamed one keeps its unit under the new name, and an aggregate states
  the unit compute's `aggregateUnit` gives it (a sum, mean, minimum or maximum its operand's; a count
  none). The local suffix then runs the evaluator's unit check over a pushed-down result, as it does over
  the model's rows, so the two engines check units alike. Admission still refuses a mixed-unit pipeline
  first; this is the second check, not a replacement. The connection record still writes a statement's
  columns as name and type, so a replayed result states no unit; the wire is unchanged.

## 2026-10-12 — D11: PostgreSQL, the second dialect (Phase 1906)

`Fuaran.DB.Dialect.PostgreSql` is the second dialect. Its purpose is to find what the first left in the
provider. Six rulings, each with what would reopen it.

1. **One assumption moved behind the seam: a value entered the statement as the source declares it.** The
   renderer wrote a scanned column as its bare quoted identifier and a parameter as its bare marker. So the
   engine compared, grouped and ordered every string under the column's own collation, or under the
   database default for a parameter. SQL Server's `CaseInsensitiveCollation` pin absorbed this, so it did
   not show. PostgreSQL's default collation is linguistic: it puts `a` before `B` where the reference
   (ordinal, probed) puts `B` first. Pinning that would mean predicting every locale's order. So the
   dialect re-declares the order instead. The `Dialect` record gains `ReadValue: ColumnType -> string ->
   string`, which the renderer applies to every scanned column and every parameter marker. To type the
   markers, `Render.text` gains the statement's parameter types. Both changes are breaking on the 0.1.0
   draft (STABILITY.md). SQL Server and the reference dialect implement `ReadValue` as identity, so their
   text is unchanged. PostgreSQL reads a string `COLLATE "C"`. Exposed by `difference-collation-order`
   (the first three names: `1, 3, 5` by code unit; `1, 2, 3` under a case-insensitive or linguistic
   order). `difference-code-point-order` exposes it too.
   **Considered and kept in the core, each with why:**
   - The relational spellings the renderer writes are the same in both engines. These are the derived
     tables with `AS`, the four join keywords, `EXISTS` and `NOT EXISTS` for the filtering joins,
     `COALESCE`, `CASE`, `IN`, `IS NULL`, `NOT`, and `HAVING COUNT(*) > 0`.
   - Paging, booleans and typed aggregates are already the dialect's (D8.1). PostgreSQL uses its native
     `NULLS LAST`, identity conversions, `bool_and` and `bool_or`, and `avg` over a double.
   - The provider clamps a page's count and offset at zero. That is the reference's own semantics, not
     T-SQL's, and PostgreSQL, which refuses a negative `LIMIT`, needs the clamp too.
   - A marker may repeat in the text. PostgreSQL's positional markers may too.
   - `RenderFunction` stays untyped. `Derive` does not lower, so a scalar's result type never reaches a
     result column, only a comparison. A comparison of an `int` with a `float` answers the same in both
     engines, and `floor` and `round` over an integer answer a double in PostgreSQL as they answer a float
     in the reference. *Reopen* when `Derive` lowers.
   - The probe's `COUNT(*)` and `MAX`. A probe compares readings of one engine with each other, never with
     the reference, so the order its maximum is taken under only needs to be stable.
   - PostgreSQL truncates a quoted identifier past 63 bytes, both where an object is created and where it
     is named. A model's physical identifier therefore names the object the engine holds, and a minted
     alias is short.

   The provider's lowering core (`Statement.fs`, `Dialect.fs`, `Lowering.fs`, `FoldSplit.fs`, `Probe.fs`)
   writes no literal that only a dialect may write. A source-reading test in the suite holds this, and it
   was made to fail once, on an injected `" OFFSET"`, before it was trusted. *Reopen* when a third
   dialect finds a value it must read differently by something other than its type.
2. **What PostgreSQL lowers, and what it refuses.** Every function, operator and aggregate is matched with
   no catch-all.
   - **Mapped as SQL Server maps them:** `Abs`, `Floor`, `Ceil`, `Sqrt` (null for a negative argument),
     `Trim` (the reference's four characters, spelled with `chr`), the arithmetic, comparison and logical
     operators, `Count`, `CountDistinct`, `Sum`, `Mean`, `Min` and `Max`.
   - **`Round`** is spelled `sign(x) * floor(abs(x) + 0.5)`. This is the reference's own formula, probed:
     `0.49999999999999994` rounds to 1 there, which is not .NET's away-from-zero rounding, and
     PostgreSQL's `round` rounds a double half to even. A test pins the premise against the reference.
   - **Mapped here and refused by SQL Server:** `Replace`, `Contains`, `StartsWith` and `EndsWith`.
     PostgreSQL's `replace`, `strpos`, `starts_with` and `right`/`length` match literally and
     case-sensitively, with no wildcard. Probed against the reference: an empty search, case, `%`, nulls,
     and an astral suffix.
   - **Refused:** `Lower` and `Upper` (case folding by the collation's character classes, which under `C`
     reach ASCII only). `Length`, `Substr` and `IndexOf` (they count code points, where the reference
     counts UTF-16 code units; probed: an emoji has length 2). `DatePart` and `DateDiffDays`. `Concat`.
     `Least` and `Greatest` (a null is skipped). `Div` and `Mod`. `Median` and `StdDev` (not measured).
     `First` and `Last`. `Cast`. The exact roundings. The empty list.
   - **The change counter is `None`.** The write-ahead log position moves for every database in the
     cluster and misses an unlogged table.

   *Reopen* a mapping on the first unexpected difference an armed run reports.
3. **The differences, and where the two dialects part.**
   - **Collation and case** are *spelled* here (`COLLATE "C"`), and the armed leg requires that no fixture
     differ as `CaseInsensitiveCollation` predicts. SQL Server *declares* them. The two dialects part on
     `difference-collation-case` and `difference-collation-order`. Both fixtures declare the pin, so the
     difference is pinned on both: SQL Server shows it, and PostgreSQL agrees.
   - **Code point order** is *declared* by a new pin, `CodePointOrder`. Under `C`, PostgreSQL orders
     strings by code point, and the reference orders them by UTF-16 code unit. The two part only where an
     astral character meets one in U+E000 to U+FFFF. The pin predicts the reference over seed and bindings
     re-encoded so that code-unit order is code-point order. Fixture: `difference-code-point-order` (3 by
     code unit, 2 by code point). SQL Server agreed on it (ruling 6). PostgreSQL differs as pinned,
     measured on the engine (ruling 5).
   - **Null ordering** is *spelled*. **Integer division**, **decimal scale on division** and **date
     truncation** are *refused*. **Float summation order** is *declared*. **A pushed sum's width** is
     *decoded*: PostgreSQL's `sum` and `count` answer a `bigint`, and `decodeCell` narrows it, refusing a
     sum past int32 with the reference's refusal. These use the existing fixtures.
   - The four constructs ruling 2 maps here and SQL Server refuses keep their `reach-*` fixtures in the
     refusal set, not the subset. PostgreSQL lowers them there, so its agreement on them is checked
     wherever the kit runs, and the subset a dialect is certified on does not widen.
4. **The kit's seed gains a table, and the kit version does not advance.** The seed gains `words`
   (physical `CK_WORD`), and the bindings gain `p_private_use`, for the code-point fixture. `seed.json`
   changes for the new table, and `manifest.json` changes for the two new ids (64 fixtures). Every existing
   fixture file is byte-identical, and so is each existing table's encoding inside `seed.json`. A host
   that loads the new seed gains a table no earlier fixture reads. The set is additive, and the 0.1.0 slot
   is an untagged draft, so the kit's stamp, which is the assembly version, rides it. A released kit would
   take a new version for the same change. *Reopen* if a seed change ever alters an existing table.
5. **The PostgreSQL leg is armed by configuration and Pending without it, and its first run is recorded
   (amended 2026-10-23, data#4).** With `FUARAN_DB_POSTGRESQL` set to a connection string, the suite does
   the following:
   - It creates a database (`TEMPLATE template0`, UTF-8), seeds it, runs the kit, fails on any unexpected
     difference or any case-insensitive verdict, and drops the database (`WITH (FORCE)`). It prints one
     line per fixture: the verdict, the steps the engine ran and where the boundary fell.
   - It runs the falsifier: a dialect that reads strings bare, over a database created under the ICU root
     locale. That run must go red.
   - It runs this dialect over that same ICU database, where it must still agree. That is the falsifier's
     converse: it shows the `COLLATE "C"` read is what turns the run green, not an order the database
     happens to share with the reference.

   Requirements: PostgreSQL 11 for `starts_with`, 13 for the forced drop, and 15 for the ICU database.

   **The recorded run.** PostgreSQL 17.11 (`PostgreSQL 17.11 on x86_64-windows, compiled by
   msvc-19.44.35229, 64-bit`), from the EDB installer (`winget install PostgreSQL.PostgreSQL.17 --version
   17.11-5`), as a local Windows service on an ARM64 machine, so the x86-64 server runs under emulation.
   The server's default databases are UTF-8 with the libc collation `English_United Kingdom.1252`, and the
   run's database inherits it.
   - **Before any fix:** 64 fixtures. 63 agreed, 1 differing as pinned, 0 differing unexpectedly.
   - **After:** the same, because nothing differed unexpectedly and the dialect was not changed. The only
     fix was in the suite's host. Its engine description joined `datlocprovider`, a `"char"`, with `||`.
     PostgreSQL refuses that (`42725`, operator is not unique) before the kit runs, so the leg had never
     reached the kit.
   - **The differences, with their dispositions:**
     - `difference-code-point-order` differs as pinned (`CodePointOrder`), as ruling 3 predicted. It is
       the one fixture whose verdict differs from SQL Server's, and the pin stays.
     - `difference-collation-case` and `difference-collation-order` agree, so the spelled `COLLATE "C"`
       holds on the engine. SQL Server shows both as `CaseInsensitiveCollation`.
     - The other 61 fixtures agree, including the ones that exercise ruling 2 on the engine:
       - `filter-round`, the `sign(x) * floor(abs(x) + 0.5)` formula.
       - `reach-replace`, `reach-contains`, `reach-starts-with` and `reach-ends-with`, the four
         constructs SQL Server refuses.
       - `sum-int-overflow`, the `bigint` narrowing.
       - The typed seed, which binds and decodes every column type.

       Of the 64, 45 run at least one planned step on the engine and 19 stop at a refusal at step 1. Those
       19 still scan and decode the source there.
   - **ICU root locale (`und`):** this dialect gives 63 agreed, 1 differing as pinned, 0 differing
     unexpectedly. The falsifier gives the same counts, with `difference-collation-order` differing as
     `CaseInsensitiveCollation`, so it is red, as required.
   - **The falsifier over the libc database as well** was probed once and not kept as a test: 62 agreed and
     2 differing as pinned (`CaseInsensitiveCollation` on `difference-collation-order`, `CodePointOrder` on
     `difference-code-point-order`). So the bare read is wrong under the server's default locale too, not
     only under ICU.
   - No conformance database survived the runs.

   No difference stays open. Nothing in the `Dialect` seam, the kit or its fixtures changed, so
   `STABILITY.md` is untouched and every fixture is byte-identical. *Reopen* a mapping on the first
   unexpected difference a later armed run reports, including one on another server version or locale.
   The in-memory run still lowers all 64 fixtures through this dialect and agrees on all 64.
6. **The first dialect's recorded run, repeated after the seam change.** SQL Server 2025 LocalDB,
   17.0.4025.3 (Express Edition, 64-bit), collation `SQL_Latin1_General_CP1_CI_AS`.
   - **Before**, on `0f8ffbe`: 62 fixtures. 61 agreed, 1 differing as pinned, 0 differing unexpectedly.
   - **After**: 64 fixtures. 62 agreed, 2 differing as pinned, 0 differing unexpectedly.
     `difference-collation-case` and the new `difference-collation-order` differ as pinned
     (`CaseInsensitiveCollation`), and the new `difference-code-point-order` agreed.

   The falsifier went red as required both times.
   - **Again on 2026-10-23 (data#4)**, in the same suite run as ruling 5's PostgreSQL leg, on the same
     LocalDB: 64 fixtures, 62 agreed, 2 differing as pinned, 0 differing unexpectedly, the falsifier red,
     and the in-memory legs of both dialects at 64 of 64. On this ARM64 machine the `(localdb)\` alias
     fails, because the client cannot load the x64 `SQLUserInstance.dll`. The leg connects through the
     instance's named pipe (`sqllocaldb info`) with `Encrypt=false`.

## 2026-10-12 — D10: the model projected per peer (Phase 1907)

In the custodian topology the provider, the model and the policy run on the far side of a peer call. The
requester still needs something to build a pipeline against, and may not be given the whole model, because
what exists is itself information. `ModelProjection` answers the part one subject may read. Seven rulings,
each with what would reopen it.

1. **A projection is a model, and a function of the readable part alone.** `project model grants` keeps
   the classifications the model declares and the grants reach; each table with a column of one of them,
   holding just those columns; each source holding such a table; each relation whose tables and key
   columns all survive (a relation that lost one key pair is dropped, never narrowed, since a join on fewer
   keys is another relation); and each measure whose column survives. Ids and names are kept, because
   relations and measures name columns by id and pipelines read names. The root keeps its id and name, so
   the empty projection is `Model.empty` of the model the requester addressed. The output is an ordinary
   model and passes every model rule. *Reopen* if the root's name is itself to be withheld.
2. **What it never carries.** Physical identifiers become the model names (the field stays, so the model
   codec carries a projection unchanged); row policies, limits, a source's fingerprint and the recording
   rule are dropped; no classification outside the grants is declared. The shard reads that a projection
   is "a function of the model's hash and the subject's grants": it is a function of the model and the
   grants, deterministically, but it does **not** carry the model's hash, since two models that differ only
   in what the subject cannot read must project to the same bytes, and a carried model hash would tell
   them apart. *Reopen* never by addition: carrying more is a change to the posture (STABILITY.md).
3. **The grants travel with it, normalised.** A projection carries one grant per declared classification,
   at the highest level any of the subject's grants names, in the model's order. The requester needs the
   level to foresee the aggregate-only rule, and the principal and attributes are not carried: two
   subjects with the same grants share a projection. A decoded text that is not its own projection under
   its own grants, or is not a well-formed model, is refused (`DB-WIRE-WRONG-SHAPE`); that one fixed-point
   check covers every part a projection may not carry. *Reopen* if grants gain a third level.
4. **The requester's check is the custodian's column authorisation, run over the projection with every
   table taken to hold withheld columns.** `ModelProjection.admits` runs the same walk as
   `Policy.columns` (`Policy.admit`'s first half, now public), with one difference: each table's rows carry
   one more lineage, for the columns the projection may withhold, classified by a mark no grant names.
   A use of every column of the rows (a result, a `Distinct`, a set operation) meets it and is refused as
   `DB-PROJECTION-WITHHELD`; projecting the table to named columns drops it. The projection cannot say a
   table hides nothing, since saying so would distinguish two models the subject may not tell apart, so this
   is conservative by construction: the requester refuses some pipelines the custodian would admit, and the
   laws show that it happens. Every other refusal is the policy's own code. *Reopen* if requesters need
   whole-table reads, which would need the custodian to publish completeness as a deliberate disclosure.
5. **A join's renaming is the subtle case.** The evaluator suffixes a right column whose name the left rows
   hold, and withheld columns take part in that renaming. So under a projection, a right column's joined
   name carries the withheld lineage when the left rows may hide a column (the name could be the left's),
   and a `_right` name carries the right table's withheld lineage too (a withheld right column could hold
   that name ahead of it). Reading such a name is refused; naming the left rows first, and reading names
   that cannot collide, is admitted. Both rules were shown load-bearing by removing each and watching the
   agreement law fail; the `_right` rule is reached rarely by the generator (in 4 of 61 seeds at 2000 cases
   with it removed), so a worked test pins it as well. *Reopen* if the evaluator's renaming changes.
6. **Drift is in the names a pipeline reads.** `drift pinned fresh` reports columns added, removed and
   retyped per table (Core's `Schema.diff`; a rename is a removal and an addition, an order change is none),
   relations and measures added and removed by name, ends or keys and aggregate, and grant levels changed.
   A removal, a retype `ColumnType.widens` does not allow, and a lowered grant are breaking; additions and
   widenings are not. The pin is the projection's hash, `sha256:` over its canonical text. *Reopen* if a
   relation rename should be read as a rename rather than a removal and an addition.
7. **The laws live with the code** (`ModelProjection.Laws`, which adds the released
   `Fuaran.Core.Conformance` to `Fuaran.DB`, as the provider and session packages already reference it).
   Over generated models, grants, twins and pipelines: total, pure, idempotent, monotone in the grants, a
   well-formed model, never carrying a physical identifier, policy, limit, fingerprint, recording rule,
   unreadable classification or unreadable table, the same bytes for a twin that differs only outside the
   grants, a round-tripping wire, drift that is never breaking for more grants and breaking for fewer
   exactly when they read less, and agreement: a pipeline the projection admits is one `Policy.columns`
   admits for the same subject, with the same columns at the same levels. The converse is not claimed:
   the custodian also applies cost, group-size and row-policy rules, and the projection's conservatism
   refuses more. The adequacy guard demands six verdicts, from both admitting (with a join and with an
   aggregate-only column) to each kind of refusal. A seventh, a policy refusal the custodian would not make,
   was never reached in 122,000 cases (61 seeds of 2,000) and is not demanded. The peer contract and the projection's
   transport are out of scope here; this ships what the contract would carry.

## 2026-10-11 — D9: the connection record (Phase 1903)

A new package, `Fuaran.DB.Session`: what passed over a connection, recorded as a hash-chained op-stream on
Core's, so a published figure can name the pipeline, the policy rules, the statement, the schema and
fingerprint, the row count and the moment it came from. Nine rulings, each with what would reopen it.

1. **The model marks what is recordable; the model gains a field to say so.** The phase's task reads "a
   parameter value or a result row is recorded only under a classification the model marks recordable",
   and the model as it stood marked nothing: `ModelSpec` held only `Classifications`. So `ModelSpec` gains
   `Recordable: string list`, the classifications whose values a record may hold. Empty is the default and
   records nothing. The wire writes it only when it names something, so every existing vector keeps its
   bytes, and a present empty array is refused as a second spelling. A new rule, `DB-RECORDABLE-UNDECLARED`,
   refuses a recordable classification the model does not declare. The alternative, a recordable set the
   host declares when it opens a session, was considered and declined, because the task says the
   model marks it. Breaking for a full-literal `ModelSpec` on the 0.1.0 draft (STABILITY.md). *Reopen*
   when recordability needs a finer grain than the classification.
2. **One decision per request, over what the policy says it reads.** A request's reads are the
   classifications of every column `PolicyRule.ColumnsGranted` lists, every column an applied row policy
   names, and every column a group floor counts (`Recording.reads`). If all are recordable, the record holds
   the parameter values, the result rows and the pipelines verbatim. Otherwise it holds none of them. The one
   decision covers every value because a pipeline's literal is lowered to a parameter: recording the literal
   while withholding the parameter would publish what the rule withholds. A withheld pipeline is recorded
   with each literal and each embedded operand replaced by a numbered `withheld:N` slot. What remains is its
   shape (columns, verbs, operators, named parameters), and that is enough to say which pipeline it was. The
   rule over-approximates, because it does not trace which output column a classified input reaches. That
   is sound, and the price is withholding some values that could have been recorded. *Reopen* with
   per-column lineage from the provider.
3. **A refusal to record is an op, and the reducer enforces the rule.** `Withheld` names the part
   (parameters or rows) and exactly the unrecordable classifications read. The reducer refuses
   `ValuesRecorded` or `RowsRecorded` for a request whose own `Admitted` op says it reads an unrecordable
   classification, refuses a `Withheld` that does not name exactly those, and refuses a digest or capture key
   of the wrong form. So a stream that breaks the rule does not fold. The rule is a property of the record,
   not a promise made by its writer.
4. **A withheld result's digest is committed, not plain.** A plain SHA-256 over a one-row count is one guess
   away from its value. A result whose rows are withheld is therefore digested under a secret the host keeps
   (`ResultDigest.Committed`). The host can later show that a result is the one recorded, and nobody without
   the secret can test a guess against it. A recordable result's digest is plain, so anyone holding the rows
   can check it. For the same reason a withheld request has no capture key: the key hashes the values. And a
   fingerprint's change value is withheld when its change column is not recordable, with the table named.
   *Reopen* if a verifier must check a withheld result without the host.
5. **The statement hash is over the five inputs that fix it, and no value.** It is SHA-256 over an injective
   pre-image of the text, the parameter count, each parameter's marker and type, the model's hash (over its
   canonical wire), the dialect's id and version, and the provider's version. The provider's version is read
   from its assembly (`major.minor.patch`), never typed by a host. The dialect's version is declared by the
   host when it opens the session, because a `Dialect` is a record of functions with no version of its own.
   The lowering is pure, so two hosts holding the same five inputs hold one hash. *Reopen* if the `Dialect`
   record gains a version, which would replace the declared one.
6. **The capture key follows `Query.invocationKey`'s pattern, with SHA-256.** It is the statement's hash,
   `#`, and a hash of the name-sorted bindings, three fields each, spelled exactly as the invocation key
   spells a cell. SHA-256 replaces FNV-1a, so that two distinct bindings sharing a key is out of reach rather
   than merely unclaimed. A captured session replays through `Session.replayExecutor`, which answers from
   the record and refuses a miss by name. `Session.replay` takes no executor at all: it rebuilds a
   statement's result from the captured rows, runs the recorded local suffix again, and compares digests.
7. **The reader refuses by the classes Core names.** A session's wire is Core's JSONL chain, hashed with
   SHA-256. Core 0.36.0 names no "torn tail" or "rewound head" class, so the reader maps both onto what Core
   does name. A line that does not scan is Core's `JsonlFault`, called a torn tail when it is the last line.
   A broken chain is Core's `ChainBreak`. A head other than the one the reader holds has the shape of Core's
   `AppendRejection.StaleHead` and is reported as rewound. The held head matters because a hash chain
   authenticates every prefix of itself: a truncation at a line boundary verifies, and only an anchored head
   catches it. *Reopen* when Core's typed load faults (its unreleased `StreamLoadFault`) ship, and adopt
   them.
8. **The export is an in-toto Statement v1 in a DSSE envelope, and the signer is an argument.** The one
   subject is the session's head, so a signature covers every record through the chain. The predicate
   summarises what the record claims: each statement was derived from a tree under a policy, and no result
   is claimed correct. `Export.sign` takes each signer as `{ KeyId; Sign: byte[] -> byte[] }` over DSSE's
   pre-authentication encoding. The package holds no key and references no signing package: it is a
   provider at the witness seam, not a consumer of it.
9. **The carried task 2008.t3 is the `Fingerprinted` op.** Beside each statement it guarded, the record
   holds the source id, the method `Fingerprint.ofSource` returns, the kind from `Fingerprint.disclose`, the
   reading taken before the statement (from `Probe.forSource` and `Probe.readTables`, or the host's file
   metadata), and `Fingerprint.compare`'s verdict against the session's previous reading of that source.
   The verdict is computed over the true readings the session holds. The reducer recomputes it whenever
   neither reading had a value withheld, so a forged verdict does not fold.

## 2026-10-11 — D8: the SQL Server dialect and the conformance kit (Phase 1901)

Two packages: `Fuaran.DB.Dialect.SqlServer`, the first dialect, and `Fuaran.DB.Conformance`, the kit that
says what "agrees with the reference" means for an engine. Six rulings, each with what would reopen it.

1. **The dialect seam is reopened, as D4.8 said it would be.** T-SQL has no boolean type, so the record as
   Phase 1900 shipped it could not spell a bit column used as a condition (`WHERE [flag]`), a `CASE` or
   `COALESCE` yielding a condition, or a `MIN` over a bit; and a pushed `Sum` over `int` could not be
   widened without the type of the column it sums. The record gains three fields: `RenderAggregate`,
   given the aggregated column's type; and `ValueAsCondition` and `ConditionAsValue`, which the renderer
   calls where a scalar's form and its position disagree (a value in a `WHERE`, a `CASE` test or a logical
   operand; a condition anywhere else). Both are identity for an engine with a boolean type, and the
   reference dialect's text is unchanged byte for byte; the placement laws hold over it at the Phase-1900
   seed and iterations, unchanged. Breaking on the 0.1.0 draft (STABILITY.md). The probe's two aggregates
   moved to `RenderAggregate` with it. *Reopen* when a dialect finds another construct it cannot spell.
2. **What the dialect lowers, and what it refuses.** Every function, operator and aggregate is matched
   with no catch-all and either mapped or refused with a reason the dialect's README names. Mapped:
   `Abs`, `Round`, `Floor`, `Ceil`, `Trim` (the reference's four-character set, spelled), `Sqrt` (null for
   a negative argument, spelled), `+`, `-`, `*`, the comparisons, `AND`, `OR`, `Count`, `CountDistinct`,
   `Sum`, `Mean`, `Min`, `Max`. Refused: the string functions whose T-SQL counterpart reads the collation
   or counts from 1, `DatePart` and `DateDiffDays`, `Concat`, `Least` and `Greatest`, `Div`, `Mod`, the
   substring operators (predicted by recipe 07, D4.4), and the aggregates and constructs the provider's
   own subset excludes. Mapping one of them is a claim of parity: *reopen* only with a fixture that shows
   it against an engine.
3. **The differences, each pinned one of three ways, each with a fixture.** *Spelled* so the engine
   agrees: null ordering (an explicit null flag per key), booleans (ruling 1), a mean (`AVG` over a float
   cast), a negative root. *Refused* so the step runs locally: integer division and decimal scale on
   division (`Div`, `Mod`, `Quotient`, `Rounded`), date truncation (`DatePart`, `DateDiffDays`). *Declared*
   where the engine's answer may differ and the kit can say how: collation and case, and the float
   summation order. The integer width of a pushed `Sum` (D4.6) is *decoded*: the sum is widened to
   `bigint` in the engine and `decodeCell` refuses it past int32 with the reference's own refusal, so the
   statement no longer fails where the reference refuses.
4. **A pin is checkable, never a waiver.** The kit's verdicts are agreed, differing as pinned, and
   differing unexpectedly. A fixture's answer is differing as pinned only when it EQUALS an answer the pin
   predicts: under `CaseInsensitiveCollation`, the reference's over case-folded seed and bindings; under
   `FloatSummationOrder`, the reference's over some order of the source rows (a source of at most seven).
   Any other difference is unexpected. Rows compare as a bag unless the fixture ends in a page whose keys
   are unique over what it keeps. *Reopen* the pin set when a dialect meets a difference neither predicts.
5. **The kit takes the host's executor and goes through the policy.** No package opens a connection; the
   kit's `Host` seeds the physical tables and runs a statement, answering rows or a `HostFailure`. Each
   fixture is admitted by `Policy.admit` for a subject granted the seed's one classification, so the kit
   certifies the path a host uses: admit, lower, execute, finish. Every fixture therefore ends in a
   `Limit` within the seed's ceiling, and a slot bound at run time (a sort key or page count from a
   parameter) is not a fixture, because the policy refuses it before lowering. The fixtures are
   emitted to `conformance/` stamped with the kit version, and the suite fails when the emitted set and
   the committed one differ.
6. **The repository's own leg is armed by configuration and Pending without it.** With
   `FUARAN_DB_SQLSERVER` set to a connection string, the suite creates a database, seeds it, runs the kit,
   fails on any unexpected difference, runs the kit again through a dialect that reads a bit as a
   condition and requires it to go red (the falsifier), and drops the database. Without it the leg is
   reported as ignored, never failed, and the output says Pending and why. The kit over the in-memory
   interpreter, through the reference dialect and through this one, is mandatory and agrees on every
   fixture. **The first recorded run**: SQL Server 2025 LocalDB, 17.0.4025.3 (Express Edition, 64-bit),
   collation `SQL_Latin1_General_CP1_CI_AS`, 62 fixtures: 61 agreed, 1 differing as pinned
   (`difference-collation-case`, `CaseInsensitiveCollation`), 0 differing unexpectedly. The float
   summation fixture agreed on that engine. *Reopen* the subset's claim on the first unexpected
   difference any host reports.

## 2026-10-11 — D7: Substrait as a second lowering target, measured and declined (Phase 1908)

The question was whether the provider should also emit Substrait, the serialised relational-algebra format,
so that every engine reading it is reached for the cost of one target rather than one dialect each. The
phase measured first. **The answer is decline, for now.** 34 of the 58 constructs in the declared subset
(58.6%) map onto the format without a pinned difference, 23 map only with one, and 1 does not map. Through
the one consumer this domain's hosts already meet, 32 map, with every parameter reduced to a literal. The
format standardises how a construct is SPELLED. It does not settle what it MEANS, so the parity work a
dialect does is still done per consuming engine. Five rulings, each with what would reopen it.

1. **The survey, from primary sources, read 2026-10-11.**
   - *The specification* (`substrait-io/substrait`). Current release v0.106.0, 2026-10-11. The project is
     pre-1.0 and releases weekly (v0.105.0 2026-10-04, v0.104.0 2026-09-27, v0.103.1 2026-09-20). Releases
     still break: v0.98.0 removed deprecated fields, and v0.101.0 changed the extension identifiers to URNs.
     Read at `main`: `proto/substrait/algebra.proto`, `proto/substrait/plan.proto`, the function catalogue
     under `extensions/` (`functions_arithmetic`, `_arithmetic_decimal`, `_boolean`, `_comparison`,
     `_string`, `_rounding`, `_rounding_decimal`, `_datetime`, `_aggregate_generic`), and the per-function
     test cases under `tests/cases/`.
   - *What it offers the subset.* `JoinRel` has `LEFT_SEMI` and `LEFT_ANTI`. `SortField` states nulls last in
     either direction (`ASC_NULLS_LAST`, `DESC_NULLS_LAST`), which is D4.5's pin spelled as data. `FetchRel`
     takes its count and offset as expressions. A plan can carry parameters: the `DynamicParameter`
     expression, bound through `Plan.parameter_bindings`. The format also publishes a per-function test-case
     corpus that a consumer can run, a function-level conformance suite.
   - *The consumers* (`substrait.io/community/powered_by`, which carries no date). Acero and Velox consume.
     DataFusion produces and consumes. DuckDB produces and consumes, through a community extension. Ibis
     produces. Gluten turns Spark physical plans into Substrait. ADBC and Arrow Flight SQL carry plans as a
     query form. There is also an R interface.
   - *The DuckDB consumer* (`substrait-io/duckdb-substrait-extension`, last commit 2026-10-09). It is
     installed `FROM community`, not shipped in DuckDB itself. It pins specification 0.102.0, four releases
     behind. Its source (`src/from_substrait.cpp`) has no `DynamicParameter` case, so a parameter must reach
     it as a literal. It reads a `FetchRel` count and offset only as integer literals. It reads one function
     option, `distribution` for `std_dev`. Every other option a mapping relies on, such as `divide`'s
     `on_division_by_zero`, is ignored, and the engine's own function decides. It refuses the
     null-propagating `least` and `greatest` by design, because DuckDB has no such function, and it refuses
     an integer `median`.
   - *The format is not fully self-consistent yet.* `functions_arithmetic.yaml` lists `sqrt`'s
     `on_domain_error` as `NAN` or `ERROR`, while the test case `sqrt(-1::i64) [on_domain_error:NAN]` answers
     null.
   - *.NET.* `Substrait.Net.Protobuf` 0.106.0 (Apache-2.0, 2026-10-11, depends on `Google.Protobuf` only) is
     the generated bindings, released in step with the specification. `Substrait.Net` 0.1.1 is a higher-level
     library. If the target is ever built, adopting the bindings is the route. Vendoring the `.proto` files
     would only re-create a package that already tracks every release.

2. **The mapping table.** Each construct the provider asks a dialect about, and each relation and expression
   form the statement can carry, against the format. *Mapped* means the format's definition gives the
   reference evaluator's answer. Where the format needs a cast to get there, the cast is exact (a 32-bit
   integer to a 64-bit float), so the provider can insert it, and the row still counts as mapped. *Pinned*
   means the format's definition leaves a difference that someone must pin or exclude. *Unmappable* means
   the catalogue has nothing to map to. Rows marked *outside* are outside the declared subset (D4.4) and are
   not counted.

   | Construct | Format | Verdict | Reason |
   |---|---|---|---|
   | `Scan` | `ReadRel` over a `NamedTable`, `base_schema` from the model | mapped | physical identifiers only, as D4.3 |
   | `Where` | `FilterRel` | mapped | |
   | `Select` | `ProjectRel` with an emit mapping | mapped | the result is read positionally, as now |
   | `Join` (form) | `JoinRel`, the condition an `and` of `equal`s | mapped | `equal` over a null key is null, so it matches nothing |
   | `Aggregate` (form) | `AggregateRel`; `NonEmpty` as a `count()` measure, a filter on it, then a projection | mapped | the D4.5 pin, spelled as relations |
   | `Ordered` | `SortRel` with `*_NULLS_LAST`, then `FetchRel` | mapped | nulls last is native to the format |
   | `Not` | `not` | mapped | Kleene |
   | `IsNull` | `is_null` | mapped | |
   | `Coalesce` | `coalesce` | mapped | the format wants at least two arguments; one argument is itself |
   | `Case` | `IfThen` | mapped | an untaken branch cannot raise, as in the reference |
   | `InList` | `SingularOrList` | mapped | defined as a disjunction of `equal`s, which is three-valued membership |
   | `Add` | `add` | pinned | a decimal result is `DECIMAL<P,S>`, capped at 38 digits with a scale-reduction rule, where the reference is exact and unbounded; int overflow is the `overflow: ERROR` option, which the consumer does not read |
   | `Sub` | `subtract` | pinned | as `Add` |
   | `Mul` | `multiply` | pinned | as `Add` |
   | `Div` | `divide`, after an exact cast to fp64, `on_division_by_zero: NULL` | mapped | the reference always answers a float and a null on zero; decimal division is refused by the reference, which names `Quotient` |
   | `Mod` | `modulus`, `division_type: TRUNCATE` | pinned | the catalogue has no division-by-zero option for `modulus`, so the reference's null on a zero divisor is not stated; decimal precision as `Add` |
   | `Eq` | `equal` | mapped | null-propagating; int against float compared after an exact cast |
   | `Ne` | `not_equal` | mapped | as `Eq` |
   | `Lt` | `lt` | pinned | the reference orders strings ordinally, and the format states no collation, so the consumer's collation decides |
   | `Le` | `lte` | pinned | as `Lt` |
   | `Gt` | `gt` | pinned | as `Lt` |
   | `Ge` | `gte` | pinned | as `Lt` |
   | `And` | `and` | mapped | Kleene, as the catalogue states |
   | `Or` | `or` | mapped | Kleene |
   | `Contains` | `contains`, `case_sensitivity: CASE_SENSITIVE` | mapped | a literal substring with no wildcards; the test cases cover multi-byte text. This is better than SQL, where the reference dialect refuses it |
   | `StartsWith` | `starts_with`, case-sensitive | mapped | as `Contains` |
   | `EndsWith` | `ends_with`, case-sensitive | mapped | as `Contains` |
   | `Abs` | `abs` | mapped | int overflow is the `ERROR` option, as the reference refuses |
   | `Round` | `round(x, 0)`, `TIE_AWAY_FROM_ZERO` | pinned | the float form maps; the decimal overload (`functions_rounding_decimal`) declares no rounding mode, where the reference rounds half up |
   | `Floor` | `floor` | mapped | fp64 after an exact cast; decimal through `functions_rounding_decimal` |
   | `Ceil` | `ceil` | mapped | as `Floor` |
   | `Substr` | `substring` | pinned | 1-based, and counted in characters (code points), where the reference is 0-based and counts UTF-16 units; a negative start is option-dependent in the format and clamped to 0 in the reference |
   | `DatePart` | `extract` with `indexing: ONE` | pinned | answers an i64, not an int; the reference also reads a string operand, which the format reaches only through a cast |
   | `Concat` | `concat`, `null_handling: ACCEPT_NULLS` | pinned | null propagation matches, but the reference stringifies a non-string argument by its own cell rendering, and the format needs a cast whose rendering is the consumer's |
   | `Trim` | `trim(x, ' \t\r\n')` | mapped | the reference's pinned character set, passed as the argument |
   | `Replace` | `replace` | pinned | an empty search string returns the subject in the reference; the format does not define it and has no test case for it |
   | `DateDiffDays` | (none) | unmappable | the catalogue has no date-minus-date; `subtract` takes only an interval and answers a date |
   | `Sqrt` | `sqrt` | pinned | a negative argument is null in the reference; the catalogue offers `NAN` or `ERROR`, and its own test case contradicts it |
   | `Least` | `least` | mapped | null-propagating, as the reference; one argument is itself. The DuckDB consumer refuses it |
   | `Greatest` | `greatest` | mapped | as `Least` |
   | `Pow` | `power` | pinned | added 2026-10-24 (compute 0.41.0, read at `main` that day): the reference answers null outside the reals' domain and refuses int32 overflow by name, and a float exponent is host-rounded within 2 ulp; the catalogue states an overflow option only, no domain-error answer and no accuracy bound |
   | `Exp` | `exp` | pinned | added 2026-10-24: host-rounded within 2 ulp in the reference; the catalogue's `rounding` option names a tie rule, not an accuracy bound |
   | `Ln` | `ln` (`functions_logarithmic`) | pinned | added 2026-10-24: the reference answers null at zero and below; `on_domain_error: NULL` states the below, but `on_log_zero` offers `NAN`, `ERROR` or `MINUS_INFINITY`, never null; host-rounded as `Exp` |
   | `Log10` | `log10` (`functions_logarithmic`) | pinned | added 2026-10-24: as `Ln` |
   | `IndexOf` | `strpos` minus one | pinned | `strpos` is 1-based and counts characters, where the reference counts UTF-16 units |
   | `Length` | `char_length` | outside | D4.4; the format counts characters, which is the very unit question D4.4 names |
   | `Lower` | `lower` | outside | D4.4; `char_set` is `UTF8` or `ASCII_ONLY`, with neither specified |
   | `Upper` | `upper` | outside | as `Lower` |
   | `Count` | `count(x)` | pinned | answers an i64, where the reference answers an int |
   | `CountDistinct` | `count(x)`, invocation `DISTINCT` | pinned | as `Count` |
   | `Sum` | `sum` | pinned | int into i64, where the reference refuses past int32 (D4.6); decimal into `DECIMAL<38,S>` |
   | `Mean` | `avg` | pinned | `avg` over an i32 answers an i32 and over a decimal a decimal, where the reference answers a float, so a cast must precede it; the float summation order is the consumer's (D4.6) |
   | `Min` | `min` | pinned | string order is the consumer's collation, as `Lt` |
   | `Max` | `max` | pinned | as `Min` |
   | `Median` | `median`, `precision: EXACT` | outside | D4.4; the consumer refuses the integer form |
   | `StdDev` | `std_dev`, `distribution: POPULATION` | outside | D4.4; this is the one option the consumer reads |
   | `First` | (none; `any_value` is arbitrary) | outside | D4.4 |
   | `Last` | (none) | outside | D4.4 |
   | `Inner` | `JOIN_TYPE_INNER` | mapped | |
   | `Left` | `JOIN_TYPE_LEFT` | mapped | |
   | `Semi` | `JOIN_TYPE_LEFT_SEMI` | mapped | |
   | `Anti` | `JOIN_TYPE_LEFT_ANTI` | mapped | a null key matches nothing, so its row is kept, as the provider's `NOT EXISTS` keeps it |
   | `Right` | `JOIN_TYPE_RIGHT` | outside | D4.4 |
   | `Outer` | `JOIN_TYPE_OUTER` | outside | D4.4 |
   | `IntType` | `i32` | mapped | |
   | `FloatType` | `fp64` | mapped | |
   | `BoolType` | `boolean` | mapped | |
   | `StringType` | `string` | mapped | |
   | `DateType` | `date`, days since the epoch | mapped | the canonical text converts exactly |
   | `TimestampType` | `precision_timestamp_tz` | pinned | carries a precision and a time-zone reading, where the reference holds whole-second UTC text |
   | `DecimalType` | `decimal`, `P ≤ 38` and a fixed `S` | pinned | the reference decimal is unparameterised; a value past 38 digits has no literal |

   The count, by group: relation forms 6 of 6 mapped; expression forms 5 of 5; operators 8 of 16;
   functions 6 of 14 (7 pinned, 1 unmappable); aggregates 0 of 6; join kinds 4 of 4; parameter types 5 of 7.
   That is 34 of 58 mapped, 23 pinned and 1 unmappable. Through the DuckDB consumer, `Least` and `Greatest`
   fall to refused, which leaves 32. *Amended 2026-10-24:* compute 0.41.0 added `Pow`, `Exp`, `Ln` and
   `Log10`, each pinned (rows above), so functions are now 6 of 18 (11 pinned, 1 unmappable) and the subset
   34 of 62 mapped (54.8%), 27 pinned and 1 unmappable. Four pinned rows only move the decision further
   toward decline; it stands. Both dialects refuse the four by name until a pinned conformance fixture
   shows an engine agrees. No parameter reaches that consumer as a parameter. The suite holds this
   table to the algebra: `SubstraitSurveyTests` goes red when the transform algebra gains a function,
   operator, aggregate, join kind or column type this table does not classify, and when the counts above no
   longer match the table's rows.

3. **The decision: decline, with the evidence.** The shard names the measure, the share of the subset that
   maps without a pinned difference, but no threshold. The threshold follows from what the target was for:
   to reach engines "for the cost of one target and not one dialect each". It pays only if it removes
   per-engine parity work. That needs two things. (a) Beyond the differences D4.6 already leaves to every
   dialect, the format must add no pinned difference of its own. (b) At least one consumer must read the
   format's parameters and honour the options the mapping relies on. Neither holds. On (a), of the 23 pinned
   rows, the collation, decimal-scale, `Sum`-width and float-summation rows are D4.6's own differences in
   the format's spelling. The rest are the format's own: 1-based character units (`Substr`, `IndexOf`),
   i64 counts and an integer `avg`, `DatePart`, `Concat`'s rendering, `Replace`, `Sqrt`, decimal `Round`,
   `Mod` by zero and the timestamp precision. `DateDiffDays` has no mapping at all. On (b), the one consumer
   that reaches an engine the hosts already use reads no `DynamicParameter`, and reads one function option.
   A pinned difference is therefore realised by each consumer's engine, not by the format, and each engine
   needs its own conformance run, which is what a dialect costs. Building now would also ship a target with
   no conformance run behind it. The kit is Phase 1901's and has not shipped. An engine run would also need
   the community extension fetched at gate time, which this repository's gate does not do.
   - *The ruling is the operator's to overturn.* (A) Accept the decline: no target package, and the
     dialects (Phase 1901, and the second dialect) carry every engine, a DuckDB one included. (B) Build
     anyway, on `Substrait.Net.Protobuf`, as a target for DataFusion, Velox and Acero, which read no SQL from
     this provider, with the 24 non-mapped rows refused through the target's own `Refuses` and the
     parameters inlined as typed literals. B buys reach to engines no host here uses today, at the cost of a
     pre-1.0 dependency that releases weekly and still breaks.
   - *Reopen when any of these holds.* The format reaches 1.0, or states a compatibility policy. A consumer
     that a host here uses reads `DynamicParameter` and honours function options. A client needs an engine
     reachable only by a plan. The catalogue gains a date difference and states a string collation. The
     table above is then re-measured, not assumed, and the suite's test says where to start.

4. **If a plan target is ever built, it is not a `Dialect`.** The dialect record (D4.8) is shaped for text:
   it quotes identifiers, writes parameter markers, renders functions over rendered strings, and renders
   paging. A plan target spells none of that. It reads the statement's abstract form, `Statement.Form` (the
   `Relation` tree, with its outputs, physical identifiers and parameter ordinals), together with
   `Statement.Parameters`, and builds the plan from them. The form already is the plan in all but encoding.
   The half of the seam a target shares with the dialects is `Refuses`, which is what places the fold
   boundary. The seam would therefore split in two. (a) A capability half, `Refuses` (and `MapType` for
   reports), which `FoldSplit` asks. (b) A spelling half: text for a dialect, a plan emitted over the
   finished `Statement` for a target. The record is NOT split now. There is one implementer of the spelling
   half, the change would be a breaking change to a seam D4.8 holds open for its first real dialect, and
   reuse over a new abstraction needs the second implementer first. *Reopen* when a plan target is accepted
   under 3(B) or a reopening, and split the record in the same change, recorded as a breaking change in
   `STABILITY.md`.

5. **The positioning in the plan is corrected** where the survey was written from memory. Substrait is
   pre-1.0, so "a query as serialised data" exists, but not yet as a stable contract. The claim that "the
   tools above document the differences between their backends; they do not certify agreement" overstates
   the gap. The format publishes a per-function test-case corpus a consumer can run. What it does not
   certify is plan-level parity against a reference evaluator, which is the distinction this domain draws.
   And the plan's Q9 premise that the format reaches its engines "for little cost" is the one measured here
   and found false. The engines are reached, but each still answers the parity question for itself.

## 2026-10-11 — D6: a cheap source fingerprint (Phase 2008)

A host that re-runs work when a source changes needs to learn that it changed without reading it. A source
may declare a fingerprint the host polls; each method states how far it can be trusted. Seven rulings, each
with what would reopen it.

1. **The model declares the method. The kind and the blind spots are derived from it.**
   `SourceSpec.Fingerprint` is a `FingerprintMethod option`: `ChangeCounter`, `RowCountMaxChange` (per table,
   the change column the model names) or `FileStat` (with or without a content hash). `Fingerprint.disclose`
   derives the kind (`Exact` or `Heuristic`), the changes it can miss, and the moves it can make when nothing
   changed. An author never asserts a kind, and the wire carries none (a `fingerprint` object with an `exact`
   field is refused as an unknown field), so no model can call a heuristic exact. A source with `None`
   declares no fingerprint. Its host must read it or work to a schedule, and the probe says so by name
   (`DB-PROBE-UNFINGERPRINTED`). *Reopen if* a method's guarantee depends on deployment facts the method
   cannot name, such as a counter scoped to one schema on one engine and to a whole server on another. Its
   disclosure would then have to be the dialect's to state.
2. **Exact means no missed change. Over-reporting is disclosed and allowed.** A change counter moves on a
   write that leaves the data as it was, and on writes outside the source when its scope is wider. Both cost
   a re-run and never a wrong result, so the counter is `Exact` with those over-reports disclosed. A content
   hash is exact with nothing over-reported: an unmoved hash is `Unchanged` even when the modification time
   moved. The verdicts are not symmetric. `Unchanged` is only answered under an exact method. A heuristic's
   unmoved reading is `NoChangeSeen`, carrying its misses, so a host cannot read a heuristic silence as an
   exact one. *Reopen if* a counter is found that can stand still across a change inside one history; that
   would make it heuristic.
3. **What each heuristic misses is documented and demonstrated.** Row count plus maximum change value misses
   four things: an update that does not advance the change column; a delete paired with an insert whose
   value does not exceed the old maximum; a change within the column's resolution (Core's timestamps keep
   whole seconds); and a writer whose clock is behind. A file's size and modification time without the hash
   miss a same-size rewrite within the time resolution, or one whose writer restores the time. The suite
   shows the first three row-count misses and the file miss as real data changes the readings cannot see.
   The clock-skew case is the same shape as the second, so it has no test of its own. *Reopen if* a new
   miss is found: it is added to the disclosure, which STABILITY.md classes as a correction that rides.
4. **A row-count fingerprint covers every table of its source, once.** Four rule codes:
   `DB-FINGERPRINT-COVERAGE` for a table left out or named twice, because a change to a table left out would
   never move the fingerprint. `DB-FINGERPRINT-DANGLING` for a table or column the model does not hold.
   `DB-FINGERPRINT-FOREIGN` for another source's table or another table's column. `DB-FINGERPRINT-TYPE` for
   a change column that is not timestamp, date or int, the ordered types a writer stamps. A float, decimal,
   string or boolean maximum does not advance with a change in any useful sense. *Reopen if* a source wants
   to fingerprint some of its tables and leave the rest to a schedule; that would need a per-table
   declaration rather than a gap.
5. **Per dialect: the counter where the engine keeps one, row counts where it does not, file metadata for a
   file.** The dialect seam gains `ChangeCounter: string option`, the statement that reads the engine's own
   change counter or log position. The provider's `Probe.forSource` writes the probe from the model's method
   and the dialect: the counter text; one `SELECT COUNT(*), MAX(<change column>) FROM <table>` per table,
   rendered through the dialect's own quoting and aggregates and refused by name where the dialect refuses
   either aggregate; or a file probe that needs no statement. A counter declared over a dialect without one
   is refused (`DB-PROBE-NO-CHANGE-COUNTER`) rather than silently degraded to a heuristic. `Probe.advise`
   names the counter where the dialect keeps one and the model declares row counts or nothing, because it is
   one statement and exact. It is advice only, and the model's declaration is what probes. The count is
   `COUNT(*)`, not a count of the change column, so a row inserted with a null change value still moves it.
   *Reopen if* a dialect's counter needs parameters, or more than one value, to be read.
6. **The host reads; the domain compares.** As with drift (D2), nothing here opens a connection or a file.
   The host runs the probe, and `Probe.readTables` turns its rows into a reading, refusing a count that is
   not a non-negative integer and a maximum of the wrong type. For a file the host reads the size, the
   modification time in UTC ticks and, where the method asks, `sha256:` and 64 lowercase hex over the
   content. `Fingerprint.compare` is total and pure. A reading of the wrong shape, readings over different
   tables, or a hash the method needs and a reading lacks answer `Incomparable`, which a host treats as
   changed. A maximum is compared by Core's canonical cell token, so `1.50` and `1.5` are one value.
7. **The fingerprint is recorded in the connection record by Phase 1903, not here.** The connection record
   (`Fuaran.DB.Session`) does not exist yet, and its phase already lists the source's fingerprint among its
   ops. This phase supplies what 1903 records: the method, `Fingerprint.disclose`'s kind, and the
   `FingerprintReading` taken before each statement it guarded, with `Fingerprint.compare`'s verdict against
   the previous reading.

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
