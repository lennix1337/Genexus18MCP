# Candidate CI workflow and remote branch/PR lifecycle management.
#
# Manages automated candidate branch (candidate/vX.Y.Z) and candidate PR lifecycle
# to trigger and verify required GitHub Actions status checks on the exact release
# source commit. The candidate is created after the local build and preflight pass;
# cloud CI then overlaps with the remaining local steps (stamps, manifest, publish.zip).

$ErrorActionPreference = 'Stop'

function Test-ReleaseRequiredChecksQuiet {
    param([Parameter(Mandatory = $true)][string]$Commit)

    return Get-GxMcpReleaseRequiredChecksReport -Commit $Commit
}

# Commit SHA of a remote branch, or $null when it does not exist. Throws on a failed read: the
# callers decide whether that is a failure (Ensure) or only undo noise (Remove-OwnReleaseCandidate).
function Get-ReleaseCandidateRemoteSha {
    param([Parameter(Mandatory = $true)][string]$Branch)

    $lines = @(& git ls-remote --heads origin "refs/heads/$Branch" 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not read remote branch $Branch." }
    $match = @($lines | Where-Object { ([string]$_) -match "\srefs/heads/$([regex]::Escape($Branch))$" })
    if ($match.Count -eq 0) { return $null }
    return (([string]$match[0]).Trim() -split '\s+')[0]
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

    # candidate/<tag> belongs to this release. A copy left by an earlier run points at another
    # commit: it is replaced, never pushed over (a non-fast-forward push would fail the run).
    $remoteSha = Get-ReleaseCandidateRemoteSha -Branch $candidateBranch
    if ($remoteSha -ceq $Commit) {
        if (-not $DryRun) { Ok "Candidate branch $candidateBranch already points at $Commit; reusing it." }
    } else {
        Step "Pushing candidate branch $candidateBranch for CI"
        if ($remoteSha) {
            Warn "Replacing the stale candidate branch $candidateBranch ($remoteSha) with $Commit."
            Invoke-Cmd 'git' @('push', 'origin', ":refs/heads/$candidateBranch")
        }
        Invoke-Cmd 'git' @('push', 'origin', "$($Commit):refs/heads/$candidateBranch")
        if (-not $DryRun) {
            $script:releaseCandidateCreatedThisRun = $true
            Ok "Pushed candidate branch $candidateBranch ($Commit)."
        }
    }

    $prListRaw = @(gh pr list --head $candidateBranch --state all --json number,state,url 2>$null)
    # @() keeps an empty '[]' list an array: ConvertFrom-Json enumerates it to $null,
    # and StrictMode callers then fail on .Count.
    $prs = @(if ($LASTEXITCODE -eq 0 -and $prListRaw.Count -gt 0) {
        ($prListRaw -join [Environment]::NewLine) | ConvertFrom-Json
    })

    $openPr = @($prs | Where-Object { [string]$_.state -ieq 'OPEN' } | Select-Object -First 1)
    if ($openPr.Count -gt 0) {
        $prNum = [int]$openPr[0].number
        $prUrl = [string]$openPr[0].url
        if ($null -ne $statusState -and $statusState.Contains('candidate')) {
            $statusState.candidate.prNumber = $prNum
            $statusState.candidate.prUrl = $prUrl
        }
        Ok "Candidate PR already open: #$prNum ($prUrl)."
        return
    }

    if ($prs.Count -gt 0) {
        $closedPr = $prs[0]
        $prNum = [int]$closedPr.number
        $prUrl = [string]$closedPr.url
        if ($DryRun) {
            Warn "[DRY-RUN] would reopen candidate PR #$prNum or create a new one."
            return
        }
        # A closed PR whose branch was deleted and pushed again cannot be reopened;
        # GitHub answers 'Could not open the pull request', so a new PR is created.
        gh pr reopen ([string]$prNum) 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            if ($null -ne $statusState -and $statusState.Contains('candidate')) {
                $statusState.candidate.prNumber = $prNum
                $statusState.candidate.prUrl = $prUrl
            }
            Ok "Reopened candidate PR #$prNum for CI."
            return
        }
        Warn "Could not reopen candidate PR #$prNum; creating a new one."
    }

    if ($DryRun) {
        Warn "[DRY-RUN] would create the candidate PR for $candidateBranch."
        return
    }
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

# Undo of a failed run: closes the candidate PR this run opened (its branch goes with it) and
# deletes the candidate branch if it survived. Direct commands and no Fail, because undo must not
# exit the run it is cleaning up after.
function Remove-OwnReleaseCandidate {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $candidateBranch = "candidate/$Tag"
    $prNumber = if ($null -ne $statusState -and $statusState.Contains('candidate')) { $statusState.candidate.prNumber } else { $null }
    if ($prNumber) {
        & gh pr close ([string]$prNumber) --delete-branch 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Ok "Closed candidate PR #$prNumber and deleted $candidateBranch."
            return
        }
        Warn "Could not close candidate PR #$prNumber; deleting the branch directly."
    }
    if (Get-ReleaseCandidateRemoteSha -Branch $candidateBranch) {
        & git push origin ":refs/heads/$candidateBranch" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not delete remote branch $candidateBranch." }
        Ok "Deleted remote candidate branch $candidateBranch."
    }
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
