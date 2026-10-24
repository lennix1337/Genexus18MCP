# Fallback config.json written into publish\ by build.ps1, and the bookkeeping
# that keeps a previous Engine section from vanishing without a word (issue #649).
# Dot-source this file; it only defines functions.

function Get-PublishedEngineSection {
    <#
    .SYNOPSIS
    Returns the Engine object of an existing publish\config.json, or $null.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $null }
    try {
        $document = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        # An unreadable previous config is simply not a source of settings; the
        # build rewrites the file either way.
        return $null
    }
    if ($null -eq $document -or $document -isnot [pscustomobject]) { return $null }
    if (@($document.PSObject.Properties | ForEach-Object { $_.Name }) -notcontains 'Engine') { return $null }
    $engine = $document.Engine
    if ($null -eq $engine -or $engine -isnot [pscustomobject]) { return $null }
    return $engine
}

function Get-PublishFallbackConfigJson {
    <#
    .SYNOPSIS
    The sanitized neutral fallback config.json for publish\ as JSON text.
    .DESCRIPTION
    No KB catalog and no startup default. An Engine section is written only when
    -EngineBackend is given, so a release artifact never carries one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactGxPath,
        # build.ps1 forwards its own unbound -EngineBackend, which is an empty string.
        [AllowEmptyString()][ValidateSet('', 'sdk', 'auto', 'gxengine')][string]$EngineBackend
    )

    $config = @{
        ConfigSchemaVersion = 2
        GatewayMode = 'stdio-isolated'
        GeneXus = @{
            InstallationPath = $ArtifactGxPath
            WorkerExecutable = 'worker\\GxMcp.Worker.exe'
        }
        Server = @{
            HttpPort = 0
            McpStdio = $true
            SessionIdleTimeoutMinutes = 10
            WorkerIdleTimeoutMinutes = 5
            EmitStructuredContent = $false
            TerseResponses = $true
        }
        Environment = @{
            ResolutionPolicy = 'strict'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($EngineBackend)) {
        $config.Engine = @{ Backend = $EngineBackend }
    }
    return ($config | ConvertTo-Json -Depth 4)
}

function Get-DiscardedEngineSectionWarning {
    <#
    .SYNOPSIS
    The warning for an Engine section the rebuilt config.json does not carry, or $null.
    .DESCRIPTION
    $PreviousEngine is the result of Get-PublishedEngineSection taken before
    publish\ was cleaned. With no -EngineBackend the whole section is dropped; with
    one, only Backend is rewritten, so Fallback, Executable and TimeoutSeconds are
    the keys that go missing.
    #>
    [CmdletBinding()]
    param(
        $PreviousEngine,
        [string]$EngineBackend
    )

    if ($null -eq $PreviousEngine) { return $null }

    $names = @($PreviousEngine.PSObject.Properties | ForEach-Object { $_.Name })
    $describe = {
        param([string[]]$keys)
        $parts = foreach ($key in $keys) {
            if ($key -eq 'Executable') { 'Executable=<path>' } else { "$key=$($PreviousEngine.$key)" }
        }
        $parts -join ', '
    }

    if ([string]::IsNullOrWhiteSpace($EngineBackend)) {
        if ($names.Count -eq 0) { return $null }
        return "The previous publish\config.json had an Engine section ($(& $describe $names)) that the rebuilt " +
            "config.json does not carry; the Gateway now resolves the default backend. Run build.ps1 with " +
            "-EngineBackend <sdk|auto|gxengine> to write the choice again, or point GX_CONFIG_PATH at a config " +
            "that holds it."
    }

    $dropped = @($names | Where-Object { $_ -ne 'Backend' })
    if ($dropped.Count -eq 0) { return $null }
    return "The previous publish\config.json Engine section also set $(& $describe $dropped); only " +
        "Engine.Backend=$EngineBackend was written to the rebuilt config.json. Edit publish\config.json afterwards " +
        "to restore the other keys."
}
