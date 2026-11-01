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
$contract = Join-Path $root 'scripts\release-contract.ps1'
if (Test-Path -LiteralPath $contract -PathType Leaf) { . $contract }
$canonical = Join-Path $root 'release.ps1'
if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) { throw "Canonical release script not found: $canonical" }

$forwarded = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($BumpType)) {
    if ($BumpType -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        $targetVersion = $BumpType
    } elseif ($BumpType -in @('patch', 'minor', 'major')) {
        $package = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
        if (Get-Command Get-GxMcpNextSemVer -ErrorAction SilentlyContinue) {
            $targetVersion = Get-GxMcpNextSemVer -CurrentVersion ([string]$package.version) -Bump $BumpType
        } else {
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
        }
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
# Nothing may run between the canonical script and its exit status: any trailing
# statement (or a pipeline/log filter around the call) would replace the release's
# failure with its own success. A missing status is treated as a failure, never as 0.
$exitCode = 1
Push-Location $root
try {
    $global:LASTEXITCODE = $null
    if ($pwsh) {
        & $pwsh.Source -NoProfile -File $canonical @($forwarded)
        if ($null -ne $LASTEXITCODE) { $exitCode = [int]$LASTEXITCODE }
    } else {
        # In-process run: a script that finishes normally leaves $LASTEXITCODE stale, so
        # success is read from $? and only a failing run consults the numeric code.
        & $canonical @($forwarded)
        if ($?) { $exitCode = 0 } elseif ($LASTEXITCODE) { $exitCode = [int]$LASTEXITCODE }
    }
} finally {
    Pop-Location
}
exit $exitCode
