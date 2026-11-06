# Live validation gate for Server.WorkerSharingMode = "shared-host" (issue #854,
# concurrent-multi-session-workers Requirement 13 criteria 10 to 14).
#
# Checks, in this order:
#   two-gateways-one-kb  two independent stdio Gateways on -KbPath share one
#                        Worker_Host and one Worker; each submits at least
#                        -OperationsPerGateway SDK_Operations, overlapping in time.
#   one-gateway-two-kbs  one Gateway opens -KbPath and -SecondKbPath (two Physical_KB
#                        identities) and submits at least -OperationsPerGateway
#                        SDK_Operations to each, overlapping in time; each KB has its
#                        own Worker_Host and Worker.
#   cleanup              after every Gateway has exited and
#                        Server.WorkerIdleTimeoutMinutes plus 60 seconds has elapsed,
#                        no recorded Worker or Worker_Host process and no
#                        Ownership_Registry record for either KB remains.
#
# Each check is 'passed', 'failed' or 'unavailable'. The KB guard runs first (a
# target outside the two configured disposable KBs is refused and failed with the
# rejected path), then the SDK/license/KB prerequisites (unavailable with the
# missing prerequisite named), then the check itself. The gate passes only when
# every check passed and cleanup counted zero leftovers. Exit codes: 0 passed,
# 1 failed, 2 unavailable; the last status line contains 'live=<status>'.
#
# The SDK_Operations are reads (genexus_read with refresh) under Engine.Backend
# "sdk", so they reach the shared-host broker queue and never build, deploy or
# touch GeneXus Cloud. The Gateways run from the published artifact with stdio
# piped by this script, a temporary strict config under scratchpad/, and an
# isolated GXMCP_LOG_DIR per Gateway. Only processes this script started or
# recorded (matched by PID and start time) are ever stopped.
#
# The report under docs/benchmarks/ and the JSON summary are redacted: configured
# KB paths, the GeneXus installation, the profile config path, the user profile,
# the user name and the machine name become placeholders.
#
# Requires PowerShell 7+. Contract tests: scripts/tests/test-live-shared-host.test.ps1.
#   pwsh -NoProfile -File scripts/test-live-shared-host.ps1 -SecondKbPath <second disposable KB>
[CmdletBinding()]
param(
    [string]$KbPath = 'C:\KBs\KBTeste',

    [string]$SecondKbPath = $env:GXMCP_TEST_KB2,

    [string]$GxPath = $env:GX_PATH,

    [ValidateRange(20, 1000)]
    [int]$OperationsPerGateway = 20,

    [string]$ReportPath,

    [string]$SummaryPath,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ is required for the live harness. Run with 'pwsh', not Windows PowerShell 5.1."
}
$root = Split-Path -Parent $PSScriptRoot

# --- Pure functions (extracted and exercised by the contract test) -----------

function Test-SharedHostDisposableKbPath {
    # Same rule as Resolve-TestKbPath in scripts/sdk-local-build.ps1: a directory
    # directly under C:\KBs whose name contains 'teste'.
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
        $parent = Split-Path -Parent $full
        $leaf = Split-Path -Leaf $full
    }
    catch { return $false }
    return [bool]($parent -ieq 'C:\KBs' -and $leaf -like '*teste*')
}

function Get-SharedHostAllowedKbPaths {
    # The allowlist is exactly the configured KBs that also pass the disposable rule.
    param([string]$KbPath, [string]$SecondKbPath)
    $allowed = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($KbPath, $SecondKbPath)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (-not (Test-SharedHostDisposableKbPath -Path $candidate)) { continue }
        $full = [IO.Path]::GetFullPath($candidate).TrimEnd('\', '/')
        $duplicate = $false
        foreach ($existing in $allowed) { if ($existing -ieq $full) { $duplicate = $true } }
        if (-not $duplicate) { $allowed.Add($full) }
    }
    return [string[]]$allowed.ToArray()
}

