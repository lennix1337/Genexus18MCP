param([string]$ReleasePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Wiring guards for the three CI checks that steer release.ps1: the cheap check before the
# build (Quiet with the candidate flow, Assert without it), the decision to create the
# candidate, and the wait before tagging. The blocks are cut out of release.ps1 by AST and run
# against recording stubs, so swapping a call, inverting a condition or dropping a guard fails here.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ReleasePath) { $ReleasePath = Join-Path $root 'release.ps1' }

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ReleasePath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }

function Get-IfBlock {
    param([Parameter(Mandatory = $true)][string]$Contains, [string]$Condition)
    $found = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains($Contains) -and
        (-not $Condition -or $node.Clauses[0].Item1.Extent.Text -eq $Condition)
    }, $true) | Sort-Object { $_.Extent.Text.Length })
    if ($found.Count -eq 0) { throw "release.ps1 lost the block containing: $Contains" }
    return [scriptblock]::Create($found[0].Extent.Text)
}

$earlyCheck = Get-IfBlock -Contains 'Checking required CI on the release source commit'
$candidateDecision = Get-IfBlock -Contains '$candidateCiValid ='
$preTagWait = Get-IfBlock -Contains 'Wait-ReleaseRequiredChecksGreen -Commit $releaseSourceCommit' -Condition '$DryRun'

$script:calls = @()
$script:quietValid = $false
function Step($m) {}
function Ok($m) { $script:calls += "ok:$m" }
function Warn($m) {}
function Test-ReleaseRequiredChecksQuiet { param([string]$Commit) $script:calls += "quiet:$Commit"; [pscustomobject]@{ Valid = $script:quietValid; Required = @('test') } }
function Assert-ReleaseRequiredChecksGreen { param([string]$Commit) $script:calls += "assert:$Commit"; if ($script:assertThrows) { throw 'ci not green' } }
function Ensure-ReleaseCandidateCi { param([string]$Commit, [string]$Tag, [switch]$DryRun) $script:calls += "ensure:${Commit}:${Tag}:dry=$([bool]$DryRun)" }
function Wait-ReleaseRequiredChecksGreen { param([string]$Commit) $script:calls += "wait:$Commit"; if ($script:waitThrows) { throw 'wait timed out' } }
function Assert-ReleasePrePublicationFresh { param([string]$SourceCommit, [string]$Tag, [string]$SummaryPath) $script:calls += "fresh:${SourceCommit}:${Tag}" }

function Invoke-Block {
    param([scriptblock]$Block, [bool]$DryRun, [bool]$AutoCandidate, [bool]$ResumeFlag, [bool]$QuietValid = $false, [bool]$AssertThrows = $false, [bool]$WaitThrows = $false)
    $script:calls = @(); $script:quietValid = $QuietValid; $script:assertThrows = $AssertThrows; $script:waitThrows = $WaitThrows
    $resumeRelease = $ResumeFlag; $releaseSourceCommit = 'abc123'; $tag = 'v9.9.9'; $preflightSummaryPath = 'summary.json'
    $thrown = $null
    try { . $Block } catch { $thrown = $_.Exception.Message }
    [pscustomobject]@{ Calls = @($script:calls | Where-Object { $_ -notlike 'ok:*' }); Messages = @($script:calls | Where-Object { $_ -like 'ok:*' }); Thrown = $thrown }
}

function Assert-Calls {
    param([string]$Case, $Result, [string[]]$Expected)
    if (($Result.Calls -join '|') -ne ($Expected -join '|')) {
        throw "$Case`: expected calls [$($Expected -join ', ')] but got [$($Result.Calls -join ', ')]."
    }
}

# 1. Cheap check before the build: skipped on -DryRun and on resume.
Assert-Calls 'early/dry-run' (Invoke-Block $earlyCheck $true $true $false) @()
Assert-Calls 'early/resume' (Invoke-Block $earlyCheck $false $true $true) @()
# With the candidate flow the check is informational (Quiet) and never asserts.
$r = Invoke-Block $earlyCheck $false $true $false -QuietValid $true
Assert-Calls 'early/auto/valid' $r @('quiet:abc123')
if ($r.Thrown -or "$($r.Messages)" -notmatch 'already green') { throw 'early/auto/valid: a green commit must be reported as already green without failing.' }
$r = Invoke-Block $earlyCheck $false $true $false -QuietValid $false
Assert-Calls 'early/auto/not-green' $r @('quiet:abc123')
if ($r.Thrown -or "$($r.Messages)" -notmatch 'not green') { throw 'early/auto/not-green: an unknown commit must not fail the cheap check when the candidate will be pushed.' }
# Without the candidate flow the CI must already be green: Assert, never Quiet, and its failure propagates.
Assert-Calls 'early/no-auto' (Invoke-Block $earlyCheck $false $false $false) @('assert:abc123')
$r = Invoke-Block $earlyCheck $false $false $false -AssertThrows $true
if ($r.Thrown -ne 'ci not green') { throw 'early/no-auto: a red CI must stop the release before the build.' }

# 2. Candidate decision: only a real run with CI already green skips the candidate.
Assert-Calls 'candidate/dry-run' (Invoke-Block $candidateDecision $true $true $false -QuietValid $true) @('ensure:abc123:v9.9.9:dry=True')
Assert-Calls 'candidate/green' (Invoke-Block $candidateDecision $false $true $false -QuietValid $true) @('quiet:abc123')
Assert-Calls 'candidate/not-green' (Invoke-Block $candidateDecision $false $true $false -QuietValid $false) @('quiet:abc123', 'ensure:abc123:v9.9.9:dry=False')
Assert-Calls 'candidate/resume' (Invoke-Block $candidateDecision $false $true $true) @()
Assert-Calls 'candidate/no-auto' (Invoke-Block $candidateDecision $false $false $false) @()

# 3. Wait before tagging: the candidate flow waits for CI, then the freshness gate runs.
Assert-Calls 'pretag/dry-run' (Invoke-Block $preTagWait $true $true $false) @()
Assert-Calls 'pretag/auto' (Invoke-Block $preTagWait $false $true $false) @('wait:abc123', 'fresh:abc123:v9.9.9')
Assert-Calls 'pretag/no-auto' (Invoke-Block $preTagWait $false $false $false) @('fresh:abc123:v9.9.9')
$r = Invoke-Block $preTagWait $false $true $false -WaitThrows $true
if ($r.Thrown -ne 'wait timed out' -or $r.Calls -contains 'fresh:abc123:v9.9.9') { throw 'pretag/wait-fails: a CI that never turns green must stop the release before the freshness gate.' }

Write-Host 'release-ci-wiring: early check, candidate decision and pre-tag wait guards passed' -ForegroundColor Green
