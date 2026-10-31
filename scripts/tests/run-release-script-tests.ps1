param(
    # Test seams: run only these scripts, from this directory.
    [string[]]$Name,
    [string]$TestRoot = $PSScriptRoot,
    [int]$ThrottleLimit = [Environment]::ProcessorCount,
    [string]$BudgetPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ci/test-time-budget.json'),
    # The per-script time budget is a hosted-runner guardrail; locally it only reports.
    [switch]$EnforceBudget = ($env:GITHUB_ACTIONS -eq 'true')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tests = @(
    'test-version-catalog.ps1',
    'install-contract.tests.ps1',
    'release-manifest.tests.ps1',
    'test-build-publish-config.ps1',
    'test-live-build-all.ps1',
    'test-live-fixture-manifest.ps1',
    'test-live.test.ps1',
    'test-start-sdk-runner.ps1',
    'test-release-entrypoint.ps1',
    'test-release-issues.ps1',
    'test-release-contract.ps1',
    'test-release-changelog.ps1',
    'test-collect-diagnostics.ps1',
    'test-release-orchestration.ps1',
    'test-release-preflight.ps1',
    'test-release-workflow.ps1',
    'test-ci-workflow-policy.ps1',
    'test-ci-time-budget.ps1',
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
    'test-check-whitespace.ps1',
    'test-live-matrix.test.ps1',
    'test-workwith-persistence-smoke.ps1',
    'test-workwith-persistence-smoke-behavior.ps1',
    'test-run-gate.ps1'
)
if ($Name) { $tests = @($Name) }

# Each script runs in its own pwsh with its own GUID temp directory, so they run in
# parallel: one after another they took ~215s of every CI run. Output is printed per
# script, in list order, once all have finished.
$results = @($tests | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
    $path = Join-Path $using:TestRoot $_
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $output = & pwsh -NoProfile -File $path 2>&1 | Out-String
    [pscustomobject]@{ Name = $_; ExitCode = $LASTEXITCODE; Seconds = $clock.Elapsed.TotalSeconds; Output = $output }
})
$results = @($tests | ForEach-Object { $name = $_; $results | Where-Object Name -eq $name })
$invariant = [Globalization.CultureInfo]::InvariantCulture
foreach ($result in $results) {
    Write-Host ("`n>>> $($result.Name) (" + $result.Seconds.ToString('N1', $invariant) + 's)') -ForegroundColor Cyan
    Write-Host $result.Output.TrimEnd()
}

$budget = Get-Content -LiteralPath $BudgetPath -Raw | ConvertFrom-Json
$limit = [double]$budget.releaseScriptSeconds
$exceptions = @{}
foreach ($entry in @($budget.exceptions.releaseScripts.PSObject.Properties)) { $exceptions[$entry.Name] = [string]$entry.Value }
Write-Host "`nSlowest scripts (budget ${limit}s each):"
foreach ($result in @($results | Sort-Object Seconds -Descending | Select-Object -First 5)) {
    Write-Host ([string]::Format($invariant, '  {0,6:N1}s  {1}', $result.Seconds, $result.Name))
}

$failures = @($results | Where-Object ExitCode -ne 0 | ForEach-Object { "$($_.Name) failed with exit code $($_.ExitCode)." })
if ($EnforceBudget) {
    $failures += @($results | Where-Object { $_.Seconds -gt $limit -and -not $exceptions.ContainsKey($_.Name) } | ForEach-Object {
        [string]::Format($invariant, '{0} took {1:N1}s, over the {2}s CI time budget: make it faster or list it under exceptions.releaseScripts in {3} with the reason.', $_.Name, $_.Seconds, $limit, (Split-Path $BudgetPath -Leaf))
    })
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAILED: $_" -ForegroundColor Red }
    throw "release-script-suite: $($failures.Count) failure(s) above."
}
Write-Host 'release-script-suite: all PowerShell release/live guards passed' -ForegroundColor Green