function Get-SharedHostKbRefusal {
    # $null when the path is one of the allowed KBs, otherwise the normalized rejected path.
    param([string]$Path, [string[]]$AllowedKbPaths)
    $normalized = [string]$Path
    try {
        if (-not [string]::IsNullOrWhiteSpace($Path)) { $normalized = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }
    }
    catch { $normalized = [string]$Path }
    if ([string]::IsNullOrWhiteSpace($normalized)) { return $normalized }
    foreach ($allowed in @($AllowedKbPaths)) {
        if ([string]::IsNullOrWhiteSpace($allowed)) { continue }
        if ($allowed.TrimEnd('\', '/') -ieq $normalized) { return $null }
    }
    return $normalized
}

function Get-SharedHostPrerequisites {
    # Each probe receives a path and must return [bool]; a throw or any other result
    # fails closed as not available.
    param([string]$GxPath, [string]$KbPath, [string]$SecondKbPath, [hashtable]$Probes = @{})
    $defaults = @{
        sdk = {
            param($Path)
            if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
            return [bool](Test-Path -LiteralPath ([IO.Path]::Combine($Path, 'Artech.Architecture.Common.dll')) -PathType Leaf)
        }
        # No offline API reports the GeneXus license without loading the SDK. The
        # static probe requires the installation; a license refusal while opening the
        # KB or running an operation is reported by the check as 'license' unavailable.
        license = {
            param($Path)
            if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
            return [bool](Test-Path -LiteralPath ([IO.Path]::Combine($Path, 'Artech.Architecture.Common.dll')) -PathType Leaf)
        }
        # Same KB marker as cli/lib/config.js: a .gxw file or knowledgebase.connection.
        kb = {
            param($Path)
            if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
            if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
            $markers = @(Get-ChildItem -LiteralPath $Path -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like '*.gxw' -or $_.Name -ieq 'knowledgebase.connection' })
            return [bool]($markers.Count -gt 0)
        }
    }
    $details = @{
        sdk = @('GeneXus SDK anchor Artech.Architecture.Common.dll found in the selected installation',
            'GeneXus SDK anchor Artech.Architecture.Common.dll not found in the selected installation; set -GxPath or GX_PATH')
        license = @('GeneXus installation present; a license refusal while opening the KB is reported as license unavailable',
            'GeneXus license could not be confirmed for the selected installation')
        kb = @('KB directory with a .gxw file or knowledgebase.connection found',
            'KB directory not found or not a GeneXus KB')
        'second-kb' = @('second KB directory with a .gxw file or knowledgebase.connection found',
            'second KB directory not found or not a GeneXus KB')
    }
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($entry in @(@('sdk', 'sdk', $GxPath), @('license', 'license', $GxPath), @('kb', 'kb', $KbPath), @('second-kb', 'kb', $SecondKbPath))) {
        $name = $entry[0]
        $probeName = $entry[1]
        $argument = $entry[2]
        if ($name -eq 'second-kb') {
            if ([string]::IsNullOrWhiteSpace($SecondKbPath)) {
                $items.Add([pscustomobject]@{ name = $name; available = $false; detail = 'no second disposable KB configured; set -SecondKbPath or GXMCP_TEST_KB2' })
                continue
            }
            $first = $null
            $second = $null
            try { $first = [IO.Path]::GetFullPath($KbPath).TrimEnd('\', '/'); $second = [IO.Path]::GetFullPath($SecondKbPath).TrimEnd('\', '/') } catch { }
            if ($null -ne $first -and $first -ieq $second) {
                $items.Add([pscustomobject]@{ name = $name; available = $false; detail = 'the second KB must be a different KB directory from the first' })
                continue
            }
        }
        $probe = $null
        if ($null -ne $Probes -and $Probes.ContainsKey($probeName)) { $probe = $Probes[$probeName] } else { $probe = $defaults[$probeName] }
        $available = $false
        $detail = $null
        try {
            $value = & $probe $argument
            if ($value -is [bool]) {
                $available = $value
            }
            else {
                $detail = $details[$name][1] + ' (probe returned a non-boolean result)'
            }
        }
        catch {
            $detail = $details[$name][1] + ' (probe failed)'
        }
        if ($null -eq $detail) {
            if ($available) { $detail = $details[$name][0] } else { $detail = $details[$name][1] }
        }
        $items.Add([pscustomobject]@{ name = $name; available = [bool]$available; detail = $detail })
    }
    return $items.ToArray()
}

function Get-SharedHostCheckPlan {
    param([string]$KbPath, [string]$SecondKbPath)
    $twoKbTargets = @($KbPath)
    if (-not [string]::IsNullOrWhiteSpace($SecondKbPath)) { $twoKbTargets += $SecondKbPath }
    return @(
        [pscustomobject]@{ name = 'two-gateways-one-kb'; targets = @($KbPath); prerequisites = @('sdk', 'license', 'kb') }
        [pscustomobject]@{ name = 'one-gateway-two-kbs'; targets = $twoKbTargets; prerequisites = @('sdk', 'license', 'kb', 'second-kb') }
        [pscustomobject]@{ name = 'cleanup'; targets = @($KbPath); prerequisites = @('sdk', 'license', 'kb') }
    )
}

function Invoke-SharedHostCheck {
    # Order: KB guard, then prerequisites, then the body. Only 'passed' passes; an
    # 'unavailable' body result must name a prerequisite; anything else is failed.
    param(
        [string]$Name,
        [string[]]$TargetKbPaths,
        [string[]]$AllowedKbPaths,
        [object[]]$Prerequisites,
        [string[]]$RequiredPrerequisites,
        [scriptblock]$Body
    )
    $field = {
        param($Object, $Key)
        if ($null -eq $Object) { return $null }
        if ($Object -is [System.Collections.IDictionary]) {
            if ($Object.Contains($Key)) { return $Object[$Key] }
            return $null
        }
        $property = $Object.PSObject.Properties[$Key]
        if ($null -eq $property) { return $null }
        return $property.Value
    }
    $new = {
        param($Status, $Detail, $Missing, $Rejected)
        [pscustomobject][ordered]@{
            name = $Name
            status = $Status
            detail = $Detail
            missingPrerequisites = @($Missing)
            rejectedPath = $Rejected
        }
    }
    foreach ($target in @($TargetKbPaths)) {
        $refusal = Get-SharedHostKbRefusal -Path $target -AllowedKbPaths $AllowedKbPaths
        if ($null -ne $refusal) {
            return (& $new 'failed' "Refused KB path outside the configured disposable KBs: $refusal" @() $refusal)
        }
    }
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($required in @($RequiredPrerequisites)) {
        $found = $false
        foreach ($item in @($Prerequisites)) {
            if ([string](& $field $item 'name') -eq $required -and (& $field $item 'available') -eq $true) { $found = $true }
        }
        if (-not $found) { $missing.Add($required) }
    }
    if ($missing.Count -gt 0) {
        return (& $new 'unavailable' ('Missing prerequisite(s): ' + ($missing -join ', ')) $missing.ToArray() $null)
    }
    $outcome = $null
    try {
        $outcome = & $Body
    }
    catch {
        return (& $new 'failed' ('Check threw: ' + $_.Exception.Message) @() $null)
    }
    if ($outcome -is [array]) {
        if ($outcome.Count -eq 0) { $outcome = $null } else { $outcome = $outcome[$outcome.Count - 1] }
    }
    $status = [string](& $field $outcome 'status')
    $detail = [string](& $field $outcome 'detail')
    if ($status -eq 'passed') { return (& $new 'passed' $detail @() $null) }
    if ($status -eq 'unavailable') {
        $named = @(@(& $field $outcome 'missingPrerequisites') | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })
        if ($named.Count -gt 0) { return (& $new 'unavailable' $detail $named $null) }
        return (& $new 'failed' ("Check reported unavailable without naming a prerequisite: $detail") @() $null)
    }
    if ($null -eq $outcome) { return (& $new 'failed' 'Check returned no result.' @() $null) }
    return (& $new 'failed' ("Check reported '$status': $detail") @() $null)
}

function Get-SharedHostCleanupWaitSeconds {
    param([int]$WorkerIdleTimeoutMinutes)
    return [int]($WorkerIdleTimeoutMinutes * 60 + 60)
}

function New-SharedHostCleanupResult {
    param([object]$WorkerProcesses, [object]$WorkerHostProcesses, [object]$RegistryRecords, [object]$WaitedSeconds)
    $counts = @($WorkerProcesses, $WorkerHostProcesses, $RegistryRecords)
    $status = 'passed'
    if (@($counts | Where-Object { $null -ne $_ -and [int]$_ -gt 0 }).Count -gt 0) {
        $status = 'failed'
    }
    elseif (@($counts | Where-Object { $null -eq $_ }).Count -gt 0) {
        $status = 'unavailable'
    }
    return [pscustomobject][ordered]@{
        workerProcesses = $WorkerProcesses
        workerHostProcesses = $WorkerHostProcesses
        registryRecords = $RegistryRecords
        waitedSeconds = $WaitedSeconds
        status = $status
    }
}

function Get-SharedHostGateStatus {
    param([object[]]$Checks, $Cleanup)
    $field = {
        param($Object, $Key)
        if ($null -eq $Object) { return $null }
        if ($Object -is [System.Collections.IDictionary]) {
            if ($Object.Contains($Key)) { return $Object[$Key] }
            return $null
        }
        $property = $Object.PSObject.Properties[$Key]
        if ($null -eq $property) { return $null }
        return $property.Value
    }
    $statuses = @(@($Checks) | Where-Object { $null -ne $_ } | ForEach-Object { [string](& $field $_ 'status') })
    $cleanupStatus = [string](& $field $Cleanup 'status')
    if ($null -eq $Cleanup) { $cleanupStatus = 'unavailable' }
    foreach ($status in @($statuses + $cleanupStatus)) {
        if ($status -ne 'passed' -and $status -ne 'unavailable') { return 'failed' }
    }
    if ($statuses.Count -gt 0 -and @($statuses | Where-Object { $_ -ne 'passed' }).Count -eq 0 -and $cleanupStatus -eq 'passed') {
        return 'passed'
    }
    return 'unavailable'
}

function Get-SharedHostExitCode {
    param([string]$Status)
    switch ($Status) {
        'passed' { return 0 }
        'unavailable' { return 2 }
        default { return 1 }
    }
}

function Get-SharedHostDefaultReportPath {
    param([string]$Root, [datetime]$DateUtc)
    if ($DateUtc.Kind -eq [DateTimeKind]::Local) { $DateUtc = $DateUtc.ToUniversalTime() }
    $stamp = $DateUtc.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    return (Join-Path $Root ('docs\benchmarks\' + $stamp + '-shared-host-concurrency.md'))
}

function New-SharedHostRedaction {
    # Ordered (value, placeholder) pairs; the local identity is captured here so a
    # report never depends on what the environment holds when it is written.
    param([string[]]$KbPaths, [string]$GxPath, [string]$ProfileConfigPath)
    $pairs = New-Object System.Collections.Generic.List[object]
    $add = {
        param($Value, $Placeholder)
        if ([string]::IsNullOrWhiteSpace($Value)) { return }
        $trimmed = ([string]$Value).Trim()
        if ($trimmed.Length -gt 3) { $trimmed = $trimmed.TrimEnd('\', '/') }
        if ($trimmed.Length -lt 2) { return }
        $pairs.Add([pscustomobject]@{ value = $trimmed; placeholder = $Placeholder })
    }
    $index = 0
    foreach ($kb in @($KbPaths)) {
        if ([string]::IsNullOrWhiteSpace($kb)) { continue }
        $index++
        & $add $kb "<kb-$index>"
        try { & $add ([IO.Path]::GetFullPath($kb)) "<kb-$index>" } catch { }
    }
    & $add $GxPath '<gx-install>'
    & $add $ProfileConfigPath '<profile-config>'
    & $add ([Environment]::GetEnvironmentVariable('USERPROFILE')) '<user-profile>'
    & $add ([Environment]::GetEnvironmentVariable('USERNAME')) '<user>'
    & $add ([Environment]::GetEnvironmentVariable('COMPUTERNAME')) '<machine>'
    return [pscustomobject]@{ pairs = $pairs.ToArray() }
}

function Protect-SharedHostText {
    # One case-insensitive pass, longest value first, so a placeholder is never
    # rewritten and a path is replaced before a shorter path it contains. Each value
    # is also matched in its JSON-escaped form (backslashes doubled).
    param([string]$Text, $Redaction)
    if ([string]::IsNullOrEmpty($Text) -or $null -eq $Redaction) { return $Text }
    $map = @{}
    $needles = New-Object System.Collections.Generic.List[string]
    foreach ($pair in @($Redaction.pairs)) {
        $value = [string]$pair.value
        if ([string]::IsNullOrEmpty($value)) { continue }
        foreach ($form in @($value, $value.Replace('\', '\\'))) {
            $key = $form.ToLowerInvariant()
            if (-not $map.ContainsKey($key)) {
                $map[$key] = [string]$pair.placeholder
                $needles.Add($form)
            }
        }
    }
    if ($needles.Count -eq 0) { return $Text }
    $ordered = @($needles | Sort-Object -Property Length -Descending)
    $pattern = (@($ordered | ForEach-Object { [regex]::Escape($_) }) -join '|')
    $options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    $regex = New-Object System.Text.RegularExpressions.Regex -ArgumentList $pattern, $options
    $builder = New-Object System.Text.StringBuilder
    $position = 0
    foreach ($match in $regex.Matches($Text)) {
        [void]$builder.Append($Text, $position, $match.Index - $position)
        [void]$builder.Append($map[$match.Value.ToLowerInvariant()])
        $position = $match.Index + $match.Length
    }
    [void]$builder.Append($Text, $position, $Text.Length - $position)
    return $builder.ToString()
}

function New-SharedHostReport {
    param(
        [datetime]$RunDateUtc,
        [object]$GeneXusVersion,
        [object]$RepositoryCommit,
        [object[]]$GatewayPids,
        [object[]]$WorkerHostPids,
        [object[]]$WorkerPids,
        [object[]]$QueueWaitSamplesMs,
        [object]$MinAvailablePhysicalMemoryMb,
        [object]$MaxWorkerWorkingSetMb,
        [object[]]$Checks,
        $Cleanup
    )
    $field = {
        param($Object, $Key)
        if ($null -eq $Object) { return $null }
        if ($Object -is [System.Collections.IDictionary]) {
            if ($Object.Contains($Key)) { return $Object[$Key] }
            return $null
        }
        $property = $Object.PSObject.Properties[$Key]
        if ($null -eq $property) { return $null }
        return $property.Value
    }
    $pidList = {
        param($Values)
        return , [object[]]@(@($Values) | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_ } | Sort-Object -Unique)
    }
    if ($RunDateUtc.Kind -eq [DateTimeKind]::Local) { $RunDateUtc = $RunDateUtc.ToUniversalTime() }
    $samples = @(@($QueueWaitSamplesMs) | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    $minimum = $null
    $median = $null
    $maximum = $null
    if ($samples.Count -gt 0) {
        $minimum = $samples[0]
        $maximum = $samples[$samples.Count - 1]
        $middle = [int][Math]::Floor($samples.Count / 2)
        if ($samples.Count % 2 -eq 1) { $median = $samples[$middle] } else { $median = ($samples[$middle - 1] + $samples[$middle]) / 2 }
    }
    $checkCopies = @(@($Checks) | Where-Object { $null -ne $_ } | ForEach-Object {
            [pscustomobject][ordered]@{
                name = [string](& $field $_ 'name')
                status = [string](& $field $_ 'status')
                detail = [string](& $field $_ 'detail')
                missingPrerequisites = [object[]]@(@(& $field $_ 'missingPrerequisites') | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
                rejectedPath = (& $field $_ 'rejectedPath')
            }
        })
    if ($null -eq $Cleanup) {
        $Cleanup = New-SharedHostCleanupResult -WorkerProcesses $null -WorkerHostProcesses $null -RegistryRecords $null -WaitedSeconds 0
    }
    $cleanupCopy = [pscustomobject][ordered]@{
        workerProcesses = (& $field $Cleanup 'workerProcesses')
        workerHostProcesses = (& $field $Cleanup 'workerHostProcesses')
        registryRecords = (& $field $Cleanup 'registryRecords')
        waitedSeconds = (& $field $Cleanup 'waitedSeconds')
        status = [string](& $field $Cleanup 'status')
    }
    $status = Get-SharedHostGateStatus -Checks $checkCopies -Cleanup $cleanupCopy
    $version = $null
    if (-not [string]::IsNullOrWhiteSpace([string]$GeneXusVersion)) { $version = [string]$GeneXusVersion }
    $commit = $null
    if (-not [string]::IsNullOrWhiteSpace([string]$RepositoryCommit)) { $commit = [string]$RepositoryCommit }
    return [pscustomobject][ordered]@{
        schemaVersion = 'gxmcp-live-shared-host/1'
        runDate = $RunDateUtc.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        genexusVersion = $version
        repositoryCommit = $commit
        pids = [pscustomobject][ordered]@{
            gateway = (& $pidList $GatewayPids)
            workerHost = (& $pidList $WorkerHostPids)
            worker = (& $pidList $WorkerPids)
        }
        queueWaitMs = [pscustomobject][ordered]@{ min = $minimum; median = $median; max = $maximum; samples = $samples.Count }
        minAvailablePhysicalMemoryMb = $MinAvailablePhysicalMemoryMb
        maxWorkerWorkingSetMb = $MaxWorkerWorkingSetMb
        checks = [object[]]$checkCopies
        cleanup = $cleanupCopy
        status = $status
        allPassed = [bool]($status -eq 'passed')
    }
}

function ConvertTo-SharedHostReportMarkdown {
    param($Report)
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $format = {
        param($Value)
        if ($null -eq $Value) { return 'unavailable' }
        if ($Value -is [string]) {
            if ($Value.Length -eq 0) { return 'none' }
            return $Value
        }
        if ($Value -is [System.Collections.IEnumerable]) {
            $parts = @(@($Value) | Where-Object { $null -ne $_ } | ForEach-Object { & $format $_ })
            if ($parts.Count -eq 0) { return 'none' }
            return ($parts -join ', ')
        }
        if ($Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) { return ([double]$Value).ToString('0.##', $invariant) }
        return [string]::Format($invariant, '{0}', $Value)
    }
    $cell = {
        param($Value)
        $text = & $format $Value
        return ($text -replace '\r?\n', ' ').Replace('|', '\|')
    }
    $wait = $Report.queueWaitMs
    $waitText = 'no queued SDK_Operation reported a queue wait (0 samples)'
    if ([int]$wait.samples -gt 0) {
        $waitText = [string]::Format($invariant, 'min {0}, median {1}, max {2} ({3} samples)',
            (& $format $wait.min), (& $format $wait.median), (& $format $wait.max), [int]$wait.samples)
    }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# Shared-host concurrency live validation')
    $lines.Add('')
    $lines.Add('Generated by `scripts/test-live-shared-host.ps1` (schema `' + $Report.schemaVersion + '`). Paths and the local identity are redacted to placeholders.')
    $lines.Add('')
    $lines.Add('| Field | Value |')
    $lines.Add('|---|---|')
    $lines.Add('| Run date | ' + (& $cell $Report.runDate) + ' |')
    $lines.Add('| GeneXus version | ' + (& $cell $Report.genexusVersion) + ' |')
    $lines.Add('| Repository commit | ' + (& $cell $Report.repositoryCommit) + ' |')
    $lines.Add('| Gateway PIDs | ' + (& $cell $Report.pids.gateway) + ' |')
    $lines.Add('| Worker_Host PIDs | ' + (& $cell $Report.pids.workerHost) + ' |')
    $lines.Add('| Worker PIDs | ' + (& $cell $Report.pids.worker) + ' |')
    $lines.Add('| Queue wait (ms) | ' + (& $cell $waitText) + ' |')
    $lines.Add('| Minimum available physical memory (MB) | ' + (& $cell $Report.minAvailablePhysicalMemoryMb) + ' |')
    $lines.Add('| Maximum Worker working set (MB) | ' + (& $cell $Report.maxWorkerWorkingSetMb) + ' |')
    $lines.Add('| Gate | ' + (& $cell $Report.status) + ' |')
    $lines.Add('')
    $lines.Add('## Checks')
    $lines.Add('')
    $lines.Add('| Check | Result | Detail | Missing prerequisites | Rejected path |')
    $lines.Add('|---|---|---|---|---|')
    foreach ($check in @($Report.checks)) {
        $rejected = '-'
        if ($null -ne $check.rejectedPath) { $rejected = & $cell $check.rejectedPath }
        $missing = '-'
        if (@($check.missingPrerequisites).Count -gt 0) { $missing = & $cell $check.missingPrerequisites }
        $lines.Add('| ' + $check.name + ' | ' + $check.status + ' | ' + (& $cell $check.detail) + ' | ' + $missing + ' | ' + $rejected + ' |')
    }
    $lines.Add('')
    $lines.Add('## Cleanup')
    $lines.Add('')
    $lines.Add('| Field | Value |')
    $lines.Add('|---|---|')
    $lines.Add('| Worker processes remaining | ' + (& $cell $Report.cleanup.workerProcesses) + ' |')
    $lines.Add('| Worker_Host processes remaining | ' + (& $cell $Report.cleanup.workerHostProcesses) + ' |')
    $lines.Add('| Ownership_Registry records remaining | ' + (& $cell $Report.cleanup.registryRecords) + ' |')
    $lines.Add('| Waited after the last Gateway exit (s) | ' + (& $cell $Report.cleanup.waitedSeconds) + ' |')
    $lines.Add('| Cleanup result | ' + (& $cell $Report.cleanup.status) + ' |')
    $lines.Add('')
    return (($lines.ToArray()) -join "`n")
}

function Write-SharedHostReport {
    param($Report, [string]$ReportPath, [string]$SummaryPath, $Redaction)
    $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
    $outputs = @()
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $outputs += , @($ReportPath, (Protect-SharedHostText -Text (ConvertTo-SharedHostReportMarkdown -Report $Report) -Redaction $Redaction))
    }
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        $outputs += , @($SummaryPath, (Protect-SharedHostText -Text ($Report | ConvertTo-Json -Depth 8) -Redaction $Redaction))
    }
    foreach ($output in $outputs) {
        $path = [IO.Path]::GetFullPath([string]$output[0])
        $parent = Split-Path -Parent $path
        if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $text = ([string]$output[1]) -replace "`r`n", "`n"
        if (-not $text.EndsWith("`n")) { $text += "`n" }
        [IO.File]::WriteAllText($path, $text, $utf8)
    }
}

# --- Live run helpers (not part of the pure contract) -------------------------

# Server.WorkerIdleTimeoutMinutes written to the temporary config; the cleanup
# check waits this value plus 60 seconds after the last Gateway exits.
$WorkerIdleTimeoutMinutes = 2
$RpcTimeoutSeconds = 240
$IndexReadySeconds = 180
$GatewayExitSeconds = 30
# Structural and shared-host overrides a strict config rejects or that would make
# the run depend on the caller's environment.
$ChildEnvironmentRemovals = @(
    'GXMCP_TEST_KB', 'GXMCP_TEST_KB2', 'GX_MCP_PORT', 'GX_CONFIG_PATH', 'GXMCP_LOG_DIR',
    'GXMCP_SHARED_GATEWAY', 'GX_MCP_SHARED_GATEWAY', 'GXMCP_PROFILE', 'GXMCP_NO_STRUCTURED_CONTENT',
    'GXMCP_EMIT_STRUCTURED_CONTENT', 'GXMCP_TERSE', 'GXMCP_ENGINE_BACKEND', 'GXMCP_ENGINE_FALLBACK',
    'GXMCP_ENGINE_EXE', 'GXMCP_ENGINE_TIMEOUT_SECONDS', 'GXMCP_BUSY_WAIT_MS', 'GXMCP_SHARED_HOST',
    'GXMCP_SHARED_QUEUE_WAIT_MS', 'GXMCP_SHARED_QUEUE_DEPTH', 'GXMCP_SHARED_SESSION_QUEUE_CAP',
    'GXMCP_SHARED_MAX_WORKERS', 'GXMCP_SHARED_MEMORY_FLOOR_MB', 'GXMCP_SHARED_COLD_START_CONCURRENCY',
    'GXMCP_SHARED_ATTACH_READY_TIMEOUT_S', 'GXMCP_LIVE_SUMMARY_PATH'
)

function Get-LiveField($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-LivePath($Object, [string[]]$Names) {
    $current = $Object
    foreach ($name in $Names) {
        if ($current -is [System.Collections.IList] -and $name -match '^\d+$') {
            $index = [int]$name
            if ($index -ge $current.Count) { return $null }
            $current = $current[$index]
        }
        else {
            $current = Get-LiveField $current $name
        }
        if ($null -eq $current) { return $null }
    }
    return $current
}

function ConvertTo-LiveNormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    try { return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') } catch { return $Path.TrimEnd('\', '/') }
}

function Write-LiveStep([string]$Message) {
    $text = Protect-SharedHostText -Text $Message -Redaction $script:redaction
    Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $text) -ForegroundColor DarkCyan
}

function Get-LiveProcessStartTicks([int]$ProcessId) {
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        try { return $process.StartTime.ToUniversalTime().Ticks } finally { $process.Dispose() }
    }
    catch { return $null }
}

function Test-LiveIdentityAlive($Identity) {
    if ($null -eq $Identity -or [int]$Identity.pid -le 0) { return $false }
    $ticks = Get-LiveProcessStartTicks ([int]$Identity.pid)
    if ($null -eq $ticks) { return $false }
    if ([long]$Identity.startTicks -le 0) { return $false }
    # Same process when the start time matches within one second (PID reuse guard).
    return ([Math]::Abs([long]$ticks - [long]$Identity.startTicks) -lt [TimeSpan]::TicksPerSecond)
}

function Add-LiveIdentity([string]$Role, [int]$ProcessId, [long]$StartTicks, [string]$KbPath) {
    if ($ProcessId -le 0) { return }
    if ($StartTicks -le 0) {
        $observed = Get-LiveProcessStartTicks $ProcessId
        if ($null -eq $observed) { return }
        $StartTicks = [long]$observed
    }
    foreach ($existing in $script:run.identities) {
        if ($existing.role -eq $Role -and $existing.pid -eq $ProcessId -and [Math]::Abs($existing.startTicks - $StartTicks) -lt [TimeSpan]::TicksPerSecond) {
            if (-not [string]::IsNullOrWhiteSpace($KbPath)) { $existing.kbPaths[$KbPath.ToLowerInvariant()] = $true }
            return
        }
    }
    $kbPaths = @{}
    if (-not [string]::IsNullOrWhiteSpace($KbPath)) { $kbPaths[$KbPath.ToLowerInvariant()] = $true }
    $script:run.identities.Add([pscustomobject]@{ role = $Role; pid = $ProcessId; startTicks = $StartTicks; kbPaths = $kbPaths })
}

function Get-LiveRegistryDirectory {
    # Registry v2: %LOCALAPPDATA%\GxMcp\shared-workers\v2\<kbKey>.json (docs/worker-ownership.md).
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) { return $null }
    return [IO.Path]::Combine($localAppData, 'GxMcp', 'shared-workers', 'v2')
}

function Get-LiveRegistryRecords([string[]]$KbPaths) {
    $records = New-Object System.Collections.Generic.List[object]
    $directory = Get-LiveRegistryDirectory
    if ($null -eq $directory -or -not (Test-Path -LiteralPath $directory -PathType Container)) { return $records.ToArray() }
    $wanted = @($KbPaths | ForEach-Object { ConvertTo-LiveNormalizedPath $_ })
    foreach ($file in @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
        $record = $null
        try { $record = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable } catch { continue }
        $kb = ConvertTo-LiveNormalizedPath ([string](Get-LivePath $record @('identity', 'kbPath')))
        foreach ($candidate in $wanted) {
            if ($kb -ieq $candidate) {
                $records.Add([pscustomobject]@{
                        kbPath = $candidate
                        hostPid = [int](Get-LiveField $record 'hostPid')
                        hostStartTicks = [long](Get-LiveField $record 'hostStartTimeUtcTicks')
                        workerPid = [int](Get-LiveField $record 'workerPid')
                        workerStartTicks = [long](Get-LiveField $record 'workerStartTimeUtcTicks')
                        state = [string](Get-LiveField $record 'state')
                    })
            }
        }
    }
    return $records.ToArray()
}

function Update-LiveSamples([switch]$Force) {
    $now = [datetime]::UtcNow
    if (-not $Force -and ($now - $script:run.lastSampleUtc).TotalMilliseconds -lt 1000) { return }
    $script:run.lastSampleUtc = $now
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $freeMb = [math]::Round([double]$os.FreePhysicalMemory / 1024, 0)
        if ($null -eq $script:run.minFreeMb -or $freeMb -lt $script:run.minFreeMb) { $script:run.minFreeMb = $freeMb }
    }
    catch { }
    $records = Get-LiveRegistryRecords $script:allowed
    foreach ($record in @($records)) {
        Add-LiveIdentity 'host' $record.hostPid $record.hostStartTicks $record.kbPath
        if ($record.workerPid -gt 0) { Add-LiveIdentity 'worker' $record.workerPid $record.workerStartTicks $record.kbPath }
    }
    foreach ($identity in @($script:run.identities | Where-Object { $_.role -eq 'worker' })) {
        if (-not (Test-LiveIdentityAlive $identity)) { continue }
        try {
            $process = Get-Process -Id $identity.pid -ErrorAction Stop
            $workingSetMb = [math]::Round($process.WorkingSet64 / 1MB, 0)
            $process.Dispose()
            if ($null -eq $script:run.maxWorkingSetMb -or $workingSetMb -gt $script:run.maxWorkingSetMb) { $script:run.maxWorkingSetMb = $workingSetMb }
        }
        catch { }
    }
}

function Initialize-LiveRun {
    # Builds (unless -SkipBuild) and writes the temporary shared-host config once.
    # Returns $null when ready, otherwise the failure detail.
    if ($script:run.initialized) { return $script:run.initError }
    $script:run.initialized = $true
    try {
        if (-not $SkipBuild) {
            Write-LiveStep 'Building the current Gateway/Worker artifact'
            & pwsh -NoProfile -File (Join-Path $root 'build.ps1')
            if ($LASTEXITCODE -ne 0) { throw "Artifact build failed with exit code $LASTEXITCODE." }
        }
        $gatewayExe = Join-Path $root 'publish\GxMcp.Gateway.exe'
        $workerExe = Join-Path $root 'publish\worker\GxMcp.Worker.exe'
        foreach ($required in @($gatewayExe, $workerExe, (Join-Path $root 'publish\tool_definitions.json'))) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published artifact is missing $(Split-Path -Leaf $required); run without -SkipBuild." }
        }
        if ((Get-FileHash -LiteralPath (Join-Path $root 'src\GxMcp.Gateway\tool_definitions.json') -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $root 'publish\tool_definitions.json') -Algorithm SHA256).Hash) {
            throw 'Published tool schema is stale; run without -SkipBuild.'
        }
        New-Item -ItemType Directory -Path $script:runDirectory -Force | Out-Null
        $config = [ordered]@{
            ConfigSchemaVersion = 2
            GatewayMode = 'stdio-isolated'
            GeneXus = [ordered]@{ InstallationPath = $GxPath; WorkerExecutable = $workerExe }
            Server = [ordered]@{
                HttpPort = 0
                McpStdio = $true
                BindAddress = '127.0.0.1'
                WorkerSharingMode = 'shared-host'
                WorkerIdleTimeoutMinutes = $WorkerIdleTimeoutMinutes
            }
            # The script opens each KB explicitly; declaring a KB would make strict
            # session resolution depend on the config instead of the request.
            Environment = [ordered]@{ ResolutionPolicy = 'strict' }
            # SDK reads reach the broker queue; a native gx-engine read would bypass it.
            Engine = [ordered]@{ Backend = 'sdk' }
        }
        $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
        [IO.File]::WriteAllText($script:configPath, ($config | ConvertTo-Json -Depth 5), $utf8)
        $script:run.gatewayExe = $gatewayExe
        return $null
    }
    catch {
        $script:run.initError = $_.Exception.Message
        return $script:run.initError
    }
}

function Start-LiveGateway([string]$Name) {
    $logDirectory = Join-Path $script:runDirectory ('gateway-' + $Name)
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $script:run.gatewayExe
    $start.WorkingDirectory = Split-Path -Parent $script:run.gatewayExe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($key in $ChildEnvironmentRemovals) { [void]$start.Environment.Remove($key) }
    $start.Environment['GX_CONFIG_PATH'] = $script:configPath
    $start.Environment['GXMCP_LOG_DIR'] = $logDirectory
    $start.Environment['GX_PATH'] = $GxPath
    $start.Environment['GX_PROGRAM_DIR'] = $GxPath
    $start.Environment['GX_MCP_STDIO'] = 'true'
    $process = [System.Diagnostics.Process]::Start($start)
    $gateway = [pscustomobject]@{
        name = $Name
        process = $process
        processId = $process.Id
        startTicks = $process.StartTime.ToUniversalTime().Ticks
        nextId = 1
        responses = @{}
        readTask = $process.StandardOutput.ReadLineAsync()
        errorTask = $process.StandardError.ReadLineAsync()
        eof = $false
        errorEof = $false
        stopped = $false
    }
    $script:run.gatewayPids.Add($process.Id)
    $script:run.gateways.Add($gateway)
    return $gateway
}

function Update-LiveGateway($Gateway) {
    while (-not $Gateway.eof -and $Gateway.readTask.IsCompleted) {
        $line = $null
        try { $line = $Gateway.readTask.Result } catch { $Gateway.eof = $true; break }
        if ($null -eq $line) { $Gateway.eof = $true; break }
        $Gateway.readTask = $Gateway.process.StandardOutput.ReadLineAsync()
        $message = $null
        try { $message = $line | ConvertFrom-Json -AsHashtable } catch { continue }
        $id = Get-LiveField $message 'id'
        if ($null -ne $id -and ($message.Contains('result') -or $message.Contains('error'))) {
            $Gateway.responses[[string]$id] = [pscustomobject]@{ message = $message; receivedUtc = [datetime]::UtcNow }
        }
    }
    # Drain stderr so a chatty Gateway never blocks on a full pipe; the isolated
    # GXMCP_LOG_DIR keeps the diagnostic log.
    while (-not $Gateway.errorEof -and $Gateway.errorTask.IsCompleted) {
        $line = $null
        try { $line = $Gateway.errorTask.Result } catch { $Gateway.errorEof = $true; break }
        if ($null -eq $line) { $Gateway.errorEof = $true; break }
        $Gateway.errorTask = $Gateway.process.StandardError.ReadLineAsync()
    }
}

function Send-LiveRpc($Gateway, [string]$Method, $Params, [switch]$Notification) {
    $envelope = [ordered]@{ jsonrpc = '2.0' }
    $id = $null
    if (-not $Notification) {
        $id = $Gateway.nextId
        $Gateway.nextId++
        $envelope.id = $id
    }
    $envelope.method = $Method
    $envelope.params = $Params
    $Gateway.process.StandardInput.WriteLine(($envelope | ConvertTo-Json -Depth 20 -Compress))
    $Gateway.process.StandardInput.Flush()
    return $id
}

function Receive-LiveResponse($Gateway, $Id, [int]$TimeoutSeconds) {
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        Update-LiveGateway $Gateway
        $key = [string]$Id
        if ($Gateway.responses.ContainsKey($key)) {
            $response = $Gateway.responses[$key]
            $Gateway.responses.Remove($key)
            return $response.message
        }
        if ($Gateway.eof) { throw "Gateway $($Gateway.name) closed stdout before answering request $Id." }
        Update-LiveSamples
        Start-Sleep -Milliseconds 25
    }
    throw "Gateway $($Gateway.name) did not answer request $Id within $TimeoutSeconds seconds."
}

function Get-LiveToolOutcome($Message) {
    $payload = $null
    $text = [string](Get-LivePath $Message @('result', 'content', '0', 'text'))
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $payload = $text | ConvertFrom-Json -AsHashtable } catch { $payload = $null }
    }
    $isError = ($null -ne (Get-LiveField $Message 'error')) -or ((Get-LivePath $Message @('result', 'isError')) -eq $true)
    $code = Get-LiveField $payload 'code'
    if ($null -eq $code) { $code = Get-LivePath $payload @('error', 'code') }
    if ($null -eq $code) { $code = Get-LivePath $Message @('error', 'code') }
    if ($null -ne $payload -and $null -ne (Get-LiveField $payload 'error')) { $isError = $true }
    $shared = Get-LivePath $Message @('result', '_meta', 'sharedHost')
    if ($null -eq $shared) { $shared = Get-LivePath $payload @('_meta', 'sharedHost') }
    $workerPid = Get-LiveField $shared 'workerPid'
    if ($null -eq $workerPid) { $workerPid = Get-LiveField $payload 'workerPid' }
    $raw = $text + ' ' + [string](Get-LivePath $Message @('error', 'message'))
    return [pscustomobject]@{
        payload = $payload
        isError = [bool]$isError
        code = $code
        queueWaitMs = (Get-LiveField $shared 'queueWaitMs')
        workerPid = $workerPid
        licenseIssue = [bool]($isError -and $raw -match '(?i)licen[cs]')
    }
}

