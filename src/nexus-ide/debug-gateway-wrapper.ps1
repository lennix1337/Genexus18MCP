param(
    [string]$ConfigPath,
    [int]$Port
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\\..")
$gatewayDir = Join-Path $repoRoot "src\\GxMcp.Gateway\\bin\\Debug\\net10.0-windows"
$gatewayExe = Join-Path $gatewayDir "GxMcp.Gateway.exe"
$configPath = if ($ConfigPath) { $ConfigPath } elseif ($env:GX_CONFIG_PATH) { $env:GX_CONFIG_PATH } else { Join-Path $repoRoot "config.json" }

function Resolve-CanonicalPort {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return 5000
    }

    try {
        $config = Get-Content -Path $Path -Raw | ConvertFrom-Json
        $httpPort = $config.Server.HttpPort
        if ($httpPort -is [int] -and $httpPort -gt 0) {
            return $httpPort
        }
        if ($httpPort -and [int]::TryParse([string]$httpPort, [ref]([int]$parsed = 0))) {
            return $parsed
        }
    } catch {}

    return 5000
}

$port = if ($Port -gt 0) { $Port } elseif ($env:GX_MCP_PORT) { [int]$env:GX_MCP_PORT } else { Resolve-CanonicalPort $configPath }

function Get-GatewayRuntimeProcess {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match 'GxMcp\.Gateway\.dll') -or
            $_.Name -ieq 'GxMcp.Gateway.exe'
        }
}

$env:GX_CONFIG_PATH = [string]$configPath
$env:GX_MCP_PORT = [string]$port
$env:GX_MCP_STDIO = "false"
Set-Location $gatewayDir
& $gatewayExe
exit $LASTEXITCODE
