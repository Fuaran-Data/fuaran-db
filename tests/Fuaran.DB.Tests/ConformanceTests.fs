/// Core's conformance kit, run at the domain's witnesses. The census — every law the kit ran, and
/// whether it held — is printed to the run's output, so a gate log carries it.
module Fuaran.DB.Tests.ConformanceTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB

module Rng = Fuaran.Core.ConfRng

let private seed = 18990
let private iterations = 200

let private types =
    [ IntType
      FloatType
      BoolType
      StringType
      DateType
      TimestampType
      DecimalType ]

let private column (id: string) (r: Rng.T) : ModelNode * Rng.T =
    let ty, r = Rng.choose types r
    let cls, r = Rng.choose [ "public"; "personal" ] r

    { Id = id
      Kind =
        NodeKind.Column
            { Name = "col_" + id
              Physical = "COL_" + id
              Type = ty
              Classification = cls }
      Children = [] },
    r

/// A random model: a root with sources, tables, columns, row policies and limits.
let genTree (rng: Rng.T) : ModelNode * Rng.T =
    let mutable counter = 0
    let mutable r = rng

    let fresh () =
        counter <- counter + 1
        sprintf "n%d" counter

    let below n =
        let v, r' = Rng.intBelow n r
        r <- r'
        v

    let table () =
        let id = fresh ()

        let cols =
            [ for _ in 1 .. below 4 do
                  let c, r' = column (fresh ()) r
                  r <- r'
                  c ]

        let extras =
            [ if below 2 = 0 then
                  { Id = fresh ()
                    Kind =
                      NodeKind.Limit
                          { Name = "cap"
                            MaxRows = 1 + below 1000
                            // the four optional caps, each present about half the time, so the
                            // wire's round trip is exercised with and without them
                            MaxJoins = (if below 2 = 0 then Some(below 4) else None)
                            MaxGroupKeys = (if below 2 = 0 then Some(below 4) else None)
                            MinGroupSize = (if below 2 = 0 then Some(1 + below 20) else None)
                            TimeoutSeconds = (if below 2 = 0 then Some(1 + below 600) else None) }
                    Children = [] } ]

        { Id = id
          Kind =
            NodeKind.Table
                { Name = "table_" + id
                  Physical = "T_" + id }
          Children = cols @ extras }

    let source () =
        let id = fresh ()
        let tables = [ for _ in 1 .. below 3 -> table () ]

        // each fingerprint method, or none, so the wire's round trip is exercised with every shape
        let fingerprint =
            match below 4 with
            | 0 -> None
            | 1 -> Some FingerprintMethod.ChangeCounter
            | 2 -> Some(FingerprintMethod.FileStat(below 2 = 0))
            | _ ->
                Some(
                    FingerprintMethod.RowCountMaxChange(
                        tables
                        |> List.choose (fun t -> t.Children |> List.tryHead |> Option.map (fun c -> t.Id, c.Id))
                    )
                )

        { Id = id
          Kind =
            NodeKind.Source
                { Name = "source_" + id
                  Fingerprint = fingerprint }
          Children = tables }

    let root =
        { Id = "root"
          Kind =
            NodeKind.Model
                { Name = "generated"
                  Classifications = [ "public"; "personal" ] }
          Children = [ for _ in 1 .. below 3 -> source () ] }

    root, r

let private genFresh (taken: Set<string>) (rng: Rng.T) : ModelNode * Rng.T =
    let rec pick r =
        let v, r = Rng.next r
        let id = sprintf "f%d" (abs v % 100000)
        if taken.Contains id then pick r else id, r

    let id, r = pick rng
    column id r

let opGen: OpGen<ModelNode, string> =
    { Tree = genTree
      FreshNode = genFresh
      CanHold = Some ModelNode.isContainer }

let private sample = Sample.state

/// Ops drawn from a fixed pool against the worked model: some land, some are refused, so both
/// sides of the reducer are exercised.
let genOp (rng: Rng.T) : ModelOp * Rng.T =
    let addresses =
        [ "m:retail"
          "s:sales"
          "t:orders"
          "t:lines"
          "c:orders.revenue"
          "x:revenue"
          "l:rows"
          "nowhere" ]

    let which, r = Rng.intBelow 3 rng
    let at, r = Rng.choose addresses r
    let v, r = Rng.intBelow 6 r
    let freshId = sprintf "g%d" v

    match which with
    | 0 when v % 2 = 0 ->
        let c, r = column freshId r
        ModelOp.Add(at, c), r
    | 0 ->
        // a limit whose bound may not be positive: the policy denies those
        let node =
            { Id = freshId
              Kind = NodeKind.Limit(LimitSpec.rows "cap" (v - 3))
              Children = [] }

        ModelOp.Add(at, node), r
    | 1 ->
        let kind =
            match ModelNode.tryFind at sample.Root with
            | Some n ->
                match n.Kind with
                | NodeKind.Column c -> NodeKind.Column { c with Name = c.Name + "_renamed" }
                | NodeKind.Limit l -> NodeKind.Limit { l with MaxRows = l.MaxRows + v }
                | other -> other
            | None -> NodeKind.Source { Name = "s"; Fingerprint = None }

        ModelOp.Amend(at, kind), r
    | _ -> ModelOp.Retire at, r

let streamGen: StreamGen<ModelOp, ModelState> = { State0 = sample; Op = genOp }

let private census (title: string) (laws: LawResult list) =
    printfn "census (%s): %d laws, %d held" title laws.Length (laws |> List.filter _.Passed |> List.length)

    for l in laws do
        printfn "  [%s] %s" (if l.Passed then "held" else "FAILED") l.Law

let private expectAll (title: string) (laws: LawResult list) =
    census title laws
    Expect.isNonEmpty laws (title + ": the kit ran laws")

    let failed = laws |> List.filter (fun l -> not l.Passed)

    if not (List.isEmpty failed) then
        failed
        |> List.map (fun l -> sprintf "  %s: %A" l.Law l.Counterexample)
        |> String.concat "\n"
        |> failtestf "%s failed Core conformance:\n%s" title

[<Tests>]
let tests =
    testList
        "Core conformance at the domain's witness"
        [ testCase "certify: the node witness, the op algebra, the diff laws and the op-stream"
          <| fun _ ->
              let report =
                  Conformance.certify
                      ModelNode.witness
                      ModelNode.idWitness
                      opGen
                      ModelWire.streamWitness
                      streamGen
                      OpStream.defaultHash
                      seed
                      iterations

              expectAll "certify" report.Results

          testCase "the reducer laws, with rejections that name their alternatives"
          <| fun _ ->
              let namesAlternatives (r: ModelRejection) =
                  match r with
                  | ModelRejection.UnknownNode(_, known) -> not (List.isEmpty known)
                  | ModelRejection.IllegalChild(_, _, _) -> true
                  | ModelRejection.DuplicateId _
                  | ModelRejection.RetiredId _
                  | ModelRejection.KindChange _
                  | ModelRejection.RetireRoot -> true

              expectAll
                  "reducer"
                  (Conformance.reducer ModelOps.apply streamGen (Some namesAlternatives) seed iterations)

          testCase "the rule registry: introduced defects, verdicts and gates"
          <| fun _ ->
              expectAll
                  "introducedLaws"
                  (Conformance.introducedLaws
                      ModelNode.witness
                      ModelNode.idWitness
                      ModelRules.registry
                      opGen
                      seed
                      iterations)

          testCase "the AI-tools surface"
          <| fun _ ->
              expectAll "aiSurfaceLawsAt" (Conformance.aiSurfaceLawsAt AiTools.surface genOp sample seed iterations) ]