function Invoke-LiveTool($Gateway, [string]$Name, [hashtable]$Arguments, [int]$TimeoutSeconds = $RpcTimeoutSeconds) {
    $id = Send-LiveRpc $Gateway 'tools/call' ([ordered]@{ name = $Name; arguments = $Arguments })
    return (Get-LiveToolOutcome (Receive-LiveResponse $Gateway $id $TimeoutSeconds))
}

function Initialize-LiveGatewaySession($Gateway) {
    $id = Send-LiveRpc $Gateway 'initialize' ([ordered]@{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = [ordered]@{ name = 'test-live-shared-host'; version = '1' }
        })
    $response = Receive-LiveResponse $Gateway $id 60
    if ($null -eq (Get-LiveField $response 'result')) { throw "Gateway $($Gateway.name) initialize returned no result." }
    [void](Send-LiveRpc $Gateway 'notifications/initialized' @{} -Notification)
}

function Find-LiveObjectReference($Node, [int]$Depth = 0) {
    if ($null -eq $Node -or $Depth -gt 8) { return $null }
    if ($Node -is [System.Collections.IDictionary]) {
        $name = Get-LiveField $Node 'name'
        $type = Get-LiveField $Node 'type'
        if ($name -is [string] -and -not [string]::IsNullOrWhiteSpace($name) -and $null -ne $type -and $type -is [string]) {
            return [pscustomobject]@{ name = $name; type = $type }
        }
        foreach ($value in @($Node.Values)) {
            $found = Find-LiveObjectReference $value ($Depth + 1)
            if ($null -ne $found) { return $found }
        }
        return $null
    }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($value in $Node) {
            $found = Find-LiveObjectReference $value ($Depth + 1)
            if ($null -ne $found) { return $found }
        }
    }
    return $null
}

