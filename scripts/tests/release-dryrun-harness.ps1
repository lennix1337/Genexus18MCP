# Runs release.ps1 -DryRun with git and gh stubs, so the whole release flow executes without a
# write reaching GitHub or the repository. Read-only commands run for real (local git, the
# metadata checks); write commands are recorded to -CallLog instead of executed. A dry run must
# record none. Remote reads are simulated too: any `git ls-remote` this file does not answer
# throws, so a rehearsal can never read the real origin (nor depend on local unpushed commits).
# The current branch is reported as main so the base-freshness step runs in every checkout.
# test-release-dryrun.ps1 drives this file; run it directly only to debug a run.
param(
    [Parameter(Mandatory = $true)][string]$ReleaseScript,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$StatusFile,
    [Parameter(Mandatory = $true)][string]$CallLog,
    [string]$PrListJson = '[]',
    [string]$RemoteCandidateLine = '',
    # What the simulated origin returns for refs/heads/main; default is the local HEAD (in sync).
    [string]$RemoteMainLine = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Stub functions run inside the release script's scope, where $script: means that script, so
# the harness keeps its real-git path in a global.
$global:HarnessRealGit = (Get-Command git -CommandType Application | Select-Object -First 1).Source
# Pin the working directory: outside a repository the branch would be empty and the rehearsal
# would silently take the dry-run warning path.
Set-Location -LiteralPath (Split-Path -Parent $ReleaseScript)
if (-not $RemoteMainLine) {
    $RemoteMainLine = ((& $global:HarnessRealGit rev-parse HEAD) | Select-Object -First 1).Trim() + "`trefs/heads/main"
}

function Add-StubCall {
    param([string]$Tool, [object[]]$Arguments)
    Add-Content -LiteralPath $CallLog -Value ("$Tool " + (@($Arguments) -join ' '))
}

function git {
    $subcommand = if ($args.Count -gt 0) { [string]$args[0] } else { '' }
    $gitWriteSubcommands = @('push', 'commit', 'add', 'reset', 'checkout', 'rm', 'restore', 'merge', 'rebase', 'cherry-pick', 'stash')
    $isWrite = ($subcommand -in $gitWriteSubcommands) -or
        ($subcommand -eq 'tag' -and ($args -contains '-a' -or $args -contains '-d')) -or
        ($subcommand -eq 'branch' -and ($args -contains '-D' -or $args -contains '-d'))
    if ($isWrite) {
        Add-StubCall -Tool 'git' -Arguments $args
        $global:LASTEXITCODE = 0
        return
    }
    if ($subcommand -eq 'rev-parse' -and (@($args) -join ' ') -eq 'rev-parse --abbrev-ref HEAD') {
        $global:LASTEXITCODE = 0
        return 'main'
    }
    # `git -C <dir> ls-remote ...` carries the subcommand later in the argument list.
    if ($args -contains 'ls-remote') {
        $joined = @($args) -join ' '
        if ($joined -match 'refs/heads/main') { $global:LASTEXITCODE = 0; return $RemoteMainLine }
        if ($joined -match 'refs/heads/candidate/') {
            $global:LASTEXITCODE = 0
            if ($RemoteCandidateLine) { return $RemoteCandidateLine }
            return
        }
        if ($joined -match '--tags') { $global:LASTEXITCODE = 0; return }
        throw "release dry-run harness: unsimulated remote read 'git $joined'"
    }
    & $global:HarnessRealGit @args
}

function gh {
    Add-StubCall -Tool 'gh' -Arguments $args
    $global:LASTEXITCODE = 0
    $first = if ($args.Count -gt 1) { "$($args[0]) $($args[1])" } else { [string]$args[0] }
    if ($first -eq 'pr list') { return $PrListJson }
    if ($first -eq 'issue list') { return '[]' }
    if ($first -eq 'release view') { $global:LASTEXITCODE = 1; return 'release not found' }
    return
}

& $ReleaseScript -Version $Version -DryRun -AllowDirty -SkipLabeledIssues -StatusFile $StatusFile
exit 0
