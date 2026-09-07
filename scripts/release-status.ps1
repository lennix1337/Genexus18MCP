[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [ValidateRange(0, 86400)][int]$WaitSeconds = 0
)

$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($Path)
$deadline = (Get-Date).AddSeconds($WaitSeconds)

function Read-ReleaseStatus {
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json }
    catch { throw "Release status file is not valid JSON: $resolved" }
}

$status = $null
do {
    $status = Read-ReleaseStatus
    if ($null -ne $status -and $status.state -in @('succeeded', 'failed')) { break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Seconds 1
} while ($true)

if ($null -eq $status) {
    Write-Error "Release status file not found: $resolved"
    exit 2
}

$phase = if ($status.phase) { $status.phase } else { 'unknown' }
$state = if ($status.state) { $status.state } else { 'unknown' }
Write-Host ("version={0} tag={1} phase={2} state={3}" -f $status.version, $status.tag, $phase, $state)
Write-Host ("updatedAtUtc={0} pid={1} exitCode={2}" -f $status.updatedAtUtc, $status.pid, $status.exitCode)
if ($status.releaseUrl) { Write-Host "releaseUrl=$($status.releaseUrl)" }
if ($status.workflowRunId) { Write-Host "workflowRunId=$($status.workflowRunId)" }
if ($status.error) { Write-Host "error=$($status.error)" -ForegroundColor Red }

if ($state -eq 'succeeded') { exit 0 }
if ($state -eq 'failed') { exit 1 }
if ((Get-Date) -ge $deadline) { exit 2 }
exit 2
