# GeneXus MCP - Corporate Installer (fixed-path)
# ==============================================
# Installs `genexus-mcp` to a stable directory so corporate ASR / Defender
# policies can whitelist exact paths without needing wildcards over the
# npm cache (`%LOCALAPPDATA%\npm-cache\_npx\<hash>\...`).
#
# Default install dir:
#   - Admin           -> C:\Tools\GenexusMCP
#   - Non-admin       -> %LOCALAPPDATA%\Programs\GenexusMCP
#
# One-liner (latest release, runs init too):
#   iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)
#
# With params:
#   $script = irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1
#   & ([scriptblock]::Create($script)) -Kb "C:\KBs\MyKB" -Gx "C:\Program Files (x86)\GeneXus\GeneXus18"
#
# Re-run with the same args to upgrade. Use -Force to reinstall the same version.
# Use -Repair to wipe + reinstall the same currently-installed version.
# Use -Uninstall to remove the install dir + drop AI client mcpServers.genexus entries.

[CmdletBinding()]
param(
    [string]$InstallDir,

    [string]$Version,

    [ValidateScript({ -not $_ -or (Test-Path -LiteralPath $_) })]
    [string]$Kb,

    [ValidateScript({ -not $_ -or (Test-Path -LiteralPath $_) })]
    [string]$Gx,

    [switch]$NoClient,

    # Comma-separated client ids: claude-desktop-win, claude-desktop-mac,
    # antigravity, claude-code, gemini-cli, cursor, opencode, codex-cli.
    # Default: all detected installed agents.
    [string]$Clients,

    # Show interactive y/N prompt per detected agent (overrides -Clients).
    [switch]$InteractiveClients,

    [switch]$Force,

    # Wipe + reinstall the currently-installed version (or -Version) into the same dir.
    [switch]$Repair,

    # Remove the install dir and any AI client config that points at it. No reinstall.
    [switch]$Uninstall,

    # Skip the post-install AI client process detect/restart prompt.
    [switch]$NoRestartPrompt,

    # Number of times to retry the publish.zip download on transient failure.
    [int]$DownloadRetries = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:initFailed = $false
$Repo = 'lennix1337/Genexus18MCP'
$ApiBase = 'https://api.github.com'
$skipExtract = $false
$installResult = $null

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($msg) { Write-Host "[i] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Ok($msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "[X] $msg" -ForegroundColor Red }

# Pure, filesystem-scoped helpers used by scripts/install.ps1.
#
# This file intentionally has no top-level installation side effects. It can be
# dot-sourced by contract tests with synthetic ZIPs and a temporary install root.

Set-StrictMode -Version Latest

function Assert-SafeZipEntryPath {
    param([Parameter(Mandatory = $true)][string]$Name)

    $normalized = $Name.Replace('\', '/').TrimStart('/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        [System.IO.Path]::IsPathRooted($Name) -or
        $normalized -match '(^|/)\.\.(/|$)' -or
        $normalized -match '^[A-Za-z]:' -or
        $normalized.Contains("`0")) {
        throw "Unsafe ZIP entry path: $Name"
    }

    return $normalized
}

function Test-InstallArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$StagingDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [switch]$RequireManifest
    )

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
        throw "Archive not found: $ZipPath"
    }

    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-Item -LiteralPath $StagingDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StagingDirectory -Force | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        foreach ($entry in $archive.Entries) {
            [void](Assert-SafeZipEntryPath -Name $entry.FullName)
        }
    } catch {
        if (Test-Path -LiteralPath $StagingDirectory) {
            Remove-Item -LiteralPath $StagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    } finally {
        if ($archive) { $archive.Dispose() }
    }

    try {
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $StagingDirectory -Force

        $required = @(
            'GxMcp.Gateway.exe',
            'worker\GxMcp.Worker.exe',
            'tool_definitions.json'
        )
        foreach ($relative in $required) {
            $path = Join-Path $StagingDirectory $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Required release artefact missing from archive: $relative"
            }
        }

        $manifest = $null
        if ($RequireManifest) {
            $manifestPath = Join-Path $StagingDirectory 'gxmcp-manifest.json'
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                throw 'gxmcp-manifest.json is required for v3 releases.'
            }

            try {
                $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            } catch {
                throw "Invalid gxmcp-manifest.json: $($_.Exception.Message)"
            }

            if ($manifest.schemaVersion -ne 'gxmcp-release-manifest/1') {
                throw "Unsupported release manifest schema: $($manifest.schemaVersion)"
            }

            $expected = $ExpectedVersion.TrimStart('v')
            if ([string]$manifest.version -ne $expected) {
                throw "Release manifest version $($manifest.version) does not match requested $expected."
            }

            if (-not $manifest.artifacts) {
                throw 'Release manifest has no artifacts.'
            }

            if ([string]$manifest.schema -ne 'tool_definitions.json' -or
                -not $manifest.protocolVersions -or
                -not (@($manifest.protocolVersions) -contains '2025-11-25') -or
                -not (@($manifest.protocolVersions) -contains '2026-07-28')) {
                throw 'Release manifest is missing the schema or supported MCP protocol revisions.'
            }
            if ([string]$manifest.provenance -ne 'gxmcp-sbom.json') {
                throw 'Release manifest must bind the staged provenance document.'
            }

            if ([string]::IsNullOrWhiteSpace([string]$manifest.sourceCommit) -or
                [string]::IsNullOrWhiteSpace([string]$manifest.schemaSha256) -or
                [string]$manifest.schemaSha256 -notmatch '^[0-9a-fA-F]{64}$') {
                throw 'Release manifest must bind a source commit and SHA-256 schema hash.'
            }
            if ([string]$manifest.runtime.gateway -ne 'net10.0-windows' -or
                [string]$manifest.runtime.worker -ne 'net48-x86' -or
                [string]$manifest.runtime.node -notmatch '^>=22\.0\.0$') {
                throw 'Release manifest runtime contract is unsupported.'
            }

            $manifestPaths = @()
            foreach ($artifact in $manifest.artifacts) {
                if ([string]::IsNullOrWhiteSpace([string]$artifact.path) -or
                    [string]::IsNullOrWhiteSpace([string]$artifact.sha256) -or
                    $null -eq $artifact.size) {
                    throw 'Release manifest contains an incomplete artifact entry.'
                }
                $relative = Assert-SafeZipEntryPath -Name ([string]$artifact.path)
                $artifactPath = Join-Path $StagingDirectory $relative
                if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
                    throw "Manifest artifact missing from archive: $relative"
                }
                $item = Get-Item -LiteralPath $artifactPath
                if ([int64]$artifact.size -ne [int64]$item.Length) {
                    throw "Manifest size mismatch for $relative."
                }
                $actual = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actual -ne ([string]$artifact.sha256).ToLowerInvariant()) {
                    throw "Manifest SHA-256 mismatch for $relative."
                }
                $manifestPaths += $relative
            }
            if (($manifestPaths | Sort-Object -Unique).Count -ne $manifestPaths.Count) {
                throw 'Release manifest contains duplicate artifact paths.'
            }
            $schemaActual = (Get-FileHash -LiteralPath (Join-Path $StagingDirectory 'tool_definitions.json') -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($schemaActual -ne ([string]$manifest.schemaSha256).ToLowerInvariant()) {
                throw 'Release manifest schemaSha256 does not match tool_definitions.json.'
            }
            foreach ($requiredRelative in @('GxMcp.Gateway.exe', 'worker/GxMcp.Worker.exe', 'tool_definitions.json', 'gxmcp-sbom.json')) {
                if ($manifestPaths -notcontains $requiredRelative) {
                    throw "Release manifest does not cover required artifact: $requiredRelative"
                }
            }
        }

        return [pscustomobject]@{
            StagingDirectory = $StagingDirectory
            GatewayPath = Join-Path $StagingDirectory 'GxMcp.Gateway.exe'
            WorkerPath = Join-Path $StagingDirectory 'worker\GxMcp.Worker.exe'
            Manifest = $manifest
        }
    } catch {
        if (Test-Path -LiteralPath $StagingDirectory) {
            Remove-Item -LiteralPath $StagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Invoke-InstallProbe {
    param(
        [Parameter(Mandatory = $true)][string]$GatewayPath,
        [int]$TimeoutMs = 5000
    )

    $proc = Start-Process -FilePath $GatewayPath -ArgumentList '--self-test' -PassThru -WindowStyle Hidden -ErrorAction Stop
    try {
        if (-not $proc.WaitForExit($TimeoutMs)) {
            try { $proc.Kill() } catch { }
            throw "Gateway self-test timed out after ${TimeoutMs}ms."
        }
        if ($proc.ExitCode -ne 0) {
            throw "Gateway self-test exited with code $($proc.ExitCode)."
        }
    } finally {
        $proc.Dispose()
    }
}

function Invoke-ValidatedInstall {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [switch]$RequireManifest,
        [int]$ProbeTimeoutMs = 5000,
        [scriptblock]$Probe = $null
    )

    $parent = Split-Path -Parent $InstallDirectory
    $leaf = Split-Path -Leaf $InstallDirectory
    if ([string]::IsNullOrWhiteSpace($parent) -or [string]::IsNullOrWhiteSpace($leaf)) {
        throw "Install directory must be a concrete path: $InstallDirectory"
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    $stage = Join-Path $parent (".$leaf.staging-" + [guid]::NewGuid().ToString('N'))
    $backup = $null
    $targetExisted = Test-Path -LiteralPath $InstallDirectory
    try {
        $validated = Test-InstallArchive -ZipPath $ZipPath -StagingDirectory $stage -ExpectedVersion $Version -RequireManifest:$RequireManifest

        # Preserve operator-owned configuration and credentials across upgrades.
        if ($targetExisted) {
            foreach ($name in @('config.json', 'config.local.json', 'auth.json', 'credentials.json')) {
                $oldPath = Join-Path $InstallDirectory $name
                $newPath = Join-Path $stage $name
                if (Test-Path -LiteralPath $oldPath -PathType Leaf) {
                    Copy-Item -LiteralPath $oldPath -Destination $newPath -Force
                }
            }
        }

        $Version | Out-File -FilePath (Join-Path $stage 'version.txt') -Encoding ascii -NoNewline
        if ($Probe) {
            $probeResult = & $Probe $validated.GatewayPath
            if ($probeResult -eq $false) { throw 'Gateway self-test probe rejected the staged release.' }
        } else {
            Invoke-InstallProbe -GatewayPath $validated.GatewayPath -TimeoutMs $ProbeTimeoutMs
        }

        if ($targetExisted) {
            $backup = Join-Path $parent (".$leaf.previous-" + (Get-Date -Format 'yyyyMMddHHmmssfff') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)))
            Move-Item -LiteralPath $InstallDirectory -Destination $backup -Force
        }
        try {
            Move-Item -LiteralPath $stage -Destination $InstallDirectory -Force
        } catch {
            if ($backup -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $InstallDirectory)) {
                Move-Item -LiteralPath $backup -Destination $InstallDirectory -Force
            }
            throw
        }

        return [pscustomobject]@{
            InstallDirectory = $InstallDirectory
            BackupDirectory = $backup
            Manifest = $validated.Manifest
        }
    } finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Confirm-Action {
    param([string]$Prompt, [bool]$DefaultYes = $false)
    $suffix = if ($DefaultYes) { '[Y/n]' } else { '[y/N]' }
    $ans = Read-Host "$Prompt $suffix"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $DefaultYes }
    return ($ans.Trim().ToLowerInvariant() -in @('y', 'yes', 's', 'sim'))
}

