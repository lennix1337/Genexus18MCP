$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$drySummary = Join-Path $env:TEMP ('gxmcp-preflight-test-' + [guid]::NewGuid().ToString('N') + '.json')
$requiredSummary = Join-Path $env:TEMP ('gxmcp-preflight-required-' + [guid]::NewGuid().ToString('N') + '.json')
$SummaryPath = $null
$requiredNames = @(
    'release metadata parity', 'solution build and tests', 'CLI tests', 'CLI lint', 'Nexus IDE checks',
    'tool contract validation', 'operation contract inventory',
    'v3 plan readiness', 'Python script tests', 'PowerShell script tests', 'Release warning baseline',
    'live KB gate'
)
try {
    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -SkipLive -SummaryPath $drySummary
    if ($LASTEXITCODE -ne 0) { throw "Dry-run preflight failed with exit code $LASTEXITCODE." }
    $summary = Get-Content -LiteralPath $drySummary -Raw | ConvertFrom-Json
    if ($summary.schemaVersion -ne 'gxmcp-release-preflight/1') { throw 'Unexpected preflight summary schema.' }
    $actualNames = @($summary.phases | ForEach-Object name)
    if (($actualNames -join '|') -ne ($requiredNames -join '|')) { throw "Preflight phase order changed: $($actualNames -join ', ')" }
    if (@($summary.phases | Where-Object status -eq 'dry-run').Count -ne 11) { throw 'All non-live phases must be marked dry-run.' }
    if (@($summary.phases | Where-Object status -eq 'skipped').Count -ne 1) { throw 'Live skip must be explicit in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -RequireBuildAll -SummaryPath $requiredSummary *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Required live Build All gate must fail closed when no fixture is configured.' }
    $required = Get-Content -LiteralPath $requiredSummary -Raw | ConvertFrom-Json
    $requiredLive = @($required.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($required.status -ne 'failed' -or $requiredLive.status -ne 'skipped') { throw 'Required live gate failure was not recorded in the summary.' }

    $releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
    if ($releaseSource -match "'-SkipLive'") { throw 'Canonical release entrypoint must allow configured live preflight values to participate.' }

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
    foreach ($path in @($drySummary, $requiredSummary, $SummaryPath)) {
        if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }
}
