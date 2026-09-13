[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KbPath,
    [string]$FixtureManifest,
    [string[]]$Majors,
    [string[]]$GxPathMap,
    [switch]$SkipBuild,
    [switch]$GatewayOnly,
    [switch]$RequireBuildAll,
    [switch]$RunBenchmark,
    [ValidateRange(1, 100)][int]$Iterations = 12,
    [ValidateRange(5, 7200)][int]$RpcTimeoutSeconds = 240,
    [string]$TestFilter = 'Category=LiveE2E&FullyQualifiedName!~TeamDevelopment',
    [string]$SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\gx-version-catalog.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $root
$catalogEntries = @($gxCatalog.supportedMajors)
$knownMajors = @($catalogEntries | ForEach-Object { [string]$_.major })
$catalogSource = if ($gxCatalog.PSObject.Properties['source']) { [string]$gxCatalog.source } else { 'config/gx-versions.json' }

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-live-matrix-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$SummaryPath = [IO.Path]::GetFullPath($SummaryPath)

function Parse-GxPathMap {
    param(
        [string[]]$Mappings,
        [string[]]$Known
    )

    $map = @{}
    foreach ($mapping in @($Mappings)) {
        if ([string]::IsNullOrWhiteSpace($mapping)) { continue }
        foreach ($piece in ([string]$mapping -split ';')) {
            $text = $piece.Trim()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $separator = $text.IndexOf('=')
            if ($separator -le 0 -or $separator -ge ($text.Length - 1)) {
                throw "Invalid -GxPathMap entry '$text'. Use major=absolute-path."
            }
            $major = $text.Substring(0, $separator).Trim()
            $path = $text.Substring($separator + 1).Trim()
            if ($major -notmatch '^\d+$' -or $Known -notcontains $major) {
                throw "-GxPathMap references unsupported or invalid major '$major'. Supported majors: $($Known -join ', ')."
            }
            if ($map.ContainsKey($major)) {
                throw "-GxPathMap contains duplicate entries for major '$major'."
            }
            if (-not [IO.Path]::IsPathRooted($path)) {
                throw "-GxPathMap path for major '$major' must be absolute: '$path'."
            }
            $map[$major] = [IO.Path]::GetFullPath($path)
        }
    }
    return ,$map
}

function Get-MatrixSelection {
    param(
        [Parameter(Mandatory = $true)][object]$Catalog,
        [string[]]$RequestedMajors,
        [Parameter(Mandatory = $true)][hashtable]$PathMap
    )

    $known = @($Catalog.supportedMajors | ForEach-Object { [string]$_.major })
    $requested = if (@($RequestedMajors).Count -eq 0) {
        $known
    } else {
        @($RequestedMajors | ForEach-Object {
                [string]$_ -split '[,;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            })
    }
    $seen = @{}
    $selection = foreach ($major in $requested) {
        if ($major -notmatch '^\d+$' -or $known -notcontains $major) {
            throw "Unsupported live-test major '$major'. Supported majors: $($known -join ', ')."
        }
        if ($seen.ContainsKey($major)) { throw "Live-test major '$major' was requested more than once." }
        $seen[$major] = $true
        $entry = @($Catalog.supportedMajors | Where-Object { [string]$_.major -eq $major } | Select-Object -First 1)[0]
        $pathValue = if ($PathMap.ContainsKey($major)) { [string]$PathMap[$major] } else { [string]$entry.defaultInstallPath }
        if ([string]::IsNullOrWhiteSpace($pathValue)) {
            throw "Catalog entry for major '$major' has no defaultInstallPath and no -GxPathMap override."
        }
        [pscustomobject]@{
            major = $major
            displayName = [string]$entry.displayName
            path = [IO.Path]::GetFullPath($pathValue)
        }
    }
    return @($selection)
}

function Write-MatrixSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][object]$Build,
        [Parameter(Mandatory = $true)][object[]]$Results,
        [string[]]$SelectedMajors,
        [string]$Reason
    )

    $selectedMajorValues = @()
    if (@($SelectedMajors).Count -gt 0) {
        $selectedMajorValues = @($SelectedMajors)
    } else {
        $selectedMajorValues = @($Results | ForEach-Object { [string]$_.major })
    }
    $reasonValue = $null
    if (-not [string]::IsNullOrWhiteSpace($Reason)) { $reasonValue = $Reason }

    $summary = [ordered]@{
        schemaVersion = 'gxmcp-live-matrix/1'
        startedAtUtc = $script:MatrixStartedAtUtc
        endedAtUtc = [DateTime]::UtcNow.ToString('o')
        root = $root
        kbPath = $KbPath
        fixtureManifest = $FixtureManifest
        testFilter = $TestFilter
        catalogSource = $catalogSource
        supportedMajors = @($knownMajors)
        selectedMajors = $selectedMajorValues
        artifactBuild = $Build
        results = @($Results)
        allPassed = $Status -eq 'passed'
        status = $Status
        reason = $reasonValue
    }
    $parent = Split-Path -Parent $SummaryPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $tmp = "$SummaryPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($tmp, (($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $SummaryPath -Force
}

