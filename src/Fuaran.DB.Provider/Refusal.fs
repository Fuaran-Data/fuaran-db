namespace Fuaran.DB.Provider

open Fuaran.Core
open Fuaran.Compute

/// Why the provider lowers no further — one case per reason (DECISIONS.md D4). Step indices count
/// the PLANNED pipeline's steps from 1; step 0 is the pipeline's source.
///
/// Three cases refuse the pipeline WHOLE, because no statement can be written at all:
/// `EmbeddedSource`, `UnknownSource`, and `EmptyRow` at step 0. Every other case places the FOLD
/// BOUNDARY: the steps before it lower to the statement, and the step it names and every step after
/// it run in the reference evaluator over the statement's result. A boundary is not an error: the
/// pipeline still means what the reference says it means, and the step that would refuse at
/// evaluation still refuses there.
[<RequireQualifiedAccess>]
type Refusal =
    /// The pipeline's source is an embedded table; a statement reads what a source holds.
    | EmbeddedSource
    /// The pipeline's source names a table the model does not hold.
    | UnknownSource of table: string * tables: string list
    /// The rows at this step carry no columns, which no statement can select.
    | EmptyRow of step: int
    /// A verb outside the declared subset: `derive`, `window`, `unpivot`, `distinct`, `union`,
    /// `intersect` or `except`.
    | VerbNotLowered of step: int * verb: string
    /// A pivot, which never lowers: it turns data values into identifiers.
    | PivotNeverLowers of step: int
    /// A `Sort` not followed by a `Limit`. A sort lowers only as the ordering of a page, and a
    /// relation keeps no order through an enclosing select.
    | UnpairedSort of step: int
    /// A `Limit` with no `Sort` before it: which rows it keeps depends on an order a relation does
    /// not have.
    | UnorderedLimit of step: int
    /// The step after an ordered page: the statement ends at its ORDER BY, because a relation keeps
    /// no order through an enclosing select.
    | AfterOrder of step: int
    /// A join kind outside the declared subset (`Right`, `Outer`).
    | JoinKindNotLowered of step: int * kind: JoinKind
    /// A join's operand is not a table of the model (an embedded table, or an unknown name).
    | OperandNotModelled of step: int * operand: string
    /// A join between tables the model does not relate on exactly those keys. Key columns are
    /// followed through renames to the model column they came from.
    | UndeclaredJoin of step: int * operand: string * on: (string * string) list
    /// A step reads a column the rows reaching it do not carry; `members` are the columns they do.
    | UnknownColumn of step: int * column: string * members: string list
    /// The step's output would carry a column name twice. A result is read back by name.
    | RepeatedName of step: int * name: string
    /// A parameter the bindings do not hold.
    | UnboundParam of step: int * name: string
    /// A `Now` with no clock pinned in the bindings.
    | UnpinnedClock of step: int * grain: NowGrain
    /// A construct outside the provider's declared subset, whatever the dialect.
    | ConstructExcluded of step: int * construct: Construct
    /// A construct the dialect declares it does not lower, with the dialect's reason.
    | DialectRefuses of step: int * construct: Construct * reason: string
    /// An evaluation the reference evaluator can refuse at this step (a predicate the planner's
    /// totality verdict does not admit, an aggregate over a type it refuses, a slot bound to a cell
    /// of the wrong shape). It runs where the reference reports it.
    | MayRefuse of step: int * detail: string
    /// A type the statement needs and the expression does not decide (a predicate that is not
    /// statically boolean, a null whose type nothing around it fixes, join keys of two types).
    | Untyped of step: int * detail: string

