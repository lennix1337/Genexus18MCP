<#
.SYNOPSIS
Durability smoke for genexus_workwith writes (issue #590), with cleanup of what it creates (issue #645).

.DESCRIPTION
The genexus_workwith write receipts verify by rereading the WorkWith instance from
the same in-memory object that was just saved, and they say so in
persistenceEvidence (independentPersistedReread=false, provesDurability=false).
This smoke is the durability proof that the receipt deliberately does not claim:
on a DISPOSABLE Knowledge Base with a standard WorkWith instance it authors a filter
attribute (and optionally an action), restarts the Worker process with
genexus_worker_reload, and rereads the definition on a Worker that never held the
written instance in memory. It then removes what it created (remove_filter_attribute,
and remove_action for the optional lane), restarts the Worker again, and checks on
that Worker that the filter reads back equal to its baseline.

WHAT THIS SMOKE DEMONSTRATES
- In the tested scenario the written definition survives a Worker restart: the
  reread runs on a new Worker process (the whoami pid changes, which the smoke
  asserts), so the result cannot come from the in-memory instance the receipt
  verified against.
- The receipt contract of this build: every write receipt carries
  persistenceEvidence with provesDurability=false.
- The cleanup survives a second Worker restart: the filter attribute and the condition
  this run created are absent, and the Level's filter (attributes with name, description,
  domain, default and variableName, plus the conditions, in order) equals its baseline.
  Comparing the neighbors' default and variableName is intentionally stronger than the
  tool's own removal verifier, which compares neighbors by name, description and domain.

WHAT THIS SMOKE DOES NOT COVER
- The GeneXus IDE: it does not prove the IDE displays the attribute, nor that the
  derived objects regenerate (receipts declare derivedObjectRegeneration).
- Concurrent writers, or a KB that the IDE has open at the same time.
- Other Knowledge Bases or GeneXus majors than the one it ran against.
- A pristine KB: the filter node stays (removing the attribute preserves it) and
  derived objects are not regenerated. Run it only against a scratch KB.
- The optional action lane on failure: the action is removed only on the success path,
  so a run that fails after add_action can leave the action behind. The filter cleanup
  result (cleanup=pass) does not cover it.
- Cleanup after a transport failure or a dead Worker, which can prevent the removal;
  the summary then reports cleanup=fail. Only a kill of this script, or a hang in a call
  without a timeout, prevents the summary itself.

CLEANUP RULES
- The attribute name is normalized once (trim, one leading '&' removed). A name that
  already exists in the baseline stops the run as a validation failure (exit 1) before
  anything is written: the smoke never removes an attribute it did not create.
- After a failed run the finalizer removes the filter attribute this run may have written
  (a failed or unreadable add can still have persisted), with its own list_filters read
  and no extra Worker restart; the result is cleanup=unverified or cleanup=fail, never pass.
- Only the exact condition text this run sent is passed in removeConditions, and only
  while it is still on the filter. A condition the run did not create is never removed;
  if one still references the variable the tool refuses with
  FilterAttributeReferencedByConditions, which counts as a cleanup failure.

OUTCOME FIELDS
durability = pass | fail | not_attempted
cleanup    = pass | unverified | fail | not_attempted
outcome    = pass | fail | unavailable
outcome=pass needs durability=pass and cleanup=pass. durability=pass with cleanup=fail
is a possible result and is reported in the summary.

PREREQUISITES
- PowerShell 7+.
- A Gateway serving Streamable HTTP (default http://127.0.0.1:5000/mcp), for
  example publish\start_mcp.bat or the Gateway your MCP client started.
- A disposable KB (directory name or alias containing "teste", or pass
  -AllowKbMutation) with the standard WorkWith pattern applied to -Transaction and
  a `filter` node on the Selection of the target Level. The filter node is created
  in the GeneXus IDE; without it the smoke exits 2 (WorkWithFilterNotPresent).
- For -ReloadMode hard, -WorkerSourceDir must point at a built Worker output
  directory (for example src\GxMcp.Worker\bin\Debug); mode=soft restarts the
  Worker with the binaries it already runs. Both modes replace the process.

EXIT CODES
0 = pass.
1 = fail: any rejected validation (including an attribute that already exists), a broken
    receipt contract, a durability failure, or a cleanup that failed or is unverified.
2 = unavailable: a precondition is missing before any write was attempted (KB,
    Transaction or filter absent, no Gateway, PowerShell older than 7).

.EXAMPLE
pwsh -NoProfile -File scripts\workwith_persistence_smoke.ps1 -KbPath C:\KBs\KBTesteWorkWith -Transaction Customer

.EXAMPLE
pwsh -NoProfile -File scripts\workwith_persistence_smoke.ps1 -Kb kbtesteworkwith -Transaction Customer -Procedure procPrintCustomer -ActionParameters CustomerId -ReloadMode hard -WorkerSourceDir .\src\GxMcp.Worker\bin\Debug

.NOTES
-Procedure enables the optional action lane. genexus_workwith checks the number of
parameters against the Procedure's parm() rule (ArityMismatch), so pass one
-ActionParameters entry per parm() argument (attributes, &variables or expressions).
Without them the lane only works for a Procedure that declares no parameters.
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:5000/mcp',
    [string]$KbPath = '',
    [string]$Kb = '',
    [Parameter(Mandatory = $true)][string]$Transaction,
    [string]$Level = '',
    [string]$Attribute = '',
    [string]$Condition = '',
    [string]$Procedure = '',
    [string[]]$ActionParameters = @(),
    [string]$Tab = 'General',
    [ValidateSet('soft', 'hard')][string]$ReloadMode = 'soft',
    [string]$WorkerSourceDir = '',
    [switch]$AllowKbMutation,
    [string]$SummaryPath = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host '[WW-SMOKE] unavailable: PowerShell 7+ required. Run with pwsh.'
    exit 2
}

