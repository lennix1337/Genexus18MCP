$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts/release-contract.ps1')
$scriptPath = Join-Path $root 'scripts/verify-release-publication.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Protect-PublicationMessage', 'Write-AtomicJson', 'Get-RequiredAssetNames')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing publication helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$releaseAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$alreadyVerifiedFunction = $releaseAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ReleasePublicationAlreadyVerified' }, $true)
if (-not $alreadyVerifiedFunction) { throw 'Missing release publication recovery function.' }
. ([scriptblock]::Create($alreadyVerifiedFunction.Extent.Text))

$version = [string](Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
$tag = "v$version"
$assets = @(Get-RequiredAssetNames -PackageVersion $version)
if (($assets -join '|') -ne "publish.zip|publish.zip.sha256|nexus-ide-$version.vsix") {
    throw "Required release asset contract changed: $($assets -join ', ')"
}
$redacted = Protect-PublicationMessage 'token=super-secret npm_abcdefghijkl'
if ($redacted -match 'super-secret|abcdefghijkl') { throw 'Publication diagnostics did not redact sensitive values.' }
if ([string]::IsNullOrWhiteSpace($redacted)) { throw 'Publication diagnostic redaction returned no message.' }

$temp = Join-Path $env:TEMP ('gxmcp-publication-test-' + [guid]::NewGuid().ToString('N') + '.json')
$artifactRoot = Join-Path $env:TEMP ('gxmcp-publication-artifacts-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
foreach ($assetName in @('publish.zip', 'publish.zip.sha256', "nexus-ide-$version.vsix")) {
    Set-Content -LiteralPath (Join-Path $artifactRoot $assetName) -Value $assetName -NoNewline -Encoding ascii
}
try {
    Write-AtomicJson -Path $temp -Value ([ordered]@{ schemaVersion = 'gxmcp-release-publication/1'; state = 'verified' })
    $document = Get-Content -LiteralPath $temp -Raw | ConvertFrom-Json
    if ($document.schemaVersion -ne 'gxmcp-release-publication/1' -or $document.state -ne 'verified') {
        throw 'Publication evidence was not written atomically as valid JSON.'
    }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
}

$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($marker in @('ExpectedCommit', 'refs/tags/$Tag^{}', 'npm view "genexus-mcp@$Version" version', 'workflow run release.yml', '--ref $Tag', 'DispatchIfMissing', 'publication')) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Publication verifier marker is missing: $marker" }
}

$fakeBin = Join-Path $env:TEMP ('gxmcp-publication-bin-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
try {
    # The publication verification below is hermetic: git, gh and npm are all
    # stubbed. The commit only has to be a real sha, so derive it from HEAD.
    # Resolving it from the release tag would make this test require that tag to
    # exist, which is false between the release's version commit and its tag
    # creation -- exactly the window the documented resume path runs in, so a
    # retry of the same version could never pass its own preflight.
    $expectedCommit = (git rev-parse HEAD).Trim()
    $assetObjects = foreach ($assetName in $assets) {
        $localAssetPath = Join-Path $artifactRoot $assetName
        [ordered]@{
            name = $assetName
            size = [int64](Get-Item -LiteralPath $localAssetPath).Length
            digest = 'sha256:' + (Get-FileHash -LiteralPath $localAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
            state = 'uploaded'
            updatedAt = '2026-09-23T00:00:00Z'
        }
    }
    $releaseObject = [ordered]@{
        tagName = $tag
        url = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/' + $tag
        publishedAt = '2026-09-23T00:00:00Z'
        isDraft = $false
        isPrerelease = $false
        assets = @($assetObjects)
    }
    $releaseJson = $releaseObject | ConvertTo-Json -Compress -Depth 8
    $runJson = '[{"databaseId":77,"status":"completed","conclusion":"success","headSha":"' + $expectedCommit + '","headBranch":"' + $tag + '","event":"release","createdAt":"2026-09-23T00:00:01Z","url":"https://github.com/lennix1337/Genexus18MCP/actions/runs/77"}]'
    Set-Content -LiteralPath (Join-Path $fakeBin 'gh.cmd') -Value "@echo off`r`nif /I `"%~1`"==`"release`" echo $releaseJson`r`nif /I `"%~1`"==`"run`" echo $runJson`r`nexit /b 0" -Encoding ascii
    $gitContent = @(
        '@echo off',
        "echo %* | findstr /I `"rev-list`" >nul && echo $expectedCommit && exit /b 0",
        "echo %* | findstr /I `"ls-remote`" >nul && echo $expectedCommit refs/tags/$tag^{} && exit /b 0",
        "echo %* | findstr /I `"rev-parse`" >nul && echo $expectedCommit && exit /b 0",
        'exit /b 1'
    ) -join "`r`n"
    Set-Content -LiteralPath (Join-Path $fakeBin 'git.cmd') -Value $gitContent -Encoding ascii
    Set-Content -LiteralPath (Join-Path $fakeBin 'npm.cmd') -Value "@echo [{`"version`":`"$version`",`"gitHead`":`"$expectedCommit`"}]" -Encoding ascii
    $evidence = Join-Path $env:TEMP ('gxmcp-publication-e2e-' + [guid]::NewGuid().ToString('N') + '.json')
    $statusPath = Join-Path $env:TEMP ('gxmcp-publication-status-' + [guid]::NewGuid().ToString('N') + '.json')
    [ordered]@{ state = 'running'; phase = 'verifying-publication'; publicationEvidencePath = $evidence } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    $oldPath = $env:PATH
    try {
        $env:PATH = "$fakeBin;$oldPath"
        if (-not (Test-ReleasePublicationAlreadyVerified -Tag $tag -PackageVersion $version -RepositoryRoot $artifactRoot)) {
            throw 'Release recovery did not recognize the already-verified publication fixture.'
        }
        & pwsh -NoProfile -File $scriptPath -Tag $tag -Version $version -ExpectedCommit $expectedCommit -EvidencePath $evidence -StatusFile $statusPath -ArtifactRoot $artifactRoot -TimeoutSeconds 30 -PollSeconds 1
        if ($LASTEXITCODE -ne 0) { throw "Hermetic publication verification failed with exit code $LASTEXITCODE." }
        $verified = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
        if ($verified.state -ne 'verified' -or $verified.npmVersion -ne $version -or $verified.workflowRunId -ne '77' -or
            [string]::IsNullOrWhiteSpace([string]$verified.assetsUpdatedAtUtc) -or @($verified.assets).Count -ne 3 -or
            [string]$verified.assets[0].digest -notmatch '^sha256:[0-9a-f]{64}$') {
            throw 'Hermetic publication evidence did not reach the verified state.'
        }
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        if ($status.publication.state -ne 'verified' -or $status.publication.npmVersion -ne $version) {
            throw 'Publication verifier did not update the main release status evidence.'
        }
    } finally {
        $env:PATH = $oldPath
        if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $statusPath) { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue }
    }

    $dispatchMarker = Join-Path $fakeBin 'workflow-dispatched.marker'
    $failedRunJson = '[{"databaseId":76,"status":"completed","conclusion":"failure","headSha":"' + $expectedCommit + '","headBranch":"' + $tag + '","event":"release","createdAt":"2026-09-23T00:00:01Z","url":"https://github.com/lennix1337/Genexus18MCP/actions/runs/76"}]'
    $dispatchedRunJson = '[{"databaseId":78,"status":"completed","conclusion":"success","headSha":"' + $expectedCommit + '","headBranch":"' + $tag + '","event":"workflow_dispatch","createdAt":"2026-09-23T00:01:00Z","url":"https://github.com/lennix1337/Genexus18MCP/actions/runs/78"}]'
    $ghDispatchContent = @(
        '@echo off',
        "if /I `"%~1`"==`"release`" echo $releaseJson",
        "if /I `"%~1`"==`"run`" (",
        "  if exist `"$dispatchMarker`" echo $dispatchedRunJson",
        "  if not exist `"$dispatchMarker`" echo $failedRunJson",
        ")",
        "if /I `"%~1`"==`"workflow`" if /I `"%~2`"==`"run`" type nul > `"$dispatchMarker`"",
        'exit /b 0'
    ) -join "`r`n"
    Set-Content -LiteralPath (Join-Path $fakeBin 'gh.cmd') -Value $ghDispatchContent -Encoding ascii
    $dispatchEvidence = Join-Path $env:TEMP ('gxmcp-publication-dispatch-' + [guid]::NewGuid().ToString('N') + '.json')
    $dispatchStatus = Join-Path $env:TEMP ('gxmcp-publication-dispatch-status-' + [guid]::NewGuid().ToString('N') + '.json')
    [ordered]@{ state = 'running'; phase = 'verifying-publication'; publicationEvidencePath = $dispatchEvidence } | ConvertTo-Json | Set-Content -LiteralPath $dispatchStatus -Encoding utf8
    $oldPath = $env:PATH
    try {
        $env:PATH = "$fakeBin;$oldPath"
        & pwsh -NoProfile -File $scriptPath -Tag $tag -Version $version -ExpectedCommit $expectedCommit -EvidencePath $dispatchEvidence -StatusFile $dispatchStatus -ArtifactRoot $artifactRoot -DispatchIfMissing -TimeoutSeconds 30 -PollSeconds 1
        if ($LASTEXITCODE -ne 0) { throw "Failed workflow dispatch verification exited with code $LASTEXITCODE." }
        $dispatched = Get-Content -LiteralPath $dispatchEvidence -Raw | ConvertFrom-Json
        if ($dispatched.state -ne 'verified' -or $dispatched.workflowRunId -ne '78') {
            throw 'Verifier did not ignore the stale failed run and accept the dispatched run.'
        }
    } finally {
        $env:PATH = $oldPath
        if (Test-Path -LiteralPath $dispatchEvidence) { Remove-Item -LiteralPath $dispatchEvidence -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $dispatchStatus) { Remove-Item -LiteralPath $dispatchStatus -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $dispatchMarker) { Remove-Item -LiteralPath $dispatchMarker -Force -ErrorAction SilentlyContinue }
    }
} finally {
    if (Test-Path -LiteralPath $fakeBin) { Remove-Item -LiteralPath $fakeBin -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'release-publication: asset, evidence, tag, npm, redaction and hermetic workflow contracts passed' -ForegroundColor Green
