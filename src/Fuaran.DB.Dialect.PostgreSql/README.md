# Fuaran.DB.Dialect.PostgreSql

The PostgreSQL dialect of the Fuaran.DB provider. It decides how a statement is spelled, which constructs it
does not lower, and how a host binds a parameter and decodes a result cell. It opens no connection and
references no driver. The host binds `PostgreSql.parameterValue`, and hands each result cell, as its driver
read it, to `PostgreSql.decodeCell`. It needs PostgreSQL 11 or later, for `starts_with`.

## Spelling

- **Identifiers** are double-quoted, and a double quote inside one is doubled: `a"b` is `"a""b"`. A quoted
  identifier keeps its case. The provider never writes an unquoted one, so a table created unquoted (and
  therefore folded to lower case) must be modelled under its lower-case name.
- **Parameters** are positional markers, `$1`, `$2`, …. The marker for ordinal 0 is `$1`. A host adds the
  parameters in ordinal order, each with the engine type below.
- **Strings are read under the `C` collation.** Every scanned string column and every string parameter is
  written `(… COLLATE "C")`, so strings compare, group and order by code point, whatever the column or the
  database declares. A linguistic collation would order `a` before `B`, which the reference does not.
- **Paging** is `ORDER BY key ASC|DESC NULLS LAST, … LIMIT $n OFFSET $m`. The provider has already clamped
  both counts at zero.
- **Booleans** are PostgreSQL's own: a boolean value is a condition, and a condition is a value.

## The type map

| Column type | Engine type | Read back from |
|---|---|---|
| `int` | `integer` | `integer`, `int`, `int4`, `smallint`, `int2` |
| `float` | `double precision` | `double precision`, `float8`, `real`, `float4` |
| `bool` | `boolean` | `boolean`, `bool` |
| `string` | `text` | `text`, `varchar`, `character varying` |
| `date` | `date` | `date` |
| `timestamp` (seconds) | `timestamp(0) without time zone` | `timestamp(0)` |
| `timestamp` (milliseconds) | `timestamp(3) without time zone` | `timestamp(1)` to `(3)` |
| `timestamp` (microseconds) | `timestamp(6) without time zone` | `timestamp(4)` to `(6)`, `timestamp`, `timestamp without time zone` |
| `timestamp` (nanoseconds) | refused | |
| `decimal` | `numeric` | `numeric`, `decimal` |

