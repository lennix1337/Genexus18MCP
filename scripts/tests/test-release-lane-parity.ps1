$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Drift guard for the test lanes hosted CI cannot run. The Worker net48 suite needs the GeneXus SDK
# and the ProcessSmoke lane needs the Release Gateway; both exist only in release-preflight.ps1.
# The protected SDK job must run those lanes through that entry point, so the lane definitions
# cannot fork into a second copy in ci.yml or sdk-validation.ps1.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$preflightPath = Join-Path $root 'scripts\release-preflight.ps1'
$sdkValidationPath = Join-Path $root 'scripts\ci\sdk-validation.ps1'
$workflowPath = Join-Path $root '.github\workflows\ci.yml'
$preflight = Get-Content -LiteralPath $preflightPath -Raw
$sdkValidation = Get-Content -LiteralPath $sdkValidationPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$requiredLanes = @('solution build and tests', 'solution process smoke tests')

foreach ($lane in $requiredLanes) {
    if ($preflight -notmatch "Name = '$([regex]::Escape($lane))'") {
        throw "release-preflight.ps1 no longer defines the lane '$lane'; the protected job would run a lane that does not exist."
    }
}

$call = [regex]::Match($sdkValidation, '(?ms)&\s+pwsh[^\r\n]*release-preflight\.ps1[^\r\n]*(\r?\n[ \t]*[^\r\n&]*)*')
if (-not $call.Success) {
    throw 'scripts/ci/sdk-validation.ps1 must run scripts/release-preflight.ps1 for the Worker net48 suite and the ProcessSmoke lane.'
}
$callText = $call.Value
foreach ($lane in $requiredLanes) {
    if ($callText -notmatch [regex]::Escape($lane)) {
        throw "The protected lane's release-preflight call must select '$lane' through -OnlyLanes."
    }
}
foreach ($switch in @('-SkipLive', '-SkipWarningBaseline')) {
    if ($callText -notmatch [regex]::Escape($switch)) {
        throw "The protected lane's release-preflight call must pass ${switch}: the SDK job already runs the live and baseline gates itself."
    }
}

# The protected job is the only CI job with the GeneXus SDK, so it must be the one that runs the lanes.
$sdkJob = [regex]::Match($workflow, '(?ms)^  sdk-validation:\s*\r?\n(.*?)(?=^  [a-z][a-z0-9-]*:\s*$|\z)')
if (-not $sdkJob.Success -or $sdkJob.Groups[1].Value -notmatch 'scripts\\ci\\sdk-validation\.ps1|scripts/ci/sdk-validation\.ps1') {
    throw 'ci.yml sdk-validation job must keep running scripts/ci/sdk-validation.ps1.'
}

Write-Host 'release-lane-parity: protected SDK lanes run through release-preflight.ps1 with the same lane names passed.' -ForegroundColor Green
