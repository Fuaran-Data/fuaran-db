module Fuaran.DB.Tests.GateReadsCensusTests

// WHAT THIS REPOSITORY'S GATE READS — `gate-reads.json`, checked rather than trusted.
//
// A landing may stand on an earlier green gate run across later commits only when none of them
// touched a file the gate reads. The record at the repository root is where that set is written down
// for a reader outside this repository: `reads` names the non-code files the gate opens beyond the
// code-shaped default (sources, tests, proofs, build and project files, the gate scripts), and
// `notRead` waives, with a reason, a file the census below flags that a human has confirmed is not
// opened. A record nobody checks decays one convenient omission at a time, and its failure mode is
// the permissive one — a commit to a file the gate reads crossed without a gate run — so it is held
// here in two halves.
//
// 1. THE GATE. `verify.ps1` passes through to `run.ps1`, which formats, builds the solution and runs
//    this suite, and runs one other script: the publication-boundary sweep, which reads every tracked
//    file, so the record must then read `**` (DECISIONS D14). The test below pins that shape, so a
//    stage added to the gate (which would read files no census of this suite can see) turns it red
//    until the record is re-examined.
//
// 2. THE SUITE. The record lists what this suite opens, checked by a STATIC census of the suite's own
//    sources: a test file reaches the real repository only through a root locator (`fixturesDir`,
//    `repoRoot`, or a directory walk over `dir.FullName`), so in every file that names one, every
//    quoted string that is a candidate's file name or repo-relative path, and every
//    `Path.Combine(<locator>, "a", "b", ...)` prefix, counts as a read. It over-reports by design, and
//    each over-report is settled by a reviewed `notRead` entry. What it cannot see is a path held in
//    DATA (the corpus manifest's family paths), which is why the corpus directories are read whole.
//
// The census walks the file system rather than asking git for the tracked set: an untracked file
// only ever widens the candidates.

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Expecto

let private solutionName = "Fuaran.DB.slnx"
let private recordName = "gate-reads.json"

/// This file's own name: excluded from its own census, because its go-red fixtures quote paths on
/// purpose.
let private selfName = "GateReadsCensusTests.fs"

let private repoRoot =
    lazy
        (let rec walk (d: DirectoryInfo) =
            if isNull (box d) then
                failtestf "could not locate the repository root (no %s above the test assembly)" solutionName
            elif File.Exists(Path.Combine(d.FullName, solutionName)) then
                d.FullName
            else
                walk d.Parent

         walk (DirectoryInfo(AppContext.BaseDirectory)))

// ── the path grammar the record is read with ─────────────────────────────────────────────────────────

/// Does `path` fall under `spec`? A spec with no `*` names a file or a directory and matches the path
/// itself or anything beneath it; a spec with wildcards is a glob in which `**` crosses `/` and `*`
/// does not. Ordinal, never case-folded: folding can only widen a match.
let matches (spec: string) (path: string) : bool =
    let s = spec.Trim().Replace('\\', '/').Trim('/')
    let p = path.Trim().Replace('\\', '/').TrimStart('/')

    if s = "" || p = "" then
        false
    elif not (s.Contains "*") then
        p = s || p.StartsWith(s + "/", StringComparison.Ordinal)
    else
        let pattern =
            Regex.Escape(s).Replace(@"\*\*", "\u0000").Replace(@"\*", "[^/]*").Replace("\u0000", ".*")

        Regex.IsMatch(p, "^" + pattern + "$")

let matchesAny (specs: string list) (path: string) =
    specs |> List.exists (fun s -> matches s path)

/// Code-shaped paths every reader of the record treats as gate inputs already, so they are never
/// census candidates. Kept deliberately NARROWER than any reader's default: a narrower list only
/// makes more files candidates, which is the restrictive direction.
let private implied =
    [ "src/**"
      "tests/**"
      ".config/**"
      solutionName
      "Directory.Build.props"
      "global.json"
      ".editorconfig"
      "verify.ps1"
      "run.ps1"
      recordName ]

// ── the record ───────────────────────────────────────────────────────────────────────────────────────

type private Record =
    { Kind: string
      Reads: string list
      NotRead: (string * string) list }

