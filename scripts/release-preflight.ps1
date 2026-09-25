[CmdletBinding()]
param(
    [string]$GxPath,
    [string[]]$LiveMajors,
    [string[]]$LiveGxPathMap,
    [string]$Version,
    [switch]$SkipLive,
    [switch]$RequireLive,
    [switch]$RequireBuildAll,
    [string]$LiveKbPath,
    [string]$LiveFixtureManifest,
    [string]$SummaryPath,
    [string]$ResumeSummaryPath,
    [ValidateRange(30, 7200)][int]$PhaseTimeoutSeconds = 1200,
    [switch]$SkipWarningBaseline,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\gx-version-catalog.ps1')
. (Join-Path $root 'scripts\release-contract.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $root

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
}
if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) { $env:GX_PATH } else { Get-GxPrimaryInstallPath -Catalog $gxCatalog }
}
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-release-preflight-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$SummaryPath = [IO.Path]::GetFullPath($SummaryPath)

$liveMode = if (@($LiveMajors | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or @($LiveGxPathMap | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) { 'matrix' } else { 'single' }
$sourceCommit = $null
try {
    $resolvedCommit = @(& git -C $root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $resolvedCommit.Count -gt 0) {
        $sourceCommit = ([string]$resolvedCommit[0]).Trim()
    }
} catch { }

$artifactFingerprint = if ($DryRun) { $null } else {
    Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:($Version -notmatch '^3\.')
}
$preflightInputs = Resolve-GxMcpReleasePreflightInputs `
    -Root $root `
    -Catalog $gxCatalog `
    -GxPath $GxPath `
    -Version $Version `
    -SourceCommit $sourceCommit `
    -LiveMajors $LiveMajors `
    -LiveGxPathMap $LiveGxPathMap `
    -LiveKbPath $LiveKbPath `
    -LiveFixtureManifest $LiveFixtureManifest `
    -RequireLive:$RequireLive `
    -RequireBuildAll:$RequireBuildAll `
    -SkipLive:$SkipLive `
    -SkipWarningBaseline:$SkipWarningBaseline `
    -ArtifactFingerprint $artifactFingerprint
$GxPath = [string]$preflightInputs.gxPath
$LiveKbPath = [string]$preflightInputs.liveKbPath
$LiveFixtureManifest = [string]$preflightInputs.liveFixtureManifest
$LiveMajors = @($preflightInputs.liveMajors)
$LiveGxPathMap = @($preflightInputs.liveGxPathMap)
$liveKbSource = [string]$preflightInputs.liveKbSource
$liveFixtureSource = [string]$preflightInputs.liveFixtureSource
$liveMode = [string]$preflightInputs.liveMode
$RequireBuildAll = [bool]$preflightInputs.requireBuildAll
$artifactFingerprint = $preflightInputs.artifactFingerprint
$resumeEnabled = $false
$resumePhases = @{}
$resumeReason = $null
$priorSummary = $null
if (-not $DryRun -and -not [string]::IsNullOrWhiteSpace($ResumeSummaryPath)) {
    $ResumeSummaryPath = [IO.Path]::GetFullPath($ResumeSummaryPath)
    if (Test-Path -LiteralPath $ResumeSummaryPath -PathType Leaf) {
        try {
            $priorSummary = Get-Content -LiteralPath $ResumeSummaryPath -Raw | ConvertFrom-Json
            $sameInputs = Test-GxMcpReleasePreflightCompatibility -Summary $priorSummary -Expected $preflightInputs
            if ($sameInputs) {
                foreach ($priorPhase in @($priorSummary.phases)) {
                    if ($priorPhase.status -eq 'passed') {
                        $resumePhases[[string]$priorPhase.name] = $priorPhase
                    }
                }
                $resumeEnabled = $resumePhases.Count -gt 0
                if ($resumeEnabled) { $resumeReason = "reused passed phases from $ResumeSummaryPath" }
            }
        } catch {
            Write-Verbose "Could not load a compatible preflight summary for resume: $($_.Exception.Message)"
        }
    }
}

$summary = [ordered]@{
    schemaVersion = 'gxmcp-release-preflight/1'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    endedAtUtc = $null
    root = $preflightInputs.root
    gxPath = $preflightInputs.gxPath
    liveKbPath = $preflightInputs.liveKbPath
    liveKbSource = $preflightInputs.liveKbSource
    liveFixtureManifest = $preflightInputs.liveFixtureManifest
    liveFixtureSource = $preflightInputs.liveFixtureSource
    liveMode = $preflightInputs.liveMode
    liveMajors = @($preflightInputs.liveMajors)
    liveGxPathMap = @($preflightInputs.liveGxPathMap)
    requireLive = $preflightInputs.requireLive
    requireBuildAll = $preflightInputs.requireBuildAll
    skipLive = $preflightInputs.skipLive
    skipWarningBaseline = $preflightInputs.skipWarningBaseline
    version = $preflightInputs.version
    sourceCommit = $preflightInputs.sourceCommit
    artifactFingerprint = $preflightInputs.artifactFingerprint
    phaseTimeoutSeconds = $PhaseTimeoutSeconds
    executionMode = if ($DryRun) { 'dry-run' } elseif ($resumeEnabled) { 'parallel-resume' } else { 'parallel' }
    processSmokeMode = 'serial-after-parallel'
    processSmokeTestCount = $null
    processSmokeResultsPath = $null
    processSmokeBinaryFingerprint = $null
    resumedFrom = if ($resumeEnabled) { $ResumeSummaryPath } else { $null }
    wallDurationSeconds = 0
    phaseDurationTotalSeconds = 0
    dryRun = [bool]$DryRun
    phases = New-Object System.Collections.Generic.List[object]
    status = 'running'
}

function Write-PreflightSummary {
    $now = [DateTime]::UtcNow
    $summary.endedAtUtc = $now.ToString('o')
    $started = [DateTimeOffset]::Parse([string]$summary.startedAtUtc).UtcDateTime
    $summary.wallDurationSeconds = [Math]::Round(($now - $started).TotalSeconds, 3)
    $phaseTotal = 0.0
    foreach ($phase in ($summary.phases | ForEach-Object { $_ })) {
        if ($null -ne $phase.durationSeconds) { $phaseTotal += [double]$phase.durationSeconds }
    }
    $summary.phaseDurationTotalSeconds = [Math]::Round($phaseTotal, 3)
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

function Test-PreflightPhaseStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [switch]$AllowFailure,
        [switch]$AllowUnavailable
    )

    if ($AllowFailure -and $Status -eq 'failed') { return $true }
    if ($AllowUnavailable -and $Status -eq 'unavailable') { return $true }
    return $Status -in @('passed', 'skipped', 'dry-run')
}

function Get-PreflightTrxTestCount {
    param([Parameter(Mandatory = $true)][string]$ResultsDirectory)

    return Get-GxMcpPreflightTrxTestCount -ResultsDirectory $ResultsDirectory
}

function Get-ReusablePreflightPhase {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][string]$Command)

    if ($Name -eq 'solution process smoke tests') { return $null }
    if (-not $resumeEnabled -or -not $resumePhases.ContainsKey($Name)) { return $null }
    $prior = $resumePhases[$Name]
    if ([string]$prior.status -ne 'passed' -or [string]::IsNullOrWhiteSpace([string]$prior.command) -or
        [string]$prior.command -cne $Command) {
        return $null
    }
    [ordered]@{
        name = $Name
        command = if ([string]::IsNullOrWhiteSpace([string]$prior.command)) { $Command } else { [string]$prior.command }
        status = 'passed'
        exitCode = 0
        durationSeconds = 0
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        endedAtUtc = [DateTime]::UtcNow.ToString('o')
        reason = if ($resumeReason) { $resumeReason } else { 'reused passed phase from a matching summary' }
        reused = $true
    }
}

