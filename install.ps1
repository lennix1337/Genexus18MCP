# GeneXus 18 MCP Server installer

[CmdletBinding()]
param(
    [string]$KBPath,
    [string]$GeneXusPath,
    [switch]$SkipExtensionInstall,
    [switch]$SkipClaudeConfig,
    [switch]$SkipCodexConfig
)

$progressPreference = "SilentlyContinue"

$root = $PSScriptRoot
$configPath = Join-Path $root "config.json"
$publishDir = Join-Path $root "publish"
$extensionDir = Join-Path $root "src\nexus-ide"
$vsixPath = Join-Path $extensionDir "nexus-ide.vsix"
$startMcpBatPath = Join-Path $publishDir "start_mcp.bat"
$claudeConfigPath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
$codexConfigPath = Join-Path $env:USERPROFILE ".codex\config.toml"
$antigravityConfigPath = Join-Path $env:USERPROFILE ".gemini\antigravity\mcp_config.json"
$cursorConfigPath = Join-Path $env:APPDATA "Cursor\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json"

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

    # .NET 8 SDK
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Warn ".NET SDK not found. Gateway build requires .NET 8 SDK."
        Write-Host "    Download from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Gray
        $missing.Add(".NET 8 SDK")
    } else {
        $version = dotnet --version
        Write-Ok ".NET SDK found: $version"
    }

    # Node.js & npm
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if (-not $npm) {
        if (-not $SkipExtensionInstall) {
            Write-Warn "npm not found. Extension packaging requires Node.js."
            Write-Host "    Download from: https://nodejs.org/" -ForegroundColor Gray
            $missing.Add("Node.js/npm")
        } else {
            Write-Warn "npm not found, but extension installation is being skipped."
        }
    } else {
        $version = npm --version
        Write-Ok "npm found: $version"
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

    # Auto-detect GeneXus 18 from registry if label is "GeneXus installation path"
    if ($label -eq "GeneXus installation path") {
        $regRoots = @("HKLM:\SOFTWARE\WOW6432Node\Artech\GeneXus", "HKLM:\SOFTWARE\Artech\GeneXus", "HKCU:\SOFTWARE\Artech\GeneXus")

        foreach ($rootPath in $regRoots) {
            $path = Join-Path $rootPath "18.0"
            if (Test-Path $path) {
                $detected = Get-ItemProperty -Path $path -Name "InstallPath" -ErrorAction SilentlyContinue
                if ($detected -and (Test-Path $detected.InstallPath)) {
                    Write-Ok "Auto-detected GeneXus 18 at: $($detected.InstallPath)"
                    return $detected.InstallPath
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

function Set-ClaudeConfig([string]$path, [string]$commandPath) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
    }

    if (Test-Path $path) {
        Backup-File $path
        $config = Get-Content $path -Raw | ConvertFrom-Json
    } else {
        $config = [pscustomobject]@{}
    }

    if ($null -eq $config.mcpServers) {
        $config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([pscustomobject]@{})
    }

    if ($null -eq $config.mcpServers.genexus18) {
        $config.mcpServers | Add-Member -MemberType NoteProperty -Name "genexus18" -Value ([pscustomobject]@{
            command = $commandPath
            args = @()
        })
    } else {
        $config.mcpServers.genexus18.command = $commandPath
        $config.mcpServers.genexus18.args = @()
    }

    Save-JsonFile $path $config
}

function Set-AntigravityConfig([string]$path, [string]$commandPath) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
    }

    if (Test-Path $path) {
        Backup-File $path
        $config = Get-Content $path -Raw | ConvertFrom-Json
    } else {
        $config = [pscustomobject]@{}
    }

    if ($null -eq $config.mcpServers) {
        $config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([pscustomobject]@{})
    }

    if ($null -eq $config.mcpServers.genexus) {
        $config.mcpServers | Add-Member -MemberType NoteProperty -Name "genexus" -Value ([pscustomobject]@{
            command = $commandPath
            args = @()
            env = [pscustomobject]@{}
        })
    }

    $config.mcpServers.genexus.command = $commandPath
    $config.mcpServers.genexus.args = @()
    if ($null -eq $config.mcpServers.genexus.env) {
        $config.mcpServers.genexus | Add-Member -MemberType NoteProperty -Name "env" -Value ([pscustomobject]@{}) -Force
    }

    Save-JsonFile $path $config
}

function Set-CursorClineConfig([string]$path, [string]$commandPath) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        return $false
    }

    if (Test-Path $path) {
        Backup-File $path
        $config = Get-Content $path -Raw | ConvertFrom-Json
    } else {
        $config = [pscustomobject]@{}
    }

    if ($null -eq $config.mcpServers) {
        $config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([pscustomobject]@{})
    }

    if ($null -eq $config.mcpServers.genexus18) {
        $config.mcpServers | Add-Member -MemberType NoteProperty -Name "genexus18" -Value ([pscustomobject]@{
            command = $commandPath
            args = @()
            disabled = $false
            autoApprove = @()
        })
    }

    $config.mcpServers.genexus18.command = $commandPath
    $config.mcpServers.genexus18.args = @()
    if ($null -eq $config.mcpServers.genexus18.disabled) {
        $config.mcpServers.genexus18 | Add-Member -MemberType NoteProperty -Name "disabled" -Value $false -Force
    } else {
        $config.mcpServers.genexus18.disabled = $false
    }
    if ($null -eq $config.mcpServers.genexus18.autoApprove) {
        $config.mcpServers.genexus18 | Add-Member -MemberType NoteProperty -Name "autoApprove" -Value @() -Force
    }

    Save-JsonFile $path $config
    return $true
}

