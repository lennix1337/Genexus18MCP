$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$drySummary = Join-Path $env:TEMP ('gxmcp-preflight-test-' + [guid]::NewGuid().ToString('N') + '.json')
$matrixDrySummary = Join-Path $env:TEMP ('gxmcp-preflight-matrix-test-' + [guid]::NewGuid().ToString('N') + '.json')
$requiredSummary = Join-Path $env:TEMP ('gxmcp-preflight-required-' + [guid]::NewGuid().ToString('N') + '.json')
$localSummary = Join-Path $env:TEMP ('gxmcp-preflight-local-' + [guid]::NewGuid().ToString('N') + '.json')
$fixtureRoot = Join-Path $env:TEMP ('gxmcp-preflight-fixtures-' + [guid]::NewGuid().ToString('N'))
$SummaryPath = $null
$requiredNames = @(
    'release metadata parity', 'tool contract validation', 'operation contract inventory',
    'v3 plan readiness', 'Python script tests', 'PowerShell script tests',
    'CLI tests', 'CLI lint', 'Nexus IDE checks', 'solution build and tests',
    'Release warning baseline',
    'live KB gate'
)
try {
    $preflightPath = Join-Path $root 'scripts/release-preflight.ps1'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($preflightPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $fixtureFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-LocalLiveKbPath' }, $true)
    if (-not $fixtureFunction) { throw 'Missing Get-LocalLiveKbPath production function.' }
    . ([scriptblock]::Create($fixtureFunction.Extent.Text))
    $catalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @(
            [pscustomobject]@{ major = '17'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' }
            [pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus18' }
        )
    }
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'KBTeste'), (Join-Path $fixtureRoot 'KBTeste17') -Force | Out-Null
    $auto18 = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus18' -KbRoot $fixtureRoot)
    $auto17 = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' -KbRoot $fixtureRoot)
    $auto17Custom = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'D:\SDK\GeneXus17Trial' -KbRoot $fixtureRoot)
    $none = @(Get-LocalLiveKbPath -Catalog $catalog -GxPath 'C:\SDK\Unknown' -KbRoot (Join-Path $env:TEMP 'gxmcp-no-fixtures'))
    if ($auto18.Count -ne 1 -or $auto18[0].major -ne '18' -or $auto18[0].path -ne (Join-Path $fixtureRoot 'KBTeste')) { throw 'GeneXus 18 fixture autodetection selected the wrong KB.' }
    if ($auto17.Count -ne 1 -or $auto17[0].major -ne '17' -or $auto17[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($auto17Custom.Count -ne 1 -or $auto17Custom[0].major -ne '17' -or $auto17Custom[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'Custom GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($none.Count -ne 1 -or $null -ne $none[0]) { throw 'Fixture autodetection returned a KB that does not exist.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -SkipLive -SummaryPath $drySummary
    if ($LASTEXITCODE -ne 0) { throw "Dry-run preflight failed with exit code $LASTEXITCODE." }
    $summary = Get-Content -LiteralPath $drySummary -Raw | ConvertFrom-Json
    if ($summary.schemaVersion -ne 'gxmcp-release-preflight/1') { throw 'Unexpected preflight summary schema.' }
    $actualNames = @($summary.phases | ForEach-Object name)
    if (($actualNames -join '|') -ne ($requiredNames -join '|')) { throw "Preflight phase order changed: $($actualNames -join ', ')" }
    $buildIndex = [array]::IndexOf($actualNames, 'solution build and tests')
    foreach ($staticName in @('tool contract validation', 'operation contract inventory', 'v3 plan readiness', 'Python script tests', 'PowerShell script tests')) {
        if ([array]::IndexOf($actualNames, $staticName) -ge $buildIndex) {
            throw "Cheap static phase '$staticName' must run before the solution build and tests."
        }
    }
    if (@($summary.phases | Where-Object status -eq 'dry-run').Count -ne 11) { throw 'All non-live phases must be marked dry-run.' }
    if (@($summary.phases | Where-Object status -eq 'skipped').Count -ne 1) { throw 'Live skip must be explicit in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -SkipLive -LiveMajors '17,18' -LiveGxPathMap '17=C:\SDK\GX17' -SummaryPath $matrixDrySummary *> $null
    if ($LASTEXITCODE -ne 0) { throw "Matrix dry-run preflight failed with exit code $LASTEXITCODE." }
    $matrixSummary = Get-Content -LiteralPath $matrixDrySummary -Raw | ConvertFrom-Json
    if ($matrixSummary.liveMode -ne 'matrix') { throw 'Matrix options were not reflected in the release preflight summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'missing') -SummaryPath $requiredSummary *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Required live Build All gate must fail closed when no fixture is configured.' }
    $required = Get-Content -LiteralPath $requiredSummary -Raw | ConvertFrom-Json
    $requiredLive = @($required.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($required.status -ne 'failed' -or $requiredLive.status -ne 'skipped') { throw 'Required live gate failure was not recorded in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'KBTeste') -SummaryPath $localSummary *> $null
    if ($LASTEXITCODE -ne 0) { throw 'A local KB must be sufficient for the dry-run live gate without a fixture manifest.' }
    $local = Get-Content -LiteralPath $localSummary -Raw | ConvertFrom-Json
    $localLive = @($local.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($localLive.status -ne 'dry-run' -or $local.liveKbPath -ne (Join-Path $fixtureRoot 'KBTeste')) { throw 'Local KB live source was not recorded in the preflight summary.' }

    $releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
    if ($releaseSource -match "'-SkipLive'") { throw 'Canonical release entrypoint must allow configured live preflight values to participate.' }

    $preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts\release-preflight.ps1') -Raw
    foreach ($marker in @('LiveMajors', 'LiveGxPathMap', 'test-live-matrix.ps1', "liveMode =")) {
        if ($preflightSource -notmatch [regex]::Escape($marker)) { throw "Release preflight is missing multi-version live marker: $marker" }
    }

    # Load the production runner and exercise a nonzero command without
    # starting the real release matrix. AllowFailure keeps the phase object so
    # the test can inspect exit-code propagation.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts\release-preflight.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    foreach ($name in @('Format-PreflightCommand', 'Write-PreflightSummary', 'Invoke-PreflightPhase')) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing $name production function." }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $summary = [ordered]@{ phases = New-Object System.Collections.Generic.List[object]; endedAtUtc = $null }
    $SummaryPath = Join-Path $env:TEMP ('gxmcp-preflight-phase-' + [guid]::NewGuid().ToString('N') + '.json')
    $DryRun = $false
    $phase = Invoke-PreflightPhase -Name 'mock failure' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '7') -AllowFailure
    if ($phase.exitCode -ne 7 -or $phase.status -ne 'failed') { throw 'Nonzero phase exit code was not preserved.' }
    if (-not (Test-Path -LiteralPath $SummaryPath)) { throw 'Phase summary was not written atomically.' }

    Write-Host 'release-preflight: order, summary shape, skip and exit propagation passed' -ForegroundColor Green
}
finally {
    foreach ($path in @($drySummary, $matrixDrySummary, $requiredSummary, $localSummary, $SummaryPath, $fixtureRoot)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        if ($path -eq $fixtureRoot) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}