function Open-LiveKb($Gateway, [string]$Kb, [string]$Alias) {
    # Returns @{ alias; object } or @{ unavailable = <prerequisite>; detail } or @{ failed = <detail> }.
    $open = Invoke-LiveTool $Gateway 'genexus_kb' @{ action = 'open'; path = $Kb; alias = $Alias } 600
    if ($open.isError) {
        $prerequisite = 'kb'
        if ($open.licenseIssue) { $prerequisite = 'license' }
        return [pscustomobject]@{ unavailable = $prerequisite; detail = "Gateway $($Gateway.name) could not open $Kb (code $($open.code))." }
    }
    $resolved = [string](Get-LiveField $open.payload 'kbAlias')
    if ([string]::IsNullOrWhiteSpace($resolved)) { $resolved = [string](Get-LivePath $open.payload @('result', 'kbAlias')) }
    if ([string]::IsNullOrWhiteSpace($resolved)) { $resolved = $Alias }
    $deadline = [datetime]::UtcNow.AddSeconds($IndexReadySeconds)
    $listing = $null
    while ([datetime]::UtcNow -lt $deadline) {
        $listing = Invoke-LiveTool $Gateway 'genexus_list_objects' @{ kb = $resolved; limit = 5 }
        $code = [string]$listing.code
        $state = [string](Get-LiveField $listing.payload 'status')
        if ($code -ine 'IndexNotReady' -and $state -ine 'Indexing') { break }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $listing -or $listing.isError) {
        return [pscustomobject]@{ failed = "Gateway $($Gateway.name) could not list objects in $Kb (code $($listing.code))." }
    }
    $object = Find-LiveObjectReference $listing.payload
    if ($null -eq $object) { return [pscustomobject]@{ failed = "No object to read was listed in $Kb." } }
    return [pscustomobject]@{ alias = $resolved; object = $object }
}

