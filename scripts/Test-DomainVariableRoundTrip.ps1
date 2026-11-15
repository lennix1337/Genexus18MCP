[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerExe,
    [Parameter(Mandatory)] [string] $GeneXusPath,
    [Parameter(Mandatory)] [string] $SourceKbPath,
    [string] $ImportKbPath,
    [string] $OutputFile = (Join-Path $env:TEMP 'GxMcp-DomainVariableRoundTrip.xpz'),
    # Regular expression, matched without case against the resolved path of each KB, of the KB
    # paths this destructive regression must never touch. Empty protects no path.
    [string] $ProtectedKbPathPattern = $env:GXMCP_TEST_PROTECTED_KB_PATTERN,
    [Parameter(Mandatory)] [switch] $ConfirmDisposableKbs
)

$ErrorActionPreference = 'Stop'

function Assert-DisposableKb([string] $Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not [string]::IsNullOrWhiteSpace($ProtectedKbPathPattern)) {
        # A pattern that cannot be evaluated stops the run: it was written to protect something.
        try {
            $protected = [regex]::IsMatch($resolved, $ProtectedKbPathPattern,
                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        catch {
            throw "ProtectedKbPathPattern is not a valid regular expression: $($_.Exception.Message)"
        }
        if ($protected) {
            throw "Refusing to run the destructive regression against a protected KB path: $resolved"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'knowledgebase.connection'))) {
        throw "Not a GeneXus KB directory: $resolved"
    }
    return $resolved
}

function Get-KbDatabaseName([string] $Path) {
    [xml] $connection = Get-Content -LiteralPath (Join-Path $Path 'knowledgebase.connection') -Raw
    return [string] $connection.ConnectionInformation.DBName
}

function Start-TestWorker([string] $KbPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Resolve-Path -LiteralPath $WorkerExe).Path
    $start.Arguments = '--kb "' + $KbPath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GX_PROGRAM_DIR'] = (Resolve-Path -LiteralPath $GeneXusPath).Path
    $start.EnvironmentVariables['GX_KB_PATH'] = $KbPath
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Could not start GxMcp.Worker.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'Worker exited before SDK readiness.' }
        if ($line -match 'notifications/worker/sdk_ready') { return $process }
    }
    throw 'Timed out waiting for worker SDK readiness.'
}

function Invoke-Worker($Process, [string] $Id, [string] $Method, [string] $Action,
    [string] $Target, [hashtable] $Arguments, [string] $Payload = $null, [switch] $AllowError) {
    $request = [ordered]@{
        jsonrpc = '2.0'; id = $Id; method = $Method; action = $Action
        target = $Target; params = $Arguments
    }
    if ($null -ne $Payload) { $request.payload = $Payload }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress -Depth 20))
    $Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $Process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Worker exited while handling request $Id." }
        try { $response = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ([string] $response.id -ne $Id) { continue }
        if ($null -ne $response.error) {
            throw "Request $Id failed: $($response.error | ConvertTo-Json -Compress -Depth 20)"
        }
        $status = [string] $response.result.status
        if ($status -and $status -ne 'ok') {
            if ($AllowError) { return $response }
            throw "Request $Id returned status '$status': $($response.result | ConvertTo-Json -Compress -Depth 20)"
        }
        return $response
    }
    throw "Timed out waiting for request $Id."
}

function Stop-TestWorker($Process) {
    if ($null -eq $Process) { return }
    try { $Process.StandardInput.Close() } catch { }
    if (-not $Process.WaitForExit(15000)) { $Process.Kill() }
    $Process.Dispose()
}

function Assert-XpzHasNativeDomains([string] $Path, [string[]] $DomainNames, [string] $ProcedureName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $xml = [Text.StringBuilder]::new()
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.EndsWith('.xml', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [void] $xml.AppendLine($reader.ReadToEnd()) } finally { $reader.Dispose() }
        }
        $content = $xml.ToString()
        if ($content -match '(?is)ATTCUSTOMTYPE.{0,200}(dom|domain):') {
            throw 'XPZ contains a display-only Domain token in ATTCUSTOMTYPE.'
        }
        foreach ($domain in $DomainNames) {
            if ($content -notmatch ('Domain:' + [regex]::Escape($domain))) {
                throw "XPZ does not contain the native Domain reference for '$domain'."
            }
        }
        if ($content -notmatch [regex]::Escape($ProcedureName)) { throw 'Procedure missing from XPZ.' }
    }
    finally { $archive.Dispose() }
}

function Assert-VariablesResponse($Response, [string[]] $DomainNames) {
    $json = $Response.result | ConvertTo-Json -Compress -Depth 30
    if ($json -match '(?i)(dom|domain):') {
        throw 'Variables read exposed a display-only Domain token.'
    }
    foreach ($domain in $DomainNames) {
        if ($json -notmatch [regex]::Escape($domain)) {
            throw "Variables read does not reflect Domain '$domain'."
        }
    }
}

$source = Assert-DisposableKb $SourceKbPath
$import = if ($ImportKbPath) { Assert-DisposableKb $ImportKbPath } else { $source }
if ($ImportKbPath -and (Get-KbDatabaseName $source) -eq (Get-KbDatabaseName $import)) {
    throw 'SourceKbPath and ImportKbPath point to the same KB database; use genuinely distinct disposable KBs.'
}

