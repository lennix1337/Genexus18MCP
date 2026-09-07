[CmdletBinding()]
param(
    [string]$BaselineFile,

    [string]$GxPath,

    [switch]$UpdateBaseline,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Fail-Baseline([string]$Message) {
    Write-Error "Warning baseline failed: $Message"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($BaselineFile)) {
    $BaselineFile = Join-Path $root 'docs\build_warning_baseline.json'
}
$BaselineFile = [System.IO.Path]::GetFullPath($BaselineFile)

function Get-Baseline([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Baseline "Baseline manifest not found: $Path"
    }
    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        Fail-Baseline "Baseline manifest is not valid JSON: $Path"
    }
    if ($manifest.schemaVersion -ne 1) {
        Fail-Baseline "Unsupported baseline schemaVersion '$($manifest.schemaVersion)'; expected 1."
    }
    $entries = @($manifest.warnings)
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if ([string]::IsNullOrWhiteSpace($entry.code) -or
            [string]::IsNullOrWhiteSpace($entry.file) -or
            $null -eq $entry.line) {
            Fail-Baseline "Every warning entry must contain code, file, and line."
        }
        $key = '{0}|{1}|{2}' -f $entry.code, $entry.file, $entry.line
        if (-not $seen.Add($key)) {
            Fail-Baseline "Duplicate warning entry: $key"
        }
    }
    if ($null -ne $manifest.warningCount -and [int]$manifest.warningCount -ne $entries.Count) {
        Fail-Baseline "warningCount=$($manifest.warningCount) does not match the $($entries.Count) warning entries."
    }
    return $manifest
}

$manifest = $null
if ((Test-Path -LiteralPath $BaselineFile -PathType Leaf) -or -not $UpdateBaseline) {
    $manifest = Get-Baseline $BaselineFile
}
if ($ValidateOnly) {
    Write-Host "Warning baseline manifest valid: $(@($manifest.warnings).Count) distinct locations." -ForegroundColor Green
    exit 0
}

if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        $env:GX_PATH
    } else {
        'C:\Program Files (x86)\GeneXus\GeneXus18'
    }
}
$sdkMarker = Join-Path $GxPath 'Artech.Architecture.Common.dll'
if (-not (Test-Path -LiteralPath $sdkMarker -PathType Leaf)) {
    Fail-Baseline "GeneXus 18 SDK not found under '$GxPath'. Set -GxPath or GX_PATH."
}
$env:GX_PATH = $GxPath

$rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\'
function Normalize-WarningFile([string]$File) {
    $trimmed = $File.Trim().TrimEnd(']')
    try {
        $full = [System.IO.Path]::GetFullPath($trimmed)
        if ($full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
            return $full.Substring($rootFull.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        }
    } catch { }
    return $trimmed.Replace('\', '/')
}

function Warning-Key($Entry) {
    return '{0}|{1}|{2}' -f $Entry.code, $Entry.file, $Entry.line
}

function Warning-Pair($Entry) {
    return '{0}|{1}' -f $Entry.code, $Entry.file
}

function Compare-WarningLocations {
    param(
        [object[]]$Baseline,
        [object[]]$Current
    )
    $baselineList = @($Baseline)
    $currentList = @($Current)
    $baselineKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $baselineList) { [void]$baselineKeys.Add((Warning-Key $entry)) }
    $currentKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $currentList) { [void]$currentKeys.Add((Warning-Key $entry)) }

    $baselineGroups = @{}
    foreach ($entry in $baselineList) {
        $pair = Warning-Pair $entry
        if (-not $baselineGroups.ContainsKey($pair)) { $baselineGroups[$pair] = @() }
        $baselineGroups[$pair] += $entry
    }
    $currentGroups = @{}
    foreach ($entry in $currentList) {
        $pair = Warning-Pair $entry
        if (-not $currentGroups.ContainsKey($pair)) { $currentGroups[$pair] = @() }
        $currentGroups[$pair] += $entry
    }

    $moved = New-Object System.Collections.Generic.List[object]
    foreach ($pair in $baselineGroups.Keys) {
        if (-not $currentGroups.ContainsKey($pair)) { continue }
        $oldLines = @($baselineGroups[$pair] | ForEach-Object line | Sort-Object)
        $newLines = @($currentGroups[$pair] | ForEach-Object line | Sort-Object)
        if ($oldLines.Count -eq $newLines.Count -and (@($oldLines) -join ',') -ne (@($newLines) -join ',')) {
            $first = $baselineGroups[$pair][0]
            [void]$moved.Add([PSCustomObject]@{
                code = $first.code
                file = $first.file
                baselineLines = @($oldLines)
                currentLines = @($newLines)
            })
        }
    }
    $movedPairs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $moved) { [void]$movedPairs.Add((Warning-Pair $entry)) }

    $newWarnings = @($currentList | Where-Object {
        $key = Warning-Key $_
        if ($baselineKeys.Contains($key)) { return $false }
        $pair = Warning-Pair $_
        # A pair with the same cardinality is a line move, not a new warning.
        if ($movedPairs.Contains($pair)) { return $false }
        return $true
    })
    $removedWarnings = @($baselineList | Where-Object {
        $key = Warning-Key $_
        if ($currentKeys.Contains($key)) { return $false }
        return -not $movedPairs.Contains((Warning-Pair $_))
    })
    [PSCustomObject]@{
        New = $newWarnings
        Removed = $removedWarnings
        Moved = $moved.ToArray()
    }
}

