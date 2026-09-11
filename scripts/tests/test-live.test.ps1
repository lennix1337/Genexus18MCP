$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\live-fixture.ps1')
. (Join-Path $root 'scripts\live-harness.ps1')
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot '../test-live.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-OwnedDescendants')) {
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
if ($productionSource -notmatch [regex]::Escape("'--alias', `$liveFixtureAlias")) {
    throw 'The live benchmark must receive the same alias used by its isolated config.'
}
foreach ($requiredText in @('GXMCP_LIVE_GATEWAY_EXE', 'GXMCP_LOG_DIR', 'GXMCP_LIVE_RPC_TIMEOUT_MS', 'Assert-LiveGatewayMaster', '-filter $TestFilter')) {
    if ($productionSource -notmatch [regex]::Escape($requiredText)) { throw "Live entrypoint lost required harness guard: $requiredText" }
}
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'Expected rejection' }
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
Write-Host 'PASS: fixture rejection, provenance hashes, master/proxy log guard, benchmark alias contract and owned process selection.'
