$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wrapper = Get-Content -LiteralPath (Join-Path $root 'scripts\release.ps1') -Raw
if ($wrapper -match '(?im)^\s*(?:git\s|npm\s|dotnet\s|gh\s|Compress-Archive)') {
    throw 'Legacy release entrypoint contains an independent command implementation.'
}
if ($wrapper -notmatch 'release\.ps1') { throw 'Legacy entrypoint does not delegate to the canonical script.' }
if ($wrapper -notmatch 'Push-Location \$root' -or $wrapper -notmatch 'Pop-Location') {
    throw 'Legacy release entrypoint must delegate from the repository root.'
}

$canonicalSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
if ($canonicalSource -notmatch '\$numericVersion' -or $canonicalSource -notmatch 'AssemblyVersion>.*numericVersion\.0') {
    throw 'Canonical release entrypoint must keep prerelease assembly versions numeric.'
}

$output = & pwsh -NoProfile -File (Join-Path $root 'scripts\release.ps1') -NoBump 2>&1
if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'NoBump.*no longer supported') {
    throw '-NoBump did not fail with migration guidance.'
}

$metadata = & python (Join-Path $root 'scripts\verify-release-metadata.py') --root $root --version 3.0.0 2>&1
if ($LASTEXITCODE -ne 0) { throw "Current release metadata is not synchronized: $($metadata -join "`n")" }

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$lockDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-LockfileVersion' }, $true)
if (-not $lockDefinition) { throw 'Canonical release script is missing lockfile synchronization.' }
. ([scriptblock]::Create($lockDefinition.Extent.Text))
$DryRun = $false
function Ok([string]$Message) { }
$fixtureLock = Join-Path $env:TEMP ('gxmcp-lock-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [ordered]@{ name = 'fixture'; version = '2.0.0'; packages = [ordered]@{ '' = [ordered]@{ name = 'fixture'; version = '2.0.0' }; dep = [ordered]@{ version = '1.0.0' } } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fixtureLock -Encoding utf8
    Set-LockfileVersion -Path $fixtureLock -TargetVersion '3.0.1' | Out-Null
    $lockDocument = Get-Content -LiteralPath $fixtureLock -Raw | ConvertFrom-Json -AsHashtable
    if ($lockDocument['version'] -ne '3.0.1' -or $lockDocument['packages']['']['version'] -ne '3.0.1' -or $lockDocument['packages']['dep']['version'] -ne '1.0.0') {
        throw 'Lockfile synchronization changed the wrong version fields.'
    }
}
finally { if (Test-Path -LiteralPath $fixtureLock) { Remove-Item -LiteralPath $fixtureLock -Force -ErrorAction SilentlyContinue } }
Write-Host 'release-entrypoint: wrapper and metadata checks passed' -ForegroundColor Green