The exact decimal is an unconstrained `numeric`, which holds the reference's value exactly. A value outside
the .NET decimal's range is refused by `parameterValue` by name. A timestamp keeps the fractional digits
its unit states. `timestamp` holds at most six (1 microsecond), so a nanosecond timestamp is refused by
name (`typeRefusal`): `createTable` answers `Error` naming the column, and a nanosecond parameter is a
refused construct. A reported timestamp keeps the unit its precision states, the coarsest that holds its
digits (a bare `timestamp` is the engine's default of six). A timestamp value travels at its column's
unit: `parameterValue` binds a `DateTime` exact to the cell's fraction, and `decodeCell` writes the
engine's value at the column's unit, refusing by name one finer than the unit rather than truncating it.
The following read as no column type:
- `bigint`, which outgrows int32.
- `timestamp with time zone`, which carries an offset.
- `character`, which compares with its padding ignored.
- `money`, which formats by the session's locale.
- Binary and every other type.

A date binds as a `DateOnly`, and a timestamp as a `DateTime` of unspecified kind.

## What it lowers

- **Functions:**
  - `Abs`.
  - `Round`, spelled `sign(x) * floor(abs(x) + 0.5)`. That is the reference's own rounding, in the
    operand's arithmetic, where `round` would round a double half to even.
  - `Floor` and `Ceil`.
  - `Trim`, over the reference's pinned set: space, tab, carriage return and line feed.
  - `Sqrt`, which answers null for a negative argument as the reference does, where `sqrt` would raise.
  - `Replace`, which is literal and case-sensitive. An empty search leaves the text as it is.
- **Operators:** `+`, `-`, `*`, the comparisons, `AND` and `OR`. The substring tests read no wildcard:
  `Contains` is `strpos(a, b) > 0`, `StartsWith` is `starts_with(a, b)`, and `EndsWith` compares
  `right(a, length(b))` with `b`.
- **Aggregates:**
  - `Count` and `CountDistinct`. Both answer a `bigint`, which `decodeCell` narrows.
  - `Sum`. Over `integer` it answers a `bigint`, which `decodeCell` refuses past int32 with the reference's
    own refusal.
  - `Mean`, as `avg` over a `double precision` cast.
  - `Min` and `Max`. Over `boolean` they are `bool_and` and `bool_or`.
- **Joins:** every join kind the provider lowers.
- **Derived columns** (`DECISIONS.md` D15): a `Derive` the provider lowers is a select of computed
  columns, and a grouping keyed by one groups that select. A computed string is read under `C` like any
  other, because its arms are scanned columns and parameters, each already written `COLLATE "C"`.

  | Form | PostgreSQL | Against the reference |
  |---|---|---|
  | `Case` | `(CASE WHEN … THEN … ELSE … END)`; a missing otherwise is a typed null parameter | matches |
  | `Coalesce` | `COALESCE(…)` over arms of one type | matches; arms of two types are undecided and stay local |
  | `Cast` to `double precision` from `integer` | `CAST(x AS double precision)` | matches: exact over int32 |
  | `Cast` to `boolean` from `integer` | `CAST(x AS boolean)` | matches: any non-zero integer is true |
  | `Cast` to `integer` from `boolean` | `CAST(x AS integer)` | matches: 1 and 0 |
  | `Cast` to `text`, `numeric`, `date`, `timestamp` | refused | PostgreSQL formats a float and a boolean as text by its own rules, keeps a scale the reference does not state, and parses a string by the session's `DateStyle` |
  | `Not`, `IsNull`, `InList`, a comparison | a condition, which is a boolean value here | matches |
  | the functions and operators above | as in a predicate | matches |

## Every refusal it declares

Each refusal places the fold boundary at the step that uses the construct, and that step runs locally in the
reference evaluator. The conformance kit has a fixture that reaches each one.

| Construct | Why |
|---|---|
| `Lower`, `Upper` | case folding follows the collation's character classes, which under `C` reach ASCII only |
| `Length` | `length` counts code points, where the reference counts UTF-16 code units |
| `Substr` | `substr` counts code points from 1, where the reference counts UTF-16 code units from 0 |
| `IndexOf` | `strpos` counts code points from 1, where the reference counts UTF-16 code units from 0 |
| `DatePart` | date truncation: the reference reads a component from the first ten characters of its operand's text, a string among them, and `date_part` takes a date or timestamp and answers a double |
| `DateDiffDays` | date truncation: the reference reads each operand's day from the first ten characters of its text, and PostgreSQL parses a string by the session's `DateStyle` |
| `Concat` | `concat` reads a null as empty, and renders a non-string by its own output format |
| `Least`, `Greatest` | PostgreSQL skips a null argument, where the reference propagates it |
| `Div` | integer division: PostgreSQL truncates an integer quotient where the reference answers a float, raises on a zero divisor where the reference answers null, and scales a numeric quotient by its own rule |
| `Mod` | PostgreSQL raises on a zero divisor, where the reference answers null |
| `Median` | the median is the ordered-set aggregate `percentile_cont`, which answers a double for every input type; not measured |
| `StdDev` | `stddev_pop` sums in the engine's order; not measured |
| `First`, `Last` | an aggregate over a relation reads no row order |
| `Cast` to `text`, `numeric`, `date`, `timestamp` | `CAST` formats, scales and parses by PostgreSQL's own rules (D15) |
| `Quotient`, `Rounded` | decimal scale on division: PostgreSQL scales and rounds a numeric result by its own rule |
| an empty `IN` list | PostgreSQL cannot spell `IN ()` |

## Every difference it pins

The last column is what the recorded engine run measured (PostgreSQL 17.11, `DECISIONS.md` D11.5).

| Difference | How it is pinned | Fixture | On the engine |
|---|---|---|---|
| Collation and case | Spelled: strings are read under `C`, so they compare case-sensitively and by code point. The armed leg requires that no fixture differ as the case-insensitive pin predicts. | `difference-collation-case`, `difference-collation-order` | agreed, under a libc database and an ICU one |
| Code point order | Declared: under `C`, strings order by code point, where the reference orders them by UTF-16 code unit. They part only where a character beyond the Basic Multilingual Plane meets one in U+E000 to U+FFFF. The kit accepts the reference's answer over strings re-encoded to code-point order (`CodePointOrder`). | `difference-code-point-order` | differing as pinned (`CodePointOrder`), the one fixture that does |
| Null ordering in `ORDER BY` | Spelled: every page key is `NULLS LAST`. | `difference-null-ordering-asc`, `-desc` | agreed |
| Integer division | Refused: `Div` and `Mod` run locally. | `difference-integer-division` | agreed, boundary at the refusal |
| Float summation order | Declared: a float `Sum` or `Mean` may add in the engine's order. | `difference-float-summation-order` | agreed |
| Decimal scale on division | Refused: `Div`, `Quotient` and `Rounded` run locally. | `difference-decimal-scale-on-division` | agreed, boundary at the refusal |
| Date truncation | Refused: `DatePart` and `DateDiffDays` run locally. | `difference-date-truncation` | agreed, boundary at the refusal |
| The integer width of a pushed `Sum` | Decoded: the engine answers a `bigint`, and `decodeCell` refuses it past int32 with the reference's own refusal. | `sum-int-overflow` | agreed |

## Certification

`Fuaran.DB.Conformance` runs its fixtures through this dialect and a host's executor, and reports each one as
agreed, differing as pinned, or differing unexpectedly. When `FUARAN_DB_POSTGRESQL` holds a connection
string, the repository's suite runs the kit against that server. It creates a UTF-8 database from
`template0` for the run and drops it afterwards, which needs PostgreSQL 13. It runs the falsifier, a dialect
that reads strings bare, over a database under the ICU root locale, which needs PostgreSQL 15, and runs this
dialect over that same database, where it must still agree. When the variable is not set, the suite reports
the leg as Pending.

**The recorded run** (`DECISIONS.md` D11.5) was made on PostgreSQL 17.11 (x86-64 build, under emulation on
ARM64 Windows), in a UTF-8 database under the libc collation `English_United Kingdom.1252`. Of 64 fixtures,
63 agreed, 1 differed as pinned (`difference-code-point-order`) and 0 differed unexpectedly, before any
change to this dialect, which therefore needed none. Over the ICU root locale this dialect also gave 63,
1 and 0, and the falsifier went red there on `difference-collation-order`. That run covers what the
in-memory run cannot: the spelling, the type map in both directions, result decoding, the `COLLATE "C"`
read, the `Round` formula (`filter-round`), and the four constructs SQL Server refuses (`reach-replace`,
`reach-contains`, `reach-starts-with`, `reach-ends-with`).

The derive fixtures (`DECISIONS.md` D15) have not yet been run on a PostgreSQL engine: the in-memory run
lowers all 91 fixtures through this dialect and agrees on all 91, and the engine leg arms as before.