# Upper bound for each call of the cleanup, so a dead Worker cannot hang the summary.
$script:CleanupTimeoutSec = 90

$script:Summary = [ordered]@{
    smoke        = 'workwith_persistence_smoke'
    issue        = 590
    demonstrates = 'The authored WorkWith definition is reread, unchanged, by a new Worker process after genexus_worker_reload; the filter attribute and condition this run created are then removed and a second new Worker reads the filter back equal to its baseline.'
    doesNotCover = @('GeneXus IDE visibility', 'derived-object regeneration', 'concurrent writers', 'other KBs or GeneXus majors', 'a pristine KB (the filter node stays)', 'the optional action lane on failure (removed only on the success path)')
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    outcome      = 'unavailable'
    durability   = 'not_attempted'
    cleanup      = 'not_attempted'
    evidence     = [ordered]@{}
    errors       = @()
}

# What this run wrote, so the finalizer removes only its own artifacts and only once.
$script:State = [ordered]@{
    FilterWritten = $false
    CleanupDone   = $false
    ActionAdded   = $false
    ActionRemoved = $false
}
$script:SummaryWritten = $false

function Write-Summary {
    if ($script:SummaryWritten) { return }
    $script:SummaryWritten = $true
    $script:Summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $json = $script:Summary | ConvertTo-Json -Depth 10
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        Set-Content -LiteralPath $SummaryPath -Value $json -Encoding utf8
        Write-Host "[WW-SMOKE] summary written to $SummaryPath"
    }
    Write-Host $json
}

# Exit-Fail and Exit-Unavailable never end the script: they throw, so the single finalizer at the
# bottom can clean up, write the summary once and choose the exit code last.
function New-SmokeStop([string]$Outcome, [string]$Reason) {
    $stop = [System.Exception]::new($Reason)
    $stop.Data['smokeOutcome'] = $Outcome
    return $stop
}

function Exit-Unavailable([string]$Reason) {
    # "Unavailable" means nothing was written; once a write was attempted it is a failure.
    $outcome = if ($script:State.FilterWritten) { 'fail' } else { 'unavailable' }
    throw (New-SmokeStop $outcome $Reason)
}

function Exit-Fail([string]$Reason) {
    throw (New-SmokeStop 'fail' $Reason)
}

# The tool normalizes the same way: trim, and drop one leading '&'.
function ConvertTo-FilterName([string]$Value) {
    $name = ([string]$Value).Trim()
    if ($name.StartsWith('&')) { $name = $name.Substring(1).Trim() }
    return $name
}

# --- MCP transport (same contract as scripts/mcp_smoke.ps1) -------------------------
$script:SessionId = ''

function Invoke-Mcp([string]$Method, [hashtable]$Params, [int]$TimeoutSec = 0) {
    $headers = @{
        'MCP-Protocol-Version' = '2025-11-25'
        'Content-Type'         = 'application/json'
        'Accept'               = 'application/json, text/event-stream'
    }
    if (-not [string]::IsNullOrWhiteSpace($script:SessionId)) { $headers['MCP-Session-Id'] = $script:SessionId }
    $bodyObj = @{ jsonrpc = '2.0'; id = [guid]::NewGuid().ToString('N'); method = $Method }
    if ($null -ne $Params) { $bodyObj['params'] = $Params }
    $body = $bodyObj | ConvertTo-Json -Depth 20 -Compress
    $resp = Invoke-WebRequest -Uri $BaseUrl -Method Post -Headers $headers -Body $body -UseBasicParsing -TimeoutSec $TimeoutSec
    $sid = $resp.Headers['MCP-Session-Id']
    if ($sid) { $script:SessionId = [string]($sid | Select-Object -First 1) }
    return ($resp.Content | ConvertFrom-Json)
}

