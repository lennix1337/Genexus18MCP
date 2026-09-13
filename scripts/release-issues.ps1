[CmdletBinding()]
param(
    [ValidateSet('MarkFixedPendingRelease', 'CloseAfterRelease')]
    [string]$Action,
    [int[]]$Issue,
    [string]$ReleaseUrl,
    [switch]$DryRun,
    [Parameter(DontShow = $true)]
    [switch]$DefineOnly
)

$ErrorActionPreference = 'Stop'
$script:ReleaseIssueLabel = 'fixed-pending-release'

function Get-ReleaseIssueLabelNames {
    param([object[]]$Labels)

    return @($Labels | ForEach-Object {
        if ($_ -is [string]) {
            [string]$_
        } elseif ($null -ne $_ -and $null -ne $_.PSObject.Properties['name']) {
            [string]$_.name
        }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
}

function Assert-ReleaseIssueAction {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('MarkFixedPendingRelease', 'CloseAfterRelease')]
        [string]$Action,
        [Parameter(Mandatory = $true)][int]$IssueNumber,
        [Parameter(Mandatory = $true)][object]$IssueData
    )

    $state = ([string]$IssueData.state).Trim().ToLowerInvariant()
    if ($state -ne 'open') {
        throw "Issue #$IssueNumber must be open for $Action; current state is '$state'."
    }

    if ($Action -eq 'CloseAfterRelease') {
        $labels = @(Get-ReleaseIssueLabelNames -Labels @($IssueData.labels))
        if ($labels -notcontains $script:ReleaseIssueLabel) {
            throw "Issue #$IssueNumber lacks '$script:ReleaseIssueLabel'; mark it for the next release instead of closing it directly."
        }
    }
}

function Get-ReleaseIssueData {
    param([Parameter(Mandatory = $true)][int]$IssueNumber)

    $raw = @(gh issue view ([string]$IssueNumber) --json state,labels 2>$null)
    if ($LASTEXITCODE -ne 0 -or $raw.Count -eq 0) {
        throw "Could not read issue #$IssueNumber."
    }
    return (($raw -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Invoke-ReleaseIssueCommand {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $display = "gh $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[DRY-RUN] would run: $display" -ForegroundColor DarkGray
        return
    }
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed (exit $LASTEXITCODE): $display"
    }
}

function Invoke-ReleaseIssueWorkflow {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('MarkFixedPendingRelease', 'CloseAfterRelease')]
        [string]$Action,
        [Parameter(Mandatory = $true)][int[]]$Issue,
        [string]$ReleaseUrl
    )

    $issues = @($Issue | Select-Object -Unique)
    if ($issues.Count -eq 0) { throw 'At least one issue number is required.' }
    if ($Action -eq 'CloseAfterRelease' -and [string]::IsNullOrWhiteSpace($ReleaseUrl)) {
        throw 'CloseAfterRelease requires the published release URL.'
    }

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($issueNumber in $issues) {
        if ($issueNumber -le 0) { throw "Issue number must be positive: $issueNumber" }
        $record = Get-ReleaseIssueData -IssueNumber $issueNumber
        Assert-ReleaseIssueAction -Action $Action -IssueNumber $issueNumber -IssueData $record
        [void]$records.Add([pscustomobject]@{ number = $issueNumber; data = $record })
    }

    foreach ($record in $records) {
        $issueNumber = [int]$record.number
        if ($Action -eq 'MarkFixedPendingRelease') {
            Invoke-ReleaseIssueCommand -Arguments @('issue', 'edit', [string]$issueNumber, '--add-label', $script:ReleaseIssueLabel)
            if (-not $DryRun) {
                $verified = Get-ReleaseIssueData -IssueNumber $issueNumber
                Assert-ReleaseIssueAction -Action 'MarkFixedPendingRelease' -IssueNumber $issueNumber -IssueData $verified
                $labels = @(Get-ReleaseIssueLabelNames -Labels @($verified.labels))
                if ($labels -notcontains $script:ReleaseIssueLabel) {
                    throw "Issue #$issueNumber was not verified with '$script:ReleaseIssueLabel'."
                }
            }
            Write-Host "Issue #$issueNumber marked $script:ReleaseIssueLabel and remains open." -ForegroundColor Green
            continue
        }

        Invoke-ReleaseIssueCommand -Arguments @('issue', 'comment', [string]$issueNumber, '--body', "Released in $ReleaseUrl")
        Invoke-ReleaseIssueCommand -Arguments @('issue', 'close', [string]$issueNumber, '--reason', 'completed')
        if (-not $DryRun) {
            $verified = Get-ReleaseIssueData -IssueNumber $issueNumber
            $verifiedState = ([string]$verified.state).Trim().ToLowerInvariant()
            if ($verifiedState -ne 'closed') {
                throw "Issue #$issueNumber was not verified as closed after the release comment."
            }
        }
        Write-Host "Issue #$issueNumber closed with release link." -ForegroundColor Green
    }
}

if (-not $DefineOnly) {
    if ([string]::IsNullOrWhiteSpace($Action)) {
        throw 'Specify -Action MarkFixedPendingRelease or -Action CloseAfterRelease.'
    }
    Invoke-ReleaseIssueWorkflow -Action $Action -Issue $Issue -ReleaseUrl $ReleaseUrl
}
