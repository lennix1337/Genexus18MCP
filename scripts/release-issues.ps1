[CmdletBinding()]
param(
    [ValidateSet('MarkFixedPendingRelease', 'CloseAfterRelease')]
    [string]$Action,
    # Strings, not [int[]]: `pwsh -File ... -Issue 184,185` passes '184,185' as
    # one string, which an int conversion reads as 184185.
    [string[]]$Issue,
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

function ConvertTo-ReleaseIssueNumbers {
    param([string[]]$Value)

    $numbers = New-Object System.Collections.Generic.List[int]
    foreach ($token in @($Value | ForEach-Object { [string]$_ -split '[,\s]+' })) {
        $text = $token.Trim().TrimStart('#')
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $parsed = 0
        if (-not [int]::TryParse($text, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -le 0) {
            throw "Invalid issue number: '$token'."
        }
        if (-not $numbers.Contains($parsed)) { $numbers.Add($parsed) }
    }
    if ($numbers.Count -eq 0) { throw 'At least one issue number is required.' }
    return $numbers.ToArray()
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

function Test-ReleaseIssuePublicationComment {
    param(
        [Parameter(Mandatory = $true)][object]$IssueData,
        [Parameter(Mandatory = $true)][string]$ReleaseUrl
    )

    $expected = "Released in $ReleaseUrl"
    return @($IssueData.comments | Where-Object { [string]$_.body -eq $expected }).Count -gt 0
}

function Get-GhJson {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$GhPath = 'gh'
    )

    $raw = @(& $GhPath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    $text = $raw -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "gh $($Arguments -join ' ') returned empty output."
    }
    try {
        $value = $text | ConvertFrom-Json
    } catch {
        throw "gh $($Arguments -join ' ') returned invalid JSON."
    }
    if ($null -eq $value) {
        throw "gh $($Arguments -join ' ') returned a null JSON value."
    }
    return $value
}

function Get-ReleaseIssueData {
    param(
        [Parameter(Mandatory = $true)][int]$IssueNumber,
        [string]$GhPath = 'gh'
    )

    $issue = Get-GhJson -GhPath $GhPath -Arguments @(
        'issue', 'view', ([string]$IssueNumber),
        '--json', 'number,title,body,state,labels,comments'
    )
    if ($null -eq $issue.number -or [int]$issue.number -ne $IssueNumber) {
        throw "GitHub returned issue number '$($issue.number)' while reading #$IssueNumber."
    }
    if ([string]::IsNullOrWhiteSpace([string]$issue.title)) {
        throw "GitHub returned no title while reading issue #$IssueNumber."
    }
    if ([string]::IsNullOrWhiteSpace([string]$issue.state)) {
        throw "GitHub returned no state while reading issue #$IssueNumber."
    }
    return $issue
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
        if (-not $DryRun) {
            $commented = Get-ReleaseIssueData -IssueNumber $issueNumber
            if (-not (Test-ReleaseIssuePublicationComment -IssueData $commented -ReleaseUrl $ReleaseUrl)) {
                throw "Issue #$issueNumber did not expose the exact release publication comment after posting."
            }
        }
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
    Invoke-ReleaseIssueWorkflow -Action $Action -Issue (ConvertTo-ReleaseIssueNumbers -Value $Issue) -ReleaseUrl $ReleaseUrl
}
