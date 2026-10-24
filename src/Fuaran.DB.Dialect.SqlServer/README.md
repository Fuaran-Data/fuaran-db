# Fuaran.DB.Dialect.SqlServer

The SQL Server (T-SQL) dialect of the Fuaran.DB provider. It decides how a statement is spelled, which
constructs it does not lower, and how a host binds a parameter and decodes a result cell. It opens no
connection and references no driver: the host binds `SqlServer.parameterValue` and hands each result cell,
as its driver read it, to `SqlServer.decodeCell`.

## Spelling

- **Identifiers** are bracketed, a closing bracket doubled: `ORD_NOTE]X` is `[ORD_NOTE]]X]`.
- **Parameters** are named markers, `@p0`, `@p1`, …; a host declares each with the engine type below.
- **Paging** is `ORDER BY … OFFSET @off ROWS FETCH NEXT … ROWS ONLY`, each key preceded by
  `CASE WHEN key IS NULL THEN 1 ELSE 0 END` so nulls sort last in either direction. T-SQL refuses a fetch of
  no rows, so the page also carries `WHERE @limit > 0` and its fetch count never falls below one: a page
  of zero rows is a valid statement that keeps none.
- **Booleans.** T-SQL has no boolean type: a bit read where a condition is required is spelled `(v = 1)`,
  and a condition read where a value is required is
  `(CASE WHEN c THEN CAST(1 AS bit) WHEN NOT c THEN CAST(0 AS bit) END)`, which keeps its third value.

## The type map

| Column type | Engine type | Read back from |
|---|---|---|
| `int` | `int` | `int`, `smallint`, `tinyint` |
| `float` | `float` | `float`, `real` |
| `bool` | `bit` | `bit` |
| `string` | `nvarchar(max)` | `nvarchar`, `varchar`, `nchar`, `char` |
| `date` | `date` | `date` |
| `timestamp` (seconds) | `datetime2(0)` | `datetime2`, `datetime`, `smalldatetime` |
| `timestamp` (milliseconds) | `datetime2(3)` | |
| `timestamp` (microseconds) | `datetime2(6)` | |
| `timestamp` (nanoseconds) | refused | |
| `decimal` | `decimal(38, 10)` | `decimal`, `numeric`, `money`, `smallmoney` |

The exact decimal is `decimal(38, 10)`: a value with more than ten digits after the point is rounded by
the engine when it is stored, and a value outside the .NET decimal's range is refused by
`parameterValue` by name. `bigint`, `datetimeoffset`, binary and spatial types read as no column type.
A timestamp keeps the fractional digits its unit states. `datetime2` holds at most seven (100 ns), so a
nanosecond timestamp is refused by name (`typeRefusal`): `createTable` answers `Error` naming the column,
and a nanosecond parameter is a refused construct. A reported timestamp still reads as seconds.

## What it lowers

Functions `Abs`, `Round` (`ROUND(x, 0)`, half away from zero, as the reference rounds), `Floor`, `Ceil`,
`Trim` (the reference's pinned set: space, tab, carriage return, line feed) and `Sqrt` (null for a negative
argument, as the reference answers, where `SQRT` would raise). Operators `+`, `-`, `*`, the comparisons,
`AND` and `OR`. Aggregates `Count`, `CountDistinct`, `Sum` (over `int` widened to `bigint`), `Mean`
(`AVG` over a `float` cast), `Min` and `Max` (over `bit` through `tinyint`). Every join kind the provider
lowers.

## Every refusal it declares

Each places the fold boundary at the step that uses it, and the step runs locally in the reference
evaluator. The conformance kit has a fixture reaching each one.

| Construct | Why |
|---|---|
| `Lower`, `Upper` | T-SQL folds case by the column's collation, not by the invariant culture |
| `Length` | T-SQL's `LEN` ignores trailing spaces |
| `Substr` | `SUBSTRING` counts from 1, shortens an out-of-range start rather than clamping it, and raises on a negative length |
| `DatePart` | date truncation: the reference reads a date from the first ten characters of its text, and `DATEPART` takes its grain as a keyword, which a parameter cannot supply |
| `DateDiffDays` | date truncation: the reference reads each operand's day from the first ten characters of its text, a string among them, and T-SQL parses a string by the session's language settings |
| `Concat` | `CONCAT` reads a null as empty, and renders a non-string by its own formatting |
| `Replace` | `REPLACE` matches under the column's collation, so it can fold case |
| `IndexOf` | `CHARINDEX` matches under the column's collation and counts from 1 |
| `Least`, `Greatest` | T-SQL skips a null argument, where the reference propagates it |
| `Div` | integer division: T-SQL truncates an integer quotient where the reference answers a float, raises on a zero divisor where the reference answers null, and scales a decimal quotient by its own rule |
| `Mod` | T-SQL raises on a zero divisor, where the reference answers null |
| `Contains`, `StartsWith`, `EndsWith` | `LIKE` reads wildcards in the pattern and matches under the column's collation |
| `Median` | T-SQL has no median aggregate; `PERCENTILE_CONT` is a window function |
| `StdDev` | `STDEVP` sums in the engine's order; not measured |
| `First`, `Last` | an aggregate over a relation reads no row order |
| `Cast` | `CAST` coerces where the reference refuses |
| `Quotient`, `Rounded` | decimal scale on division: T-SQL scales and rounds a decimal result by its own rule |
| an empty `IN` list | T-SQL cannot spell `IN ()` |

`Lower`, `Upper`, `Length`, `Median`, `StdDev`, `First`, `Last`, `Cast`, the exact roundings and the empty
list are also outside the provider's own declared subset, which refuses them first.

## Every difference it pins

| Difference | How it is pinned | Fixture |
|---|---|---|
| Collation and case | Declared: strings compare, group and order under the column's collation. Under a case-insensitive one an answer may be the reference's over case-folded strings, and the kit accepts exactly that answer as differing as pinned. The kit's other fixtures keep strings distinct under any collation; trailing spaces, which T-SQL ignores in a comparison, are kept out of them too. | `difference-collation-case`, `difference-collation-order` |
| Null ordering in `ORDER BY` | Spelled: every page key sorts nulls last explicitly. | `difference-null-ordering-asc`, `-desc` |
| Integer division | Refused: `Div` and `Mod` run locally. | `difference-integer-division` |
| Float summation order | Declared: a float `Sum` or `Mean` may add in the engine's order; the kit accepts the reference's answer under any order of the source rows. | `difference-float-summation-order` |
| Decimal scale on division | Refused: `Div`, `Quotient` and `Rounded` run locally. | `difference-decimal-scale-on-division` |
| Date truncation | Refused: `DatePart` and `DateDiffDays` run locally. | `difference-date-truncation` |
| The integer width of a pushed `Sum` | Decoded: a `Sum` over `int` is widened to `bigint` in the engine and refused past int32 by `decodeCell`, with the reference's own refusal. | `sum-int-overflow` |

## Certification

`Fuaran.DB.Conformance` runs its fixtures through this dialect and a host's executor and reports each as
agreed, differing as pinned or differing unexpectedly. The repository's suite runs it against a server
when `FUARAN_DB_SQLSERVER` holds a connection string, creating and dropping a database for the run, and
reports the leg as Pending when it does not.
