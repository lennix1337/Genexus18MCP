$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Regression guards for the release run lifecycle: remote state (candidate branch/PR) is created
# only after the local preflight passes, a stale candidate of this release is replaced instead of
# pushed over, a failed run undoes exactly the state it created, and every run ends with one
# RELEASE SUMMARY line.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$releasePath = Join-Path $root 'release.ps1'
$releaseSource = Get-Content -LiteralPath $releasePath -Raw
$candidatePath = Join-Path $root 'scripts\release-candidate-ci.ps1'

$tokens = $null; $errors = $null
$releaseAst = [System.Management.Automation.Language.Parser]::ParseFile($releasePath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$candidateTokens = $null; $candidateErrors = $null
$candidateAst = [System.Management.Automation.Language.Parser]::ParseFile($candidatePath, [ref]$candidateTokens, [ref]$candidateErrors)
if ($candidateErrors.Count) { throw $candidateErrors[0] }

function Get-FunctionText {
    param([Parameter(Mandatory = $true)]$Ast, [Parameter(Mandatory = $true)][string]$Name)
    $definition = $Ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
    if (-not $definition) { throw "Function $Name is missing." }
    return $definition.Extent.Text
}

# 1. Ordering: no candidate push before the preflight; candidate before main/tag publication.
$preflightPassed = $releaseSource.IndexOf('Complete release preflight passed.', [StringComparison]::Ordinal)
$candidatePush = $releaseSource.IndexOf('Ensure-ReleaseCandidateCi -Commit $releaseSourceCommit', [StringComparison]::Ordinal)
$mainPush = $releaseSource.IndexOf("Invoke-Cmd 'git' @('push', 'origin', 'HEAD')", [StringComparison]::Ordinal)
if ($preflightPassed -lt 0 -or $candidatePush -lt 0 -or $mainPush -lt 0) {
    throw 'release.ps1 lost the preflight, candidate or publication markers this guard relies on.'
}
if ($releaseSource.Substring(0, $preflightPassed) -match 'Ensure-ReleaseCandidateCi') {
    throw 'The candidate branch/PR is remote state and must not be created before the local preflight passes.'
}
if ($candidatePush -lt $preflightPassed -or $candidatePush -gt $mainPush) {
    throw 'The candidate branch must be pushed after the local preflight and before main and the tag are published.'
}
$publicationStarts = $releaseSource.IndexOf('$script:releaseUndoDone = $true', [StringComparison]::Ordinal)
if ($publicationStarts -lt 0 -or $publicationStarts -gt $mainPush) {
    throw 'Publication must disable the undo before main is pushed, so a published run is resumed and never undone.'
}

# 1b. The undo's "already dirty before the run" list is captured before the run writes any file;
# captured later, the files the run itself wrote (release-issues.json, server.json, generated
# docs) look like operator edits and a failed run leaves them modified.
$dirtyCapture = $releaseSource.IndexOf('$script:releasePreRunDirtyPaths = @(git status --porcelain', [StringComparison]::Ordinal)
$firstWrites = @('Step "Snapshotting release issues"', 'Step "Synchronizing release-facing metadata"', 'Step "Resolving version"') |
    ForEach-Object { $releaseSource.IndexOf($_, [StringComparison]::Ordinal) }
if ($dirtyCapture -lt 0 -or @($firstWrites | Where-Object { $_ -lt 0 }).Count) {
    throw 'release.ps1 lost the pre-run dirty capture or the first writing steps this guard relies on.'
}
if (@($firstWrites | Where-Object { $_ -lt $dirtyCapture }).Count) {
    throw 'The pre-run dirty paths must be captured before the run snapshots issues, syncs metadata or bumps the version.'
}

# 2. Every terminal path prints the RELEASE SUMMARY line (failure, trap, prepare, dry run, complete).
$summaryCalls = [regex]::Matches($releaseSource, 'Write-ReleaseFinalSummary -ExitCode').Count
if ($summaryCalls -lt 5) { throw "Expected the RELEASE SUMMARY line on every terminal path (found $summaryCalls call sites, need 5)." }

# 3. Candidate replacement: a stale candidate branch of this release is replaced, never pushed over.
$candidateProbe = {
    param($CandidatePath, $RemoteLine, [bool]$DryRunMode)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $script:calls = @()
    $script:created = $false
    $script:releaseCandidateCreatedThisRun = $false
    function Step($m) {}
    function Ok($m) {}
    function Warn($m) {}
    function Fail($m) { throw "Fail: $m" }
    function Invoke-Cmd {
        param([string]$Exe, [object[]]$Arguments, [switch]$IgnoreExit)
        $script:calls += ("$Exe " + (@($Arguments) -join ' '))
    }
    function git {
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'ls-remote') { return $RemoteLine }
    }
    function gh {
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'pr' -and $args[1] -eq 'list') { return '[]' }
        if ($args[0] -eq 'pr' -and $args[1] -eq 'create') { $script:created = $true; return 'https://github.com/o/r/pull/9' }
    }
    $statusState = $null
    . $CandidatePath
    Ensure-ReleaseCandidateCi -Commit 'newsha' -Tag 'v0.0.1' -DryRun:$DryRunMode
    [pscustomobject]@{ Calls = @($script:calls); Created = $script:created; Created2 = $script:releaseCandidateCreatedThisRun }
}
$stale = & $candidateProbe $candidatePath "oldsha`trefs/heads/candidate/v0.0.1" $false
$deleteIndex = [array]::IndexOf(@($stale.Calls), 'git push origin :refs/heads/candidate/v0.0.1')
$pushIndex = [array]::IndexOf(@($stale.Calls), 'git push origin newsha:refs/heads/candidate/v0.0.1')
if ($deleteIndex -lt 0 -or $pushIndex -le $deleteIndex) {
    throw "A stale candidate branch must be deleted and then re-pushed at the new commit. Calls: $(@($stale.Calls) -join ' | ')"
}
if (-not $stale.Created2) { throw 'A pushed candidate must be recorded as created by this run, so undo can remove it.' }