let private parseRecord (text: string) : Record =
    use doc = JsonDocument.Parse text
    let root = doc.RootElement

    let str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString().Trim()
        | _ -> ""

    let arr (name: string) =
        match root.TryGetProperty name with
        | true, a when a.ValueKind = JsonValueKind.Array -> [ for e in a.EnumerateArray() -> e ]
        | _ -> []

    { Kind = str root "kind"
      Reads =
        arr "reads"
        |> List.filter (fun e -> e.ValueKind = JsonValueKind.String)
        |> List.map (fun e -> e.GetString().Trim())
      NotRead =
        arr "notRead"
        |> List.filter (fun e -> e.ValueKind = JsonValueKind.Object)
        |> List.map (fun e -> str e "path", str e "reason") }

let private record =
    lazy
        (let p = Path.Combine(repoRoot.Value, recordName)

         if not (File.Exists p) then
             failtestf "this repository records what its gate reads: %s is missing" p

         parseRecord (File.ReadAllText p))

// ── the census (PURE over (source path, text) — the go-red runs THIS) ──────────────────────────────

let private locator =
    Regex(@"fixturesDir|repoRoot|emittedDir|Fuaran\.DB\.slnx|\b(d|dir)\.FullName", RegexOptions.Compiled)

let private literal = Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled)

let private combine =
    Regex(@"Path\.Combine\(([^()]*(?:\([^()]*\)[^()]*)*)\)", RegexOptions.Compiled)

let private norm (s: string) =
    s.Replace(@"\\", "/").Replace('\\', '/').Trim('/')

/// Which of `candidates` (repo-relative, `/`-separated) the `sources` may read, each with the source
/// files that name it. Only a source mentioning a root locator is read at all.
let census (sources: (string * string) list) (candidates: string list) : (string * string list) list =
    let perSource =
        sources
        |> List.filter (fun (_, text) -> locator.IsMatch text)
        |> List.map (fun (rel, text) ->
            let lits =
                [ for m in literal.Matches text -> norm m.Groups.[1].Value ]
                |> List.filter (fun s -> s <> "")
                |> Set.ofList

            let prefixes =
                [ for m in combine.Matches text do
                      let args = m.Groups.[1].Value

                      if locator.IsMatch(args.Split(',').[0]) then
                          match
                              [ for p in literal.Matches args -> norm p.Groups.[1].Value ]
                              |> List.filter (fun p -> p <> "")
                          with
                          | [] -> ()
                          | ps -> yield String.Join("/", ps) ]

            rel, lits, prefixes)

    candidates
    |> List.choose (fun c ->
        let name = c.Substring(c.LastIndexOf('/') + 1)

        let by =
            perSource
            |> List.filter (fun (_, lits, prefixes) ->
                lits.Contains name
                || lits.Contains c
                || prefixes
                   |> List.exists (fun p -> c = p || c.StartsWith(p + "/", StringComparison.Ordinal)))
            |> List.map (fun (rel, _, _) -> rel)

        if List.isEmpty by then None else Some(c, by))

// ── the real repository ──────────────────────────────────────────────────────────────────────────────

let private skippedDirs =
    set [ ".git"; "bin"; "obj"; ".vs"; "node_modules"; "TestResults" ]

/// Every file under the root but git's own: `.git` is a directory in a clone and a FILE in a linked
/// worktree, and in neither is it something a commit can change.
let private files =
    lazy
        (let root = repoRoot.Value

         let rec walk (d: string) : string list =
             [ for f in Directory.GetFiles d do
                   if Path.GetFileName f <> ".git" then
                       yield Path.GetRelativePath(root, f).Replace('\\', '/')
               for sub in Directory.GetDirectories d do
                   if not (skippedDirs.Contains(Path.GetFileName sub)) then
                       yield! walk sub ]

         walk root)

let private testSources () : (string * string) list =
    files.Value
    |> List.filter (fun f -> f.StartsWith "tests/" && (f.EndsWith ".fs" || f.EndsWith ".fsx"))
    |> List.filter (fun f -> Path.GetFileName f <> selfName)
    |> List.map (fun f -> f, File.ReadAllText(Path.Combine(repoRoot.Value, f)))

let private candidates () =
    files.Value |> List.filter (fun f -> not (matchesAny implied f))

/// The publication-boundary sweep (DECISIONS D14): the one script besides `run.ps1` the gate may run.
/// It reads every tracked file.
let private boundaryScript = "check-publication-boundary.ps1"

