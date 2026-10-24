namespace Fuaran.DB.Dialect

open System
open System.Globalization
open System.Text.RegularExpressions
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB.Provider

/// The PostgreSQL dialect (`impl=Fuaran.DB.Provider.Dialect:Fuaran.DB.Dialect.PostgreSql`, Phase 1906):
/// how a statement is spelled for PostgreSQL, which constructs it refuses and why, and how a host binds
/// a parameter and decodes a result cell. Nothing here opens a connection or names a driver type: a host
/// binds `parameterValue` and hands each result cell, as the driver read it, to `decodeCell`.
///
/// Every function, operator and aggregate of the algebra is matched below with no catch-all, either
/// mapped or refused with its reason (DECISIONS.md D11). The README names each refusal and each
/// difference the dialect pins.
[<RequireQualifiedAccess>]
module PostgreSql =

    /// `"name"`, with a double quote doubled: total over any text. A quoted identifier keeps its case;
    /// PostgreSQL folds only an unquoted one, and the provider never writes one.
    let quote (identifier: string) : string =
        "\"" + identifier.Replace("\"", "\"\"") + "\""

    /// `$1`, `$2`, …: the positional marker for the 0-based ordinal.
    let marker (ordinal: int) : string = "$" + string (ordinal + 1)

    /// The collation a string is read under: byte order over UTF-8, which is code point order.
    let collation = "\"C\""

    /// The engine type a column of this type is held in, and a parameter of it is declared as. The
    /// exact decimal is an unconstrained `numeric`, which holds the reference's value exactly.
    let mapType (t: ColumnType) : string =
        match t with
        | IntType -> "integer"
        | FloatType -> "double precision"
        | BoolType -> "boolean"
        | StringType -> "text"
        | DateType -> "date"
        | TimestampType _ -> "timestamp(0) without time zone"
        | DecimalType -> "numeric"

    let private modifiers = Regex(@"\([^)]*\)")

    /// The other direction: the column type an engine type name reads as, ignoring its length,
    /// precision and scale, or `None` for a type the columnar model has no column for (`bigint`, which
    /// outgrows int32; `timestamp with time zone`, which carries an offset; `character`, which compares
    /// with its padding ignored; `money`, which formats by the session's locale; binary and the rest).
    let columnType (engineType: string) : ColumnType option =
        let name =
            modifiers
                .Replace(engineType.Trim().ToLowerInvariant(), "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> String.concat " "

        match name with
        | "integer"
        | "int"
        | "int4"
        | "smallint"
        | "int2" -> Some IntType
        | "double precision"
        | "float8"
        | "real"
        | "float4" -> Some FloatType
        | "boolean"
        | "bool" -> Some BoolType
        | "text"
        | "varchar"
        | "character varying" -> Some StringType
        | "date" -> Some DateType
        | "timestamp"
        | "timestamp without time zone" -> Some(TimestampType TimeUnit.Seconds)
        | "numeric"
        | "decimal" -> Some DecimalType
        | _ -> None

    /// A value read into the statement: a string under the C collation, so it compares, groups and
    /// orders by code point as the reference orders it by code unit (DECISIONS.md D11.2); any other
    /// value as it is.
    let readValue (t: ColumnType) (value: string) : string =
        match t with
        | StringType -> "(" + value + " COLLATE " + collation + ")"
        | IntType
        | FloatType
        | BoolType
        | DateType
        | TimestampType _
        | DecimalType -> value

    let private call (name: string) (args: string list) =
        name + "(" + String.concat ", " args + ")"

    let private binary (op: string) (args: string list) =
        match args with
        | [ a; b ] -> "(" + a + " " + op + " " + b + ")"
        | _ -> call op args

    let private one (args: string list) =
        match args with
        | [ a ] -> a
        | _ -> String.concat ", " args

    /// Why each construct the dialect does not lower is refused. `None`: it lowers.
    let refuses (c: Construct) : string option =
        match c with
        | Construct.Function f ->
            match f with
            | Abs
            | Round
            | Floor
            | Ceil
            | Trim
            | Sqrt
            | Replace -> None
            | Lower
            | Upper ->
                Some
                    "PostgreSQL folds case by the collation's character classes, which under the C collation reach ASCII only, not by the invariant culture"
            | Length -> Some "PostgreSQL's length counts code points, where the reference counts UTF-16 code units"
            | Substr ->
                Some
                    "PostgreSQL's substr counts code points from 1, where the reference counts UTF-16 code units from 0"
            | DatePart ->
                Some
                    "date truncation: the reference reads a component from the first ten characters of its operand's text, a string among them, and PostgreSQL's date_part takes a date or timestamp and answers a double"
            | DateDiffDays ->
                Some
                    "date truncation: the reference reads each operand's day from the first ten characters of its text, a string among them, and PostgreSQL parses a string by the session's DateStyle"
            | Concat ->
                Some
                    "PostgreSQL's concat reads a null as empty, and renders a non-string by its own output format rather than the reference's"
            | IndexOf ->
                Some
                    "PostgreSQL's strpos counts code points from 1, where the reference counts UTF-16 code units from 0"
            | Least
            | Greatest -> Some "PostgreSQL's LEAST and GREATEST skip a null argument, where the reference propagates it"
            | Pow
            | Exp
            | Ln
            | Log10 ->
                Some
                    "not lowered: no pinned conformance fixture shows PostgreSQL's power, exp, ln and log agree with the reference on a domain edge or in the last place"
        | Construct.Operator op ->
            match op with
            | Add
            | Sub
            | Mul
            | Eq
            | Ne
            | Lt
            | Le
            | Gt
            | Ge
            | And
            | Or
            | Contains
            | StartsWith
            | EndsWith -> None
            | Div ->
                Some
                    "integer division: PostgreSQL truncates an integer quotient where the reference answers a float, raises on a zero divisor where the reference answers null, and scales a numeric quotient by its own rule"
            | Mod -> Some "PostgreSQL raises on a zero divisor, where the reference answers null"
        | Construct.Aggregate fn ->
            match fn with
            | Sum
            | Mean
            | Min
            | Max
            | Count
            | CountDistinct -> None
            | Median ->
                Some
                    "PostgreSQL's median is the ordered-set aggregate percentile_cont, which answers a double for every input type; not measured"
            | StdDev -> Some "PostgreSQL's stddev_pop sums in the engine's order; not measured"
            | First
            | Last -> Some "an aggregate over a relation reads no row order"
        | Construct.Join kind ->
            match kind with
            | Inner
            | Left
            | Right
            | Outer
            | Semi
            | Anti -> None
        | Construct.Parameter ty ->
            match ty with
            | IntType
            | FloatType
            | BoolType
            | StringType
            | DateType
            | TimestampType _
            | DecimalType -> None
        | Construct.Cast _ -> Some "PostgreSQL's CAST coerces where the reference refuses"
        | Construct.Rounding _ ->
            Some "decimal scale on division: PostgreSQL scales and rounds a numeric result by its own rule"
        | Construct.EmptyList -> Some "PostgreSQL cannot spell IN ()"

    /// A function or an operator over rendered arguments; only what `refuses` admits is reached.
    let private render (c: Construct) (args: string list) : string =
        match c with
        | Construct.Function f ->
            match f with
            | Abs -> call "abs" args
            // The reference rounds as sign(x) * floor(|x| + 0.5), in the operand's own arithmetic: a
            // float in float, so 0.49999999999999994 rounds to one there too, and a numeric exactly.
            // PostgreSQL's round rounds a double half to even.
            | Round -> "(sign(" + one args + ") * floor(abs(" + one args + ") + 0.5))"
            | Floor -> call "floor" args
            | Ceil -> call "ceil" args
            // The reference trims its pinned ASCII set, not every space-like character.
            | Trim -> "btrim(" + one args + ", chr(32) || chr(9) || chr(13) || chr(10))"
            // A negative root is null in the reference; sqrt raises on one.
            | Sqrt -> "(CASE WHEN " + one args + " < 0 THEN NULL ELSE sqrt(" + one args + ") END)"
            // replace matches literally and case-sensitively, whatever the collation, and an empty
            // search leaves the text as it is, as the reference does.
            | Replace -> call "replace" args
            | Lower -> call "lower" args
            | Upper -> call "upper" args
            | Length -> call "length" args
            | Substr -> call "substr" args
            | DatePart -> call "date_part" args
            | DateDiffDays -> call "DATEDIFF" args
            | Concat -> call "concat" args
            | IndexOf -> call "strpos" args
            | Least -> call "LEAST" args
            | Greatest -> call "GREATEST" args
            // Refused above; spelled for totality only, and never reached.
            | Pow
            | Exp
            | Ln
            | Log10 -> call ((sprintf "%A" f).ToUpperInvariant()) args
        | Construct.Operator op ->
            match op with
            | Add -> binary "+" args
            | Sub -> binary "-" args
            | Mul -> binary "*" args
            | Div -> binary "/" args
            | Mod -> binary "%" args
            | Eq -> binary "=" args
            | Ne -> binary "<>" args
            | Lt -> binary "<" args
            | Le -> binary "<=" args
            | Gt -> binary ">" args
            | Ge -> binary ">=" args
            | And -> binary "AND" args
            | Or -> binary "OR" args
            // The substring tests read no wildcard: strpos and starts_with match the text literally,
            // and a suffix is the last characters compared for equality.
            | Contains ->
                match args with
                | [ a; b ] -> "(strpos(" + a + ", " + b + ") > 0)"
                | _ -> call "strpos" args
            | StartsWith -> call "starts_with" args
            | EndsWith ->
                match args with
                | [ a; b ] -> "(right(" + a + ", length(" + b + ")) = " + b + ")"
                | _ -> call "right" args
        // Aggregates render through `aggregate`, joins and parameters through the provider's renderer,
        // and the excluded constructs never reach a dialect; spelled for totality only.
        | Construct.Aggregate fn -> call ((sprintf "%A" fn).ToLowerInvariant()) args
        | Construct.Join _
        | Construct.Parameter _
        | Construct.Cast _
        | Construct.Rounding _
        | Construct.EmptyList -> one args

    /// An aggregate over a rendered column of this type.
    let private aggregate (fn: AggFn) (ty: ColumnType) (arg: string) : string =
        match fn with
        // count answers a bigint, which `decodeCell` narrows.
        | Count -> "count(" + arg + ")"
        | CountDistinct -> "count(DISTINCT " + arg + ")"
        // sum over integer answers a bigint, so it reaches `decodeCell`, which refuses it past int32
        // exactly as the reference does.
        | Sum -> "sum(" + arg + ")"
        // avg over integer answers a numeric; the reference's mean is a float.
        | Mean -> "avg(CAST(" + arg + " AS double precision))"
        | Min
        | Max ->
            match ty with
            // PostgreSQL has no min or max over boolean; bool_and and bool_or are they, nulls skipped.
            | BoolType -> (if fn = Min then "bool_and(" else "bool_or(") + arg + ")"
            | IntType
            | FloatType
            | DecimalType
            | StringType
            | DateType
            | TimestampType _ -> (if fn = Min then "min(" else "max(") + arg + ")"
        | Median
        | StdDev
        | First
        | Last -> render (Construct.Aggregate fn) [ arg ]

    /// `ORDER BY` the keys with nulls last in either direction, then `LIMIT … OFFSET`. The provider
    /// clamps both counts at zero, and `LIMIT 0` keeps no rows.
    let private paging (keys: (string * SortDir) list) (limit: string) (offset: string) : string =
        let order =
            match keys with
            | [] -> ""
            | _ ->
                "ORDER BY "
                + (keys
                   |> List.map (fun (k, dir) -> k + (if dir = Asc then " ASC" else " DESC") + " NULLS LAST")
                   |> String.concat ", ")
                + " "

        order + "LIMIT " + limit + " OFFSET " + offset

    /// The dialect.
    let dialect: Dialect =
        { Name = "postgresql"
          QuoteIdentifier = quote
          ParameterMarker = marker
          ReadValue = readValue
          RenderFunction = render
          RenderAggregate = aggregate
          // PostgreSQL has a boolean type: a boolean value is a condition, and a condition a value.
          ValueAsCondition = id
          ConditionAsValue = id
          RenderPaging = paging
          MapType = mapType
          Refuses = refuses
          // The write-ahead log position moves for every database of the cluster and misses an unlogged
          // table: no exact counter. A source over it is fingerprinted by row count until a model
          // declares otherwise.
          ChangeCounter = None }

    // ---- what a host needs to bind and to read ----

    let private dateFormat = "yyyy-MM-dd"
    let private timestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'"

    /// The value a host binds for a parameter cell: `DBNull` for a null, a `DateOnly` for a date, a
    /// `DateTime` of unspecified kind for a timestamp (a `timestamp without time zone`), a `decimal` for
    /// an exact decimal. `Error` for a decimal outside the range the .NET decimal holds, which a host
    /// binds as text if its driver allows.
    let parameterValue (c: Cell) : Result<obj, string> =
        match c with
        | Null -> Ok(box DBNull.Value)
        | Int i -> Ok(box i)
        | Float f -> Ok(box f)
        | Bool b -> Ok(box b)
        | Str s -> Ok(box s)
        | Date s -> Ok(box (DateOnly.ParseExact(s, dateFormat, CultureInfo.InvariantCulture)))
        | Timestamp s ->
            Ok(
                box (
                    DateTime.SpecifyKind(
                        DateTime.ParseExact(s, timestampFormat, CultureInfo.InvariantCulture),
                        DateTimeKind.Unspecified
                    )
                )
            )
        | Decimal s ->
            match Decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, d -> Ok(box d)
            | false, _ -> Error(sprintf "the decimal %s is outside the range a .NET decimal holds" s)

    /// A result cell as the driver read it, decoded into the column type the statement selects. An
    /// `int` column read as a 64-bit value is a `count` or a `sum`, refused past int32 exactly as the
    /// reference refuses a sum; a value of a shape the type does not admit is a named type error.
    let decodeCell (ty: ColumnType) (value: obj) : Result<Cell, EvalError> =
        let unexpected () =
            Error(
                TypeError(
                    sprintf
                        "the engine answered a %s where the statement selects %s"
                        (value.GetType().Name)
                        (ColumnType.tag ty)
                )
            )

        match value with
        | null -> Ok Null
        | :? DBNull -> Ok Null
        | _ ->
            match ty, value with
            | IntType, (:? int as i) -> Ok(Int i)
            | IntType, (:? int16 as i) -> Ok(Int(int i))
            | IntType, (:? int64 as i) ->
                if i < int64 Int32.MinValue || i > int64 Int32.MaxValue then
                    Error(OverflowError("sum overflowed int32: " + string i))
                else
                    Ok(Int(int i))
            | FloatType, (:? float as f) -> Ok(Float f)
            | FloatType, (:? float32 as f) -> Ok(Float(float f))
            | BoolType, (:? bool as b) -> Ok(Bool b)
            | StringType, (:? string as s) -> Ok(Str s)
            | DateType, (:? DateOnly as d) -> Ok(Date(d.ToString(dateFormat, CultureInfo.InvariantCulture)))
            | DateType, (:? DateTime as d) -> Ok(Date(d.ToString(dateFormat, CultureInfo.InvariantCulture)))
            | TimestampType _, (:? DateTime as d) ->
                Ok(Timestamp(d.ToString(timestampFormat, CultureInfo.InvariantCulture)))
            | DecimalType, (:? decimal as d) ->
                match Cell.decimal (d.ToString(CultureInfo.InvariantCulture)) with
                | Some c -> Ok c
                | None -> unexpected ()
            | DecimalType, (:? string as s) ->
                match Cell.decimal s with
                | Some c -> Ok c
                | None -> unexpected ()
            | _ -> unexpected ()

    /// The statement that creates a table of these columns, each nullable, in the database's own
    /// collation: the dialect reads strings under the C collation whatever a column declares.
    let createTable (table: string) (columns: (string * ColumnType) list) : string =
        "CREATE TABLE "
        + quote table
        + " ("
        + (columns
           |> List.map (fun (c, t) -> quote c + " " + mapType t + " NULL")
           |> String.concat ", ")
        + ")"

    /// The statement that drops a table if it exists.
    let dropTable (table: string) : string = "DROP TABLE IF EXISTS " + quote table

    /// The statement that inserts one row into these columns, its values the markers `$1` onwards.
    let insertRow (table: string) (columns: string list) : string =
        "INSERT INTO "
        + quote table
        + " ("
        + (columns |> List.map quote |> String.concat ", ")
        + ") VALUES ("
        + (columns |> List.mapi (fun i _ -> marker i) |> String.concat ", ")
        + ")"
