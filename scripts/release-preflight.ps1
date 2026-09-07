[CmdletBinding()]
param(
    [string]$GxPath,
    [string]$Version,
    [switch]$SkipLive,
    [switch]$RequireLive,
    [switch]$RequireBuildAll,
    [string]$LiveKbPath,
    [string]$LiveFixtureManifest,
    [string]$SummaryPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
}
if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) { $env:GX_PATH } else { 'C:\Program Files (x86)\GeneXus\GeneXus18' }
}
if ([string]::IsNullOrWhiteSpace($LiveKbPath)) { $LiveKbPath = $env:GXMCP_TEST_KB }
if ([string]::IsNullOrWhiteSpace($LiveFixtureManifest)) { $LiveFixtureManifest = $env:GXMCP_TEST_FIXTURE }
if (-not $RequireBuildAll -and $env:GXMCP_REQUIRE_LIVE_BUILD_ALL -eq '1') { $RequireBuildAll = $true }
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-release-preflight-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$SummaryPath = [IO.Path]::GetFullPath($SummaryPath)

$summary = [ordered]@{
    schemaVersion = 'gxmcp-release-preflight/1'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    endedAtUtc = $null
    root = $root
    gxPath = $GxPath
    version = $Version
    dryRun = [bool]$DryRun
    phases = New-Object System.Collections.Generic.List[object]
    status = 'running'
}

function Write-PreflightSummary {
    $summary.endedAtUtc = [DateTime]::UtcNow.ToString('o')
    $parent = Split-Path -Parent $SummaryPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $tmp = "$SummaryPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($tmp, (($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $SummaryPath -Force
}

function Format-PreflightCommand {
    param([string]$Executable, [string[]]$Arguments)
    return ((@($Executable) + @($Arguments)) -join ' ').Trim()
}

function Invoke-PreflightPhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $root,
        [switch]$AllowFailure,
        [switch]$AllowUnavailable,
        [string]$SkipReason
    )
    $command = Format-PreflightCommand $Executable $Arguments
    $phase = [ordered]@{
        name = $Name
        command = $command
        status = $null
        exitCode = $null
        durationSeconds = 0
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        endedAtUtc = $null
        reason = $null
    }
    if ($SkipReason) {
        $phase.status = 'skipped'
        $phase.reason = $SkipReason
        $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
        [void]$summary.phases.Add($phase)
        Write-Host "[SKIP] $Name — $SkipReason" -ForegroundColor Yellow
        Write-PreflightSummary
        return $phase
    }
    Write-Host "`n>>> Preflight: $Name" -ForegroundColor Cyan
    Write-Host "    $ $command" -ForegroundColor DarkGray
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($DryRun) {
        $phase.status = 'dry-run'
        $phase.exitCode = 0
    } else {
        try {
            Push-Location $WorkingDirectory
            try {
                $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
            } finally {
                Pop-Location
            }
            foreach ($line in ($output | Select-Object -Last 80)) { Write-Host "    $line" }
            $phase.exitCode = $LASTEXITCODE
            $phase.status = if ($phase.exitCode -eq 0) { 'passed' } elseif ($phase.exitCode -eq 2 -and $AllowUnavailable) { 'unavailable' } else { 'failed' }
            if ($phase.exitCode -ne 0) { $phase.reason = "Command exited with code $($phase.exitCode)." }
        } catch {
            $phase.exitCode = 1
            $phase.status = 'failed'
            $phase.reason = $_.Exception.Message
        }
    }
    $watch.Stop()
    $phase.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
    [void]$summary.phases.Add($phase)
    Write-PreflightSummary
    if ($phase.status -eq 'failed' -and -not $AllowFailure) {
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error "Preflight failed in '$Name': $($phase.reason)"
        exit 1
    }
    $phase
}

Write-Host "Release preflight summary: $SummaryPath" -ForegroundColor DarkGray
$env:GX_PATH = $GxPath