$script:MatrixStartedAtUtc = [DateTime]::UtcNow.ToString('o')
$pathMap = Parse-GxPathMap -Mappings $GxPathMap -Known $knownMajors
$selection = Get-MatrixSelection -Catalog $gxCatalog -RequestedMajors $Majors -PathMap $pathMap
$primarySelection = Get-MatrixSelection -Catalog $gxCatalog -RequestedMajors @([string]$gxCatalog.primaryMajor) -PathMap $pathMap
$pathsToValidate = @($selection)
if (-not $SkipBuild) {
    foreach ($primary in $primarySelection) {
        if (-not @($pathsToValidate | Where-Object { $_.major -eq $primary.major })) { $pathsToValidate += $primary }
    }
}

$results = New-Object System.Collections.Generic.List[object]
$unavailable = @($pathsToValidate | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.path 'GeneXus.exe') -PathType Leaf) })
if ($unavailable.Count -gt 0) {
    foreach ($candidate in $selection) {
        $missing = @($unavailable | Where-Object { $_.major -eq $candidate.major } | Select-Object -First 1)
        if ($missing) {
            [void]$results.Add([pscustomobject]@{ major = $candidate.major; path = $candidate.path; status = 'unavailable'; exitCode = 2; reason = "GeneXus.exe was not found under '$($candidate.path)'." })
        } else {
            [void]$results.Add([pscustomobject]@{ major = $candidate.major; path = $candidate.path; status = 'not_run'; exitCode = $null; reason = 'A required SDK path was unavailable before the matrix started.' })
        }
    }
    Write-MatrixSummary -Status 'unavailable' -Build ([ordered]@{ status = 'not_run'; exitCode = $null; major = [string]$gxCatalog.primaryMajor }) -Results $results.ToArray() -SelectedMajors @($selection | ForEach-Object { [string]$_.major }) -Reason 'One or more selected SDK installations were unavailable.'
    Write-Error "live=unavailable; one or more selected GeneXus SDK paths are missing. Summary: $SummaryPath" -ErrorAction Continue
    exit 2
}

