$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$contractPath = Join-Path $root 'scripts/release-contract.ps1'
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw 'Shared release contract helper is missing.'
}
. $contractPath

$temp = Join-Path $env:TEMP ('gxmcp-release-contract-' + [guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $temp 'repo'
$publish = Join-Path $artifactRoot 'publish'
New-Item -ItemType Directory -Path (Join-Path $publish 'worker') -Force | Out-Null
try {
    $artifactContents = [ordered]@{
        'publish/GxMcp.Gateway.exe' = 'gateway'
        'publish/worker/GxMcp.Worker.exe' = 'worker'
        'publish/tool_definitions.json' = '{}'
        'publish/nexus-ide.vsix' = 'vsix'
    }
    foreach ($entry in $artifactContents.GetEnumerator()) {
        $path = Join-Path $artifactRoot ($entry.Key -replace '/', '\')
        [IO.File]::WriteAllText($path, [string]$entry.Value, [Text.UTF8Encoding]::new($false))
    }
    $sourceBinaryContents = [ordered]@{
        'src/GxMcp.Gateway/bin/Release/net10.0-windows/GxMcp.Gateway.exe' = 'gateway'
        'src/GxMcp.Worker/bin/x86/Release/GxMcp.Worker.exe' = 'worker'
    }
    foreach ($entry in $sourceBinaryContents.GetEnumerator()) {
        $path = Join-Path $artifactRoot ($entry.Key -replace '/', '\')
        $parent = Split-Path -Parent $path
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::WriteAllText($path, [string]$entry.Value, [Text.UTF8Encoding]::new($false))
    }
    $processTrxRoot = Join-Path $temp 'process-trx'
    New-Item -ItemType Directory -Path $processTrxRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $processTrxRoot 'process.trx'), '<TestRun><ResultSummary><Counters total="4" /></ResultSummary></TestRun>')

    $parts = @(
        foreach ($relativePath in $artifactContents.Keys) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot ($relativePath -replace '/', '\')) -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relativePath=$hash"
        }
    )
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedPayload = $parts -join "`n"
        $expectedFingerprint = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($expectedPayload))) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
    $fingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $artifactRoot
    if ($fingerprint -cne $expectedFingerprint) {
        throw "Canonical artifact fingerprint mismatch: expected $expectedFingerprint, got $fingerprint."
    }

    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Value 'publish-zip' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish.zip.sha256') -Value 'checksum' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'nexus-ide-3.9.1.vsix') -Value 'vsix' -NoNewline
    $remoteAssets = @(
        foreach ($name in @('publish.zip', 'publish.zip.sha256', 'nexus-ide-3.9.1.vsix')) {
            $path = Join-Path $artifactRoot $name
            [pscustomobject]@{
                name = $name
                size = (Get-Item -LiteralPath $path).Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                state = 'uploaded'
                updatedAt = '2026-09-24T12:00:00Z'
            }
        }
    )
    $assetState = Get-GxMcpReleaseAssetVerification -Assets $remoteAssets -RepositoryRoot $artifactRoot -Version '3.9.1'
    if (-not $assetState.isValid -or @($assetState.assets).Count -ne 3) {
        throw "Exact remote assets did not match local bytes: $($assetState.error)"
    }
    $remoteAssets[0].digest = 'sha256:' + ('0' * 64)
    $assetState = Get-GxMcpReleaseAssetVerification -Assets $remoteAssets -RepositoryRoot $artifactRoot -Version '3.9.1'
    if ($assetState.isValid) { throw 'A stale remote asset digest must invalidate publication.' }
    $remoteAssets[0].digest = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $legacyAssets = @($remoteAssets | Where-Object name -eq 'publish.zip')
    $legacyState = Get-GxMcpReleaseAssetVerification -Assets $legacyAssets -RepositoryRoot $artifactRoot -Version '2.6.8' -AllowLegacy
    if (-not $legacyState.isValid) { throw 'Legacy publish.zip-only recovery must remain supported.' }
    $legacyAssets[0].digest = $null
    $legacyState = Get-GxMcpReleaseAssetVerification -Assets $legacyAssets -RepositoryRoot $artifactRoot -Version '2.6.8' -AllowLegacy
    if (-not $legacyState.isValid) { throw 'Legacy publish.zip without a GitHub digest must remain supported.' }

    $catalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @([pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\SDK\GeneXus18' })
    }
    $inputs = Resolve-GxMcpReleasePreflightInputs `
        -Root $artifactRoot `
        -Catalog $catalog `
        -GxPath 'C:\SDK\GeneXus18' `
        -Version '3.9.1' `
        -SourceCommit ('a' * 40) `
        -LiveKbPath 'C:\KBs\Fixture' `
        -LiveFixtureManifest 'C:\Fixtures\fixture.json' `
        -RequireLive `
        -RequireBuildAll `
        -ArtifactFingerprint $fingerprint
    $summary = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-preflight/1'
        status = 'passed'
        root = $inputs.root
        version = $inputs.version
        sourceCommit = $inputs.sourceCommit
        gxPath = $inputs.gxPath
        liveKbPath = $inputs.liveKbPath
        liveMode = $inputs.liveMode
        liveMajors = @($inputs.liveMajors)
        liveGxPathMap = @($inputs.liveGxPathMap)
        liveFixtureManifest = $inputs.liveFixtureManifest
        liveKbSource = $inputs.liveKbSource
        liveFixtureSource = $inputs.liveFixtureSource
        requireLive = $inputs.requireLive
        requireBuildAll = $inputs.requireBuildAll
        skipLive = $inputs.skipLive
        skipWarningBaseline = $inputs.skipWarningBaseline
        artifactFingerprint = $inputs.artifactFingerprint
        processSmokeMode = 'serial-after-parallel'
        processSmokeTestCount = 4
        processSmokeResultsPath = $processTrxRoot
        processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $artifactRoot
        phases = @(
            foreach ($phaseName in (Get-GxMcpMandatoryPreflightPhaseNames)) {
                $command = switch ($phaseName) {
                    'release metadata parity' { 'python scripts/verify-release-metadata.py --version 3.9.1' }
                    'tool contract validation' { 'python scripts/validate-tool-contracts.py' }
                    'operation contract inventory' { 'python scripts/generate-operation-contract-inventory.py --check' }
                    'v3 plan readiness' { 'python scripts/validate-v3-plan.py --require-ready' }
                    'warning baseline documentation parity' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -ValidateOnly' }
                    'Python script tests' { 'python -m unittest discover -s scripts/tests -v' }
                    'PowerShell script tests' { 'pwsh -File scripts/tests/run-release-script-tests.ps1' }
                    'CLI tests' { 'npm test' }
                    'CLI lint' { 'npm run lint' }
                    'Nexus IDE checks' { 'npm --prefix src/nexus-ide run check' }
                    'solution build and tests' { 'dotnet test Genexus18MCP.sln -c Release --filter Category!=ProcessSmoke -v:minimal' }
                    'solution process smoke tests' { 'dotnet test Genexus18MCP.sln -c Release --no-build --no-restore --filter Category=ProcessSmoke --logger trx;LogFilePrefix=process-smoke --results-directory C:/temp/process-smoke -v:minimal' }
                    'Release warning baseline' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -BaselineFile docs/build_warning_baseline.json' }
                    'live KB gate' { 'pwsh -File scripts/test-live.ps1 -KbPath C:/KBs/Fixture -SkipBuild' }
                }
                [pscustomobject]@{ name = $phaseName; status = 'passed'; command = $command; exitCode = 0 }
            }
        )
    }
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed)) {
        throw 'Matching release preflight inputs must be reusable.'
    }
    if (-not (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs)) {
        throw 'A complete matching preflight summary must be a valid skip certificate.'
    }
    $summary.processSmokeTestCount = 0
    if (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs) {
        throw 'A process lane that selected zero tests must not certify release reuse.'
    }
    $summary.processSmokeTestCount = 4
    $summary.phases[11].status = 'timeout'
    if (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs) {
        throw 'A timed-out mandatory process phase must not certify release reuse.'
    }
    $summary.phases[11].status = 'passed'

    $summary.liveFixtureManifest = 'C:\Fixtures\different.json'
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A different live fixture manifest must invalidate preflight reuse.'
    }
    $summary.liveFixtureManifest = $inputs.liveFixtureManifest

    $summary.requireBuildAll = $false
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A weaker Build All policy must not reuse a stricter preflight.'
    }
    $summary.requireBuildAll = $true

    $summary.skipWarningBaseline = $true
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A skipped warning baseline must not reuse a complete preflight.'
    }
    $summary.skipWarningBaseline = $false

    $summary.status = 'failed'
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs)) {
        throw 'Build reuse should be allowed to rerun after a matching failed preflight.'
    }
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'SkipBuild and SkipTests must require a passed preflight.'
    }
    $summary.status = 'passed'

    $summary.PSObject.Properties.Remove('liveFixtureManifest')
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A legacy summary without live gate identity must fail closed.'
    }

    $remoteAssets[0].digest = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $snapshotRecord = [pscustomobject]@{ number = 42; title = 'Issue'; url = 'https://example.invalid/issues/42' }
    $snapshot = [pscustomobject]@{
        schema = 'gxmcp-release-issues/1'; version = '3.9.1'; tag = 'v3.9.1'; issues = @($snapshotRecord)
    }
    if (-not (Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'A valid issue snapshot shape was rejected.' }
    $snapshot.issues = $null
    if ((Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'A null issue snapshot was accepted.' }
    $snapshot.issues = @($snapshotRecord, $snapshotRecord)
    if ((Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'Duplicate issue snapshot records were accepted.' }

    $publicationStatus = [pscustomobject]@{ version = '3.9.1'; tag = 'v3.9.1'; tagCommit = ('a' * 40) }
    $publicationEvidence = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-publication/1'; state = 'verified'; tag = 'v3.9.1'; version = '3.9.1'
        expectedCommit = ('a' * 40); sourceCommit = ('a' * 40); tagCommit = ('a' * 40)
        npmVersion = '3.9.1'; npmGitHead = ('a' * 40); releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.9.1'; assetsUpdatedAtUtc = '2026-09-24T12:00:00Z'
        workflowRunId = '77'; workflowStatus = 'completed'; workflowConclusion = 'success'; workflowUrl = 'https://github.com/lennix1337/Genexus18MCP/actions/runs/77'
        releasePublishedAtUtc = '2026-09-24T11:59:00Z'; assets = $remoteAssets
    }
    if (-not (Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence -RepositoryRoot $artifactRoot).Valid) {
        throw 'Complete publication evidence must validate.'
    }
    $publicationEvidence.state = 'failed'
    if ((Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence).Valid) {
        throw 'Failed publication evidence must not validate.'
    }
    $publicationEvidence.state = 'verified'
    $publicationEvidence.assets = @($publicationEvidence.assets | Select-Object -First 2)
    if ((Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence).Valid) {
        throw 'Publication evidence with missing assets must not validate.'
    }

    $run = [pscustomobject]@{
        headSha = ('a' * 40); headBranch = 'v3.9.1'; event = 'release'; createdAt = '2026-09-24T12:00:01Z'
    }
    if (-not (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -MinimumCreatedAtUtc '2026-09-24T12:00:00Z')) {
        throw 'The exact release workflow run must match.'
    }
    $run.createdAt = '2026-09-24T11:59:59Z'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -MinimumCreatedAtUtc '2026-09-24T12:00:00Z') {
        throw 'A workflow that predates the current assets must not verify them.'
    }
    $run.createdAt = '2026-09-24T12:00:01Z'
    $run.headBranch = 'v3.9.0'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'A same-commit workflow for another tag must not match.'
    }
    $run.headBranch = 'v3.9.1'; $run.event = 'push'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'A same-tag push workflow must not match release publication.'
    }
    $run.event = 'workflow_dispatch'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'Manual repair must not match a first-publication verification.'
    }
    if (-not (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -AllowDispatch)) {
        throw 'An exact manual repair workflow must match an idempotent resume.'
    }

    foreach ($relativePath in @('release.ps1', 'scripts/release-preflight.ps1', 'scripts/release-doctor.ps1', 'scripts/verify-release-publication.ps1')) {
        $source = Get-Content -LiteralPath (Join-Path $root ($relativePath -replace '/', '\')) -Raw
        if ($source -notmatch 'release-contract\.ps1') {
            throw "$relativePath does not use the shared release contract."
        }
    }

    # Get-GxMcpReleaseProcessSmokeFingerprint proves the published Gateway is the
    # same build the test lanes ran by comparing publish\GxMcp.Gateway.exe with
    # the non-RID build output byte-for-byte; that pair has no alternate source
    # path. `dotnet publish -o <dir>` never writes the non-RID build output, so
    # build.ps1 must issue a Release *build* for the Gateway. Without it the
    # certificate silently breaks as soon as the copied assembly goes stale,
    # which a commit does on its own because the SDK stamps the revision into it.
    $contractSource = Get-Content -LiteralPath $contractPath -Raw
    if ($contractSource -notmatch "Source = 'src/GxMcp\.Gateway/bin/Release/net10\.0-windows/GxMcp\.Gateway\.exe'") {
        throw 'The process-smoke fingerprint no longer compares a non-RID Gateway Release build.'
    }
    $gatewayPairBlock = [regex]::Match($contractSource, "(?s)\{\s*Source = 'src/GxMcp\.Gateway.*?\}").Value
    if ($gatewayPairBlock -match 'AlternateSource') {
        throw 'The Gateway fingerprint pair must stay bound to a single source build path.'
    }
    $buildScriptSource = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
    if (-not $buildScriptSource.Contains('("build", $gatewayProject, "-c", "Release"')) {
        throw 'build.ps1 must build the Gateway in Release so the process-smoke fingerprint has a source binary to compare against publish.'
    }
    # The Worker is the other half of the fingerprint pair, and the solution
    # platform mapping is what decides it. Release|Any CPU maps the Worker to the
    # x86 project platform, so every test lane and the process-smoke lane exercise
    # an x86-platform build. Compiling the project directly instead (AnyCPU)
    # produces different bytes: the certificate refuses to bind the process-smoke
    # binaries to publish, and the published Worker is not the tested one.
    $slnSource = Get-Content -LiteralPath (Join-Path $root 'Genexus18MCP.sln') -Raw
    $workerProjectEntry = [regex]::Match($slnSource, '(?s)Project\("\{[^}]+\}"\) = "GxMcp\.Worker", "src\\GxMcp\.Worker\\GxMcp\.Worker\.csproj", "\{(?<guid>[0-9A-Fa-f-]+)\}"')
    if (-not $workerProjectEntry.Success) { throw 'Could not resolve the Worker project GUID from the solution.' }
    $workerGuid = $workerProjectEntry.Groups['guid'].Value.ToUpperInvariant()
    $releaseMapping = [regex]::Match($slnSource, "\{$workerGuid\}\.Release\|Any CPU\.ActiveCfg\s*=\s*(?<platform>[^\r\n]+)")
    if (-not $releaseMapping.Success) { throw 'The solution no longer maps Release|Any CPU for the Worker.' }
    # "Release|x86" is configuration|platform: compare the platform half.
    $solutionWorkerPlatform = ($releaseMapping.Groups['platform'].Value.Trim() -split '\|')[-1].Trim()
    $buildScriptWorkerPlatform = ''
    if ($buildScriptSource -match '\$workerProject,\s*"-c",\s*"Release"[^\r\n]*?"-p:Platform=(?<platform>[^"]+)"') {
        $buildScriptWorkerPlatform = [string]$Matches['platform']
    }
    if ($solutionWorkerPlatform -ne $buildScriptWorkerPlatform) {
        throw "The Worker Release platform must match the solution ($solutionWorkerPlatform); build.ps1 builds '$buildScriptWorkerPlatform', so publish/ would not contain the tested binary."
    }

    # --- Nexus IDE host-blocker classification (issue #318) -----------------
    # The classifier may only label a cause. It must never turn a failed phase
    # into 'unavailable', because the aggregate accepts that as an approved
    # terminal status and would certify a release that ran no Electron test.
    $vscodeSignature = 'Error: Code is currently being updated. Please wait for the update to complete before launching.'
    $nexusOutput = "compile ok`nlint ok`n$vscodeSignature`nExit code: 1`nFailed to run tests TestRunFailedError: Test run failed with code 1"

    $other = Get-GxMcpPreflightHostBlocker -Name 'PowerShell script tests' -Stdout $nexusOutput
    if ($null -ne $other) { throw 'The VS Code signature must not label an unrelated phase.' }
    $noSignature = Get-GxMcpPreflightHostBlocker -Name 'Nexus IDE checks' -Stdout "compile ok`nlint ok`nExit code: 1" -MutexProbe { param($r) $true }
    if ($null -ne $noSignature) { throw 'A plain Nexus IDE failure must stay unlabelled.' }

    $probeCalls = 0
    $confirmed = Get-GxMcpPreflightHostBlocker -Name 'Nexus IDE checks' -Stdout $nexusOutput -MutexProbe { param($r) $script:probeCalls++; return $true }
    if ($null -eq $confirmed) { throw 'The VS Code updater signature was not classified.' }
    if ([string]$confirmed.code -ne 'vscode-update-in-progress') { throw "Unexpected host blocker code: $($confirmed.code)." }
    if ($confirmed.mutexConfirmed -isnot [bool] -or -not $confirmed.mutexConfirmed) { throw 'A confirmed mutex must be reported as a boolean true.' }
    if ([string]::IsNullOrWhiteSpace([string]$confirmed.remediation) -or
        [string]$confirmed.remediation -notmatch 'ResumeSummaryPath') { throw 'The remediation must tell the operator how to recover.' }
    if ($script:probeCalls -ne 1) { throw 'The injected mutex probe must run exactly once per classification.' }

    $unconfirmed = Get-GxMcpPreflightHostBlocker -Name 'Nexus IDE checks' -Stderr $vscodeSignature -MutexProbe { param($r) return $null }
    if ($null -eq $unconfirmed -or $null -ne $unconfirmed.mutexConfirmed) { throw 'An unestablished probe must report unconfirmed, not false.' }
    $throwing = Get-GxMcpPreflightHostBlocker -Name 'Nexus IDE checks' -Stdout $nexusOutput -MutexProbe { param($r) throw 'probe exploded' }
    if ($null -eq $throwing -or $null -ne $throwing.mutexConfirmed) { throw 'A throwing probe must degrade to unconfirmed.' }

    # The classifier exposes no status/exitCode parameter, so it cannot alter an
    # outcome. Lock the wiring too: the classification must run only on an
    # already-failed phase and must never assign the outcome itself.
    $preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts/release-preflight.ps1') -Raw
    $hostBlockerBlock = [regex]::Match($preflightSource, '(?s)if \(\$null -ne \$hostBlocker\) \{.*?\r?\n        \}').Value
    if ([string]::IsNullOrWhiteSpace($hostBlockerBlock)) { throw 'The preflight no longer attaches host-blocker details to a failed phase.' }
    if ($hostBlockerBlock -match '\$(phase\.status|phase\.exitCode)\s*=') {
        throw 'Host-blocker classification must never change the phase outcome.'
    }
    $failedGuard = [regex]::Match($preflightSource, '(?s)if \(\$phase\.status -eq ''failed''\) \{.*?Get-GxMcpPreflightHostBlocker')
    if (-not $failedGuard.Success) {
        throw 'Host-blocker classification must be guarded by an already-failed phase.'
    }

    # Real probe: build a runtime layout that mirrors runTest.ts resolution and
    # prove the top-level win32MutexName wins over the 'embedded' sub-product key.
    $runtimeRoot = Join-Path $temp 'runtime-repo'
    $appDir = Join-Path $runtimeRoot 'src\nexus-ide\.vscode-test\vscode-win32-x64-archive-9.9.9\abc123\resources\app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $appDir 'product.json'), '{"win32MutexName":"gxmcpfixture","win32VersionedUpdate":true,"embedded":{"win32MutexName":"gxpembedded"}}', [Text.UTF8Encoding]::new($false))
    $resolvedMutex = Get-GxMcpVsCodeUpdateMutexName -RepositoryRoot $runtimeRoot -Version '9.9.9'
    if ($resolvedMutex -ne 'gxmcpfixture-updating') { throw "The probe must use the top-level win32MutexName: got '$resolvedMutex'." }
    if ($null -ne (Get-GxMcpVsCodeUpdateMutexName -RepositoryRoot $runtimeRoot -Version '0.0.0')) {
        throw 'An unresolvable runtime version must not be guessed.'
    }
    if ($null -ne (Get-GxMcpVsCodeUpdateMutexName -RepositoryRoot $runtimeRoot)) {
        throw 'Without an explicit version the probe must honor the runTest.ts default/env rule, not a stale layout.'
    }

    $held = [System.Threading.Mutex]::new($false, 'gxmcp-release-contract-fixture')
    try {
        if ((Test-GxMcpVsCodeUpdateMutexHeld -MutexName 'gxmcp-release-contract-fixture') -ne $true) {
            throw 'The probe must detect a mutex that is held.'
        }
    } finally {
        $held.Dispose()
    }
    if ((Test-GxMcpVsCodeUpdateMutexHeld -MutexName 'gxmcp-release-contract-absent') -ne $false) {
        throw 'The probe must report a free mutex as false.'
    }
    if ($null -ne (Test-GxMcpVsCodeUpdateMutexHeld -MutexName '   ')) {
        throw 'A blank mutex name must be unconfirmed rather than false.'
    }

    Write-Host 'release-contract: canonical fingerprint, full preflight identity and exact workflow matching passed' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
