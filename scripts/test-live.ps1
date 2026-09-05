[CmdletBinding()]
param(
    [string]$KbPath,

    [string]$GxPath,

    [switch]$GatewayOnly,

    [switch]$RunBenchmark,

    [string]$BenchmarkBaseline,

    [string]$BenchmarkOut,

    [ValidateRange(1024, 65535)]
    [int]$HttpPort,

    [switch]$SkipBuild,

    [ValidateRange(1, 100)]
    [int]$Iterations = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Fail-Live([string]$Message) {
    Write-Error "Live gate failed: $Message"
    exit 1
}

function Get-FreeHttpPort {
    foreach ($candidate in 55100..55199) {
        if (-not (Get-NetTCPConnection -LocalPort $candidate -State Listen -ErrorAction SilentlyContinue)) {
            return $candidate
        }
    }
    Fail-Live 'No free isolated HTTP port was found in 55100..55199.'
}

function Stop-BenchmarkGateway([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit(5000)
        }
    }
    catch { }
    finally {
        $Process.Dispose()
    }

    Get-CimInstance Win32_Process -Filter "Name = 'GxMcp.Worker.exe'" |
        Where-Object { $_.ExecutablePath -like "$root\publish\worker\*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if ([string]::IsNullOrWhiteSpace($KbPath)) {
    $KbPath = if (-not [string]::IsNullOrWhiteSpace($env:GXMCP_TEST_KB)) {
        $env:GXMCP_TEST_KB
    } else {
        'C:\KBs\KBTeste'
    }
}
if (-not (Test-Path -LiteralPath $KbPath -PathType Container)) {
    Fail-Live "KB directory not found: $KbPath"
}
$KbPath = (Resolve-Path -LiteralPath $KbPath).Path

if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        $env:GX_PATH
    } else {
        'C:\Program Files (x86)\GeneXus\GeneXus18'
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $GxPath 'Artech.Architecture.Common.dll') -PathType Leaf)) {
    Fail-Live "GeneXus 18 SDK not found under '$GxPath'. Set -GxPath or GX_PATH."
}

$env:GXMCP_TEST_KB = $KbPath
$env:GX_PATH = $GxPath
$HttpPort = if ($HttpPort -gt 0) { $HttpPort } else { Get-FreeHttpPort }
$env:GX_MCP_PORT = $HttpPort.ToString()
$env:GX_MCP_STDIO = 'true'

Write-Host "Live KB: $KbPath" -ForegroundColor Cyan
Write-Host "GeneXus SDK: $GxPath" -ForegroundColor Cyan
Write-Host "Isolated HTTP port: $HttpPort" -ForegroundColor Cyan

if (-not $SkipBuild) {
    Write-Host "`n>>> Building current Gateway/Worker artifact" -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        Fail-Live "Current artifact build failed with exit code $LASTEXITCODE."
    }
}

$gatewayExe = Join-Path $root 'publish\GxMcp.Gateway.exe'
$sourceSchema = Join-Path $root 'src\GxMcp.Gateway\tool_definitions.json'
$publishedSchema = Join-Path $root 'publish\tool_definitions.json'
if (-not (Test-Path -LiteralPath $gatewayExe -PathType Leaf)) {
    Fail-Live "Published Gateway not found at '$gatewayExe'."
}
if (-not (Test-Path -LiteralPath $publishedSchema -PathType Leaf)) {
    Fail-Live "Published tool schema not found at '$publishedSchema'."
}
if ((Get-FileHash -LiteralPath $sourceSchema -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $publishedSchema -Algorithm SHA256).Hash) {
    Fail-Live "Published tool schema is stale. Run without -SkipBuild to rebuild the current artifact."
}