$suffix = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$moduleName = 'McpDomainModule' + $suffix
$automaticDomain = 'McpIdAutomatico' + $suffix
$manualDomain = 'McpIdManual' + $suffix
$procedure = 'McpDomainRoundTrip' + $suffix
$sourceWorker = $null
$importWorker = $null
try {
    $sourceWorker = Start-TestWorker $source
    Invoke-Worker $sourceWorker '1' 'Object' 'Create' $moduleName @{ type = 'Module' } | Out-Null
    Invoke-Worker $sourceWorker '2' 'Object' 'Create' $automaticDomain @{
        type = 'Domain'; dataType = 'Numeric'; length = 4; decimals = 0
    } | Out-Null
    Invoke-Worker $sourceWorker '3' 'Object' 'Create' $manualDomain @{
        type = 'Domain'; dataType = 'Numeric'; length = 8; decimals = 0; destModule = $moduleName
    } | Out-Null
    Invoke-Worker $sourceWorker '4' 'Object' 'Create' $procedure @{
        type = 'Procedure'; destModule = $moduleName
    } | Out-Null

    # Direct add based on a Root Module Domain.
    Invoke-Worker $sourceWorker '5' 'Write' 'AddVariable' $procedure @{
        varName = 'EmpresaID'; typeName = 'Numeric'; basedOn = $automaticDomain; length = 4; decimals = 0
    } | Out-Null
    # Primitive add followed by conversion to a qualified Domain in a named Module.
    Invoke-Worker $sourceWorker '6' 'Write' 'AddVariable' $procedure @{
        varName = 'ArmazemID'; typeName = 'Numeric'; length = 8; decimals = 0
    } | Out-Null
    Invoke-Worker $sourceWorker '7' 'Write' 'ModifyVariable' $procedure @{
        varName = 'ArmazemID'; typeName = 'Numeric'; basedOn = "$moduleName.$manualDomain"; length = 8; decimals = 0
    } | Out-Null
    Invoke-Worker $sourceWorker '8' 'Write' 'Rules' $procedure @{ part = 'Rules' } `
        'parm(in:&EmpresaID, in:&ArmazemID);' | Out-Null

    # Unknown Domain must be rejected before Save and preserve Rules and every other variable.
    Invoke-Worker $sourceWorker '8a' 'Write' 'AddVariable' $procedure @{
        varName = 'GuardID'; typeName = 'Numeric'; length = 6; decimals = 0
    } | Out-Null
    $rejected = Invoke-Worker $sourceWorker '8b' 'Write' 'ModifyVariable' $procedure @{
        varName = 'GuardID'; typeName = 'Numeric'; basedOn = 'DomainThatMustNotExist'
    } -AllowError
    if ([string] $rejected.result.error.code -ne 'UnknownType') {
        throw 'Unknown Domain did not return UnknownType before persistence.'
    }
    $guardRead = Invoke-Worker $sourceWorker '8c' 'Read' 'ExtractParts' $procedure @{
        parts = @('Variables', 'Rules')
    }
    $guardJson = $guardRead.result | ConvertTo-Json -Compress -Depth 30
    if ($guardJson -notmatch 'GuardID' -or $guardJson -notmatch 'NUMERIC\(6' -or
        $guardJson -notmatch 'parm\(in:&EmpresaID, in:&ArmazemID\)') {
        throw 'Rejected Domain conversion changed the guard variable or Rules.'
    }

    Invoke-Worker $sourceWorker '9' 'Transfer' 'export' '' @{
        action = 'export'; targets = @($procedure); outputFile = $OutputFile; type = 'Procedure'
    } | Out-Null
    Invoke-Worker $sourceWorker '10' 'Transfer' 'inspect' '' @{ action = 'inspect'; file = $OutputFile } | Out-Null
    Assert-XpzHasNativeDomains $OutputFile @($automaticDomain, $manualDomain) $procedure
    Stop-TestWorker $sourceWorker
    $sourceWorker = $null

    # Reopen is deliberate: verifies the binding is not merely present in the in-memory part.
    $sourceWorker = Start-TestWorker $source
    $sourceRead = Invoke-Worker $sourceWorker '11' 'Read' 'GetVariables' $procedure @{}
    Assert-VariablesResponse $sourceRead @($automaticDomain, $manualDomain)
    Stop-TestWorker $sourceWorker
    $sourceWorker = $null

    $importWorker = Start-TestWorker $import
    Invoke-Worker $importWorker '12' 'Transfer' 'import' '' @{
        action = 'import'; file = $OutputFile; dryRun = $false; confirm = $true
    } | Out-Null
    $importRead = Invoke-Worker $importWorker '13' 'Read' 'GetVariables' $procedure @{}
    Assert-VariablesResponse $importRead @($automaticDomain, $manualDomain)

    [pscustomobject]@{
        Passed = $true
        Procedure = $procedure
        RootDomain = $automaticDomain
        NamedModuleDomain = "$moduleName.$manualDomain"
        Xpz = (Resolve-Path -LiteralPath $OutputFile).Path
        ImportUsedDistinctKb = [bool] $ImportKbPath
    }
}
finally {
    Stop-TestWorker $sourceWorker
    Stop-TestWorker $importWorker
}
