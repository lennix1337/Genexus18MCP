[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KbPath,
    [Parameter(Mandatory = $true)][string]$FixtureManifest,
    [Parameter(Mandatory = $true)][string]$GatewayExe,
    [string]$GxPath = $(if ($env:GX_PATH) { $env:GX_PATH } else { 'C:\Program Files (x86)\GeneXus\GeneXus18' }),
    [ValidateRange(1024, 65535)][int]$HttpPort,
    [ValidateRange(30, 7200)][int]$TimeoutSeconds = 2400,
    [string]$Alias = 'live-fixture'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-FreeBuildAllPort {
    foreach ($candidate in 55200..55299) {
        if (-not (Get-NetTCPConnection -LocalPort $candidate -State Listen -ErrorAction SilentlyContinue)) { return $candidate }
    }
    throw 'No free isolated HTTP port was found in 55200..55299.'
}

function Get-JsonProperty {
    param([object]$Object, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($key in $Object.Keys) {
            if ([string]$key -ieq $Name) { return $Object[$key] }
        }
        return $null
    }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($property) { return $property.Value }
    return $null
}

function Convert-McpToolPayload {
    param([object]$Envelope, [int]$Depth = 0)
    if ($null -eq $Envelope) { return $null }
    if ($Depth -gt 4) { return $Envelope }
    $structured = Get-JsonProperty $Envelope 'structuredContent'
    if ($null -ne $structured) {
        # Some gateway responses preserve an adapter envelope as
        # structuredContent.message where the actual operation JSON is encoded
        # as a string. Decode that one extra layer so evidence fields remain
        # machine-readable instead of being hidden in a diagnostic blob.
        $message = Get-JsonProperty $structured 'message'
        if ($message -is [string] -and -not [string]::IsNullOrWhiteSpace($message)) {
            try { return Convert-McpToolPayload ($message | ConvertFrom-Json -AsHashtable -ErrorAction Stop) ($Depth + 1) } catch { }
        }
        return $structured
    }
    $result = Get-JsonProperty $Envelope 'result'
    if ($null -ne $result) {
        $structured = Get-JsonProperty $result 'structuredContent'
        $content = Get-JsonProperty $result 'content'
        $resultType = Get-JsonProperty $result 'resultType'
        $isError = Get-JsonProperty $result 'isError'
        if ($null -eq $structured -and $null -eq $content -and $null -eq $resultType -and $null -eq $isError) {
            # A lifecycle result also has a property named `result`, but it is
            # already an operation payload rather than an MCP response envelope.
            return $Envelope
        }
        if ($null -ne $structured) {
            $message = Get-JsonProperty $structured 'message'
            if ($message -is [string] -and -not [string]::IsNullOrWhiteSpace($message)) {
                try { return Convert-McpToolPayload ($message | ConvertFrom-Json -AsHashtable -ErrorAction Stop) ($Depth + 1) } catch { }
            }
            return $structured
        }
        if ($content) {
            $text = Get-JsonProperty @($content)[0] 'text'
            if ($text -is [string]) {
                try { return Convert-McpToolPayload ($text | ConvertFrom-Json -AsHashtable -ErrorAction Stop) ($Depth + 1) } catch { return [pscustomobject]@{ message = $text } }
            }
        }
        return $result
    }
    $payload = Get-JsonProperty $Envelope 'payload'
    if ($null -ne $payload) {
        return Convert-McpToolPayload $payload ($Depth + 1)
    }
    $message = Get-JsonProperty $Envelope 'message'
    if ($message -is [string] -and -not [string]::IsNullOrWhiteSpace($message)) {
        try { return Convert-McpToolPayload ($message | ConvertFrom-Json -AsHashtable -ErrorAction Stop) ($Depth + 1) } catch { }
    }
    return $Envelope
}

function Find-BuildAllUnavailableReason {
    param([object]$Payload, [int]$Depth = 0)
    if ($null -eq $Payload -or $Depth -gt 5) { return $null }
    $messageParts = New-Object System.Collections.Generic.List[string]
    foreach ($name in @('message', 'error', 'detail', 'hint', 'output', 'head', 'tail', 'LastLine', 'Message')) {
        $value = Get-JsonProperty $Payload $name
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) { [void]$messageParts.Add([string]$value) }
    }
    $message = $messageParts -join ' '
    if ($message -match '(?i)(cloud|nuvem).*(user|usu[aá]rio)|(user|usu[aá]rio).*(required|obrigat|not complete)|(input parameters are not complete).*(user|usu[aá]rio)') {
        return "GeneXus cloud build is unavailable until User is configured: $message"
    }
    foreach ($name in @('result', 'payload', 'Output', 'PhaseFailure', 'content')) {
        $nested = Get-JsonProperty $Payload $name
        if ($null -eq $nested) { continue }
        if ($name -ieq 'content') {
            $nested = Get-JsonProperty @($nested)[0] 'text'
            if ($nested -is [string]) {
                try { $nested = $nested | ConvertFrom-Json -AsHashtable -ErrorAction Stop } catch { }
            }
        }
        $reason = Find-BuildAllUnavailableReason $nested ($Depth + 1)
        if ($reason) { return $reason }
    }
    return $null
}

