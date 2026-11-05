$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Pre-publication protections: required-CI evidence on the exact source commit, live
# origin/main alignment, a still-valid preflight certificate, and no forced git pushes.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\release-contract.ps1')

# --- Pure contracts -----------------------------------------------------------------
$classic = [pscustomobject]@{ protection = [pscustomobject]@{ required_status_checks = [pscustomobject]@{ contexts = @('test'); checks = @([pscustomobject]@{ context = 'test'; app_id = 1 }) } } }
$ruleset = @([pscustomobject]@{ type = 'required_status_checks'; parameters = [pscustomobject]@{ required_status_checks = @([pscustomobject]@{ context = 'lint' }) } },
             [pscustomobject]@{ type = 'deletion' })
$union = @(Get-GxMcpReleaseRequiredCheckContexts -Branch $classic -Rules $ruleset)
if (($union -join ',') -ne 'lint,test') { throw "Required contexts must be the de-duplicated union of branch protection and rulesets, got: $($union -join ',')" }
# The workflow check is mandatory even when protection exposes nothing (hidden from the token
# or removed): the publish workflow requires it regardless, so the local gate must too.
foreach ($hidden in @([pscustomobject]@{ protection = $null }, $null, [pscustomobject]@{ protection = [pscustomobject]@{ required_status_checks = $null } })) {
    if ((@(Get-GxMcpReleaseRequiredCheckContexts -Branch $hidden -Rules @()) -join ',') -ne 'test') { throw 'Empty protection must still require the test check.' }
}

$sha = 'c' * 40
function New-Run([int]$Id, [string]$Name, [string]$Status, [string]$Conclusion, [string]$AppId = '15368', [string]$Started = '2026-10-21T10:00:00Z', [string]$HeadSha = $sha) {
    [pscustomobject]@{ id = $Id; name = $Name; status = $Status; conclusion = $Conclusion; app_id = $AppId; started_at = $Started; head_sha = $HeadSha }
}
$cases = @(
    @{ label = 'green'; runs = @(New-Run 1 'test' 'completed' 'success'); valid = $true },
    @{ label = 'neutral is not success'; runs = @(New-Run 1 'test' 'completed' 'neutral'); valid = $false },
    @{ label = 'skipped is not success'; runs = @(New-Run 1 'test' 'completed' 'skipped'); valid = $false },
    @{ label = 'success from another app (spoof)'; runs = @(New-Run 1 'test' 'completed' 'success' '99999'); valid = $false },
    @{ label = 'spoof newer than real run is ignored'; runs = @((New-Run 1 'test' 'completed' 'success'), (New-Run 2 'test' 'completed' 'failure' '99999' '2026-10-21T11:00:00Z')); valid = $true },
    @{ label = 'run bound to another commit'; runs = @(New-Run 1 'test' 'completed' 'success' '15368' '2026-10-21T10:00:00Z' ('d' * 40)); valid = $false },
    @{ label = 'newest by started_at wins over higher id'; runs = @((New-Run 9 'test' 'completed' 'failure' '15368' '2026-10-21T09:00:00Z'), (New-Run 3 'test' 'completed' 'success' '15368' '2026-10-21T10:00:00Z')); valid = $true },
    @{ label = 'in progress'; runs = @(New-Run 1 'test' 'in_progress' ''); valid = $false },
    @{ label = 'failed'; runs = @(New-Run 1 'test' 'completed' 'failure'); valid = $false },
    @{ label = 'absent'; runs = @(New-Run 1 'other' 'completed' 'success'); valid = $false },
    @{ label = 'rerun supersedes failure'; runs = @((New-Run 1 'test' 'completed' 'failure' '15368' '2026-10-21T10:00:00Z'), (New-Run 2 'test' 'completed' 'success' '15368' '2026-10-21T10:05:00Z')); valid = $true },
    @{ label = 'older pass superseded by newer failure'; runs = @((New-Run 1 'test' 'completed' 'success' '15368' '2026-10-21T10:00:00Z'), (New-Run 2 'test' 'completed' 'failure' '15368' '2026-10-21T10:05:00Z')); valid = $false }
)
foreach ($case in $cases) {
    $verdict = Test-GxMcpReleaseRequiredChecks -RequiredContexts @('test') -CheckRuns $case.runs -Statuses @() -Commit $sha
    if ($verdict.Valid -ne $case.valid) { throw "Required-check verdict wrong for '$($case.label)': $($verdict.Error)" }
}
$statusOnly = Test-GxMcpReleaseRequiredChecks -RequiredContexts @('ci/legacy') -CheckRuns @() -Statuses @([pscustomobject]@{ context = 'ci/legacy'; state = 'success' }) -Commit $sha
if (-not $statusOnly.Valid) { throw 'A successful commit status must satisfy a required context.' }
$statusPending = Test-GxMcpReleaseRequiredChecks -RequiredContexts @('ci/legacy') -CheckRuns @() -Statuses @([pscustomobject]@{ context = 'ci/legacy'; state = 'pending' }) -Commit $sha
if ($statusPending.Valid) { throw 'A pending commit status must not satisfy a required context.' }
$statusForTest = Test-GxMcpReleaseRequiredChecks -RequiredContexts @('test') -CheckRuns @() -Statuses @([pscustomobject]@{ context = 'test'; state = 'success' }) -Commit $sha
if ($statusForTest.Valid) { throw 'A commit status must not stand in for the GitHub Actions test check run.' }

