$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wrapper = Get-Content -LiteralPath (Join-Path $root 'scripts\release.ps1') -Raw
if ($wrapper -match '(?im)^\s*(?:git\s|npm\s|dotnet\s|gh\s|Compress-Archive)') {
    throw 'Legacy release entrypoint contains an independent command implementation.'
}
if ($wrapper -notmatch 'release\.ps1') { throw 'Legacy entrypoint does not delegate to the canonical script.' }
if ($wrapper -notmatch 'Push-Location \$root' -or $wrapper -notmatch 'Pop-Location') {
    throw 'Legacy release entrypoint must delegate from the repository root.'
}

$canonicalSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
if ($canonicalSource -notmatch '\$numericVersion' -or $canonicalSource -notmatch 'AssemblyVersion>.*numericVersion\.0') {
    throw 'Canonical release entrypoint must keep prerelease assembly versions numeric.'
}

$output = & pwsh -NoProfile -File (Join-Path $root 'scripts\release.ps1') -NoBump 2>&1
if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'NoBump.*no longer supported') {
    throw '-NoBump did not fail with migration guidance.'
}

$currentVersion = ((Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version).Trim()
$metadata = & python (Join-Path $root 'scripts\verify-release-metadata.py') --root $root --version $currentVersion 2>&1
if ($LASTEXITCODE -ne 0) { throw "Current release metadata is not synchronized: $($metadata -join "`n")" }

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$semverDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-StrictSemVer' }, $true)
if (-not $semverDefinition) { throw 'Canonical release script is missing strict semver validation.' }
. ([scriptblock]::Create($semverDefinition.Extent.Text))
foreach ($valid in @('0.0.0', '1.2.3-rc.1+build.7')) {
    if (-not (Test-StrictSemVer $valid)) { throw "Valid semver was rejected: $valid" }
}
foreach ($invalid in @('01.2.3', '1.02.3', '1.2.03', '1.2', '1.2.3-01')) {
    if (Test-StrictSemVer $invalid) { throw "Invalid semver was accepted: $invalid" }
}
$lockDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-LockfileVersion' }, $true)
if (-not $lockDefinition) { throw 'Canonical release script is missing lockfile synchronization.' }
. ([scriptblock]::Create($lockDefinition.Extent.Text))
$DryRun = $false
function Ok([string]$Message) { }
$fixtureLock = Join-Path $env:TEMP ('gxmcp-lock-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [ordered]@{ name = 'fixture'; version = '2.0.0'; packages = [ordered]@{ '' = [ordered]@{ name = 'fixture'; version = '2.0.0' }; dep = [ordered]@{ version = '1.0.0' } } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fixtureLock -Encoding utf8
    Set-LockfileVersion -Path $fixtureLock -TargetVersion '3.0.1' | Out-Null
    $lockDocument = Get-Content -LiteralPath $fixtureLock -Raw | ConvertFrom-Json -AsHashtable
    if ($lockDocument['version'] -ne '3.0.1' -or $lockDocument['packages']['']['version'] -ne '3.0.1' -or $lockDocument['packages']['dep']['version'] -ne '1.0.0') {
        throw 'Lockfile synchronization changed the wrong version fields.'
    }
}
finally { if (Test-Path -LiteralPath $fixtureLock) { Remove-Item -LiteralPath $fixtureLock -Force -ErrorAction SilentlyContinue } }
$releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
if ($releaseSource -notmatch 'sync-release-metadata\.py' -or
    $releaseSource -notmatch 'config/gx-versions\.json' -or
    $releaseSource -notmatch 'docs/generated/supported-versions\.md') {
    throw 'Canonical release entrypoint must synchronize and commit generated release metadata.'
}
$syncPosition = $releaseSource.IndexOf('sync-release-metadata.py', [StringComparison]::Ordinal)
$dirtyGatePosition = $releaseSource.IndexOf('Checking git working tree', [StringComparison]::Ordinal)
if ($syncPosition -lt 0 -or $dirtyGatePosition -lt 0 -or $syncPosition -gt $dirtyGatePosition) {
    throw 'Canonical release entrypoint must synchronize generated metadata before the dirty-tree gate.'
}
$buildSource = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
if ($buildSource -notmatch '\$artifactGxPath\s*=\s*Get-GxPrimaryInstallPath\s+-Catalog\s+\$gxCatalog' -or
    $buildSource -notmatch '\$buildGxPath\s*=\s*\$artifactGxPath' -or
    $buildSource -notmatch 'InstallationPath\s*=\s*\$artifactGxPath' -or
    $buildSource -notmatch 'GX_PATH=\$buildGxPath' -or
    $buildSource -match '\$gxPath') {
    throw 'Build must keep the catalog artifact path separate from machine-specific SDK overrides.'
}
Write-Host 'release-entrypoint: wrapper and metadata checks passed' -ForegroundColor Green
