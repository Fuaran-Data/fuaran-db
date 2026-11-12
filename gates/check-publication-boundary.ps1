#Requires -Version 7.0
<#
.SYNOPSIS
    Publication-boundary sweep: this repository must name nothing a reader outside it cannot see.

.DESCRIPTION
    Fuaran.DB is Apache-2.0 (DECISIONS D3) and was written to a public standard from its first
    commit. "Written to a public standard" is a claim, and a claim without a check decays one
    convenient reference at a time, invisibly, because nothing breaks. So the standard is a gate.

    Every tracked file is swept for the vocabulary a reader outside this repository could not look
    up and should not learn from it: private component, product and client names, the operator
    command surface of the development tree this repository is developed in, its tool binaries and
    scripts, machine and actor identifiers of real people, absolute paths from a developer machine,
    and relative paths that leave the repository.

    THE SCOPE IS THE TRACKED SET (`git ls-files`), which is exactly what a publication carries.
    Build output is not swept: it is not published, and it is full of absolute paths.

    PATTERNS ARE WORD-BOUNDARY ANCHORED and case-insensitive. An unanchored term matches inside
    ordinary words, and a gate that cries wolf gets turned off.

    SOME TERMS ARE HELD AS DIGESTS, NOT WORDS. A confidential name cannot be banned by spelling it
    in a public file: the ban would publish it. Those terms are held below as the SHA-256 of their
    lower-cased form, and every word of every line is hashed and compared. The gate can say THAT a
    line names one, and which digest matched; it cannot say what the name is, and neither can a
    reader of this file.

    WHAT IS DELIBERATELY NOT BANNED. This repository's own name, its package ids, its host
    organisation, the public packages it depends on (Fuaran.Core, Fuaran.Compute) and bare phase
    citations ("Phase 1906"): a number names no private thing, and it ties a change to its record.
    The bare word "sibling" is not banned either; the PHRASE "sibling repo" is.

    THE EXCEPTIONS ARE LOUD. A hit that genuinely cannot be fixed is an itemised entry below, keyed
    to a file, a pattern and an exact occurrence count, with its reason, and every entry is printed
    with its sites on every run, a green one included. A hit with no entry is a violation; a count
    above the entry's is a violation; a count below it, or an entry matching nothing, is a stale
    entry and also a violation.

    THE HISTORY. A publication carries its history, so `-History` also sweeps every commit reachable
    from any ref: each commit's message, and every line any commit added (`git log -p -m`, so a line
    a merge introduced is read too). Every line of every commit's tree was added by some commit, so
    this reads every tree without checking each one out. In the history a carried (file, pattern)
    pair is admitted at any count, since a past tree may have held the term more often than the
    head does; anything else fails the run, exactly as on the head.

.PARAMETER History
    Also sweep every commit's message and every line any commit added, with the same verdict.

.PARAMETER ListPatterns
    Print the pattern catalogue and exit without sweeping.

.EXAMPLE
    pwsh ./gates/check-publication-boundary.ps1

.EXAMPLE
    pwsh ./gates/check-publication-boundary.ps1 -History
