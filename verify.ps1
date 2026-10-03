# fuaran-db — the gate. A pass-through to run.ps1's full pass, so the gate and the
# one-command entry point can never disagree about what "green" means.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'run.ps1')
exit $LASTEXITCODE