function Set-CodexConfig([string]$path, [string]$url) {
    $configDir = Split-Path $path
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
    }

    if (Test-Path $path) {
        Backup-File $path
        $content = Get-Content $path -Raw
    } else {
        $content = ""
    }

    $sectionPattern = '(?ms)^\[mcp_servers\.genexus\]\s*.*?(?=^\[|\z)'
    $replacement = "[mcp_servers.genexus]`r`nurl = `"$url`"`r`n"

    if ($content -match $sectionPattern) {
        $updated = [System.Text.RegularExpressions.Regex]::Replace($content, $sectionPattern, $replacement)
    } else {
        $separator = if ([string]::IsNullOrWhiteSpace($content)) { "" } else { "`r`n`r`n" }
        $updated = $content.TrimEnd() + $separator + $replacement
    }

    [System.IO.File]::WriteAllText($path, $updated, [System.Text.Encoding]::UTF8)
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

function Invoke-NativeCommand([string]$commandPath, [string[]]$arguments) {
    & $commandPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $commandPath $($arguments -join ' ')"
    }
}

function Get-EditorCommands() {
    $candidates = @(
        "code.cmd",
        "code",
        "code-insiders.cmd",
        "code-insiders",
        "cursor.cmd",
        "cursor",
        "codium.cmd",
        "codium",
        "antigravity.cmd",
        "antigravity"
    )
    $resolved = New-Object System.Collections.Generic.List[string]

    foreach ($candidate in $candidates) {
        $path = Resolve-CommandPath @($candidate)
        if ($path -and -not $resolved.Contains($path)) {
            $resolved.Add($path)
        }
    }

    return $resolved.ToArray()
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

$httpPort = 5000
if ($config.Server -and $config.Server.HttpPort) {
    try {
        $httpPort = [int]$config.Server.HttpPort
    } catch {}
}
$codexMcpUrl = "http://127.0.0.1:$httpPort/mcp"

Write-Step "[1/5] Building gateway, worker, and extension backend"
& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) {
    Fail "Build failed."
}
if (-not (Test-Path $startMcpBatPath)) {
    Fail "Build completed but $startMcpBatPath was not generated."
}
Write-Ok "Build completed."

if (-not $SkipExtensionInstall) {
    Write-Step "[2/5] Packaging and installing the VS Code extension"
    Push-Location $extensionDir
    try {
        $npmCommand = Resolve-CommandPath @("npm.cmd", "npm")
        $npxCommand = Resolve-CommandPath @("npx.cmd", "npx")
        if (-not $npmCommand) {
            throw "npm was not found in PATH."
        }
        if (-not $npxCommand) {
            throw "npx was not found in PATH."
        }

        if (Test-Path (Join-Path $extensionDir "package-lock.json")) {
            Invoke-NativeCommand $npmCommand @("ci", "--silent")
        } else {
            Invoke-NativeCommand $npmCommand @("install", "--silent")
        }

        Invoke-NativeCommand $npmCommand @("run", "compile")
        
        Write-Host "Packaging extension..." -ForegroundColor Cyan
        & $npxCommand --yes @vscode/vsce package --out nexus-ide.vsix
        if ($LASTEXITCODE -ne 0) {
            $vsceLog = Join-Path $extensionDir "vsce-package.log"
            Write-Error "vsce package failed. Checking for common issues..."
            $extensionLicensePath = Join-Path $extensionDir "LICENSE.txt"
            if (-not (Test-Path $extensionLicensePath)) {
                $licenseCandidates = @("LICENSE.txt", "LICENSE.md", "LICENSE")
                $sourceLicensePath = $null
                foreach ($candidate in $licenseCandidates) {
                    $candidatePath = Join-Path $root $candidate
                    if (Test-Path $candidatePath) {
                        $sourceLicensePath = $candidatePath
                        break
                    }
                }

                if ($sourceLicensePath) {
                    Write-Warn "LICENSE.txt is missing in $extensionDir. Copying from $sourceLicensePath..."
                    Copy-Item $sourceLicensePath $extensionLicensePath -Force
                    & $npxCommand --yes @vscode/vsce package --out nexus-ide.vsix
                } else {
                    Write-Warn "No license file found at repository root (LICENSE, LICENSE.md, LICENSE.txt)."
                }
            }

            if ($LASTEXITCODE -ne 0) {
                throw "vsce package failed again. Please check license include patterns and package.json."
            }
        }

        $editorCommands = Get-EditorCommands
        if ($editorCommands.Length -gt 0) {
            foreach ($editorCommand in $editorCommands) {
                Invoke-NativeCommand $editorCommand @("--install-extension", $vsixPath, "--force")
                Write-Ok "Extension installed via $editorCommand"
            }
        } else {
            Write-Warn "No supported editor CLI was found. Install $vsixPath manually."
        }
    } catch {
        Write-Warn "Automatic VS Code extension installation failed: $($_.Exception.Message)"
        Write-Warn "You can still install $vsixPath manually."
    } finally {
        Pop-Location
    }
} else {
    Write-Step "[2/5] Skipping VS Code extension installation"
}

if (-not $SkipClaudeConfig) {
    Write-Step "[3/5] Configuring Claude Desktop"
    try {
        Set-ClaudeConfig -path $claudeConfigPath -commandPath $startMcpBatPath
        Write-Ok "Claude Desktop configured at $claudeConfigPath"
    } catch {
        Write-Warn "Failed to update Claude Desktop config: $($_.Exception.Message)"
    }
} else {
    Write-Step "[3/5] Skipping Claude Desktop configuration"
}

if (-not $SkipCodexConfig) {
    Write-Step "[4/5] Configuring Codex Desktop"
    try {
        Set-CodexConfig -path $codexConfigPath -url $codexMcpUrl
        Write-Ok "Codex configured at $codexConfigPath"
    } catch {
        Write-Warn "Failed to update Codex config: $($_.Exception.Message)"
    }
} else {
    Write-Step "[4/5] Skipping Codex Desktop configuration"
}

Write-Step "[5/5] Configuring Antigravity & IDEs"
try {
    Set-AntigravityConfig -path $antigravityConfigPath -commandPath $startMcpBatPath
    Write-Ok "Antigravity configured at $antigravityConfigPath"
} catch {
    Write-Warn "Failed to update Antigravity config: $($_.Exception.Message)"
}
try {
    $clineConfigured = Set-CursorClineConfig -path $cursorConfigPath -commandPath $startMcpBatPath
    if ($clineConfigured) {
        Write-Ok "Cursor (Cline) configured at $cursorConfigPath"
    } else {
        Write-Warn "Cursor (Cline) was not found. Skipping integration."
    }
} catch {
    Write-Warn "Failed to update Cursor (Cline) config: $($_.Exception.Message)"
}

Write-Host ""
Write-Ok "Installation complete."
Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Cyan
Write-Host "  Backend launcher: $startMcpBatPath"
Write-Host "  VS Code extension: $vsixPath"
Write-Host ""
Write-Host "Cursor/Cline MCP snippet:" -ForegroundColor Cyan
Write-Host '{'
Write-Host '  "mcpServers": {'
Write-Host '    "genexus18": {'
Write-Host "      ""command"": ""$($startMcpBatPath -replace '\\', '\\')"","
Write-Host '      "args": []'
Write-Host '    }'
Write-Host '  }'
Write-Host '}'
Write-Host ""
Write-Host "If Claude, Codex, or Antigravity was open, restart the app to pick up the new MCP configuration."
