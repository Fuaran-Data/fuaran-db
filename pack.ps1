# Pack this repository's package, Fuaran.DB, into a local folder feed — by default the shared feed
# three directories up from this repository, the layout it is developed in.
#
#   pwsh ./pack.ps1                      pack at the version in Directory.Build.props
#   pwsh ./pack.ps1 -Configuration Debug
#   pwsh ./pack.ps1 -Feed <dir>          pack into another folder (a scratch or artifacts directory)
#   pwsh ./pack.ps1 -QuietRepack         overwrite an already-packed version deliberately
#   pwsh ./pack.ps1 -Sweep               the caller is a multi-package sweep (see below)
#
# THE SAME-VERSION OVERWRITE GUARD. A folder feed keys a package by id AND version, so packing a
# version that is already there does not add a slot — it REWRITES one in place, under every consumer
# already pinned to it, while their extracted caches keep serving the old bits. The contract a
# consumer compiles against then depends on when its cache was last filled rather than on its pin,
# and the failure is silent both ways. So this script refuses when the slot for the version it is
# about to pack already exists — exactly the condition "the version did not advance" — and names
# both ways forward in the refusal.
#
# It is an EXISTENCE check, not a source fingerprint: what it can say without keeping any state is
# that a slot is about to be overwritten, which is the thing that matters.
#
# -Sweep vs -QuietRepack. -QuietRepack is an operator's deliberate acknowledgement of one repack.
# -Sweep says the CALLER is a multi-package sweep that keeps its own version fingerprint and surface
# check and reports a repeat repack rather than refusing it; under it the overwrite proceeds and is
# still reported, naming which of the two admitted it.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $QuietRepack,
    [switch] $Sweep,
    # Where the pack goes. Defaults to the shared local folder feed three directories up.
    [string] $Feed
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$feed = if ($Feed) { $Feed } else { Join-Path $PSScriptRoot '..\..\..\local-nuget-feed' }
if (-not (Test-Path $feed)) { New-Item -ItemType Directory -Force $feed | Out-Null }

$packageIds = @('Fuaran.DB')
$projects = @('src/Fuaran.DB/Fuaran.DB.fsproj')

# The version this pack would write, read from the one file that declares it. Without it there is no
# slot to check, so a props file that cannot be read stops the pack rather than disabling the guard.
$propsPath = Join-Path $PSScriptRoot 'Directory.Build.props'
$versionMatch = Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) {
    Write-Error "pack.ps1: no <Version> element found in $propsPath — cannot tell which feed slot this pack would write."
    exit 1
}
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()

# Every slot is checked before any is written.
$occupied = @()
foreach ($packageId in $packageIds) {
    $slot = Join-Path $feed "$packageId.$version.nupkg"
    if (Test-Path $slot) { $occupied += Get-Item $slot }
}

if ($occupied.Count -gt 0) {
    if (-not ($QuietRepack -or $Sweep)) {
        Write-Host ''
        Write-Host "pack.ps1 REFUSED: $($occupied.Count) of $($packageIds.Count) package(s) at $version are already in the feed." -ForegroundColor Red
        foreach ($existing in $occupied) {
            Write-Host "    slot    $($existing.FullName)"
            Write-Host "    packed  $($existing.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')) ($($existing.Length) bytes)"
        }
        Write-Host ''
        Write-Host 'A folder feed keys on id AND version, so this pack would REWRITE those slots in place —'
        Write-Host 'swapping the contract under every consumer pinned to them. Two ways forward:'
        Write-Host ''
        Write-Host "  1. ADVANCE <Version> in $propsPath (in the same commit as the change that moved the"
        Write-Host '     public surface or the delivered content), then pack again.'
        Write-Host ''
        Write-Host '  2. Re-pack this version DELIBERATELY with -QuietRepack; every consumer of this version'
        Write-Host '     must then be rebuilt against the new bits, with their caches for it cleared first.'
        Write-Host ''
        exit 1
    }

    $admittedBy =
        if ($QuietRepack) { '-QuietRepack' }
        else { '-Sweep (the multi-package sweep; its own repack and surface checks cover these slots)' }

    Write-Host "pack.ps1: $admittedBy — overwriting $($occupied.Count) existing slot(s) at $version." -ForegroundColor Yellow
    Write-Host '           Consumers pinned to this version must be rebuilt with their caches cleared.' -ForegroundColor Yellow
}

foreach ($project in $projects) {
    $global:LASTEXITCODE = 0
    dotnet pack $project --configuration $Configuration --nologo --output $feed
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "packed $($packageIds -join ', ') at $version into $((Resolve-Path $feed).Path)"