$solution = Join-Path $root 'Genexus18MCP.sln'
$buildOutput = @(& dotnet build $solution '-c' 'Release' '-t:Rebuild' '--nologo' '-v:minimal' "-p:GX_PATH=$GxPath" 2>&1 | ForEach-Object { $_.ToString() })
$buildExit = $LASTEXITCODE

$warningPattern = '^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*warning\s+(?<code>[A-Za-z]+\d+):'
$globalWarningPattern = '(?i)\bwarning\s+(?<code>[A-Za-z]+\d+):'
$current = @()
foreach ($outputLine in $buildOutput) {
    if ($outputLine -match $warningPattern) {
        $current += [PSCustomObject]@{
            code = $Matches.code.ToUpperInvariant()
            file = Normalize-WarningFile $Matches.file
            line = [int]$Matches.line
        }
    } elseif ($outputLine -match $globalWarningPattern) {
        $current += [PSCustomObject]@{
            code = $Matches.code.ToUpperInvariant()
            file = '<global>'
            line = 0
        }
    }
}

$distinct = @($current | Sort-Object code,file,line -Unique)
$msb3277 = @($distinct | Where-Object { $_.code -eq 'MSB3277' })
if ($buildExit -ne 0) {
    $tail = ($buildOutput | Select-Object -Last 12) -join "`n"
    Fail-Baseline "Release rebuild exited with code $buildExit.`n$tail"
}
if ($msb3277.Count -gt 0) {
    Fail-Baseline "MSB3277 is present in the Release build."
}

if ($UpdateBaseline) {
    $newManifest = [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToString('yyyy-MM-dd')
        warningCount = $distinct.Count
        warnings = @($distinct)
    }
    $json = $newManifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($BaselineFile, "$json`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Warning baseline updated: $($distinct.Count) distinct locations at $BaselineFile" -ForegroundColor Green
    exit 0
}

$comparison = Compare-WarningLocations -Baseline @($manifest.warnings) -Current $distinct
$newWarnings = @($comparison.New)
$removedWarnings = @($comparison.Removed)
$movedWarnings = @($comparison.Moved)

Write-Host "Release warning locations: $($distinct.Count) (baseline $(@($manifest.warnings).Count)); new $($newWarnings.Count); moved $($movedWarnings.Count); removed $($removedWarnings.Count)."
if ($newWarnings.Count -gt 0) {
    $details = ($newWarnings | ForEach-Object { "{0} {1}:{2}" -f $_.code, $_.file, $_.line }) -join ', '
    Fail-Baseline "New warning locations detected (line-only moves are reported separately): $details"
}
if ($movedWarnings.Count -gt 0) {
    $details = ($movedWarnings | ForEach-Object { "{0} {1} [$($_.baselineLines -join ',') -> $($_.currentLines -join ',')]" }) -join ', '
    Write-Host "Line-only warning moves: $details" -ForegroundColor DarkGray
}
Write-Host "Warning baseline passed; no new locations and no MSB3277." -ForegroundColor Green
