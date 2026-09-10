$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../pr-preflight.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RipwireGate' }, $true)
if (-not $definition) { throw 'Missing Invoke-RipwireGate production function.' }
. ([scriptblock]::Create($definition.Extent.Text))

$temp = Join-Path $env:TEMP ('gxmcp-ripwire-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $missing = Join-Path $temp 'missing.cmd'
    $result = Invoke-RipwireGate -BaseRef 'origin/main' -RipwirePath $missing
    if ($result.status -ne 'skipped' -or $result.exitCode -ne 0) { throw 'Missing optional ripwire must be reported as skipped.' }

    $result = Invoke-RipwireGate -BaseRef 'origin/main' -RipwirePath $missing -Require
    if ($result.status -ne 'failed' -or $result.exitCode -ne 127) { throw 'Missing required ripwire must fail with exit code 127.' }

    $fake = Join-Path $temp 'ripwire.cmd'
    Set-Content -LiteralPath $fake -Value "@echo off`r`nexit /b 0" -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'origin/main' -RipwirePath $fake
    if ($result.status -ne 'passed' -or $result.exitCode -ne 0) { throw 'Successful ripwire must be reported as passed.' }

    Set-Content -LiteralPath $fake -Value "@echo off`r`nexit /b 7" -Encoding ascii
    $result = Invoke-RipwireGate -BaseRef 'origin/main' -RipwirePath $fake
    if ($result.status -ne 'failed' -or $result.exitCode -ne 7) { throw 'Failed ripwire must preserve its exit code.' }

    Write-Host 'pr-preflight ripwire policy: absent, success and failure paths passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
