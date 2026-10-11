/// The in-repo conformance corpus (`wire-fixtures/`), on the manifest pattern: the manifest is
/// authoritative for every family's vector count, and a file on disk the manifest does not count
/// fails the suite as surely as a counted file that is missing.
module Fuaran.DB.Tests.CorpusTests

open System
open System.IO
open System.Text
open Expecto
open Fuaran.Core
open Fuaran.DB
open Fuaran.DB.Session

/// The repository's `wire-fixtures/` directory, found by walking up from the test binary.
let fixturesDir () : string =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "wire-fixtures/manifest.json not found above the test binary"
        elif File.Exists(Path.Combine(dir.FullName, "wire-fixtures", "manifest.json")) then
            Path.Combine(dir.FullName, "wire-fixtures")
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private utf8 = UTF8Encoding(false)
let private read (path: string) = File.ReadAllText(path, utf8)

type private Family =
    { Id: string
      Kind: string
      Path: string
      Vectors: int }

let private str (k: string) (fs: (string * JVal) list) =
    match fs |> List.tryFind (fun (n, _) -> n = k) with
    | Some(_, JStr s) -> s
    | _ -> failwithf "manifest: '%s' is not a string" k

let private families () : Family list =
    match Json.parse (read (Path.Combine(fixturesDir (), "manifest.json"))) with
    | Ok(JObj fs) ->
        match fs |> List.tryFind (fun (k, _) -> k = "families") with
        | Some(_, JArr xs) ->
            xs
            |> List.map (fun x ->
                match x with
                | JObj f ->
                    { Id = str "id" f
                      Kind = str "kind" f
                      Path = str "path" f
                      Vectors =
                        match f |> List.tryFind (fun (k, _) -> k = "vectors") with
                        | Some(_, JInt n) -> n
                        | _ -> failwith "manifest: 'vectors' is not an integer" }
                | _ -> failwith "manifest: a family is not an object")
        | _ -> failwith "manifest: no families"
    | _ -> failwith "manifest: not a JSON object"

/// The accept vectors in a directory family, by file name: a session stream is JSONL.
let private acceptFiles (f: Family) =
    let pattern = if f.Kind = "accept-session" then "*.jsonl" else "*.json"

    Directory.GetFiles(Path.Combine(fixturesDir (), f.Path), pattern)
    |> Array.sort
    |> Array.toList

/// One reject vector: an input text, what it is decoded as, and the code it must be refused with.
type private Reject =
    { Name: string
      As: string
      Input: string
      Code: string }

let private rejects (f: Family) : Reject list =
    match Json.parse (read (Path.Combine(fixturesDir (), f.Path))) with
    | Ok(JArr xs) ->
        xs
        |> List.map (fun x ->
            match x with
            | JObj fs ->
                { Name = str "name" fs
                  As = str "as" fs
                  Input = str "input" fs
                  Code = str "code" fs }
            | _ -> failwith "a reject vector is not an object")
    | _ -> failwith "the reject file is not a JSON array"

let private decodeAs (kind: string) (text: string) : Result<string, WireError> =
    match kind with
    | "state" -> ModelWire.decodeState text |> Result.map ModelWire.encodeState
    | "op" -> ModelWire.decodeOp text |> Result.map ModelWire.encodeOp
    | "node" -> ModelWire.decodeNode text |> Result.map ModelWire.encodeNode
    | "session-op" -> SessionWire.decodeOp text |> Result.map SessionWire.encodeOp
    | other -> failwithf "unknown vector kind '%s'" other

/// One session reject vector: a session's wire, the head the reader holds, and the code.
type private SessionReject =
    { Name: string
      As: string
      Input: string
      Head: string option
      Code: string }

let private sessionRejects (f: Family) : SessionReject list =
    match Json.parse (read (Path.Combine(fixturesDir (), f.Path))) with
    | Ok(JArr xs) ->
        xs
        |> List.map (fun x ->
            match x with
            | JObj fs ->
                { Name = str "name" fs
                  As = str "as" fs
                  Input = str "input" fs
                  Head =
                    fs
                    |> List.tryFind (fun (k, _) -> k = "expectedHead")
                    |> Option.map (fun _ -> str "expectedHead" fs)
                  Code = str "code" fs }
            | _ -> failwith "a reject vector is not an object")
    | _ -> failwith "the reject file is not a JSON array"

/// A session reject's code: the reader's, or the op codec's.
let private sessionCode (v: SessionReject) : string option =
    match v.As with
    | "session" ->
        match SessionWire.read v.Head v.Input with
        | Error f -> Some(SessionWire.faultCode f)
        | Ok _ -> None
    | "session-op" ->
        match SessionWire.decodeOp v.Input with
        | Error e -> Some(ModelWire.code e)
        | Ok _ -> None
    | other -> failwithf "unknown session vector kind '%s'" other

[<Tests>]
let tests =
    testList
        "wire corpus"
        [ testCase "the manifest counts every vector on disk, and no other"
          <| fun _ ->
              for f in families () do
                  let onDisk =
                      match f.Kind with
                      | "accept-state"
                      | "accept-op"
                      | "accept-session-op"
                      | "accept-session" -> List.length (acceptFiles f)
                      | "reject" -> List.length (rejects f)
                      | "reject-session" -> List.length (sessionRejects f)
                      | other -> failtestf "unknown family kind '%s'" other

                  Expect.equal onDisk f.Vectors (sprintf "family '%s': the manifest's count is authoritative" f.Id)

          testCase "every accept vector decodes and re-encodes to its own bytes"
          <| fun _ ->
              for f in
                  families ()
                  |> List.filter (fun f -> f.Kind.StartsWith "accept-" && f.Kind <> "accept-session") do
                  let kind = f.Kind.Substring "accept-".Length

                  for file in acceptFiles f do
                      let text = read file

                      match decodeAs kind text with
                      | Ok canonical -> Expect.equal canonical text (Path.GetFileName file + " is canonical")
                      | Error e -> failtestf "%s: %s" (Path.GetFileName file) (ModelWire.describe e)

          testCase "every reject vector is refused with its named code"
          <| fun _ ->
              for f in families () |> List.filter (fun f -> f.Kind = "reject") do
                  for v in rejects f do
                      match decodeAs v.As v.Input with
                      | Error e -> Expect.equal (ModelWire.code e) v.Code v.Name
                      | Ok decoded -> failtestf "%s: decoded %s" v.Name decoded

          testCase "every session stream reads, verifies, folds and re-writes to its own bytes"
          <| fun _ ->
              for f in families () |> List.filter (fun f -> f.Kind = "accept-session") do
                  for file in acceptFiles f do
                      let text = read file

                      match SessionWire.read None text with
                      | Ok(records, _) -> Expect.equal (SessionWire.write records) text (Path.GetFileName file)
                      | Error e -> failtestf "%s: %s" (Path.GetFileName file) (SessionWire.describeFault e)

          testCase "every session reject vector is refused with its named code"
          <| fun _ ->
              for f in families () |> List.filter (fun f -> f.Kind = "reject-session") do
                  for v in sessionRejects f do
                      Expect.equal (sessionCode v) (Some v.Code) v.Name

          testCase "every accepted state is a model the rules find clean"
          <| fun _ ->
              for f in families () |> List.filter (fun f -> f.Kind = "accept-state") do
                  for file in acceptFiles f do
                      match ModelWire.decodeState (read file) with
                      | Ok s -> Expect.isEmpty (ModelRules.check s.Root) (Path.GetFileName file)
                      | Error e -> failtestf "%s" (ModelWire.describe e) ]
