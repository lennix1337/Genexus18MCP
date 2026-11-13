$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Guards #649: build.ps1 empties publish\ and regenerates publish\config.json,
# which the direct-exe launcher and start_mcp.bat read. An Engine section in the
# previous file must be carried over on request and must never vanish silently.
# The build steps themselves are executed (not just pattern-matched) by cutting
# them out of build.ps1, so a build.ps1 that stops reading the old config before
# the clean step, or stops using the preserved section, fails here.

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\publish-config.ps1')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Get-BuildSegment {
    param([string]$Source, [string]$StartMarker, [string]$EndMarker)
    $start = $Source.IndexOf($StartMarker, [StringComparison]::Ordinal)
    Assert-True ($start -ge 0) "build.ps1 no longer contains '$StartMarker'."
    $end = $Source.IndexOf($EndMarker, $start, [StringComparison]::Ordinal)
    Assert-True ($end -gt $start) "build.ps1 no longer contains '$EndMarker' after '$StartMarker'."
    return $Source.Substring($start, $end - $start)
}

$buildSource = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
$cleanSegment = Get-BuildSegment $buildSource '# 1. Clean Publish Directory' 'Write-Host "[build] Building solutions..."'
$configSegment = Get-BuildSegment $buildSource '# 5. Write a SANITIZED neutral fallback config.json' '# 6. Generate start_mcp.bat'

# Runs the clean step, then the config step, of build.ps1 against a temp publish dir.
function Invoke-BuildConfigSteps {
    param([string]$PublishDir, [bool]$Preserve)
    $artifactGxPath = 'C:\Program Files (x86)\GeneXus\GeneXus18'
    $PreserveEngineConfig = [switch]$Preserve
    $publishDir = $PublishDir
    $WarningPreference = 'SilentlyContinue'
    $output = & ([scriptblock]::Create("$cleanSegment`n$configSegment")) 6>&1
    return @($output | ForEach-Object { [string]$_ })
}