# Calls one tool and returns the Worker/Gateway envelope ({status, code, result, error}).
function Invoke-Tool([string]$Name, [hashtable]$Arguments, [string]$Label, [int]$TimeoutSec = 0) {
    $envelope = Invoke-Mcp -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments } -TimeoutSec $TimeoutSec
    if ($null -ne $envelope.error) { Exit-Fail "${Label}: JSON-RPC error $($envelope.error.message)" }
    $text = $envelope.result.content[0].text
    if ([string]::IsNullOrWhiteSpace($text)) { Exit-Fail "${Label}: empty tool payload." }
    try { return ($text | ConvertFrom-Json) } catch { Exit-Fail "${Label}: payload is not JSON: $text" }
}

function Test-PayloadOk($Payload) {
    if ($null -eq $Payload) { return $false }
    $status = [string]$Payload.status
    return ($status -eq 'ok' -or $status -eq 'partial' -or $status -eq 'Reloaded')
}

function Get-PayloadCode($Payload) {
    if ($null -eq $Payload) { return '' }
    if ($Payload.error) { return [string]$Payload.error.code }
    return [string]$Payload.code
}

function Get-PayloadMessage($Payload) {
    if ($null -eq $Payload) { return '' }
    if ($Payload.error) { return [string]$Payload.error.message }
    return [string]$Payload.detail
}

function Assert-Ok($Payload, [string]$Label) {
    if ($null -eq $Payload) { Exit-Fail "${Label}: no payload." }
    if (Test-PayloadOk $Payload) { return }
    Exit-Fail "${Label}: status=$([string]$Payload.status) code=$(Get-PayloadCode $Payload) $(Get-PayloadMessage $Payload)"
}

function Get-WorkerPid([string]$Label) {
    $who = Invoke-Tool -Name 'genexus_whoami' -Arguments @{ kb = $Kb } -Label $Label
    $workerPid = $null
    try { $workerPid = $who.worker.pid } catch { }
    if ($null -eq $workerPid) { try { $workerPid = $who.result.worker.pid } catch { } }
    if ($null -eq $workerPid) { Exit-Fail "${Label}: whoami did not expose worker.pid; the restart cannot be evidenced." }
    return [int]$workerPid
}

function Find-FilterAttribute($Filters, [string]$Name) {
    if ($null -eq $Filters -or $null -eq $Filters.attributes) { return $null }
    return @($Filters.attributes | Where-Object { (ConvertTo-FilterName ([string]$_.name)) -ieq $Name } | Select-Object -First 1)[0]
}

function Find-TabAction($Tabs, [string]$TabCode, [string]$Name) {
    if ($null -eq $Tabs) { return $null }
    $tabObj = @($Tabs | Where-Object { [string]$_.code -ieq $TabCode -or [string]$_.name -ieq $TabCode } | Select-Object -First 1)[0]
    if ($null -eq $tabObj -or $null -eq $tabObj.actions) { return $null }
    return @($tabObj.actions | Where-Object { [string]$_.name -ieq $Name } | Select-Object -First 1)[0]
}

# The projection of list_filters that the baseline, the write and the cleanup are compared on:
# level, the attributes (name, description, domain, default, variableName) and the conditions,
# in order.
function Get-FilterProjection($Filters) {
    $attributes = @()
    if ($null -ne $Filters -and $null -ne $Filters.attributes) {
        foreach ($item in @($Filters.attributes)) {
            $attributes += [ordered]@{
                name         = [string]$item.name
                description  = [string]$item.description
                domain       = [string]$item.domain
                default      = [string]$item.default
                variableName = [string]$item.variableName
            }
        }
    }
    $conditions = @()
    if ($null -ne $Filters -and $null -ne $Filters.conditions) {
        $conditions = @($Filters.conditions | ForEach-Object { [string]$_ })
    }
    return [ordered]@{ level = [string]$Filters.level; attributes = $attributes; conditions = $conditions }
}

function Get-ProjectionJson($Projection) {
    return ($Projection | ConvertTo-Json -Depth 6 -Compress)
}

