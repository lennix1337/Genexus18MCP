[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$SourceCommit,
    [string]$VsixPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RelativeArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $root = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release artifact is outside publish directory: $Path"
    }
    return $full.Substring($root.Length).Replace('\', '/')
}

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory not found: $PublishDirectory"
}

$resolvedPublish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$versionValue = $Version.TrimStart('v')
if ($versionValue -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    # A local build is allowed to run from a dirty checkout, but a release
    # manifest must never claim that those bytes came from HEAD. Preserve the
    # honest state marker so release verification can reject the candidate
    # until the source is committed.
    $gitStatus = try { @(git -C $SourceRoot status --porcelain --untracked-files=all 2>$null) } catch { @() }
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($gitStatus -join "`n"))) {
        $SourceCommit = 'working-tree'
    } else {
        $SourceCommit = try { (git -C $SourceRoot rev-parse HEAD).Trim() } catch { 'working-tree' }
    }
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $SourceCommit = 'working-tree' }

$required = @(
    'GxMcp.Gateway.exe',
    'worker\GxMcp.Worker.exe',
    'tool_definitions.json'
)
foreach ($relative in $required) {
    $path = Join-Path $resolvedPublish $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release artifact is missing: $relative"
    }
}

$artifactPaths = New-Object System.Collections.Generic.List[string]
foreach ($relative in $required) { [void]$artifactPaths.Add((Join-Path $resolvedPublish $relative)) }

# Keep a small, reproducible provenance document beside the binaries. It does
# not claim publisher identity; it binds the candidate to the source commit and
# the lockfiles used to assemble the CLI/VSIX dependencies.
$sbomPath = Join-Path $resolvedPublish 'gxmcp-sbom.json'
$lockfiles = @(
    [ordered]@{ name = 'genexus-mcp'; path = 'package-lock.json' },
    [ordered]@{ name = 'nexus-ide'; path = 'src/nexus-ide/package-lock.json' }
)
$components = foreach ($lock in $lockfiles) {
    $lockPath = Join-Path $SourceRoot ($lock.path.Replace('/', '\'))
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        $lockItem = Get-Item -LiteralPath $lockPath
        [ordered]@{
            name = $lock.name
            lockfile = $lock.path
            size = [int64]$lockItem.Length
            sha256 = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
$sbom = [ordered]@{
    schemaVersion = 'gxmcp-provenance/1'
    version = $versionValue
    sourceCommit = $SourceCommit
    sourceCommitPolicy = 'exact-tag'
    components = @($components)
}
[IO.File]::WriteAllText(
    $sbomPath,
    (($sbom | ConvertTo-Json -Depth 12) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
[void]$artifactPaths.Add($sbomPath)

$vsixCandidate = $VsixPath
if ([string]::IsNullOrWhiteSpace($vsixCandidate)) {
    $vsixCandidate = Join-Path $resolvedPublish 'nexus-ide.vsix'
}
if (Test-Path -LiteralPath $vsixCandidate -PathType Leaf) {
    $resolvedVsix = [IO.Path]::GetFullPath($vsixCandidate)
    if ($resolvedVsix.StartsWith($resolvedPublish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        [void]$artifactPaths.Add($resolvedVsix)
    } else {
        throw "VSIX must be inside publish directory: $vsixCandidate"
    }
}

$artifacts = foreach ($path in $artifactPaths | Sort-Object -Unique) {
    $item = Get-Item -LiteralPath $path
    [ordered]@{
        path = Get-RelativeArtifactPath -Path $path
        size = [int64]$item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$schemaPath = Join-Path $resolvedPublish 'tool_definitions.json'
$manifest = [ordered]@{
    schemaVersion = 'gxmcp-release-manifest/1'
    version = $versionValue
    sourceCommit = $SourceCommit
    sourceCommitPolicy = 'exact-tag'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    runtime = [ordered]@{
        gateway = 'net10.0-windows'
        worker = 'net48-x86'
        node = '>=22.0.0'
        recommendedNode = '24 LTS'
    }
    protocolVersions = @('2025-11-25', '2026-07-28')
    schema = 'tool_definitions.json'
    schemaSha256 = (Get-FileHash -LiteralPath $schemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    provenance = 'gxmcp-sbom.json'
    artifacts = @($artifacts)
}

$manifestPath = Join-Path $resolvedPublish 'gxmcp-manifest.json'
$json = $manifest | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output $manifestPath
