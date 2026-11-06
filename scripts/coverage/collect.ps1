param(
    [string]$OutputRoot = "",
    [string]$GxPath = "",
    # Required for an output directory outside artifacts and the runner's temporary roots.
    [string]$AllowedOutputRoot = ""
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

function Invoke-CoverageTest {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$BaseOutputPath,
        [Parameter(Mandatory = $true)][string]$SettingsPath,
        [string[]]$BuildProperties = @()
    )

    $resultsDir = Join-Path $OutputRoot $Label
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

    Write-Host "Running $Label tests with coverage..."
    $testArguments = @(
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
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label tests failed."
    }

    $coverage = Get-ChildItem -Path $resultsDir -Recurse -Filter coverage.cobertura.xml -File |
        Select-Object -First 1

    if (-not $coverage) {
        throw "Coverage report not found for $Label."
    }

    Copy-Item -LiteralPath $coverage.FullName -Destination (Join-Path $OutputRoot "$Label.cobertura.xml") -Force
    Copy-Item -LiteralPath (Join-Path $resultsDir "$Label.trx") -Destination (Join-Path $OutputRoot "$Label.trx") -Force
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
try {
    Invoke-CoverageTest `
        -ProjectPath (Join-Path $repoRoot "src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj") `
        -Label "gateway" `
        -BaseOutputPath (Get-BaseOutputPath -Label "gateway") `
        -SettingsPath (Join-Path $PSScriptRoot "gateway.runsettings")
} finally {
    $env:GXMCP_SHAREDHOST_TESTHOST_EXE = $previousTestHostExe
}

$previousGxPath = $env:GX_PATH
if ($resolvedGxPath) {
    Write-Host "Using GeneXus SDK from '$resolvedGxPath'."
    $env:GX_PATH = $resolvedGxPath
    try {
        Invoke-CoverageTest `
            -ProjectPath (Join-Path $repoRoot "src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj") `
            -Label "worker" `
            -BaseOutputPath (Get-BaseOutputPath -Label "worker") `
            -SettingsPath (Join-Path $PSScriptRoot "worker.runsettings") `
            -BuildProperties @("-p:Platform=x86", "-p:GX_PATH=$resolvedGxPath")
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

Get-ChildItem -Path $OutputRoot -Recurse | ForEach-Object {
    Write-Host $_.FullName
}