function New-PreflightPhaseState {
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
    $reused = Get-ReusablePreflightPhase -Name $Name -Command $command
    if ($null -ne $reused) {
        return [pscustomobject]@{
            Phase = $reused; Process = $null; StdoutTask = $null; StderrTask = $null
            Stdout = ''; Stderr = ''; Watch = $null
            AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
        }
    }

    $phase = [ordered]@{
        name = $Name
        command = $command
        status = 'running'
        exitCode = $null
        durationSeconds = 0
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        endedAtUtc = $null
        reason = $null
    }
    if ($SkipReason) {
        $phase.status = 'skipped'
        $phase.exitCode = 0
        $phase.reason = $SkipReason
        $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
        return [pscustomobject]@{
            Phase = $phase; Process = $null; StdoutTask = $null; StderrTask = $null
            Stdout = ''; Stderr = ''; Watch = $null
            AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
        }
    }
    if ($DryRun) {
        $phase.status = 'dry-run'
        $phase.exitCode = 0
        $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
        return [pscustomobject]@{
            Phase = $phase; Process = $null; StdoutTask = $null; StderrTask = $null
            Stdout = ''; Stderr = ''; Watch = $null
            AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
        }
    }

    $resolvedExecutable = $Executable
    if (-not [IO.Path]::IsPathRooted($resolvedExecutable)) {
        $commandInfo = Get-Command $resolvedExecutable -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $commandInfo) {
            $phase.status = 'failed'
            $phase.exitCode = 1
            $phase.reason = "Executable '$Executable' was not found on PATH."
            $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
            return [pscustomobject]@{
                Phase = $phase; Process = $null; StdoutTask = $null; StderrTask = $null
                Stdout = ''; Stderr = ''; Watch = $null
                AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
            }
        }
        $resolvedExecutable = $commandInfo.Source
    }

    Write-Host "`n>>> Preflight: $Name" -ForegroundColor Cyan
    Write-Host "    $ $command" -ForegroundColor DarkGray
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::new()
    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $resolvedExecutable
        $startInfo.WorkingDirectory = $WorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Could not start '$Executable'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
    } catch {
        $watch.Stop()
        $phase.status = 'failed'
        $phase.exitCode = 1
        $phase.reason = $_.Exception.Message
        $phase.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
        $process.Dispose()
        return [pscustomobject]@{
            Phase = $phase; Process = $null; StdoutTask = $null; StderrTask = $null
            Stdout = ''; Stderr = ''; Watch = $null
            AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
        }
    }
    [pscustomobject]@{
        Phase = $phase; Process = $process; StdoutTask = $stdoutTask; StderrTask = $stderrTask
        Stdout = ''; Stderr = ''; Watch = $watch
        AllowFailure = [bool]$AllowFailure; AllowUnavailable = [bool]$AllowUnavailable
    }
}