function Invoke-LiveOperationLanes([object[]]$Lanes, [int]$Count) {
    # Each lane keeps one SDK_Operation outstanding and submits the next when the
    # previous one is answered; all lanes start on the same tick, so lanes on
    # different Gateways (or KBs) overlap in time.
    $operations = New-Object System.Collections.Generic.List[object]
    foreach ($lane in $Lanes) {
        $lane | Add-Member -NotePropertyName remaining -NotePropertyValue $Count -Force
        $lane | Add-Member -NotePropertyName outstanding -NotePropertyValue $null -Force
        $lane | Add-Member -NotePropertyName sentUtc -NotePropertyValue ([datetime]::MinValue) -Force
        $lane | Add-Member -NotePropertyName aborted -NotePropertyValue $false -Force
    }
    while (@($Lanes | Where-Object { -not $_.aborted -and ($_.remaining -gt 0 -or $null -ne $_.outstanding) }).Count -gt 0) {
        foreach ($gateway in @($Lanes | ForEach-Object { $_.gateway } | Sort-Object -Property processId -Unique)) { Update-LiveGateway $gateway }
        foreach ($lane in $Lanes) {
            if ($lane.aborted) { continue }
            if ($null -ne $lane.outstanding) {
                $key = [string]$lane.outstanding
                if ($lane.gateway.responses.ContainsKey($key)) {
                    $response = $lane.gateway.responses[$key]
                    $lane.gateway.responses.Remove($key)
                    $outcome = Get-LiveToolOutcome $response.message
                    $operations.Add([pscustomobject]@{
                            lane = $lane.name; gateway = $lane.gateway.name; kbPath = $lane.kbPath
                            startUtc = $lane.sentUtc; endUtc = $response.receivedUtc
                            ok = -not $outcome.isError; code = $outcome.code; licenseIssue = $outcome.licenseIssue
                            queueWaitMs = $outcome.queueWaitMs; workerPid = $outcome.workerPid
                        })
                    $lane.outstanding = $null
                }
                elseif ($lane.gateway.eof -or ([datetime]::UtcNow - $lane.sentUtc).TotalSeconds -gt $RpcTimeoutSeconds) {
                    $operations.Add([pscustomobject]@{
                            lane = $lane.name; gateway = $lane.gateway.name; kbPath = $lane.kbPath
                            startUtc = $lane.sentUtc; endUtc = [datetime]::UtcNow
                            ok = $false; code = 'NO_RESPONSE'; licenseIssue = $false; queueWaitMs = $null; workerPid = $null
                        })
                    $lane.outstanding = $null
                    $lane.aborted = $true
                    continue
                }
            }
            if ($null -eq $lane.outstanding -and $lane.remaining -gt 0) {
                $arguments = @{ name = $lane.object.name; kb = $lane.alias; refresh = $true }
                if (-not [string]::IsNullOrWhiteSpace($lane.object.type)) { $arguments.type = $lane.object.type }
                $lane.sentUtc = [datetime]::UtcNow
                $lane.outstanding = Send-LiveRpc $lane.gateway 'tools/call' ([ordered]@{ name = 'genexus_read'; arguments = $arguments })
                $lane.remaining--
            }
        }
        Update-LiveSamples
        Start-Sleep -Milliseconds 10
    }
    foreach ($operation in $operations) {
        if ($null -ne $operation.queueWaitMs) { $script:run.queueWaits.Add([double]$operation.queueWaitMs) }
        if ($null -ne $operation.workerPid) {
            $workerPid = 0
            if ([int]::TryParse([string]$operation.workerPid, [ref]$workerPid)) { Add-LiveIdentity 'worker' $workerPid 0 $operation.kbPath }
        }
    }
    return $operations.ToArray()
}

