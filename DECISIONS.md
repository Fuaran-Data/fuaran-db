# Fuaran.DB — decisions (newest first)

## 2026-11-17 — D21: the group floor drops a group for too few contributors, not for none (Phase 43)

The floor (D5.5, kept by D18.5) kept a group only when every counted input had at least the model's floor of
contributors. That dropped a group whose count for one input was zero, at any floor, the floor of 1 included. D20.6
found the first consumer's shoppers roll-up emptied by it under the keyed grant: one grouping counts the customer key
four times, three of them as copies chosen by a condition, and a group with no new shopper, or none returning, has a
count of zero for that copy. A floor cannot be declared as zero, and an undeclared floor is refused, so a model had no
way to say "no minimum". Eight rulings; D5.5 and D18.5 are amended in place, and D20.6's reopen clause is closed.

1. **The rule.** A group is dropped when some counted input has at least one contributor and fewer than the floor.
   An input with no contributor does not drop it, with one condition above a floor of 1 (ruling 2): some counted
   input has at least the floor's contributors. At a floor of 1 the floor drops nothing, which is what "no minimum
   group size" means. With one counted input, above a floor of 1, the rule is the comparison it always was, a count
   at or above the floor; with several it is a conjunction: each count none or at the floor, and some count at the
   floor. **The argument.** A count of zero says that no row of the group holds a value for the input. It names no
   value and no contributor: there is no one in that answer to be fewer than the floor. What it does say is a fact
   about each of the group's rows, that the input is absent from it, and a fact about every row of a group is a fact
   about that many contributors. So a zero is as safe as the group is large, and the floor needs the group shown to
   be at least the floor's size. A contributor is a present cell, one per row, so an input counted at the floor
   shows that many rows, and a zero beside it says one thing about each of them: the kind of answer a count at the
   floor already gives (that every one of its rows holds the input), which D5.5 admits.

2. **Why the rule as filed is narrowed: a zero over fewer rows than the floor is about them.** The phase was filed
   with "an input with no contributor does not drop the group", unconditionally. Checked at a floor above 1, that
   keeps a group of one row whose only counted input is zero. A copy of a classified column chosen by a condition on
   its own value is admitted (a `Derive` carries what it reads, a use refused at no level), and its count is floored.
   Under `Keyed`, a grouping on row-level columns counting `Case(key = 'x', key)` would keep a one-row group whose
   count is zero, telling the reader that the row's key is not `'x'`: an answer about one contributor, below the
   floor. Under `Aggregate` the plainest case does it: `Count(salary)` is zero exactly when the one row's salary is
   absent, and under `Aggregate` a presence is the column's row value (D20.3). The floor used to drop both groups, and
   with the condition of ruling 1 it still does: no count in either is at the floor. **What the condition costs.** A
   group of the floor's rows or more in which every counted input is zero is dropped, because no count shows how
   many rows it has. The algebra has no row count (D16.6: every aggregate names a column, and `Count` counts its
   present cells), so showing it would add a counted constant to every floored grouping, for a group that answers
   nothing but zeros and nulls. Recorded, not built.

3. **The alternatives D20.6 named, and why neither was taken.**
   - **Counting the group's rows instead of each input's members.** Weaker, not stronger: a group of the floor's
     rows with one contributor to an input passes, and a `Sum` or a pick of that input is that contributor's value.
     The count per input stays the floor; the group's size enters only as ruling 1's condition on a zero, read off
     the counts the floor already takes.
   - **Withholding one aggregate below the floor instead of its group.** At floors above 1 it is the more useful
     rule: a group keeps its other answers. It is not built, because the simpler rule is defensible and because it
     costs three things. Every aggregate over a floored input becomes a `Case` on its count, so a result column that
     was never null may be, and a withheld answer reads the same as an aggregate over no contributor unless a flag is
     returned beside it. Each dialect lowers a different rewrite, and the recorded rewrite grows with it. And it
     widens differencing: a group returning its total beside all but one of its parts returns the withheld part by
     subtraction, so withholding needs its own complement rule before it holds. It stays a candidate for a consumer
     that wants it at a floor above 1, with that argument answered first.

4. **A finding outside this phase: the floor counts each input, never the difference of two.** A grouping counting
   a key and a copy of it chosen by a condition, at 5 and 4 under a floor of 3, is kept before this phase and after
   it, and tells the reader that one row fails the condition; summing a classified amount and the same copy of it
   gives that one row's amount by subtraction. It holds at 0.3.0 under `Aggregate`, and under `Keyed` since D18. This
   phase does not introduce it, and the zero ruling 1 admits does not widen it: beside a count at the floor, the
   difference with a zero is that count. *Reopen* D5.5 with a complement rule (each conditionally chosen copy's
   complement in its group, counted, also none or at the floor) if a grant must hold against a subject who writes
   its own pipelines, which is the subject the floor is for.

5. **What an aggregate answers over no contributor.** A kept group can now carry aggregates over an input no row
   holds. `Count` and `CountDistinct` answer 0; `Sum`, `Mean`, `Min` and `Max` answer null, the absence of an
   answer, never 0. The reference evaluator answers so, and both shipped dialects answer the same; a probe pipeline
   over the staged rows pins it in the reference and every dialect at floors of 1 and 2, and runs on each engine
   where its leg is armed. Under `Keyed` a key's pick over no contributor is null, held as a key as D18.4 holds any
   pick.

6. **The recorded rule and the wires.** `GroupFloorApplied(step, floor, columns)` keeps its shape and its spelling:
   it records that the floor was applied, at which floor and over which columns, and the rule applied is this one.
   The session records the rewritten pipeline, which carries the filter as applied. With one counted input above a
   floor of 1 that is the same comparison as before, so no vector in the corpus moves (`keyed.jsonl` counts one input
   at a floor of 2, and keeps its bytes); with several it is the conjunction, written with the `or` and `eq` the wire
   already carries. A session test pins a two-input floor recorded, round-tripped and run.

7. **The proof on the staged shapes.** Under the keyed grant each of the six answers exactly the rows it answers
   under the row-level grant, in the reference evaluator and on each engine where its leg is armed, and each answers
   rows. The test that pinned the shoppers roll-up's empty answer now pins the equality. Seen red: with the floor of
   0.4.0 before this phase, three tests fail, the equality among them.

8. **The surface.** No public member is added or removed. Behaviour moves for an existing consumer, so the change
   is breaking in what it answers: at a floor of 1, a grouping keeps a group in which a counted input is zero (0.3.0
   dropped it); above it, a grouping with several counted inputs keeps a group in which one is zero and another is
   at the floor. A pipeline that returned fewer groups returns more. A grouping with one counted input above a floor
   of 1 answers as before, and so does every refusal.

**The evidence.** SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit), collation
`SQL_Latin1_General_CP1_CI_AS`: the six staged shapes, admitted under the keyed grant, agreed with the reference, and
so did the empty-input probe at floors of 1 and 2; the kit's 112 fixtures, 110 agreed and 2 differing as pinned, 0
differing unexpectedly. PostgreSQL 18.7 (Debian 18.7-1.pgdg13+2, 64-bit) in a local container, database collation
`en_US.utf8`: the six shapes and the probe at both floors agreed; 112 fixtures, 111 agreed, 1 differing as pinned, 0
differing unexpectedly, and over the ICU root locale 111, 1 and 0. No conformance database survived either run.

## 2026-11-17 — D20: a keyed column's presence may be tested, and the test is not the key (Phase 42)

D18.2 refused every row-wise use of a keyed column but a grouping key or a join key, a predicate included. Phase 41
found the first consumer's six pipelines refused under the keyed grant for one reason (D19.7): they test whether a
key is present. The order stage keeps the orders of a known customer with a non-blank test of the customer key, and
the line stage labels an order without lines by testing, after a left join, whether the line's order key is null,
then groups and sorts by those labels. A presence test says whether a row has a key. It does not say which. Seven
rulings; D18.2 is amended in place, and D19.7's reopen clause is closed.