function Start-PreflightPhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $root,
        [switch]$AllowFailure,
        [switch]$AllowUnavailable,
        [string]$SkipReason
    )
    $state = New-PreflightPhaseState -Name $Name -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory `
        -AllowFailure:$AllowFailure -AllowUnavailable:$AllowUnavailable -SkipReason $SkipReason
    Add-PreflightPhaseResult -Phase $state.Phase | Out-Null
    $state
}

function Complete-PreflightPhase {
    param([Parameter(Mandatory = $true)][object]$State)

    $phase = $State.Phase
    if ($null -ne $State.Process) {
        try {
            if (-not $State.Process.WaitForExit($PhaseTimeoutSeconds * 1000)) {
                try { $State.Process.Kill($true) } catch { }
                try { $State.Process.WaitForExit(5000) } catch { }
                $phase.status = 'timeout'
                $phase.exitCode = 124
                $phase.reason = "Phase exceeded ${PhaseTimeoutSeconds}s and was terminated."
            } else {
                $phase.exitCode = $State.Process.ExitCode
                $phase.status = if ($phase.exitCode -eq 0) { 'passed' } elseif ($phase.exitCode -eq 2 -and $State.AllowUnavailable) { 'unavailable' } else { 'failed' }
                if ($phase.status -eq 'failed') { $phase.reason = "Command exited with code $($phase.exitCode)." }
            }
        } catch {
            $phase.status = 'failed'
            $phase.exitCode = 1
            $phase.reason = $_.Exception.Message
        } finally {
            try { $State.Stdout = $State.StdoutTask.GetAwaiter().GetResult() } catch { $State.Stdout = '' }
            try { $State.Stderr = $State.StderrTask.GetAwaiter().GetResult() } catch { $State.Stderr = '' }
            if ($null -ne $State.Watch) {
                $State.Watch.Stop()
                $phase.durationSeconds = [Math]::Round($State.Watch.Elapsed.TotalSeconds, 3)
            }
            $phase.endedAtUtc = [DateTime]::UtcNow.ToString('o')
            try { $State.Process.Dispose() } catch { }
        }
        foreach ($line in @($State.Stdout -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ } | Select-Object -Last 80)) {
            Write-Host "    $line"
        }
        foreach ($line in @($State.Stderr -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ } | Select-Object -Last 40)) {
            Write-Host "    stderr: $line" -ForegroundColor DarkYellow
        }
        # Runs only after the process already exited non-zero, so a host probe
        # never decides whether a phase runs. It can only add a cause: status and
        # exitCode are left exactly as the process reported them, because the
        # aggregate accepts 'unavailable' as approved and downgrading here would
        # certify a release that ran no Electron test.
        if ($phase.status -eq 'failed') {
            $hostBlocker = Get-GxMcpPreflightHostBlocker -Name ([string]$phase.name) -Stdout $State.Stdout -Stderr $State.Stderr -RepositoryRoot $root
            if ($null -ne $hostBlocker) {
                $phase.details = $hostBlocker
                $phase.reason = "$($phase.reason) $($hostBlocker.cause) Remediation: $($hostBlocker.remediation)"
                Write-Host "    host blocker: $($hostBlocker.code) (mutex confirmed: $($hostBlocker.mutexConfirmed))" -ForegroundColor Yellow
            }
        }
    }
    $phase
}

function Add-PreflightPhaseResult {
    param([Parameter(Mandatory = $true)][object]$Phase)
    $existing = @($summary.phases | Where-Object { [string]$_.name -eq [string]$Phase.name }) | Select-Object -First 1
    if ($null -eq $existing) {
        [void]$summary.phases.Add($Phase)
    } elseif (-not [object]::ReferenceEquals($existing, $Phase)) {
        $index = $summary.phases.IndexOf($existing)
        if ($index -ge 0) { $summary.phases[$index] = $Phase }
    }
    Write-PreflightSummary
}

function Invoke-PreflightParallel {
    param([Parameter(Mandatory = $true)][object[]]$Definitions)

    $states = foreach ($definition in @($Definitions)) {
        $allowFailure = if ($definition.Contains('AllowFailure')) { [bool]$definition.AllowFailure } else { $false }
        $allowUnavailable = if ($definition.Contains('AllowUnavailable')) { [bool]$definition.AllowUnavailable } else { $false }
        $skipReason = if ($definition.Contains('SkipReason')) { [string]$definition.SkipReason } else { $null }
        Start-PreflightPhase -Name ([string]$definition.Name) -Executable ([string]$definition.Executable) `
            -Arguments @($definition.Arguments) -WorkingDirectory ([string]$definition.WorkingDirectory) `
            -AllowFailure:$allowFailure -AllowUnavailable:$allowUnavailable -SkipReason $skipReason
    }
    $results = foreach ($state in @($states)) {
        $phase = Complete-PreflightPhase -State $state
        Add-PreflightPhaseResult -Phase $phase | Out-Null
        $phase
    }
    $allowedFailureNames = @{}
    $allowedUnavailableNames = @{}
    foreach ($definition in @($Definitions)) {
        if ($definition.Contains('AllowFailure') -and [bool]$definition.AllowFailure) {
            $allowedFailureNames[[string]$definition.Name] = $true
        }
        if ($definition.Contains('AllowUnavailable') -and [bool]$definition.AllowUnavailable) {
            $allowedUnavailableNames[[string]$definition.Name] = $true
        }
    }
    $blocking = @($results | Where-Object {
        if ($DryRun) { return $false }
        -not (Test-PreflightPhaseStatus -Status ([string]$_.status) `
            -AllowFailure:($allowedFailureNames.ContainsKey([string]$_.name)) `
            -AllowUnavailable:($allowedUnavailableNames.ContainsKey([string]$_.name)))
    })
    if ($blocking.Count -gt 0) {
        $summary.status = 'failed'
        Write-PreflightSummary
        $firstFailure = $blocking | Select-Object -First 1
        Write-Error "Preflight failed in '$($firstFailure.name)': $($firstFailure.reason)"
        exit 1
    }
    @($results)
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
    $state = Start-PreflightPhase -Name $Name -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory `
        -AllowFailure:$AllowFailure -AllowUnavailable:$AllowUnavailable -SkipReason $SkipReason
    $phase = Complete-PreflightPhase -State $state
    Add-PreflightPhaseResult -Phase $phase
    $phaseBlocked = -not (Test-PreflightPhaseStatus -Status ([string]$phase.status) `
        -AllowFailure:$AllowFailure -AllowUnavailable:$AllowUnavailable)
    if ($phaseBlocked) {
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error "Preflight failed in '$Name': $($phase.reason)"
        exit 1
    }
    $phase
}

Write-PreflightSummary
Write-Host "Release preflight summary: $SummaryPath" -ForegroundColor DarkGray
$env:GX_PATH = $GxPath

Invoke-PreflightPhase -Name 'release metadata parity' -Executable 'python' -Arguments @((Join-Path $root 'scripts\verify-release-metadata.py'), '--root', $root, '--version', $Version) | Out-Null
Invoke-PreflightPhase -Name 'tool contract validation' -Executable 'python' -Arguments @((Join-Path $root 'scripts\validate-tool-contracts.py')) | Out-Null
Invoke-PreflightPhase -Name 'operation contract inventory' -Executable 'python' -Arguments @((Join-Path $root 'scripts\generate-operation-contract-inventory.py'), '--check') | Out-Null
Invoke-PreflightPhase -Name 'v3 plan readiness' -Executable 'python' -Arguments @((Join-Path $root 'scripts\validate-v3-plan.py'), '--require-ready') | Out-Null
Invoke-PreflightPhase -Name 'warning baseline documentation parity' -Executable 'pwsh' -Arguments @(
    '-NoProfile', '-File', (Join-Path $root 'scripts\check-build-warning-baseline.ps1'),
    '-ValidateOnly',
    '-BaselineFile', (Join-Path $root 'docs\build_warning_baseline.json'),
    '-DocumentationFile', (Join-Path $root 'docs\build_warning_baseline.md')
) | Out-Null
$liveRequested = -not $SkipLive
$missingLiveReasons = New-Object System.Collections.Generic.List[string]
if ([string]::IsNullOrWhiteSpace($LiveKbPath)) {
    [void]$missingLiveReasons.Add('No explicit, environment, or local C:/KBs live KB was found.')
} elseif (-not (Test-Path -LiteralPath $LiveKbPath -PathType Container)) {
    [void]$missingLiveReasons.Add("Live KB directory was not found: $LiveKbPath")
}
if (-not [string]::IsNullOrWhiteSpace($LiveFixtureManifest) -and -not (Test-Path -LiteralPath $LiveFixtureManifest -PathType Leaf)) {
    [void]$missingLiveReasons.Add("Fixture manifest was not found: $LiveFixtureManifest")
}
$liveMissing = $missingLiveReasons.Count -gt 0
if (-not $liveRequested) {
    if ($RequireLive -or $RequireBuildAll) {
        Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason 'live gate disabled while it is required' | Out-Null
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error 'Live validation was explicitly skipped while a live gate was required.'
        exit 1
    }
    $liveDefinition = [ordered]@{
        Name = 'live KB gate'; Executable = 'pwsh'; Arguments = @(); WorkingDirectory = $root
        SkipReason = 'disabled by -SkipLive'
    }
} elseif ($liveMissing) {
    $reason = $missingLiveReasons -join ' '
    if ($RequireLive -or $RequireBuildAll) {
        Invoke-PreflightPhase -Name 'live KB gate' -Executable 'pwsh' -Arguments @() -SkipReason $reason | Out-Null
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error "Live validation is required but unavailable: $reason"
        exit 1
    }
    $liveDefinition = [ordered]@{
        Name = 'live KB gate'; Executable = 'pwsh'; Arguments = @(); WorkingDirectory = $root
        SkipReason = $reason
    }
} else {
    $matrixMode = @($LiveMajors | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or @($LiveGxPathMap | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
    $liveArgs = if ($matrixMode) {
        @(
            '-NoProfile', '-File', (Join-Path $root 'scripts/test-live-matrix.ps1'),
            '-KbPath', $LiveKbPath,
            '-SkipBuild',
            '-SummaryPath', "$SummaryPath.matrix.json"
        )
    } else {
        @(
            '-NoProfile', '-File', (Join-Path $root 'scripts/test-live.ps1'),
            '-KbPath', $LiveKbPath,
            '-GxPath', $GxPath,
            '-SkipBuild'
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($LiveFixtureManifest)) {
        $liveArgs += @('-FixtureManifest', $LiveFixtureManifest)
    }
    if ($matrixMode -and @($LiveMajors | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        $liveArgs += '-Majors'
        $liveArgs += @($LiveMajors)
    }
    if ($matrixMode) {
        $primaryMajor = [string]$gxCatalog.primaryMajor
        $hasPrimaryOverride = @($LiveGxPathMap | Where-Object { $_ -match "^\s*$([regex]::Escape($primaryMajor))\s*=" }).Count -gt 0
        if (-not $hasPrimaryOverride) {
            $LiveGxPathMap = @($LiveGxPathMap) + ("{0}={1}" -f $primaryMajor, $GxPath)
        }
    }
    if ($matrixMode -and @($LiveGxPathMap | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        $liveArgs += '-GxPathMap'
        $liveArgs += @($LiveGxPathMap)
    }
    if ($RequireBuildAll) { $liveArgs += '-RequireBuildAll' }
    $allowUnavailable = -not ($RequireLive -or $RequireBuildAll)
    $liveDefinition = [ordered]@{
        Name = 'live KB gate'; Executable = 'pwsh'; Arguments = @($liveArgs); WorkingDirectory = $root
        AllowUnavailable = $allowUnavailable
    }
}

# The warning baseline performs a full Rebuild and must not overlap with the
# solution test's MSBuild output. Process/socket smoke tests also run in a
# dedicated lane after the parallel wave: launching Gateways while the CLI,
# Nexus, and script suites are active made the release signal nondeterministic.
$parallelDefinitions = @(
    [ordered]@{ Name = 'Python script tests'; Executable = 'python'; Arguments = @('-m', 'unittest', 'discover', '-s', (Join-Path $root 'scripts\tests'), '-v'); WorkingDirectory = $root },
    [ordered]@{ Name = 'PowerShell script tests'; Executable = 'pwsh'; Arguments = @('-NoProfile', '-File', (Join-Path $root 'scripts/tests/run-release-script-tests.ps1')); WorkingDirectory = $root },
    [ordered]@{ Name = 'CLI tests'; Executable = 'npm'; Arguments = @('test'); WorkingDirectory = $root },
    [ordered]@{ Name = 'CLI lint'; Executable = 'npm'; Arguments = @('run', 'lint'); WorkingDirectory = $root },
    [ordered]@{ Name = 'Nexus IDE checks'; Executable = 'npm'; Arguments = @('--prefix', (Join-Path $root 'src\nexus-ide'), 'run', 'check'); WorkingDirectory = $root },
    [ordered]@{ Name = 'solution build and tests'; Executable = 'dotnet'; Arguments = @('test', (Join-Path $root 'Genexus18MCP.sln'), '-c', 'Release', '--filter', 'Category!=ProcessSmoke', '-v:minimal'); WorkingDirectory = $root }
)

Invoke-PreflightParallel -Definitions $parallelDefinitions | Out-Null
$processSmokeResultsPath = Join-Path $env:TEMP ('gxmcp-release-process-smoke-' + [guid]::NewGuid().ToString('N'))
$processSmokeGateway = Join-Path $root 'src\GxMcp.Gateway\bin\Release\net10.0-windows\GxMcp.Gateway.exe'
if (-not $DryRun -and -not (Test-Path -LiteralPath $processSmokeGateway -PathType Leaf)) {
    $summary.status = 'failed'
    Write-PreflightSummary
    Write-Error "Process smoke lane did not find the Release Gateway built by the preceding phase: $processSmokeGateway"
    exit 1
}
$processSmokeDefinition = [ordered]@{
    Name = 'solution process smoke tests'
    Executable = 'dotnet'
    Arguments = @(
        'test', (Join-Path $root 'Genexus18MCP.sln'), '-c', 'Release', '--no-build', '--no-restore',
        '--filter', 'Category=ProcessSmoke', '--logger', 'trx;LogFilePrefix=process-smoke',
        '--results-directory', $processSmokeResultsPath, '-v:minimal'
    )
    WorkingDirectory = $root
}
$previousProcessSmokeGateway = $env:GXMCP_LIVE_GATEWAY_EXE
try {
    if (-not $DryRun) { $env:GXMCP_LIVE_GATEWAY_EXE = $processSmokeGateway }
    Invoke-PreflightPhase @processSmokeDefinition | Out-Null
} finally {
    if ($null -eq $previousProcessSmokeGateway) {
        Remove-Item Env:GXMCP_LIVE_GATEWAY_EXE -ErrorAction SilentlyContinue
    } else {
        $env:GXMCP_LIVE_GATEWAY_EXE = $previousProcessSmokeGateway
    }
}
if (-not $DryRun) {
    $summary.processSmokeResultsPath = $processSmokeResultsPath
    $summary.processSmokeTestCount = Get-PreflightTrxTestCount -ResultsDirectory $processSmokeResultsPath
    $summary.processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $root
    if ($null -eq $summary.processSmokeBinaryFingerprint) {
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error 'Process smoke binaries could not be bound to the current publish artifacts.'
        exit 1
    }
    if ($summary.processSmokeTestCount -le 0) {
        $summary.status = 'failed'
        Write-PreflightSummary
        Write-Error 'Process smoke filter selected zero tests; refusing to certify the preflight.'
        exit 1
    }
    Write-PreflightSummary
}
if ($SkipWarningBaseline) {
    Invoke-PreflightPhase -Name 'Release warning baseline' -Executable 'pwsh' -Arguments @() -SkipReason 'disabled by -SkipWarningBaseline' | Out-Null
} else {
    Invoke-PreflightPhase -Name 'Release warning baseline' -Executable 'pwsh' -Arguments @(
        '-NoProfile', '-File', (Join-Path $root 'scripts\check-build-warning-baseline.ps1'),
        '-BaselineFile', (Join-Path $root 'docs\build_warning_baseline.json'), '-GxPath', $GxPath
    ) | Out-Null
}
Invoke-PreflightPhase @liveDefinition | Out-Null

$allowedTerminalStatuses = @('passed', 'skipped', 'unavailable')
if ($DryRun) { $allowedTerminalStatuses += 'dry-run' }
$terminalPhaseFailure = @($summary.phases | Where-Object {
    [string]$_.status -notin $allowedTerminalStatuses
}).Count -gt 0
$summary.status = if (-not $terminalPhaseFailure) { 'passed' } else { 'failed' }
Write-PreflightSummary
if ($summary.status -eq 'passed') {
    Write-Host "`nPreflight passed. Summary: $SummaryPath" -ForegroundColor Green
    exit 0
}
Write-Error "Preflight failed. Summary: $SummaryPath"
exit 1