Invoke-PreflightPhase -Name 'release metadata parity' -Executable 'python' -Arguments @((Join-Path $root 'scripts\verify-release-metadata.py'), '--root', $root, '--version', $Version) | Out-Null
Invoke-PreflightPhase -Name 'solution build and tests' -Executable 'dotnet' -Arguments @('test', (Join-Path $root 'Genexus18MCP.sln'), '-c', 'Release', '-v:minimal') | Out-Null
Invoke-PreflightPhase -Name 'CLI tests' -Executable 'npm' -Arguments @('test') | Out-Null
Invoke-PreflightPhase -Name 'CLI lint' -Executable 'npm' -Arguments @('run', 'lint') | Out-Null
Invoke-PreflightPhase -Name 'Nexus IDE checks' -Executable 'npm' -Arguments @('--prefix', (Join-Path $root 'src\nexus-ide'), 'run', 'check') | Out-Null
Invoke-PreflightPhase -Name 'tool contract validation' -Executable 'python' -Arguments @((Join-Path $root 'scripts\validate-tool-contracts.py')) | Out-Null
Invoke-PreflightPhase -Name 'operation contract inventory' -Executable 'python' -Arguments @((Join-Path $root 'scripts\generate-operation-contract-inventory.py'), '--check') | Out-Null
Invoke-PreflightPhase -Name 'v3 plan readiness' -Executable 'python' -Arguments @((Join-Path $root 'scripts\validate-v3-plan.py'), '--require-ready') | Out-Null
Invoke-PreflightPhase -Name 'Python script tests' -Executable 'python' -Arguments @('-m', 'unittest', 'discover', '-s', (Join-Path $root 'scripts\tests'), '-v') | Out-Null
Invoke-PreflightPhase -Name 'PowerShell script tests' -Executable 'pwsh' -Arguments @('-NoProfile', '-File', (Join-Path $root 'scripts\tests\run-release-script-tests.ps1')) | Out-Null
Invoke-PreflightPhase -Name 'Release warning baseline' -Executable 'pwsh' -Arguments @('-NoProfile', '-File', (Join-Path $root 'scripts\check-build-warning-baseline.ps1'), '-BaselineFile', (Join-Path $root 'docs\build_warning_baseline.json'), '-GxPath', $GxPath) | Out-Null

$liveRequested = -not $SkipLive
$liveMissing = [string]::IsNullOrWhiteSpace($LiveKbPath) -or [string]::IsNullOrWhiteSpace($LiveFixtureManifest)
if (-not $liveRequested) {
    if ($RequireLive -or $RequireBuildAll) {
        Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason 'live gate disabled while it is required' | Out-Null
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error 'Live validation was explicitly skipped while a live gate was required.'
        exit 1
    }
    Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason 'disabled by -SkipLive' | Out-Null
} elseif ($liveMissing) {
    $reason = 'GXMCP_TEST_KB and GXMCP_TEST_FIXTURE are required for live validation.'
    if ($RequireLive -or $RequireBuildAll) {
        Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason $reason | Out-Null
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error "Live validation is required but unavailable: $reason"
        exit 1
    }
    Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason $reason | Out-Null
} else {
    $liveArgs = @(
        '-NoProfile', '-File', (Join-Path $root 'scripts\test-live.ps1'),
        '-KbPath', $LiveKbPath,
        '-FixtureManifest', $LiveFixtureManifest,
        '-GxPath', $GxPath,
        '-SkipBuild'
    )
    if ($RequireBuildAll) { $liveArgs += '-RequireBuildAll' }
    $allowUnavailable = -not ($RequireLive -or $RequireBuildAll)
    Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments $liveArgs -AllowUnavailable:$allowUnavailable | Out-Null
}

$summary.status = if (@($summary.phases | Where-Object status -eq 'failed').Count -eq 0) { 'passed' } else { 'failed' }
Write-PreflightSummary
if ($summary.status -eq 'passed') {
    Write-Host "`nPreflight passed. Summary: $SummaryPath" -ForegroundColor Green
    exit 0
}
Write-Error "Preflight failed. Summary: $SummaryPath"
exit 1