#>
[CmdletBinding()]
param(
    [switch] $History,
    [switch] $ListPatterns
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------------------------
# The pattern catalogue. `Id` is the key the carried list refers to; `Why` is printed beside a hit.
# ---------------------------------------------------------------------------------------------
$patterns = @(
    # -- The private coordination plane and its pillar names. ---------------------------------
    @{ Id = 'plane-collective'; Category = 'pillar'; Pattern = '\bFuaran[-. ]Build\b'
        Why = 'the collective name of a private coordination plane'
    }
    @{ Id = 'pillar-names'; Category = 'pillar'; Pattern = '\bFuaran[-.](Roadmap|Dispatch|Judge|Witness|App)\b'
        Why = 'a private pillar name'
    }

    # -- Private repositories, products and the tree they sit in. -----------------------------
    @{ Id = 'enclosing-tree'; Category = 'private-repo'; Pattern = '\bFuaran-?ToolUp\b'
        Why = 'the name of the enclosing private development tree'
    }
    @{ Id = 'product-family'; Category = 'private-repo'; Pattern = '\bToolUp\b'
        Why = 'a product family built on top; the open-core boundary must not be inferable from here'
    }
    @{ Id = 'product-repos'; Category = 'private-repo'; Pattern = '\btoolup-(forge|app|algorithms)\b'
        Why = 'a product repository'
    }
    @{ Id = 'company-repo'; Category = 'private-repo'; Pattern = '\bDiametrical\b'
        Why = 'a private strategy repository (and the legal entity that owns it; see the carried entries)'
    }
    @{ Id = 'brand-names'; Category = 'private-repo'; Pattern = '\b(Constellation|Forgewright|Xcelsys|ZenkoStudio)\b'
        Why = 'a private application brand name'
    }
    @{ Id = 'private-tiers'; Category = 'private-repo'; Pattern = '\b(orchestrator-demo|eval-suite|fuaran-cookbook|fuaran-app|fuaran-judge)\b'
        Why = 'a private peer repository'
    }
    @{ Id = 'private-packages'; Category = 'private-repo'; Pattern = '\b(Fuaran\.UI\.Orchestration|ToolUp\.Fuaran\.Adapter)\b'
        Why = 'a private package family; naming it discloses the commercial layer above the open one'
    }
    @{ Id = 'data-private'; Category = 'private-repo'; Pattern = '\bFuaran[-.]Survey\b(?![-.]Sav)|\bFuaran-Data/workspace\b'
        Why = 'a private repository of the organisation this repository is published from'
    }

    # -- The operator command vocabulary: named acts in a private process. --------------------
    @{ Id = 'cmd-roadmap'; Category = 'strategic-command'; Pattern = '\b(Refine|Refresh)[- ]Roadmap\b'
        Why = 'the name of a private operator command'
    }
    @{ Id = 'cmd-features'; Category = 'strategic-command'; Pattern = '\b(Suggest[- ]features|Accept[- ]suggestions)\b'
        Why = 'the name of a private operator command'
    }
    @{ Id = 'cmd-review'; Category = 'strategic-command'; Pattern = '\b(Evaluate[- ]design|Investigate[- ]gaps|Full[- ]Analysis)\b'
        Why = 'the name of a private operator command'
    }
    @{ Id = 'cmd-session'; Category = 'strategic-command'; Pattern = '\b(Prompt[- ]next|Make[- ]it[- ]so|Sync[- ]All|Author[- ]application[- ]plan)\b'
        Why = 'the name of a private operator command'
    }
    @{ Id = 'cmd-cookbook'; Category = 'strategic-command'; Pattern = '\bcookbook\b'
        Why = 'a private authoring surface and the commands over it'
    }

    # -- The private tool surface: binaries, verbs, orchestration scripts, governing docs. -----
    @{ Id = 'tool-cli'; Category = 'private-tool'; Pattern = '\b(roadmapctl|dispatchctl|fuaran-build)\b'
        Why = 'the name of a private tool binary'
    }
    @{ Id = 'tool-scripts'; Category = 'private-tool'; Pattern = '\b(pull-all|push-all|pack-all|check-roadmaps|machine-check|repo-discovery|bootstrap)\.ps1\b'
        Why = 'a private orchestration script that exists only in the enclosing tree'
    }
    @{ Id = 'tool-instructions'; Category = 'private-tool'; Pattern = '\b(CLAUDE\.md|LICENSING\.md|SDK-ADOPTION\.md|TIDY-UP\.md)\b'
        Why = 'a private governing document'
    }
    @{ Id = 'local-feed'; Category = 'private-tool'; Pattern = '\blocal-nuget-feed\b'
        Why = 'a shared local package folder that exists only in the enclosing tree'
    }

    # -- Who is running it: identifiers naming real machines, people and keys. -----------------
    @{ Id = 'machine-ids'; Category = 'identity'; Pattern = '\bdiam-[a-z0-9]+\b|\bDiametricalPC\b'
        Why = 'a real machine identifier'
    }
    @{ Id = 'actor-ids'; Category = 'identity'; Pattern = '\boperator-[a-z0-9]+-[a-z0-9-]+\b'
        Why = 'a real actor or signing-key identifier'
    }
    @{ Id = 'env-vars'; Category = 'identity'; Pattern = '\bFUARAN_(ROADMAP|MEMORY)_[A-Z_]+\b'
        Why = 'an environment variable of the private tool surface'
    }

    # -- Framing: words asserting a larger private structure around this repository. -----------
    @{ Id = 'framing-estate'; Category = 'framing'; Pattern = '\b(sub-)?estates?\b'
        Why = 'asserts a wider private ecosystem this repository is one member of'
    }
    @{ Id = 'framing-sibling-repo'; Category = 'framing'; Pattern = '\b(sibling|private) repo(s|sitor(y|ies))?\b'
        Why = 'implies peer repositories an outside reader cannot see'
    }
    @{ Id = 'framing-monorepo'; Category = 'framing'; Pattern = '\b(monorepo|workspace root)\b'
        Why = 'asserts an enclosing multi-repository tree'
    }

    # -- Absolute paths from a developer machine. ----------------------------------------------
    @{ Id = 'absolute-paths'; Category = 'layout'; Pattern = '[A-Za-z]:[\\/](repos|Users)[\\/]|/home/[a-z]+/'
        Why = 'an absolute path from a developer machine'
    }
)

# Terms held only as the SHA-256 (lower-case hex) of their lower-cased form: a confidential product
# and a client's names. Every [A-Za-z0-9]+ word of a line is lower-cased, hashed and looked up.
$digestId = 'held-term'
$digestWhy = 'a confidential product or client name (held here as a digest, so this gate does not publish it)'
$heldDigests = @(
    '132ef8ea5046d6fb70e774a66a8c34777f0d59c97ed762e3800dc367361d0d8d'
    '7f4b75430d69800a8dec96b6ea232b821df70deee391c80a78a2834fce4f241c'
    'f0c1a4fadb9a66609ee42c263def3d17f6fcccb008b8da5953a772f092ac6156'
    'ec33a2997255f0d611146fb2df8f94b0b8aeb0c5e04cad8dc4ed13ef77981d03'
)

# The relative-path rule resolves each `..`-relative literal against the file that holds it and
# asks whether it lands outside this repository. Head only: a past file's location is not on disk.
$outsidePathId = 'outside-repo-path'
$outsidePathWhy = 'a relative path that resolves OUTSIDE this repository; it assumes an enclosing layout nothing here enforces'

# ---------------------------------------------------------------------------------------------
# Carried exceptions: the residue this repository knowingly holds. File, pattern id, the EXACT
# count on the head, and the reason, printed verbatim on every run.
# ---------------------------------------------------------------------------------------------
$rWhyCopyright = @'
LEGAL ENTITY, NOT A REPOSITORY. The copyright holder, in the licence and attribution a published
        package is required to carry, and in the package metadata that states them. Removing it
        would be a defect rather than a scrub. It is itemised rather than exempted by a narrower
        pattern, so a further appearance, anywhere it would be naming the private counterpart,
        still reddens.
'@

$rWhyDevFeed = @'
THE DEVELOPMENT LOOP'S FOLDER FEED. pack.ps1 packs into a local folder feed by default, the
        layout this repository is developed in; -Feed names any other folder, and publication
        packs into the runner's own directory instead (.github/workflows/publish-packages.yml). The
        default is a convenience of one development tree, stated so a reader can see it.
'@

$rWhyDependencyGuard = @'
A DEPENDENCY GUARD NAMES WHAT IT REFUSES. The dependency boundary (DECISIONS D3) is a test
        that goes red on any project reference to a package family this repository must not take,
        and the decision that states it. A guard cannot refuse a family without naming its prefix,
        and the family it names is published on nuget.org, so the name discloses nothing a reader
        cannot look up. Counted, so a mention outside the guard still reddens.
'@

$carried = @(
    @{ File = 'tests/Fuaran.DB.Tests/BoundaryTests.fs'; Pattern = 'product-family'; Count = 3; Reason = $rWhyDependencyGuard }
    @{ File = 'DECISIONS.md'; Pattern = 'product-family'; Count = 1; Reason = $rWhyDependencyGuard }
    @{ File = 'LICENSE'; Pattern = 'company-repo'; Count = 1; Reason = $rWhyCopyright }
    @{ File = 'NOTICE'; Pattern = 'company-repo'; Count = 2; Reason = $rWhyCopyright }
    @{ File = 'Directory.Build.props'; Pattern = 'company-repo'; Count = 2; Reason = $rWhyCopyright }
    @{ File = 'pack.ps1'; Pattern = 'local-feed'; Count = 1; Reason = $rWhyDevFeed }
    @{ File = 'pack.ps1'; Pattern = 'outside-repo-path'; Count = 1; Reason = $rWhyDevFeed }
)

# Carried HISTORY exceptions: a recorded commit message cannot be edited in an ordinary change. The
# only remedies are to rewrite the published history or to publish with it, and that is a decision
# taken at a publication (DECISIONS D14), so each is itemised by commit and pattern and printed by
# every -History run. A carried entry whose commit no longer carries the hit is a stale entry.
$carriedHistory = @(
    @{ Commit = 'cf0b2f25e012'; Pattern = 'framing-monorepo'
        Reason = 'a dependency-raise message says the raise was recorded in a version ledger at the root of the tree it was developed in; it names no repository, product or person (DECISIONS D14)'
    }
)

# ---------------------------------------------------------------------------------------------
# Engine.
# ---------------------------------------------------------------------------------------------
if ($ListPatterns) {
    foreach ($p in $patterns) { Write-Host ("  {0,-22} {1}" -f $p.Id, $p.Pattern) }
    Write-Host ("  {0,-22} {1} term(s) held as SHA-256 digests" -f $digestId, $heldDigests.Count)
    Write-Host ("  {0,-22} (resolved, not matched) ..-relative paths landing outside the repository" -f $outsidePathId)
    exit 0
}

$selfPath = 'gates/check-publication-boundary.ps1'
$binaryExtensions = @('.png', '.jpg', '.jpeg', '.gif', '.ico', '.zip', '.gz', '.pdf', '.dll', '.exe', '.pdb', '.nupkg')
$compiled = foreach ($p in $patterns) {
    [pscustomobject]@{ Id = $p.Id; Why = $p.Why
        Regex = [regex]::new($p.Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
}
$wordRegex = [regex]::new('[A-Za-z0-9]+')
$sha = [Security.Cryptography.SHA256]::Create()
$held = [Collections.Generic.HashSet[string]]::new([string[]]$heldDigests)
$digestCache = @{}

function Get-LineHits([string] $line) {
    # Returns (pattern id, why) pairs, one per occurrence.
    foreach ($c in $compiled) {
        $n = $c.Regex.Matches($line).Count
        for ($k = 0; $k -lt $n; $k++) { , @($c.Id, $c.Why) }
    }
    foreach ($m in $wordRegex.Matches($line)) {
        $w = $m.Value.ToLowerInvariant()
        if (-not $digestCache.ContainsKey($w)) {
            $digestCache[$w] = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($w))).ToLowerInvariant()
        }
        if ($held.Contains($digestCache[$w])) { , @($digestId, $digestWhy) }
    }
}

$relPathRegex = [regex]::new('(?:\.\.[\\/])+[A-Za-z0-9_.\-][A-Za-z0-9_.\-\\/]*')
$rootFull = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')

Push-Location $repoRoot
try {
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in $repoRoot" }
}
finally { Pop-Location }

if ($tracked.Count -eq 0) {
    # A sweep of nothing is not a pass; say which it is.
    Write-Host 'publication boundary: no tracked files, nothing swept (not a pass).'
    exit 1
}

$hits = [Collections.Generic.List[object]]::new()
$scanned = 0
foreach ($rel in $tracked) {
    if ($rel -eq $selfPath) { continue }
    if ($binaryExtensions -contains [IO.Path]::GetExtension($rel).ToLowerInvariant()) { continue }
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    $lines = [IO.File]::ReadAllLines($full)
    $scanned++
    $fileDir = Split-Path -Parent $full
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        foreach ($h in @(Get-LineHits $line)) {
            $hits.Add([pscustomobject]@{ File = $rel; Line = $i + 1; Pattern = $h[0]; Why = $h[1]; Text = $line.Trim() })
        }
        foreach ($m in $relPathRegex.Matches($line)) {
            $candidate = $m.Value.TrimEnd('\', '/', '"', "'", ')', ',', ';')
            $resolved = $null
            try { $resolved = [IO.Path]::GetFullPath((Join-Path $fileDir $candidate)).TrimEnd('\', '/') } catch { }
            if ($null -ne $resolved -and -not ($resolved -eq $rootFull -or
                    $resolved.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
                $hits.Add([pscustomobject]@{ File = $rel; Line = $i + 1; Pattern = $outsidePathId; Why = $outsidePathWhy; Text = $line.Trim() })
            }
        }
    }
}

# Reconcile the head's hits against the carried list.
$knownIds = @($compiled.Id) + @($digestId, $outsidePathId)
$defects = [Collections.Generic.List[string]]::new()
$carriedKeys = @{}
foreach ($e in $carried) {
    $carriedKeys["$($e.File)|$($e.Pattern)"] = $e
    if ($knownIds -notcontains $e.Pattern) {
        $defects.Add("carried entry names unknown pattern id '$($e.Pattern)' ($($e.File))")
    }
}

Write-Host ''
Write-Host '=== Publication boundary ====================================================='
Write-Host "  $($compiled.Count) vocabulary patterns, $($heldDigests.Count) digest-held terms and 1 resolved-path rule over $scanned tracked file(s)."
Write-Host "  Self-excluded (it spells out every banned term): $selfPath"
Write-Host ''
Write-Host "  CARRIED EXCEPTIONS ($($carried.Count)), printed on every run:"
foreach ($e in $carried) {
    $sites = @($hits | Where-Object { $_.File -eq $e.File -and $_.Pattern -eq $e.Pattern })
    Write-Host "    $($e.File) [$($e.Pattern)] x$($e.Count)"
    foreach ($s in $sites) { Write-Host "      line $($s.Line): $($s.Text)" }
    if ($sites.Count -eq 0) {
        $defects.Add("STALE carried entry: $($e.File) [$($e.Pattern)] expects $($e.Count) and there are none; remove it")
    }
    elseif ($sites.Count -ne $e.Count) {
        $defects.Add("carried entry COUNT MOVED: $($e.File) [$($e.Pattern)] carries $($e.Count), found $($sites.Count); a new occurrence is a new violation, and a cleaned one leaves the entry")
    }
}
foreach ($r in ($carried | ForEach-Object { $_.Reason } | Select-Object -Unique)) { Write-Host "    reason: $($r.Trim())" }
Write-Host ''

$uncovered = @($hits | Where-Object { -not $carriedKeys.ContainsKey("$($_.File)|$($_.Pattern)") })
foreach ($h in ($uncovered | Sort-Object File, Line)) {
    Write-Host "  VIOLATION $($h.File):$($h.Line) [$($h.Pattern)] $($h.Text)"
    Write-Host "    $($h.Why)"
}
foreach ($d in $defects) { Write-Host "  CARRIED-LIST DEFECT: $d" }

$historyFailed = $false
if ($History) {
    Push-Location $repoRoot
    try {
        $commits = @(& git rev-list --all)
        if ($LASTEXITCODE -ne 0) { throw 'git rev-list failed' }
        # One pass over every commit: a record separator, the sha, the message, then the patch (each
        # merge against each parent). Added lines are '+' lines outside the '+++' file header.
        $raw = & git -c core.quotepath=off log --all -p -m --no-color --no-ext-diff --format='%x1e%H%n%B%x1f'
        if ($LASTEXITCODE -ne 0) { throw 'git log failed' }
    }
    finally { Pop-Location }

    $historyHits = [Collections.Generic.List[object]]::new()
    $current = ''; $file = ''; $inMessage = $false; $linesRead = 0
    foreach ($line in $raw) {
        # Ordinal: a culture-aware StartsWith treats a control character as ignorable and matches every line.
        if ($line.Length -gt 0 -and $line[0] -eq [char]0x1e) { $current = $line.Substring(1, 12); $inMessage = $true; $file = ''; continue }
        if ($inMessage) {
            if ($line.Contains([string][char]0x1f)) { $inMessage = $false; $line = $line.Replace([string][char]0x1f, '') }
            foreach ($h in @(Get-LineHits $line)) {
                $historyHits.Add([pscustomobject]@{ Commit = $current; File = '(message)'; Pattern = $h[0]; Text = $line.Trim() })
            }
            continue
        }
        if ($line.StartsWith('+++ ')) { $file = if ($line -match '^\+\+\+ b/(.*)$') { $Matches[1] } else { '' }; continue }
        if (-not $line.StartsWith('+') -or $file -eq $selfPath) { continue }
        $linesRead++
        foreach ($h in @(Get-LineHits $line.Substring(1))) {
            if (-not $carriedKeys.ContainsKey("$file|$($h[0])")) {
                $historyHits.Add([pscustomobject]@{ Commit = $current; File = $file; Pattern = $h[0]; Text = $line.Substring(1).Trim() })
            }
        }
    }

    Write-Host ''
    Write-Host "  HISTORY: $($commits.Count) commit(s) reachable from every ref; their messages and $linesRead added line(s) swept."
    Write-Host "  CARRIED HISTORY EXCEPTIONS ($($carriedHistory.Count)), printed on every -History run:"
    foreach ($e in $carriedHistory) {
        $sites = @($historyHits | Where-Object { $_.File -eq '(message)' -and $_.Commit -eq $e.Commit -and $_.Pattern -eq $e.Pattern })
        Write-Host "    $($e.Commit) message [$($e.Pattern)]: $($e.Reason)"
        foreach ($s in $sites) { Write-Host "      $($s.Text)" }
        if ($sites.Count -eq 0) {
            Write-Host "  CARRIED-LIST DEFECT: STALE history entry $($e.Commit) [$($e.Pattern)]: no message carries it; remove it"
            $historyFailed = $true
        }
    }
    $historyHits = @($historyHits | Where-Object { $h = $_; -not ($h.File -eq '(message)' -and ($carriedHistory | Where-Object { $_.Commit -eq $h.Commit -and $_.Pattern -eq $h.Pattern })) })
    foreach ($h in $historyHits) {
        $t = if ($h.Text.Length -gt 100) { $h.Text.Substring(0, 100) + '...' } else { $h.Text }
        Write-Host "  HISTORY VIOLATION $($h.Commit) $($h.File) [$($h.Pattern)] $t"
    }
    if ($commits.Count -eq 0 -or $linesRead -eq 0) {
        Write-Host '  HISTORY: nothing was read, which is not a pass.'
        $historyFailed = $true
    }
    $historyFailed = $historyFailed -or ($historyHits.Count -gt 0)
}

Write-Host ''
if ($uncovered.Count -gt 0 -or $defects.Count -gt 0 -or $historyFailed) {
    Write-Host '  RESULT: FAILED. Fix the site, or, if it genuinely cannot be fixed here, add an itemised'
    Write-Host '  entry to $carried in this script saying why.'
    Write-Host '=============================================================================='
    exit 1
}
Write-Host "  RESULT: clean. 0 violations; $($carried.Count) carried exception(s) listed above$(if ($History) { '; history clean' })."
Write-Host '=============================================================================='
exit 0