1. **What a presence test is: a closed set of forms over one name.** `IsNull` of the name; a comparison, `Eq` or
   `Ne`, of the name against the empty text; and the same comparison of the name's `Coalesce` with the empty text,
   which is how the consumer writes "not blank". Either operand may stand on either side, and a negation is `Not`
   of one, which the walk reaches as it reaches any `Not`. The name must carry one model column UNCHANGED: through
   a projection's rename, a grouping key, a join (a joined side's `_right` name included) and a grouped operand's
   output, and never through a derivation, a pick (D18.4), a union, a pivot or a model step. A derivation's nulls are
   its own, and may say anything about the key it read (a derivation null exactly when the key is one value is a
   test of that value), so the check does not read through one, as D19.2 does not for a relation. **Declined**, and
   each stays a predicate on the key: a comparison with any other literal, with a parameter or with another column;
   a `Coalesce` with any other fallback or a third member; an ordering against the empty text, which over text is
   the same question but over any other type is not, and which the set does not need; a pattern; a length; a
   membership, even of the empty text alone (the phase's charter keeps membership refused, and `Eq` asks it); and a
   null test of an expression over the column (`Upper`, `Trim`), whose answer is the expression's.

2. **Where it is admitted: a filter's predicate and a `Case`'s condition**, anywhere inside either, beside reads of
   other columns, in the pipeline and in a grouped operand. A presence test elsewhere in a `Derive` (a boolean
   column computed as `IsNull` of the key) carries the key as before: it is the same bit, and the phase's charter
   places the admission at the condition, where the consumer writes it; a host writes the column as a `Case`. Under
   `Aggregate` nothing changes, and a presence test is refused as D5.4 refuses it: that level withholds the
   column's rows one by one, and whether a given row holds a value is one row's fact about it. `Keyed` withholds the
   value and the grain, and a presence answers neither.

3. **Its result is not the key.** The walk records what a presence test reads as the column's presence, apart from
   its value. Under `Keyed` a presence is neither the key's value nor its grain: a label chosen by one may be grouped
   on, sorted by, joined on and returned, and a grouping keyed on such a label does not set the key's grain (D18.3).
   The branches of the `Case` keep their own lineage, so a branch or a default that answers the key is still the key
   (`DB-GRANT-KEY-IN-RESULT` when returned, `DB-GRANT-KEY-ONLY` when sorted by), and so is a `Coalesce` of it. Under
   `Aggregate` and `RowLevel` a presence is the column's row value exactly as before, so nothing a subject holding
   either level reads moves: the same pipelines are refused with the same refusals, and the floor counts the same
   columns.

4. **What it does not change.** The grain ruling (D18.3): a presence filter after a grouping keyed on the key leaves
   the rows at its grain, and the result is refused as `KeyGrain`. The key-in-result ruling, the picks (D18.4) and the
   group floor (D18.5) stand as written. The floor counts a keyed column's values and picks, which is what it was
   written over; a presence folded into an aggregate is neither, and is counted only under `Aggregate`, where it is
   the column's row value.

5. **The proof on the staged shapes.** All six are admitted under the subject holding the keys at `Keyed` and every
   other column row by row, as written: the five that end in a grouping lower in full with an empty local suffix in
   the reference dialect and both shipped dialects, and the sixth places its boundary at its first cast, right after
   the grouping to its grain. The lowering and placement acceptance moved from the row-level grant to the keyed one,
   and the tests that pinned the two refusals now pin the admissions, each naming the presence test's column at the
   keyed level among the granted columns. Seen red: with the walk of 0.4.0 before this phase, six tests fail, the
   admissions among them. The policy tests pin the smuggling cases by name: a comparison with a non-empty literal
   either way, with a parameter, with another key outside a join, an ordering, a pattern, a length, a membership, a
   null test of an expression over the key, a presence test of a `Case` that answers the key, a presence test of a
   derived name (an identity derivation included) and of a key's pick, a `Case` whose branch answers the key
   (returned, and sorted by), the key's `Coalesce` returned, a bare null test returned, and a presence filter that
   does not reset the key's grain.

6. **A finding the floor stands on: the shoppers roll-up is emptied under the keyed grant.** Its last grouping
   counts four columns carrying the customer key, three of them chosen by a condition (the shoppers with a spend, the
   new ones, the returning ones). Under `Keyed` the floor counts each (D18.5) and keeps a group only when each has a
   member, so a group with no new shopper, or none returning, is dropped; on the staged rows no group has both, and the
   reference answers no rows. The other five shapes answer exactly what they answer under the row-level grant, and a
   test pins both facts. The floor stands as written, as this phase was charged: a consumer counting several
   conditional copies of a key in one grouping, under a keyed grant, loses each group where any copy has no member.
   *Reopen* D18.5 if such a roll-up is wanted with a floor, with an argument for counting the group's rows rather
   than each keyed input's members, or for withholding one aggregate below the floor rather than its group.
   *Closed 2026-11-17 (D21, Phase 43):* reopened and decided. The floor drops a group for too few contributors to
   an input, not for none, while some input shows the group at the floor's size; at a floor of 1 it drops nothing.
   Neither alternative was taken (D21.3). The shoppers roll-up now answers under the keyed grant exactly what it
   answers under the row-level grant.

7. **The surface.** No public member is added or removed, and no wire moves: a refusal is narrowed. Pipelines that
   0.3.0 and Phase 40 refused under `Keyed` as `KeyOnly` or `KeyInResult` because they tested a key's presence are
   admitted; under `Aggregate` and `RowLevel` nothing moves.

**The evidence.** SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit), collation
`SQL_Latin1_General_CP1_CI_AS`: the six shapes, admitted under the keyed grant, agreed with the reference; the kit's
112 fixtures, 110 agreed and 2 differing as pinned, 0 differing unexpectedly. PostgreSQL 18.6 (Debian
18.6-1.pgdg13+2, 64-bit) in a local container, database collation `en_US.utf8`: the six shapes agreed; 112
fixtures, 111 agreed, 1 differing as pinned, 0 differing unexpectedly, and over the ICU root locale 111, 1 and 0.
No conformance database survived either run.

## 2026-11-17 — D19: a declared relation is followed through a rename and after a grouping (Phase 41)

D4.2 admits a join only along a relation the model declares. The check compared the join's column names with the
relation's as written, at the step where the join stands, while the provider already followed each left key to the
model column it came from (D4.3) and policy follows every read the same way (D5.3). So a join written after the
left key was renamed was refused as undeclared although it joins the declared columns, and the first consumer's
staged pipelines (an order stage and a shopper stage, each a grouping, then joins on their keys) could not be
written as they are. Seven rulings.

1. **The check follows lineage.** Each column name reaching a step resolves to the model column it carries
   unchanged, or to what took it away. A projection carries its sources under their new names; a join to a
   model table or to a grouped operand brings in the right side's columns, a name the left already carries
   suffixed `_right` as the reference names it (an operand's columns through `Operand.provenance`, D16.2); a
   grouping keeps its keys; an intersection or a difference keeps the left rows. A join is admitted when every
   left key resolves to a column of one table, and the model declares a relation between that table and the joined
   one on exactly those column pairs, either way round. A key that resolves to nothing is refused as
   `UnresolvedJoinKey`, naming what it resolved to: "is derived at step 3", "is the aggregate Max of 'customer_id'
   at step 1, where the grouping keeps 'channel' as its keys", "comes through the union at step 1". A key that
   resolves to a model column on no declared pair is `UnrelatedJoin`, as before.

2. **A derivation is not a model column, even one that copies.** `Derive("k", Col "k")` resolves to "is derived";
   so does a window's output, a pivot's or unpivot's value columns, and everything after a union or a model step.
   The relation is a statement about model columns, and a derivation is a statement about an expression; the check
   does not read expressions to decide whether one is the identity. **This newly refuses** two shapes 0.3.0's check
   admitted by name: a derivation or an aggregate given the relation's column name, and a key renamed to that name
   from another column. 0.3.0's provider never lowered either (its lowering already read lineage), so a host that
   reached the provider through the check ran them locally; now the check names them. Nothing that resolves to the
   declared columns is refused, and nothing is newly admitted that 0.3.0 refused for a reason other than its name.

3. **A grouping keeps or loses a relation, and the refusal says which.** A grouping whose keys include the
   relation's column keeps it; an aggregate of the column, `Max` included, loses it, and the refusal names the
   aggregate and the keys the grouping kept. A grouping that drops the column leaves a name the rows do not carry,
   refused as `UnknownColumn` with the members that remain. This is stricter than D18.4, which holds a key's `Min`,
   `Max` or `Median` as the key for policy: a pick answers one of the group's values, but the relation is between
   columns, and the check does not admit an aggregate as a column. A pipeline that wants the key after a grouping
   keeps it as a grouping key, which is how the acceptance pack's order stage is written (ruling 6). *Reopen if* a
   consumer needs a pick of a key to join, with an argument that the check can verify the pick is functional.

4. **The provider needed no new form.** The premise was that the provider must learn to lower a join whose left
   side is a grouped pipeline. Checked against the tree: every relation already renders as a nested select, so the
   left side of a join is a derived table whatever it holds, and the lowering already followed each key through
   renames and grouping keys (`Col.Origin`). What placed the boundary in the measurement the phase was filed from
   was a key taken as an aggregate (`Max` of the customer key, grouped on the order key), which has no origin; with
   the key kept as a grouping key the same pipeline lowered in full. The phase therefore changes no lowering and no
   dialect, and every statement 0.3.0 wrote is the same text. It adds what proves the form: the placement laws gain
   the law "a join whose left side is a grouping on the relation's key, or whose key a projection renamed, equals the
   reference's join", with sample adequacy over each kind and the renamed key, eight templates (four kinds after a
   grouping, a rename, a grouped left side joined to a grouped operand, a re-aggregation after the join, and a key
   the grouping aggregated, which places the boundary), and its falsifier as a permanent test: an engine whose join
   matches a null key, which the null key's group exposes. Widening the sample left `unpinnedClock` unreached on the
   fixed seed; the generator now draws a case with no pinned clock a third of the time, and two more templates read
   the clock, so the refusal is reached by the draw rather than by hunting a seed. The kit gains `Fixtures.staged`,
   five fixtures over a seed whose customer key holds a null and repeats.

