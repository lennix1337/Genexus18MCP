$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\publish-config.ps1')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "Assertion failed: $Message (expected '$Expected', got '$Actual')" }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-publish-config-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    # --- the fallback config -------------------------------------------------
    $neutral = Get-PublishFallbackConfigJson -ArtifactGxPath 'C:\GX18' | ConvertFrom-Json
    Assert-Equal 2 $neutral.ConfigSchemaVersion 'schema version'
    Assert-Equal 'stdio-isolated' $neutral.GatewayMode 'gateway mode'
    Assert-Equal 'C:\GX18' $neutral.GeneXus.InstallationPath 'artifact GeneXus path'
    Assert-Equal 'strict' $neutral.Environment.ResolutionPolicy 'resolution policy'
    Assert-True ($neutral.PSObject.Properties.Name -notcontains 'Engine') 'a release artifact must not carry an Engine section'

    # build.ps1 forwards its unbound -EngineBackend, which is an empty string.
    $forwardedEmpty = Get-PublishFallbackConfigJson -ArtifactGxPath 'C:\GX18' -EngineBackend '' | ConvertFrom-Json
    Assert-True ($forwardedEmpty.PSObject.Properties.Name -notcontains 'Engine') 'an empty -EngineBackend must not write an Engine section'

    foreach ($backend in 'sdk', 'auto', 'gxengine') {
        $withEngine = Get-PublishFallbackConfigJson -ArtifactGxPath 'C:\GX18' -EngineBackend $backend | ConvertFrom-Json
        Assert-Equal $backend $withEngine.Engine.Backend "Engine.Backend=$backend"
        Assert-Equal 1 @($withEngine.Engine.PSObject.Properties).Count 'only Backend is written'
        Assert-Equal 2 $withEngine.ConfigSchemaVersion "schema version with $backend"
    }
    $rejected = $false
    try { Get-PublishFallbackConfigJson -ArtifactGxPath 'C:\GX18' -EngineBackend 'turbo' | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'an unknown backend must be rejected'

    # --- reading the previous publish\config.json ------------------------------
    $path = Join-Path $temp 'config.json'
    Assert-True ($null -eq (Get-PublishedEngineSection -ConfigPath $path)) 'a missing file has no Engine section'

    Set-Content -LiteralPath $path -Value 'not json {' -Encoding utf8
    Assert-True ($null -eq (Get-PublishedEngineSection -ConfigPath $path)) 'an unreadable file has no Engine section'

    Set-Content -LiteralPath $path -Value '{"ConfigSchemaVersion":2}' -Encoding utf8
    Assert-True ($null -eq (Get-PublishedEngineSection -ConfigPath $path)) 'a config without Engine has no Engine section'

    Set-Content -LiteralPath $path -Value '{"Engine":null}' -Encoding utf8
    Assert-True ($null -eq (Get-PublishedEngineSection -ConfigPath $path)) 'a null Engine is no section'

    Set-Content -LiteralPath $path -Value '{"ConfigSchemaVersion":2,"Engine":{"Backend":"sdk","Fallback":"error","TimeoutSeconds":60,"Executable":"C:\\machine\\gx-engine.exe"}}' -Encoding utf8
    $previous = Get-PublishedEngineSection -ConfigPath $path
    Assert-Equal 'sdk' $previous.Backend 'previous Engine.Backend is read'
    Assert-Equal 60 $previous.TimeoutSeconds 'previous Engine.TimeoutSeconds is read'

    # --- the warning ---------------------------------------------------------
    Assert-True ($null -eq (Get-DiscardedEngineSectionWarning -PreviousEngine $null)) 'no previous section, no warning'
    Assert-True ($null -eq (Get-DiscardedEngineSectionWarning -PreviousEngine $null -EngineBackend 'sdk')) 'no previous section, no warning with a backend'

    $dropped = Get-DiscardedEngineSectionWarning -PreviousEngine $previous
    Assert-True ($null -ne $dropped) 'dropping the section must warn'
    Assert-True ($dropped -match 'Backend=sdk') 'the warning names the discarded backend'
    Assert-True ($dropped -match 'Fallback=error') 'the warning names the discarded fallback'
    Assert-True ($dropped -match 'TimeoutSeconds=60') 'the warning names the discarded timeout'
    Assert-True ($dropped -match '-EngineBackend') 'the warning says how to keep the choice'
    Assert-True ($dropped -notmatch 'machine') 'the warning does not print the machine-specific executable path'
    Assert-True ($dropped -match 'Executable=<path>') 'the warning flags that an executable was set'

    $partial = Get-DiscardedEngineSectionWarning -PreviousEngine $previous -EngineBackend 'auto'
    Assert-True ($null -ne $partial) 'rewriting only Backend must warn about the other keys'
    Assert-True ($partial -match 'Fallback=error' -and $partial -match 'TimeoutSeconds=60') 'the partial warning names the dropped keys'
    Assert-True ($partial -notmatch 'Backend=sdk') 'the partial warning does not list the key that was rewritten'

    $onlyBackend = [pscustomobject]@{ Backend = 'sdk' }
    Assert-True ($null -eq (Get-DiscardedEngineSectionWarning -PreviousEngine $onlyBackend -EngineBackend 'sdk')) 'a section that only held Backend loses nothing when Backend is rewritten'
    Assert-True ($null -ne (Get-DiscardedEngineSectionWarning -PreviousEngine $onlyBackend)) 'a section that only held Backend is lost when nothing is rewritten'
    Assert-True ($null -eq (Get-DiscardedEngineSectionWarning -PreviousEngine ([pscustomobject]@{}))) 'an empty section loses nothing'

    # --- build.ps1 and install.ps1 are wired to it ------------------------------
    $build = [IO.File]::ReadAllText((Join-Path $root 'build.ps1'))
    $install = [IO.File]::ReadAllText((Join-Path $root 'install.ps1'))
    $readAt = $build.IndexOf('Get-PublishedEngineSection', [StringComparison]::Ordinal)
    $cleanAt = $build.IndexOf('Get-ChildItem -Path "$publishDir\*" | Remove-Item', [StringComparison]::Ordinal)
    Assert-True ($readAt -ge 0 -and $cleanAt -ge 0) 'build.ps1 reads the previous Engine section and cleans publish'
    Assert-True ($readAt -lt $cleanAt) 'build.ps1 must read the previous Engine section before it cleans publish'
    Assert-True ($build -match '\[ValidateSet\(''sdk'', ''auto'', ''gxengine''\)\]\s*\[string\]\$EngineBackend') 'build.ps1 declares -EngineBackend'
    Assert-True ($build.Contains('Get-PublishFallbackConfigJson -ArtifactGxPath $artifactGxPath -EngineBackend $EngineBackend')) 'build.ps1 writes the config through the shared function'
    Assert-True ($build.Contains('Get-DiscardedEngineSectionWarning')) 'build.ps1 warns about a discarded Engine section'
    Assert-True ($install -match '\[ValidateSet\(''sdk'', ''auto'', ''gxengine''\)\]\s*\[string\]\$EngineBackend') 'install.ps1 declares -EngineBackend'
    Assert-True ($install.Contains('@buildArguments')) 'install.ps1 forwards -EngineBackend to build.ps1'
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host 'publish-config: fallback config, Engine section bookkeeping and build/install wiring passed' -ForegroundColor Green