function Resolve-BuildAllPayload {
    param([object]$Payload)
    $current = $Payload
    for ($depth = 0; $depth -le 5 -and $null -ne $current; $depth++) {
        $hasEvidence = $null -ne (Get-JsonProperty $current 'buildMode') -or
            $null -ne (Get-JsonProperty $current 'BuildMode') -or
            $null -ne (Get-JsonProperty $current 'kbOpened') -or
            $null -ne (Get-JsonProperty $current 'KbOpened')
        if ($hasEvidence) { return $current }
        $status = Get-JsonProperty $current 'status'
        $operationResult = Get-JsonProperty $current 'result'
        if ($null -ne $status -and $null -ne $operationResult) { return $current }
        $message = Get-JsonProperty $current 'message'
        if ($message -is [string] -and -not [string]::IsNullOrWhiteSpace($message)) {
            try { $current = $message | ConvertFrom-Json -AsHashtable -ErrorAction Stop; continue } catch { }
        }
        $nested = Get-JsonProperty $current 'payload'
        if ($null -ne $nested) { $current = $nested; continue }
        $nested = Get-JsonProperty $current 'result'
        if ($null -ne $nested) { $current = $nested; continue }
        $content = Get-JsonProperty $current 'content'
        if ($content) {
            $text = Get-JsonProperty @($content)[0] 'text'
            if ($text -is [string]) {
                try { $current = $text | ConvertFrom-Json -AsHashtable -ErrorAction Stop; continue } catch { }
            }
        }
        break
    }
    return $current
}

function Get-BuildAllField {
    param([object]$Primary, [object]$Fallback, [string[]]$Names)
    foreach ($source in @($Primary, $Fallback)) {
        foreach ($name in $Names) {
            $value = Get-JsonProperty $source $name
            if ($null -ne $value) { return $value }
        }
    }
    return $null
}

function Test-BuildAllTerminal {
    param([object]$Payload)
    $status = [string](Get-JsonProperty $Payload 'status')
    if ([string]::IsNullOrWhiteSpace($status)) { $status = [string](Get-JsonProperty $Payload 'Status') }
    if ([string]::IsNullOrWhiteSpace($status)) {
        $operation = Get-JsonProperty $Payload 'operationId'
        if ($null -eq $operation) { $operation = Get-JsonProperty $Payload 'job_id' }
        if (-not [string]::IsNullOrWhiteSpace([string]$operation)) { return $false }
    }
    return $status -notin @('Running', 'Accepted', 'Queued', 'InProgress', 'Pending')
}