5. **What still does not lower is stated.** A join between two stages neither of which is a model table is refused
   by the check by name (`UnknownTable`): the model holds no such source, and a stage is not an operand, which
   reads one table. A host joins two results itself. A tail `Unpivot` never lowers (`VerbNotLowered`), and a cast
   to decimal is outside the subset (`ConstructExcluded`). `FoldSplit.suffix` answers the planned steps after the
   boundary, and `FoldSplit.describe` names them, so a host can assert that its suffix neither groups nor joins:
   that it reads only rows already at the grain it returns.

6. **The acceptance pack: the first consumer's shapes, as structure.** The test sample gains a neutral retail model
   (order headers, order lines, customers, web orders; an order key and a customer key, classified together; three
   relations; a group floor of one) and six pipelines with the consumer's structure: an order stage grouping the
   counted orders on both keys, then a left join to a grouped shopper operand on the customer key and to a grouped
   attribution operand on the order key, derived labels, and a projection renaming the keys; a line stage joining
   the lines on the renamed order key, after the grouping; and five roll-ups to a coarser grain (orders, lines,
   shoppers, new shoppers' products, campaigns) and a sixth (quality checks) that joins a first-orders operand on the
   shopper's suffixed key, re-aggregates by day and store, and ends in casts and an unpivot. Each ends in `Sort`
   then `Limit`, and each uses deterministic aggregates (`Max` where the consumer wrote `First`). Under a grant
   holding the keys row by row, the five lower in full with an empty local suffix in the reference dialect and both
   shipped dialects, and the sixth places its boundary at its first cast, right after the grouping to its grain;
   all six equal the reference evaluator through the in-memory interpreter and on both engines.

7. **Under the keyed grant, two refusals of D18 meet the shapes, and neither is bent.** The order stage keeps the
   orders of a known customer with a predicate on the customer key (a non-blank test), which D18.2 refuses as
   `KeyOnly` ("a filter predicate"); every shape inherits it. With that predicate alone removed, the orders,
   shoppers and campaigns shapes lower in full under the keyed grant and the quality shape to its grain; the lines
   and new-products shapes are still refused, because the line stage marks an order without lines by testing the
   line's order key for null inside the derives of its labels, so policy's lineage carries the key into those labels,
   and sorting by a label is a key used as a sort key. Both are findings about the keyed level and the shapes
   together, pinned by a test that names each refusal: the check and the provider admit and lower the shapes as
   written, and what refuses them is the policy's ruling, which this phase does not amend. *Reopen* D18.2 if a
   null test of a key (one bit, answered for a whole group) should be admitted as a predicate or a condition, or
   the consumer writes those two tests over a column that is not a key. *Closed 2026-11-17 (D20, Phase 42):* the
   first condition was met. D18.2 now admits a presence test of a keyed column as a filter predicate and as a
   `Case`'s condition, and its answer carries no lineage of the column, so all six shapes are admitted under the
   keyed grant as written.

**The evidence.** SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit), collation
`SQL_Latin1_General_CP1_CI_AS`, through the instance's named pipe with `Encrypt=false`: 112 fixtures, 110 agreed,
2 differing as pinned (as in D16), 0 differing unexpectedly; the six shapes agreed. PostgreSQL 18.6 (Debian
18.6-1.pgdg13+2, 64-bit) in a local container, database collation `en_US.utf8`: 112 fixtures, 111 agreed, 1
differing as pinned, 0 differing unexpectedly; the six shapes agreed; over the ICU root locale, 111, 1 and 0. Every
new fixture agreed on both engines. No conformance database survived either run.

## 2026-11-17 — D18: a keyed grant, under which a classified column keys a grouping or a join and never reaches the result (Phase 40)

A grant had two levels. Under `Aggregate` a grouping key and a join key are row-wise uses and are refused (D5.4), and
under `RowLevel` a statement returning the column is admitted. A consumer's cubes join their tables on a
pseudonymous key and group on one before aggregating to the grain they return, and the consumer's rule is that such
a key may be used inside the database and never leave it. Neither level states that rule: the first refuses every
cube, the second admits a statement returning the keys. This decision adds the level between them. Nine rulings.

1. **The level is `GrantLevel.Keyed`, between `Aggregate` and `RowLevel`.** The name says what the column is for
   in a pipeline: it keys. `KeyOnly` was considered and declined, because the level also admits everything
   `Aggregate` admits as an aggregate's input, and a name saying "only" would be false; `JoinKey` was declined as
   naming one of the two uses. The refusal that names a use outside the level is still called `KeyOnly`
   (`DB-GRANT-KEY-ONLY`), since there the word describes the subject's limit, as `AggregateOnly` does.

2. **What it admits, and what it refuses, by lineage (D5.3).** A column held at `Keyed` may be carried by a
   `Project` or `Derive`, folded into a grouping aggregate other than `First` and `Last`, and used as a grouping key
   or a join key on either side, in the pipeline and in a grouped operand (D16). Every other use of its value is
   refused as `KeyOnly`, naming the step, the model column and the use: a predicate, a sort key, a window or pivot
   input, a row compared whole, a model fit's or a prediction's input, `First` and `Last`. A result column that
   carries its value, under any name and through any derivation, is refused as `KeyInResult`
   (`DB-GRANT-KEY-IN-RESULT`), naming the result column and the model column. Lineage is the walk D5.3 already
   makes, so a rename, a `Derive`, a join's `_right` suffix and an operand's output do not get the value out.
   *Amended 2026-11-17 (D20, Phase 42):* the first consumer's pipelines test whether a key is present (a filter
   keeping the rows with a non-blank customer key, and labels chosen by whether a left join found a line), and this
   ruling refused both as predicates on the key (D19.7). A presence test, D20.1's closed set of forms over a name
   carrying the column unchanged, is admitted as a filter predicate and as a `Case`'s condition, and the value it
   answers carries no lineage of the column. Every other predicate stays refused as written, as do the branches of
   a `Case`, which keep their own lineage.