# Baseline plus the attribute and the condition this run appended at the end (the SDK appends).
# Returns the first difference, or $null.
function Get-AppendedProjectionDiff($Baseline, $Observed) {
    if ($Observed.level -cne $Baseline.level) { return "the Level changed: expected '$($Baseline.level)', observed '$($Observed.level)'." }
    $expectedAttributes = @($Baseline.attributes)
    $observedAttributes = @($Observed.attributes)
    if ($observedAttributes.Count -ne ($expectedAttributes.Count + 1)) {
        return "filter attributes: expected $($expectedAttributes.Count + 1) (baseline plus '$Attribute'), observed $($observedAttributes.Count)."
    }
    for ($i = 0; $i -lt $expectedAttributes.Count; $i++) {
        if ((Get-ProjectionJson $expectedAttributes[$i]) -cne (Get-ProjectionJson $observedAttributes[$i])) {
            return "neighbor filter attribute #$i changed: expected $(Get-ProjectionJson $expectedAttributes[$i]), observed $(Get-ProjectionJson $observedAttributes[$i])."
        }
    }
    $appended = $observedAttributes[$observedAttributes.Count - 1]
    if ((ConvertTo-FilterName ([string]$appended.name)) -ine $Attribute) {
        return "the appended filter attribute is '$($appended.name)', expected '$Attribute'."
    }
    $expectedConditions = @($Baseline.conditions)
    $observedConditions = @($Observed.conditions)
    if ($observedConditions.Count -ne ($expectedConditions.Count + 1)) {
        return "filter conditions: expected $($expectedConditions.Count + 1) (baseline plus the smoke condition), observed $($observedConditions.Count)."
    }
    for ($i = 0; $i -lt $expectedConditions.Count; $i++) {
        if ($observedConditions[$i] -cne $expectedConditions[$i]) { return "preserved condition #$i changed: expected '$($expectedConditions[$i])', observed '$($observedConditions[$i])'." }
    }
    if ($observedConditions[$observedConditions.Count - 1] -cne $Condition) {
        return "the appended condition is '$($observedConditions[$observedConditions.Count - 1])', expected '$Condition'."
    }
    return $null
}

function Invoke-WorkerRestart([string]$Label) {
    $before = Get-WorkerPid -Label "$Label whoami(before)"
    $reloadArgs = @{ mode = $ReloadMode; kb = $Kb; alias = $Kb }
    if ($ReloadMode -eq 'hard') { $reloadArgs['sourceDir'] = (Resolve-Path -LiteralPath $WorkerSourceDir).Path }
    Write-Host "[WW-SMOKE] genexus_worker_reload mode=$ReloadMode (worker pid before: $before)"
    $reload = Invoke-Tool -Name 'genexus_worker_reload' -Arguments $reloadArgs -Label "$Label genexus_worker_reload"
    Assert-Ok -Payload $reload -Label "$Label genexus_worker_reload"
    if ($reload.PSObject.Properties['swappedAndReady'] -and -not [bool]$reload.swappedAndReady) {
        Exit-Fail "${Label}: the replacement Worker did not become SDK-ready."
    }
    $after = Get-WorkerPid -Label "$Label whoami(after)"
    if ($after -eq $before) {
        Exit-Fail "${Label}: worker pid did not change ($before); the reread would not be independent of the written instance."
    }
    Write-Host "[WW-SMOKE] Worker restarted: pid $before -> $after"
    return [ordered]@{ pidBefore = $before; pidAfter = $after }
}

# Removes what this run created. It never exits and never throws: every step is isolated and
# bounded, so a failing step cannot stop the later ones or replace the error that ended the run.
# The condition passed in removeConditions is exactly the text this run sent, and only while it
# is still on the filter; the attribute is removed only when it is on the filter now.
function Invoke-SmokeCleanup([bool]$IncludeAction) {
    $script:State.CleanupDone = $true
    $result = [ordered]@{
        attemptedAtUtc         = [DateTime]::UtcNow.ToString('o')
        includesAction         = $IncludeAction
        attributePresentBefore = $null
        conditionPresentBefore = $null
        attributeRemoved       = $false
        removalCode            = ''
        receiptVerified        = $null
        actionRemoved          = $null
        errors                 = @()
    }

    try {
        $read = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'cleanup list_filters' -TimeoutSec $script:CleanupTimeoutSec
        if (-not (Test-PayloadOk $read)) { throw "status=$([string]$read.status) code=$(Get-PayloadCode $read) $(Get-PayloadMessage $read)" }
        $result.attributePresentBefore = ($null -ne (Find-FilterAttribute -Filters $read.result.filters -Name $Attribute))
        $result.conditionPresentBefore = (@($read.result.filters.conditions | ForEach-Object { [string]$_ }) -ccontains $Condition)
    } catch {
        $result.errors += "list_filters before the removal: $($_.Exception.Message)"
    }

    if ($result.attributePresentBefore -eq $true) {
        try {
            $removeArgs = @{ action = 'remove_filter_attribute'; name = $Transaction; level = $Level; attribute = $Attribute; kb = $Kb }
            if ($result.conditionPresentBefore -eq $true) { $removeArgs['removeConditions'] = @($Condition) }
            $remove = Invoke-Tool -Name 'genexus_workwith' -Arguments $removeArgs -Label 'cleanup remove_filter_attribute' -TimeoutSec $script:CleanupTimeoutSec
            $result.removalCode = Get-PayloadCode $remove
            if (Test-PayloadOk $remove) {
                $result.attributeRemoved = $true
                # An unverified receipt is never a pass by itself.
                $result.receiptVerified = ($result.removalCode -ne 'WorkWithFilterAttributeRemovedUnverified') -and ($remove.result.verified -eq $true)
            } elseif ($result.removalCode -ne 'FilterAttributeNotFound') {
                $result.errors += "remove_filter_attribute: $($result.removalCode) $(Get-PayloadMessage $remove)"
            }
        } catch {
            $result.errors += "remove_filter_attribute: $($_.Exception.Message)"
        }
    }

    if ($IncludeAction -and $script:State.ActionAdded -and -not $script:State.ActionRemoved) {
        try {
            $removeAction = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'remove_action'; name = $Transaction; tab = $Tab; actionName = $actionName; kb = $Kb } -Label 'cleanup remove_action' -TimeoutSec $script:CleanupTimeoutSec
            if (Test-PayloadOk $removeAction) {
                $script:State.ActionRemoved = $true
                $result.actionRemoved = $true
                if ($null -eq $removeAction.result.persistenceEvidence) { $result.errors += 'remove_action receipt carries no persistenceEvidence (issue #590 contract).' }
            } else {
                $result.actionRemoved = $false
                $result.errors += "remove_action: $(Get-PayloadCode $removeAction) $(Get-PayloadMessage $removeAction)"
            }
        } catch {
            $result.actionRemoved = $false
            $result.errors += "remove_action: $($_.Exception.Message)"
        }
    }

    $script:Summary.evidence.cleanup = $result
    return $result
}

