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

$baselineKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.warnings)) {
    [void]$baselineKeys.Add((Warning-Key $entry))
}
$newWarnings = @($distinct | Where-Object { -not $baselineKeys.Contains((Warning-Key $_)) })
$removedWarnings = @($manifest.warnings | Where-Object {
    $key = Warning-Key $_
    -not ($distinct | Where-Object { (Warning-Key $_) -eq $key })
})

Write-Host "Release warning locations: $($distinct.Count) (baseline $(@($manifest.warnings).Count)); new $($newWarnings.Count); removed $($removedWarnings.Count)."
if ($newWarnings.Count -gt 0) {
    $details = ($newWarnings | ForEach-Object { "{0} {1}:{2}" -f $_.code, $_.file, $_.line }) -join ', '
    Fail-Baseline "New warning locations detected: $details"
}
Write-Host "Warning baseline passed; no new locations and no MSB3277." -ForegroundColor Green
