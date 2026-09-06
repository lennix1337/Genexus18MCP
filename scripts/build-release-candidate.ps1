[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'scratchpad\release-candidate'),
    [string]$GxPath = $env:GX_PATH
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GxPath)) { $GxPath = 'C:\Program Files (x86)\GeneXus\GeneXus18' }
$env:GX_PATH = $GxPath

& pwsh -NoProfile -File (Join-Path $root 'build.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Base build failed with exit code $LASTEXITCODE." }

$publish = Join-Path $root 'publish'
$stage = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force

$vsix = Join-Path $stage 'nexus-ide.vsix'
$extensionPackage = Join-Path $root 'src\nexus-ide\package.json'
$extensionOriginal = [IO.File]::ReadAllText($extensionPackage)
try {
    $extensionVersioned = [regex]::Replace(
        $extensionOriginal,
        '("version"\s*:\s*")[^"]+(")',
        "`${1}$Version`${2}",
        1)
    [IO.File]::WriteAllText($extensionPackage, $extensionVersioned, [Text.UTF8Encoding]::new($false))
    & npm --prefix (Join-Path $root 'src\nexus-ide') run package -- --out $vsix
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $vsix -PathType Leaf)) {
        throw 'Nexus VSIX packaging failed or did not produce an artifact.'
    }
} finally {
    [IO.File]::WriteAllText($extensionPackage, $extensionOriginal, [Text.UTF8Encoding]::new($false))
}

$manifestScript = Join-Path $root 'scripts\write-release-manifest.ps1'
& pwsh -NoProfile -File $manifestScript -PublishDirectory $stage -Version $Version -SourceRoot $root | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Release-candidate manifest generation failed.' }

$zip = Join-Path (Split-Path -Parent $stage) "gxmcp-release-candidate-$Version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$zipHash  $(Split-Path -Leaf $zip)`n", [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    directory = $stage
    archive = $zip
    sha256 = $zipHash
    manifest = Join-Path $stage 'gxmcp-manifest.json'
    vsix = $vsix
} | ConvertTo-Json -Depth 5
