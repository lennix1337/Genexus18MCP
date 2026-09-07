[CmdletBinding()]
param(
    [string]$KbPath,

    [string]$FixtureManifest,

    [string]$GxPath,

    [switch]$GatewayOnly,

    [switch]$RunBenchmark,

    [string]$BenchmarkBaseline,

    [string]$BenchmarkOut,

    [ValidateRange(1024, 65535)]
    [int]$HttpPort,

    [switch]$SkipBuild,

    [switch]$RequireBuildAll,

    [ValidateRange(30, 7200)]
    [int]$BuildAllTimeoutSeconds = 2400,

    [ValidateRange(1, 100)]
    [int]$Iterations = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Fail-Live([string]$Message, [int]$ExitCode = 1) {
    Write-Error "live=unavailable; Live gate failed: $Message"
    exit $ExitCode
}

function Get-FreeHttpPort {
    foreach ($candidate in 55100..55199) {
        if (-not (Get-NetTCPConnection -LocalPort $candidate -State Listen -ErrorAction SilentlyContinue)) {
            return $candidate
        }
    }
    Fail-Live 'No free isolated HTTP port was found in 55100..55199.'
}

function Get-LiveFixtureHash([string]$Path, [switch]$NormalizeGxw) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($NormalizeGxw) {
        $text = ([Text.Encoding]::UTF8.GetString($bytes)).TrimStart([char]0xFEFF)
        try {
            $xml = [xml]$text
            foreach ($elementName in @('FriendlyVersion', 'VersionNumber')) {
                $nodes = $xml.SelectNodes("//*[local-name()='$elementName']")
                foreach ($node in $nodes) { $node.InnerText = '' }
            }
            $text = $xml.OuterXml
        }
        catch { }
        $text = ($text -replace "`r`n", "`n" -replace "`r", "`n").Trim()
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-LiveFixture($Fixture, [string]$ResolvedKbPath) {
    if ($Fixture.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($Fixture.fixtureId) -or
        [string]::IsNullOrWhiteSpace($Fixture.fixtureRevision) -or
        [string]::IsNullOrWhiteSpace($Fixture.generator) -or
        $Fixture.synthetic -isnot [bool] -or -not $Fixture.synthetic -or
        $Fixture.disposable -isnot [bool] -or -not $Fixture.disposable) {
        throw 'Fixture must identify a synthetic, disposable KB with fixtureRevision and generator using schemaVersion 1.'
    }
    if ([string]::IsNullOrWhiteSpace($Fixture.kbPath) -or
        -not [IO.Path]::IsPathRooted($Fixture.kbPath) -or
        [IO.Path]::GetFullPath($Fixture.kbPath).TrimEnd('\') -ine $ResolvedKbPath.TrimEnd('\')) {
        throw 'Fixture kbPath must match the explicitly selected KB.'
    }
    $isolation = $Fixture.isolation
    if ($isolation.verified -isnot [bool] -or -not $isolation.verified) {
        throw 'Fixture requires verified database isolation, not a copied KB directory.'
    }
    foreach ($field in @('kbDatabaseId', 'applicationDatabaseId', 'evidence', 'provisionedBy', 'verifiedAt')) {
        if ([string]::IsNullOrWhiteSpace($isolation.$field)) { throw "Fixture isolation missing $field." }
    }
    if ($isolation.provisionedBy -notin @('GeneXus', 'XPZ')) { throw 'Fixture must be provisioned through GeneXus or verified XPZ import.' }
    $verifiedAt = [datetimeoffset]::MinValue
    if (-not [datetimeoffset]::TryParse($isolation.verifiedAt, [ref]$verifiedAt) -or $verifiedAt -gt [datetimeoffset]::UtcNow) {
        throw 'Fixture verification timestamp is invalid.'
    }

    # A fixture revision is only comparable while the on-disk provenance files
    # are unchanged. GeneXus may rewrite the .gxw metadata during a normal open
    # (for example, when it refreshes the friendly SDK version), so a manifest
    # that carries hashes must fail closed instead of silently mixing runs from
    # different KB revisions.
    $provenance = $Fixture.provenance
    if ($null -ne $provenance) {
        foreach ($field in @('gxwSha256', 'connectionSha256')) {
            if ([string]::IsNullOrWhiteSpace($provenance.$field)) {
                throw "Fixture provenance missing $field."
            }
        }
        $gxwFiles = @(Get-ChildItem -LiteralPath $ResolvedKbPath -Filter '*.gxw' -File -ErrorAction Stop)
        if ($gxwFiles.Count -ne 1) {
            throw "Fixture must contain exactly one .gxw workspace file for provenance validation; found $($gxwFiles.Count)."
        }
        $connectionFile = Join-Path $ResolvedKbPath 'knowledgebase.connection'
        if (-not (Test-Path -LiteralPath $connectionFile -PathType Leaf)) {
            throw 'Fixture knowledgebase.connection is required for provenance validation.'
        }
        $actualGxw = Get-LiveFixtureHash $gxwFiles[0].FullName -NormalizeGxw
        $actualConnection = Get-LiveFixtureHash $connectionFile
        if ($actualGxw -ine [string]$provenance.gxwSha256) {
            throw "Fixture .gxw hash does not match manifest provenance (expected $($provenance.gxwSha256), actual $actualGxw)."
        }
        if ($actualConnection -ine [string]$provenance.connectionSha256) {
            throw "Fixture connection hash does not match manifest provenance (expected $($provenance.connectionSha256), actual $actualConnection)."
        }
    }
}

function Get-OwnedDescendants([int]$ParentId, [datetime]$ParentStarted, [object[]]$Snapshot) {
    foreach ($child in $Snapshot) {
        if ($child.ParentProcessId -eq $ParentId -and $child.ProcessId -ne $ParentId -and $child.CreationDate -ge $ParentStarted) {
            Get-OwnedDescendants ([int]$child.ProcessId) ([datetime]$child.CreationDate) $Snapshot
            $child
        }
    }
}

function Stop-BenchmarkGateway([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    try {
        # Snapshot before stopping the parent; directory/name matching is not ownership.
        $descendants = @(Get-OwnedDescendants $Process.Id $Process.StartTime @(Get-CimInstance Win32_Process))
        foreach ($child in $descendants) {
            $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($child.ProcessId)"
            if ($null -ne $current -and $current.CreationDate -eq $child.CreationDate) {
                Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
        if (-not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit(5000)
        }
    }
    catch { Write-Warning "Owned benchmark process cleanup failed: $($_.Exception.Message)" }
    finally {
        $Process.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($KbPath)) {
    $KbPath = if (-not [string]::IsNullOrWhiteSpace($env:GXMCP_TEST_KB)) {
        $env:GXMCP_TEST_KB
    } else {
        Fail-Live 'Explicit -KbPath or GXMCP_TEST_KB is required; no implicit KB is safe.'
    }
}
if (-not (Test-Path -LiteralPath $KbPath -PathType Container)) {
    Fail-Live "KB directory not found: $KbPath"
}
$KbPath = (Resolve-Path -LiteralPath $KbPath).Path
if ([string]::IsNullOrWhiteSpace($FixtureManifest)) { $FixtureManifest = $env:GXMCP_TEST_FIXTURE }
if ([string]::IsNullOrWhiteSpace($FixtureManifest) -or -not (Test-Path -LiteralPath $FixtureManifest -PathType Leaf)) {
    Fail-Live 'Provide -FixtureManifest or GXMCP_TEST_FIXTURE identifying a verified isolated synthetic KB. See docs/live-kb-test-harness.md.'
}
try {
    $fixture = Get-Content -LiteralPath $FixtureManifest -Raw | ConvertFrom-Json
    Assert-LiveFixture $fixture $KbPath
} catch { Fail-Live $_.Exception.Message }

if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        $env:GX_PATH
    } else {
        'C:\Program Files (x86)\GeneXus\GeneXus18'
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $GxPath 'Artech.Architecture.Common.dll') -PathType Leaf)) {
    Fail-Live "GeneXus 18 SDK not found under '$GxPath'. Set -GxPath or GX_PATH."
}

$savedEnvironment = @{}
foreach ($key in @('GXMCP_TEST_KB', 'GX_PATH', 'GX_MCP_PORT', 'GX_MCP_STDIO', 'GX_CONFIG_PATH')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
$env:GXMCP_TEST_KB = $KbPath
$env:GX_PATH = $GxPath
$HttpPort = if ($HttpPort -gt 0) { $HttpPort } else { Get-FreeHttpPort }
if (Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue) {
    Fail-Live "Selected port $HttpPort is already in use."
}
$env:GX_MCP_PORT = $HttpPort.ToString()
$env:GX_MCP_STDIO = 'true'

Write-Host "Live KB: $KbPath" -ForegroundColor Cyan
Write-Host "GeneXus SDK: $GxPath" -ForegroundColor Cyan
Write-Host "Isolated HTTP port: $HttpPort" -ForegroundColor Cyan

if (-not $SkipBuild) {
    Write-Host "`n>>> Building current Gateway/Worker artifact" -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        Fail-Live "Current artifact build failed with exit code $LASTEXITCODE."
    }
}

$gatewayExe = Join-Path $root 'publish\GxMcp.Gateway.exe'
$sourceSchema = Join-Path $root 'src\GxMcp.Gateway\tool_definitions.json'
$publishedSchema = Join-Path $root 'publish\tool_definitions.json'
if (-not (Test-Path -LiteralPath $gatewayExe -PathType Leaf)) {
    Fail-Live "Published Gateway not found at '$gatewayExe'."
}
if (-not (Test-Path -LiteralPath $publishedSchema -PathType Leaf)) {
    Fail-Live "Published tool schema not found at '$publishedSchema'."
}
if ((Get-FileHash -LiteralPath $sourceSchema -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $publishedSchema -Algorithm SHA256).Hash) {
    Fail-Live "Published tool schema is stale. Run without -SkipBuild to rebuild the current artifact."
}

$versionMatch = Select-String -LiteralPath (Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj') -Pattern '<Version>([^<]+)</Version>'
$sourceVersion = if ($versionMatch) { $versionMatch.Matches[0].Groups[1].Value } else { $null }
$publishedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($gatewayExe.Replace('.exe', '.dll')).Version.ToString(3)
if (-not [string]::IsNullOrWhiteSpace($sourceVersion) -and $sourceVersion -ne $publishedVersion) {
    Fail-Live "Published Gateway version $publishedVersion does not match source version $sourceVersion. Run without -SkipBuild."
}

$gatewayProject = Join-Path $root 'src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj'
# Never inherit a user's default KB or let tests persist selection to their config.
$runDirectory = Join-Path $root ('scratchpad\live-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$env:GX_CONFIG_PATH = Join-Path $runDirectory 'config.json'
$liveFixtureAlias = 'live-fixture'
@{
    GeneXus = @{ InstallationPath = $GxPath; WorkerExecutable = (Join-Path $root 'publish\worker\GxMcp.Worker.exe') }
    Server = @{ HttpPort = $HttpPort; McpStdio = $true; BindAddress = '127.0.0.1' }
    Environment = @{ DefaultKb = $liveFixtureAlias; KBs = @(@{ Alias = $liveFixtureAlias; Path = $KbPath }) }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:GX_CONFIG_PATH -Encoding utf8
Write-Host "`n>>> Gateway live smoke" -ForegroundColor Cyan
& dotnet test $gatewayProject --no-restore --nologo -v:minimal --filter 'Category=LiveE2E'
if ($LASTEXITCODE -ne 0) {
    Fail-Live "Gateway live smoke failed with exit code $LASTEXITCODE."
}

if ($RequireBuildAll) {
    $buildAllScript = Join-Path $root 'scripts\live-build-all.ps1'
    Write-Host "`n>>> Native Build All evidence gate" -ForegroundColor Cyan
    & pwsh -NoProfile -File $buildAllScript `
        -KbPath $KbPath `
        -FixtureManifest $FixtureManifest `
        -GatewayExe $gatewayExe `
        -GxPath $GxPath `
        -TimeoutSeconds $BuildAllTimeoutSeconds
    $buildAllExit = $LASTEXITCODE
    if ($buildAllExit -eq 2) {
        Fail-Live 'Native Build All is unavailable in this fixture/environment (see the structured result above).' 2
    }
    if ($buildAllExit -ne 0) {
        Fail-Live "Native Build All evidence gate failed with exit code $buildAllExit."
    }
    Write-Host 'Native Build All evidence gate passed.' -ForegroundColor Green
}

if (-not $GatewayOnly) {
    $workerProject = Join-Path $root 'src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj'
    Write-Host "`n>>> Worker SDK live check" -ForegroundColor Cyan
    & dotnet test $workerProject --no-restore --nologo -v:minimal --filter 'FullyQualifiedName~InProcessBuildRunnerTests.TryResolveTypes_finds_GeneXus_tasks_when_SDK_installed'
    if ($LASTEXITCODE -ne 0) {
        Fail-Live "Worker SDK live check failed with exit code $LASTEXITCODE."
    }
}

if ($RunBenchmark) {
    $benchmark = Join-Path $root 'scripts\bench-live-http.py'
    if (-not (Test-Path -LiteralPath $benchmark -PathType Leaf)) {
        Fail-Live "Benchmark harness not found at '$benchmark'."
    }
    if ([string]::IsNullOrWhiteSpace($BenchmarkOut)) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $BenchmarkOut = Join-Path $env:TEMP "gxmcp-live-benchmark-$stamp.json"
    }

    $benchmarkArgs = @(
        $benchmark,
        '--kb', $KbPath,
        '--alias', $liveFixtureAlias,
        '--fixture-id', [string]$fixture.fixtureId,
        '--fixture-revision', [string]$fixture.fixtureRevision,
        '--generator', [string]$fixture.generator,
        '--iterations', $Iterations.ToString(),
        '--port', $HttpPort.ToString(),
        '--ops', 'whoami,list_objects,query,search_source,inspect,read,lifecycle_status',
        '--out', $BenchmarkOut,
        '--name', 'quality-gate'
    )
    if (-not [string]::IsNullOrWhiteSpace($BenchmarkBaseline)) {
        if (-not (Test-Path -LiteralPath $BenchmarkBaseline -PathType Leaf)) {
            Fail-Live "Benchmark baseline not found: $BenchmarkBaseline"
        }
        $benchmarkArgs += @('--compare', $BenchmarkBaseline, '--fail-on-regression')
    }

    Write-Host "`n>>> Live performance benchmark" -ForegroundColor Cyan
    $benchmarkGateway = $null
    try {
        $gatewayStart = [System.Diagnostics.ProcessStartInfo]::new()
        $gatewayStart.FileName = $gatewayExe
        $gatewayStart.WorkingDirectory = Split-Path -Parent $gatewayExe
        $gatewayStart.UseShellExecute = $false
        $gatewayStart.CreateNoWindow = $true
        $gatewayStart.EnvironmentVariables['GX_MCP_PORT'] = $HttpPort.ToString()
        $gatewayStart.EnvironmentVariables['GX_MCP_STDIO'] = 'false'
        $gatewayStart.EnvironmentVariables['GXMCP_TEST_KB'] = $KbPath
        $gatewayStart.EnvironmentVariables['GX_PATH'] = $GxPath
        $benchmarkGateway = [System.Diagnostics.Process]::Start($gatewayStart)

        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($benchmarkGateway.HasExited) {
                Fail-Live "Benchmark Gateway exited before binding isolated port $HttpPort (exit code $($benchmarkGateway.ExitCode))."
            }
            $listener = Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue
            if ($listener -and @($listener | Where-Object OwningProcess -ne $benchmarkGateway.Id).Count) {
                Fail-Live 'Benchmark port was acquired by another process.'
            }
            if ($listener) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            Fail-Live "Benchmark Gateway did not bind isolated port $HttpPort within 30 seconds."
        }

        & python @benchmarkArgs
        if ($LASTEXITCODE -ne 0) {
            Fail-Live "Live performance benchmark failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Stop-BenchmarkGateway $benchmarkGateway
    }
    Write-Host "Benchmark output: $BenchmarkOut" -ForegroundColor Green
}

Write-Host "`nLive gate completed." -ForegroundColor Green
} finally {
    foreach ($key in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key])
    }
}
