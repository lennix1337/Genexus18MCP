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
# The Run action relies on $PSNativeCommandUseErrorActionPreference (PowerShell 7.3+) so a failed
# native command cannot be masked by a later successful one. On 7.0-7.2 the variable is silently
# ignored and a failing gate could read as passed, so refuse to start rather than misreport.
if ($Action -in @('Start', 'Run') -and $PSVersionTable.PSVersion -lt [version]'7.3') {
    throw "run-gate.ps1 requires PowerShell 7.3 or later (found $($PSVersionTable.PSVersion)); older versions cannot fail a gate on a native command's exit code reliably."
}
$WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
$GateDirectory = [IO.Path]::GetFullPath($GateDirectory)
New-Item -ItemType Directory -Path $GateDirectory -Force | Out-Null
$logPath = Join-Path $GateDirectory "$Name.log"
$statusPath = Join-Path $GateDirectory "$Name.json"

function Write-Status {
    param([hashtable]$Value)
    $temp = "$statusPath.tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    # One atomic replace: Move-Item -Force can leave a moment with no file, and a Wait that reads then
    # reported a gate that had just passed as an unreadable path.
    # Retry on sharing violations if a concurrent Wait is currently reading the destination.
    for ($attempt = 1; ; $attempt++) {
        try {
            [IO.File]::Move($temp, $statusPath, $true)
            break
        } catch {
            if ($attempt -ge 30) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Read-Status {
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { return $null }
    # The writer replaces the record while a Wait may be reading it; a read that loses that race
    # (sharing violation, or the file swapped away) is retried instead of failing the gate.
    for ($attempt = 1; ; $attempt++) {
        try { return Get-Content -LiteralPath $statusPath -Raw -ErrorAction Stop | ConvertFrom-Json }
        catch { if ($attempt -ge 20) { throw }; Start-Sleep -Milliseconds 50 }
    }
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
            # A native command that fails mid-chain must fail the gate even when a later
            # command succeeds and overwrites $LASTEXITCODE (`pwsh -File x.ps1; cmd /c exit 0`
            # would otherwise report passed). Stop-on-native-error makes the first nonzero
            # exit terminate the command and carry its code into the catch below.
            $ErrorActionPreference = 'Stop'
            $PSNativeCommandUseErrorActionPreference = $true
            & ([scriptblock]::Create($Command)) *>&1 | Out-File -LiteralPath $logPath -Encoding utf8
            $exitCode = if ($global:LASTEXITCODE) { [int]$global:LASTEXITCODE } else { 0 }
        } catch {
            $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding utf8
            $nativeExit = if ($_.Exception.PSObject.Properties['ExitCode']) { [int]$_.Exception.ExitCode } else { 0 }
            if ($nativeExit -ne 0) { $exitCode = $nativeExit } elseif ($global:LASTEXITCODE) { $exitCode = [int]$global:LASTEXITCODE }
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
                # The gate may have written its result and exited between the read above
                # and this process check; only a status still 'running' now means it died.
                for ($retry = 0; $retry -lt 10; $retry++) {
                    Start-Sleep -Milliseconds 100
                    $state = Read-Status
                    if ($state -and $state.status -ne 'running') { break }
                }
                if ($state -and $state.status -ne 'running') { break }
                Write-Output "gate '$Name' died without a result (pid $($state.pid) is gone)"
                exit 3
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                Write-Output "gate '$Name' still running (pid $($state.pid)); call Wait again"
                exit 2
            }
            Start-Sleep -Seconds 2
        }
        # Cast, do not Parse. ConvertFrom-Json already materialised the round-trip
        # 'o' timestamps as [DateTime], and handing one to [DateTime]::Parse
        # forces a detour through a culture-formatted string. On a machine whose
        # culture does not round-trip that form, the detour produced
        #   String '10/14/2026 00:18:46' was not recognized as a valid DateTime
        # and failed the gate that had actually passed. The values are never
        # strings, so the parse was doing nothing but losing information.
        $seconds = [Math]::Round((([DateTime]$state.endedUtc) - ([DateTime]$state.startedUtc)).TotalSeconds)
        $tail = @(Get-Content -LiteralPath $logPath -Tail 400 -ErrorAction SilentlyContinue)
        $summary = @($tail | Where-Object { $_ -match 'Passed!|Failed!|error\b|FAIL' } | Select-Object -Last 3)
        if ($summary.Count -eq 0) { $summary = @($tail | Select-Object -Last 3) }
        Write-Output "gate '$Name' $($state.status) exit=$($state.exitCode) in ${seconds}s; log: $logPath"
        $summary | ForEach-Object { Write-Output "  $_" }
        exit $(if ($state.status -eq 'passed') { 0 } else { 1 })
    }
}
