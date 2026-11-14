param(
    [string]$OutputRoot = "",
    [string]$GxPath = "",
    # Required for an output directory outside artifacts and the runner's temporary roots.
    [string]$AllowedOutputRoot = "",
    # Opt-in: run the Gateway and the Worker suites at the same time instead of one
    # after the other. They are separate test processes with separate build outputs
    # and reports, so a machine with the GeneXus SDK waits for the slower suite, not
    # for both. Without the SDK the Worker suite is skipped and this changes nothing.
    # The Gateway per-test durations are then measured with the Worker suite running.
    [switch]$Parallel
)

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repoRoot 'scripts\gx-version-catalog.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $repoRoot
$defaultGxPath = Get-GxPrimaryInstallPath -Catalog $gxCatalog

function Resolve-GxSdkPath {
    $configuredPath = if (-not [string]::IsNullOrWhiteSpace($GxPath)) {
        $GxPath
    } elseif (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        $env:GX_PATH
    } else {
        ""
    }

    if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
        $resolvedPath = [System.IO.Path]::GetFullPath($configuredPath.Trim().Trim('"'))
        $sdkAssembly = Join-Path $resolvedPath "Artech.Architecture.Common.dll"
        if (-not (Test-Path -LiteralPath $sdkAssembly -PathType Leaf)) {
            throw "GeneXus SDK not found under configured GX_PATH '$resolvedPath'. Expected '$sdkAssembly'."
        }
        return $resolvedPath
    }

    $defaultAssembly = Join-Path $defaultGxPath "Artech.Architecture.Common.dll"
    if (Test-Path -LiteralPath $defaultAssembly -PathType Leaf) {
        return $defaultGxPath
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    if ($env:RUNNER_TEMP) {
        $OutputRoot = Join-Path $env:RUNNER_TEMP "gx-coverage-artifacts"
    } else {
        $OutputRoot = Join-Path $repoRoot "artifacts\coverage"
    }
}

. (Join-Path $repoRoot 'scripts/coverage/output-path.ps1')
$OutputRoot = Resolve-CoverageOutputDirectory -OutputRoot $OutputRoot -RepoRoot $repoRoot -AllowedOutputRoot $AllowedOutputRoot
if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction Stop
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

# Ensure gxwire binary is available in publish for gateway integration tests
$publishDir = Join-Path $repoRoot "publish"
$gxwirePublish = Join-Path $publishDir "gxwire.exe"
$gxwireRelease = Join-Path $repoRoot "src\gxwire\target\release\gxwire.exe"
if (-not (Test-Path -LiteralPath $gxwirePublish)) {
    if (Test-Path -LiteralPath $gxwireRelease) {
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        Copy-Item -LiteralPath $gxwireRelease -Destination $gxwirePublish -Force
    } elseif (Get-Command cargo -ErrorAction SilentlyContinue) {
        cargo build --release --manifest-path (Join-Path $repoRoot "src\gxwire\Cargo.toml")
        if (Test-Path -LiteralPath $gxwireRelease) {
            New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
            Copy-Item -LiteralPath $gxwireRelease -Destination $gxwirePublish -Force
        }
    }
}

function Get-CoverageTestArguments {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$BaseOutputPath,
        [Parameter(Mandatory = $true)][string]$SettingsPath,
        [string[]]$BuildProperties = @()
    )

    $resultsDir = Join-Path $OutputRoot $Label
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

    return @(
        "test",
        $ProjectPath,
        "-v", "minimal",
        "-p:BaseOutputPath=$BaseOutputPath"
    ) + $BuildProperties + @(
        "--collect", "XPlat Code Coverage",
        "--settings", $SettingsPath,
        "--results-directory", $resultsDir,
        # Per-test durations for scripts/ci/assert-test-durations.ps1 (CI time budget).
        "--logger", "trx;LogFileName=$Label.trx"
    )
}