# Surfaces proxy hint when the user is likely behind a corporate proxy but
# hasn't exported the env vars Invoke-WebRequest honors. PowerShell respects
# $env:HTTPS_PROXY / $env:HTTP_PROXY automatically.
function Test-LikelyProxiedNetwork {
    if ($env:HTTPS_PROXY -or $env:HTTP_PROXY) { return $false }
    try {
        $sysProxy = [System.Net.WebRequest]::GetSystemWebProxy()
        $direct = $sysProxy.GetProxy([uri]'https://api.github.com')
        if ($direct -and $direct.AbsoluteUri -ne 'https://api.github.com/') { return $true }
    } catch { }
    return $false
}

function Invoke-WithRetry {
    param(
        [scriptblock]$Action,
        [int]$Retries = 3,
        [string]$Label = 'network call'
    )
    $attempt = 0
    $lastErr = $null
    while ($attempt -lt $Retries) {
        $attempt += 1
        try {
            return & $Action
        } catch {
            $lastErr = $_
            if ($attempt -lt $Retries) {
                $waitSec = [Math]::Min(30, [Math]::Pow(2, $attempt))
                Write-Warn "$Label attempt $attempt/$Retries failed: $($_.Exception.Message). Retrying in $waitSec s..."
                Start-Sleep -Seconds $waitSec
            }
        }
    }
    throw $lastErr
}

