$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tests = @(
    'install-contract.tests.ps1',
    'release-manifest.tests.ps1',
    'test-live-build-all.ps1',
    'test-live.test.ps1',
    'test-release-entrypoint.ps1',
    'test-release-orchestration.ps1',
    'test-release-preflight.ps1',
    'test-release-status.ps1',
    'test-warning-baseline.ps1'
)
foreach ($name in $tests) {
    $path = Join-Path $PSScriptRoot $name
    Write-Host "`n>>> $name" -ForegroundColor Cyan
    & pwsh -NoProfile -File $path
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}
Write-Host 'release-script-suite: all PowerShell release/live guards passed' -ForegroundColor Green