function Test-LiveOverlap([object[]]$First, [object[]]$Second) {
    foreach ($a in @($First)) {
        foreach ($b in @($Second)) {
            if ($a.startUtc -lt $b.endUtc -and $b.startUtc -lt $a.endUtc) { return $true }
        }
    }
    return $false
}

function Get-LiveOperationVerdict([object[]]$Operations, [string[]]$Lanes, [int]$Count) {
    # $null when every lane completed $Count successful operations, otherwise the outcome to return.
    $license = @($Operations | Where-Object { $_.licenseIssue })
    if ($license.Count -gt 0) {
        return @{ status = 'unavailable'; detail = 'An SDK_Operation was refused for a GeneXus license reason.'; missingPrerequisites = @('license') }
    }
    $summary = New-Object System.Collections.Generic.List[string]
    $failed = $false
    foreach ($lane in $Lanes) {
        $laneOperations = @($Operations | Where-Object { $_.lane -eq $lane })
        $succeeded = @($laneOperations | Where-Object { $_.ok }).Count
        $codes = @($laneOperations | Where-Object { -not $_.ok } | ForEach-Object { [string]$_.code } | Sort-Object -Unique)
        $text = "$lane $succeeded/$Count succeeded"
        if ($codes.Count -gt 0) { $text += ' (errors: ' + ($codes -join ', ') + ')' }
        $summary.Add($text)
        if ($succeeded -lt $Count) { $failed = $true }
    }
    if ($failed) { return @{ status = 'failed'; detail = ($summary -join '; ') } }
    return $null
}