function Get-BuildAllEvidence {
    param([object]$Payload)
    $payload = Resolve-BuildAllPayload (Convert-McpToolPayload $Payload)
    if ($null -eq $payload) {
        return [pscustomobject]@{ live = 'fail'; reason = 'Build All returned no payload.'; terminal = $true }
    }
    $operationResult = Get-JsonProperty $payload 'result'
    $buildMode = [string](Get-BuildAllField $payload $operationResult @('buildMode', 'BuildMode'))
    $kbOpened = Get-BuildAllField $payload $operationResult @('kbOpened', 'KbOpened')
    $buildAllDone = Get-BuildAllField $payload $operationResult @('buildAllDone', 'BuildAllDone')
    $reorgRequired = Get-BuildAllField $payload $operationResult @('reorgRequired', 'ReorgRequired')
    $exitCode = Get-BuildAllField $payload $operationResult @('msBuildExitCode', 'MsBuildExitCode')
    $logPath = Get-BuildAllField $payload $operationResult @('fullLogPath', 'FullLogPath')
    $status = [string](Get-BuildAllField $payload $operationResult @('status', 'Status'))
    $unavailable = Find-BuildAllUnavailableReason $Payload
    if (-not $unavailable) { $unavailable = Find-BuildAllUnavailableReason $payload }
    $reasons = New-Object System.Collections.Generic.List[string]
    if ($buildMode -ine 'BuildAll') { [void]$reasons.Add("buildMode must be BuildAll (got '$buildMode')") }
    if ($kbOpened -ne $true) { [void]$reasons.Add('kbOpened must be true') }
    if ($buildAllDone -ne $true) { [void]$reasons.Add('buildAllDone must be true') }
    if ($reorgRequired -eq $true -or $status -ieq 'ReorgRequired') { [void]$reasons.Add('reorgRequired must be false') }
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) { [void]$reasons.Add("msBuildExitCode must be 0 (got '$exitCode')") }
    if ([string]::IsNullOrWhiteSpace([string]$logPath)) { [void]$reasons.Add('fullLogPath must be present') }
    $live = if ($unavailable) { 'unavailable' } elseif ($reasons.Count -eq 0) { 'pass' } else { 'fail' }
    [pscustomobject]@{
        live = $live
        terminal = Test-BuildAllTerminal $payload
        status = $status
        buildMode = $buildMode
        kbOpened = [bool]$kbOpened
        buildAllDone = [bool]$buildAllDone
        reorgRequired = [bool]$reorgRequired
        msBuildExitCode = if ($null -eq $exitCode) { $null } else { [int]$exitCode }
        fullLogPath = [string]$logPath
        reason = if ($unavailable) { $unavailable } else { ($reasons -join '; ') }
        payload = $payload
    }
}

function Get-BuildAllTimeoutResult {
    param([int]$TimeoutSeconds)
    [pscustomobject]@{
        live = 'fail'
        terminal = $false
        reason = "Build All did not reach a terminal result within $TimeoutSeconds seconds."
    }
}