/// Reading a refusal.
[<RequireQualifiedAccess>]
module Refusal =

    /// The case's tag, stable, for reports and sample counts.
    let tag (r: Refusal) : string =
        match r with
        | Refusal.EmbeddedSource -> "embeddedSource"
        | Refusal.UnknownSource _ -> "unknownSource"
        | Refusal.EmptyRow _ -> "emptyRow"
        | Refusal.VerbNotLowered _ -> "verbNotLowered"
        | Refusal.PivotNeverLowers _ -> "pivotNeverLowers"
        | Refusal.UnpairedSort _ -> "unpairedSort"
        | Refusal.UnorderedLimit _ -> "unorderedLimit"
        | Refusal.AfterOrder _ -> "afterOrder"
        | Refusal.JoinKindNotLowered _ -> "joinKindNotLowered"
        | Refusal.OperandNotModelled _ -> "operandNotModelled"
        | Refusal.UndeclaredJoin _ -> "undeclaredJoin"
        | Refusal.UnknownColumn _ -> "unknownColumn"
        | Refusal.RepeatedName _ -> "repeatedName"
        | Refusal.UnboundParam _ -> "unboundParam"
        | Refusal.UnpinnedClock _ -> "unpinnedClock"
        | Refusal.ConstructExcluded _ -> "constructExcluded"
        | Refusal.DialectRefuses _ -> "dialectRefuses"
        | Refusal.MayRefuse _ -> "mayRefuse"
        | Refusal.Untyped _ -> "untyped"

    /// Every tag, in declaration order.
    let allTags =
        [ "embeddedSource"
          "unknownSource"
          "emptyRow"
          "verbNotLowered"
          "pivotNeverLowers"
          "unpairedSort"
          "unorderedLimit"
          "afterOrder"
          "joinKindNotLowered"
          "operandNotModelled"
          "undeclaredJoin"
          "unknownColumn"
          "repeatedName"
          "unboundParam"
          "unpinnedClock"
          "constructExcluded"
          "dialectRefuses"
          "mayRefuse"
          "untyped" ]

    /// The step the refusal names; 0 for a refusal of the source.
    let step (r: Refusal) : int =
        match r with
        | Refusal.EmbeddedSource
        | Refusal.UnknownSource _ -> 0
        | Refusal.EmptyRow s
        | Refusal.VerbNotLowered(s, _)
        | Refusal.PivotNeverLowers s
        | Refusal.UnpairedSort s
        | Refusal.UnorderedLimit s
        | Refusal.AfterOrder s
        | Refusal.JoinKindNotLowered(s, _)
        | Refusal.OperandNotModelled(s, _)
        | Refusal.UndeclaredJoin(s, _, _)
        | Refusal.UnknownColumn(s, _, _)
        | Refusal.RepeatedName(s, _)
        | Refusal.UnboundParam(s, _)
        | Refusal.UnpinnedClock(s, _)
        | Refusal.ConstructExcluded(s, _)
        | Refusal.DialectRefuses(s, _, _)
        | Refusal.MayRefuse(s, _)
        | Refusal.Untyped(s, _) -> s

    /// A construct as a reader would name it.
    let describeConstruct (c: Construct) : string =
        match c with
        | Construct.Function f -> sprintf "the function %A" f
        | Construct.Operator op -> sprintf "the operator %A" op
        | Construct.Aggregate fn -> sprintf "the aggregate %A" fn
        | Construct.Join kind -> sprintf "a %A join" kind
        | Construct.Parameter ty -> sprintf "a parameter of type %s" (ColumnType.tag ty)
        | Construct.Cast ty -> sprintf "a cast to %s" (ColumnType.tag ty)
        | Construct.Rounding mode -> sprintf "an exact rounding (%A)" mode
        | Construct.EmptyList -> "a membership test against an empty list"
        | Construct.Interpolation -> "an interpolation through a curve's knots"

    /// The refusal as one sentence, naming the rule that placed it.
    let describe (r: Refusal) : string =
        match r with
        | Refusal.EmbeddedSource ->
            "the source is an embedded table; a statement reads only what a modelled source holds"
        | Refusal.UnknownSource(t, tables) ->
            sprintf
                "the source names table '%s', which the model does not hold; it holds: %s"
                t
                (String.concat ", " tables)
        | Refusal.EmptyRow 0 -> "the source table has no columns, and a statement cannot select none"
        | Refusal.EmptyRow s -> sprintf "step %d leaves no columns, and a statement cannot select none" s
        | Refusal.VerbNotLowered(s, verb) ->
            sprintf "step %d is a %s, which is outside the declared subset; it runs locally" s verb
        | Refusal.PivotNeverLowers s ->
            sprintf
                "step %d is a pivot, which never lowers: it would turn data values into identifiers; it runs locally"
                s
        | Refusal.UnpairedSort s ->
            sprintf
                "step %d sorts without a limit after it; a sort lowers only as the ordering of a page, so it runs locally"
                s
        | Refusal.UnorderedLimit s ->
            sprintf
                "step %d limits rows no sort has ordered; which rows it keeps depends on order, so it runs locally"
                s
        | Refusal.AfterOrder s ->
            sprintf
                "step %d follows an ordered page; the statement ends at its ORDER BY, because a relation keeps no order through an enclosing select"
                s
        | Refusal.JoinKindNotLowered(s, kind) ->
            sprintf "step %d is a %A join, which is outside the declared subset; it runs locally" s kind
        | Refusal.OperandNotModelled(s, operand) ->
            sprintf "step %d joins %s, which is not a table of the model; it runs locally" s operand
        | Refusal.UndeclaredJoin(s, operand, on) ->
            sprintf
                "step %d joins table '%s' on %s, a relation the model does not declare; it runs locally"
                s
                operand
                (on |> List.map (fun (a, b) -> a + " = " + b) |> String.concat ", ")
        | Refusal.UnknownColumn(s, c, members) ->
            sprintf
                "step %d reads column '%s', which the rows reaching it do not carry (they carry: %s); it runs locally, where the reference reports it"
                s
                c
                (String.concat ", " members)
        | Refusal.RepeatedName(s, name) ->
            sprintf "step %d would carry the column name '%s' twice, and a result is read back by name" s name
        | Refusal.UnboundParam(s, name) ->
            sprintf "step %d reads parameter '%s', which the bindings do not hold; it runs locally" s name
        | Refusal.UnpinnedClock(s, grain) ->
            sprintf
                "step %d reads the clock at grain '%s' and no clock is pinned; it runs locally"
                s
                (NowGrain.tag grain)
        | Refusal.ConstructExcluded(s, c) ->
            sprintf
                "step %d uses %s, which is outside the provider's declared subset; it runs locally"
                s
                (describeConstruct c)
        | Refusal.DialectRefuses(s, c, reason) ->
            sprintf
                "step %d uses %s, which the dialect does not lower (%s); it runs locally"
                s
                (describeConstruct c)
                reason
        | Refusal.MayRefuse(s, detail) ->
            sprintf "step %d can refuse at evaluation (%s); it runs locally, where the reference reports it" s detail
        | Refusal.Untyped(s, detail) -> sprintf "step %d needs a type the expression does not decide (%s)" s detail
