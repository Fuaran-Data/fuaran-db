/// The dependency boundary (DECISIONS.md D3): no project in this repository references a UI-tier,
/// program-domain or platform package. Read from the project files themselves, so a reference added
/// anywhere — a package, a project, a central version — turns this red.
module Fuaran.DB.Tests.BoundaryTests

open System.IO
open System.Text.RegularExpressions
open Expecto

/// The package-id prefixes no project here may reference.
let bannedPrefixes = [ "Fuaran.UI."; "Fuaran.Program."; "ToolUp." ]

let private reference =
    Regex(
        @"<(PackageReference|PackageVersion|ProjectReference)\s[^>]*(Include|Update)\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase
    )

/// The banned references in one project file's text, as `(kind, id)`. A project reference is read
/// by its file name, so `..\x\Fuaran.UI.Core.fsproj` is caught as surely as the package id.
let violations (text: string) : (string * string) list =
    reference.Matches text
    |> Seq.map (fun m ->
        let kind = m.Groups[1].Value
        let target = m.Groups[3].Value

        let id =
            if kind = "ProjectReference" then
                Path.GetFileNameWithoutExtension(target.Replace('\\', '/'))
            else
                target

        kind, id)
    |> Seq.filter (fun (_, id) ->
        bannedPrefixes
        |> List.exists (fun p -> id.StartsWith(p, System.StringComparison.OrdinalIgnoreCase)))
    |> Seq.toList

/// Every project and MSBuild props/targets file in the repository, outside build output.
let projectFiles (root: string) : string list =
    [ "*.fsproj"; "*.csproj"; "*.props"; "*.targets" ]
    |> List.collect (fun pattern -> Directory.GetFiles(root, pattern, SearchOption.AllDirectories) |> Array.toList)
    |> List.filter (fun p ->
        let parts = p.Replace('\\', '/').Split('/')
        not (parts |> Array.exists (fun s -> s = "bin" || s = "obj")))
    |> List.sort

[<Tests>]
let tests =
    testList
        "dependency boundary"
        [ testCase "the scanner catches each banned reference form (its falsifier)"
          <| fun _ ->
              let planted =
                  """<Project>
  <ItemGroup>
    <PackageReference Include="Fuaran.UI.Core" Version="1.0.0" />
    <PackageReference Include="Fuaran.Program.Bounded" Version="0.1.0" />
    <PackageVersion Include="ToolUp.Platform" Version="0.1.0" />
    <ProjectReference Include="..\elsewhere\Fuaran.UI.Render.fsproj" />
    <PackageReference Include="Fuaran.Core.Tree" Version="0.36.0" />
    <PackageReference Include="Fuaran.UIx" Version="0.36.0" />
  </ItemGroup>
</Project>"""

              Expect.equal
                  (violations planted)
                  [ "PackageReference", "Fuaran.UI.Core"
                    "PackageReference", "Fuaran.Program.Bounded"
                    "PackageVersion", "ToolUp.Platform"
                    "ProjectReference", "Fuaran.UI.Render" ]
                  "every banned form, and nothing allowed"

          testCase "no project in the repository references a UI, program or platform package"
          <| fun _ ->
              let root = Path.GetDirectoryName(CorpusTests.fixturesDir ())
              let files = projectFiles root
              Expect.isNonEmpty files "the scan found the projects"

              let found =
                  files
                  |> List.collect (fun f -> violations (File.ReadAllText f) |> List.map (fun (k, id) -> f, k, id))

              Expect.equal found [] "banned references (file, kind, id)" ]
