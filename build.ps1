# GeneXus MCP - Build & Deploy Script
# ==========================================

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"
$root = $PSScriptRoot
$publishDir = Join-Path $root "publish"
$gatewayProject = Join-Path $root "src\GxMcp.Gateway\GxMcp.Gateway.csproj"
$workerProject = Join-Path $root "src\GxMcp.Worker\GxMcp.Worker.csproj"

function Fail-Build([string]$message, [int]$exitCode = 1) {
    Write-Host "[build] $message" -ForegroundColor Red
    exit $exitCode
}

function Invoke-DotNet([string[]]$arguments, [string]$failureMessage) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        Fail-Build $failureMessage $LASTEXITCODE
    }
}

Write-Host "[build] Preparing build..." -ForegroundColor Cyan

# 0. Stop only processes launched from this checkout. Other checkouts and Hub
# profiles may be serving active tasks and must not be interrupted by a build.
Write-Host "   > Stopping running processes from this checkout..."
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -in @('GxMcp.Worker.exe', 'GxMcp.Gateway.exe') -and
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# Verify prerequisites
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
        Fail-Build ".NET SDK was not found in PATH. Install .NET 10 SDK before running build.ps1."
}

$dotnetVersion = dotnet --version
Write-Host "   > Found .NET SDK: $dotnetVersion" -ForegroundColor Gray

# 0.1 Restore Dependencies
Write-Host "   > Restoring Gateway dependencies..."
Invoke-DotNet @("restore", $gatewayProject) "Gateway restore failed."

Write-Host "   > Restoring Worker dependencies..."
Invoke-DotNet @("restore", $workerProject) "Worker restore failed."

# Resolve GeneXus Path
$gxPath = "C:\Program Files (x86)\GeneXus\GeneXus18"
if (Test-Path (Join-Path $root "config.json")) {
    $configData = Get-Content (Join-Path $root "config.json") -Raw | ConvertFrom-Json
    if ($configData.GeneXus -and $configData.GeneXus.InstallationPath) {
        $gxPath = $configData.GeneXus.InstallationPath
    }
}
if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
    # An explicit environment value is the build-time source of truth. This
    # keeps clean worktrees and CI jobs independent from a local config.json.
    $gxPath = $env:GX_PATH
}

if (-not (Test-Path $gatewayProject)) {
    Fail-Build "Gateway project file was not found at $gatewayProject."
}

if (-not (Test-Path $workerProject)) {
    Fail-Build "Worker project file was not found at $workerProject."
}

if (-not (Test-Path $gxPath)) {
    Fail-Build "GeneXus installation path was not found: $gxPath."
}

if (-not (Test-Path (Join-Path $gxPath "Definitions"))) {
    Fail-Build "GeneXus Definitions folder was not found under $gxPath."
}

# Also stop dotnet processes running this checkout's Gateway DLL.
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
        $_.CommandLine -like "*GxMcp.Gateway.dll*" -and
        $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0
    } |
    ForEach-Object {
        Write-Host "     - Stopping dotnet process ($($_.ProcessId))..."
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

Start-Sleep -Seconds 1

$ErrorActionPreference = "Stop"

# 1. Clean Publish Directory
if (Test-Path $publishDir) {
    Write-Host "   > Cleaning publish directory..."
    Get-ChildItem -Path "$publishDir\*" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -Path $publishDir -ItemType Directory | Out-Null
}

Write-Host "[build] Building solutions..." -ForegroundColor Cyan

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $numericVersion = ([regex]::Match($Version, '^\d+\.\d+\.\d+')).Value
    $versionArguments = @(
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )
    Write-Host "   > Stamping build version: $Version" -ForegroundColor Gray
}

# 2. Build Gateway (.NET 10)
Write-Host "   > Building Gateway (Release)..."
$tempGw = Join-Path $publishDir "temp_gw"
Invoke-DotNet (@("publish", $gatewayProject, "-c", "Release", "--nologo", "-o", $tempGw) + $versionArguments) "Gateway publish failed."

if (Test-Path $tempGw) {
    Copy-Item "$tempGw\*" "$publishDir" -Force -Recurse
    Remove-Item $tempGw -Recurse -Force
}

Write-Host "   > Building Gateway (Debug)..."
Invoke-DotNet (@("build", $gatewayProject, "-c", "Debug", "--nologo") + $versionArguments) "Gateway debug build failed."

# 3. Build Worker (.NET Framework 4.8)
Write-Host "   > Building Worker (Release)..."
Invoke-DotNet (@("build", $workerProject, "-c", "Release", "--nologo", "-p:GX_PATH=$gxPath") + $versionArguments) "Worker build failed."

Write-Host "   > Building Worker (Debug)..."
Invoke-DotNet (@("build", $workerProject, "-c", "Debug", "--nologo", "-p:GX_PATH=$gxPath") + $versionArguments) "Worker debug build failed."

# 4. Copy Worker Binaries to Publish
$workerPublishDir = Join-Path $publishDir "worker"
if (-not (Test-Path $workerPublishDir)) {
    New-Item -Path $workerPublishDir -ItemType Directory | Out-Null
}

$workerBinRelease = Join-Path $root "src\GxMcp.Worker\bin\Release"
if (-not (Test-Path $workerBinRelease)) {
    $workerBinRelease = Join-Path $root "src\GxMcp.Worker\bin\x86\Release"
}

if (Test-Path $workerBinRelease) {
    Write-Host "   > Deploying Release Worker binaries to $workerPublishDir..."
    Get-ChildItem -Path "$workerBinRelease\*" -Recurse | Copy-Item -Destination "$workerPublishDir" -Recurse -Force
}