3. **A result still at the key's grain is refused.** The walk now tracks the model columns the rows are grouped
   or joined on: a grouping sets them to its keys (a `Pivot` index and a fit's group keys likewise), and a join of
   any kind, semi and anti included, adds the keys it reads on both sides and, for a join that keeps the
   operand's rows, the operand's own. When the result is reached and any of them is held at `Keyed`, the pipeline
   is refused as `KeyGrain` (`DB-GRANT-KEY-GRAIN`), naming the column. So every grouping or join keyed on such a
   column must be followed, before the result, by a grouping that is not. The argument for refusing, the
   default the phase put to the test: a row per key with the key withheld still says, row by row, what is true
   of one customer or one order, which is what the consumer's rule keeps in. Its answers become identifying by
   combination (a row whose other columns are rare names its key as surely as the key would), and the policy
   cannot know which combinations are rare; and a host that wants the key's grain wants a row-level grant,
   which says so. Admitting it would make `Keyed` a renaming of `RowLevel` with one column dropped. A semi or
   anti join keyed on the column is refused on the same terms: it is a membership test on the key, and its rows
   are at the left table's grain, which the key decided. What the walk cannot see: a grouping keyed on another
   column that is functionally one row per key (a name, say) is not the key's grain to this check; the grant
   over that column decides what it may reveal. *Reopen if* a consumer needs a key's grain inside a result that
   is then reduced outside the database, with an argument the check can verify.

4. **A key's `Min`, `Max` or `Median` is a key.** Each answers one of the group's values (a `Median` of an
   odd-sized group exactly, of an even-sized one the mean of two), so over a keyed column the answer is held as
   the key itself: the walk records it as a pick of the column, and under `Keyed` a pick is the column's value. It
   may key a later grouping or join, and may be counted or folded into another aggregate (counted distinct, for
   instance); it may not reach the result (`KeyInResult`), be filtered on or sorted by (`KeyOnly`). Under
   `Aggregate` and `RowLevel` a pick is an aggregate, as before, so D5.5's record that `Min` and `Max` over a group
   at the floor answer one row's value is unchanged for those levels. The other aggregates (`Count`,
   `CountDistinct`, `Sum`, `Mean`, `StdDev`) answer no member of the group, and their answers leave, floored, as
   under `Aggregate`. Refusing `Min` and `Max` outright was the stricter alternative; holding the answer as a key
   keeps the value in as surely, and keeps what a key's pick is good for inside the statement.

5. **The group floor is unchanged (D5.5).** A grouping that aggregates a column held at `Keyed`, or a pick of one,
   takes the floor rewrite exactly as one over an `Aggregate` column does: a `Count` per such input, a `Filter` at
   the model's floor, a `Project` back. An undeclared floor is still refused (`DB-GROUP-FLOOR-UNDECLARED`). A
   grouping keyed on the column and aggregating only row-level columns needs no floor, as before; the grain
   ruling, not the floor, keeps its rows out of the result. *Amended 2026-11-17 (D21, Phase 43):* the rewrite stands
   and its filter changes, for this level and every other: a group is dropped when some counted input has at least
   one contributor and fewer than the floor; a zero does not drop it while another input is at the floor; a floor of
   1 drops nothing. Several conditional copies of a key counted in one grouping no longer empty it (D20.6).

6. **The levels are not a chain, and a keyed grant governs an aggregate one.** Ruling 4 makes `Keyed` refuse a
   result `Aggregate` admits (a key's `Min`), and `Keyed` admits keys `Aggregate` refuses, so neither covers the
   other; `RowLevel` covers both. `Policy.level` over grants holding one classification at both `Aggregate` and
   `Keyed` answers `Keyed`: the keyed grant is the statement that the value never leaves, and a second grant
   should not quietly cancel it. `ModelProjection.isBreaking` reads the order as it is: a grant changed between
   `Aggregate` and `Keyed`, either way, is breaking, and a change to `RowLevel` is not. The projection's monotone
   and drift laws keep their claims with that one exception, which they check rather than skip: a keyed grant
   added over an aggregate one always drifts breaking.

7. **The wires.** A level's tag is `"keyed"`, beside `"aggregate"` and `"row"`, which keep their spelling and
   meaning, so every 0.3.0 stream and projection reads and every existing vector keeps its bytes. Both codecs now
   read and write the tag through `Policy.levelTag` and `Policy.tryLevel`, where each held a private copy of the
   table, and an unknown tag is refused by name (`UnknownKind`, listing `Policy.levelTags`). A 0.3.0 reader
   refuses the new tag the same way, naming the two it knew; that answer was taken from the 0.3.0 package and is
   pinned by a test. The corpus gains a projection for a subject holding `personal` at `Keyed` and a session stream
   whose admitted request records the level and whose second request, returning the key under another name, is
   refused by name. The existing vectors are not regenerated: each carries the provider version that wrote it.

8. **The AI tools carry no grant level, and none is added.** `AiTools` reads the dataset model, which holds
   classifications and no subject; a level belongs to a subject's grants. What a requester's model reads about its
   own levels is the projection (D10), which carries the new level. *Reopen if* the AI surface gains a
   subject-scoped tool.

9. **The laws.** The policy laws' sample gains a subject holding `internal` at `Keyed` and three pipelines (a
   grouping keyed on the column and re-aggregated, the same returned at the key's grain, and a join keyed on it at
   its grain); sample adequacy demands the three new codes as it demands every code. The projection laws draw
   keyed grants and a keyed-then-re-aggregated count, and demand a keyed grant, a keyed grant over an aggregate
   one, and an admission at the keyed level. The grain ruling's tests were seen red with its refusal removed.

Ruling 4 of D5 is amended in place with this decision's date and reason.

## 2026-11-16 — D17: a recording session admits and records a request's grouped operands (Phase 36)

D16 taught the check, the policy and the provider to carry grouped operands, and left the recording session as
it was: `Session.request` called `Policy.admit`, which admits no operand, so a request joining one was refused by
the session although the provider admitted and lowered it when called directly. A consumer must have both at once:
the grouped join, and the record of the run that carries its statement hash and session head. Seven rulings.

1. **`Session.requestWith` beside `Session.request`.** `requestWith` takes the operands and admits through
   `Policy.admitWith`; `request` is `requestWith` with none. It is the shape D16 chose for `check`/`checkWith` and
   `admit`/`admitWith`, for the same reason: every existing caller compiles and means what it meant, and a
   request with no operand produces exactly the ops 0.2.0 produced. Changing `request`'s signature would have
   broken every caller to say nothing new. `lower`, `guard` and `run` take no operand argument: the session keeps
   the `Admitted` it was given, which holds the operands as the policy rewrote them, and `FoldSplit.lower` and
   `FoldSplit.finish` read them from there.

