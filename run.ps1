# fuaran-db — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library + tests only): tool restore -> fantomas -> build -> Expecto runner.
#
#   pwsh ./run.ps1                 full pass
#   pwsh ./run.ps1 -SkipFormat     skip the Fantomas pass
#   pwsh ./run.ps1 -SkipBuild      skip the build (implies a prior build)
#   pwsh ./run.ps1 -SkipTests      skip the Expecto suite
#
# Until the domain's solution exists there is nothing to build or test, and this script says so
# rather than reporting a build that did not happen. The stages engage as their inputs appear.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A stage that runs no native command must not inherit another stage's exit code, and the
# `global:` scope is what reaches the code a caller sees when this script is invoked with `&`.
$global:LASTEXITCODE = 0

$solution = 'Fuaran.DB.slnx'
$testProject = 'tests/Fuaran.DB.Tests'
# `@(...)` keeps a one-element result an array: unwrapped, a lone 'src' would splat as characters.
$formatRoots = @(@('src', 'tests') | Where-Object { Test-Path $_ })

if (-not $SkipFormat) {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($formatRoots.Count -gt 0) {
        dotnet fantomas @formatRoots
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    else {
        Write-Host 'format: no F# sources yet (src/, tests/ absent) — nothing to format.'
    }
}

if (-not (Test-Path $solution)) {
    Write-Host "build/test: $solution does not exist yet — nothing was built or tested."
    exit 0
}

$global:LASTEXITCODE = 0
if (-not $SkipBuild) {
    dotnet build $solution --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$global:LASTEXITCODE = 0
if (-not $SkipTests) {
    if (-not (Test-Path $testProject)) {
        Write-Host "test: $testProject does not exist yet — no tests ran."
        exit 0
    }
    # `--no-build` only when this run already built; otherwise let `dotnet run` build first.
    if ($SkipBuild) {
        dotnet run --project $testProject
    }
    else {
        dotnet run --project $testProject --no-build
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
