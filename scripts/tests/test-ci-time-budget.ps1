$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$assertDurations = Join-Path $root 'scripts/ci/assert-test-durations.ps1'
$suite = Join-Path $root 'scripts/tests/run-release-script-tests.ps1'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "CI time budget test failed: $Message" }
}

function Invoke-Pwsh([string[]]$Arguments) {
    $output = & pwsh -NoProfile @Arguments 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

$temp = Join-Path $env:TEMP ('gxmcp-ci-time-budget-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    # ---- assert-test-durations.ps1 (dotnet per-test budget) --------------------
    $trx = Join-Path $temp 'gateway.trx'
    @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Ns.Fast.Runs" duration="00:00:01.2000000" outcome="Passed" />
    <UnitTestResult testName="Ns.Slow.WaitsOutATimeout" duration="00:00:12.5000000" outcome="Passed"><Output><StdOut>phase broker-start 9100 ms</StdOut></Output></UnitTestResult>
    <UnitTestResult testName="Ns.Theory.Case(x: 1)" duration="00:00:15.0000000" outcome="Passed" />
    <UnitTestResult testName="Ns.Theory.Case(x: 2)" duration="00:00:00.1000000" outcome="Passed" />
  </Results>
</TestRun>
'@ | Set-Content -LiteralPath $trx -Encoding utf8

    function Write-Budget([string]$Path, [hashtable]$DotnetExceptions) {
        [ordered]@{
            schemaVersion = 1; dotnetTestSeconds = 10; releaseScriptSeconds = 1
            exceptions = [ordered]@{ dotnet = $DotnetExceptions; releaseScripts = @{} }
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8
    }

    $budget = Join-Path $temp 'budget.json'
    $summary = Join-Path $temp 'summary.md'
    Write-Budget $budget @{ 'Ns.Theory.Case' = 'compiles a fixture project; covered by issue 1' }
    $over = Invoke-Pwsh @('-File', $assertDurations, '-TrxPath', $trx, '-BudgetPath', $budget, '-SummaryPath', $summary)
    Assert-True ($over.ExitCode -ne 0) 'a test over the per-test budget must fail the lane.'
    Assert-True ($over.Output -match 'Ns\.Slow\.WaitsOutATimeout took 12\.5') "the slow test must be named with its duration: $($over.Output)"
    Assert-True ($over.Output -match 'phase broker-start 9100 ms') "the slow test's own output must be printed, so the log names the slow phase: $($over.Output)"
    Assert-True ($over.Output -notmatch 'Ns\.Theory\.Case took') 'an excepted Theory (matched without its arguments) must not fail.'
    Assert-True ((Get-Content -LiteralPath $summary -Raw) -match '\| 15[.,]0 \| Ns\.Theory\.Case \|') 'the step summary must list the slowest tests.'

    Write-Budget $budget @{ 'Ns.Theory.Case' = 'compiles a fixture project; covered by issue 1'; 'Ns.Slow.WaitsOutATimeout' = 'waits a real socket timeout; covered by issue 2'; 'Ns.Gone' = 'was slow once; the test is gone now' }
    $stale = Invoke-Pwsh @('-File', $assertDurations, '-TrxPath', $trx, '-BudgetPath', $budget, '-SummaryPath', $summary)
    Assert-True ($stale.ExitCode -ne 0 -and $stale.Output -match "budget exception 'Ns\.Gone' names no test") "a stale exception must fail: $($stale.Output)"

    Write-Budget $budget @{ 'Ns.Theory.Case' = 'compiles a fixture project; covered by issue 1'; 'Ns.Slow.WaitsOutATimeout' = 'waits a real socket timeout; covered by issue 2' }
    $within = Invoke-Pwsh @('-File', $assertDurations, '-TrxPath', $trx, '-BudgetPath', $budget, '-SummaryPath', $summary)
    Assert-True ($within.ExitCode -eq 0) "every slow test excepted must pass: $($within.Output)"

    Write-Budget $budget @{ 'Ns.Theory.Case' = ' ' }
    $unexplained = Invoke-Pwsh @('-File', $assertDurations, '-TrxPath', $trx, '-BudgetPath', $budget, '-SummaryPath', $summary)
    Assert-True ($unexplained.ExitCode -ne 0 -and $unexplained.Output -match 'must give the reason') 'an exception without a reason must be refused.'

    # ---- run-release-script-tests.ps1 (per-script budget, parallel runner) -------
    $scripts = Join-Path $temp 'scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    "Write-Host 'fast ran'" | Set-Content -LiteralPath (Join-Path $scripts 'fast.ps1')
    "Start-Sleep -Seconds 3; Write-Host 'slow ran'" | Set-Content -LiteralPath (Join-Path $scripts 'slow.ps1')
    "Write-Host 'broken ran'; exit 3" | Set-Content -LiteralPath (Join-Path $scripts 'broken.ps1')
    Write-Budget $budget @{}

    $enforced = Invoke-Pwsh @('-Command', "& '$suite' -Name fast.ps1,slow.ps1 -TestRoot '$scripts' -BudgetPath '$budget' -EnforceBudget")
    Assert-True ($enforced.ExitCode -ne 0 -and $enforced.Output -match 'slow\.ps1 took [\d.,]+s, over the 1s CI time budget') "an over-budget script must fail on CI: $($enforced.Output)"
    Assert-True ($enforced.Output -match 'fast ran' -and $enforced.Output -match 'slow ran') 'every script output must be printed.'

    $local = Invoke-Pwsh @('-Command', "& '$suite' -Name fast.ps1,slow.ps1 -TestRoot '$scripts' -BudgetPath '$budget' -EnforceBudget:`$false")
    Assert-True ($local.ExitCode -eq 0) "outside CI the per-script budget only reports: $($local.Output)"

    $failed = Invoke-Pwsh @('-Command', "& '$suite' -Name fast.ps1,broken.ps1 -TestRoot '$scripts' -BudgetPath '$budget' -EnforceBudget:`$false")
    Assert-True ($failed.ExitCode -ne 0 -and $failed.Output -match 'broken\.ps1 failed with exit code 3') "a failing script must fail the suite even when run in parallel: $($failed.Output)"
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'ci-time-budget: per-test and per-script budgets, exceptions and parallel failures passed' -ForegroundColor Green
