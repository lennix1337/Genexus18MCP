# GeneXus 18 MCP Server installer

[CmdletBinding()]
param(
    [string]$KBPath,
    [string]$GeneXusPath,
    # Skip AI client MCP registration (delegated to the genexus-mcp CLI).
    # Replaces the legacy -SkipClaudeConfig / -SkipCodexConfig / -SkipVsCodeMcp
    # switches, which are still accepted as aliases for backward compatibility.
    [Alias('SkipClaudeConfig', 'SkipCodexConfig', 'SkipVsCodeMcp', 'SkipExtensionInstall')]
    [switch]$SkipClientConfig
)

$progressPreference = "SilentlyContinue"

$root = $PSScriptRoot
$configPath = Join-Path $root "config.json"
$publishDir = Join-Path $root "publish"
$startMcpBatPath = Join-Path $publishDir "start_mcp.bat"
$gatewayExePath = Join-Path $publishDir "GxMcp.Gateway.exe"
$cliRunPath = Join-Path $root "cli\run.js"
# AI client MCP registration (Claude Desktop/Code, Antigravity, Gemini CLI,
# Cursor, OpenCode, Codex, VS Code) is delegated to the genexus-mcp CLI, which is
# the single source of truth for agent paths/detection (see cli/lib/config.js).
# This installer no longer writes client configs itself.

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host ">>> $message" -ForegroundColor Cyan
}

function Write-Ok([string]$message) {
    Write-Host "    [OK] $message" -ForegroundColor Green
}

function Write-Warn([string]$message) {
    Write-Host "    [!] $message" -ForegroundColor Yellow
}

function Fail([string]$message) {
    Write-Host ""
    Write-Host "    [ERROR] $message" -ForegroundColor Red
    Write-Host "    Installation halted." -ForegroundColor Red
    exit 1
}

function Check-Prerequisites {
    Write-Step "Checking prerequisites..."

    $missing = New-Object System.Collections.Generic.List[string]

    # .NET 10 SDK (Gateway target; Worker remains .NET Framework 4.8)
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Warn ".NET SDK not found. Gateway build requires .NET 10 SDK."
        Write-Host "    Download from: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Gray
        $missing.Add(".NET 10 SDK")
    } else {
        $version = dotnet --version
        $major = 0
        [void][int]::TryParse(($version -split '\.')[0], [ref]$major)
        if ($major -lt 10) {
            Write-Warn ".NET SDK $version found, but Gateway v3 requires .NET 10 SDK."
            $missing.Add(".NET 10 SDK (found $version)")
        } else {
            Write-Ok ".NET SDK found: $version"
        }
    }

    # Node.js (needed for AI client MCP registration via the genexus-mcp CLI)
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        if (-not $SkipClientConfig) {
            Write-Warn "Node.js not found. AI client registration requires Node.js 22+."
            Write-Host "    Download from: https://nodejs.org/" -ForegroundColor Gray
            Write-Warn "Build will still proceed; pass -SkipClientConfig to register clients manually later."
        } else {
            Write-Warn "Node.js not found, but client registration is being skipped."
        }
    } else {
        $version = node --version
        Write-Ok "Node.js found: $version"
    }

    # MSBuild (optional but good for Worker)
    $msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($msbuild) {
        Write-Ok "MSBuild found."
    }

    if ($missing.Count -gt 0) {
        Fail "Missing required prerequisites: $($missing -join ', ')."
    }
}

function Backup-File([string]$path) {
    if (-not (Test-Path $path)) {
        return
    }

    $timestamp = Get-Date -Format "yyyyMMddHHmmss"
    $backupPath = "$path.$timestamp.bak"
    Copy-Item $path $backupPath -Force
}

function Get-ExistingPathOrPrompt([string]$label, [string]$currentValue) {
    if (-not [string]::IsNullOrWhiteSpace($currentValue) -and (Test-Path $currentValue)) {
        return $currentValue
    }

    # Auto-detect GeneXus 18 from registry if label is "GeneXus installation path".
    # Probes BOTH known key/value shapes (kept in sync with cli/lib/config.js
    # discoverGeneXusFromRegistry): the modern `Artech\GeneXus 18` +
    # `InstallationDirectory`, and the legacy `Artech\GeneXus\18.0` + `InstallPath`.
    # Only accepts a hit whose folder actually contains genexus.exe.
    if ($label -eq "GeneXus installation path") {
        $hives = @("HKLM:\SOFTWARE\WOW6432Node\Artech", "HKLM:\SOFTWARE\Artech", "HKCU:\SOFTWARE\Artech")
        $probes = @(
            @{ Sub = "GeneXus 18";   Value = "InstallationDirectory" },
            @{ Sub = "GeneXus\18.0"; Value = "InstallPath" }
        )
        foreach ($hive in $hives) {
            foreach ($probe in $probes) {
                $keyPath = Join-Path $hive $probe.Sub
                if (-not (Test-Path $keyPath)) { continue }
                $detected = Get-ItemProperty -Path $keyPath -Name $probe.Value -ErrorAction SilentlyContinue
                if (-not $detected) { continue }
                $dir = $detected.$($probe.Value)
                if ($dir -and (Test-Path (Join-Path $dir "genexus.exe"))) {
                    Write-Ok "Auto-detected GeneXus 18 at: $dir"
                    return $dir
                }
            }
        }
    }

    # Default KB path search
    if ($label -eq "Knowledge Base path") {
        $commonKbRoot = Join-Path ([System.Environment]::GetFolderPath("MyDocuments")) "GeneXus Knowledge Bases"
        if (Test-Path $commonKbRoot) {
            Write-Ok "Found common KB root: $commonKbRoot"
            # Maybe list directories? For now, we just suggest it
        }
    }

    while ($true) {
        $promptSuffix = if ([string]::IsNullOrWhiteSpace($currentValue)) { "" } else { " [$currentValue]" }
        $entered = Read-Host "$label$promptSuffix"
        if ([string]::IsNullOrWhiteSpace($entered)) {
            $entered = $currentValue
        }

        if (-not [string]::IsNullOrWhiteSpace($entered)) {
            # Try to resolve relative to root if it starts with '.'
            if ($entered.StartsWith(".")) {
                $entered = Join-Path $PSScriptRoot $entered
            }

            if (Test-Path $entered) {
                return (Resolve-Path $entered).Path
            }
        }

        Write-Warn "Path not found: '$entered'. Please enter a valid path."
    }
}