$originalGxPath = [Environment]::GetEnvironmentVariable('GX_PATH', 'Process')
$buildStatus = [ordered]@{
    status = if ($SkipBuild) { 'skipped' } else { 'pending' }
    major = [string]$gxCatalog.primaryMajor
    path = [string]$primarySelection[0].path
    exitCode = $null
}
$benchmarkDirectory = Split-Path -Parent $SummaryPath
try {
    if (-not $SkipBuild) {
        $env:GX_PATH = [string]$primarySelection[0].path
        Write-Host "Matrix artifact build: GeneXus $($gxCatalog.primaryMajor) at $env:GX_PATH" -ForegroundColor Cyan
        & pwsh -NoProfile -File (Join-Path $root 'build.ps1')
        $buildExit = $LASTEXITCODE
        $buildStatus.exitCode = $buildExit
        if ($buildExit -ne 0) {
            $buildStatus.status = 'failed'
            Write-MatrixSummary -Status 'failed' -Build $buildStatus -Results $results.ToArray() -SelectedMajors @($selection | ForEach-Object { [string]$_.major }) -Reason "Primary artifact build failed with exit code $buildExit."
            Write-Error "live=failed; primary artifact build failed. Summary: $SummaryPath"
            exit 1
        }
        $buildStatus.status = 'passed'
    }

    foreach ($candidate in $selection) {
        $env:GX_PATH = [string]$candidate.path
        $testArgs = @(
            '-NoProfile', '-File', (Join-Path $root 'scripts\test-live.ps1'),
            '-KbPath', $KbPath,
            '-GxPath', $candidate.path,
            '-SkipBuild',
            '-RpcTimeoutSeconds', $RpcTimeoutSeconds,
            '-TestFilter', $TestFilter
        )
        if (-not [string]::IsNullOrWhiteSpace($FixtureManifest)) {
            $testArgs += @('-FixtureManifest', $FixtureManifest)
        }
        if ($GatewayOnly) { $testArgs += '-GatewayOnly' }
        if ($RequireBuildAll) { $testArgs += '-RequireBuildAll' }
        if ($RunBenchmark) {
            if (-not (Test-Path -LiteralPath $benchmarkDirectory)) { New-Item -ItemType Directory -Path $benchmarkDirectory -Force | Out-Null }
            $testArgs += @('-RunBenchmark', '-Iterations', $Iterations, '-BenchmarkOut', (Join-Path $benchmarkDirectory ("gxmcp-live-$($candidate.major).json")))
        }

        Write-Host ("[{0}] Starting major {1}; child output streams below." -f (Get-Date -Format 'HH:mm:ss'), $candidate.major) -ForegroundColor DarkCyan
        $childOutput = New-Object System.Collections.Generic.List[string]
        & pwsh @testArgs 2>&1 | ForEach-Object {
            $line = $_.ToString()
            [void]$childOutput.Add($line)
            Write-Host ("    [{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $line)
        }
        $exitCode = $LASTEXITCODE
        $unavailableSignal = @($childOutput | Where-Object { $_ -match 'live=unavailable' }).Count -gt 0
        $status = if ($exitCode -eq 0) { 'passed' } elseif ($exitCode -eq 2 -or $unavailableSignal) { 'unavailable' } else { 'failed' }
        $reason = if ($exitCode -eq 0) { $null } elseif ($unavailableSignal) { ($childOutput | Where-Object { $_ -match 'live=unavailable' } | Select-Object -Last 1) } else { "scripts/test-live.ps1 exited with code $exitCode." }
        [void]$results.Add([pscustomobject]@{
                major = $candidate.major
                path = $candidate.path
                status = $status
                exitCode = $exitCode
                reason = $reason
            })
    }

    $failedCount = @($results | Where-Object { $_.status -eq 'failed' }).Count
    $unavailableCount = @($results | Where-Object { $_.status -eq 'unavailable' }).Count
    $overallStatus = if ($failedCount -gt 0) { 'failed' } elseif ($unavailableCount -gt 0) { 'unavailable' } else { 'passed' }
    Write-MatrixSummary -Status $overallStatus -Build $buildStatus -Results $results.ToArray() -SelectedMajors @($selection | ForEach-Object { [string]$_.major })
    if ($overallStatus -eq 'passed') {
        Write-Host "live-matrix=pass; majors=$(($selection | ForEach-Object { $_.major }) -join ','); summary=$SummaryPath" -ForegroundColor Green
        exit 0
    }
    if ($overallStatus -eq 'unavailable') {
        Write-Error "live=unavailable; one or more SDK/fixture environments were unavailable. Summary: $SummaryPath" -ErrorAction Continue
        exit 2
    }
    Write-Error "live=failed; one or more SDK live checks failed. Summary: $SummaryPath"
    exit 1
}
finally {
    if ($null -eq $originalGxPath) { Remove-Item Env:GX_PATH -ErrorAction SilentlyContinue }
    else { $env:GX_PATH = $originalGxPath }
}
