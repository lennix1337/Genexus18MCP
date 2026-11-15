$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Guards scripts/coverage/collect.ps1 -Parallel. The two suites must really overlap,
# each must get only the environment the sequential path gives it, and the verdicts
# must be the sequential ones: a Worker failure is recorded for assert-threshold.ps1,
# a Gateway failure stops the script. The functions and the step that runs the suites
# are cut out of collect.ps1 and executed, so a collect.ps1 that awaits the first suite
# before starting the second, or leaks an environment override, fails here.

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Get-Content -LiteralPath (Join-Path $root 'scripts/coverage/collect.ps1') -Raw

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Get-CollectSegment {
    param([string]$StartMarker, [string]$EndMarker)
    $start = $source.IndexOf($StartMarker, [StringComparison]::Ordinal)
    Assert-True ($start -ge 0) "collect.ps1 no longer contains '$StartMarker'."
    $end = $source.IndexOf($EndMarker, $start, [StringComparison]::Ordinal)
    Assert-True ($end -gt $start) "collect.ps1 no longer contains '$EndMarker' after '$StartMarker'."
    return $source.Substring($start, $end - $start)
}

$functions = Get-CollectSegment 'function Get-CoverageTestArguments {' 'function Get-BaseOutputPath {'
$runSuites = Get-CollectSegment '# 7. Run the suites.' '# 8. List what was produced.'
$pwsh = (Get-Process -Id $PID).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-coverage-parallel-' + [guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($name in @('GXMCP_COVERAGE_TEST_A', 'GXMCP_COVERAGE_TEST_B', 'GXMCP_SHAREDHOST_TESTHOST_EXE', 'GX_PATH')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $OutputRoot = Join-Path $temp 'coverage'
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    . ([scriptblock]::Create($functions))

    # 1. The dotnet arguments are the ones the sequential path has always used.
    $arguments = @(Get-CoverageTestArguments -ProjectPath 'p.csproj' -Label 'worker' -BaseOutputPath 'out\' -SettingsPath 'w.runsettings' -BuildProperties @('-p:Platform=x86'))
    $expected = @('test', 'p.csproj', '-v', 'minimal', '-p:BaseOutputPath=out\', '-p:Platform=x86', '--collect', 'XPlat Code Coverage', '--settings', 'w.runsettings', '--results-directory', (Join-Path $OutputRoot 'worker'), '--logger', 'trx;LogFileName=worker.trx')
    Assert-True (($arguments -join "`n") -ceq ($expected -join "`n")) "Unexpected dotnet arguments: $($arguments -join ' ')"
    Assert-True (Test-Path -LiteralPath (Join-Path $OutputRoot 'worker') -PathType Container) 'The results directory must be created.'

    # A finished suite is judged the same way in both modes: a failed run, then a
    # missing report, then the report and the TRX published beside the other suite's.
    $threw = $null
    try { Complete-CoverageTest -Label 'worker' -ExitCode 1 } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -ceq 'worker tests failed.') "A non-zero exit code must fail the suite, got: $threw"
    $threw = $null
    try { Complete-CoverageTest -Label 'worker' -ExitCode 0 } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -ceq 'Coverage report not found for worker.') "A run without a coverage report must fail, got: $threw"
    $report = Join-Path $OutputRoot 'worker\run\coverage.cobertura.xml'
    New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
    Set-Content -LiteralPath $report -Value '<coverage line-rate="1" />'
    Set-Content -LiteralPath (Join-Path $OutputRoot 'worker\worker.trx') -Value '<TestRun />'
    Complete-CoverageTest -Label 'worker' -ExitCode 0
    Assert-True ((Get-Content -LiteralPath (Join-Path $OutputRoot 'worker.cobertura.xml') -Raw) -match 'line-rate="1"') 'The coverage report must be published beside the other suite.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $OutputRoot 'worker.trx') -Raw) -match 'TestRun') 'The TRX must be published beside the other suite.'

    # The sequential path runs dotnet with those arguments and judges the run.
    function dotnet { $script:dotnetArguments = @($args); $global:LASTEXITCODE = $script:dotnetExit }
    $script:dotnetExit = 0
    Invoke-CoverageTest -ProjectPath 'p.csproj' -Label 'worker' -BaseOutputPath 'out\' -SettingsPath 'w.runsettings' -BuildProperties @('-p:Platform=x86') 6>$null
    Assert-True (($script:dotnetArguments -join "`n") -ceq ($expected -join "`n")) "The sequential path must run dotnet with the same arguments, got: $($script:dotnetArguments -join ' ')"
    $script:dotnetExit = 1
    $threw = $null
    try { Invoke-CoverageTest -ProjectPath 'p.csproj' -Label 'worker' -BaseOutputPath 'out\' -SettingsPath 'w.runsettings' 6>$null } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -ceq 'worker tests failed.') "The sequential path must fail a suite whose dotnet run failed, got: $threw"

    # 2. Two suites started one after the other really run at the same time, each with
    # its own environment, and each returns its own exit code and log.
    $stub = Join-Path $temp 'suite.ps1'
    Set-Content -LiteralPath $stub -Encoding utf8 -Value @'
param([string]$Name, [int]$ExitCode, [string]$Marker)
Set-Content -LiteralPath "$Marker.started" -Value 'started'
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while (-not (Test-Path -LiteralPath "$Marker.release") -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
Write-Output "$Name A=[$env:GXMCP_COVERAGE_TEST_A] B=[$env:GXMCP_COVERAGE_TEST_B]"
[Console]::Error.WriteLine("$Name stderr")
exit $ExitCode
'@
    $env:GXMCP_COVERAGE_TEST_A = 'parent-a'
    $env:GXMCP_COVERAGE_TEST_B = 'parent-b'
    $markerOne = Join-Path $temp 'one'
    $markerTwo = Join-Path $temp 'two'
    $one = Start-CoverageTest -Label 'one' -FileName $pwsh -Arguments @('-NoProfile', '-File', $stub, 'one', '0', $markerOne) `
        -Environment @{ GXMCP_COVERAGE_TEST_A = 'only-one' } 6>$null
    $two = Start-CoverageTest -Label 'two' -FileName $pwsh -Arguments @('-NoProfile', '-File', $stub, 'two', '3', $markerTwo) `
        -Environment @{ GXMCP_COVERAGE_TEST_B = $null } 6>$null
    # Each stub waits for a release file that is written only after both have started,
    # so neither can finish unless the two were alive together.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not ((Test-Path -LiteralPath "$markerOne.started") -and (Test-Path -LiteralPath "$markerTwo.started")) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    Assert-True ((Test-Path -LiteralPath "$markerOne.started") -and (Test-Path -LiteralPath "$markerTwo.started")) 'Both suites must be running before either is awaited.'
    Assert-True (-not $one.Process.HasExited -and -not $two.Process.HasExited) 'Neither suite may have finished while the other was still starting.'
    Set-Content -LiteralPath "$markerOne.release" -Value 'go'
    Set-Content -LiteralPath "$markerTwo.release" -Value 'go'
    $printed = @(& { Wait-CoverageTest -Run $one; Wait-CoverageTest -Run $two } 6>&1)
    $exitCodes = @($printed | Where-Object { $_ -is [int] })
    $text = ($printed | Where-Object { $_ -isnot [int] } | ForEach-Object { [string]$_ }) -join "`n"
    Assert-True ($exitCodes.Count -eq 2 -and $exitCodes[0] -eq 0 -and $exitCodes[1] -eq 3) "Each suite must return its own exit code, got: $($exitCodes -join ',')"
    Assert-True ($text -match 'one A=\[only-one\] B=\[parent-b\]') "The first suite must see only its own override, got: $text"
    Assert-True ($text -match 'two A=\[parent-a\] B=\[\]') "A null override must remove the variable for that suite only, got: $text"
    Assert-True ($text -match 'one stderr' -and $text -match 'two stderr') 'The error stream of each suite must be printed.'
    Assert-True ($text.IndexOf('one tests', [StringComparison]::Ordinal) -lt $text.IndexOf('two tests', [StringComparison]::Ordinal)) 'The logs must be printed whole, one suite after the other.'
    Assert-True ($env:GXMCP_COVERAGE_TEST_A -ceq 'parent-a' -and $env:GXMCP_COVERAGE_TEST_B -ceq 'parent-b') 'The overrides must not leak into the calling process.'
    foreach ($label in @('one', 'two')) {
        $log = Join-Path $OutputRoot "$label.console.log"
        Assert-True ((Test-Path -LiteralPath $log) -and (Get-Content -LiteralPath $log -Raw) -match "$label A=") "The log of '$label' must be kept beside the reports."
    }

    # 3. The step that runs the suites, executed with recorded stand-ins.
    $script:events = New-Object System.Collections.Generic.List[string]
    function Get-GxPrimaryMajor { param($Catalog) return '18' }
    function Get-CoverageTestArguments { param($ProjectPath, $Label, $BaseOutputPath, $SettingsPath, $BuildProperties) return @("args-$Label") }
    function Start-CoverageTest {
        param($Label, $Arguments, $Environment = @{}, $FileName = 'dotnet')
        $pairs = @($Environment.Keys | Sort-Object | ForEach-Object { "$_=$($Environment[$_])" }) -join ','
        $script:events.Add("start:$Label[$pairs] parent-testhost=[$env:GXMCP_SHAREDHOST_TESTHOST_EXE] parent-gx=[$env:GX_PATH]")
        return @{ Label = $Label }
    }
    function Wait-CoverageTest { param($Run) $script:events.Add("wait:$($Run.Label)"); return $script:exitCodes[$Run.Label] }
    function Complete-CoverageTest {
        param($Label, $ExitCode)
        $script:events.Add("complete:$Label=$ExitCode")
        if ($ExitCode -ne 0) { throw "$Label tests failed." }
    }
    function Invoke-CoverageTest {
        param($ProjectPath, $Label, $BaseOutputPath, $SettingsPath, $BuildProperties)
        $script:events.Add("invoke:$Label testhost=[$env:GXMCP_SHAREDHOST_TESTHOST_EXE] gx=[$env:GX_PATH]")
        if ($script:exitCodes[$Label] -ne 0) { throw "$Label tests failed." }
    }
    function Invoke-RunSuites {
        param([bool]$UseParallel, [string]$Sdk, [hashtable]$ExitCodes)
        $script:events.Clear()
        $script:exitCodes = $ExitCodes
        $OutputRoot = Join-Path $temp ('run-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
        $gxCatalog = $null
        $Parallel = [switch]$UseParallel
        $resolvedGxPath = $Sdk
        $previousTestHostExe = 'previous-host'
        $env:GXMCP_SHAREDHOST_TESTHOST_EXE = 'built-host'
        $env:GX_PATH = 'caller-gx'
        $previousGxPath = $env:GX_PATH
        $gatewayTest = @{ ProjectPath = 'g.csproj'; Label = 'gateway'; BaseOutputPath = 'base-gateway\'; SettingsPath = 'g.runsettings' }
        $workerTest = @{ ProjectPath = 'w.csproj'; Label = 'worker'; BaseOutputPath = 'base-worker\'; SettingsPath = 'w.runsettings'; BuildProperties = @('-p:Platform=x86') }
        $failure = $null
        try { & ([scriptblock]::Create($runSuites)) 6>$null | Out-Null } catch { $failure = $_.Exception.Message }
        return @{
            Events  = @($script:events)
            Failure = $failure
            Failed  = Test-Path -LiteralPath (Join-Path $OutputRoot 'worker.failed.txt')
            Skipped = Test-Path -LiteralPath (Join-Path $OutputRoot 'worker.skipped.txt')
            Host    = $env:GXMCP_SHAREDHOST_TESTHOST_EXE
            Gx      = $env:GX_PATH
        }
    }

    $run = Invoke-RunSuites -UseParallel $true -Sdk 'sdk-path' -ExitCodes @{ gateway = 0; worker = 0 }
    $order = $run.Events -join ' | '
    Assert-True ($null -eq $run.Failure -and -not $run.Failed -and -not $run.Skipped) "A clean parallel run must leave no failure or marker: $($run.Failure)"
    Assert-True ($run.Events[0] -ceq 'start:gateway[GXMCP_SHAREDHOST_TESTHOST_EXE=built-host] parent-testhost=[previous-host] parent-gx=[caller-gx]') "The Gateway suite must get the test host and nothing else: $order"
    Assert-True ($run.Events[1] -ceq 'start:worker[GX_PATH=sdk-path] parent-testhost=[previous-host] parent-gx=[caller-gx]') "The Worker suite must get GX_PATH and nothing else: $order"
    Assert-True ((($run.Events | Select-Object -Skip 2) -join ' | ') -ceq 'wait:gateway | wait:worker | complete:worker=0 | complete:gateway=0') "Both suites must be started before either is awaited, and the Worker judged first: $order"
    Assert-True ($run.Host -ceq 'previous-host' -and $run.Gx -ceq 'caller-gx') 'A parallel run must leave the caller environment as the sequential path does.'

    $run = Invoke-RunSuites -UseParallel $true -Sdk 'sdk-path' -ExitCodes @{ gateway = 0; worker = 1 }
    Assert-True ($null -eq $run.Failure -and $run.Failed) 'A Worker failure must be recorded in worker.failed.txt, not thrown.'
    Assert-True ($run.Events -contains 'complete:gateway=0') 'The Gateway suite must still be judged after a Worker failure.'

    $run = Invoke-RunSuites -UseParallel $true -Sdk 'sdk-path' -ExitCodes @{ gateway = 2; worker = 1 }
    Assert-True ($run.Failure -match 'gateway tests failed' -and $run.Failed) "A Gateway failure must stop the script after the Worker verdict was recorded: $($run.Failure)"

    $run = Invoke-RunSuites -UseParallel $true -Sdk '' -ExitCodes @{ gateway = 0; worker = 0 }
    Assert-True ((($run.Events) -join ' | ') -ceq 'invoke:gateway testhost=[built-host] gx=[caller-gx]' -and $run.Skipped) "Without the SDK -Parallel must behave as the sequential path: $(($run.Events) -join ' | ')"

    $run = Invoke-RunSuites -UseParallel $false -Sdk 'sdk-path' -ExitCodes @{ gateway = 0; worker = 0 }
    Assert-True ((($run.Events) -join ' | ') -ceq 'invoke:gateway testhost=[built-host] gx=[caller-gx] | invoke:worker testhost=[previous-host] gx=[sdk-path]') "Without -Parallel the suites must run one after the other as before: $(($run.Events) -join ' | ')"
    Assert-True ($run.Host -ceq 'previous-host' -and $run.Gx -ceq 'caller-gx' -and -not $run.Failed) 'The sequential path must restore the caller environment.'

    $run = Invoke-RunSuites -UseParallel $false -Sdk 'sdk-path' -ExitCodes @{ gateway = 0; worker = 1 }
    Assert-True ($null -eq $run.Failure -and $run.Failed -and $run.Gx -ceq 'caller-gx') 'The sequential path must still record a Worker failure and restore GX_PATH.'
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'coverage-parallel: overlap, per-suite environment and verdict checks passed' -ForegroundColor Green