function Save-JsonFile([string]$path, [object]$value) {
    $json = $value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($path, $json, [System.Text.Encoding]::UTF8)
}

function Resolve-CommandPath([string[]]$names) {
    foreach ($name in $names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    return $null
}

Check-Prerequisites

if (-not (Test-Path $configPath)) {
    Write-Warn "config.json not found at $configPath. Creating from template..."
    $defaultConfig = @{
        GeneXus = @{
            InstallationPath = "C:\\Program Files (x86)\\GeneXus\\GeneXus18"
            WorkerExecutable = "$publishDir\\worker\\GxMcp.Worker.exe"
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
    }
    Save-JsonFile $configPath $defaultConfig
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json

if ($PSBoundParameters.ContainsKey("GeneXusPath")) {
    $config.GeneXus.InstallationPath = $GeneXusPath
}
if ($PSBoundParameters.ContainsKey("KBPath")) {
    $config.Environment.KBPath = $KBPath
}

$config.GeneXus.InstallationPath = Get-ExistingPathOrPrompt "GeneXus installation path" $config.GeneXus.InstallationPath
$config.Environment.KBPath = Get-ExistingPathOrPrompt "Knowledge Base path" $config.Environment.KBPath

Backup-File $configPath
Save-JsonFile $configPath $config
Write-Ok "config.json updated."

Write-Step "[1/2] Building gateway and worker"
& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) {
    Fail "Build failed."
}
if (-not (Test-Path $startMcpBatPath)) {
    Fail "Build completed but $startMcpBatPath was not generated."
}
Write-Ok "Build completed."

if ($SkipClientConfig) {
    Write-Step "[2/2] Skipping AI client MCP registration (-SkipClientConfig)"
} else {
    Write-Step "[2/2] Registering MCP with detected AI clients (via genexus-mcp CLI)"
    $node = Resolve-CommandPath @("node.exe", "node")
    if (-not $node) {
        Write-Warn "node was not found in PATH - cannot register AI clients automatically."
        Write-Warn "Install Node.js 18+ and run: node `"$cliRunPath`" init --write-clients --gx `"$($config.GeneXus.InstallationPath)`" --kb `"$($config.Environment.KBPath)`""
    } elseif (-not (Test-Path $gatewayExePath)) {
        Write-Warn "Gateway exe not found at $gatewayExePath - skipping client registration."
    } else {
        # Point the CLI at the freshly-built gateway exe so the client launcher is a
        # direct exe path (not npx). getLauncher() in cli/lib/config.js honors this.
        $prevGatewayExe = $env:GENEXUS_MCP_GATEWAY_EXE
        $env:GENEXUS_MCP_GATEWAY_EXE = $gatewayExePath
        try {
            $initArgs = @(
                "`"$cliRunPath`"", "init", "--write-clients", "--no-smoke", "--format", "json",
                "--gx", "`"$($config.GeneXus.InstallationPath)`"",
                "--kb", "`"$($config.Environment.KBPath)`""
            )
            & $node @initArgs | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Ok "AI clients registered (Claude Desktop/Code, Antigravity, Gemini CLI, Cursor, OpenCode, Codex, VS Code - whichever are installed)."
            } else {
                Write-Warn "genexus-mcp init exited with code $LASTEXITCODE. Re-run manually to see details."
            }
        } catch {
            Write-Warn "AI client registration failed: $($_.Exception.Message)"
        } finally {
            if ($null -ne $prevGatewayExe) { $env:GENEXUS_MCP_GATEWAY_EXE = $prevGatewayExe }
            else { Remove-Item env:GENEXUS_MCP_GATEWAY_EXE -ErrorAction SilentlyContinue }
        }
    }
}

Write-Host ""
Write-Ok "Installation complete."
Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Cyan
Write-Host "  Gateway exe:       $gatewayExePath"
Write-Host "  Backend launcher:  $startMcpBatPath"
Write-Host ""
Write-Host "Manual MCP snippet (for any client not auto-registered):" -ForegroundColor Cyan
Write-Host '{'
Write-Host '  "mcpServers": {'
Write-Host '    "genexus": {'
Write-Host "      ""command"": ""$($gatewayExePath -replace '\\', '\\')"","
Write-Host '      "args": []'
Write-Host '    }'
Write-Host '  }'
Write-Host '}'
Write-Host ""
Write-Host "Re-run client registration anytime with:" -ForegroundColor Cyan
Write-Host "  `$env:GENEXUS_MCP_GATEWAY_EXE='$gatewayExePath'; node `"$cliRunPath`" init --write-clients --gx `"$($config.GeneXus.InstallationPath)`" --kb `"$($config.Environment.KBPath)`""
Write-Host ""
Write-Host "If any AI client was open, restart it to pick up the new MCP configuration."
