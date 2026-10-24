$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tests = @(
    'test-version-catalog.ps1',
    'install-contract.tests.ps1',
    'release-manifest.tests.ps1',
    'test-live-build-all.ps1',
    'test-live-fixture-manifest.ps1',
    'test-live.test.ps1',
    'test-release-entrypoint.ps1',
    'test-release-issues.ps1',
    'test-release-contract.ps1',
    'test-release-changelog.ps1',
    'test-collect-diagnostics.ps1',
    'test-release-orchestration.ps1',
    'test-release-preflight.ps1',
    'test-release-workflow.ps1',
    'test-ci-workflow-policy.ps1',
    'test-pr-preflight-ripwire.ps1',
    'test-pr-push.ps1',
    'test-pr-review-tooling.ps1',
    'test-integration-preflight.ps1',
    'test-release-status.ps1',
    'test-release-exit-codes.ps1',
    'test-release-freshness.ps1',
    'test-release-resume.ps1',
    'test-release-publication.ps1',
    'test-release-doctor.ps1',
    'test-warning-baseline.ps1',
    'test-line-endings.ps1',
    'test-live-matrix.test.ps1',
    'test-workwith-persistence-smoke.ps1',
    'test-publish-config.ps1',
    'test-run-gate.ps1'
)
foreach ($name in $tests) {
    $path = Join-Path $PSScriptRoot $name
    Write-Host "`n>>> $name" -ForegroundColor Cyan
    & pwsh -NoProfile -File $path
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}
Write-Host 'release-script-suite: all PowerShell release/live guards passed' -ForegroundColor Green