function New-PublishDirWithConfig {
    param([string]$Directory, [string]$ConfigJson)
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Directory 'config.json') -Value $ConfigJson -Encoding utf8
    Set-Content -LiteralPath (Join-Path $Directory 'stale.txt') -Value 'stale' -Encoding ascii
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-build-publish-config-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $previous = '{"ConfigSchemaVersion":2,"GatewayMode":"stdio-isolated","Engine":{"Backend":"SDK","Fallback":"error","TimeoutSeconds":45,"Executable":"C:\\dev\\gx-engine.exe","Bogus":1}}'

    # 1. -PreserveEngineConfig carries the behavioral keys across the clean step.
    $dir = Join-Path $temp 'preserve'
    New-PublishDirWithConfig $dir $previous
    $log = Invoke-BuildConfigSteps -PublishDir $dir -Preserve $true
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'stale.txt'))) 'The clean step no longer empties publish\ (test would not prove anything).'
    $written = Get-Content -LiteralPath (Join-Path $dir 'config.json') -Raw | ConvertFrom-Json
    Assert-True ($null -ne $written.PSObject.Properties['Engine']) 'Engine section was dropped although -PreserveEngineConfig was requested.'
    Assert-True ($written.Engine.Backend -eq 'sdk') "Engine.Backend not preserved/normalized: '$($written.Engine.Backend)'."
    Assert-True ($written.Engine.Fallback -eq 'error') 'Engine.Fallback not preserved.'
    Assert-True ($written.Engine.TimeoutSeconds -eq 45) 'Engine.TimeoutSeconds not preserved.'
    Assert-True ($null -eq $written.Engine.PSObject.Properties['Executable']) 'Machine-specific Engine.Executable must not be preserved.'
    Assert-True ($null -eq $written.Engine.PSObject.Properties['Bogus']) 'Unknown Engine keys must not be preserved.'
    Assert-True ($written.ConfigSchemaVersion -eq 2 -and $written.GatewayMode -eq 'stdio-isolated') 'Regenerated config lost its neutral base.'
    Assert-True ($written.GeneXus.InstallationPath -eq 'C:\Program Files (x86)\GeneXus\GeneXus18') 'Regenerated config must carry the catalog artifact path.'
    Assert-True ($written.Environment.ResolutionPolicy -eq 'strict') 'Regenerated config lost Environment.ResolutionPolicy.'
    Assert-True ($null -eq $written.PSObject.Properties['KnowledgeBases'] -and $null -eq $written.Environment.PSObject.Properties['KBPath']) 'Regenerated config must stay KB-neutral.'
    Assert-True (@($log | Where-Object { $_ -match 'Dropping Engine\.Executable' }).Count -eq 1) 'Dropping Engine.Executable was not reported.'

    # 2. Without the switch the section is dropped, loudly.
    $dir = Join-Path $temp 'discard'
    New-PublishDirWithConfig $dir $previous
    $log = Invoke-BuildConfigSteps -PublishDir $dir -Preserve $false
    $written = Get-Content -LiteralPath (Join-Path $dir 'config.json') -Raw | ConvertFrom-Json
    Assert-True ($null -eq $written.PSObject.Properties['Engine']) 'Engine section leaked into the artifact without -PreserveEngineConfig.'
    Assert-True (@($log | Where-Object { $_ -match 'Discarding the Engine section' -and $_ -match '-PreserveEngineConfig' }).Count -eq 1) 'Discarding an Engine section must be reported with the switch to use.'

    # 3. No previous config / no Engine section: neutral file, no warnings, no Engine.
    foreach ($case in @('missing', 'no-engine', 'empty-engine', 'malformed')) {
        $dir = Join-Path $temp $case
        switch ($case) {
            'missing' { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            'no-engine' { New-PublishDirWithConfig $dir '{"ConfigSchemaVersion":2}' }
            'empty-engine' { New-PublishDirWithConfig $dir '{"Engine":{}}' }
            'malformed' { New-PublishDirWithConfig $dir '{ not json' }
        }
        $log = Invoke-BuildConfigSteps -PublishDir $dir -Preserve $true
        $written = Get-Content -LiteralPath (Join-Path $dir 'config.json') -Raw | ConvertFrom-Json
        Assert-True ($null -eq $written.PSObject.Properties['Engine']) "Case '$case': an Engine section appeared from nowhere."
        Assert-True (@($log | Where-Object { $_ -match 'WARNING' }).Count -eq 0) "Case '$case': unexpected warning: $($log -join '|')"
    }

    # 4. Invalid values are rejected, not shipped; nothing valid left means no section.
    $resolved = Resolve-PublishEngineSection -Preserve -PreviousSection ('{"Backend":"turbo","Fallback":"maybe","TimeoutSeconds":-3}' | ConvertFrom-Json)
    Assert-True ($null -eq $resolved.Section) 'Invalid Engine values must not be preserved.'
    Assert-True ($resolved.Warnings.Count -eq 3) "Expected one warning per rejected value, got $($resolved.Warnings.Count)."
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

# 5. The local-checkout installer is the flow that must not lose the choice.
$installSource = Get-Content -LiteralPath (Join-Path $root 'install.ps1') -Raw
Assert-True ($installSource -match '(?m)^\s*&\s*\(Join-Path \$root "build\.ps1"\)\s+-PreserveEngineConfig\b') 'install.ps1 must call build.ps1 with -PreserveEngineConfig.'

# 6. A release build (-RequireEngineBinaries) fails when the Rust binaries were not
# produced; a plain developer build only warns. The gate is cut out of build.ps1
# and executed against a temp publish dir.
$engineGateSegment = Get-BuildSegment $buildSource '# 6.07 Release builds must ship the default backend' '# 6.1 Slim the publish output'
function Fail-Build([string]$message, [int]$exitCode = 1) { throw "Fail-Build: $message" }
function Invoke-EngineGate {
    param([string]$PublishDir, [bool]$Require)
    $publishDir = $PublishDir
    $RequireEngineBinaries = [switch]$Require
    $output = & ([scriptblock]::Create($engineGateSegment)) 6>&1
    return @($output | ForEach-Object { [string]$_ })
}
$gateTemp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-engine-gate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $gateTemp -Force | Out-Null
try {
    $threw = $null
    try { Invoke-EngineGate -PublishDir $gateTemp -Require $true } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -match 'gx-engine\.exe was not produced') "A release build must fail when gx-engine.exe is missing, got: $threw"

    Set-Content -LiteralPath (Join-Path $gateTemp 'gx-engine.exe') -Value 'engine' -Encoding ascii
    $threw = $null
    try { Invoke-EngineGate -PublishDir $gateTemp -Require $true } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -match 'gxwire\.exe was not produced') "A release build must fail when gxwire.exe is missing, got: $threw"

    $devLog = Invoke-EngineGate -PublishDir $gateTemp -Require $false
    Assert-True (@($devLog | Where-Object { $_ -match 'Warning: gxwire\.exe was not produced' }).Count -eq 1) 'A developer build must only warn about the missing binary.'

    Set-Content -LiteralPath (Join-Path $gateTemp 'gxwire.exe') -Value 'wire' -Encoding ascii
    Invoke-EngineGate -PublishDir $gateTemp -Require $true | Out-Null
}
finally {
    if (Test-Path -LiteralPath $gateTemp) { Remove-Item -LiteralPath $gateTemp -Recurse -Force }
}

Write-Host 'build-publish-config: Engine section preservation checks passed' -ForegroundColor Green