# 4.1 GeneXus Definitions/ - NOT copied into the artifact.
# The Worker sets Directory.SetCurrentDirectory(gxPath) before calling any SDK
# methods, so the SDK resolves Definitions/ from the local GeneXus 18 install
# (the same path it was loaded from). Shipping a copy would:
#   (a) add ~20 MB of GeneXus proprietary XML to the public npm tarball, and
#   (b) risk stale copies diverging from the user's actual GeneXus version.
# Every install of genexus-mcp already requires a local GeneXus 18 install, so
# the SDK always finds the canonical Definitions/ at runtime without a copy here.
Write-Host "   > Skipping Definitions/ copy - resolved at runtime from GeneXus install dir ($gxPath)."

# 5. Write a SANITIZED fallback config.json into the publish artifact.
#    This file is only a fallback for a bare manual run (every real launcher sets
#    GX_CONFIG_PATH to the KB's own config). We deliberately do NOT sync the dev's
#    root config.json here - that would ship the developer's real KB path in the
#    release zip (a privacy/hygiene leak). The placeholder KBPath signals "set me".
Write-Host "   > Writing sanitized fallback config.json to publish..."
$defaultConfig = @{
    GeneXus = @{
        InstallationPath = "C:\\Program Files (x86)\\GeneXus\\GeneXus18"
        WorkerExecutable = "worker\\GxMcp.Worker.exe"
    }
    Server = @{
        HttpPort = 5000
        McpStdio = $true
    }
    Logging = @{
        Level = "Debug"
        Path = "logs"
    }
    Environment = @{
        KBPath = "C:\\KBs\\YourKB"
    }
} | ConvertTo-Json -Depth 4
Set-Content "$publishDir\config.json" $defaultConfig

# 6. Generate start_mcp.bat
Write-Host "   > Generating start_mcp.bat..."
# IMPORTANT: use %~dp0-relative paths so the bat works from any install location.
# GX_CONFIG_PATH is set only when not already defined, so callers that pass their
# own KB-specific config (e.g. the MCP client launcher) are not overridden.
$batContent = @'
@echo off
setlocal

rem Resolve the directory containing this bat file (works at any install path).
set "BAT_DIR=%~dp0"
rem Remove trailing backslash so paths join cleanly.
if "%BAT_DIR:~-1%"=="\" set "BAT_DIR=%BAT_DIR:~0,-1%"

rem Only set GX_CONFIG_PATH when the caller hasn't provided one.
if not defined GX_CONFIG_PATH (
  set "GX_CONFIG_PATH=%BAT_DIR%\config.json"
)

set "GX_MCP_STDIO=true"

rem Launch the gateway exe that lives alongside this bat file.
set "GATEWAY_EXE=%BAT_DIR%\GxMcp.Gateway.exe"
if exist "%GATEWAY_EXE%" (
  "%GATEWAY_EXE%"
  exit /b %ERRORLEVEL%
)

rem Fallback: run the DLL via dotnet (dev layout where .exe is not present).
cd /d "%BAT_DIR%"
dotnet GxMcp.Gateway.dll
'@
Set-Content -Path "$publishDir\start_mcp.bat" -Value $batContent -Encoding Ascii

# 6.1 Slim the publish output: drop debug symbols (.pdb - not shipped) and any
# transient runtime logs/cache a dev run may have written into the dir.
Get-ChildItem -Path $publishDir -Recurse -Include *.pdb,*.log,*.prev.log,*panic*.log -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path "$publishDir\worker\cache") {
    Remove-Item "$publishDir\worker\cache" -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path "$publishDir\worker\search_index.json") {
    Remove-Item "$publishDir\worker\search_index.json" -Force -ErrorAction SilentlyContinue
}
if (Test-Path "$publishDir\worker\DataTracing.log") {
    Remove-Item "$publishDir\worker\DataTracing.log" -Force -ErrorAction SilentlyContinue
}

# 6.2 Generate a self-describing release manifest from the exact files that
# will be shipped. The installer and CI validate these hashes before a v3
# archive can replace an existing installation.
Write-Host "   > Writing release manifest..."
$manifestScript = Join-Path $root "scripts\write-release-manifest.ps1"
$manifestVersion = if (-not [string]::IsNullOrWhiteSpace($Version)) { $Version } else { '2.57.0' }
$manifestCsproj = Get-Content -LiteralPath $gatewayProject -Raw
if ([string]::IsNullOrWhiteSpace($Version) -and $manifestCsproj -match '<Version>([^<]+)</Version>') {
    $manifestVersion = $Matches[1]
}
& pwsh -NoProfile -File $manifestScript `
    -PublishDirectory $publishDir `
    -Version $manifestVersion `
    -SourceRoot $root | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail-Build "Release manifest generation failed."
}

Write-Host "`n[build] Build complete." -ForegroundColor Green
Write-Host "   - Output: $publishDir"
Write-Host "   - Worker: $publishDir\worker\GxMcp.Worker.exe"
Write-Host "   - Gateway: $publishDir\GxMcp.Gateway.exe"

# 7. Deploy to Extension Backend (for live development)
$extBackendDir = Join-Path $root "src\nexus-ide\backend"
Write-Host "`n[build] Deploying to extension backend: $extBackendDir" -ForegroundColor Cyan
if (-not (Test-Path $extBackendDir)) {
    New-Item -Path $extBackendDir -ItemType Directory | Out-Null
}
Copy-Item "$publishDir\*" -Destination "$extBackendDir" -Recurse -Force