2. **The record holds each operand the request joins, as written and as rewritten.** An `OperandRecord` is the
   operand's name, the model table it reads, its pipeline and how many of its literals were redacted.
   `SessionOp.Received` gains the operands as written and `SessionOp.Admitted` the operands as rewritten (its
   table's row policies first, a grouping over an aggregate-only column floored); `RequestRecord.Operands`,
   `Verdict.Admitted` and `Account.Operands` carry them. Only the operands the pipeline's joins name are recorded,
   in join order: an operand passed and never joined is not part of the request, and the policy drops it too. The
   rules applied inside an operand were already `Admitted.Applied` (D16.4's `RowPolicyApplied` at the join's
   step, `GroupFloorApplied`, and the operand's columns in `ColumnsGranted`), so the recorded rules and the
   request's reads (`Recording.reads`) already covered them; a test now pins it.

3. **An operand's literals are withheld on the pipeline's rule.** One recording decision per request (D9.2)
   covers its operands: when the request reads a classification the model does not mark recordable, every
   operand pipeline is redacted with `Recording.redact`, as written and as rewritten, and a refused request's
   operands are always recorded withheld, as its pipeline is. Each operand pipeline numbers its own withheld slots
   from 1 and counts them in its own `Redacted`.

4. **The reducer holds the record to the joins (the operands law).** `Received` folds only when its operands are
   exactly those its pipeline's joins name among them, distinct and in join order; `Admitted` folds only when its
   operands are exactly those the admitted pipeline's joins name among the received ones, each reading the table
   it was received with. Anything else is refused as `SessionRejection.OperandMismatch`
   (`DB-SESSION-OPERAND-MISMATCH`), naming both lists. `StreamLaws.operandLaw` states it over drawn requests
   recorded honestly or with an operand dropped, added, reordered or moved to another table, with its own sample
   adequacy; the stream laws' generator now mixes operand requests into its chains.

5. **The wire: an absent `operands` member is none, so a 0.2.0 stream still reads.** `received` and `admitted`
   gain an optional `operands` member, an array of `{name, table, pipeline, redacted}`, written only when there is
   an operand. A request with none is written byte for byte as 0.2.0 wrote it, every frozen stream in
   `wire-fixtures/session/` still reads, verifies, folds and re-writes to its own bytes, and a test pins both. An
   empty `operands` array is refused (`DB-WIRE-WRONG-SHAPE`): none is spelled by omission, so each value has one
   spelling. A 0.2.0 reader refuses a stream that records an operand, as its strict decoder refuses any member it
   does not know: the record is readable forward, not backward. `wire-fixtures/session/streams/operand.jsonl`
   is the new vector, admitted and refused.

6. **The in-toto predicate type does not move** (`db-session/v0.1`). The predicate summarises the session (its
   header, its record count, its statement hashes, the claim) and gains no member; its subject is the session's
   head, which already covers every operand recorded through the chain. A verifier of the predicate reads nothing
   new. *Reopen* if the predicate comes to name what each statement read.

7. **Replay does not capture an operand's rows, and says so.** Where the fold split leaves an operand join in the
   local suffix, `Session.run` resolves the operand as `Operand.resolver` does (it always did, through
   `FoldSplit.finish`), over the host's `resolve` for the operand's table. The record holds the operand's
   pipeline but not that table's rows, so `Session.replay` answers `NotCaptured`, with a reason naming the
   operand, for a finished request whose recorded suffix joins one; it never reaches a silent `Reached` or an
   unnamed `Unevaluable`. A request whose operand join lowered replays as before, from the statement's captured
   rows. Capturing an operand's table would put a second statement's worth of rows in the record under the
   first statement's capture key, which the capture model does not have. *Reopen* when a consumer needs to
   replay a suffix-side operand join; the route is a capture op per resolved source.

## 2026-11-15 — D16: a join to a grouped operand on the declared key lowers, and `Count` is pinned (Phase 34)

D4.2 admits a join only along a relation the model declares between two modelled tables, because a join is
the one verb that can fan out and the model has no cardinality to check a fan-out against. A consumer's
roll-up needs one attribution row per order: the attribution rows grouped on the order key, the most of each
field, left-joined to the orders. With the grouped side outside the subset, the boundary fell at the join and
the whole roll-up ran locally. Grouping the right side on the relation's key makes it at most one row per key,
which is checkable from the pipeline alone. The same roll-up's quality figures count the orders that state a
spend beside the orders, and whether a lowered `Count` agrees with the reference on every null shape was not
pinned by any fixture. Eight rulings.

1. **A grouped operand is a named sub-pipeline, carried beside the pipeline.** The transform algebra's sources
   are an embedded table or a name (`DataSource`), so a sub-pipeline cannot be a join's operand inside the
   algebra. An `Operand` is a name, the model name of one table, and a pipeline over that table; a join brings
   it in as `Join(Ref name, on, kind)`. What it MEANS is fixed by `Operand.resolver`: the reference evaluator's
   answer to the operand's pipeline over its table. Every lowering of it is held to that. `check` and `admit`
   keep their signatures and name no operand; `PipelineCheck.checkWith` and `Policy.admitWith` take the
   operands a pipeline's joins name. *Reopen* if the algebra gains a sub-pipeline source: the operand then
   becomes that source, and this record its wire.

2. **The check runs over the operand as over any pipeline.** Its pipeline is checked against the model over
   its table (`PipelineRefusal.Operand` wraps a refusal inside it, its steps counted within the operand). It
   may be named only by a join, by a name no table of the model holds and no other operand holds, and it
   brings in no second source (`OperandMisused` says which). The join's keys must follow a relation the model
   declares between the left side's tables and the operand's table: each right key is followed through the
   operand's renames and grouping keys to the model column it carries (`Operand.provenance`, D4.3's provenance
   over a sub-pipeline), and a key the operand derives or aggregates names no relation (`UnrelatedJoin`).

3. **It lowers when it ends in a grouping on exactly the join's keys**, and that is why it cannot fan out. The
   operand must lower in full; its last grouping may be followed only by filters and projections, which keep at
   most one row per key; and that grouping's keys, followed through those projections, must be exactly the
   keys the join reads. Then the right side holds at most one row per key, so every left row meets at most one
   right row: the join keeps or drops rows and never multiplies them, whatever the model's cardinality. A
   grouped operand that breaks any of these places the boundary as `OperandNotLowered`, naming the reason
   ("it is grouped on cust, channel, not on the join's keys cust, so the join could fan out"), and the join
   runs locally over the reference's answer to the operand. Kinds are D4.4's, `Inner`, `Left`, `Semi` and
   `Anti`. *Reopen* if `JoinSpec` gains a cardinality (D4.2's own reopening), or if a consumer needs an
   operand grouped on a superset of the key with a further reduction after it.

4. **The form: a derived table joined on the minted key columns.** The operand lowers through the same walk
   as any pipeline, from a scan of its table with aliases continuing the left side's, and the join's right
   side is that relation, rendered as a derived table (`t<n>`) by the existing renderer. Its keys are the
   aliases its grouping minted. A right column the left already names is suffixed `_right`, as the reference
   names it. The right side renders after the left, so its parameters take the ordinals after the left's,
   and the request-text law holds of every statement.

5. **The policy runs over the operand as over any pipeline.** Its columns are walked against the subject's
   grants from its own table, and a refusal inside it is placed at the step of the join that brings it in
   (`PolicySite.Step` of that join), since the operand is part of that step. An aggregate-only column is
   admitted inside the operand's aggregates and flows into the pipeline as aggregated lineage; a grouping in
   the operand over one is floored at the model's floor as any grouping is (`GroupFloorApplied` at the join's
   written step), and with no floor declared it is `FloorUndeclared` at that step. **A row policy on the
   operand's table applies inside the operand, before its grouping**: its filter is the operand's first step,
   recorded as `RowPolicyApplied` at the admitted step of the join. `Admitted.Operands` carries the operands so
   rewritten, and `explain` shows the rewrite: `SplitReport.Operands` holds each operand's planned pipeline, the
   policy's filter ahead of the grouping, and `FoldSplit.describe` says each operand join in a sentence. A left
   join to an operand over a policed table is therefore admitted, where a left join to the table itself is
   `PolicyUnwrappable`. The join cap counts the operand's join as one (the operand brings in no further
   source), and its groupings are held to the grouping-column cap at its join. *No `PolicyRule` or
   `PolicySite` case is added*: both are recorded on the session's wire and in its projections, and reading
   an operand's rules through the join that brings it in loses nothing a reader needs.

6. **`Count` is pinned against the reference, and agrees.** The kit's nine `count-*` fixtures put a lowered
   `Count` over a group whose values are all null (0), a mixed group, no input with a key (no groups) and
   without one (no row, D4.5's `NonEmpty`), nulls in a grouping key (one group), the columns a left join leaves
   null (0), a grouped operand's counts after a left join (null for the row it does not match, then skipped by
   a count of them), a count of rows beside a count of values, and a distinct count past a null. Both engines
   answer the reference's meaning on every one, so no lowering changes and nothing is pinned. *A premise of
   the phase, checked and corrected*: it spoke of `Count *`. The algebra has no row count: every aggregate
   names a column (`Agg.Of`), and the reference's `Count` counts the present values of it. A count of rows is
   a count of a column no row leaves null, or of a derived constant, which is what `count-rows-and-values`
   pins. A difference found later is a fixture's verdict, never an assumption.

7. **One difference was found, in a dialect, and fixed by spelling.** PostgreSQL's `sum` over an `integer`
   answers a `bigint`, and over a `bigint` a `numeric`. A count or a sum the statement computed is a `bigint`
   in the engine, so a sum over a grouped operand's counts answered a `numeric` the decoder refused as a
   type error (`count-operand-after-left-join`, on the engine). The dialect now spells a sum over an `int`
   column `CAST(sum(x) AS bigint)`, so an int sum is a `bigint` at every depth, and `decodeCell` refuses it past
   int32 exactly as before. T-SQL already widened every int sum to `bigint` (D8) and answers `COUNT` as `int`.

