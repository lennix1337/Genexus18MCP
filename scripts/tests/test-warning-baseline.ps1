$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../check-build-warning-baseline.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Warning-Key', 'Warning-Pair', 'Compare-WarningLocations')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$baseline = @(
    [pscustomobject]@{ code = 'CS8600'; file = 'src/A.cs'; line = 10 }
    [pscustomobject]@{ code = 'CS8602'; file = 'src/A.cs'; line = 20 }
)
$current = @(
    [pscustomobject]@{ code = 'CS8600'; file = 'src/A.cs'; line = 11 }
    [pscustomobject]@{ code = 'CS8602'; file = 'src/A.cs'; line = 20 }
    [pscustomobject]@{ code = 'CS8604'; file = 'src/A.cs'; line = 30 }
)
$comparison = Compare-WarningLocations -Baseline $baseline -Current $current
if (@($comparison.Moved).Count -ne 1) { throw 'A line-only shift must be classified as moved.' }
if (@($comparison.New).Count -ne 1 -or $comparison.New[0].code -ne 'CS8604') { throw 'A new warning code must remain a blocking new diagnostic.' }
if (@($comparison.Removed).Count -ne 0) { throw 'Moved diagnostics must not be reported as removed.' }

$manifest = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.json') -Raw | ConvertFrom-Json
$doc = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.md') -Raw
if ($doc -notmatch "(?m)\|\s*\*\*$($manifest.warningCount)\*\*\s*\|\s*$") {
    throw "Warning baseline Markdown total does not match JSON warningCount=$($manifest.warningCount)."
}
Write-Host 'warning-baseline: move-aware comparison and documentation parity passed' -ForegroundColor Green
