$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-manifest-' + [guid]::NewGuid().ToString('N'))
$passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

try {
    $publish = Join-Path $root 'publish'
    New-Item -ItemType Directory -Path (Join-Path $publish 'worker') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'worker\GxMcp.Worker.exe') -Value 'worker' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'tool_definitions.json') -Value '[]' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'nexus-ide.vsix') -Value 'vsix' -Encoding ascii

    $manifestPath = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\write-release-manifest.ps1') `
        -PublishDirectory $publish -Version '3.0.0-rc.1' -SourceRoot $PSScriptRoot -SourceCommit 'fixture-commit'
    $manifest = Get-Content -LiteralPath ($manifestPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 'gxmcp-release-manifest/1') 'manifest schema version'
    Assert-True ($manifest.version -eq '3.0.0-rc.1') 'manifest version'
    Assert-True ($manifest.sourceCommit -eq 'fixture-commit') 'manifest source commit'
    Assert-True ($manifest.sourceCommitPolicy -eq 'exact-tag') 'manifest source commit policy'
    Assert-True (@($manifest.protocolVersions) -contains '2025-11-25') 'legacy protocol declared'
    Assert-True (@($manifest.protocolVersions) -contains '2026-07-28') 'modern protocol declared'
    Assert-True ($manifest.provenance -eq 'gxmcp-sbom.json') 'provenance document is declared'
    Assert-True (@($manifest.artifacts).Count -eq 5) 'all required artifacts plus VSIX and provenance are listed'
    Assert-True ((Test-Path -LiteralPath (Join-Path $publish 'gxmcp-sbom.json'))) 'provenance document was generated'
    $gateway = @($manifest.artifacts | Where-Object path -eq 'GxMcp.Gateway.exe')[0]
    Assert-True ($gateway.sha256 -eq (Get-FileHash -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) 'gateway hash matches bytes'
    Assert-True ($manifest.schemaSha256 -eq @($manifest.artifacts | Where-Object path -eq 'tool_definitions.json')[0].sha256) 'schema hash matches artifact'
    $passed += 10

    Set-Content -LiteralPath (Join-Path $publish 'tool_definitions.json') -Value '[{"changed":true}]' -Encoding ascii
    $manifestPath = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\write-release-manifest.ps1') `
        -PublishDirectory $publish -Version '3.0.0-rc.1' -SourceRoot $PSScriptRoot -SourceCommit 'fixture-commit'
    $manifest2 = Get-Content -LiteralPath ($manifestPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    Assert-True ($manifest2.schemaSha256 -ne $manifest.schemaSha256) 'schema change changes manifest hash'
    $passed++

    Write-Host "release-manifest: $passed assertions passed" -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
