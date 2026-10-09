[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Start', 'Wait', 'Run')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Name,
    [string]$Command,
    [string]$WorkingDirectory = (Get-Location).Path,
    [string]$GateDirectory = (Join-Path $env:TEMP 'gxmcp-gates'),
    [ValidateRange(5, 280)]
    [int]$TimeoutSeconds = 240
)

# Long gates (solution tests, preflight) outlive one tool call. Start returns at once; Wait polls
# the status record, so a caller checks once per call instead of sleeping in fixed steps.
#   Start: run -Command detached; the log and status record live in -GateDirectory.
#   Wait:  block up to -TimeoutSeconds; prints one summary.
# Wait exit codes: 0 passed, 1 failed, 2 still running, 3 died without a result.

$ErrorActionPreference = 'Stop'
$WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
$GateDirectory = [IO.Path]::GetFullPath($GateDirectory)
New-Item -ItemType Directory -Path $GateDirectory -Force | Out-Null
$logPath = Join-Path $GateDirectory "$Name.log"
$statusPath = Join-Path $GateDirectory "$Name.json"

function Write-Status {
    param([hashtable]$Value)
    $temp = "$statusPath.tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $statusPath -Force
}

function Read-Status {
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { return $null }
    return Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
}

switch ($Action) {
    'Start' {
        if ([string]::IsNullOrWhiteSpace($Command)) { throw '-Command is required for Start.' }
        $previous = Read-Status
        if ($previous -and $previous.status -eq 'running' -and $previous.pid -ne 0 -and (Get-Process -Id $previous.pid -ErrorAction SilentlyContinue)) {
            throw "Gate '$Name' is already running (pid $($previous.pid))."
        }
        Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
        # pid 0 = launched, child not yet reporting; Wait must not mistake that for a dead gate.
        Write-Status @{ status = 'running'; pid = 0; startedUtc = [DateTime]::UtcNow.ToString('o'); command = $Command }
        # The command travels in the environment: Start-Process joins arguments without quoting them.
        $env:GXMCP_GATE_COMMAND = $Command
        try {
            $process = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
                '-NoProfile', '-File', "`"$PSCommandPath`"", 'Run', '-Name', $Name,
                '-WorkingDirectory', "`"$WorkingDirectory`"", '-GateDirectory', "`"$GateDirectory`"")
            Write-Status @{ status = 'running'; pid = $process.Id; startedUtc = [DateTime]::UtcNow.ToString('o'); command = $Command }
        } catch {
            $failedAt = [DateTime]::UtcNow.ToString('o')
            Write-Status @{ status = 'failed'; exitCode = 1; pid = 0; startedUtc = $failedAt; endedUtc = $failedAt; command = $Command }
            throw
        } finally { Remove-Item Env:GXMCP_GATE_COMMAND -ErrorAction SilentlyContinue }
        Write-Output "started gate '$Name' (pid $($process.Id)); log: $logPath"
    }
    'Run' {
        $Command = $env:GXMCP_GATE_COMMAND
        Set-Location -LiteralPath $WorkingDirectory
        $started = [DateTime]::UtcNow
        Write-Status @{ status = 'running'; pid = $PID; startedUtc = $started.ToString('o'); command = $Command }
        $exitCode = 1
        try {
            $global:LASTEXITCODE = 0
            & ([scriptblock]::Create($Command)) *>&1 | Out-File -LiteralPath $logPath -Encoding utf8
            $exitCode = if ($global:LASTEXITCODE) { [int]$global:LASTEXITCODE } else { 0 }
        } catch {
            $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding utf8
        }
        Write-Status @{
            status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
            exitCode = $exitCode
            pid = $PID
            startedUtc = $started.ToString('o')
            endedUtc = [DateTime]::UtcNow.ToString('o')
            command = $Command
        }
    }
    'Wait' {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ($true) {
            $state = Read-Status
            if (-not $state) { Write-Output "no gate named '$Name' in $GateDirectory"; exit 3 }
            if ($state.status -ne 'running') { break }
            if ($state.pid -ne 0 -and -not (Get-Process -Id $state.pid -ErrorAction SilentlyContinue)) {
                Write-Output "gate '$Name' died without a result (pid $($state.pid) is gone)"
                exit 3
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                Write-Output "gate '$Name' still running (pid $($state.pid)); call Wait again"
                exit 2
            }
            Start-Sleep -Seconds 2
        }
        $seconds = [Math]::Round(([DateTime]::Parse($state.endedUtc) - [DateTime]::Parse($state.startedUtc)).TotalSeconds)
        $tail = @(Get-Content -LiteralPath $logPath -Tail 400 -ErrorAction SilentlyContinue)
        $summary = @($tail | Where-Object { $_ -match 'Passed!|Failed!|error\b|FAIL' } | Select-Object -Last 3)
        if ($summary.Count -eq 0) { $summary = @($tail | Select-Object -Last 3) }
        Write-Output "gate '$Name' $($state.status) exit=$($state.exitCode) in ${seconds}s; log: $logPath"
        $summary | ForEach-Object { Write-Output "  $_" }
        exit $(if ($state.status -eq 'passed') { 0 } else { 1 })
    }
}