# Failure path: best effort, no Worker restart. Without a restart the reread runs on the same
# Worker, so the best this can report is cleanup=unverified; cleanup=fail when the artifact is
# still there or the removal could not run.
function Invoke-FailureCleanup {
    $result = Invoke-SmokeCleanup -IncludeAction $false
    $status = 'unverified'
    if ($result.errors.Count -gt 0) {
        $status = 'fail'
    } else {
        try {
            $check = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'cleanup list_filters (verify)' -TimeoutSec $script:CleanupTimeoutSec
            if (Test-PayloadOk $check) {
                $stillThere = ($null -ne (Find-FilterAttribute -Filters $check.result.filters -Name $Attribute)) -or
                    (@($check.result.filters.conditions | ForEach-Object { [string]$_ }) -ccontains $Condition)
                if ($stillThere) {
                    $status = 'fail'
                    $result.errors += "the smoke filter attribute or its condition is still on the filter after the removal."
                }
            }
        } catch {
            $result.errors += "list_filters after the removal: $($_.Exception.Message)"
        }
    }
    $script:Summary.cleanup = $status
    if ($script:State.ActionAdded -and -not $script:State.ActionRemoved) {
        $script:Summary.evidence.actionLaneResidue = "Action '$actionName' may remain on tab '$Tab' (removed only on the success path)."
    }
}

