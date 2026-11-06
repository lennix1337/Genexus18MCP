$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\gx-version-catalog.ps1')

$catalog = Get-GxVersionCatalog -Root $root
if ($catalog.primaryMajor -ne '18') { throw 'The primary GeneXus major must remain 18.' }
$majors = @($catalog.supportedMajors | ForEach-Object { [string]$_.major })
if (($majors -join ',') -ne '16,17,18,10.3') { throw "Unexpected supported majors: $($majors -join ',')." }
if ((Get-GxPrimaryInstallPath -Catalog $catalog) -ne 'C:\Program Files (x86)\GeneXus\GeneXus18') {
    throw 'The primary install path does not come from the catalog.'
}
if ((Get-GxSupportedMajorsDisplay -Catalog $catalog) -ne '16, 17, 18, 10.3') {
    throw 'The supported-major display does not come from the catalog.'
}
$minimalFutureCatalog = [pscustomobject]@{
    primaryMajor = '19'
    supportedMajors = @([pscustomobject]@{ major = '19'; defaultInstallPath = 'C:\GX19' })
}
if ((Get-GxPrimaryInstallPath -Catalog $minimalFutureCatalog) -ne 'C:\GX19') {
    throw 'A future catalog entry without optional registry metadata must remain usable.'
}

$temp = Join-Path $env:TEMP ('gxmcp-version-catalog-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    New-Item -ItemType Directory -Path (Join-Path $temp 'config') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $temp 'config\gx-versions.json') -Value '{"primaryMajor":"18"}' -Encoding utf8
    try {
        Get-GxVersionCatalog -Root $temp | Out-Null
        throw 'Malformed catalog was accepted.'
    } catch {
        if ($_.Exception.Message -notmatch 'no supportedMajors') { throw }
    }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host 'version-catalog: source-of-truth and validation checks passed' -ForegroundColor Green
