$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\live-fixture.ps1')
. (Join-Path $root 'scripts\live-harness.ps1')
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot '../test-live.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-OwnedDescendants', 'New-LiveGatewayConfig', 'Get-LiveBackendEvidence', 'Get-LiveKbOpenFailure', 'Test-HttpPortBindable', 'Get-FreeHttpPort')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$helperAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'scripts/live-fixture.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-LiveFixtureHash', 'Assert-LiveFixture')) {
    $definition = $helperAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing shared fixture function: $name" }
}
$productionSource = Get-Content (Join-Path $PSScriptRoot '../test-live.ps1') -Raw
if ($productionSource -notmatch [regex]::Escape("'--alias', 'live-fixture'")) {
    throw 'The live benchmark must receive the same alias used by its isolated config.'
}
if ($productionSource -notmatch [regex]::Escape("ResolutionPolicy = 'strict'")) {
    throw 'The live config must keep strict KB resolution enabled.'
}
if ($productionSource -match 'DefaultKb\s*=|KBs\s*=') {
    throw 'The live config must not auto-open a second KB alias.'
}
foreach ($project in @('gatewayProject')) {
    $invocation = '& dotnet test $' + $project + ' -c Release --no-build --no-restore'
    if (-not $productionSource.Contains($invocation)) {
        throw "Live tests must consume previously compiled Release tests without rebuilding: $project"
    }
}
# The solution builds Worker.Tests as Release|x86 (bin\x86\Release). Without the same
# platform, `dotnet test --no-build` reads bin\Release, which only a stale checkout has.
$solutionSource = Get-Content (Join-Path $root 'Genexus18MCP.sln') -Raw
$workerGuid = [regex]::Match($solutionSource, '"GxMcp\.Worker\.Tests", "[^"]+", "\{([0-9A-F-]+)\}"').Groups[1].Value
$workerPlatform = [regex]::Match($solutionSource, '\{' + $workerGuid + '\}\.Release\|Any CPU\.Build\.0 = Release\|(\S+)').Groups[1].Value
if (-not $workerPlatform) { throw 'Cannot resolve the Release platform the solution builds Worker.Tests for.' }
if (-not $productionSource.Contains('& dotnet test $workerProject -c Release -p:Platform=' + $workerPlatform + ' --no-build --no-restore')) {
    throw "The Worker live check must run the tests the solution built for platform $workerPlatform."
}
foreach ($requiredText in @('GXMCP_LIVE_GATEWAY_EXE', 'GXMCP_LOG_DIR', 'GXMCP_LIVE_RPC_TIMEOUT_MS', 'GXMCP_LIVE_SUMMARY_PATH', 'live-summary.json', 'Show-LiveSummary', 'Assert-LiveGatewayMaster', '-filter $TestFilter', 'gxw32.exe', 'Legacy GXPublic live runs require -SkipBuild', 'live=$Status', "1 'failed'")) {
    if ($productionSource -notmatch [regex]::Escape($requiredText)) { throw "Live entrypoint lost required harness guard: $requiredText" }
}
$harnessSource = Get-Content (Join-Path $root 'src/GxMcp.Gateway.Tests/LiveGatewayHarness.cs') -Raw
foreach ($requiredText in @('DisposeAsync()', 'Dispose();', 'gxmcp-live-summary/1', 'gatewayPid', 'workerPid', 'initializeMs', 'kbOpenMs', 'coldStartMs', 'rpcMs', 'queueWaitMs', 'phase', 'selectionState', 'errorCount', 'File.Move(temporary')) {
    if ($harnessSource -notmatch [regex]::Escape($requiredText)) { throw "Live harness lost structured lifecycle diagnostics: $requiredText" }
}
$whoamiSource = Get-Content (Join-Path $root 'src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs') -Raw
foreach ($requiredText in @('whoami warmup failed', 'DiagnosticsSummary()', 'whoami baseline must be <500ms')) {
    if ($whoamiSource -notmatch [regex]::Escape($requiredText)) { throw "Whoami live gate lost warm/cold separation marker: $requiredText" }
}
$patchProbeSource = Get-Content (Join-Path $root 'scripts/test_live_patch_persistence_kbteste.ps1') -Raw
foreach ($requiredText in @('ReadLineAsync', 'pendingRead', 'Stop-LiveProbeProcess', 'Remove-LiveProbeObjects', 'Assert-TerminalToolResult', 'Read-Ready', 'Get-PayloadValue', "action='index'", 'trap')) {
    if ($patchProbeSource -notmatch [regex]::Escape($requiredText)) { throw "Issue probe lost required safety guard: $requiredText" }
}
if ($patchProbeSource -notmatch '(?m)^exit 0\s*$') { throw 'Issue probe must set an explicit zero exit code after verified cleanup.' }
$configArguments = @{ GxPath = 'C:\Program Files (x86)\GeneXus\"quoted"'; WorkerExecutable = 'C:\scratch\worker.exe'; HttpPort = 55291 }
$defaultConfig = New-LiveGatewayConfig @configArguments
$roundTrip = $defaultConfig | ConvertTo-Json -Depth 5 | ConvertFrom-Json
if ($roundTrip.GeneXus.InstallationPath -ne $configArguments.GxPath -or
    $roundTrip.Server.HttpPort -ne 55291 -or -not $roundTrip.Server.McpStdio -or
    $roundTrip.Environment.ResolutionPolicy -ne 'strict' -or $defaultConfig.Contains('Engine')) {
    throw 'Default smoke must round-trip paths and exercise the real omitted Engine default.'
}
foreach ($backend in @('sdk', 'auto', 'gxengine')) {
    $config = New-LiveGatewayConfig @configArguments -EngineBackend $backend
    if ($config.Engine.Backend -ne $backend) { throw "Backend selection lost: $backend" }
}
foreach ($key in @('GXMCP_ENGINE_BACKEND', 'GXMCP_ENGINE_FALLBACK', 'GXMCP_ENGINE_EXE', 'GXMCP_ENGINE_TIMEOUT_SECONDS')) {
    if ($productionSource -notmatch [regex]::Escape($key)) { throw "Live run must isolate and restore inherited $key" }
}
if ($productionSource -notmatch 'New-LiveGatewayConfig -GxPath') { throw 'Live entry point must use the tested configuration builder.' }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'Expected rejection' }
}
$backendLog = Join-Path ([System.IO.Path]::GetTempPath()) ('live-engine-test-' + [guid]::NewGuid().ToString('N') + '.log')
try {
    Set-Content -LiteralPath $backendLog -Value '[Engine] genexus_read deferred to the SDK (outside admitted space): versionToken'
    $fallback = Get-LiveBackendEvidence -GatewayLogPath $backendLog -Backend default
    if ($fallback.nativeCalls -ne 0 -or $fallback.sdkDeferrals -ne 1 -or $fallback.nativeObserved) { throw 'SDK fallback is not native evidence.' }
    Expect-Failure { Get-LiveBackendEvidence -GatewayLogPath $backendLog -Backend gxengine -RequireEngineServed }
    Add-Content -LiteralPath $backendLog -Value '[Engine] genexus_query served by gx-engine: test'
    $native = Get-LiveBackendEvidence -GatewayLogPath $backendLog -Backend gxengine -RequireEngineServed
    if ($native.nativeCalls -ne 1 -or -not $native.nativeObserved -or $native.requestedPolicy -ne 'gxengine') { throw 'Native evidence must come from served log entries.' }
    Expect-Failure { Get-LiveBackendEvidence -GatewayLogPath ($backendLog + '.missing') -Backend default }
    if ($productionSource -notmatch 'Get-LiveBackendEvidence -GatewayLogPath' -or $productionSource -notmatch 'engine-evidence.json') { throw 'Live entrypoint must persist its observed routing evidence.' }
} finally {
    Remove-Item -LiteralPath $backendLog -Force
}
$summaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('live-summary-test-' + [guid]::NewGuid().ToString('N'))
$savedSummaryPath = $env:GXMCP_LIVE_SUMMARY_PATH
New-Item -ItemType Directory -Path $summaryRoot | Out-Null
try {
    $env:GXMCP_LIVE_SUMMARY_PATH = Join-Path $summaryRoot 'summary.json'
    $summaryLog = Join-Path $summaryRoot 'gateway.log'
    Set-Content -LiteralPath $summaryLog -Value 'Gateway ready'
    $summary = @{ initialized = $true; phase = 'ready'; kbAlias = 'kbteste'; selectionState = $null }
    foreach ($selection in @($null, 'valid', 'absent', 'invalid', 'conflicting')) {
        $summary.selectionState = $selection
        $summary | ConvertTo-Json | Set-Content -LiteralPath $env:GXMCP_LIVE_SUMMARY_PATH
        $expectedFailure = $null -ne $selection -and $selection -ne 'valid'
        if ((Get-LiveKbOpenFailure -GatewayLogPath $summaryLog) -ne $expectedFailure) {
            throw "KB readiness must distinguish omitted session selection from explicit invalid selection: '$selection'"
        }
    }
    $summary.selectionState = $null
    foreach ($field in @('initialized', 'phase', 'kbAlias')) {
        $original = $summary[$field]
        $summary[$field] = $null
        $summary | ConvertTo-Json | Set-Content -LiteralPath $env:GXMCP_LIVE_SUMMARY_PATH
        if (-not (Get-LiveKbOpenFailure -GatewayLogPath $summaryLog)) { throw "Missing readiness evidence must fail closed: $field" }
        $summary[$field] = $original
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath $env:GXMCP_LIVE_SUMMARY_PATH
    Add-Content -LiteralPath $summaryLog -Value 'KB-OPEN-FAIL'
    if (-not (Get-LiveKbOpenFailure -GatewayLogPath $summaryLog)) { throw 'Explicit KB-open log failure must not be hidden by ready summary.' }
} finally {
    $env:GXMCP_LIVE_SUMMARY_PATH = $savedSummaryPath
    Remove-Item -LiteralPath $summaryRoot -Recurse -Force
}
$fixture = [pscustomobject]@{
    schemaVersion = 1; fixtureId = 'synthetic-small'; fixtureRevision = 'seed-r1'
    generator = 'GeneXus18-net'; kbPath = 'C:\fixtures\small'
    synthetic = $true; disposable = $true
    isolation = [pscustomobject]@{
        verified = $true; kbDatabaseId = 'isolated-kb-01'; applicationDatabaseId = 'isolated-app-01'
        evidence = 'provisioning-record-01'; provisionedBy = 'GeneXus'; verifiedAt = '2026-09-05T12:00:00Z'
    }
}
Assert-LiveFixture $fixture 'C:\fixtures\small'
Expect-Failure { Assert-LiveFixture $fixture 'C:\fixtures\other' }
$fixture.synthetic = $false
Expect-Failure { Assert-LiveFixture $fixture 'C:\fixtures\small' }
$fixture.synthetic = $true
$fixture.isolation.verified = 'true'
Expect-Failure { Assert-LiveFixture $fixture 'C:\fixtures\small' }
$fixture.isolation.verified = $true
$fixture.isolation.evidence = ''
Expect-Failure { Assert-LiveFixture $fixture 'C:\fixtures\small' }
$provenanceRoot = Join-Path $env:TEMP ('gxmcp-fixture-provenance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $provenanceRoot -Force | Out-Null
try {
    $gxw = Join-Path $provenanceRoot 'Synthetic.gxw'
    $connection = Join-Path $provenanceRoot 'knowledgebase.connection'
    Set-Content -LiteralPath $gxw -Value 'synthetic-gxw' -Encoding utf8
    Set-Content -LiteralPath $connection -Value 'synthetic-connection' -Encoding utf8
    $hashFixture = [pscustomobject]@{
        schemaVersion = 1; fixtureId = 'synthetic-provenance'; fixtureRevision = 'seed-provenance'
        generator = 'GeneXus18-net'; kbPath = $provenanceRoot
        synthetic = $true; disposable = $true
        isolation = [pscustomobject]@{
            verified = $true; kbDatabaseId = 'isolated-kb-provenance'; applicationDatabaseId = 'isolated-app-provenance'
            evidence = 'provenance-test'; provisionedBy = 'GeneXus'; verifiedAt = '2026-09-05T12:00:00Z'
        }
        provenance = [pscustomobject]@{
            gxwSha256 = Get-LiveFixtureHash $gxw -NormalizeGxw
            connectionSha256 = Get-LiveFixtureHash $connection
        }
    }
    Assert-LiveFixture $hashFixture $provenanceRoot
    Set-Content -LiteralPath $gxw -Value 'changed-gxw' -Encoding utf8
    Expect-Failure { Assert-LiveFixture $hashFixture $provenanceRoot }
}
finally {
    Remove-Item -LiteralPath $provenanceRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$logRoot = Join-Path $env:TEMP ('gxmcp-live-log-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
try {
    $masterLog = Join-Path $logRoot 'master.log'
    Set-Content -LiteralPath $masterLog -Value '[Gateway] Entering Stdio Loop...' -Encoding utf8
    Assert-LiveGatewayMaster -LogPath $masterLog -RequireStdio
    Set-Content -LiteralPath $masterLog -Value '[Gateway] existing_master_detected currentPid=2 masterPid=1' -Encoding utf8
    Expect-Failure { Assert-LiveGatewayMaster -LogPath $masterLog -RequireStdio }
}
finally {
    Remove-Item -LiteralPath $logRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$started = [datetime]'2026-09-05T12:00:00Z'
$snapshot = @(
    [pscustomobject]@{ ProcessId=11; ParentProcessId=10; CreationDate=$started.AddSeconds(1) }
    [pscustomobject]@{ ProcessId=12; ParentProcessId=11; CreationDate=$started.AddSeconds(2) }
    [pscustomobject]@{ ProcessId=13; ParentProcessId=90; CreationDate=$started.AddSeconds(2) }
    [pscustomobject]@{ ProcessId=14; ParentProcessId=10; CreationDate=$started.AddSeconds(-1) }
)
$owned = @(Get-OwnedDescendants 10 $started $snapshot)
if (($owned.ProcessId -join ',') -ne '12,11') { throw 'Cleanup must select only descendants, children first, excluding reused parent PIDs.' }
$missingManifest = Join-Path $PSScriptRoot ([guid]::NewGuid().ToString('N') + '.missing.json')
$savedErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $output = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot '../test-live.ps1') -KbPath $PSScriptRoot -FixtureManifest $missingManifest 2>&1)
    $childExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $savedErrorAction
}
if ($childExit -eq 0 -or ($output -join "`n") -notmatch 'live=unavailable') {
    throw 'The real entry point must fail closed when an explicitly supplied manifest is missing.'
}
# A port can be unusable without anything listening on it: Windows reserves dynamic ranges (netsh int ipv4 show
# excludedportrange) and a socket can hold it bound. The harness binds the port, so the chooser must bind too.
$firstFree = Get-FreeHttpPort
$holder = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork, [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
try {
    $holder.ExclusiveAddressUse = $true
    $holder.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, $firstFree))
    $next = Get-FreeHttpPort
    if ($next -eq $firstFree) { throw "Get-FreeHttpPort returned port $firstFree, which is bound but not listening; the live harness cannot bind it." }
    if (Test-HttpPortBindable $firstFree) { throw 'Test-HttpPortBindable must report a bound port as unusable.' }
} finally { $holder.Dispose() }

Write-Host 'PASS: fixture rejection, provenance hashes, master/proxy log guard, benchmark alias contract, owned process selection and bindable port choice.'

# test-live.ps1 runs both test projects with --no-build. A checkout where only the publish artifact was built has no
# test assemblies, and dotnet then reports a bare "provided was not found" after the KB was already opened. The
# preflight must name each missing assembly and the command that builds it before any KB is touched.
$fakeRoot = Join-Path ([IO.Path]::GetTempPath()) ('live-assemblies-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fakeRoot | Out-Null
    $missing = @(Get-LiveMissingTestAssemblies -Root $fakeRoot)
    if ($missing.Count -ne 2) { throw "Both test assemblies must be reported missing in an unbuilt tree, got $($missing.Count)." }
    if (($missing -join "`n") -notmatch 'GxMcp\.Worker\.Tests' -or ($missing -join "`n") -notmatch 'dotnet build') {
        throw 'The report must name the Worker test assembly and the build command.'
    }
    if (@(Get-LiveMissingTestAssemblies -Root $fakeRoot -GatewayOnly).Count -ne 1) { throw '-GatewayOnly must not require the Worker test assembly.' }
    $gatewayDir = Join-Path $fakeRoot 'src\GxMcp.Gateway.Tests\bin\Release\net8.0'
    $workerDir = Join-Path $fakeRoot 'src\GxMcp.Worker.Tests\bin\x86\Release\net48'
    New-Item -ItemType Directory -Path $gatewayDir, $workerDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $gatewayDir 'GxMcp.Gateway.Tests.dll') -Value 'x'
    Set-Content -LiteralPath (Join-Path $workerDir 'GxMcp.Worker.Tests.dll') -Value 'x'
    if (@(Get-LiveMissingTestAssemblies -Root $fakeRoot).Count -ne 0) { throw 'Built test assemblies must pass the preflight.' }
} finally { Remove-Item -LiteralPath $fakeRoot -Recurse -Force -ErrorAction SilentlyContinue }
if ((Get-Content (Join-Path $PSScriptRoot '../test-live.ps1') -Raw) -notmatch 'Get-LiveMissingTestAssemblies') {
    throw 'test-live.ps1 must run the test-assembly preflight before opening the KB.'
}
Write-Host 'PASS: missing test assemblies are reported before any KB is opened.'
