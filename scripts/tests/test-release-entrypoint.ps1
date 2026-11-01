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
$helperImport = $canonicalSource.IndexOf("release-issues.ps1') -DefineOnly", [StringComparison]::Ordinal)
$helperRestore = $canonicalSource.IndexOf('$DryRun = $releaseDryRunBeforeIssueHelpers', [StringComparison]::Ordinal)
if ($helperImport -lt 0 -or $helperRestore -lt $helperImport) {
    throw 'Canonical release entrypoint must restore DryRun after importing issue helpers.'
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
$paramBlock = $ast.ParamBlock
$paramNames = @($paramBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($expectedParam in @('Minor', 'Patch', 'NoAutoCandidate')) {
    if ($paramNames -notcontains $expectedParam) {
        throw "Canonical release script is missing parameter: -$expectedParam"
    }
}
$candidateCiScript = Join-Path $root 'scripts\release-candidate-ci.ps1'
if (-not (Test-Path -LiteralPath $candidateCiScript -PathType Leaf)) { throw 'release-candidate-ci.ps1 is missing.' }
$ciTokens = $null; $ciErrors = $null
$ciAst = [System.Management.Automation.Language.Parser]::ParseFile($candidateCiScript, [ref]$ciTokens, [ref]$ciErrors)
if ($ciErrors.Count) { throw $ciErrors[0] }
foreach ($funcName in @('Ensure-ReleaseCandidateCi', 'Wait-ReleaseRequiredChecksGreen', 'Cleanup-ReleaseCandidate', 'Test-ReleaseRequiredChecksQuiet')) {
    $fnDef = $ciAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $funcName }, $true)
    if (-not $fnDef) { throw "Candidate CI script is missing function: $funcName" }
}
$minorPatchRun = & pwsh -NoProfile -File (Join-Path $root 'release.ps1') -Minor -Patch -DryRun 2>&1
if ($LASTEXITCODE -eq 0 -or ($minorPatchRun -join "`n") -notmatch 'Cannot specify both -Minor and -Patch') {
    throw 'Running release.ps1 with -Minor and -Patch must fail with conflict guidance.'
}
$minorVersionRun = & pwsh -NoProfile -File (Join-Path $root 'release.ps1') -Minor -Version 9.9.9 -DryRun 2>&1
if ($LASTEXITCODE -eq 0 -or ($minorVersionRun -join "`n") -notmatch 'Cannot specify -Version together with -Minor or -Patch') {
    throw 'Running release.ps1 with -Minor and -Version must fail with conflict guidance.'
}
$issueReferenceDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ChangelogIssueReferences' }, $true)
if (-not $issueReferenceDefinition) { throw 'Canonical release entrypoint is missing changelog issue-reference validation.' }
. ([scriptblock]::Create($issueReferenceDefinition.Extent.Text))
# Validate the issue-reference gate against an isolated fixture so the check
# stays hermetic: mid-release the real CHANGELOG has already promoted its
# ## Unreleased content to the version heading, which is a valid state.
$fixtureChangelog = Join-Path $env:TEMP ('gxmcp-changelog-' + [guid]::NewGuid().ToString('N') + '.md')
try {
    @('# Changelog', '', '## Unreleased', '', '- Fixed a thing ([#210](https://github.com/lennix1337/Genexus18MCP/issues/210)) and another ([#227](https://github.com/lennix1337/Genexus18MCP/issues/227)).', '', '## v0.0.1 - 2026-01-01', '', '- Older.') -join "`r`n" | Set-Content -LiteralPath $fixtureChangelog -Encoding utf8
    Assert-ChangelogIssueReferences -IssueNumbers @(210, 227) -ChangelogPath $fixtureChangelog -Version '0.0.2'
    $missingIssueReferenceFailed = $false
    try { Assert-ChangelogIssueReferences -IssueNumbers @(999999) -ChangelogPath $fixtureChangelog -Version '0.0.2' } catch { $missingIssueReferenceFailed = $true }
    if (-not $missingIssueReferenceFailed) { throw 'Missing changelog issue reference was accepted.' }

    # Mid-release rerun state: the links were already promoted under the
    # version heading and ## Unreleased is empty; the gate must accept it.
    @('# Changelog', '', '## Unreleased', '', '## v0.0.2 - 2026-01-02', '', '### Tracked issues', '', '- [#210](https://github.com/lennix1337/Genexus18MCP/issues/210) — one', '- [#227](https://github.com/lennix1337/Genexus18MCP/issues/227) — two', '', '## v0.0.1 - 2026-01-01', '', '- Older.') -join "`r`n" | Set-Content -LiteralPath $fixtureChangelog -Encoding utf8
    Assert-ChangelogIssueReferences -IssueNumbers @(210, 227) -ChangelogPath $fixtureChangelog -Version '0.0.2'
    $promotedGateFailed = $false
    try { Assert-ChangelogIssueReferences -IssueNumbers @(210, 227) -ChangelogPath $fixtureChangelog -Version '0.0.3' } catch { $promotedGateFailed = $true }
    if (-not $promotedGateFailed) { throw 'Promoted issue links satisfied a gate for a different version.' }
} finally {
    Remove-Item -LiteralPath $fixtureChangelog -ErrorAction SilentlyContinue
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
$publicationVerifyIndex = $releaseSource.IndexOf('scripts\verify-release-publication.ps1', [StringComparison]::Ordinal)
$issueCloseIndex = $releaseSource.IndexOf('Close-ReleaseIssues -ReleaseUrl', [StringComparison]::Ordinal)
if ($publicationVerifyIndex -lt 0 -or $issueCloseIndex -le $publicationVerifyIndex) {
    throw 'Release issues must close only after publication verification.'
}
foreach ($marker in @('publicationState', 'publicationEvidencePath', 'nextAction', 'artifactFingerprint', 'preflightSummaryPath', 'DispatchIfMissing')) {
    if ($releaseSource -notmatch [regex]::Escape($marker)) { throw "Release publication status marker is missing: $marker" }
}
$buildSource = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
if ($buildSource -notmatch '\$artifactGxPath\s*=\s*Get-GxPrimaryInstallPath\s+-Catalog\s+\$gxCatalog' -or
    $buildSource -notmatch '\$buildGxPath\s*=\s*\$artifactGxPath' -or
    $buildSource -notmatch 'New-PublishConfigJson\s+-ArtifactGxPath\s+\$artifactGxPath' -or
    $buildSource -notmatch 'GX_PATH=\$buildGxPath' -or
    $buildSource -match '\$gxPath') {
    throw 'Build must keep the catalog artifact path separate from machine-specific SDK overrides.'
}
$releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
$resumeBuildBlock = [regex]::Match($releaseSource, '(?s)\$canResumeBuild\s*=.*?\n\s*if \(\$canResumeBuild\)').Value
if ([string]::IsNullOrWhiteSpace($resumeBuildBlock) -or
    $releaseSource -notmatch 'Get-GxMcpReleaseArtifactFingerprint' -or
    $resumeBuildBlock -notmatch 'Test-GxMcpReleasePreflightCompatibility') {
    throw 'Release build resume must fail closed when artifact fingerprints are missing.'
}
foreach ($marker in @('Ensure-ReleaseVsixArtifacts', 'nexus-ide.vsix', 'tool_definitions.json', 'Skip-build reuse validated', 'matching preflight inputs', 'Test-ReleasePublicationAlreadyVerified', 'hasCompleteAssetSet', 'remaining issue-closure', 'already closed with this release', 'Verifying existing release manifest', 'Existing publish.zip and checksum reused', 'Reusing existing tag', 'Existing GitHub Release assets reused')) {
    if ($releaseSource -notmatch [regex]::Escape($marker)) { throw "Release artifact reconciliation is missing: $marker" }
}
$resumeManifestGuard = $releaseSource.IndexOf('Step "Verifying existing release manifest"', [StringComparison]::Ordinal)
$writeManifest = $releaseSource.IndexOf('Step "Writing release manifest"', [StringComparison]::Ordinal)
$resumeZipGuard = $releaseSource.IndexOf('Existing publish.zip and checksum reused', [StringComparison]::Ordinal)
$packZip = $releaseSource.IndexOf('publish.zip created', [StringComparison]::Ordinal)
$resumeTagGuard = $releaseSource.IndexOf('Step "Reusing existing tag $tag"', [StringComparison]::Ordinal)
$createTag = $releaseSource.IndexOf("Invoke-Cmd 'git' @('tag'", [StringComparison]::Ordinal)
if ($resumeManifestGuard -lt 0 -or $resumeManifestGuard -gt $writeManifest -or $resumeZipGuard -lt 0 -or $resumeZipGuard -gt $packZip -or $resumeTagGuard -lt 0 -or $resumeTagGuard -gt $createTag) {
    throw 'Resume guards must precede manifest, zip, and tag mutation paths.'
}
Write-Host 'release-entrypoint: wrapper and metadata checks passed' -ForegroundColor Green