# Judges a finished suite and publishes its report next to the other one.
function Complete-CoverageTest {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][int]$ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Label tests failed."
    }

    $resultsDir = Join-Path $OutputRoot $Label
    $coverage = Get-ChildItem -Path $resultsDir -Recurse -Filter coverage.cobertura.xml -File |
        Select-Object -First 1

    if (-not $coverage) {
        throw "Coverage report not found for $Label."
    }

    Copy-Item -LiteralPath $coverage.FullName -Destination (Join-Path $OutputRoot "$Label.cobertura.xml") -Force
    Copy-Item -LiteralPath (Join-Path $resultsDir "$Label.trx") -Destination (Join-Path $OutputRoot "$Label.trx") -Force
}

function Invoke-CoverageTest {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$BaseOutputPath,
        [Parameter(Mandatory = $true)][string]$SettingsPath,
        [string[]]$BuildProperties = @()
    )

    Write-Host "Running $Label tests with coverage..."
    $testArguments = Get-CoverageTestArguments @PSBoundParameters
    & dotnet @testArguments
    Complete-CoverageTest -Label $Label -ExitCode $LASTEXITCODE
}

# -Parallel: starts one suite as its own process. The environment overrides apply
# to that process only; the output is collected and printed when the suite ends,
# so the two logs do not interleave. -FileName is a test seam.
function Start-CoverageTest {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [hashtable]$Environment = @{},
        [string]$FileName = "dotnet"
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FileName)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.WorkingDirectory = (Get-Location).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in $Environment.Keys) {
        if ($null -eq $Environment[$name]) {
            $null = $startInfo.Environment.Remove($name)
        } else {
            $startInfo.Environment[$name] = [string]$Environment[$name]
        }
    }

    Write-Host "Running $Label tests with coverage (in parallel)..."
    $process = [System.Diagnostics.Process]::Start($startInfo)
    return @{
        Label   = $Label
        Process = $process
        Output  = $process.StandardOutput.ReadToEndAsync()
        Error   = $process.StandardError.ReadToEndAsync()
        Watch   = [System.Diagnostics.Stopwatch]::StartNew()
    }
}

# Waits for a suite started by Start-CoverageTest, prints its log and keeps a copy
# beside the reports. Returns the exit code of the test process.
function Wait-CoverageTest {
    param([Parameter(Mandatory = $true)][hashtable]$Run)

    $Run.Process.WaitForExit()
    $log = $Run.Output.GetAwaiter().GetResult() + $Run.Error.GetAwaiter().GetResult()
    $Run.Watch.Stop()
    [System.IO.File]::WriteAllText((Join-Path $OutputRoot "$($Run.Label).console.log"), $log)
    Write-Host "----- $($Run.Label) tests, $([int]$Run.Watch.Elapsed.TotalSeconds)s -----"
    Write-Host $log.TrimEnd()
    return $Run.Process.ExitCode
}

function Get-BaseOutputPath {
    param([Parameter(Mandatory = $true)][string]$Label)

    $baseOutput = Join-Path (Join-Path $repoRoot ".test-bin") $Label
    if (-not $baseOutput.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseOutput += [System.IO.Path]::DirectorySeparatorChar
    }
    return $baseOutput
}

$resolvedGxPath = Resolve-GxSdkPath

# The shared-host process smoke tests start this SDK-free test host. Build it so they
# run here instead of returning early, and point them at exactly this build.
$testHostProject = Join-Path $repoRoot "src\GxMcp.SharedHost.TestHost\GxMcp.SharedHost.TestHost.csproj"
& dotnet build $testHostProject -c Debug -v minimal --nologo
if ($LASTEXITCODE -ne 0) {
    throw "GxMcp.SharedHost.TestHost build failed."
}
$previousTestHostExe = $env:GXMCP_SHAREDHOST_TESTHOST_EXE
$env:GXMCP_SHAREDHOST_TESTHOST_EXE = Join-Path $repoRoot "src\GxMcp.SharedHost.TestHost\bin\Debug\GxMcp.SharedHost.TestHost.exe"

