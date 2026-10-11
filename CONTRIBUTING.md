# Contributing to Fuaran.DB

## Building and testing

The .NET SDK is pinned in [`global.json`](global.json). One command runs the whole pass — tool
restore, Fantomas, build, and the test suite:

```powershell
./run.ps1                 # the full pass; ./verify.ps1 is the gate and runs the same thing
./run.ps1 -SkipFormat     # skip the Fantomas pass
./run.ps1 -SkipTests      # build only
```

Packages resolve from the public NuGet feed alone. A change that needs a package only a local feed
can serve is not ready.

## Conventions

- **Format before you commit.** `run.ps1` runs Fantomas over `src/` and `tests/` with the repository's
  `.editorconfig`; commit what it leaves.
- **Closed unions, exhaustive matches.** No catch-all arm over a union this package or its
  dependencies declare. Incomplete matches are build errors here, so a new case names every place
  that must decide it.
- **A refusal has a name.** Every rejection, rule finding and wire error is a typed case with a stable
  code, and its message names what *is* allowed: the members, the tables, the kinds a parent may hold.
- **Default-deny.** Anything a model does not name does not exist for a pipeline checked against it.
  A change that admits something previously refused is a decision, recorded in
  [`DECISIONS.md`](DECISIONS.md) before it lands.
- **Dependencies.** Released `Fuaran.Core.*` and compute packages only. The boundary test fails on a
  reference to a UI-tier, program-domain or platform package; do not work around it.
- **The wire corpus is a contract.** `wire-fixtures/manifest.json` is authoritative for every family's
  vector count. Accept vectors are regenerated deliberately with
  `dotnet tests/Fuaran.DB.Tests/bin/Debug/net10.0/Fuaran.DB.Tests.dll --write-fixtures`, reviewed as a
  diff, and counted in the manifest by hand. A vector whose bytes move is a wire-format change; see
  [`STABILITY.md`](STABILITY.md).
- **Decisions and stability.** A design choice goes in `DECISIONS.md`, newest first; a change to the
  public surface goes in `STABILITY.md` under the version it ships in.

## Commits

One logical change per commit, with a message that says what changed and why. Do not commit build
output.
