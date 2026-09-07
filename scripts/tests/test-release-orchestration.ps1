$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$temp = Join-Path $env:TEMP ('gxmcp-release-orchestration-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $temp 'source'
$publish = Join-Path $temp 'publish'
New-Item -ItemType Directory -Path $repo, (Join-Path $publish 'worker') -Force | Out-Null
try {
    Push-Location $repo
    try {
        git init -q
        git config user.email 'test@example.invalid'
        git config user.name 'release-test'
        Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'committed' -Encoding ascii
        git add source.txt
        git commit -q -m 'fixture source'
        $expectedCommit = (git rev-parse HEAD).Trim()
    } finally { Pop-Location }

    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'worker\GxMcp.Worker.exe') -Value 'worker' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'tool_definitions.json') -Value '[]' -Encoding ascii
    $writer = Join-Path $root 'scripts\write-release-manifest.ps1'
    $manifestPath = & pwsh -NoProfile -File $writer -PublishDirectory $publish -Version 3.0.1 -SourceRoot $repo
    $manifest = Get-Content -LiteralPath ($manifestPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -ne $expectedCommit) { throw "Clean source did not bind to HEAD ($expectedCommit)." }
    $verifier = Join-Path $root 'scripts\verify-release-manifest.py'
    & python $verifier $publish --version 3.0.1 --source-commit $expectedCommit *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Committed manifest was not accepted by the verifier.' }

    Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'dirty' -Encoding ascii
    $dirtyPath = & pwsh -NoProfile -File $writer -PublishDirectory $publish -Version 3.0.1 -SourceRoot $repo
    $dirty = Get-Content -LiteralPath ($dirtyPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    if ($dirty.sourceCommit -ne 'working-tree') { throw 'Dirty source must be marked working-tree.' }
    & python $verifier $publish --version 3.0.1 *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Verifier accepted working-tree provenance.' }

    $zip = Join-Path $temp 'publish.zip'
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
    $sidecar = "$zip.sha256"
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $sidecar -Value "$hash  publish.zip" -Encoding ascii
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $declared = (Get-Content -LiteralPath $sidecar -Raw).Trim().Split()[0]
    if ($actual -ne $declared) { throw 'Release checksum asset does not match publish.zip.' }
    git -C $root check-ignore --no-index -q publish.zip.sha256
    if ($LASTEXITCODE -ne 0) { throw 'Checksum sidecar must be ignored by the source tree.' }

    $releaseSource = Get-Content (Join-Path $root 'release.ps1') -Raw
    $commitIndex = $releaseSource.IndexOf('Committing release source state', [StringComparison]::Ordinal)
    $buildIndex = $releaseSource.IndexOf('# -- 3. Build + zip', [StringComparison]::Ordinal)
    if ($commitIndex -lt 0 -or $buildIndex -lt 0 -or $commitIndex -gt $buildIndex) { throw 'Release source commit must occur before build.' }
    if ($releaseSource -notmatch '''-SourceCommit'', \$releaseSourceCommit') { throw 'Manifest writer is not passed the committed source id.' }
    if ($releaseSource -notmatch '\$releaseExists' -or $releaseSource -notmatch "'release', 'upload'") { throw 'Resume path must upload assets to an existing release instead of creating a duplicate.' }
    Write-Host 'release-orchestration: exact provenance, dirty rejection and checksum asset checks passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
