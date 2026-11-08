# A VS Code update holds the Code mutex and fails the Nexus IDE checks late in the preflight.
# release.ps1 and release-preflight.ps1 must refuse it before creating any release state.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts/release-contract.ps1')
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { Write-Host 'release-host-blocker: skipped, Windows-only guard'; exit 0 }
$temp = Join-Path ([IO.Path]::GetTempPath()) ("gxmcp-host-blocker-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$global:GxMcpTestUpdaterCommand = 'CodeSetup-x64.exe /verysilent /update'
function Get-CimInstance { @([pscustomobject]@{ Name = 'CodeSetup-x64.exe'; CommandLine = $global:GxMcpTestUpdaterCommand }) }
function Assert-Refused([scriptblock]$Action, [string]$Label, [string]$StatePath) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try { & $Action; throw "$Label ran with a VS Code update in progress." }
    catch { if ($_.Exception.Message -notmatch 'VS Code update in progress') { throw } }
    if ($clock.Elapsed.TotalSeconds -ge 5) { throw "$Label took $($clock.Elapsed.TotalSeconds)s to refuse the updater." }
    if (Test-Path -LiteralPath $StatePath) { throw "$Label wrote $StatePath before refusing." }
}
try {
    $status = Join-Path $temp 'status.json'
    Assert-Refused { & (Join-Path $root 'release.ps1') -Version '9.0.0' -StatusFile $status } 'release.ps1' $status
    $summary = Join-Path $temp 'summary.json'
    Assert-Refused { & (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -SummaryPath $summary } 'release-preflight.ps1' $summary
    $global:GxMcpTestUpdaterCommand = 'CodeSetup-x64.exe /install'
    Assert-GxMcpReleaseHostReady
    Write-Host 'release-host-blocker: updater refused early at both entrypoints; other setups pass'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