function Stop-LiveGateway($Gateway) {
    if ($null -eq $Gateway -or $Gateway.stopped) { return }
    $Gateway.stopped = $true
    try { $Gateway.process.StandardInput.Close() } catch { }
    try {
        if (-not $Gateway.process.WaitForExit($GatewayExitSeconds * 1000)) {
            # Only this Gateway, identified by PID and start time; never its children,
            # because the shared Worker_Host must be left to its own idle reaping.
            $current = Get-LiveProcessStartTicks $Gateway.processId
            if ($null -ne $current -and [Math]::Abs([long]$current - [long]$Gateway.startTicks) -lt [TimeSpan]::TicksPerSecond) {
                $Gateway.process.Kill()
                [void]$Gateway.process.WaitForExit(5000)
            }
        }
    }
    catch { Write-Warning "Gateway $($Gateway.name) stop failed: $($_.Exception.Message)" }
    finally {
        $script:run.lastGatewayExitUtc = [datetime]::UtcNow
        try { $Gateway.process.Dispose() } catch { }
    }
}

function Get-LiveWorkerPidsFor([string]$KbPath, [object[]]$Operations) {
    $pids = @{}
    foreach ($operation in @($Operations | Where-Object { $_.kbPath -ieq $KbPath -and $null -ne $_.workerPid })) { $pids[[string]$operation.workerPid] = $true }
    foreach ($record in @(Get-LiveRegistryRecords @($KbPath))) { if ($record.workerPid -gt 0) { $pids[[string]$record.workerPid] = $true } }
    return @($pids.Keys)
}

$checkBodies = @{
    'two-gateways-one-kb' = {
        $initError = Initialize-LiveRun
        if ($null -ne $initError) { return @{ status = 'failed'; detail = $initError } }
        $kb = $script:allowed[0]
        $first = $null
        $second = $null
        try {
            $first = Start-LiveGateway 'one-kb-a'
            $second = Start-LiveGateway 'one-kb-b'
            Write-LiveStep "two-gateways-one-kb: Gateways $($first.processId) and $($second.processId) on $kb"
            Initialize-LiveGatewaySession $first
            Initialize-LiveGatewaySession $second
            $lanes = @()
            foreach ($pair in @(@($first, 'a'), @($second, 'b'))) {
                $opened = Open-LiveKb $pair[0] $kb ('live' + $pair[1])
                if ($null -ne (Get-LiveField $opened 'unavailable')) { return @{ status = 'unavailable'; detail = $opened.detail; missingPrerequisites = @($opened.unavailable) } }
                if ($null -ne (Get-LiveField $opened 'failed')) { return @{ status = 'failed'; detail = $opened.failed } }
                $lanes += [pscustomobject]@{ name = $pair[0].name; gateway = $pair[0]; alias = $opened.alias; object = $opened.object; kbPath = $kb }
            }
            $operations = Invoke-LiveOperationLanes $lanes $OperationsPerGateway
            Update-LiveSamples -Force
            $verdict = Get-LiveOperationVerdict $operations @('one-kb-a', 'one-kb-b') $OperationsPerGateway
            if ($null -ne $verdict) { return $verdict }
            if (-not (Test-LiveOverlap @($operations | Where-Object { $_.lane -eq 'one-kb-a' }) @($operations | Where-Object { $_.lane -eq 'one-kb-b' }))) {
                return @{ status = 'failed'; detail = 'No SDK_Operation of one Gateway overlapped in time with an operation of the other.' }
            }
            $records = @(Get-LiveRegistryRecords @($kb))
            $workers = @(Get-LiveWorkerPidsFor $kb $operations)
            if ($records.Count -ne 1 -or $records[0].hostPid -le 0) {
                return @{ status = 'failed'; detail = "Expected one Ownership_Registry record with a live Worker_Host for the KB; found $($records.Count)." }
            }
            if ($workers.Count -ne 1) {
                return @{ status = 'failed'; detail = "Expected both Gateways to share one Worker; observed Worker PIDs: $($workers -join ', ')." }
            }
            return @{ status = 'passed'; detail = "Two Gateways each completed $OperationsPerGateway overlapping SDK_Operations through one Worker_Host ($($records[0].hostPid)) and one Worker ($($workers[0]))." }
        }
        finally {
            Stop-LiveGateway $first
            Stop-LiveGateway $second
        }
    }
    'one-gateway-two-kbs' = {
        $initError = Initialize-LiveRun
        if ($null -ne $initError) { return @{ status = 'failed'; detail = $initError } }
        $kbs = @($script:allowed[0], $script:allowed[1])
        $gateway = $null
        try {
            $gateway = Start-LiveGateway 'two-kbs'
            Write-LiveStep "one-gateway-two-kbs: Gateway $($gateway.processId) on $($kbs[0]) and $($kbs[1])"
            Initialize-LiveGatewaySession $gateway
            $lanes = @()
            for ($index = 0; $index -lt 2; $index++) {
                $opened = Open-LiveKb $gateway $kbs[$index] ('livekb' + ($index + 1))
                if ($null -ne (Get-LiveField $opened 'unavailable')) {
                    $prerequisite = $opened.unavailable
                    if ($index -eq 1 -and $prerequisite -eq 'kb') { $prerequisite = 'second-kb' }
                    return @{ status = 'unavailable'; detail = $opened.detail; missingPrerequisites = @($prerequisite) }
                }
                if ($null -ne (Get-LiveField $opened 'failed')) { return @{ status = 'failed'; detail = $opened.failed } }
                $lanes += [pscustomobject]@{ name = 'kb-' + ($index + 1); gateway = $gateway; alias = $opened.alias; object = $opened.object; kbPath = $kbs[$index] }
            }
            $operations = Invoke-LiveOperationLanes $lanes $OperationsPerGateway
            Update-LiveSamples -Force
            $verdict = Get-LiveOperationVerdict $operations @('kb-1', 'kb-2') $OperationsPerGateway
            if ($null -ne $verdict) { return $verdict }
            if (-not (Test-LiveOverlap @($operations | Where-Object { $_.lane -eq 'kb-1' }) @($operations | Where-Object { $_.lane -eq 'kb-2' }))) {
                return @{ status = 'failed'; detail = 'No SDK_Operation on one KB overlapped in time with an operation on the other KB.' }
            }
            $firstRecords = @(Get-LiveRegistryRecords @($kbs[0]))
            $secondRecords = @(Get-LiveRegistryRecords @($kbs[1]))
            $firstWorkers = @(Get-LiveWorkerPidsFor $kbs[0] $operations)
            $secondWorkers = @(Get-LiveWorkerPidsFor $kbs[1] $operations)
            if ($firstRecords.Count -ne 1 -or $secondRecords.Count -ne 1 -or $firstRecords[0].hostPid -eq $secondRecords[0].hostPid) {
                return @{ status = 'failed'; detail = "Expected one Ownership_Registry record and a distinct Worker_Host per KB; found $($firstRecords.Count) and $($secondRecords.Count)." }
            }
            if ($firstWorkers.Count -ne 1 -or $secondWorkers.Count -ne 1 -or $firstWorkers[0] -eq $secondWorkers[0]) {
                return @{ status = 'failed'; detail = "Expected one distinct Worker per KB; observed $($firstWorkers -join ', ') and $($secondWorkers -join ', ')." }
            }
            return @{ status = 'passed'; detail = "One Gateway completed $OperationsPerGateway overlapping SDK_Operations on each KB through distinct Worker_Hosts ($($firstRecords[0].hostPid), $($secondRecords[0].hostPid)) and Workers ($($firstWorkers[0]), $($secondWorkers[0]))." }
        }
        finally {
            Stop-LiveGateway $gateway
        }
    }
    'cleanup' = {
        if ($script:run.gatewayPids.Count -eq 0 -or $null -eq $script:run.lastGatewayExitUtc) {
            return @{ status = 'failed'; detail = 'No Gateway ran, so there is no shared-host state to measure.' }
        }
        foreach ($gateway in $script:run.gateways) { Stop-LiveGateway $gateway }
        $waitSeconds = Get-SharedHostCleanupWaitSeconds -WorkerIdleTimeoutMinutes $WorkerIdleTimeoutMinutes
        $readyUtc = $script:run.lastGatewayExitUtc.AddSeconds($waitSeconds)
        Write-LiveStep "cleanup: waiting until Server.WorkerIdleTimeoutMinutes ($WorkerIdleTimeoutMinutes) plus 60 seconds after the last Gateway exit"
        while ([datetime]::UtcNow -lt $readyUtc) {
            Update-LiveSamples
            $remaining = ($readyUtc - [datetime]::UtcNow).TotalMilliseconds
            Start-Sleep -Milliseconds ([int][Math]::Max(1, [Math]::Min(5000, $remaining)))
        }
        $waited = [int][Math]::Round(([datetime]::UtcNow - $script:run.lastGatewayExitUtc).TotalSeconds)
        $workers = @($script:run.identities | Where-Object { $_.role -eq 'worker' -and (Test-LiveIdentityAlive $_) }).Count
        $hosts = @($script:run.identities | Where-Object { $_.role -eq 'host' -and (Test-LiveIdentityAlive $_) }).Count
        # An unknown registry location leaves the record count unmeasured, never zero.
        $recordCount = $null
        if ($null -ne (Get-LiveRegistryDirectory)) { $recordCount = @(Get-LiveRegistryRecords $script:allowed).Count }
        $script:cleanupResult = New-SharedHostCleanupResult -WorkerProcesses $workers -WorkerHostProcesses $hosts -RegistryRecords $recordCount -WaitedSeconds $waited
        $detail = "After $waited s: $workers Worker, $hosts Worker_Host process(es) and $recordCount Ownership_Registry record(s) remain."
        if ($script:cleanupResult.status -eq 'passed') { return @{ status = 'passed'; detail = $detail } }
        return @{ status = 'failed'; detail = $detail }
    }
}