# Scan likely GeneXus install roots and return all candidates. We don't just take
# the first hit - the user might have GeneXus18 + GeneXus18u7 side-by-side and we
# want to surface both. This catches the exact case from the v2.6.7 field report:
# user accepted the doc default `GeneXus18` but their install was at `GeneXus18u7`,
# so init wrote a broken config and the worker crashed silently on first call.
function Find-GeneXusInstallations {
    $roots = @()
    $pfx86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pfx86)            { $roots += $pfx86 }
    if ($env:ProgramFiles) { $roots += $env:ProgramFiles }
    foreach ($drive in 'C', 'D', 'E') {
        $roots += "${drive}:\Program Files (x86)"
        $roots += "${drive}:\Program Files"
    }

    $found = New-Object System.Collections.Generic.List[object]
    $seen = New-Object System.Collections.Generic.HashSet[string]

    foreach ($root in $roots) {
        $gxRoot = Join-Path $root 'GeneXus'
        if (-not (Test-Path -LiteralPath $gxRoot)) { continue }
        try {
            Get-ChildItem -LiteralPath $gxRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $exe = Join-Path $_.FullName 'genexus.exe'
                if (Test-Path -LiteralPath $exe) {
                    $key = $_.FullName.ToLowerInvariant()
                    if ($seen.Add($key)) {
                        $version = $null
                        try {
                            $vinfo = (Get-Item -LiteralPath $exe).VersionInfo
                            $version = $vinfo.ProductVersion
                        } catch { }
                        $found.Add([pscustomobject]@{
                            Path    = $_.FullName
                            Folder  = $_.Name
                            Version = $version
                        })
                    }
                }
            }
        } catch { }
    }

    return $found
}

function Resolve-GeneXusPath {
    param([string]$ProvidedGx)

    if ($ProvidedGx) {
        if (Test-Path -LiteralPath (Join-Path $ProvidedGx 'genexus.exe')) {
            Write-Ok "Using -Gx: $ProvidedGx"
            return $ProvidedGx
        }
        Write-Warn "-Gx '$ProvidedGx' does not contain genexus.exe. Searching for alternatives..."
    }

    $candidates = Find-GeneXusInstallations
    if ($candidates.Count -eq 0) {
        Write-Warn 'No GeneXus installations auto-detected. Init will require -Gx explicitly.'
        return $ProvidedGx  # may be empty; let npx init produce the actionable error
    }

    if ($candidates.Count -eq 1) {
        $verLabel = if ($candidates[0].Version) { $candidates[0].Version } else { 'unknown version' }
        Write-Ok ("Detected GeneXus install: {0} ({1})" -f $candidates[0].Path, $verLabel)
        return $candidates[0].Path
    }

    Write-Step "Found $($candidates.Count) GeneXus installations:"
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        $c = $candidates[$i]
        $verStr = if ($c.Version) { " v$($c.Version)" } else { '' }
        Write-Host ("  [{0}] {1}{2}" -f ($i + 1), $c.Path, $verStr)
    }
    $pick = Read-Host "Pick one [1-$($candidates.Count)] (default 1)"
    if ([string]::IsNullOrWhiteSpace($pick)) { $pick = '1' }
    $idx = 0
    if (-not [int]::TryParse($pick, [ref]$idx) -or $idx -lt 1 -or $idx -gt $candidates.Count) {
        Write-Warn "Invalid choice; defaulting to [1]."
        $idx = 1
    }
    return $candidates[$idx - 1].Path
}