$primary = $null
# --- preconditions ----------------------------------------------------------------
try {
    if ([string]::IsNullOrWhiteSpace($KbPath) -and [string]::IsNullOrWhiteSpace($Kb)) {
        Exit-Unavailable 'Pass -KbPath (opened by this smoke) or -Kb (alias of a KB already open on the Gateway).'
    }
    if ($ReloadMode -eq 'hard' -and [string]::IsNullOrWhiteSpace($WorkerSourceDir)) {
        Exit-Unavailable '-ReloadMode hard requires -WorkerSourceDir (a built Worker output directory).'
    }
    if (-not [string]::IsNullOrWhiteSpace($KbPath) -and -not (Test-Path -LiteralPath $KbPath -PathType Container)) {
        Exit-Unavailable "KB path not found: $KbPath"
    }
    $kbLabel = if (-not [string]::IsNullOrWhiteSpace($KbPath)) { Split-Path -Leaf $KbPath } else { $Kb }
    if (-not $AllowKbMutation -and $kbLabel -notmatch 'teste') {
        # Fail closed: the smoke writes a filter attribute and restarts the Worker.
        Exit-Unavailable "Refusing to mutate '$kbLabel': the KB name does not contain 'teste'. Pass -AllowKbMutation only for a disposable KB."
    }

    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $Attribute = ConvertTo-FilterName $Attribute
    if ([string]::IsNullOrWhiteSpace($Attribute)) { $Attribute = "SmokeFilter$stamp" }
    if ([string]::IsNullOrWhiteSpace($Condition)) { $Condition = "&$Attribute.IsEmpty();" }
    $actionName = "SmokeAction$stamp"
    $script:Summary.evidence.kb = $kbLabel
    $script:Summary.evidence.transaction = $Transaction
    $script:Summary.evidence.attribute = $Attribute
    $script:Summary.evidence.condition = $Condition
    $script:Summary.evidence.reloadMode = $ReloadMode

    # --- 1. session -----------------------------------------------------------------------
    Write-Host '[WW-SMOKE] initialize'
    try {
        $init = Invoke-Mcp -Method 'initialize' -Params @{
            protocolVersion = '2025-11-25'
            capabilities    = @{}
            clientInfo      = @{ name = 'workwith-persistence-smoke'; version = '1.0.0' }
        }
    } catch {
        Exit-Unavailable "No Gateway answered at $BaseUrl ($($_.Exception.Message)). Start publish\start_mcp.bat or pass -BaseUrl."
    }
    if ([string]::IsNullOrWhiteSpace($script:SessionId)) { Exit-Unavailable 'initialize returned no MCP-Session-Id.' }
    if ($null -eq $init.result) { Exit-Unavailable 'initialize returned no result.' }

    # --- 2. KB ------------------------------------------------------------------------------
    if (-not [string]::IsNullOrWhiteSpace($KbPath)) {
        if ([string]::IsNullOrWhiteSpace($Kb)) { $Kb = (Split-Path -Leaf $KbPath).ToLowerInvariant() }
        Write-Host "[WW-SMOKE] genexus_kb open $KbPath as '$Kb'"
        $open = Invoke-Tool -Name 'genexus_kb' -Arguments @{ action = 'open'; path = $KbPath; alias = $Kb } -Label 'genexus_kb open'
        if ([string]$open.status -eq 'error') { Exit-Unavailable "genexus_kb open failed: $($open.error.code) $($open.error.message)" }
    }
    $script:Summary.evidence.kbAlias = $Kb

    # --- 3. baseline --------------------------------------------------------------------------
    Write-Host "[WW-SMOKE] list_filters baseline ($Transaction)"
    $filterArgs = @{ action = 'list_filters'; name = $Transaction; kb = $Kb }
    if (-not [string]::IsNullOrWhiteSpace($Level)) { $filterArgs['level'] = $Level }
    $baseline = Invoke-Tool -Name 'genexus_workwith' -Arguments $filterArgs -Label 'list_filters baseline'
    if ([string]$baseline.status -eq 'error') {
        $code = [string]$baseline.error.code
        if ($code -in @('ObjectNotFound', 'WorkWithInstanceNotFound', 'WorkWithPatternMismatch', 'WorkWithFilterNotPresent', 'WorkWithSelectionMissing', 'FilterLevelAmbiguous', 'FilterLevelNotFound', 'KB_CONTEXT_REQUIRED', 'KB_AMBIGUOUS')) {
            Exit-Unavailable "list_filters baseline: $code $($baseline.error.message)"
        }
        Exit-Fail "list_filters baseline: $code $($baseline.error.message)"
    }
    $resolvedLevel = [string]$baseline.result.filters.level
    if ([string]::IsNullOrWhiteSpace($Level)) { $Level = $resolvedLevel }
    $script:Summary.evidence.level = $resolvedLevel
    $baselineSource = [string]$baseline.result.observedSource
    $baselineProjection = Get-FilterProjection $baseline.result.filters
    $baselineCount = @($baselineProjection.attributes).Count
    $script:Summary.evidence.baseline = [ordered]@{
        observedSource = $baselineSource
        attributeCount = $baselineCount
        conditionCount = @($baselineProjection.conditions).Count
    }
    # Ownership guard: an attribute that already exists is not ours to write or to remove.
    if ($null -ne (Find-FilterAttribute -Filters $baseline.result.filters -Name $Attribute)) {
        Exit-Fail "Filter attribute '$Attribute' already exists before the write; pick another -Attribute. The smoke never removes an attribute it did not create."
    }
    Write-Host "[WW-SMOKE] baseline: level=$resolvedLevel attributes=$baselineCount"

    # --- 4. write -----------------------------------------------------------------------------
    Write-Host "[WW-SMOKE] add_filter_attribute $Attribute"
    # Marked before the call: an attempt can persist even when the answer is a failure or unreadable.
    $script:State.FilterWritten = $true
    $add = Invoke-Tool -Name 'genexus_workwith' -Arguments @{
        action     = 'add_filter_attribute'
        name       = $Transaction
        level      = $Level
        attribute  = $Attribute
        conditions = @($Condition)
        kb         = $Kb
    } -Label 'add_filter_attribute'
    Assert-Ok -Payload $add -Label 'add_filter_attribute'
    if (-not [bool]$add.result.verified) { Exit-Fail 'add_filter_attribute receipt is not verified.' }
    $evidenceBlock = $add.result.persistenceEvidence
    if ($null -eq $evidenceBlock) { Exit-Fail 'add_filter_attribute receipt carries no persistenceEvidence (issue #590 contract).' }
    if ([bool]$evidenceBlock.provesDurability) { Exit-Fail 'add_filter_attribute receipt claims provesDurability=true; the receipt must not claim what only this smoke proves.' }
    $script:Summary.evidence.filterReceipt = [ordered]@{
        verified            = [bool]$add.result.verified
        verificationScope   = [string]$add.result.verificationScope
        persistenceEvidence = $evidenceBlock
    }

    $actionLane = -not [string]::IsNullOrWhiteSpace($Procedure)
    if ($actionLane) {
        Write-Host "[WW-SMOKE] add_action $actionName -> $Procedure on tab $Tab"
        $addActionArgs = @{
            action     = 'add_action'
            name       = $Transaction
            tab        = $Tab
            actionName = $actionName
            caption    = 'Smoke action'
            procedure  = $Procedure
            kb         = $Kb
        }
        # The tool refuses a call whose parameter count differs from the Procedure's parm()
        # rule (ArityMismatch), so a Procedure with parameters needs matching -ActionParameters.
        if ($ActionParameters.Count -gt 0) { $addActionArgs['parameters'] = @($ActionParameters) }
        $script:State.ActionAdded = $true
        $addAction = Invoke-Tool -Name 'genexus_workwith' -Arguments $addActionArgs -Label 'add_action'
        Assert-Ok -Payload $addAction -Label 'add_action'
        if (-not [bool]$addAction.result.verified) { Exit-Fail 'add_action receipt is not verified.' }
        if ($null -eq $addAction.result.persistenceEvidence) { Exit-Fail 'add_action receipt carries no persistenceEvidence (issue #590 contract).' }
        $script:Summary.evidence.actionName = $actionName
    }

    # --- 5. restart + independent reread ------------------------------------------------------
    $restart = Invoke-WorkerRestart -Label 'after write'
    $script:Summary.evidence.restartAfterWrite = $restart

    Write-Host '[WW-SMOKE] list_filters on the new Worker'
    $after = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'list_filters after restart'
    Assert-Ok -Payload $after -Label 'list_filters after restart'
    $found = Find-FilterAttribute -Filters $after.result.filters -Name $Attribute
    if ($null -eq $found) {
        $script:Summary.durability = 'fail'
        Exit-Fail "Filter attribute '$Attribute' is absent after the Worker restart: the write did not persist."
    }
    $conditions = @($after.result.filters.conditions | ForEach-Object { [string]$_ })
    if ($conditions -notcontains $Condition) {
        $script:Summary.durability = 'fail'
        Exit-Fail "Condition '$Condition' is absent after the Worker restart (observed: $($conditions -join ' | '))."
    }
    $afterProjection = Get-FilterProjection $after.result.filters
    $afterCount = @($afterProjection.attributes).Count
    if ($afterCount -lt ($baselineCount + 1)) {
        $script:Summary.durability = 'fail'
        Exit-Fail "Filter attributes were truncated: baseline $baselineCount, after restart $afterCount."
    }
    $afterSource = [string]$after.result.observedSource
    $projectionAfterWrite = 'skipped: the reader changed between the baseline and the reread'
    if ($afterSource -eq $baselineSource) {
        $appendedDiff = Get-AppendedProjectionDiff -Baseline $baselineProjection -Observed $afterProjection
        if ($null -ne $appendedDiff) {
            $script:Summary.durability = 'fail'
            Exit-Fail "The filter after the Worker restart is not the baseline plus the smoke attribute and condition: $appendedDiff"
        }
        $projectionAfterWrite = 'baseline plus the appended attribute and condition (neighbors compared on name, description, domain, default, variableName)'
    }
    $script:Summary.evidence.filterAfterRestart = [ordered]@{
        attributeFound   = $true
        conditionFound   = $true
        attributeCount   = $afterCount
        observedSource   = $afterSource
        projectionChecked = $projectionAfterWrite
    }
    Write-Host "[WW-SMOKE] filter attribute '$Attribute' and its condition survived the restart (attributes: $baselineCount -> $afterCount)"

    if ($actionLane) {
        Write-Host '[WW-SMOKE] list_actions on the new Worker'
        $actionsAfter = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after restart'
        Assert-Ok -Payload $actionsAfter -Label 'list_actions after restart'
        if ($null -eq (Find-TabAction -Tabs $actionsAfter.result.tabs -TabCode $Tab -Name $actionName)) {
            $script:Summary.durability = 'fail'
            Exit-Fail "Action '$actionName' is absent after the Worker restart: the write did not persist."
        }
        Write-Host "[WW-SMOKE] action '$actionName' survived the restart"
    }
    $script:Summary.durability = 'pass'

    # --- 6. cleanup: one function, then one restart, then the proof ---------------------------
    $removing = "remove_filter_attribute $Attribute" + $(if ($actionLane) { " and remove_action $actionName" } else { '' })
    Write-Host "[WW-SMOKE] cleanup: $removing"
    $cleanup = Invoke-SmokeCleanup -IncludeAction ([bool]$actionLane)
    if ($cleanup.errors.Count -gt 0) {
        $script:Summary.cleanup = 'fail'
        Exit-Fail "Cleanup failed: $($cleanup.errors -join '; ')"
    }
    $restartAfterCleanup = Invoke-WorkerRestart -Label 'after cleanup'
    $script:Summary.evidence.restartAfterCleanup = $restartAfterCleanup

    Write-Host '[WW-SMOKE] list_filters on the second new Worker'
    $final = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_filters'; name = $Transaction; level = $Level; kb = $Kb } -Label 'list_filters after cleanup'
    Assert-Ok -Payload $final -Label 'list_filters after cleanup'
    # Absence first, on the main path, so a cleanup that never ran fails here and not in a comparison.
    if ($null -ne (Find-FilterAttribute -Filters $final.result.filters -Name $Attribute)) {
        $script:Summary.cleanup = 'fail'
        Exit-Fail "Filter attribute '$Attribute' is still present after remove_filter_attribute and a Worker restart: the cleanup did not persist."
    }
    $finalProjection = Get-FilterProjection $final.result.filters
    if (@($finalProjection.conditions) -ccontains $Condition) {
        $script:Summary.cleanup = 'fail'
        Exit-Fail "Condition '$Condition' is still present after remove_filter_attribute and a Worker restart: the cleanup did not persist."
    }
    if ($actionLane) {
        $actionsFinal = Invoke-Tool -Name 'genexus_workwith' -Arguments @{ action = 'list_actions'; name = $Transaction; kb = $Kb } -Label 'list_actions after cleanup'
        Assert-Ok -Payload $actionsFinal -Label 'list_actions after cleanup'
        if ($null -ne (Find-TabAction -Tabs $actionsFinal.result.tabs -TabCode $Tab -Name $actionName)) {
            $script:Summary.cleanup = 'fail'
            Exit-Fail "Action '$actionName' is still present after remove_action and a Worker restart: the removal did not persist."
        }
    }

    $cleanupStatus = 'pass'
    $finalSource = [string]$final.result.observedSource
    $comparison = 'filter equals its baseline (level, attributes with name/description/domain/default/variableName, conditions in order)'
    if ($finalSource -ne $baselineSource) {
        # Native and XML readers project differently; values are not compared across readers.
        $comparison = "not compared: the reader changed ($baselineSource -> $finalSource)"
        $cleanupStatus = 'unverified'
    } else {
        if ((Get-ProjectionJson $baselineProjection) -cne (Get-ProjectionJson $finalProjection)) {
            $script:Summary.cleanup = 'fail'
            Exit-Fail "The filter after the cleanup differs from its baseline: expected $(Get-ProjectionJson $baselineProjection), observed $(Get-ProjectionJson $finalProjection)."
        }
    }
    if ($cleanup.receiptVerified -eq $false) {
        # The removal receipt could not verify its own reread; it never counts as a pass by itself.
        $cleanupStatus = 'unverified'
    }
    $script:Summary.evidence.filterAfterCleanup = [ordered]@{
        attributeAbsent   = $true
        conditionAbsent   = $true
        attributeCount    = @($finalProjection.attributes).Count
        observedSource    = $finalSource
        comparison        = $comparison
        removalReceiptVerified = $cleanup.receiptVerified
    }
    $script:Summary.cleanup = $cleanupStatus
    Write-Host "[WW-SMOKE] cleanup of '$Attribute' survived the restart (cleanup=$cleanupStatus)"
} catch {
    # Captured before any cleanup, so the cleanup can neither replace nor hide the primary error.
    $primary = $_
}