function Assert-BuildAllFixture {
    param([object]$Fixture, [string]$ResolvedKbPath)
    if ($Fixture.schemaVersion -ne 1 -or -not $Fixture.synthetic -or -not $Fixture.disposable) {
        throw 'Fixture must identify a synthetic, disposable KB using schemaVersion 1.'
    }
    if ([string]::IsNullOrWhiteSpace($Fixture.kbPath) -or [IO.Path]::GetFullPath($Fixture.kbPath).TrimEnd('\') -ine $ResolvedKbPath.TrimEnd('\')) {
        throw 'Fixture kbPath must match the explicitly selected KB.'
    }
    if ($Fixture.isolation.verified -ne $true) { throw 'Fixture database isolation must be verified.' }
}

function Invoke-BuildAllRpc {
    param(
        [string]$BaseUrl,
        [string]$SessionId,
        [string]$Method,
        [object]$Params,
        [int]$Id,
        [switch]$Notification
    )
    $headers = @{
        'MCP-Protocol-Version' = '2025-11-25'
        'Content-Type' = 'application/json'
        Accept = 'application/json, text/event-stream'
    }
    if ($SessionId) { $headers['MCP-Session-Id'] = $SessionId }
    $envelope = [ordered]@{ jsonrpc = '2.0'; method = $Method; params = $Params }
    if (-not $Notification) { $envelope.id = $Id }
    $body = $envelope | ConvertTo-Json -Depth 30 -Compress
    $response = Invoke-WebRequest -Uri $BaseUrl -Method Post -Headers $headers -Body $body -UseBasicParsing
    $envelope = $response.Content | ConvertFrom-Json
    [pscustomobject]@{ envelope = $envelope; sessionId = [string]$response.Headers['MCP-Session-Id'] }
}

if (-not (Test-Path -LiteralPath $KbPath -PathType Container)) { throw "KB directory not found: $KbPath" }
$KbPath = (Resolve-Path -LiteralPath $KbPath).Path
if (-not (Test-Path -LiteralPath $FixtureManifest -PathType Leaf)) { throw "Fixture manifest not found: $FixtureManifest" }
$fixture = Get-Content -LiteralPath $FixtureManifest -Raw | ConvertFrom-Json
Assert-BuildAllFixture $fixture $KbPath
if (-not (Test-Path -LiteralPath $GatewayExe -PathType Leaf)) { throw "Gateway executable not found: $GatewayExe" }
if (-not (Test-Path -LiteralPath (Join-Path $GxPath 'Artech.Architecture.Common.dll') -PathType Leaf)) { throw "GeneXus SDK not found under '$GxPath'." }
if ($HttpPort -le 0) { $HttpPort = Get-FreeBuildAllPort }
$runDirectory = Join-Path $root ('scratchpad\live-build-all-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$configPath = Join-Path $runDirectory 'config.json'
@{
    GeneXus = @{ InstallationPath = $GxPath; WorkerExecutable = (Join-Path $root 'publish\worker\GxMcp.Worker.exe') }
    Server = @{ HttpPort = $HttpPort; McpStdio = $false; BindAddress = '127.0.0.1' }
    Environment = @{ DefaultKb = $Alias; KBs = @(@{ Alias = $Alias; Path = $KbPath }) }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
$gateway = $null
try {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $GatewayExe
    $psi.WorkingDirectory = Split-Path -Parent $GatewayExe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['GX_MCP_PORT'] = $HttpPort.ToString()
    $psi.EnvironmentVariables['GX_MCP_STDIO'] = 'false'
    $psi.EnvironmentVariables['GXMCP_TEST_KB'] = $KbPath
    $psi.EnvironmentVariables['GX_PATH'] = $GxPath
    $psi.EnvironmentVariables['GX_CONFIG_PATH'] = $configPath
    $gateway = [Diagnostics.Process]::Start($psi)
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($gateway.HasExited) { throw "Gateway exited before binding port $HttpPort (exit $($gateway.ExitCode))." }
        if (Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "Gateway did not bind port $HttpPort within 30 seconds." }
    $baseUrl = "http://127.0.0.1:$HttpPort/mcp"
    $init = Invoke-BuildAllRpc -BaseUrl $baseUrl -SessionId '' -Method 'initialize' -Params @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'gxmcp-build-all-gate'; version = '3.0.0' }
    } -Id 1
    $sessionId = $init.sessionId
    if ([string]::IsNullOrWhiteSpace($sessionId)) { throw 'initialize did not return MCP-Session-Id.' }
    [void](Invoke-BuildAllRpc -BaseUrl $baseUrl -SessionId $sessionId -Method 'notifications/initialized' -Params @{} -Notification)
    $call = Invoke-BuildAllRpc -BaseUrl $baseUrl -SessionId $sessionId -Method 'tools/call' -Params @{
        name = 'genexus_lifecycle'; arguments = @{ action = 'build_all'; kb = $Alias; wait = 1 }
    } -Id 3
    $payload = Convert-McpToolPayload $call.envelope
    $operationId = [string](Get-JsonProperty $payload 'operationId')
    if ([string]::IsNullOrWhiteSpace($operationId)) { $operationId = [string](Get-JsonProperty $payload 'job_id') }
    $terminal = Test-BuildAllTerminal $payload
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $evidence = $null
    while (-not $terminal -and (Get-Date) -lt $deadline) {
        if ([string]::IsNullOrWhiteSpace($operationId)) { break }
        Start-Sleep -Seconds 2
        $poll = Invoke-BuildAllRpc -BaseUrl $baseUrl -SessionId $sessionId -Method 'tools/call' -Params @{
            name = 'genexus_lifecycle'; arguments = @{ action = 'result'; target = "op:$operationId"; wait = 10 }
        } -Id ([int](Get-Random -Minimum 1000 -Maximum 999999))
        $payload = Convert-McpToolPayload $poll.envelope
        $terminal = Test-BuildAllTerminal $payload
    }
    if (-not $terminal) {
        $evidence = Get-BuildAllTimeoutResult $TimeoutSeconds
    } else {
        $evidence = Get-BuildAllEvidence $payload
    }
    Write-Output ($evidence | ConvertTo-Json -Depth 12 -Compress)
    if ($evidence.live -eq 'pass') { exit 0 }
    if ($evidence.live -eq 'unavailable') { exit 2 }
    exit 1
}
finally {
    if ($gateway) {
        try { if (-not $gateway.HasExited) { $gateway.Kill(); [void]$gateway.WaitForExit(5000) } } catch { }
        $gateway.Dispose()
    }
    if (Test-Path -LiteralPath $runDirectory) { Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
