param(
    [string]$OutputRoot = "",
    [string]$GxPath = ""
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

Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
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
        "--results-directory", $resultsDir
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

Invoke-CoverageTest `
    -ProjectPath (Join-Path $repoRoot "src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj") `
    -Label "gateway" `
    -BaseOutputPath (Get-BaseOutputPath -Label "gateway") `
    -SettingsPath (Join-Path $PSScriptRoot "gateway.runsettings")

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
