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
| `timestamp` | `timestamp(0) without time zone` | `timestamp`, `timestamp without time zone` |
| `decimal` | `numeric` | `numeric`, `decimal` |

The exact decimal is an unconstrained `numeric`, which holds the reference's value exactly. A value outside
the .NET decimal's range is refused by `parameterValue` by name. The following read as no column type:
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
| `Cast` | `CAST` coerces where the reference refuses |
| `Quotient`, `Rounded` | decimal scale on division: PostgreSQL scales and rounds a numeric result by its own rule |
| an empty `IN` list | PostgreSQL cannot spell `IN ()` |

## Every difference it pins

| Difference | How it is pinned | Fixture |
|---|---|---|
| Collation and case | Spelled: strings are read under `C`, so they compare case-sensitively and by code point. The armed leg requires that no fixture differ as the case-insensitive pin predicts. | `difference-collation-case`, `difference-collation-order` |
| Code point order | Declared: under `C`, strings order by code point, where the reference orders them by UTF-16 code unit. They part only where a character beyond the Basic Multilingual Plane meets one in U+E000 to U+FFFF. The kit accepts the reference's answer over strings re-encoded to code-point order (`CodePointOrder`). | `difference-code-point-order` |
| Null ordering in `ORDER BY` | Spelled: every page key is `NULLS LAST`. | `difference-null-ordering-asc`, `-desc` |
| Integer division | Refused: `Div` and `Mod` run locally. | `difference-integer-division` |
| Float summation order | Declared: a float `Sum` or `Mean` may add in the engine's order. | `difference-float-summation-order` |
| Decimal scale on division | Refused: `Div`, `Quotient` and `Rounded` run locally. | `difference-decimal-scale-on-division` |
| Date truncation | Refused: `DatePart` and `DateDiffDays` run locally. | `difference-date-truncation` |
| The integer width of a pushed `Sum` | Decoded: the engine answers a `bigint`, and `decodeCell` refuses it past int32 with the reference's own refusal. | `sum-int-overflow` |

## Certification

`Fuaran.DB.Conformance` runs its fixtures through this dialect and a host's executor, and reports each one as
agreed, differing as pinned, or differing unexpectedly. When `FUARAN_DB_POSTGRESQL` holds a connection
string, the repository's suite runs the kit against that server. It creates a UTF-8 database from
`template0` for the run and drops it afterwards, which needs PostgreSQL 13. It also runs a falsifier over
a database under the ICU root locale, which needs PostgreSQL 15. When the variable is not set, the suite
reports the leg as Pending. No engine run has been recorded yet. The in-memory run checks the provider's
lowering and the form, but it never executes this dialect's text.