$versionMatch = Select-String -LiteralPath (Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj') -Pattern '<Version>([^<]+)</Version>'
$sourceVersion = if ($versionMatch) { $versionMatch.Matches[0].Groups[1].Value } else { $null }
$publishedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($gatewayExe.Replace('.exe', '.dll')).Version.ToString(3)
if (-not [string]::IsNullOrWhiteSpace($sourceVersion) -and $sourceVersion -ne $publishedVersion) {
    Fail-Live "Published Gateway version $publishedVersion does not match source version $sourceVersion. Run without -SkipBuild."
}

$gatewayProject = Join-Path $root 'src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj'
Write-Host "`n>>> Gateway live smoke" -ForegroundColor Cyan
& dotnet test $gatewayProject --no-restore --nologo -v:minimal --filter 'Category=LiveE2E'
if ($LASTEXITCODE -ne 0) {
    Fail-Live "Gateway live smoke failed with exit code $LASTEXITCODE."
}

if (-not $GatewayOnly) {
    $workerProject = Join-Path $root 'src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj'
    Write-Host "`n>>> Worker SDK live check" -ForegroundColor Cyan
    & dotnet test $workerProject --no-restore --nologo -v:minimal --filter 'FullyQualifiedName~InProcessBuildRunnerTests.TryResolveTypes_finds_GeneXus_tasks_when_SDK_installed'
    if ($LASTEXITCODE -ne 0) {
        Fail-Live "Worker SDK live check failed with exit code $LASTEXITCODE."
    }
}

if ($RunBenchmark) {
    $benchmark = Join-Path $root 'scripts\bench-live-http.py'
    if (-not (Test-Path -LiteralPath $benchmark -PathType Leaf)) {
        Fail-Live "Benchmark harness not found at '$benchmark'."
    }
    if ([string]::IsNullOrWhiteSpace($BenchmarkOut)) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $BenchmarkOut = Join-Path $env:TEMP "gxmcp-live-benchmark-$stamp.json"
    }

    $benchmarkArgs = @(
        $benchmark,
        '--kb', $KbPath,
        '--iterations', $Iterations.ToString(),
        '--port', $HttpPort.ToString(),
        '--ops', 'whoami,list_objects,query,search_source,inspect,read,lifecycle_status',
        '--out', $BenchmarkOut,
        '--name', 'quality-gate'
    )
    if (-not [string]::IsNullOrWhiteSpace($BenchmarkBaseline)) {
        if (-not (Test-Path -LiteralPath $BenchmarkBaseline -PathType Leaf)) {
            Fail-Live "Benchmark baseline not found: $BenchmarkBaseline"
        }
        $benchmarkArgs += @('--compare', $BenchmarkBaseline, '--fail-on-regression')
    }

    Write-Host "`n>>> Live performance benchmark" -ForegroundColor Cyan
    $benchmarkGateway = $null
    try {
        $gatewayStart = [System.Diagnostics.ProcessStartInfo]::new()
        $gatewayStart.FileName = $gatewayExe
        $gatewayStart.WorkingDirectory = Split-Path -Parent $gatewayExe
        $gatewayStart.UseShellExecute = $false
        $gatewayStart.CreateNoWindow = $true
        $gatewayStart.EnvironmentVariables['GX_MCP_PORT'] = $HttpPort.ToString()
        $gatewayStart.EnvironmentVariables['GX_MCP_STDIO'] = 'false'
        $gatewayStart.EnvironmentVariables['GXMCP_TEST_KB'] = $KbPath
        $gatewayStart.EnvironmentVariables['GX_PATH'] = $GxPath
        $benchmarkGateway = [System.Diagnostics.Process]::Start($gatewayStart)

        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($benchmarkGateway.HasExited) {
                Fail-Live "Benchmark Gateway exited before binding isolated port $HttpPort (exit code $($benchmarkGateway.ExitCode))."
            }
            if (Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            Fail-Live "Benchmark Gateway did not bind isolated port $HttpPort within 30 seconds."
        }

        & python @benchmarkArgs
        if ($LASTEXITCODE -ne 0) {
            Fail-Live "Live performance benchmark failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Stop-BenchmarkGateway $benchmarkGateway
    }
    Write-Host "Benchmark output: $BenchmarkOut" -ForegroundColor Green
}

Write-Host "`nLive gate completed." -ForegroundColor Green