# --- Entry point ----------------------------------------------------------------

$runStartedUtc = [datetime]::UtcNow
if ([string]::IsNullOrWhiteSpace($GxPath)) {
    try {
        . (Join-Path $root 'scripts\gx-version-catalog.ps1')
        $GxPath = Get-GxPrimaryInstallPath -Catalog (Get-GxVersionCatalog -Root $root)
    }
    catch { $GxPath = '' }
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Get-SharedHostDefaultReportPath -Root $root -DateUtc $runStartedUtc }
$runDirectory = Join-Path $root ('scratchpad\live-shared-host-' + [guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($SummaryPath)) { $SummaryPath = Join-Path $runDirectory 'summary.json' }
$configPath = Join-Path $runDirectory 'config.json'

$allowed = @(Get-SharedHostAllowedKbPaths -KbPath $KbPath -SecondKbPath $SecondKbPath)
$prerequisites = @(Get-SharedHostPrerequisites -GxPath $GxPath -KbPath $KbPath -SecondKbPath $SecondKbPath)
$plan = @(Get-SharedHostCheckPlan -KbPath $KbPath -SecondKbPath $SecondKbPath)
# Configured KBs outside the disposable rule are not redacted: their path is the
# refusal evidence the report must keep (Requirement 13 criterion 13).
$redaction = New-SharedHostRedaction -KbPaths $allowed -GxPath $GxPath -ProfileConfigPath $configPath
$cleanupResult = $null
$run = [pscustomobject]@{
    initialized = $false
    initError = $null
    gatewayExe = $null
    gateways = New-Object System.Collections.Generic.List[object]
    gatewayPids = New-Object System.Collections.Generic.List[int]
    identities = New-Object System.Collections.Generic.List[object]
    queueWaits = New-Object System.Collections.Generic.List[double]
    minFreeMb = $null
    maxWorkingSetMb = $null
    lastSampleUtc = [datetime]::MinValue
    lastGatewayExitUtc = $null
}

$results = New-Object System.Collections.Generic.List[object]
try {
    foreach ($check in $plan) {
        Write-LiveStep "Check $($check.name)"
        $result = Invoke-SharedHostCheck -Name $check.name -TargetKbPaths @($check.targets) -AllowedKbPaths $allowed `
            -Prerequisites $prerequisites -RequiredPrerequisites @($check.prerequisites) -Body $checkBodies[$check.name]
        $results.Add($result)
        Write-LiveStep "Check $($result.name): $($result.status) - $($result.detail)"
    }
}
finally {
    foreach ($gateway in $run.gateways) { Stop-LiveGateway $gateway }
}

$genexusVersion = $null
$anchor = $null
if (-not [string]::IsNullOrWhiteSpace($GxPath)) { $anchor = [IO.Path]::Combine($GxPath, 'Artech.Architecture.Common.dll') }
if ($null -ne $anchor -and (Test-Path -LiteralPath $anchor -PathType Leaf)) {
    try { $genexusVersion = (Get-Item -LiteralPath $anchor).VersionInfo.ProductVersion } catch { }
}
$repositoryCommit = $null
try {
    # No Select-Object -First here: stopping the pipeline early kills git and sets a non-zero exit code.
    $gitOutput = @(& git -C $root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $gitOutput.Count -gt 0) { $repositoryCommit = ([string]$gitOutput[0]).Trim() }
}
catch { $repositoryCommit = $null }

$report = New-SharedHostReport -RunDateUtc $runStartedUtc -GeneXusVersion $genexusVersion -RepositoryCommit $repositoryCommit `
    -GatewayPids @($run.gatewayPids) `
    -WorkerHostPids @($run.identities | Where-Object { $_.role -eq 'host' } | ForEach-Object { $_.pid }) `
    -WorkerPids @($run.identities | Where-Object { $_.role -eq 'worker' } | ForEach-Object { $_.pid }) `
    -QueueWaitSamplesMs @($run.queueWaits) -MinAvailablePhysicalMemoryMb $run.minFreeMb -MaxWorkerWorkingSetMb $run.maxWorkingSetMb `
    -Checks $results.ToArray() -Cleanup $cleanupResult

# After measuring, stop any recorded Worker or Worker_Host that outlived cleanup,
# so a failed run does not leave the KB held. PID and start time must both match.
foreach ($identity in $run.identities.ToArray()) {
    if (-not (Test-LiveIdentityAlive $identity)) { continue }
    try {
        $process = Get-Process -Id $identity.pid -ErrorAction Stop
        if ($process.ProcessName -like 'GxMcp.Worker*') {
            Write-Warning "Stopping leftover $($identity.role) process $($identity.pid) recorded by this run."
            $process.Kill()
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
    catch { Write-Warning "Leftover process cleanup failed: $($_.Exception.Message)" }
}

$status = $report.status
try {
    Write-SharedHostReport -Report $report -ReportPath $ReportPath -SummaryPath $SummaryPath -Redaction $redaction
    Write-Host ('Report: ' + (Protect-SharedHostText -Text $ReportPath -Redaction $redaction)) -ForegroundColor DarkGray
}
catch {
    [Console]::Out.WriteLine("live=failed; the report could not be written: $($_.Exception.Message)")
    exit 1
}
foreach ($result in $results) {
    Write-Host (Protect-SharedHostText -Text ("  {0}: {1}" -f $result.name, $result.status) -Redaction $redaction)
}
[Console]::Out.WriteLine("live=$status; shared-host gate $status (cleanup $($report.cleanup.status))")
exit (Get-SharedHostExitCode -Status $status)
