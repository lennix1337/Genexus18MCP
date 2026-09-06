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
