/// The Substrait mapping table (DECISIONS.md D7, Phase 1908) held to the algebra it classifies. The
/// decision to decline a Substrait target stands on a count over the declared subset. These tests
/// turn red when the transform algebra gains a function, operator, aggregate, join kind, column type,
/// relation form or expression form that the table does not classify, and when the table's rows stop
/// adding up to the counts the decision cites. The table is then re-measured, not assumed.
module Fuaran.DB.Tests.SubstraitSurveyTests

open System.IO
open Microsoft.FSharp.Reflection
open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB.Provider

/// One row of the table: the construct's name and its verdict.
type Row = { Name: string; Verdict: string }

/// The text of the decision entry headed `D<n>:`, up to the next entry.
let section (n: int) (decisions: string) : string =
    let lines = decisions.Replace("\r\n", "\n").Split('\n')
    let heading = sprintf "— D%d:" n

    match lines |> Array.tryFindIndex (fun l -> l.StartsWith "## " && l.Contains heading) with
    | None -> ""
    | Some start ->
        lines
        |> Array.skip (start + 1)
        |> Array.takeWhile (fun l -> not (l.StartsWith "## "))
        |> String.concat "\n"

/// The table's rows: every line whose first cell is a backticked name.
let rows (text: string) : Row list =
    text.Split('\n')
    |> Array.map _.Trim()
    |> Array.filter (fun l -> l.StartsWith "| `")
    |> Array.map (fun l ->
        let cells = l.Trim('|').Split('|') |> Array.map _.Trim()
        let first = cells[0]
        let name = first.Substring(1, first.IndexOf('`', 1) - 1)
        { Name = name; Verdict = cells[2] })
    |> Array.toList

/// The union cases a table over the declared subset must classify, by group.
let groups: (string * string list) list =
    let cases (t: System.Type) =
        FSharpType.GetUnionCases t |> Array.map _.Name |> Array.toList

    // The statement's expression forms that are not a function or operator call, which have rows of
    // their own; a column and a parameter are what every row reads and are not constructs.
    let expressionForms =
        cases typeof<Scalar>
        |> List.filter (fun n -> not (List.contains n [ "Column"; "Parameter"; "Function"; "Operator" ]))

    [ "relation form", cases typeof<Relation>
      "expression form", expressionForms
      "operator", cases typeof<BinOp>
      "function", cases typeof<ScalarFn>
      "aggregate", cases typeof<AggFn>
      "join kind", cases typeof<JoinKind>
      "parameter type", cases typeof<ColumnType> ]

/// The cases no row classifies, as `(group, case)`.
let unclassified (table: Row list) : (string * string) list =
    let named = table |> List.map _.Name |> Set.ofList

    groups
    |> List.collect (fun (g, names) ->
        names
        |> List.filter (fun n -> not (named.Contains n))
        |> List.map (fun n -> g, n))

let private decisionsText () =
    let root = Path.GetDirectoryName(CorpusTests.fixturesDir ())
    File.ReadAllText(Path.Combine(root, "DECISIONS.md"))

let private count (verdict: string) (table: Row list) =
    table |> List.filter (fun r -> r.Verdict = verdict) |> List.length

[<Tests>]
let tests =
    testList
        "Substrait survey (D7)"
        [ testCase "the classifier reports a construct the table leaves out (its falsifier)"
          <| fun _ ->
              let planted =
                  "## 2026-01-01 — D9: planted\n\n   | `Scan` | x | mapped | |\n   | `Abs` | abs | mapped | |\n\n## next\n   | `Sqrt` | sqrt | pinned | |"

              let table = rows (section 9 planted)
              Expect.equal (table |> List.map _.Name) [ "Scan"; "Abs" ] "the section stops at the next entry"
              let missing = unclassified table
              Expect.contains missing ("function", "Sqrt") "a row in another entry does not count"
              Expect.contains missing ("operator", "Add") "an absent operator is named"
              Expect.isFalse (List.contains ("function", "Abs") missing) "a classified function is not named"

          testCase "every construct of the algebra has a row in D7's table"
          <| fun _ ->
              let table = rows (section 7 (decisionsText ()))
              Expect.isNonEmpty table "the table was found"
              Expect.equal (unclassified table) [] "constructs with no row (group, case)"

          testCase "each construct has exactly one row, with a known verdict"
          <| fun _ ->
              let table = rows (section 7 (decisionsText ()))

              let repeated =
                  table
                  |> List.countBy _.Name
                  |> List.filter (fun (_, n) -> n > 1)
                  |> List.map fst

              Expect.equal repeated [] "constructs with more than one row"

              let unknown =
                  table
                  |> List.filter (fun r ->
                      not (List.contains r.Verdict [ "mapped"; "pinned"; "unmappable"; "outside" ]))

              Expect.equal unknown [] "rows with a verdict outside the four"

          testCase "the counts the decision cites are the table's"
          <| fun _ ->
              let table = rows (section 7 (decisionsText ()))

              let mapped, pinned, unmappable =
                  count "mapped" table, count "pinned" table, count "unmappable" table

              Expect.equal (mapped, pinned, unmappable) (35, 28, 1) "mapped, pinned, unmappable"
              Expect.equal (mapped + pinned + unmappable) 64 "the declared subset as counted"

          testCase "the rows outside the subset are the exclusions D4.4 names"
          <| fun _ ->
              let table = rows (section 7 (decisionsText ()))

              let outside =
                  table
                  |> List.filter (fun r -> r.Verdict = "outside")
                  |> List.map _.Name
                  |> List.sort

              Expect.equal
                  outside
                  ([ "Length"
                     "Lower"
                     "Upper"
                     "Median"
                     "StdDev"
                     "First"
                     "Last"
                     "Right"
                     "Outer" ]
                   |> List.sort)
                  "the functions, aggregates and join kinds outside the declared subset" ]
