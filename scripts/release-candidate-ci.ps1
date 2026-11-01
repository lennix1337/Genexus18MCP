# Candidate CI workflow and remote branch/PR lifecycle management.
#
# Manages automated candidate branch (candidate/vX.Y.Z) and candidate PR lifecycle
# to trigger and verify required GitHub Actions status checks on the exact release
# source commit, overlapping cloud CI execution with local preflight/builds.

$ErrorActionPreference = 'Stop'

function Test-ReleaseRequiredChecksQuiet {
    param([Parameter(Mandatory = $true)][string]$Commit)

    return Get-GxMcpReleaseRequiredChecksReport -Commit $Commit
}

function Ensure-ReleaseCandidateCi {
    param(
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)][string]$Tag,
        [switch]$DryRun
    )

    $candidateBranch = "candidate/$Tag"
    if ($null -ne $statusState -and $statusState.Contains('candidate')) {
        $statusState.candidate.branch = $candidateBranch
    }

    if ($DryRun) {
        Warn "[DRY-RUN] would push $Commit to refs/heads/$candidateBranch and ensure a PR exists to trigger CI."
        return
    }

    Step "Pushing candidate branch $candidateBranch for CI"
    Invoke-Cmd 'git' @('push', 'origin', "$($Commit):refs/heads/$candidateBranch")
    Ok "Pushed candidate branch $candidateBranch ($Commit)."

    $prListRaw = @(gh pr list --head $candidateBranch --state all --json number,state,url 2>$null)
    $prs = if ($LASTEXITCODE -eq 0 -and $prListRaw.Count -gt 0) {
        ($prListRaw -join [Environment]::NewLine) | ConvertFrom-Json
    } else { @() }

    $openPr = @($prs | Where-Object { [string]$_.state -ieq 'OPEN' } | Select-Object -First 1)
    if ($openPr.Count -gt 0) {
        $prNum = [int]$openPr[0].number
        $prUrl = [string]$openPr[0].url
        if ($null -ne $statusState -and $statusState.Contains('candidate')) {
            $statusState.candidate.prNumber = $prNum
            $statusState.candidate.prUrl = $prUrl
        }
        Ok "Candidate PR already open: #$prNum ($prUrl)."
    } elseif ($prs.Count -gt 0) {
        $closedPr = $prs[0]
        $prNum = [int]$closedPr.number
        $prUrl = [string]$closedPr.url
        Invoke-Cmd 'gh' @('pr', 'reopen', [string]$prNum)
        if ($null -ne $statusState -and $statusState.Contains('candidate')) {
            $statusState.candidate.prNumber = $prNum
            $statusState.candidate.prUrl = $prUrl
        }
        Ok "Reopened candidate PR #$prNum for CI."
    } else {
        $prOut = @(gh pr create --head $candidateBranch --base main --title "release candidate: $Tag" --body "Automated release candidate verification for $Tag ($Commit)." 2>$null)
        if ($LASTEXITCODE -ne 0 -or $prOut.Count -eq 0) {
            Fail "Could not create candidate PR for $candidateBranch to trigger required CI."
        }
        $createdUrl = ($prOut -join ' ').Trim()
        $prNumMatch = [regex]::Match($createdUrl, '/pull/(\d+)')
        $prNum = if ($prNumMatch.Success) { [int]$prNumMatch.Groups[1].Value } else { $null }
        if ($null -ne $statusState -and $statusState.Contains('candidate')) {
            $statusState.candidate.prNumber = $prNum
            $statusState.candidate.prUrl = $createdUrl
        }
        Ok "Created candidate PR for CI: $createdUrl"
    }
}

function Wait-ReleaseRequiredChecksGreen {
    param(
        [Parameter(Mandatory = $true)][string]$Commit,
        [int]$TimeoutSeconds = 900,
        [int]$PollSeconds = 10
    )

    Step "Waiting for candidate CI to be green on $Commit"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastError = $null

    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $report = Get-GxMcpReleaseRequiredChecksReport -Commit $Commit
        if ($report.Valid) {
            $sw.Stop()
            Ok "Candidate CI is green on $Commit ($([int]$sw.Elapsed.TotalSeconds)s elapsed)."
            return
        }

        $lastError = $report.Error
        if ($report.CheckRuns) {
            $failedRun = @($report.CheckRuns | Where-Object {
                [string]$_.head_sha -ceq $Commit -and
                [string]$_.status -eq 'completed' -and
                [string]$_.conclusion -in @('failure', 'timed_out', 'action_required', 'cancelled')
            } | Select-Object -First 1)
            if ($failedRun.Count -gt 0) {
                Fail "Candidate CI failed terminally on $Commit (check '$($failedRun[0].name)' concluded '$($failedRun[0].conclusion)'). Inspect CI logs, fix, and rerun."
            }
        }

        Write-Host "    Candidate CI pending ($($report.Error)); waiting $($PollSeconds)s... ($([int]$sw.Elapsed.TotalSeconds)/${TimeoutSeconds}s)" -ForegroundColor DarkGray
        Start-Sleep -Seconds $PollSeconds
    }

    Fail "Timed out waiting for candidate CI on $Commit after $TimeoutSeconds seconds ($lastError)."
}

function Cleanup-ReleaseCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$Tag
    )

    $candidateBranch = "candidate/$Tag"
    Step "Cleaning up candidate branch and PR ($candidateBranch)"

    $prNumber = if ($null -ne $statusState -and $statusState.Contains('candidate')) { $statusState.candidate.prNumber } else { $null }
    $closedPr = $false

    if ($prNumber) {
        try {
            Invoke-Cmd 'gh' @('pr', 'close', [string]$prNumber, '--delete-branch')
            $closedPr = $true
            Ok "Candidate PR #$prNumber closed and remote branch $candidateBranch deleted."
        } catch {
            Warn "Could not close candidate PR #${prNumber}: $($_.Exception.Message)"
        }
    }

    if (-not $closedPr) {
        try {
            $prsRaw = @(gh pr list --head $candidateBranch --state open --json number 2>$null)
            if ($LASTEXITCODE -eq 0 -and $prsRaw.Count -gt 0) {
                $openPrs = ($prsRaw -join [Environment]::NewLine) | ConvertFrom-Json
                foreach ($p in $openPrs) {
                    Invoke-Cmd 'gh' @('pr', 'close', [string]$p.number, '--delete-branch')
                    Ok "Closed candidate PR #$($p.number) and deleted $candidateBranch."
                    $closedPr = $true
                }
            }
        } catch {
            Warn "Could not clean up candidate PR via search: $($_.Exception.Message)"
        }
    }

    if (-not $closedPr) {
        try {
            $remoteCheck = @(& git ls-remote --heads origin "refs/heads/$candidateBranch" 2>$null)
            if ($LASTEXITCODE -eq 0 -and $remoteCheck.Count -gt 0) {
                Invoke-Cmd 'git' @('push', 'origin', ":refs/heads/$candidateBranch")
                Ok "Deleted remote candidate branch $candidateBranch."
            }
        } catch {
            Warn "Could not delete remote candidate branch ${candidateBranch}: $($_.Exception.Message)"
        }
    }

    try {
        $localExists = (& git branch --list $candidateBranch 2>$null).Trim()
        if ($localExists) {
            Invoke-Cmd 'git' @('branch', '-D', $candidateBranch)
            Ok "Deleted local branch $candidateBranch."
        }
    } catch { }
}