/// A script the gate entry points invoke, by its `.ps1` file name.
let private invokedScripts (text: string) : string list =
    [ for m in Regex.Matches(text, @"[\w./-]+\.ps1") -> Path.GetFileName m.Value ]
    |> List.distinct

[<Tests>]
let tests =
    testList
        "gate-reads — the record of what this repository's gate reads"
        [ test "the record is a gateReads document whose every waiver carries a reason" {
              let r = record.Value
              Expect.equal r.Kind "gateReads" "the record names its kind"

              for (p, why) in r.NotRead do
                  Expect.isTrue (p <> "" && why <> "") (sprintf "a waiver needs a path and a reason: %A" (p, why))
          }

          test
              "THE GATE — verify.ps1 and run.ps1 run only the boundary sweep, and a sweep of the tracked set reads `**`" {
              let text name =
                  File.ReadAllText(Path.Combine(repoRoot.Value, name))

              let gate = text "verify.ps1" + "\n" + text "run.ps1"

              let others =
                  invokedScripts gate
                  |> List.filter (fun s -> s <> "verify.ps1" && s <> "run.ps1" && s <> boundaryScript)

              Expect.isEmpty
                  others
                  "the gate runs a script this census does not read: record what it opens, then extend this test"

              let sweeps =
                  gate.Contains "ls-files" || List.contains boundaryScript (invokedScripts gate)

              if sweeps then
                  Expect.contains
                      record.Value.Reads
                      "**"
                      "the gate sweeps the tracked set: every tracked file is then a gate input, and the record must read `**`"
          }

          test "GO-RED — a locator-bearing source that combines the root with a path reads it" {
              let src =
                  "let root = repoRoot ()\nlet d = Path.Combine(repoRoot (), \"docs\", \"census.md\")\n"

              let hits = census [ "tests/X.fs", src ] [ "docs/census.md"; "docs/notes.md" ]
              Expect.equal (hits |> List.map fst) [ "docs/census.md" ] "the named file, and only it"
          }

          test "GO-RED — a directory walk off a located directory reads everything beneath it" {
              let src =
                  "let candidate = Path.Combine(dir.FullName, \"conformance\", \"fixtures\")\n"

              let hits =
                  census [ "tests/Y.fs", src ] [ "conformance/fixtures/a.json"; "conformance/seed.json" ]

              Expect.equal (hits |> List.map fst) [ "conformance/fixtures/a.json" ] "the walked directory"
          }

          test "a source with NO root locator reads nothing, whatever it quotes" {
              let src = "let d = Path.Combine(tmp, \"docs\", \"census.md\")\n"
              Expect.isEmpty (census [ "tests/Z.fs", src ] [ "docs/census.md" ]) "a temp fixture"
          }

          test "THE SUITE — every file the suite may open is recorded as read, or waived with a reason" {
              let r = record.Value

              let unaccounted =
                  census (testSources ()) (candidates ())
                  |> List.filter (fun (p, _) ->
                      not (matchesAny r.Reads p) && not (matchesAny (List.map fst r.NotRead) p))
                  |> List.map (fun (p, by) -> sprintf "%s (named by %s)" p (String.concat ", " by))

              Expect.isEmpty
                  unaccounted
                  (sprintf
                      "these files are named by a test that reaches the real repository, and %s neither lists them under `reads` nor waives them under `notRead` with a reason:\n  %s"
                      recordName
                      (String.concat "\n  " unaccounted))
          }

          test "THE SUITE — every recorded read names a file, so a rename cannot leave it stale" {
              let stale =
                  record.Value.Reads
                  |> List.filter (fun spec -> not (files.Value |> List.exists (matches spec)))

              Expect.isEmpty stale (sprintf "reads naming no file: %A" stale)
          }

          test "THE SUITE — every waiver still covers a census hit, so a waiver cannot outlive its reason" {
              let r = record.Value
              let hits = census (testSources ()) (candidates ()) |> List.map fst

              let idle =
                  r.NotRead
                  |> List.map fst
                  |> List.filter (fun spec ->
                      not (hits |> List.exists (fun h -> matches spec h && not (matchesAny r.Reads h))))

              Expect.isEmpty idle (sprintf "notRead entries no census hit needs any more — delete them: %A" idle)
          } ]
