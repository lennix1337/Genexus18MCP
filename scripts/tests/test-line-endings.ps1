$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$attributes = @(& git -C $root check-attr text eol whitespace -- CHANGELOG.md README.md scripts/release.ps1)
$attributeText = $attributes -join "`n"
if ($attributeText -notmatch 'CHANGELOG\.md: text: unset' -or $attributeText -notmatch 'CHANGELOG\.md: eol: unspecified' -or $attributeText -notmatch 'CHANGELOG\.md: whitespace: cr-at-eol') { throw 'CHANGELOG.md must retain the repository CRLF exception without Git renormalization.' }
if ($attributeText -notmatch 'README\.md:.*eol: lf') { throw 'README.md must use the repository LF default.' }
if ($attributeText -notmatch 'scripts/release\.ps1:.*eol: lf') { throw 'PowerShell source must use the repository LF default.' }
foreach ($relativePath in @('release.ps1', 'scripts/release-status.ps1', 'scripts/release-preflight.ps1', 'scripts/check-build-warning-baseline.ps1', 'scripts/release-contract.ps1', 'scripts/release-doctor.ps1', 'scripts/verify-release-publication.ps1', 'scripts/release-candidate-ci.ps1')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root ($relativePath -replace '/', '\')))
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "$relativePath must be UTF-8 with BOM for Windows PowerShell compatibility."
    }
}

$editorConfig = Get-Content -LiteralPath (Join-Path $root '.editorconfig') -Raw
if ($editorConfig -notmatch '(?ms)\[CHANGELOG\.md\]\s+end_of_line\s*=\s*crlf') { throw 'EditorConfig does not document the changelog CRLF exception.' }
if ($editorConfig -notmatch '(?ms)^\[\*\]\s+.*?end_of_line\s*=\s*lf') { throw 'EditorConfig LF default is missing.' }

function Assert-FileLineEndings([string]$RelativePath, [ValidateSet('LF', 'CRLF')][string]$Expected) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root ($RelativePath -replace '/', '\')))
    $crlf = 0; $lf = 0; $cr = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 10) {
            if ($i -gt 0 -and $bytes[$i - 1] -eq 13) { $crlf++ } else { $lf++ }
        } elseif ($bytes[$i] -eq 13) {
            if ($i + 1 -ge $bytes.Length -or $bytes[$i + 1] -ne 10) { $cr++ }
        }
    }
    if ($Expected -eq 'CRLF' -and ($lf -gt 0 -or $cr -gt 0)) { throw "$RelativePath contains non-CRLF line endings." }
    if ($Expected -eq 'LF' -and ($crlf -gt 0 -or $cr -gt 0)) { throw "$RelativePath contains non-LF line endings." }
}
Assert-FileLineEndings 'CHANGELOG.md' 'CRLF'
Assert-FileLineEndings 'AGENTS.md' 'LF'
Assert-FileLineEndings 'release.ps1' 'LF'
Assert-FileLineEndings 'src/GxMcp.Gateway.Tests/PropertiesRouterBatchTests.cs' 'LF'
$binaryAttributes = @(& git -C $root check-attr text eol -- src/nexus-ide/resources/extension-icon.png)
if (($binaryAttributes -join "`n") -notmatch 'extension-icon\.png: eol: unspecified') { throw 'Binary assets must not receive a forced text EOL policy.' }
Write-Host 'line-endings: byte-level LF/CRLF policy and binary exception passed' -ForegroundColor Green