# Detect known AI client processes so we can offer to restart them after init.
# Restart is required for clients that read their MCP config once at startup.
# Kept in sync with the agent list in cli/lib/config.js (getClientConfigTargets).
function Get-RunningAiClients {
    $known = @(
        @{ Name = 'Claude'; Process = 'claude'; Display = 'Claude Desktop' },
        @{ Name = 'Cursor'; Process = 'Cursor'; Display = 'Cursor' },
        @{ Name = 'Antigravity'; Process = 'Antigravity'; Display = 'Antigravity' },
        @{ Name = 'Code'; Process = 'Code'; Display = 'VS Code (native MCP)' },
        @{ Name = 'CodeInsiders'; Process = 'Code - Insiders'; Display = 'VS Code Insiders (native MCP)' }
    )
    $running = @()
    foreach ($c in $known) {
        $procs = Get-Process -Name $c.Process -ErrorAction SilentlyContinue
        if ($procs) {
            # NB: try/catch as an *expression* is PowerShell 7+ only; this script
            # bootstraps under Windows PowerShell 5.1, so resolve the path first.
            $mainPath = $null
            try { $mainPath = $procs[0].MainModule.FileName } catch { $mainPath = $null }
            $running += [pscustomobject]@{
                Display = $c.Display
                Name    = $c.Name
                Pids    = ($procs | Select-Object -ExpandProperty Id)
                Path    = $mainPath
            }
        }
    }
    return $running
}

# Remove genexus MCP entries from AI client configs.
#
# Primary path: delegate to `genexus-mcp uninstall` via npx so the agent list,
# paths, and config shapes come from the single source of truth in
# cli/lib/config.js. Returns $true when the CLI handled it.
#
# NOTE: this uses `genexus-mcp@latest`, so the uninstall step reaches the network
# (npm). The corporate publish.zip ships only the gateway/worker exes, not the
# `cli/` folder, so there's no bundled CLI to run offline. `@latest`'s client
# list is a superset of any prior version's, so it removes at least as much. If
# the machine is offline, the inline fallback (Remove-GenexusFromClientConfigs)
# runs instead.
function Invoke-CliUninstall {
    $npx = Get-Command npx.cmd -ErrorAction SilentlyContinue
    if (-not $npx) { $npx = Get-Command npx -ErrorAction SilentlyContinue }
    if (-not $npx) { return $false }
    Write-Step 'Removing AI client entries via genexus-mcp uninstall (uses npx @latest; needs network)...'
    try {
        & $npx.Source -y 'genexus-mcp@latest' uninstall --yes --format json 2>&1 | Out-Null
        return $true
    } catch {
        Write-Warn "genexus-mcp uninstall failed: $($_.Exception.Message). Falling back to inline cleanup."
        return $false
    }
}

