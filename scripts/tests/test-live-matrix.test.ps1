$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\test-live-matrix.ps1'
$source = Get-Content -LiteralPath $scriptPath -Raw
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Parse-GxPathMap', 'Get-MatrixSelection')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$catalog = [pscustomobject]@{
    primaryMajor = '18'
    supportedMajors = @(
        [pscustomobject]@{ major = '17'; displayName = 'GeneXus 17'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' }
        [pscustomobject]@{ major = '18'; displayName = 'GeneXus 18'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus18' }
    )
}

function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'Expected rejection' }
}

$pathMap = Parse-GxPathMap -Mappings @('17=C:\SDK\GX17;18=C:\SDK\GX18') -Known @('17', '18')
if ($pathMap['17'] -ne 'C:\SDK\GX17' -or $pathMap['18'] -ne 'C:\SDK\GX18') {
    throw 'Path map parser did not preserve explicit absolute SDK paths.'
}
$selection = @(Get-MatrixSelection -Catalog $catalog -RequestedMajors @('17', '18') -PathMap $pathMap)
if ($selection.Count -ne 2 -or $selection[0].major -ne '17' -or $selection[1].path -ne 'C:\SDK\GX18') {
    throw 'Matrix selection did not resolve explicit major/path entries.'
}
$all = @(Get-MatrixSelection -Catalog $catalog -RequestedMajors @() -PathMap $pathMap)
if ($all.Count -ne 2 -or ($all.major -join ',') -ne '17,18') {
    throw 'Matrix selection did not default to every catalog major.'
}
$defaults = @(Get-MatrixSelection -Catalog $catalog -RequestedMajors @('18') -PathMap @{})
if ($defaults.Count -ne 1 -or $defaults[0].path -ne 'C:\Program Files (x86)\GeneXus\GeneXus18') {
    throw 'Matrix selection did not resolve the catalog default path.'
}
$delimited = @(Get-MatrixSelection -Catalog $catalog -RequestedMajors @('17,18') -PathMap $pathMap)
if ($delimited.Count -ne 2 -or $delimited[0].major -ne '17' -or $delimited[1].major -ne '18') {
    throw 'Matrix selection did not split delimited major arguments.'
}
Expect-Failure { Parse-GxPathMap -Mappings @('19=C:\SDK\GX19') -Known @('17', '18') }
Expect-Failure { Parse-GxPathMap -Mappings @('17=C:\SDK\GX17;17=C:\SDK\GX17b') -Known @('17', '18') }
Expect-Failure { Get-MatrixSelection -Catalog $catalog -RequestedMajors @('17', '17') -PathMap @{} }

$runtimeSummary = Join-Path $env:TEMP ('gxmcp-live-matrix-contract-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    & pwsh -NoProfile -File $scriptPath `
        -KbPath 'C:\missing-gxmcp-matrix-kb' `
        -Majors '17' `
        -GxPathMap '17=C:\missing-gxmcp-matrix-sdk' `
        -SkipBuild -SummaryPath $runtimeSummary *> $null
    if ($LASTEXITCODE -ne 2) { throw "Missing SDK must return unavailable exit code 2; got $LASTEXITCODE." }
    $runtime = Get-Content -LiteralPath $runtimeSummary -Raw | ConvertFrom-Json
    if ($runtime.schemaVersion -ne 'gxmcp-live-matrix/1' -or $runtime.status -ne 'unavailable' -or $runtime.allPassed) {
        throw 'Missing SDK did not produce an unavailable fail-closed summary.'
    }
}
finally {
    if (Test-Path -LiteralPath $runtimeSummary) { Remove-Item -LiteralPath $runtimeSummary -Force -ErrorAction SilentlyContinue }
}

foreach ($required in @(
    'build.ps1',
    'test-live.ps1',
    'gxmcp-live-matrix/1',
    'artifactBuild',
    '-SkipBuild',
    '-GxPathMap',
    'live=unavailable'
)) {
    if ($source -notmatch [regex]::Escape($required)) { throw "Matrix runner is missing contract marker: $required" }
}
Write-Host 'live-matrix-script: catalog selection, path-map validation and fail-closed contract passed' -ForegroundColor Green