# --- release.ps1 functions, extracted so the production code runs -------------------
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('Test-ReleaseBaseAlignment', 'Assert-ReleaseBaseFresh', 'Assert-ReleaseRequiredChecksGreen', 'Assert-ReleasePrePublicationFresh')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "release.ps1 is missing $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Fail([string]$Message) { throw "FAIL: $Message" }
function Ok([string]$Message) { }
function Warn([string]$Message) { $script:warnings += $Message }
function Get-ReleasePreflightInputs { param($ArtifactFingerprint) throw 'certificate inputs must not be needed without a summary' }

$temp = Join-Path $env:TEMP ('gxmcp-release-freshness-' + [guid]::NewGuid().ToString('N'))
$origin = Join-Path $temp 'origin.git'
$clone = Join-Path $temp 'clone'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$originalLocation = Get-Location
try {
    & git init -q --bare -b main $origin
    & git clone -q $origin $clone 2>$null
    Set-Location $clone
    & git config user.email 'fresh@example.invalid'; & git config user.name 'fresh-test'
    Set-Content -LiteralPath (Join-Path $clone 'a.txt') -Value 'a' -Encoding ascii
    & git add a.txt; & git commit -q -m 'base'; & git push -q origin HEAD:main 2>$null
    $base = (git rev-parse HEAD).Trim()

    # Base alignment: equal to origin/main passes; one release commit ahead passes; a moved origin fails.
    if (-not (Assert-ReleaseBaseFresh -Tag 'v9.9.9').AtRemoteHead) { throw 'HEAD equal to origin/main must report AtRemoteHead.' }
    Set-Content -LiteralPath (Join-Path $clone 'a.txt') -Value 'release' -Encoding ascii
    & git commit -q -am 'release: v9.9.9'
    $releaseCommit = (git rev-parse HEAD).Trim()
    $state = Assert-ReleaseBaseFresh -Tag 'v9.9.9'
    if ($state.AtRemoteHead -or $state.RemoteMainHead -ne $base) { throw 'One unpublished release commit on top of origin/main must pass as not-at-remote-head.' }
    $other = Join-Path $temp 'other'
    & git clone -q $origin $other 2>$null
    & git -C $other config user.email 'o@example.invalid'; & git -C $other config user.name 'other'
    Set-Content -LiteralPath (Join-Path $other 'b.txt') -Value 'b' -Encoding ascii
    & git -C $other add b.txt; & git -C $other commit -q -m 'someone else'; & git -C $other push -q origin HEAD:main 2>$null
    $threw = $false
    try { Assert-ReleaseBaseFresh -Tag 'v9.9.9' | Out-Null } catch { $threw = $_.Exception.Message -match 'stale or diverged' }
    if (-not $threw) { throw 'A moved origin/main must fail the base freshness check.' }

    # Required CI: `gh` is shadowed by a function that serves canned API answers.
    $script:ghCalls = @()
    $script:branchExit = 0
    $script:branchJson = '{"protection":{"required_status_checks":{"contexts":["test"]}}}'
    function New-RunLine([string]$Conclusion = 'success', [string]$Status = 'completed', [string]$AppId = '15368', [string]$Head = $releaseCommit) {
        return "1`ttest`t$Status`t$Conclusion`t$AppId`t2026-10-21T10:00:00Z`t$Head"
    }
    $script:runLines = @(New-RunLine)
    function gh {
        $script:ghCalls += ($args -join ' ')
        $global:LASTEXITCODE = 0
        $joined = $args -join ' '
        if ($joined -match 'branches/main$') {
            $global:LASTEXITCODE = $script:branchExit
            if ($script:branchExit -ne 0) { return }
            return $script:branchJson
        }
        if ($joined -match 'rules/branches/main') { return '[]' }
        if ($joined -match 'check-runs') { return $script:runLines }
        if ($joined -match '/status\?') { return @() }
        throw "unexpected gh call: $joined"
    }
    Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit
    if (-not ($script:ghCalls -match "commits/$releaseCommit/check-runs")) { throw 'Required CI must be read for the exact source commit.' }
    $script:runLines = @(New-RunLine '' 'in_progress')
    $threw = $false
    try { Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit } catch { $threw = $_.Exception.Message -match 'Required CI is not green' }
    if (-not $threw) { throw 'A pending required check must stop the release.' }
    # Protection that exposes no required checks still requires `test`, with the workflow's rules.
    $script:branchJson = '{"protection":null}'
    $script:runLines = @(New-RunLine 'success' 'completed' '99999')
    $threw = $false
    try { Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit } catch { $threw = $_.Exception.Message -match 'app 15368' }
    if (-not $threw) { throw 'With empty protection, a test run from another app must still stop the release.' }
    foreach ($weak in @('skipped', 'neutral')) {
        $script:runLines = @(New-RunLine $weak)
        $threw = $false
        try { Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit } catch { $threw = $_.Exception.Message -match "concluded $weak" }
        if (-not $threw) { throw "A $weak test run must stop the release." }
    }
    $script:runLines = @()
    $threw = $false
    try { Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit } catch { $threw = $_.Exception.Message -match 'no check run' }
    if (-not $threw) { throw 'A commit with no test check run must stop the release even with empty protection.' }
    $script:runLines = @(New-RunLine)
    Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit
    $script:branchJson = '{"protection":{"required_status_checks":{"contexts":["test"]}}}'
    $script:branchExit = 1
    $threw = $false
    try { Assert-ReleaseRequiredChecksGreen -Commit $releaseCommit } catch { $threw = $_.Exception.Message -match 'refusing to publish' }
    if (-not $threw) { throw 'An unreadable branch protection must fail closed, not read as "no required checks".' }
    $script:branchExit = 0

    # Pre-publication: a moved HEAD, then a missing certificate, each stop before CI is consulted.
    $script:ghCalls = @()
    $summaryPath = Join-Path $temp 'missing-preflight.json'
    $Version = '9.9.9'; $allowLegacyAssets = $false
    # The base freshness check inside the composite gate: origin/main moved, HEAD is the release commit.
    $threw = $false
    try { Assert-ReleasePrePublicationFresh -SourceCommit $releaseCommit -Tag 'v9.9.9' -SummaryPath $summaryPath } catch { $threw = $_.Exception.Message -match 'stale or diverged'; $script:lastMsg = $_.Exception.Message }
    if (-not $threw) { throw "A moved origin/main must stop publication from the composite gate: $script:lastMsg" }
    if (@($script:ghCalls).Count -ne 0) { throw 'CI must not be consulted when the base is stale.' }
    $threw = $false
    try { Assert-ReleasePrePublicationFresh -SourceCommit $base -Tag 'v9.9.9' -SummaryPath $summaryPath } catch { $threw = $_.Exception.Message -match 'no longer the release source commit' }
    if (-not $threw) { throw 'A HEAD that moved since the manifest was written must stop publication.' }
    & git fetch -q origin 2>$null
    & git reset -q --hard origin/main
    Set-Content -LiteralPath (Join-Path $clone 'a.txt') -Value 'release' -Encoding ascii
    & git commit -q -am 'release: v9.9.9'
    $releaseCommit = (git rev-parse HEAD).Trim()
    $threw = $false
    try { Assert-ReleasePrePublicationFresh -SourceCommit $releaseCommit -Tag 'v9.9.9' -SummaryPath $summaryPath } catch { $threw = $_.Exception.Message -match "preflight certificate"; $script:lastMsg = $_.Exception.Message }
    if (-not $threw) { throw "Publishing without a matching passed preflight certificate must be refused: $script:lastMsg" }
    if (@($script:ghCalls).Count -ne 0) { throw 'CI must not be consulted before the cheaper local freshness checks pass.' }

    # Success path: a valid certificate and green checks reach the final CI re-check for the exact commit.
    Set-Content -LiteralPath $summaryPath -Value '{}' -Encoding ascii
    function Get-GxMcpReleaseArtifactFingerprint { param($RepositoryRoot, $Version, [switch]$AllowLegacy) return 'fixed-fingerprint' }
    function Get-ReleasePreflightInputs { param($ArtifactFingerprint) return @{} }
    function Test-GxMcpReleasePreflightCertificate { param($Summary, $Expected) return $true }
    $script:runLines = @(New-RunLine)
    $script:ghCalls = @()
    Assert-ReleasePrePublicationFresh -SourceCommit $releaseCommit -Tag 'v9.9.9' -SummaryPath $summaryPath
    if (-not ($script:ghCalls -match "commits/$releaseCommit/check-runs")) { throw 'The composite gate must re-check required CI on the release source commit.' }
    $script:runLines = @(New-RunLine '' 'in_progress')
    $threw = $false
    try { Assert-ReleasePrePublicationFresh -SourceCommit $releaseCommit -Tag 'v9.9.9' -SummaryPath $summaryPath } catch { $threw = $_.Exception.Message -match 'Required CI is not green' }
    if (-not $threw) { throw 'The composite gate must refuse publication while required CI is not green.' }
} finally {
    Set-Location $originalLocation
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

# --- Source-level guards ------------------------------------------------------------
$source = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
$freshIndex = $source.IndexOf("Assert-ReleasePrePublicationFresh -SourceCommit", [StringComparison]::Ordinal)
$tagIndex = $source.IndexOf("Invoke-Cmd 'git' @('tag'", [StringComparison]::Ordinal)
$pushIndex = $source.IndexOf("Invoke-Cmd 'git' @('push', 'origin', 'HEAD')", [StringComparison]::Ordinal)
if ($freshIndex -lt 0 -or $tagIndex -lt $freshIndex -or $pushIndex -lt $freshIndex) { throw 'Freshness must be verified before the tag is created and before main/tag are pushed.' }
foreach ($line in @($source -split "`n" | Where-Object { $_ -match "'push'|git push" })) {
    if ($line -match "--force|'-f'|--no-verify|--delete|'\+|force-with-lease") { throw "release.ps1 must never force-push or bypass hooks: $($line.Trim())" }
}
# Cheap CI presence is verified before the heavy build/preflight; PrepareOnly stops before them.
$earlyCi = $source.IndexOf('Step "Checking required CI on the release source commit"', [StringComparison]::Ordinal)
$buildStep = $source.IndexOf('Step "Building (build.ps1)"', [StringComparison]::Ordinal)
$preflightStep = $source.IndexOf('Step "Running complete release preflight"', [StringComparison]::Ordinal)
$prepareStop = $source.IndexOf("Write-ReleaseStatus -Phase 'prepared'", [StringComparison]::Ordinal)
if ($earlyCi -lt 0 -or $earlyCi -gt $buildStep -or $earlyCi -gt $preflightStep) { throw 'Required CI must be checked before the build and the complete preflight.' }
if ($prepareStop -lt 0 -or $prepareStop -gt $earlyCi -or $prepareStop -gt $buildStep -or $prepareStop -gt $freshIndex) { throw '-PrepareOnly must stop before the CI gate, the build, and any publication step.' }
# An unreadable release state must stop the run rather than read as "no release yet".
if ($source -notmatch "notmatch 'release not found'") { throw 'release.ps1 must treat only an explicit release-not-found as an absent GitHub Release.' }
if ($source -match 'gh api.*(-X|--method)\s+(PUT|PATCH|DELETE|POST)' -or $source -match 'branches/main/protection[^''"]*(PUT|PATCH|DELETE)') {
    throw 'release.ps1 must not change branch protection.'
}
Write-Host 'release-freshness: required CI, live base alignment, certificate and no-force guards passed' -ForegroundColor Green