# Fallback when npx/Node is unavailable. Best-effort sweep of the JSON-shaped
# client configs (mcpServers + VS Code `servers`), removing both the current
# `genexus` key and the legacy `genexus18` key. Mirrors cli/lib/config.js paths;
# TOML (Codex) and OpenCode's `mcp.*` shape are left for manual cleanup and a
# warning is emitted.
function Remove-GenexusFromClientConfigs {
    $mcpServersConfigs = @()
    $vscodeServersConfigs = @()
    if ($env:APPDATA) {
        $mcpServersConfigs += (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
        $vscodeServersConfigs += (Join-Path $env:APPDATA 'Code\User\mcp.json')
        $vscodeServersConfigs += (Join-Path $env:APPDATA 'Code - Insiders\User\mcp.json')
    }
    if ($env:USERPROFILE) {
        $mcpServersConfigs += (Join-Path $env:USERPROFILE '.claude.json')
        $mcpServersConfigs += (Join-Path $env:USERPROFILE '.gemini\settings.json')
        $mcpServersConfigs += (Join-Path $env:USERPROFILE '.gemini\antigravity\mcp_config.json')
        $mcpServersConfigs += (Join-Path $env:USERPROFILE '.gemini\config\mcp_config.json')
        $mcpServersConfigs += (Join-Path $env:USERPROFILE '.cursor\mcp.json')
    }

    $removed = @()
    $editMap = @(
        @{ Files = $mcpServersConfigs;    Container = 'mcpServers' },
        @{ Files = $vscodeServersConfigs; Container = 'servers' }
    )
    foreach ($group in $editMap) {
        foreach ($cfg in $group.Files) {
            if (-not (Test-Path -LiteralPath $cfg)) { continue }
            try {
                $obj = (Get-Content -LiteralPath $cfg -Raw) | ConvertFrom-Json
                $container = $group.Container
                if (-not ($obj.PSObject.Properties.Name -contains $container)) { continue }
                $hadIt = $false
                foreach ($key in @('genexus', 'genexus18')) {
                    if ($obj.$container.PSObject.Properties.Name -contains $key) {
                        $obj.$container.PSObject.Properties.Remove($key)
                        $hadIt = $true
                    }
                }
                if ($hadIt) {
                    ($obj | ConvertTo-Json -Depth 100) | Set-Content -LiteralPath $cfg -Encoding utf8
                    $removed += $cfg
                }
            } catch {
                Write-Warn "Could not edit client config $cfg : $($_.Exception.Message)"
            }
        }
    }
    Write-Warn 'Inline cleanup does not touch Codex (.codex\config.toml) or OpenCode (.config\opencode) - remove the genexus entry there manually if present.'
    return $removed
}

# -------------------------------------------------------------------------
# Uninstall path
# -------------------------------------------------------------------------
if ($Uninstall) {
    if (-not $InstallDir) {
        if (Test-IsAdmin) { $InstallDir = 'C:\Tools\GenexusMCP' }
        else              { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\GenexusMCP' }
    }
    Write-Step "Uninstall: target install dir $InstallDir"
    $cliHandled = Invoke-CliUninstall
    if ($cliHandled) {
        Write-Ok 'AI client entries removed via genexus-mcp uninstall.'
    } else {
        Write-Warn 'npx not found - using inline client-config cleanup.'
        $removedConfigs = Remove-GenexusFromClientConfigs
        if ($removedConfigs.Count -gt 0) {
            Write-Ok "Removed genexus entry from $($removedConfigs.Count) client config(s):"
            $removedConfigs | ForEach-Object { Write-Host "    $_" }
        } else {
            Write-Step 'No AI client configs referenced genexus - nothing to unpatch.'
        }
    }
    if (Test-Path -LiteralPath $InstallDir) {
        if ($Force -or (Confirm-Action "Delete '$InstallDir' and all contents?" $false)) {
            try {
                Remove-Item -LiteralPath $InstallDir -Recurse -Force
                Write-Ok "Removed $InstallDir"
            } catch {
                Write-Err "Failed to remove ${InstallDir}: $($_.Exception.Message)"
                exit 1
            }
        } else {
            Write-Step 'Skipped install dir removal.'
        }
    } else {
        Write-Step "$InstallDir not present - nothing to remove."
    }
    Write-Host ''
    Write-Ok 'Uninstall complete. Restart your AI client to release any stale MCP connections.'
    exit 0
}

# -------------------------------------------------------------------------
# Install dir resolution + admin warning
# -------------------------------------------------------------------------
if (-not $InstallDir) {
    if (Test-IsAdmin) {
        $InstallDir = 'C:\Tools\GenexusMCP'
    } else {
        $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\GenexusMCP'
        Write-Host ''
        Write-Warn 'You are NOT running as Administrator.'
        Write-Warn "The installer will default to a per-user path under %LOCALAPPDATA%:"
        Write-Host  "    $InstallDir" -ForegroundColor Yellow
        Write-Warn 'This path is commonly BLOCKED by corporate AppLocker / SRP policies.'
        Write-Warn 'Symptoms: AI client shows "Failed to connect" or "Access denied" when calling the MCP.'
        Write-Host ''
        Write-Host 'Recommended: re-launch PowerShell as Administrator so the installer uses C:\Tools\GenexusMCP' -ForegroundColor Cyan
        Write-Host '(or pass -InstallDir to a path your IT policy whitelists, e.g. C:\Apps\GenexusMCP).' -ForegroundColor Cyan
        Write-Host ''
        if (-not $Force) {
            if (-not (Confirm-Action 'Continue with the per-user install anyway?' $false)) {
                Write-Step 'Install cancelled. Re-run elevated, or pass -Force to bypass this prompt.'
                exit 0
            }
        }
    }
}

# -------------------------------------------------------------------------
# Version resolution
# -------------------------------------------------------------------------
if ($Repair -and -not $Version) {
    $existingVersionFile = Join-Path $InstallDir 'version.txt'
    if (Test-Path -LiteralPath $existingVersionFile) {
        $Version = (Get-Content -LiteralPath $existingVersionFile -Raw).Trim()
        Write-Step "Repair: reusing currently-installed version $Version"
    }
}

if (-not $Version) {
    if (Test-LikelyProxiedNetwork) {
        Write-Warn 'System proxy detected but $env:HTTPS_PROXY is not set; PowerShell may not honor it. Export $env:HTTPS_PROXY=<proxy> if the download fails.'
    }
    Write-Step 'Resolving latest release...'
    $rel = Invoke-WithRetry -Label 'GitHub API release lookup' -Retries $DownloadRetries -Action {
        Invoke-RestMethod -Uri "$ApiBase/repos/$Repo/releases/latest" -UseBasicParsing
    }
    $Version = $rel.tag_name
}
if ($Version -notmatch '^v') { $Version = "v$Version" }
$VersionNoV = $Version.TrimStart('v')
$versionMajor = 0
[void][int]::TryParse(($VersionNoV -split '\.')[0], [ref]$versionMajor)
$isV3Release = $versionMajor -ge 3
Write-Step "Target version: $Version"

$versionFile = Join-Path $InstallDir 'version.txt'
$gatewayExe  = Join-Path $InstallDir 'GxMcp.Gateway.exe'
$workerExe   = Join-Path $InstallDir 'worker\GxMcp.Worker.exe'

# Repair == force a fresh extract even if versions match.
if ($Repair) { $Force = $true }

if ((Test-Path $versionFile) -and -not $Force) {
    $current = (Get-Content $versionFile -Raw).Trim()
    if ($current -eq $Version) {
        Write-Ok "Already at $Version. Pass -Force (or -Repair) to reinstall."
        # Even if we don't re-extract, still run init if -Kb/-Gx were given,
        # so the user can fix a broken config without nuking the install dir.
        if (-not $Kb -and -not $Gx) { exit 0 }
        Write-Step 'Skipping extract; re-running init with provided KB/GX.'
        $skipExtract = $true
    }
    if (-not $skipExtract) { Write-Step "Upgrading $current -> $Version" }
}

if (-not $skipExtract) {
    $zipUrl = "https://github.com/$Repo/releases/download/$Version/publish.zip"
    $tmpZip = Join-Path ([IO.Path]::GetTempPath()) "genexus-mcp-$Version.zip"

    Write-Step "Downloading $zipUrl"
    try {
        Invoke-WithRetry -Label 'publish.zip download' -Retries $DownloadRetries -Action {
            Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -UseBasicParsing
        } | Out-Null
    } catch {
        if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue }
        throw "Failed to download $zipUrl after $DownloadRetries attempts. Check the version exists on Releases, your network/proxy ('$env:HTTPS_PROXY'), or pass -Version to a known-good tag. Error: $($_.Exception.Message)"
    }

    # ── Integrity check: verify SHA-256 against the sidecar committed at the tag ──
    # v3 releases fail closed when the sidecar is missing or invalid. Older
    # releases retain an explicit compatibility path because pre-v2.9.2 tags
    # never shipped a checksum sidecar.
    $shaUrl = "https://github.com/$Repo/releases/download/$Version/publish.zip.sha256"
    $tmpSha = Join-Path ([IO.Path]::GetTempPath()) "genexus-mcp-$Version.zip.sha256"
    try {
        Invoke-WebRequest -Uri $shaUrl -OutFile $tmpSha -UseBasicParsing -ErrorAction Stop
        $expectedHash = (Get-Content $tmpSha -Raw).Trim().Split()[0].ToUpperInvariant()
        $actualHash   = (Get-FileHash -Path $tmpZip -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actualHash -ne $expectedHash) {
            Remove-Item $tmpZip  -Force -ErrorAction SilentlyContinue
            Remove-Item $tmpSha  -Force -ErrorAction SilentlyContinue
            throw "publish.zip SHA-256 mismatch. Expected: $expectedHash  Got: $actualHash`nThe downloaded file may be corrupted or tampered with. Aborting installation."
        }
        Write-Step "Integrity check passed ($($actualHash.Substring(0,16))...)"
    } catch [System.Net.WebException] {
        if ($isV3Release) {
            Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
            throw "Checksum is required for v3 releases, but $shaUrl could not be downloaded. Aborting before changing '$InstallDir'."
        }
        Write-Warning "Could not download $shaUrl — using the explicitly versioned legacy installer path for $Version."
    } catch {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        if (Test-Path $tmpSha) { Remove-Item $tmpSha -Force -ErrorAction SilentlyContinue }
    }

    # Refuse to replace an unrelated directory. The validated installer below
    # stages, probes, and swaps atomically; the previous directory is retained.
    if (Test-Path $InstallDir) {
        $looksOurs = (Test-Path $versionFile) -or (Test-Path $gatewayExe)
        $isEmpty = -not (Get-ChildItem -Path $InstallDir -Force | Select-Object -First 1)
        if (-not $looksOurs -and -not $isEmpty) {
            Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
            throw "Refusing to replace '$InstallDir' - it exists but doesn't look like a previous GenexusMCP install. Pass -InstallDir to a dedicated directory."
        }
    }

    Write-Step "Validating and staging release before replacing $InstallDir"
    try {
        $installResult = Invoke-ValidatedInstall `
            -ZipPath $tmpZip `
            -InstallDirectory $InstallDir `
            -Version $Version `
            -RequireManifest:$isV3Release `
            -ProbeTimeoutMs 5000
        if ($installResult.BackupDirectory) {
            Write-Step "Previous installation retained at $($installResult.BackupDirectory)"
        }
    } finally {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $gatewayExe)) { throw "Validated installation failed: $gatewayExe not found." }
    if (-not (Test-Path $workerExe))  { throw "Validated installation failed: $workerExe not found." }
}

# -------------------------------------------------------------------------
# Pre-flight GX resolution (catches the GeneXus18u7 case before init)
# -------------------------------------------------------------------------
if (-not $NoClient) {
    $Gx = Resolve-GeneXusPath -ProvidedGx $Gx
}

# -------------------------------------------------------------------------
# Gateway self-test: validates the install can read config + see GX + load build dll.
# Replaces the no-op --axi-spawn-probe flag that only checked exe-launchability.
# Without -Kb / -Gx we still run the AppLocker-only spawn check, since the gateway
# can't load a config that doesn't exist yet.
# -------------------------------------------------------------------------
Write-Step 'Probing gateway exe (AppLocker / execution policy check)...'
$probeError = $null
try {
    $proc = Start-Process -FilePath $gatewayExe -ArgumentList '--self-test' `
        -PassThru -WindowStyle Hidden -ErrorAction Stop
    # Self-test exits on its own; give it up to 5s for a slow disk + JIT warmup.
    if (-not $proc.WaitForExit(5000)) {
        try { $proc.Kill() } catch { }
        throw 'Gateway self-test timed out after 5000ms.'
    }
    if ($proc.ExitCode -ne 0) {
        throw "Gateway self-test exited with code $($proc.ExitCode)."
    }
    Write-Ok 'Gateway exe is launchable from the install path.'
} catch {
    $probeError = $_.Exception.Message
}

if ($probeError) {
    if ($installResult -and $installResult.BackupDirectory -and
        (Test-Path -LiteralPath $installResult.BackupDirectory)) {
        # A path-specific probe can fail after the staged probe succeeds (for
        # example, an AppLocker rule targets the final directory). Put the
        # previous installation back before surfacing the failure.
        $failedInstall = "$InstallDir.failed-" + [guid]::NewGuid().ToString('N')
        try {
            if (Test-Path -LiteralPath $InstallDir) {
                Move-Item -LiteralPath $InstallDir -Destination $failedInstall -Force
            }
            Move-Item -LiteralPath $installResult.BackupDirectory -Destination $InstallDir -Force
            Write-Warn "Probe failed; previous installation restored. Failed candidate retained at $failedInstall"
        } catch {
            Write-Warn "Probe failed and automatic rollback could not complete: $($_.Exception.Message)"
        }
    }
    $msg = $probeError
    $accessDenied = $msg -match 'Access is denied|Access denied|Acesso negado|0x80070005|UnauthorizedAccess'
    Write-Host ''
    if ($accessDenied) {
        Write-Err 'AppLocker / SRP / Defender blocked execution of the gateway from this path.'
        Write-Host "    Path: $gatewayExe" -ForegroundColor Red
        Write-Host ''
        Write-Host 'Remediations:' -ForegroundColor Yellow
        Write-Host '  - Run installer as admin so it defaults to C:\Tools\GenexusMCP (admin-writable, usually whitelisted).'
        Write-Host '  - Or pass -InstallDir to a path your IT policy allows execution from'
        Write-Host '    (e.g. C:\Apps\GenexusMCP). AppLocker default rules deny exec from'
        Write-Host '    %APPDATA%, %LOCALAPPDATA%, and %TEMP%.'
        Write-Host '  - Get-AppLockerPolicy -Effective -Xml  # to inspect current policy'
        Write-Host '  - Event log: Microsoft-Windows-AppLocker/EXE and DLL, IDs 8003/8004'
    } else {
        Write-Err 'Gateway exe failed to launch from the install path.'
        Write-Host "    Path: $gatewayExe" -ForegroundColor Red
        Write-Host "    Error: $msg" -ForegroundColor Red
    }
    throw "Aborting: gateway exe is not runnable from '$InstallDir'."
}

$Version | Out-File -FilePath $versionFile -Encoding ascii -NoNewline

# -------------------------------------------------------------------------
# AI client registration via npx, with humanized error output
# -------------------------------------------------------------------------
if (-not $NoClient) {
    $npx = Get-Command npx.cmd -ErrorAction SilentlyContinue
    if (-not $npx) { $npx = Get-Command npx -ErrorAction SilentlyContinue }

    if (-not $npx) {
        Write-Warn 'npx not found - skipping AI client registration.'
        Write-Warn '  Install Node.js 22+ (https://nodejs.org/) and re-run, or pass -NoClient and configure clients manually.'
    } else {
        # Pin npx to the same version we just extracted so the CLI shape (flags,
        # checks, error envelopes) matches the gateway exe. Otherwise `@latest`
        # may pull a newer or older CLI that doesn't agree with this gateway.
        $npxPkg = "genexus-mcp@$VersionNoV"
        $initArgs = @('-y', $npxPkg, 'init', '--write-clients', '--no-smoke', '--format', 'json')
        if ($Kb) { $initArgs += @('--kb', $Kb) }
        if ($Gx) { $initArgs += @('--gx', $Gx) }
        if ($InteractiveClients) {
            # Interactive flow can't run with --format json (it expects a TTY for prompts).
            $initArgs = @('-y', $npxPkg, 'init', '--interactive')
            if ($Kb) { $initArgs += @('--kb', $Kb) }
            if ($Gx) { $initArgs += @('--gx', $Gx) }
        } elseif ($Clients) {
            $initArgs += @('--clients', $Clients)
        }

        Write-Step "Registering with AI clients via $npxPkg (gateway = $gatewayExe)"
        $prev = $env:GENEXUS_MCP_GATEWAY_EXE
        $env:GENEXUS_MCP_GATEWAY_EXE = $gatewayExe
        try {
            if ($InteractiveClients) {
                & $npx.Source @initArgs
                if ($LASTEXITCODE -ne 0) { $script:initFailed = $true }
            } else {
                # Capture stdout/stderr, parse the JSON envelope, surface only the
                # human-readable bits. Wall-of-YAML output was the #2 friction point
                # in the v2.6.7 field install: operators couldn't tell pass from fail.
                $output = & $npx.Source @initArgs 2>&1
                $exitCode = $LASTEXITCODE
                $jsonText = ($output | Out-String).Trim()
                $envelope = $null
                try { $envelope = $jsonText | ConvertFrom-Json -ErrorAction Stop } catch { }

                if ($envelope) {
                    if ($exitCode -ne 0) {
                        $script:initFailed = $true
                        Write-Host ''
                        Write-Err 'Init reported a failure:'
                        $errMsg = if ($envelope.PSObject.Properties.Name -contains 'error') { $envelope.error.message } else { 'unknown error (envelope missing error.message)' }
                        Write-Host "    $errMsg" -ForegroundColor Red
                        if ($envelope.PSObject.Properties.Name -contains 'help' -and $envelope.help.Count -gt 0) {
                            Write-Host ''
                            Write-Host 'Suggested fix:' -ForegroundColor Yellow
                            foreach ($h in $envelope.help) { Write-Host "    $h" -ForegroundColor Yellow }
                        }
                        # If verification has failed checks, list them - this is the
                        # gx_installation / kb_path_exists / worker_startup_smoke output.
                        if ($envelope.PSObject.Properties.Name -contains 'ok' -and
                            $envelope.ok.PSObject.Properties.Name -contains 'verification' -and
                            $envelope.ok.verification.PSObject.Properties.Name -contains 'checks') {
                            $failed = $envelope.ok.verification.checks | Where-Object { $_.status -eq 'fail' }
                            if ($failed) {
                                Write-Host ''
                                Write-Host 'Failed verification checks:' -ForegroundColor Yellow
                                foreach ($f in $failed) {
                                    Write-Host ("    [X] {0,-30} {1}" -f $f.id, $f.detail) -ForegroundColor Red
                                }
                            }
                        }
                    } else {
                        # Success - print a one-line confirmation, surface any warnings.
                        $cfgPath = if ($envelope.ok.PSObject.Properties.Name -contains 'configPath') { $envelope.ok.configPath } else { '<unknown>' }
                        $patched = if ($envelope.meta.PSObject.Properties.Name -contains 'patchedClients') { ($envelope.meta.patchedClients -join ', ') } else { '' }
                        Write-Ok "Config written: $cfgPath"
                        if ($patched) { Write-Ok "Patched AI clients: $patched" }
                        if ($envelope.PSObject.Properties.Name -contains 'help' -and $envelope.help.Count -gt 0) {
                            foreach ($h in $envelope.help) { Write-Host "    [i] $h" -ForegroundColor Cyan }
                        }
                    }
                } else {
                    # Couldn't parse - fall back to raw output so the operator still sees something.
                    if ($exitCode -ne 0) { $script:initFailed = $true }
                    Write-Host $jsonText
                }
            }
        } finally {
            if ($null -ne $prev) { $env:GENEXUS_MCP_GATEWAY_EXE = $prev }
            else { Remove-Item env:GENEXUS_MCP_GATEWAY_EXE -ErrorAction SilentlyContinue }
        }
    }
}

# -------------------------------------------------------------------------
# Final report + AI client restart prompt
# -------------------------------------------------------------------------
Write-Host ''
if ($script:initFailed) {
    Write-Warn "genexus-mcp $Version files installed to:"
    Write-Host "     $InstallDir"
    Write-Host ''
    Write-Warn 'Client registration (init) FAILED - see error output above.'
    Write-Warn 'Files are extracted, but no AI client config was written and no config.json was created.'
    Write-Host ''
    Write-Host 'Common causes:' -ForegroundColor Yellow
    Write-Host '  - GeneXus installed in a non-standard path (e.g. GeneXus18u7 vs GeneXus18)'
    Write-Host '  - Not running from inside a KB folder'
    Write-Host ''
    Write-Host 'Fix by re-running with explicit paths:' -ForegroundColor Cyan
    Write-Host '  $s = irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1'
    Write-Host '  & ([scriptblock]::Create($s)) -Kb "C:\KBs\YourKB" -Gx "C:\Path\To\GeneXus18" -Force'
    Write-Host ''
    exit 1
}

Write-Ok "genexus-mcp $Version installed to:"
Write-Host "     $InstallDir"
Write-Host ''
Write-Host 'Paths to whitelist with IT (ASR / Defender):' -ForegroundColor Cyan
Write-Host "  $gatewayExe"
Write-Host "  $workerExe"
Write-Host ''

if (-not $NoClient -and -not $NoRestartPrompt) {
    $running = Get-RunningAiClients
    if ($running.Count -gt 0) {
        Write-Host 'Detected running AI client(s) - they MUST be restarted to load the new MCP config:' -ForegroundColor Cyan
        foreach ($r in $running) {
            Write-Host ("  - {0} (PID: {1})" -f $r.Display, ($r.Pids -join ', '))
        }
        Write-Host ''
        if (Confirm-Action 'Restart them now?' $false) {
            foreach ($r in $running) {
                try {
                    $r.Pids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction Stop }
                    Write-Ok "Stopped $($r.Display)"
                    if ($r.Path -and (Test-Path -LiteralPath $r.Path)) {
                        Start-Sleep -Milliseconds 600
                        Start-Process -FilePath $r.Path | Out-Null
                        Write-Ok "Relaunched $($r.Display)"
                    } else {
                        Write-Warn "Could not relaunch $($r.Display) - original exe path unknown; please start it manually."
                    }
                } catch {
                    Write-Warn "Failed to restart $($r.Display): $($_.Exception.Message)"
                }
            }
        } else {
            Write-Host 'Skipped restart. Restart your AI client manually before using the MCP.' -ForegroundColor Yellow
        }
    } else {
        Write-Host 'Restart your AI client (Claude Desktop / Cursor / Antigravity) to pick up the MCP.' -ForegroundColor Cyan
    }
}
