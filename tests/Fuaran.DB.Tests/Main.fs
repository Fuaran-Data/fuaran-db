module Fuaran.DB.Tests.Main

open System.IO
open System.Text
open Expecto
open Fuaran.Core
open Fuaran.DB

/// Regenerate the accept vectors of `wire-fixtures/` from the worked models (`--write-fixtures`).
/// A deliberate act: the corpus is a contract, so a change to it is reviewed as a diff, and the
/// manifest's counts are edited by hand beside it.
let private writeFixtures () =
    let dir = CorpusTests.fixturesDir ()
    let utf8 = UTF8Encoding(false)

    let write (sub: string) (name: string) (text: string) =
        let path = Path.Combine(dir, sub, name + ".json")
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, text, utf8)
        printfn "wrote %s" path

    let retired =
        // the relation to the table goes first, or the retired table leaves it dangling
        match ModelOps.applyAll [ ModelOp.Retire "j:orders-customers"; ModelOp.Retire "t:customers" ] Sample.state with
        | Ok s -> s
        | Error e -> failwithf "%A" e

    let seeded =
        Drift.seed
            "m:seeded"
            "seeded"
            "sales"
            "unclassified"
            { Tables =
                [ { Name = "Orders"
                    Columns = Sample.fields [ "OrderId", IntType; "Revenue", DecimalType ] } ] }

    write "states" "retail" (ModelWire.encodeState Sample.state)
    write "states" "retail-retired" (ModelWire.encodeState retired)
    write "states" "seeded" (ModelWire.encodeState (ModelOps.ofRoot seeded))

    write
        "states"
        "empty"
        (ModelWire.encodeState (
            ModelOps.ofRoot (
                Model.empty
                    "m:empty"
                    { Name = "empty"
                      Classifications = []
                      Recordable = [] }
            )
        ))

    WireTests.sampleOps
    |> List.iteri (fun i op -> write "ops" (sprintf "%02d-%s" (i + 1) (AiTools.kindOfOp op)) (ModelWire.encodeOp op))

    // the connection record (Phase 1903): one op per kind, the worked sessions, and the reader's refusals
    for name, text in SessionTests.corpusOps () do
        write "session/ops" name text

    for name, text in SessionTests.corpusStreams () do
        let path = Path.Combine(dir, "session", "streams", name + ".jsonl")
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, text, utf8)
        printfn "wrote %s" path

    let rejects =
        SessionTests.corpusRejects ()
        |> List.map (fun (name, kind, input, head, code) ->
            Canon.render (
                JObj(
                    [ "name", JStr name; "as", JStr kind; "input", JStr input; "code", JStr code ]
                    @ (head |> Option.map (fun h -> "expectedHead", JStr h) |> Option.toList)
                )
            ))
        |> List.map (fun line -> "  " + line)
        |> String.concat ",\n"

    File.WriteAllText(Path.Combine(dir, "session", "rejects.json"), "[\n" + rejects + "\n]\n", utf8)
    printfn "wrote session/rejects.json"

    // the model projected per peer (Phase 1907): the worked model for three subjects, and the refusals
    for name, text in ProjectionTests.corpusProjections () do
        write "projections/accept" name text

    let projectionRejects =
        ProjectionTests.corpusRejects ()
        |> List.map (fun (name, input, code) ->
            "  "
            + Canon.render (
                JObj
                    [ "name", JStr name
                      "as", JStr "projection"
                      "input", JStr input
                      "code", JStr code ]
            ))
        |> String.concat ",\n"

    File.WriteAllText(Path.Combine(dir, "projections", "rejects.json"), "[\n" + projectionRejects + "\n]\n", utf8)

    printfn "wrote projections/rejects.json"

[<EntryPoint>]
let main argv =
    if Array.contains "--write-fixtures" argv then
        writeFixtures ()
        0
    elif Array.contains "--write-conformance" argv then
        SqlServerTests.writeConformance ()
        0
    else
        runTestsInAssemblyWithCLIArgs [] argv
