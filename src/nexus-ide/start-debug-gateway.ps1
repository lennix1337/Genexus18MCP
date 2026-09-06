param(
    [string]$ConfigPath,
    [int]$Port
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\\..")
$gatewayDir = Join-Path $repoRoot "src\\GxMcp.Gateway\\bin\\Debug\\net10.0-windows"
$gatewayExe = Join-Path $gatewayDir "GxMcp.Gateway.exe"
$configPath = if ($ConfigPath) { $ConfigPath } elseif ($env:GX_CONFIG_PATH) { $env:GX_CONFIG_PATH } else { Join-Path $repoRoot "config.json" }
$gatewayLogPath = Join-Path $gatewayDir "gateway_debug.log"
$gatewayPrevLogPath = Join-Path $gatewayDir "gateway_debug.prev.log"
$workerLogPath = Join-Path $repoRoot "src\\GxMcp.Worker\\bin\\Debug\\worker_debug.log"
$workerPrevLogPath = Join-Path $repoRoot "src\\GxMcp.Worker\\bin\\Debug\\worker_debug.prev.log"

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

function Rotate-LogFile {
    param(
        [string]$CurrentPath,
        [string]$PreviousPath
    )

    if (-not (Test-Path $CurrentPath)) {
        return
    }

    try {
        $item = Get-Item $CurrentPath -ErrorAction Stop
        if ($item.Length -le 0) {
            Clear-Content $CurrentPath -ErrorAction SilentlyContinue
            return
        }

        if (Test-Path $PreviousPath) {
            Remove-Item $PreviousPath -Force -ErrorAction SilentlyContinue
        }

        Move-Item $CurrentPath $PreviousPath -Force -ErrorAction Stop
    } catch {}
}

function Get-GatewayRuntimeProcess {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match 'GxMcp\.Gateway\.dll') -or
            $_.Name -ieq 'GxMcp.Gateway.exe'
        }
}

if (-not (Test-Path $gatewayExe)) {
    throw "Debug gateway not found at $gatewayExe"
}

if (-not (Test-Path $configPath)) {
    throw "Canonical config not found at $configPath"
}

Get-ChildItem -Path $repoRoot -Force -Recurse -Filter ".mcp_config.json" -ErrorAction SilentlyContinue |
    ForEach-Object {
        try {
            Remove-Item $_.FullName -Force -ErrorAction Stop
        } catch {}
    }

Rotate-LogFile -CurrentPath $gatewayLogPath -PreviousPath $gatewayPrevLogPath
Rotate-LogFile -CurrentPath $workerLogPath -PreviousPath $workerPrevLogPath

$env:GX_CONFIG_PATH = [string]$configPath
$env:GX_MCP_PORT = [string]$port
$env:GX_MCP_STDIO = "false"
Write-Host "DEBUG_GATEWAY_STARTING"

$runtime = Get-GatewayRuntimeProcess
$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
$startedProcess = $null

if (-not $runtime -and -not $listener) {
    try {
        $startedProcess = Start-Process -FilePath $gatewayExe -WorkingDirectory $gatewayDir -WindowStyle Hidden -PassThru
    } catch {
        throw "Failed to launch debug gateway runtime. $($_.Exception.Message)"
    }
}

for ($attempt = 1; $attempt -le 240; $attempt++) {
    Start-Sleep -Milliseconds 500

    $runtime = Get-GatewayRuntimeProcess
    $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue

    if ($startedProcess) {
        try {
            $startedProcess.Refresh()
            if ($startedProcess.HasExited) {
                throw "Debug gateway runtime exited during bootstrap with code $($startedProcess.ExitCode)."
            }
        } catch {
            throw "$($_.Exception.Message) Check $gatewayLogPath"
        }
    }

    if (-not $runtime -and -not $listener -and -not $startedProcess -and $attempt -gt 10) {
        throw "Debug gateway runtime process is not running."
    }

    if (Test-Path $gatewayLogPath) {
        if (Select-String -Path $gatewayLogPath -Pattern "\\[HTTP\\] Fatal error|FATAL UNHANDLED EXCEPTION" -Quiet) {
            throw "Gateway failed during bootstrap. Check $gatewayLogPath"
        }
    }

    if (Test-Path $workerLogPath) {
        $workerText = [System.IO.File]::ReadAllText($workerLogPath)

        if ($workerText.Contains("Worker SDK ready.")) {
            Write-Host "DEBUG_GATEWAY_READY"
            $runtimeId = ($runtime | Select-Object -First 1 -ExpandProperty ProcessId)
            if (-not $runtimeId -and $startedProcess) {
                $runtimeId = $startedProcess.Id
            }
            Write-Host "Debug gateway ready on port $port (PID $runtimeId)."
            exit 0
        }

        if ($workerText -match "CRITICAL Init Error|Main FATAL|Worker failed to auto-open KB") {
            throw "Worker failed during bootstrap. Check $workerLogPath"
        }
    }
}

throw "Debug gateway did not become ready on port $port. Check $workerLogPath and $gatewayLogPath."
