$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\install-transaction.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-install-contract-' + [guid]::NewGuid().ToString('N'))
$passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Message)
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed $Message
}

function New-TestArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [switch]$Manifest,
        [string]$ManifestVersion = '3.0.0'
    )

    $payload = Join-Path $Directory ('payload-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $payload 'worker') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'GxMcp.Gateway.exe') -Value 'gateway-v3' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $payload 'worker\GxMcp.Worker.exe') -Value 'worker-v3' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $payload 'tool_definitions.json') -Value '[]' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $payload 'gxmcp-sbom.json') -Value '{}' -Encoding ascii

    if ($Manifest) {
        $artifacts = @()
        foreach ($relative in @('GxMcp.Gateway.exe', 'worker\GxMcp.Worker.exe', 'tool_definitions.json', 'gxmcp-sbom.json')) {
            $path = Join-Path $payload $relative
            $item = Get-Item -LiteralPath $path
            $artifacts += [ordered]@{
                path = $relative.Replace('\', '/')
                size = [int64]$item.Length
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        [ordered]@{
            schemaVersion = 'gxmcp-release-manifest/1'
            version = $ManifestVersion
            sourceCommit = 'fixture'
            generatedAtUtc = [DateTime]::UtcNow.ToString('o')
            runtime = [ordered]@{
                gateway = 'net10.0-windows'
                worker = 'net48-x86'
                node = '>=22.0.0'
                recommendedNode = '24 LTS'
            }
            protocolVersions = @('2025-11-25', '2026-07-28')
            schema = 'tool_definitions.json'
            schemaSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'tool_definitions.json') -Algorithm SHA256).Hash.ToLowerInvariant()
            provenance = 'gxmcp-sbom.json'
            artifacts = $artifacts
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'gxmcp-manifest.json') -Encoding utf8
    }

    $zip = Join-Path $Directory ('fixture-' + [guid]::NewGuid().ToString('N') + '.zip')
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force
    return $zip
}

try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $validZip = New-TestArchive -Directory $root -Manifest
    $staging = Join-Path $root 'valid-stage'
    $valid = Test-InstallArchive -ZipPath $validZip -StagingDirectory $staging -ExpectedVersion 'v3.0.0' -RequireManifest
    Assert-True (Test-Path -LiteralPath $valid.GatewayPath -PathType Leaf) 'valid archive gateway path'
    Assert-True ($valid.Manifest.version -eq '3.0.0') 'manifest version is returned'
    $passed++

    $missingManifestZip = New-TestArchive -Directory $root
    Assert-Fails { Test-InstallArchive -ZipPath $missingManifestZip -StagingDirectory (Join-Path $root 'missing-stage') -ExpectedVersion 'v3.0.0' -RequireManifest } 'missing manifest must fail closed'
    $passed++

    $wrongVersionZip = New-TestArchive -Directory $root -Manifest -ManifestVersion '3.0.1'
    Assert-Fails { Test-InstallArchive -ZipPath $wrongVersionZip -StagingDirectory (Join-Path $root 'wrong-version-stage') -ExpectedVersion 'v3.0.0' -RequireManifest } 'manifest version mismatch must fail'
    $passed++

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $traversalZip = Join-Path $root 'traversal.zip'
    $archive = [System.IO.Compression.ZipFile]::Open($traversalZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try { $archive.CreateEntry('../escape.txt') | Out-Null } finally { $archive.Dispose() }
    Assert-Fails { Test-InstallArchive -ZipPath $traversalZip -StagingDirectory (Join-Path $root 'traversal-stage') -ExpectedVersion 'v3.0.0' } 'zip traversal must fail before extraction'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'escape.txt'))) 'zip traversal did not write outside staging'
    $passed++

    $install = Join-Path $root 'install'
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $install 'GxMcp.Gateway.exe') -Value 'old-gateway' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $install 'config.json') -Value '{"kb":"operator"}' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $install 'version.txt') -Value 'v2.57.0' -Encoding ascii
    $swap = Invoke-ValidatedInstall -ZipPath $validZip -InstallDirectory $install -Version 'v3.0.0' -RequireManifest -Probe { param($path) $true }
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'GxMcp.Gateway.exe') -Raw).Trim() -eq 'gateway-v3') 'validated install swaps staged gateway'
    Assert-True ((Get-Content -LiteralPath (Join-Path $install 'config.json') -Raw).Trim() -eq '{"kb":"operator"}') 'validated install preserves config'
    Assert-True ($swap.BackupDirectory -and (Test-Path -LiteralPath $swap.BackupDirectory)) 'validated install retains previous directory'
    $passed++

    $rollbackInstall = Join-Path $root 'rollback-install'
    New-Item -ItemType Directory -Path $rollbackInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rollbackInstall 'GxMcp.Gateway.exe') -Value 'rollback-old' -Encoding ascii
    Assert-Fails { Invoke-ValidatedInstall -ZipPath $validZip -InstallDirectory $rollbackInstall -Version 'v3.0.0' -RequireManifest -Probe { param($path) $false } } 'failed staged probe must abort before swap'
    Assert-True ((Get-Content -LiteralPath (Join-Path $rollbackInstall 'GxMcp.Gateway.exe') -Raw).Trim() -eq 'rollback-old') 'failed probe preserves old installation'
    $passed++

    $installerSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\install.ps1') -Raw
    Assert-True ($installerSource.Contains('Invoke-ValidatedInstall')) 'installer uses transactional staging helper'
    Assert-True ($installerSource.Contains('-RequireManifest:$isV3Release')) 'installer gates v3 manifest validation'
    Assert-True (-not $installerSource.Contains('Remove-Item -Path (Join-Path $InstallDir ''*'')')) 'installer does not destructively wipe the live directory'
    $passed += 3

    Write-Host "install-contract: $passed assertions passed" -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