8. **The evidence.** The placement laws gain two laws, each with its sample adequacy: "a join to a grouped
   operand on the relation's keys equals the reference's join to the operand's pipeline" (every kind reached)
   and "a lowered Count equals the reference's" (a count, a count fed by a left join, and a count inside an
   operand reached). The sample draws eight operands: four that lower and four that place the boundary (wider
   than the key, ungrouped, a step that does not lower, a key the operand derives). Each law's falsifier is a
   permanent test: an engine whose join matches a null key to a null key, which the operand's null group
   exposes, and an engine whose `COUNT` counts nulls. The kit gains `Fixtures.grouped` (7) and
   `Fixtures.counts` (9), 107 in all; a fixture that joins an operand emits an `operands` member. The first
   consumer's attribution shape (orders left-joined to one attribution row per key, then rolled up with a sum
   and a distinct count) lowers to one statement with an empty local suffix and equals the reference.
   - **SQL Server**: SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit), collation
     `SQL_Latin1_General_CP1_CI_AS`, through the instance's named pipe with `Encrypt=false`: 107 fixtures,
     105 agreed, 2 differing as pinned (`difference-collation-case`, `difference-collation-order`,
     `CaseInsensitiveCollation`, as in D15), 0 differing unexpectedly. All 16 new fixtures agreed.
   - **PostgreSQL**, its first recorded run: PostgreSQL 18.6 (Debian 18.6-1.pgdg13+2, 64-bit) in a local
     container, database collation `en_US.utf8` (libc) with the kit's strings under `C`: 107 fixtures, 106
     agreed, 1 differing as pinned (`difference-code-point-order`, `CodePointOrder`), 0 differing
     unexpectedly, after ruling 7; before it, `count-operand-after-left-join` differed. Over the ICU root
     locale the dialect gave 106, 1 and 0 as well, and the falsifier went red there on
     `difference-collation-order`, as in D11.5. No conformance database survived either run.

## 2026-11-12 — D15: a statically typed `Derive` lowers, and so does a grouping keyed by it (Phase 33)

D4.4 kept `Derive` out of the declared subset on recipe 07's evidence, 20 lines on "a derived column's type
over no rows or only nulls", and named its own reopening: compute's D5 types a decided derive statically.
The first consumer needs it. It rolls a day's orders into cubes keyed by derived bands, and with `Derive`
outside the subset the fold split stopped before the first derive, so the whole grouping ran locally over
order-grain rows. Seven rulings.

1. **A `Derive` lowers when the schema decides its column's type and its expression is in the subset.**
   Four conditions, asked in this order:
   - Every node of the expression is in the declared expression subset: `Col`, values, `Binary`, `Not`,
     `IsNull`, `Coalesce`, `Case`, `InList`, `Cast` (ruling 2) and `ApplyFn`. The exclusions D4.4 names keep
     their refusal names (`ConstructExcluded` for `Lower`, `Upper`, `Length`, the exact roundings, an empty
     list, an interpolation; `UnboundParam`, `UnpinnedClock`, `UnknownColumn`), and a construct the
     dialect refuses is `DialectRefuses`, exactly as in a filter. The filter and the derive now share the
     one check.
   - The column's type is decided: the compute package's own static walk (`SchemaWalk.ofTransform`, the
     rule of its D5) names a type for it. That is the typer's decided type, or a string for an expression
     with no present value. A type the cells decide (an unbound parameter, an unpinned clock, two arms the
     exact typer keeps apart) and a refused derive (a float beside a decimal) are undecided. **An undecided
     derive places the boundary with the refusal every derive had before, `VerbNotLowered(step, "derive")`**,
     and `Refusal.describe` now says the type is what kept it.
   - The planner's totality verdict admits the step (`Plan.isTotal`), which also runs the unit check and
     requires the decided type. A derive the reference can refuse (an int sum that can overflow, a cast
     from a string) is `MayRefuse`, and runs where the reference reports it.
   - The dialect admits every construct the expression carries.

   *The evidence.* The class recipe 07 measured cannot reach a lowered derive: a decided column has its
   type on every frame, full, empty or all-null alike, and an undecided one does not lower. The kit's new
   fixtures `derive-empty`, `derive-group-empty`, `derive-all-null` and `derive-null-literal` are that class,
   and each agrees over the in-memory interpreter and over SQL Server (ruling 6).

   *A premise of the phase, checked and corrected.* The phase asked for a fixture "a `Coalesce` whose arms
   differ in type". Such a `Coalesce` is never decided: the exact typer joins `int` and `float` to
   undecided (compute D3), so it stays local by the rule above, and `derive-coalesce-mixed` shows that
   boundary. The first consumer's defaults are a string column with a string default, which is decided. The
   arms are aligned by a cast, `derive-coalesce-cast`, and then it lowers.

   *Reopen* if compute's derived-typing rule changes; if an engine run reports an unexpected difference in
   a derived value; or if a consumer needs a derive whose type its cells decide pushed down.

2. **`Cast` enters the subset along the pairs whose meaning no engine reinterprets.** D4.4 excluded it
   whole ("type affinity: the engine coerces where the reference refuses", 12 lines). The totality verdict
   now keeps every cast the reference can refuse local, which is that class. Of the casts it admits, a cast
   lowers only where SQL's meaning is the reference's: a cast to the operand's own type, and a cast of a
   null literal (each lowers as the operand, read at the target type), and three conversions the dialect
   is asked about, `int` to `float`, `int` to `bool` and `bool` to `int`. A cast to a string (each engine
   formats a number, a bit and a date its own way), to a decimal (the engine's declared scale), to a date
   or an instant (a parsed string), and from a float or a decimal to an int stays local, as
   `ConstructExcluded(Cast ty)`. Both dialects refuse a cast to every other target by name, so a wider
   subset could not reach an engine unasked. The subset is one subset: a filter's cast lowers on the same
   terms. *Reopen* a pair on a fixture that shows an engine's answer is the reference's.

3. **The form: a select of computed columns.** `Relation.Derive` carries every column through as a
   `Scalar.Column` and computes the derived one, in place when the name is already carried (the reference
   replaces the column at its position) and last otherwise. Every output takes a minted alias, as a
   `Select` does, and the derived column's field is the static walk's, unit included, so the local suffix
   reads the field the reference would have produced. `Scalar.Cast` is the one new expression form,
   rendered through the dialect's `RenderFunction (Construct.Cast ty)`. Literals are parameters (D4.3). A
   null takes its type from the decided column type or from what it is compared with, under the existing
   rule. A computed column is rendered before the select it reads from, so a derive's parameters take the
   first ordinals and the input's move up past them: the request-text law, which requires every parameter
   to be referenced once, in the order the text renders it, holds of every statement.

4. **A grouping keyed by a derived column lowers with the select that computed it, and is its own
   placement law**: "a grouping keyed by a column a lowered derive computed equals the reference grouping
   by that expression", with its own sample adequacy (`groupBy over a derived key`). Nothing in `groupBy`
   changed; a derived key is an alias like any other. The sample gained seven derives that lower (a banded
   `Case`, a `Case` with a null otherwise, a default, a cast, a derive in place, and three groupings keyed
   by or summing a derived column) and three that place the boundary (one the reference can refuse, two whose
   types the cells decide). The law's falsifier is a permanent test: an engine whose `CASE` takes the branch
   whose condition fails, and whose `COALESCE` reads its arms from the last, keeps every type and is caught.

5. **The dialects** (each README carries the table). SQL Server lowers `CASE`, `COALESCE` and the three
   casts as `CAST(x AS float)`, `CAST(x AS bit)` and `CAST(x AS int)`; a condition computed as a value keeps
   its third value through the `CASE` D8 named. PostgreSQL lowers the same table, every string still read
   under the `C` collation, since a computed string's arms are scanned columns and parameters already
   written `COLLATE "C"`. The functions and operators lower in a computed column exactly where they lower
   in a predicate, with the same refusals.
   - **One difference is recorded, not reached.** T-SQL multiplies two `decimal(38, 10)` values into
     `decimal(38, 6)`, so a decimal product with more than six fractional digits is rounded, where the
     reference is exact. A predicate has carried that since D8; a derived column can now return it. No
     fixture reaches it. The seam cannot refuse it, because `Construct.Operator Mul` carries no operand
     type, and refusing `Mul` would refuse float arithmetic too. *Reopen* the seam, or pin the difference,
     when a consumer computes such a product.

6. **The recorded SQL Server run.** SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit),
   collation `SQL_Latin1_General_CP1_CI_AS`, connected through the instance's named pipe with
   `Encrypt=false` (D11.6): 91 fixtures, 89 agreed, 2 differing as pinned (`difference-collation-case` and
   `difference-collation-order`, `CaseInsensitiveCollation`, as before), 0 differing unexpectedly. All 22
   derive fixtures agreed on the engine, the first consumer's roll-up (`derive-spend-band-rollup`) among
   them, and the engine falsifier went red as required. No conformance database survived the run. The
   PostgreSQL leg was not armed: no PostgreSQL server was installed on the machine that took the phase, so
   the in-memory run (91 of 91 agreed through that dialect) is its evidence until a run is recorded.