$same = & $candidateProbe $candidatePath "newsha`trefs/heads/candidate/v0.0.1" $false
if (@($same.Calls | Where-Object { $_ -match 'refs/heads/candidate' }).Count -ne 0) {
    throw "A candidate branch already at the release commit must be reused, not pushed again. Calls: $(@($same.Calls) -join ' | ')"
}
if ($same.Created2) { throw 'A reused candidate must not be recorded as created by this run.' }

$dry = & $candidateProbe $candidatePath "oldsha`trefs/heads/candidate/v0.0.1" $true
if ($dry.Created -or @($dry.Calls | Where-Object { $_ -match '^gh|pr create' }).Count -ne 0) {
    throw 'A dry run must not create a candidate PR.'
}

# 4. Undo: a failed run rewinds its own local commit and restores only the files that were clean.
$undoFunctions = @('Invoke-ReleaseUndo', 'Undo-ReleaseLocalCommit') | ForEach-Object { Get-FunctionText -Ast $releaseAst -Name $_ }
$undoProbe = {
    param($Repo, [string[]]$FunctionTexts, [bool]$DryRunMode, [bool]$CandidateCreated, [bool]$HeadMoved)
    $ErrorActionPreference = 'Stop'
    $script:removedCandidates = @()
    function Ok($m) {}
    function Warn($m) {}
    function Remove-OwnReleaseCandidate([string]$Tag) { $script:removedCandidates += $Tag }
    $DryRun = $DryRunMode
    $root = $Repo
    $tag = 'v9.0.0'
    $script:releaseUndoDone = $false
    $script:releaseTagCreated = $false
    $script:releaseCandidateCreatedThisRun = $CandidateCreated
    $script:releaseLocalCommitCreated = $true
    $script:releasePreRunDirtyPaths = @('dirty.txt')
    Push-Location $Repo
    try {
        $script:releaseOriginalHead = (& git rev-parse HEAD).Trim()
        foreach ($text in $FunctionTexts) { . ([scriptblock]::Create($text)) }
        Set-Content -LiteralPath (Join-Path $Repo 'clean.txt') -Value 'release edit' -NoNewline
        Set-Content -LiteralPath (Join-Path $Repo 'docs.md') -Value 'new file' -NoNewline
        & git add -A
        & git -c user.email=t@t -c user.name=t commit -q -m 'release: v9.0.0'
        $script:releaseCommitSha = (& git rev-parse HEAD).Trim()
        if ($HeadMoved) {
            Set-Content -LiteralPath (Join-Path $Repo 'later.txt') -Value 'later' -NoNewline
            & git add -A
            & git -c user.email=t@t -c user.name=t commit -q -m 'later'
        }
        Invoke-ReleaseUndo -Reason 'test failure'
        [pscustomobject]@{
            Head = (& git rev-parse HEAD).Trim()
            Original = $script:releaseOriginalHead
            CommitSha = $script:releaseCommitSha
            Clean = (Get-Content -LiteralPath (Join-Path $Repo 'clean.txt') -Raw)
            Dirty = (Get-Content -LiteralPath (Join-Path $Repo 'dirty.txt') -Raw)
            DocsExists = (Test-Path -LiteralPath (Join-Path $Repo 'docs.md'))
            Removed = @($script:removedCandidates)
            Done = $script:releaseUndoDone
        }
    } finally {
        Pop-Location
    }
}
function New-UndoRepo {
    $repo = Join-Path $env:TEMP ('gxmcp-undo-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    & git -C $repo init -q -b main
    Set-Content -LiteralPath (Join-Path $repo 'clean.txt') -Value 'original' -NoNewline
    Set-Content -LiteralPath (Join-Path $repo 'dirty.txt') -Value 'committed' -NoNewline
    & git -C $repo add -A
    & git -C $repo -c user.email=t@t -c user.name=t commit -q -m base
    # The operator's pending edit, present before the run started.
    Set-Content -LiteralPath (Join-Path $repo 'dirty.txt') -Value 'pending edit' -NoNewline
    return $repo
}
$repo = New-UndoRepo
try {
    $undone = & $undoProbe $repo $undoFunctions $false $true $false
    if ($undone.Head -cne $undone.Original) { throw "Undo must rewind HEAD to the commit the run started from ($($undone.Original)), got $($undone.Head)." }
    if ($undone.Clean -cne 'original') { throw 'Undo must restore a file the release commit changed that was clean before the run.' }
    if ($undone.DocsExists) { throw 'Undo must remove a file the release commit added.' }
    if ($undone.Dirty -cne 'pending edit') { throw 'Undo must keep the operator pending edit exactly as it was.' }
    if (@($undone.Removed) -notcontains 'v9.0.0') { throw 'Undo must remove the candidate branch and PR this run pushed.' }
    if (-not $undone.Done) { throw 'Undo must mark itself done so a failure cannot undo twice.' }
} finally {
    Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue
}

$repo = New-UndoRepo
try {
    $moved = & $undoProbe $repo $undoFunctions $false $false $true
    if ($moved.Head -ceq $moved.Original) { throw 'Undo must not rewind a HEAD that moved after the release commit.' }
    if ($moved.Clean -cne 'release edit') { throw 'Undo must leave the files alone when HEAD moved.' }
} finally {
    Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue
}

$repo = New-UndoRepo
try {
    $dryUndo = & $undoProbe $repo $undoFunctions $true $true $false
    if ($dryUndo.Head -ceq $dryUndo.Original) { throw 'A dry run must not undo anything.' }
} finally {
    Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. The summary line carries version, tag, release, assets, npm and the failing step.
$summaryText = Get-FunctionText -Ast $releaseAst -Name 'Write-ReleaseFinalSummary'
$summaryProbe = {
    param([string]$FunctionText, [bool]$DryRunMode, [int]$ExitCode, [string]$FailedStep)
    $ErrorActionPreference = 'Stop'
    $Version = '9.0.0'
    $tag = 'v9.0.0'
    $DryRun = $DryRunMode
    $releaseAssetNames = @('publish.zip', 'publish.zip.sha256')
    $statusState = [ordered]@{ releaseUrl = 'https://example.test/release'; npmVersion = $null }
    . ([scriptblock]::Create($FunctionText))
    $output = & { Write-ReleaseFinalSummary -ExitCode $ExitCode -FailedStep $FailedStep } 6>&1 | Out-String
    $output
}
$successLine = (& $summaryProbe $summaryText $false 0 '') -split "`n" | Where-Object { $_ -match 'RELEASE SUMMARY:' } | Select-Object -First 1
if ($successLine -notmatch 'RELEASE SUMMARY: version=9\.0\.0 tag=v9\.0\.0 release=https://example\.test/release assets=publish\.zip,publish\.zip\.sha256 npm=unverified rc=0\s*$') {
    throw "Success summary line is malformed: $successLine"
}
$failureLine = (& $summaryProbe $summaryText $false 1 'Tagging v9.0.0') -split "`n" | Where-Object { $_ -match 'RELEASE SUMMARY:' } | Select-Object -First 1
if ($failureLine -notmatch "rc=1 failed-step='Tagging v9\.0\.0'\s*$") { throw "Failure summary line must name the failed step: $failureLine" }

Write-Host 'release-lifecycle: ordering, candidate replacement, undo and summary guards passed' -ForegroundColor Green
