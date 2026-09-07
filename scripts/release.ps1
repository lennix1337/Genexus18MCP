# Backwards-compatible release entrypoint.
#
# The implementation lives in ..\release.ps1. Keep this file as a thin
# translator so callers using `patch`, `minor`, or `major` do not get a second
# release workflow with different provenance and validation rules.

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$BumpType,
    [switch]$NoBump,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$AllowDirty,
    [switch]$Detach,
    [string]$StatusFile
)

$ErrorActionPreference = 'Stop'
if ($NoBump) {
    throw "-NoBump is no longer supported by the legacy wrapper. Use .\release.ps1 -Version X.Y.Z (or omit -Version to use package.json)."
}

$root = Split-Path -Parent $PSScriptRoot
$canonical = Join-Path $root 'release.ps1'
if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) { throw "Canonical release script not found: $canonical" }

$forwarded = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($BumpType)) {
    if ($BumpType -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        $targetVersion = $BumpType
    } elseif ($BumpType -in @('patch', 'minor', 'major')) {
        $package = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
        if ($package.version -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
            throw "Cannot calculate $BumpType from package.json version '$($package.version)'. Pass an explicit semver to .\release.ps1."
        }
        $major = [int]$Matches.major; $minor = [int]$Matches.minor; $patchValue = [int]$Matches.patch
        switch ($BumpType) {
            'major' { $major++; $minor = 0; $patchValue = 0 }
            'minor' { $minor++; $patchValue = 0 }
            'patch' { $patchValue++ }
        }
        $targetVersion = "$major.$minor.$patchValue"
    } else {
        throw "BumpType must be patch, minor, major, or an explicit semver like 3.0.1."
    }
    $forwarded.Add('-Version'); $forwarded.Add($targetVersion)
}
foreach ($entry in @{
    NotesFile = $NotesFile
    DryRun = $DryRun
    SkipBuild = $SkipBuild
    SkipTests = $SkipTests
    AllowDirty = $AllowDirty
    Detach = $Detach
    StatusFile = $StatusFile
}.GetEnumerator()) {
    if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
        if ($entry.Value.IsPresent) { $forwarded.Add("-$($entry.Key)") }
    } elseif (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
        $forwarded.Add("-$($entry.Key)"); $forwarded.Add([string]$entry.Value)
    }
}

$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
$exitCode = 1
Push-Location $root
try {
    if ($pwsh) {
        & $pwsh.Source -NoProfile -File $canonical @($forwarded)
    } else {
        & $canonical @($forwarded)
    }
    $exitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $exitCode