# --- finalizer: clean up, write the summary once, choose the exit code last ---------------------
if ($script:State.FilterWritten -and -not $script:State.CleanupDone) {
    Write-Host '[WW-SMOKE] the run ended early: removing the filter attribute it may have written'
    try { Invoke-FailureCleanup } catch { $script:Summary.cleanup = 'fail'; $script:Summary.errors += "cleanup: $($_.Exception.Message)" }
}

if ($null -ne $primary) {
    $stopOutcome = $null
    try { $stopOutcome = $primary.Exception.Data['smokeOutcome'] } catch { }
    $script:Summary.outcome = if ($stopOutcome -eq 'unavailable') { 'unavailable' } else { 'fail' }
    $script:Summary.errors += $primary.Exception.Message
    if ($script:Summary.outcome -eq 'unavailable') {
        Write-Host "[WW-SMOKE] unavailable: $($primary.Exception.Message)"
    } else {
        Write-Host "[WW-SMOKE] FAIL: $($primary.Exception.Message)"
    }
} elseif ($script:Summary.durability -eq 'pass' -and $script:Summary.cleanup -eq 'pass') {
    $script:Summary.outcome = 'pass'
    Write-Host "[WW-SMOKE] PASS: the definition survived a Worker restart on '$kbLabel' and the smoke filter attribute '$Attribute' and its condition were removed (verified after a second restart). Not covered: IDE visibility, derived-object regeneration, a pristine KB."
} else {
    $script:Summary.outcome = 'fail'
    $script:Summary.errors += "durability=$($script:Summary.durability) cleanup=$($script:Summary.cleanup): the run needs both to be pass."
    Write-Host "[WW-SMOKE] FAIL: durability=$($script:Summary.durability) cleanup=$($script:Summary.cleanup)"
}
Write-Summary
switch ($script:Summary.outcome) {
    'pass' { exit 0 }
    'fail' { exit 1 }
    default { exit 2 }
}