# On a fresh runner the first start of an executable reads it from a cold disk, which has cost
# the process smoke tests 9-15s against their 10s per-test budget. Pay that once here, timed.
foreach ($warmUp in @(
        @{ Name = "node"; File = "node"; Arguments = @("-e", "0") },
        @{ Name = "ping"; File = "ping.exe"; Arguments = @("-n", "1", "127.0.0.1") },
        # --fake-script without a value exits at once (code 2) instead of serving stdin.
        @{ Name = "shared-host test host"; File = $env:GXMCP_SHAREDHOST_TESTHOST_EXE; Arguments = @("--fake-script") })) {
    if (-not (Get-Command $warmUp.File -ErrorAction SilentlyContinue)) { continue }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $warmUp.File @($warmUp.Arguments) *> $null
    Write-Host ("Process warm-up, {0}: {1} ms (exit {2})" -f $warmUp.Name, $watch.ElapsedMilliseconds, $LASTEXITCODE)
}
$gatewayTest = @{
    ProjectPath    = (Join-Path $repoRoot "src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj")
    Label          = "gateway"
    BaseOutputPath = (Get-BaseOutputPath -Label "gateway")
    SettingsPath   = (Join-Path $PSScriptRoot "gateway.runsettings")
}
$workerTest = @{
    ProjectPath     = (Join-Path $repoRoot "src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj")
    Label           = "worker"
    BaseOutputPath  = (Get-BaseOutputPath -Label "worker")
    SettingsPath    = (Join-Path $PSScriptRoot "worker.runsettings")
    BuildProperties = @("-p:Platform=x86", "-p:GX_PATH=$resolvedGxPath")
}
$previousGxPath = $env:GX_PATH

# 7. Run the suites.
if ($Parallel -and $resolvedGxPath) {
    # Both at once. Each process gets the environment the sequential path gives its
    # suite: the test host only for the Gateway, GX_PATH only for the Worker.
    $testHostExe = $env:GXMCP_SHAREDHOST_TESTHOST_EXE
    $env:GXMCP_SHAREDHOST_TESTHOST_EXE = $previousTestHostExe
    Write-Host "Using GeneXus SDK from '$resolvedGxPath'."
    $gatewayRun = Start-CoverageTest -Label "gateway" -Arguments (Get-CoverageTestArguments @gatewayTest) `
        -Environment @{ GXMCP_SHAREDHOST_TESTHOST_EXE = $testHostExe }
    $workerRun = Start-CoverageTest -Label "worker" -Arguments (Get-CoverageTestArguments @workerTest) `
        -Environment @{ GX_PATH = $resolvedGxPath }
    $gatewayExit = Wait-CoverageTest -Run $gatewayRun
    $workerExit = Wait-CoverageTest -Run $workerRun

    # Same verdicts as the sequential path: a Worker failure is recorded for
    # assert-threshold.ps1, a Gateway failure stops the script.
    try {
        Complete-CoverageTest -Label "worker" -ExitCode $workerExit
    } catch {
        New-Item -ItemType File -Path (Join-Path $OutputRoot "worker.failed.txt") -Force | Out-Null
        Write-Host $_.Exception.Message -ForegroundColor Yellow
    }
    Complete-CoverageTest -Label "gateway" -ExitCode $gatewayExit
} else {
    try {
        Invoke-CoverageTest @gatewayTest
    } finally {
        $env:GXMCP_SHAREDHOST_TESTHOST_EXE = $previousTestHostExe
    }

    if ($resolvedGxPath) {
        Write-Host "Using GeneXus SDK from '$resolvedGxPath'."
        $env:GX_PATH = $resolvedGxPath
        try {
            Invoke-CoverageTest @workerTest
        } catch {
            New-Item -ItemType File -Path (Join-Path $OutputRoot "worker.failed.txt") -Force | Out-Null
            Write-Host $_.Exception.Message -ForegroundColor Yellow
        } finally {
            $env:GX_PATH = $previousGxPath
        }
    } else {
        New-Item -ItemType File -Path (Join-Path $OutputRoot "worker.skipped.txt") -Force | Out-Null
        Write-Host "GeneXus SDK for primary major $(Get-GxPrimaryMajor -Catalog $gxCatalog) not found. Set GX_PATH or pass -GxPath to include Worker.Tests."
    }
}

# 8. List what was produced.

Get-ChildItem -Path $OutputRoot -Recurse | ForEach-Object {
    Write-Host $_.FullName
}
