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
                    Columns = [ "OrderId", IntType; "Revenue", DecimalType ] } ] }

    write "states" "retail" (ModelWire.encodeState Sample.state)
    write "states" "retail-retired" (ModelWire.encodeState retired)
    write "states" "seeded" (ModelWire.encodeState (ModelOps.ofRoot seeded))

    write
        "states"
        "empty"
        (ModelWire.encodeState (ModelOps.ofRoot (Model.empty "m:empty" { Name = "empty"; Classifications = [] })))

    WireTests.sampleOps
    |> List.iteri (fun i op -> write "ops" (sprintf "%02d-%s" (i + 1) (AiTools.kindOfOp op)) (ModelWire.encodeOp op))

[<EntryPoint>]
let main argv =
    if Array.contains "--write-fixtures" argv then
        writeFixtures ()
        0
    else
        runTestsInAssemblyWithCLIArgs [] argv