7. **What the split says.** `FoldSplit.derived` answers each derive that lowered, by its planned step and
   column, and `FoldSplit.describe` says each in a sentence ("step 2 derives the column 'band' in the
   statement, as a computed column of its select"). The roll-up of the first consumer's shape, a filter,
   four derives and a grouping on three of them with `Sum` and `CountDistinct`, lowers to one statement with
   an empty local suffix.

The version: two closed unions gain a case (`Relation.Derive`, `Scalar.Cast`), which is breaking, and 0.1.0
is tagged, so this is 0.2.0 (`STABILITY.md`). Every emitted fixture is re-stamped with the kit version; the
69 existing fixtures keep their content otherwise.

## 2026-11-11 — D14: published from the line its first consumers build on (Phase 32)

This repository has its first consumers, and both restore from nuget.org alone. The decisions that make it
publishable are taken together here; the two acts that publish it (the repository made public, and the
nuget.org Trusted Publishing policy registered for `Fuaran.DB*` against `publish-packages.yml`) are the operator's.

1. **The dependency line is `Fuaran.Core.*` 0.42.0 and `Fuaran.Compute.DataFrame` 0.44.0, the newest
   published.** Read at the decision from the producers' tags and from nuget.org, never from a `<Version>`:
   Core is tagged and served through 0.42.0, Compute through 0.44.0 (a 0.45.0 is cut and neither tagged nor
   served, so a public-path pin cannot name it). Its first package consumer pins 0.40.2 / 0.43.0, and a
   module that consumes both restores one version of each family, so the lines must meet. They meet on the
   newer line: the consumer raises to it before it takes its dependency on this repository. Meeting on the
   older one was measured and is feasible (the source did not change across the two lines, and the whole
   suite passed on 0.40.2 / 0.43.0), but it was declined: a pin should name a version its producer can still
   emit, so a first release built on the older line would lag from its first day, and when lines meet the
   lagging consumer moves forward rather than this repository back. So the consumer's raise to
   0.42.0 / 0.44.0 is a precondition of its dependency on these packages, not of this release. One property
   per producing repository (`FuaranCoreVersion`, `FuaranComputeVersion`) carries the line, so the two
   families can never be raised half way. _(Amended the same day by the operator's decision, which reversed
   the first choice of the lower line.)_
2. **Not a member of the Core and Compute release cohort.** The cohort is the set of build domains raised in
   one sweep when one of them cuts, because every side compiles against them. This repository consumes them
   and nothing in that set consumes it; it moves by its own changes, as the other consumer domains do. A
   cohort sweep that raises Core or Compute does not have to raise it, and its own pins are chosen by rule 1.
3. **The publication boundary is a gate** (`gates/check-publication-boundary.ps1`), run on the head by
   `run.ps1` and with `-History` by CI and by the publish workflow. It sweeps the tracked set for the names a
   reader outside this repository cannot look up: private component, product and client names, an operator
   command and tool surface, machine and actor identifiers, absolute paths, and relative paths that leave
   the repository. A confidential name is held as the SHA-256 of its lower-cased form and matched word by
   word, so the gate does not publish what it bans. A hit that cannot be fixed is an itemised, counted,
   printed entry: the copyright holder in `LICENSE`, `NOTICE` and the package metadata; the dependency guard
   of D3, which names the package family it refuses; and `pack.ps1`'s development feed default.
   `-History` reads every commit's message and every line any commit added (merges against each parent), so
   every tree in the history is read without checking one out. **The sweep at this decision**, over every
   commit reachable from any ref (28 commits, 23,959 added lines), **is clean but for one message**: a
   dependency raise records that its pins were recorded in a version ledger at the root of the tree it was
   developed in. It names no repository, product or person. A recorded message cannot be edited in an
   ordinary change; the remedies are to rewrite the published history or to publish with it, and that choice
   is the operator's at the flip. It is carried as an itemised history entry, printed by every `-History` run,
   so the choice is taken knowingly.
4. **CI and publication.** `ci.yml` runs the history sweep and `verify.ps1` on every push. `publish-packages.yml`, on
   a `v*` tag, refuses a tag that does not name `<Version>`, sweeps and gates the tagged commit, packs the six
   packages into a directory of its own, refuses any id that is not `Fuaran.DB.*`, and pushes through
   nuget.org Trusted Publishing: the `NuGet/login` step names the policy owner's public profile, never a
   secret. The engine-backed dialect legs stay Pending on both, as they are without `FUARAN_DB_SQLSERVER` and
   `FUARAN_DB_POSTGRESQL`.
5. **The version rides: 0.1.0 is the release slot.** Nothing is tagged, served or pinned from a public
   feed, so 0.1.0 is a draft. The surface guard, over the six packages packed at the previous head and at this
   one, reports every surface unchanged: the line moving down lowers each floor and changes no type. The
   first `v0.1.0` tag releases it, and `STABILITY.md` drops DRAFT from its heading in that act.

*Reopen* (1) when the consumer raises its line, or when this repository needs a Core or Compute release its
consumer does not build on; (3) when the history entry is resolved either way at the flip.

## 2026-11-05 — D13: the posture over Compute 0.43.0's new verbs and expressions

Compute 0.43.0 adds three verbs (`Define`, `Fit`, `Predict`) and three expressions (`Arg`, `Call`,
`Interpolate`). Nothing refused them before, because nothing could write them; admitting them is a change
to the posture, so it is decided here. Decided under the operator's standing direction to take the
long-term-best option on a pin raise, and open to the operator's review.

1. **A definition and a call read what their arguments read.** A function body reads no column (Compute
   D48), so `Reads.ofExpr` of a call is its arguments' reads, an `Arg` reads nothing and a `Define` step
   reads and changes nothing. An interpolation reads its operand; its knots are data.
2. **A fit is not an aggregate the group floor governs.** Its fitted and residual values are per row, and
   a coefficient over a small group can pin a row's value. Every column a fit reads — its response, keys,
   weights and every column a term or the offset reads — is therefore used row by row ("a model fit's
   input"), so an aggregate-only grant never admits one, and every output column carries the lineage of
   everything the fit read.
3. **A prediction reads its model's table as a second source.** `Reads.operandOf` names it, the pipeline
   check requires it to be modelled and to carry every column the prediction reads from it
   (`Reads.ofPredictModel`), each of those is used row by row ("a prediction's model"), a table with a row
   policy is refused as its model (`PolicyUnwrappable`: no filter after the step can remove its rows), and
   it counts against the join cap — it pairs each row with its group's model by key.
4. **Cost.** A `Coefficients` fit can return more rows than it reads, so it may not follow the bounding
   `Limit`; a fit's and a prediction's keys count against the grouping-key cap.
5. **Nothing new lowers.** The planner expands definitions before lowering; `Fit`, `Predict` and a left
   `Define` stop the prefix (`VerbNotLowered`), `Interpolate` is outside the declared subset
   (`Construct.Interpolation`), and an unexpanded call or argument is the reference's to refuse
   (`MayRefuse`).
6. **The record.** A definition's body and literal defaults, and a fit's or a prediction's terms and offset,
   are redacted as a derive's expression is, and an embedded model source is withheld. A curve's knots and
   a fit's control slots stay, as a `Limit`'s count does: neither is ever bound for a source.

*Reopen* when a phase designs a fit over aggregate-only columns under a floor, or lowers any of these.

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

**Amended 2026-10-24: a timestamp keeps its unit when reported and when it travels.** This closes the two
things the second amendment left, on the condition it named: a millisecond or microsecond column created
at its precision holds sub-second values, so the value path now follows the unit as the DDL does.

- A reported timestamp keeps the unit its precision states, the coarsest that holds its digits
  (`TimeUnit.ofDigits`). An unstated precision is the engine's default: six digits for PostgreSQL's
  `timestamp` (microseconds), seven for SQL Server's `datetime2` (nanoseconds). SQL Server's `datetime`
  reads as milliseconds (it rounds to 1/300 s, which three digits spell exactly) and `smalldatetime` as
  seconds. Reading a bare `datetime2` as nanoseconds is deliberate: it is the only unit that holds a
  100 ns value, so the column is honestly finer than the dialect can create, and a parameter of its type is
  the refused construct `5800dd7` made it. A source whose timestamps were seeded as seconds before this
  change now reports a finer unit, which `Drift.check` shows as a retype: the source states it, and the
  model said otherwise.
- A timestamp value travels at the column's unit. `parameterValue` binds a `DateTime` exact to the cell's
  fraction (refusing, by name, a fraction finer than the engine keeps: 100 ns in SQL Server, 1 microsecond
  in PostgreSQL), where it parsed seconds text and threw on a fraction. `decodeCell` writes the engine's
  value at the column's unit in Core's minimal text (`TemporalText.instantText`), and refuses one finer than
  the unit as a named `TypeError` rather than truncate it, which is what it did.
- Nanoseconds stay refused by name as a column and as a parameter, as the second amendment decided.

A test per unit per dialect pins the reading, the binding and the decoding; the armed PostgreSQL leg
creates a column at each unit, reads its unit back through `format_type`, and round-trips an instant at the
unit's full precision. SQL Server has no armed leg here, so its value path is pinned against the driver's
`DateTime` only. *Reopen if* a driver hands a timestamp as something other than a `DateTime`, or a host
needs a nanosecond column read exactly.

**Amended 2026-10-24: the connection record keeps a result column's unit.** This closes what the third
amendment left: the record wrote a statement's result columns as name and type, so a result replayed from
it stated no unit and the local suffix's unit check could not run on it.

- `StatementRecord.Columns` is the statement's schema (`Statement.schema`), a `Field list`, where it was
  `(name, type)` pairs. `Session.replay` builds the replayed table over those fields, so the recorded
  suffix meets the evaluator's unit check exactly as the live step did: a suffix comparing kilograms with
  pounds is `UnitMismatch` live and `DB-SESSION-EVAL-UNITMISMATCH` on replay.
- The wire writes each result column as Core's field document (`ColumnCodec.fieldJson`), the spelling the
  reported schema already uses (D12.3): name and type, then unit, label, description and `ext` only when
  stated. A column stating none is written as the name-and-type pair it was, so every existing vector,
  the worked streams among them, kept its bytes on regeneration, and the statement hash, which never
  covered the columns, is unchanged.
- A record written before this change still reads: a name-and-type column decodes as a field stating
  nothing. `session/legacy/before-units.jsonl` pins it. It is the worked stream as the code before this
  change wrote it, frozen, and `--write-fixtures` never regenerates it, so a later change to the record's
  form cannot rewrite its own evidence. The new form is pinned by `session/streams/measured.jsonl` and
  `session/measured/ran.json`, over the worked model with the order amount in kilograms.

A decoder older than this change refuses a result column that states a unit, by name
(`DB-WIRE-UNKNOWN-FIELD`), as it refuses any member it does not know. *Reopen if* a record must be read by
such a decoder; the units would then need to travel beside the columns rather than in them.

**Amended 2026-10-24: SQL Server's timestamp path, proved on an engine.** The third amendment pinned SQL
Server's value path against the driver's `DateTime` only. The armed SQL Server leg now carries the twin of
the PostgreSQL test, run against SQL Server 2025 LocalDB 17.0.4025.3 (Express Edition, 64-bit), with the
connection made as ruling D11.6 records. The engine agreed with the code everywhere; nothing in the dialect
changed.

- A column the dialect creates at seconds, milliseconds and microseconds (`datetime2(0)`, `(3)`, `(6)`)
  reports its unit back through the catalog (`sys.columns`), and an instant bound at the unit's full
  precision (`…:15`, `…:15.123`, `…:15.123456`) reads back whole through `decodeCell`.
- A `datetime` column, created with raw DDL, reports milliseconds. A millisecond the engine cannot hold
  exactly reads back as bound: `.123` is stored as 37/300 s, and the driver hands it back as `.123`, not as
  the 123.333… ms the engine keeps, so the reading of `datetime` as milliseconds holds at the driver, which
  is the only place a host meets it.
- A `smalldatetime` column, created with raw DDL, reports seconds, and a whole minute reads back whole. The
  engine rounds a bound value to the minute, so `…:30:15` reads back as `…:30:00`, still at seconds. The
  test pins that rounding as the engine's; a host that writes into such a column does so through DDL the
  dialect never emits, since the dialect creates timestamps as `datetime2`.

The test was seen red once, with `datetime` read as seconds. *Reopen if* another server version or driver
hands `datetime` back at a finer fraction than the millisecond.

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
   | `Derive` | `ProjectRel` whose expressions are the computed columns | mapped | added 2026-11-12 (D15): the expressions are the rows below |
   | `Not` | `not` | mapped | Kleene |
   | `IsNull` | `is_null` | mapped | |
   | `Coalesce` | `coalesce` | mapped | the format wants at least two arguments; one argument is itself |
   | `Case` | `IfThen` | mapped | an untaken branch cannot raise, as in the reference |
   | `InList` | `SingularOrList` | mapped | defined as a disjunction of `equal`s, which is three-valued membership |
   | `Cast` | `Cast`, `failure_behavior: THROW_EXCEPTION` | pinned | added 2026-11-12 (D15): the int-to-fp64 cast the provider admits is exact, but the format leaves an int-to-boolean and a boolean-to-int cast's meaning to the consumer |
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
   shows an engine agrees. No parameter reaches that consumer as a parameter. *Amended 2026-11-12 (D15):* the
   statement gained the `Derive` relation form, mapped, and the `Cast` expression form, pinned, so relation
   forms are 7 of 7 and expression forms 5 of 6, and the subset 35 of 64 mapped (54.7%), 28 pinned and 1
   unmappable. The decision stands. The suite holds this
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
   the domain resolves no identity. A grant names a classification and a level, `Aggregate` or `RowLevel`
   (and, since D18, `Keyed`).
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
   floored aggregate is wanted, with the argument that the floor covers it. *Amended 2026-11-17 (D18, Phase 40):*
   a consumer's cubes join their tables on a pseudonymous key and group on one before aggregating, so this ruling
   refused every one of them, while `RowLevel` admitted a statement returning the keys. For a column held at the
   new `Keyed` level, a grouping key and a join key are no longer row-wise uses: that level admits them besides
   what this ruling admits, and keeps the column's value and its grain out of the result. For a column held at
   `Aggregate` this ruling stands as written.
5. **The group floor is a rewrite.** A grouping that aggregates an aggregate-only column gains one `Count` per
   such input column (the evaluator's `Count` counts present cells, so it is the number of contributors), a
   `Filter` keeping groups at or above the model's floor, and a `Project` back to the written columns. Without a
   declared floor it is refused (`DB-GROUP-FLOOR-UNDECLARED`). An aggregate over already-aggregated columns needs
   no second floor. `Min` and `Max` over a group at the floor still answer one row's value; that is recorded, not
   prevented. *Reopen* with a model-declared rule naming the aggregates a classification admits. (D18.5: the
   floor holds unchanged for a column held at `Keyed`; D18.4: there a `Min` or `Max` answer is held as a key.)
   *Amended 2026-11-17 (D21, Phase 43):* keeping groups "at or above the model's floor" dropped a group whose count
   for an input was zero, an answer about no one, and did so at any floor, so a model could not say "no minimum".
   The `Filter` now drops a group when some count is at least 1 and below the floor; a zero passes while another
   count is at or above the floor, which shows the group has that many rows, and at a floor of 1 every count passes.
   With one counted input above a floor of 1 the comparison is unchanged.
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
     *Reopen* for `Derive` on that evidence (*reopened 2026-11-12 by D15: a derive whose type the schema
     decides lowers*). `Window`: the recipe did not lower it (`top-lines-per-category`
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
     (`probe-type-affinity`, `gen-1011`), and 4 pipelines stopped at a cast or a clock. *Narrowed
     2026-11-12 by D15.2: three conversions and the casts that change nothing now lower.*
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
