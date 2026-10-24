namespace Fuaran.DB.Dialect

open System
open System.Globalization
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB.Provider

/// The SQL Server dialect (`impl=Fuaran.DB.Provider.Dialect:Fuaran.DB.Dialect.SqlServer`, Phase 1901):
/// how a statement is spelled in T-SQL, which constructs it refuses and why, and how a host binds a
/// parameter and decodes a result cell. Nothing here opens a connection or names a driver type: a
/// host binds `parameterValue` and hands each result cell, as the driver read it, to `decodeCell`.
///
/// Every function, operator and aggregate of the algebra is matched below with no catch-all, either
/// mapped or refused with its reason (DECISIONS.md D8). The README names each refusal and each
/// difference the dialect pins.
[<RequireQualifiedAccess>]
module SqlServer =

    /// `[name]`, with a closing bracket doubled: total over any text.
    let quote (identifier: string) : string =
        "[" + identifier.Replace("]", "]]") + "]"

    /// `@p0`, `@p1`, …: a named marker per ordinal.
    let marker (ordinal: int) : string = "@p" + string ordinal

    /// The engine type a column of this type is held in, and a parameter of it is declared as. The
    /// exact decimal is `decimal(38, 10)`: thirty-eight digits, ten of them after the point.
    let mapType (t: ColumnType) : string =
        match t with
        | IntType -> "int"
        | FloatType -> "float"
        | BoolType -> "bit"
        | StringType -> "nvarchar(max)"
        | DateType -> "date"
        | TimestampType _ -> "datetime2(0)"
        | DecimalType -> "decimal(38, 10)"

    /// The other direction: the column type an engine type name reads as, ignoring its length,
    /// precision and scale, or `None` for a type the columnar model has no column for (`bigint`, which
    /// outgrows int32; `datetimeoffset`, which carries an offset; binary and spatial types).
    let columnType (engineType: string) : ColumnType option =
        let name =
            let t = engineType.Trim().ToLowerInvariant()

            match t.IndexOf '(' with
            | -1 -> t
            | i -> t.Substring(0, i).Trim()

        match name with
        | "int"
        | "smallint"
        | "tinyint" -> Some IntType
        | "float"
        | "real" -> Some FloatType
        | "bit" -> Some BoolType
        | "nvarchar"
        | "varchar"
        | "nchar"
        | "char" -> Some StringType
        | "date" -> Some DateType
        | "datetime2"
        | "datetime"
        | "smalldatetime" -> Some(TimestampType TimeUnit.Seconds)
        | "decimal"
        | "numeric"
        | "money"
        | "smallmoney" -> Some DecimalType
        | _ -> None

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
            | Sqrt -> None
            | Lower
            | Upper -> Some "T-SQL folds case by the column's collation, not by the invariant culture"
            | Length -> Some "T-SQL's LEN ignores trailing spaces"
            | Substr ->
                Some
                    "T-SQL's SUBSTRING counts from 1, shortens an out-of-range start rather than clamping it, and raises on a negative length"
            | DatePart ->
                Some
                    "date truncation: the reference reads a date from the first ten characters of its text, and T-SQL's DATEPART takes its grain as a keyword, which a parameter cannot supply"
            | DateDiffDays ->
                Some
                    "date truncation: the reference reads each operand's day from the first ten characters of its text, a string among them, and T-SQL parses a string by the session's language settings"
            | Concat ->
                Some
                    "T-SQL's CONCAT reads a null as empty, and renders a non-string by its own formatting rather than the reference's"
            | Replace -> Some "T-SQL's REPLACE matches under the column's collation, so it can fold case"
            | IndexOf -> Some "T-SQL's CHARINDEX matches under the column's collation and counts from 1"
            | Least
            | Greatest -> Some "T-SQL's LEAST and GREATEST skip a null argument, where the reference propagates it"
            | Pow
            | Exp
            | Ln
            | Log10 ->
                Some
                    "not lowered: no pinned conformance fixture shows T-SQL's POWER, EXP and LOG agree with the reference on a domain edge or in the last place"
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
            | Or -> None
            | Div ->
                Some
                    "integer division: T-SQL truncates an integer quotient where the reference answers a float, raises on a zero divisor where the reference answers null, and scales a decimal quotient by its own rule"
            | Mod -> Some "T-SQL raises on a zero divisor, where the reference answers null"
            | Contains
            | StartsWith
            | EndsWith -> Some "T-SQL's LIKE reads wildcards in the pattern and matches under the column's collation"
        | Construct.Aggregate fn ->
            match fn with
            | Sum
            | Mean
            | Min
            | Max
            | Count
            | CountDistinct -> None
            | Median -> Some "T-SQL has no median aggregate; PERCENTILE_CONT is a window function"
            | StdDev -> Some "T-SQL's STDEVP sums in the engine's order; not measured"
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
        | Construct.Cast _ -> Some "T-SQL's CAST coerces where the reference refuses"
        | Construct.Rounding _ ->
            Some "decimal scale on division: T-SQL scales and rounds a decimal result by its own rule"
        | Construct.EmptyList -> Some "T-SQL cannot spell IN ()"

    /// A function or an operator over rendered arguments; only what `refuses` admits is reached.
    let private render (c: Construct) (args: string list) : string =
        match c with
        | Construct.Function f ->
            match f with
            | Abs -> call "ABS" args
            | Round -> "ROUND(" + one args + ", 0)"
            | Floor -> call "FLOOR" args
            | Ceil -> call "CEILING" args
            // The reference trims its pinned ASCII set, not every space-like character.
            | Trim -> "TRIM(NCHAR(32) + NCHAR(9) + NCHAR(13) + NCHAR(10) FROM " + one args + ")"
            // A negative root is null in the reference; SQRT raises on one.
            | Sqrt -> "(CASE WHEN " + one args + " < 0 THEN NULL ELSE SQRT(" + one args + ") END)"
            | Lower -> call "LOWER" args
            | Upper -> call "UPPER" args
            | Length -> call "LEN" args
            | Substr -> call "SUBSTRING" args
            | DatePart -> call "DATEPART" args
            | DateDiffDays -> call "DATEDIFF" args
            | Concat -> call "CONCAT" args
            | Replace -> call "REPLACE" args
            | IndexOf -> call "CHARINDEX" args
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
            | Contains
            | StartsWith
            | EndsWith -> binary "LIKE" args
        // Aggregates render through `aggregate`, joins and parameters through the provider's renderer,
        // and the excluded constructs never reach a dialect; spelled for totality only.
        | Construct.Aggregate fn -> call ((sprintf "%A" fn).ToUpperInvariant()) args
        | Construct.Join _
        | Construct.Parameter _
        | Construct.Cast _
        | Construct.Rounding _
        | Construct.EmptyList -> one args

    /// An aggregate over a rendered column of this type.
    let private aggregate (fn: AggFn) (ty: ColumnType) (arg: string) : string =
        match fn with
        | Count -> "COUNT(" + arg + ")"
        | CountDistinct -> "COUNT(DISTINCT " + arg + ")"
        | Sum ->
            match ty with
            // SUM over int is int in T-SQL and raises past int32; widened to bigint, the sum reaches
            // `decodeCell`, which refuses it past int32 exactly as the reference does.
            | IntType -> "SUM(CAST(" + arg + " AS bigint))"
            | FloatType
            | DecimalType
            | BoolType
            | StringType
            | DateType
            | TimestampType _ -> "SUM(" + arg + ")"
        // AVG over int is an int in T-SQL; the reference's mean is a float.
        | Mean -> "AVG(CAST(" + arg + " AS float))"
        | Min
        | Max ->
            let name = if fn = Min then "MIN" else "MAX"

            match ty with
            // T-SQL has no MIN or MAX over bit.
            | BoolType -> "CAST(" + name + "(CAST(" + arg + " AS tinyint)) AS bit)"
            | IntType
            | FloatType
            | DecimalType
            | StringType
            | DateType
            | TimestampType _ -> name + "(" + arg + ")"
        | Median
        | StdDev
        | First
        | Last -> render (Construct.Aggregate fn) [ arg ]

    /// `ORDER BY` the keys with nulls last in either direction, then `OFFSET … FETCH`. T-SQL refuses a
    /// fetch of no rows, so a page of zero rows is spelled as a `WHERE` that keeps none.
    let private paging (keys: (string * SortDir) list) (limit: string) (offset: string) : string =
        let order =
            match keys with
            | [] -> "(SELECT NULL)"
            | _ ->
                keys
                |> List.collect (fun (k, dir) ->
                    [ "CASE WHEN " + k + " IS NULL THEN 1 ELSE 0 END"
                      k + (if dir = Asc then " ASC" else " DESC") ])
                |> String.concat ", "

        "WHERE "
        + limit
        + " > 0 ORDER BY "
        + order
        + " OFFSET "
        + offset
        + " ROWS FETCH NEXT (SELECT CASE WHEN "
        + limit
        + " > 0 THEN "
        + limit
        + " ELSE 1 END) ROWS ONLY"

    /// The dialect.
    let dialect: Dialect =
        { Name = "sqlserver"
          QuoteIdentifier = quote
          ParameterMarker = marker
          // A value is read as the source declares it: strings compare under the column's collation,
          // which is the difference `CaseInsensitiveCollation` pins.
          ReadValue = fun _ value -> value
          RenderFunction = render
          RenderAggregate = aggregate
          // T-SQL has no boolean type: a bit is a value, and a condition is not one.
          ValueAsCondition = fun v -> "(" + v + " = 1)"
          ConditionAsValue =
            fun c ->
                "(CASE WHEN "
                + c
                + " THEN CAST(1 AS bit) WHEN NOT "
                + c
                + " THEN CAST(0 AS bit) END)"
          RenderPaging = paging
          MapType = mapType
          Refuses = refuses
          // The engine's change tracking is per database and needs enabling; a source over it is
          // fingerprinted by row count until a model declares otherwise.
          ChangeCounter = None }

    // ---- what a host needs to bind and to read ----

    let private dateFormat = "yyyy-MM-dd"
    let private timestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'"

    /// The value a host binds for a parameter cell: `DBNull` for a null, a `DateTime` for a date or a
    /// timestamp, a `decimal` for an exact decimal. `Error` for a decimal outside the range the .NET
    /// decimal holds, which a host binds as text if its driver allows.
    let parameterValue (c: Cell) : Result<obj, string> =
        match c with
        | Null -> Ok(box DBNull.Value)
        | Int i -> Ok(box i)
        | Float f -> Ok(box f)
        | Bool b -> Ok(box b)
        | Str s -> Ok(box s)
        | Date s -> Ok(box (DateTime.ParseExact(s, dateFormat, CultureInfo.InvariantCulture)))
        | Timestamp s -> Ok(box (DateTime.ParseExact(s, timestampFormat, CultureInfo.InvariantCulture)))
        | Decimal s ->
            match Decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, d -> Ok(box d)
            | false, _ -> Error(sprintf "the decimal %s is outside the range a .NET decimal holds" s)

    /// A result cell as the driver read it, decoded into the column type the statement selects. An
    /// `int` column read as a 64-bit value is a widened `Sum`, refused past int32 exactly as the
    /// reference refuses it; a value of a shape the type does not admit is a named type error.
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
            | IntType, (:? byte as i) -> Ok(Int(int i))
            | IntType, (:? int64 as i) ->
                if i < int64 Int32.MinValue || i > int64 Int32.MaxValue then
                    Error(OverflowError("sum overflowed int32: " + string i))
                else
                    Ok(Int(int i))
            | FloatType, (:? float as f) -> Ok(Float f)
            | FloatType, (:? float32 as f) -> Ok(Float(float f))
            | BoolType, (:? bool as b) -> Ok(Bool b)
            | StringType, (:? string as s) -> Ok(Str s)
            | DateType, (:? DateTime as d) -> Ok(Date(d.ToString(dateFormat, CultureInfo.InvariantCulture)))
            | DateType, (:? DateOnly as d) -> Ok(Date(d.ToString(dateFormat, CultureInfo.InvariantCulture)))
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

    /// The statement that creates a table of these columns, each nullable.
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

    /// The statement that inserts one row into these columns, its values the markers `@p0` onwards.
    let insertRow (table: string) (columns: string list) : string =
        "INSERT INTO "
        + quote table
        + " ("
        + (columns |> List.map quote |> String.concat ", ")
        + ") VALUES ("
        + (columns |> List.mapi (fun i _ -> marker i) |> String.concat ", ")
        + ")"
