# Contract tests for scripts/test-live-shared-host.ps1, the shared-host live
# validation gate (concurrent-multi-session-workers, Requirement 13 criteria 10-14,
# tasks 7.9 and 7.10). These tests are written before the script exists and fail
# until task 7.10 implements it. They need no GeneXus SDK, license or KB: they
# extract the script's pure functions from its AST (as test-live.test.ps1 does)
# and run the real entry point only on fail-closed paths that stop before any
# Gateway starts.
#
# Requires PowerShell 7+ (the live entry points do; see AGENTS.md). Run with:
#   pwsh -NoProfile -File scripts/tests/test-live-shared-host.test.ps1
#
# KB guard decision: Requirement 13 criterion 13 names `C:\KBs\KBTeste` and a
# configured second disposable KB. The repository's portable disposable rule
# (AGENTS.md "Live Knowledge Bases", docs/agent-operations.md) is "a directory
# directly under C:\KBs whose name contains 'teste'", enforced by
# Resolve-TestKbPath in scripts/sdk-local-build.ps1. scripts/test-live.ps1 has no
# disposable-KB guard to reuse. The script therefore applies both rules: the
# allowlist is exactly the two configured KBs (-KbPath, default `C:\KBs\KBTeste`,
# and -SecondKbPath), each configured KB must also pass the repository rule, and
# any check whose target is not in that allowlist is refused and recorded as
# failed with the rejected path. Test-SharedHostDisposableKbPath must agree with
# Resolve-TestKbPath on every sample below.
#
# Pinned API of scripts/test-live-shared-host.ps1:
#
#   Parameters
#     -KbPath <string>             default 'C:\KBs\KBTeste'
#     -SecondKbPath <string>       default $env:GXMCP_TEST_KB2; empty makes
#                                  'one-gateway-two-kbs' unavailable ('second-kb')
#     -GxPath <string>             default $env:GX_PATH, then the catalog primary
#     -OperationsPerGateway <int>  ValidateRange(20, 1000), default 20
#     -ReportPath <string>         default docs/benchmarks/<yyyy-MM-dd>-shared-host-concurrency.md
#     -SummaryPath <string>        JSON summary; default under scratchpad/ (git-ignored)
#     -SkipBuild <switch>
#   The script refuses Windows PowerShell 5.1 (PSVersion.Major -lt 7).
#
#   Checks, in this order, each with status 'passed', 'failed' or 'unavailable':
#     two-gateways-one-kb   targets KbPath;               needs sdk, license, kb
#     one-gateway-two-kbs   targets KbPath, SecondKbPath; needs sdk, license, kb, second-kb
#     cleanup               targets KbPath;               needs sdk, license, kb
#   Order of evaluation per check: KB guard (failed + rejectedPath), then
#   prerequisites (unavailable + missingPrerequisites), then the check body.
#
#   Exit codes: 0 passed, 1 failed, 2 unavailable; the last status line printed
#   contains 'live=<status>'.
#
#   Pure functions (extracted by this test):
#     Test-SharedHostDisposableKbPath -Path                       -> [bool]
#     Get-SharedHostAllowedKbPaths -KbPath -SecondKbPath          -> [string[]] normalized, disposable only
#     Get-SharedHostKbRefusal -Path -AllowedKbPaths               -> $null, or the normalized rejected path
#     Get-SharedHostPrerequisites -GxPath -KbPath -SecondKbPath [-Probes @{ sdk; license; kb }]
#                                                                 -> items { name; available; detail }
#         names 'sdk', 'license', 'kb', 'second-kb'; a probe gets the path, returns
#         [bool]; a throw or a non-bool result is 'not available'; an empty
#         SecondKbPath is 'second-kb' not available without calling a probe.
#     Get-SharedHostCheckPlan -KbPath -SecondKbPath               -> items { name; targets; prerequisites }
#     Invoke-SharedHostCheck -Name -TargetKbPaths -AllowedKbPaths -Prerequisites -RequiredPrerequisites -Body
#                                                                 -> { name; status; detail; missingPrerequisites; rejectedPath }
#         Body returns @{ status; detail [; missingPrerequisites] }. Only 'passed'
#         passes; 'unavailable' must name a prerequisite; anything else or a
#         throw is 'failed'. Body never runs after a refusal or a missing prerequisite.
#     Get-SharedHostCleanupWaitSeconds -WorkerIdleTimeoutMinutes  -> minutes * 60 + 60
#     New-SharedHostCleanupResult -WorkerProcesses -WorkerHostProcesses -RegistryRecords -WaitedSeconds
#                                                                 -> { workerProcesses; workerHostProcesses; registryRecords; waitedSeconds; status }
#         status: 'passed' when all three are 0, 'failed' when any is above 0,
#         'unavailable' when any was not measured ($null).
#     Get-SharedHostGateStatus -Checks -Cleanup                   -> 'passed' | 'failed' | 'unavailable'
#         'passed' only when there is at least one check, every check is
#         'passed' and the cleanup status is 'passed'; any 'failed' (or unknown
#         status) gives 'failed'; otherwise 'unavailable'.
#     Get-SharedHostExitCode -Status                              -> 0 | 1 | 2
#     Get-SharedHostDefaultReportPath -Root -DateUtc              -> <Root>\docs\benchmarks\<yyyy-MM-dd>-shared-host-concurrency.md
#     New-SharedHostRedaction -KbPaths -GxPath -ProfileConfigPath -> redaction context; it also captures
#                                                                    USERPROFILE, USERNAME and COMPUTERNAME
#     Protect-SharedHostText -Text -Redaction                     -> text with placeholders, case-insensitive:
#         KbPaths[i] -> '<kb-{i+1}>', GxPath -> '<gx-install>', ProfileConfigPath ->
#         '<profile-config>', USERPROFILE -> '<user-profile>', USERNAME -> '<user>',
#         COMPUTERNAME -> '<machine>'. A longer path is replaced before a path it
#         contains. A rejected path is not a configured KB, so it keeps its text
#         apart from the profile, user and machine placeholders.
#     New-SharedHostReport -RunDateUtc -GeneXusVersion -RepositoryCommit -GatewayPids -WorkerHostPids -WorkerPids
#                          -QueueWaitSamplesMs -MinAvailablePhysicalMemoryMb -MaxWorkerWorkingSetMb -Checks -Cleanup
#                                                                 -> report (fields below)
#     ConvertTo-SharedHostReportMarkdown -Report                  -> [string]
#     Write-SharedHostReport -Report -ReportPath -SummaryPath -Redaction
#                                                                 -> writes redacted markdown and JSON
#
#   Report fields (JSON summary; Requirement 13 criterion 11):
#     schemaVersion 'gxmcp-live-shared-host/1', runDate (yyyy-MM-dd UTC),
#     genexusVersion, repositoryCommit, pids { gateway[]; workerHost[]; worker[] },
#     queueWaitMs { min; median; max; samples }, minAvailablePhysicalMemoryMb,
#     maxWorkerWorkingSetMb, checks[], cleanup { workerProcesses;
#     workerHostProcesses; registryRecords; waitedSeconds; status }, status, allPassed.
#   Markdown labels: 'Run date', 'GeneXus version', 'Repository commit',
#     'Gateway PIDs', 'Worker_Host PIDs', 'Worker PIDs', 'Queue wait (ms)',
#     'Minimum available physical memory (MB)', 'Maximum Worker working set (MB)',
#     'Worker processes remaining', 'Worker_Host processes remaining',
#     'Ownership_Registry records remaining', 'Gate', and one '| <check> | <status> |'
#     table row per check.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ is required for the live harness tests. Run with 'pwsh', not Windows PowerShell 5.1."
}
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\test-live-shared-host.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Missing production script: scripts/test-live-shared-host.ps1 (task 7.10)."
}
$source = Get-Content -LiteralPath $scriptPath -Raw

function Get-ScriptFunctionDefinitions([string]$Path, [string[]]$Names) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    foreach ($name in $Names) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing production function in $(Split-Path -Leaf $Path): $name" }
        $definition.Extent.Text
    }
}

function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'Expected rejection' }
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-Fields($Object, [string[]]$Names, [string]$What) {
    foreach ($name in $Names) {
        $present = if ($Object -is [System.Collections.IDictionary]) { $Object.Contains($name) } else { $null -ne $Object.PSObject.Properties[$name] }
        if (-not $present) { throw "$What is missing field '$name'." }
    }
}

$definitions = @(Get-ScriptFunctionDefinitions $scriptPath @(
        'Test-SharedHostDisposableKbPath', 'Get-SharedHostAllowedKbPaths', 'Get-SharedHostKbRefusal',
        'Get-SharedHostPrerequisites', 'Get-SharedHostCheckPlan', 'Invoke-SharedHostCheck',
        'Get-SharedHostCleanupWaitSeconds', 'New-SharedHostCleanupResult', 'Get-SharedHostGateStatus',
        'Get-SharedHostExitCode', 'Get-SharedHostDefaultReportPath', 'New-SharedHostRedaction',
        'Protect-SharedHostText', 'New-SharedHostReport', 'ConvertTo-SharedHostReportMarkdown',
        'Write-SharedHostReport', 'Format-SharedHostDetail', 'Invoke-SharedHostBuild',
        'Get-SharedHostKbOpenFailure', 'Resolve-SharedHostKbListing'))
# The repository's existing disposable-KB guard, used as the oracle for the new one.
$definitions += @(Get-ScriptFunctionDefinitions (Join-Path $root 'scripts\sdk-local-build.ps1') @('Resolve-TestKbPath'))
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition)) }

foreach ($required in @('PSVersion.Major -lt 7', 'WorkerSharingMode', 'shared-host', 'WorkerIdleTimeoutMinutes',
        'GXMCP_LOG_DIR', 'GXMCP_TEST_KB2', 'live=', 'shared-host-concurrency.md', 'gxmcp-live-shared-host/1')) {
    if (-not $source.Contains($required)) { throw "Shared-host live script is missing contract marker: $required" }
}

# --- KB guard: Requirement 13 criterion 13 -----------------------------------
$kb1 = 'C:\KBs\KBTeste'
$kb2 = 'C:\KBs\KBTesteTwo'
foreach ($sample in @('C:\KBs\KBTeste', 'c:\kbs\kbteste\', 'C:\KBs\KBTeste17', 'C:\KBs\Production',
        'C:\KBs\nested\KBTeste', 'D:\KBs\KBTeste', 'C:\KBs\KBTeste\..\Production', 'C:\KBsX\KBTeste')) {
    $expected = $true
    try { $null = Resolve-TestKbPath $sample } catch { $expected = $false }
    if ((Test-SharedHostDisposableKbPath -Path $sample) -ne $expected) {
        throw "Disposable KB rule must match Resolve-TestKbPath in sdk-local-build.ps1 for '$sample' (expected $expected)."
    }
}
if (-not (Test-SharedHostDisposableKbPath -Path $kb1) -or (Test-SharedHostDisposableKbPath -Path 'C:\KBs\Production')) {
    throw 'C:\KBs\KBTeste must be disposable and C:\KBs\Production must not be.'
}
$allowed = @(Get-SharedHostAllowedKbPaths -KbPath $kb1 -SecondKbPath $kb2)
if ($allowed.Count -ne 2 -or $allowed[0] -ne $kb1 -or $allowed[1] -ne $kb2) {
    throw "Allowlist must be exactly the two configured disposable KBs; got '$($allowed -join ';')'."
}
if (@(Get-SharedHostAllowedKbPaths -KbPath $kb1 -SecondKbPath 'D:\elsewhere\KBTeste2').Count -ne 1) {
    throw 'A configured second KB outside the disposable rule must not enter the allowlist.'
}
if (@(Get-SharedHostAllowedKbPaths -KbPath $kb1 -SecondKbPath '').Count -ne 1) {
    throw 'An empty second KB must not enter the allowlist.'
}
if ($null -ne (Get-SharedHostKbRefusal -Path 'c:\kbs\kbteste\' -AllowedKbPaths $allowed)) {
    throw 'A configured KB written with different case or a trailing separator must be accepted.'
}
# KBTeste16 passes the repository rule but is not configured, so the allowlist refuses it.
if ((Get-SharedHostKbRefusal -Path 'C:\KBs\KBTeste16' -AllowedKbPaths $allowed) -ne 'C:\KBs\KBTeste16') {
    throw 'A disposable KB that is not one of the two configured KBs must be refused with its path.'
}
if ((Get-SharedHostKbRefusal -Path 'C:\KBs\KBTeste\..\Production' -AllowedKbPaths $allowed) -ne 'C:\KBs\Production') {
    throw 'The refusal must report the normalized rejected path.'
}

$allAvailable = @(
    [pscustomobject]@{ name = 'sdk'; available = $true; detail = 'ok' }
    [pscustomobject]@{ name = 'license'; available = $true; detail = 'ok' }
    [pscustomobject]@{ name = 'kb'; available = $true; detail = 'ok' }
    [pscustomobject]@{ name = 'second-kb'; available = $true; detail = 'ok' }
)
$script:bodyRuns = 0
$passingBody = { $script:bodyRuns++; @{ status = 'passed'; detail = 'ok' } }
$refused = Invoke-SharedHostCheck -Name 'one-gateway-two-kbs' -TargetKbPaths @($kb1, 'D:\elsewhere\KBTeste2') `
    -AllowedKbPaths $allowed -Prerequisites $allAvailable -RequiredPrerequisites @('sdk', 'license', 'kb', 'second-kb') -Body $passingBody
if ((Get-Field $refused 'status') -ne 'failed' -or (Get-Field $refused 'rejectedPath') -ne 'D:\elsewhere\KBTeste2' -or $script:bodyRuns -ne 0) {
    throw 'A check targeting a KB outside the allowlist must be failed with the rejected path, without running.'
}
$missingAndRefused = Invoke-SharedHostCheck -Name 'two-gateways-one-kb' -TargetKbPaths @('C:\KBs\Production') -AllowedKbPaths $allowed `
    -Prerequisites @([pscustomobject]@{ name = 'sdk'; available = $false; detail = 'absent' }) -RequiredPrerequisites @('sdk') -Body $passingBody
if ((Get-Field $missingAndRefused 'status') -ne 'failed' -or (Get-Field $missingAndRefused 'rejectedPath') -ne 'C:\KBs\Production') {
    throw 'The KB guard must run before the prerequisite check: a refused path is failed, not unavailable.'
}

# --- Prerequisites: Requirement 13 criterion 12 ------------------------------
$probes = @{ sdk = { param($Path) $true }; license = { param($Path) $false }; kb = { param($Path) $true } }
$prerequisites = @(Get-SharedHostPrerequisites -GxPath 'C:\gx' -KbPath $kb1 -SecondKbPath $kb2 -Probes $probes)
$byName = @{}
foreach ($item in $prerequisites) { $byName[[string](Get-Field $item 'name')] = $item }
foreach ($name in @('sdk', 'license', 'kb', 'second-kb')) {
    if (-not $byName.ContainsKey($name)) { throw "Prerequisites must report '$name'." }
}
if ((Get-Field $byName['license'] 'available') -ne $false -or (Get-Field $byName['sdk'] 'available') -ne $true) {
    throw 'Injected probes must decide prerequisite availability.'
}
$throwing = @(Get-SharedHostPrerequisites -GxPath 'C:\gx' -KbPath $kb1 -SecondKbPath $kb2 `
    -Probes @{ sdk = { param($Path) throw 'probe crashed' }; license = { param($Path) 'yes' }; kb = { param($Path) $true } })
foreach ($name in @('sdk', 'license')) {
    $item = $throwing | Where-Object { (Get-Field $_ 'name') -eq $name }
    if ($null -eq $item -or (Get-Field $item 'available') -ne $false) {
        throw "A throwing or non-boolean '$name' probe must fail closed as not available."
    }
}
$script:secondProbeCalls = 0
$noSecond = @(Get-SharedHostPrerequisites -GxPath 'C:\gx' -KbPath $kb1 -SecondKbPath '' `
    -Probes @{ sdk = { param($Path) $true }; license = { param($Path) $true }; kb = { param($Path) if ($Path -ne $kb1) { $script:secondProbeCalls++ }; $true } })
$secondItem = $noSecond | Where-Object { (Get-Field $_ 'name') -eq 'second-kb' }
if ($null -eq $secondItem -or (Get-Field $secondItem 'available') -ne $false -or $script:secondProbeCalls -ne 0) {
    throw "An empty second KB must be reported as 'second-kb' not available without probing."
}
$defaultSdk = @(Get-SharedHostPrerequisites -GxPath ('C:\gxmcp-missing-sdk-' + [guid]::NewGuid().ToString('N')) -KbPath $kb1 -SecondKbPath $kb2 `
    -Probes @{ license = { param($Path) $true }; kb = { param($Path) $true } }) | Where-Object { (Get-Field $_ 'name') -eq 'sdk' }
if ($null -eq $defaultSdk -or (Get-Field $defaultSdk 'available') -ne $false) {
    throw 'The default SDK probe must report a missing installation as not available.'
}

$unavailable = Invoke-SharedHostCheck -Name 'two-gateways-one-kb' -TargetKbPaths @($kb1) -AllowedKbPaths $allowed `
    -Prerequisites $prerequisites -RequiredPrerequisites @('sdk', 'license', 'kb') -Body $passingBody
if ((Get-Field $unavailable 'status') -ne 'unavailable' -or @(Get-Field $unavailable 'missingPrerequisites') -notcontains 'license' -or $script:bodyRuns -ne 0) {
    throw 'A missing license must make the check unavailable with the prerequisite named, without running.'
}
$absent = Invoke-SharedHostCheck -Name 'two-gateways-one-kb' -TargetKbPaths @($kb1) -AllowedKbPaths $allowed `
    -Prerequisites @() -RequiredPrerequisites @('sdk') -Body $passingBody
if ((Get-Field $absent 'status') -ne 'unavailable' -or @(Get-Field $absent 'missingPrerequisites') -notcontains 'sdk') {
    throw 'A prerequisite that was never probed must count as missing, never as passed.'
}
$passed = Invoke-SharedHostCheck -Name 'two-gateways-one-kb' -TargetKbPaths @($kb1) -AllowedKbPaths $allowed `
    -Prerequisites $allAvailable -RequiredPrerequisites @('sdk', 'license', 'kb') -Body $passingBody
if ((Get-Field $passed 'status') -ne 'passed' -or $script:bodyRuns -ne 1) { throw 'An admitted check with its prerequisites must run its body.' }
foreach ($case in @(
        @{ body = { throw 'boom' }; expected = 'failed' }
        @{ body = { @{ status = 'skipped'; detail = 'x' } }; expected = 'failed' }
        @{ body = { $null }; expected = 'failed' }
        @{ body = { @{ status = 'unavailable'; detail = 'license lost' } }; expected = 'failed' }
        @{ body = { @{ status = 'unavailable'; detail = 'license lost'; missingPrerequisites = @('license') } }; expected = 'unavailable' })) {
    $result = Invoke-SharedHostCheck -Name 'cleanup' -TargetKbPaths @($kb1) -AllowedKbPaths $allowed `
        -Prerequisites $allAvailable -RequiredPrerequisites @('sdk') -Body $case.body
    if ((Get-Field $result 'status') -ne $case.expected) {
        throw "Check body outcome must map to '$($case.expected)'; got '$(Get-Field $result 'status')'."
    }
}

$plan = @(Get-SharedHostCheckPlan -KbPath $kb1 -SecondKbPath $kb2)
if ((@($plan | ForEach-Object { Get-Field $_ 'name' }) -join ',') -ne 'two-gateways-one-kb,one-gateway-two-kbs,cleanup') {
    throw 'The check plan must list two-gateways-one-kb, one-gateway-two-kbs and cleanup in that order.'
}
$twoKbs = $plan[1]
if (@(Get-Field $twoKbs 'targets') -notcontains $kb2 -or @(Get-Field $twoKbs 'prerequisites') -notcontains 'second-kb') {
    throw 'one-gateway-two-kbs must target the second KB and require it.'
}
foreach ($check in $plan) {
    foreach ($name in @('sdk', 'license', 'kb')) {
        if (@(Get-Field $check 'prerequisites') -notcontains $name) { throw "Check '$(Get-Field $check 'name')' must require '$name'." }
    }
}

# --- Gate and cleanup: Requirement 13 criteria 11 and 14 ---------------------
if ((Get-SharedHostCleanupWaitSeconds -WorkerIdleTimeoutMinutes 30) -ne 1860) {
    throw 'Cleanup must wait Server.WorkerIdleTimeoutMinutes plus 60 seconds.'
}
$cleanClean = New-SharedHostCleanupResult -WorkerProcesses 0 -WorkerHostProcesses 0 -RegistryRecords 0 -WaitedSeconds 1860
Assert-Fields $cleanClean @('workerProcesses', 'workerHostProcesses', 'registryRecords', 'waitedSeconds', 'status') 'Cleanup result'
if ((Get-Field $cleanClean 'status') -ne 'passed') { throw 'Zero remaining processes and records is a passed cleanup.' }
foreach ($leftover in @(@{ w = 1; h = 0; r = 0 }, @{ w = 0; h = 1; r = 0 }, @{ w = 0; h = 0; r = 1 })) {
    $dirty = New-SharedHostCleanupResult -WorkerProcesses $leftover.w -WorkerHostProcesses $leftover.h -RegistryRecords $leftover.r -WaitedSeconds 1860
    if ((Get-Field $dirty 'status') -ne 'failed') { throw 'Any remaining Worker, Worker_Host or registry record fails cleanup.' }
    if ((Get-SharedHostGateStatus -Checks @($passed) -Cleanup $dirty) -ne 'failed') {
        throw 'The gate must not pass while cleanup reports remaining processes or records.'
    }
}
$unmeasured = New-SharedHostCleanupResult -WorkerProcesses $null -WorkerHostProcesses 0 -RegistryRecords 0 -WaitedSeconds 0
if ((Get-Field $unmeasured 'status') -ne 'unavailable') { throw 'An unmeasured cleanup count is unavailable, not passed.' }
$failedCheck = [pscustomobject]@{ name = 'cleanup'; status = 'failed'; detail = 'x'; missingPrerequisites = @(); rejectedPath = $null }
$unknownCheck = [pscustomobject]@{ name = 'cleanup'; status = 'skipped'; detail = 'x'; missingPrerequisites = @(); rejectedPath = $null }
foreach ($case in @(
        @{ checks = @($passed); cleanup = $cleanClean; expected = 'passed' }
        @{ checks = @($passed, $unavailable); cleanup = $cleanClean; expected = 'unavailable' }
        @{ checks = @($passed, $unavailable, $failedCheck); cleanup = $cleanClean; expected = 'failed' }
        @{ checks = @($passed, $unknownCheck); cleanup = $cleanClean; expected = 'failed' }
        @{ checks = @($passed); cleanup = $unmeasured; expected = 'unavailable' }
        @{ checks = @(); cleanup = $cleanClean; expected = 'unavailable' })) {
    $gate = Get-SharedHostGateStatus -Checks $case.checks -Cleanup $case.cleanup
    if ($gate -ne $case.expected) { throw "Gate must be '$($case.expected)'; got '$gate'." }
}
foreach ($pair in @(@('passed', 0), @('failed', 1), @('unavailable', 2))) {
    if ((Get-SharedHostExitCode -Status $pair[0]) -ne $pair[1]) { throw "Status '$($pair[0])' must exit $($pair[1])." }
}
$expectedDefault = Join-Path $root 'docs\benchmarks\2026-09-21-shared-host-concurrency.md'
if ((Get-SharedHostDefaultReportPath -Root $root -DateUtc ([datetime]'2026-09-21T23:30:00Z').ToUniversalTime()) -ne $expectedDefault) {
    throw 'The default report path must be docs/benchmarks/<date>-shared-host-concurrency.md.'
}

# --- Diagnostic hygiene: bounded details, build output, KB open failures -----
# First live run: the whole build log leaked into the init error and became one
# report cell, and a KB the SDK refused to open surfaced only as "No object to
# read was listed". Details are single-line and capped at 500 characters.
$longDetail = ('line one of a very long build log ' * 40) + "`r`nsecond line`nthird | piped"
$bounded = Format-SharedHostDetail -Text $longDetail
if ($bounded.Length -gt 500 -or $bounded -match '[\r\n]' -or -not $bounded.StartsWith('line one')) {
    throw "A check detail must be one line of at most 500 characters; got $($bounded.Length) characters."
}
if ((Format-SharedHostDetail -Text 'short detail') -ne 'short detail' -or (Format-SharedHostDetail -Text $null) -ne '') {
    throw 'A short detail must pass unchanged and a missing detail must become an empty string.'
}
foreach ($body in @(
        { @{ status = 'failed'; detail = $longDetail } }
        { @{ status = 'passed'; detail = $longDetail } }
        { throw $longDetail })) {
    $noisy = Invoke-SharedHostCheck -Name 'cleanup' -TargetKbPaths @($kb1) -AllowedKbPaths $allowed `
        -Prerequisites $allAvailable -RequiredPrerequisites @('sdk') -Body $body
    $detail = [string](Get-Field $noisy 'detail')
    if ($detail.Length -gt 500 -or $detail -match '[\r\n]') {
        throw "Invoke-SharedHostCheck must bound every detail to one line of 500 characters; got $($detail.Length)."
    }
}
$initDefinition = @(Get-ScriptFunctionDefinitions $scriptPath @('Initialize-LiveRun'))[0]
if (-not $initDefinition.Contains('Invoke-SharedHostBuild') -or $initDefinition -match '&\s*pwsh[^\n]*build\.ps1') {
    throw 'Initialize-LiveRun must build through Invoke-SharedHostBuild so build output never reaches its return value.'
}
$pwshExe = (Get-Process -Id $PID).Path
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-shared-host-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
try {
    $okLog = Join-Path $buildRoot 'ok.log'
    $okResult = @(Invoke-SharedHostBuild -FilePath $pwshExe -LogPath $okLog `
            -ArgumentList @('-NoProfile', '-Command', "1..400 | ForEach-Object { 'build line ' + `$_ }; [Console]::Error.WriteLine('build warning'); exit 0"))
    if (@($okResult | Where-Object { $null -ne $_ }).Count -ne 0) {
        throw "A successful build must return `$null and keep its output out of the result; got $(@($okResult).Count) item(s)."
    }
    $okText = Get-Content -LiteralPath $okLog -Raw
    if (-not $okText.Contains('build line 400') -or -not $okText.Contains('build warning')) {
        throw 'The build output, including stderr, must go to the build log.'
    }
    $failLog = Join-Path $buildRoot 'fail.log'
    $failResult = @(Invoke-SharedHostBuild -FilePath $pwshExe -LogPath $failLog `
            -ArgumentList @('-NoProfile', '-Command', "1..400 | ForEach-Object { 'build line ' + `$_ }; exit 7"))
    $failText = [string]($failResult | Select-Object -Last 1)
    if ($failResult.Count -ne 1 -or $failText -notmatch 'exit code 7' -or -not $failText.Contains($failLog) -or
        $failText.Contains('build line') -or $failText.Length -gt 500 -or $failText -match '[\r\n]') {
        throw "A failed build must return one short line with the exit code and the log path; got '$failText'."
    }
    $missingResult = [string](Invoke-SharedHostBuild -FilePath (Join-Path $buildRoot 'missing-build.exe') -ArgumentList @() -LogPath (Join-Path $buildRoot 'missing.log'))
    if ([string]::IsNullOrWhiteSpace($missingResult) -or $missingResult.Length -gt 500) {
        throw 'A build that cannot start must return a short failure, not throw or pass.'
    }
}
finally {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$workerLog = @(
    '[2026-11-09T09:54:27.133-03:00] [INFO] [] Opening KB: C:\KBs\KBTeste'
    '[2026-11-09T09:54:27.666-03:00] [ERROR] [] [KB-OPEN-FAIL] elapsedMs=532 path=C:\KBs\KBTeste error=InvalidDirectory'
    '[2026-11-09T09:54:27.700-03:00] [ERROR] [] [KB-OPEN-FAIL] elapsedMs=12 path=C:\KBs\KBTesteTwo error=OtherKbFailure'
) -join "`n"
$openReason = Get-SharedHostKbOpenFailure -LogText $workerLog -KbPath 'c:\kbs\kbteste\'
if ([string]$openReason -notmatch 'InvalidDirectory' -or [string]$openReason -match 'OtherKbFailure') {
    throw "The KB open failure must be read for the requested KB only; got '$openReason'."
}
if ([string](Get-SharedHostKbOpenFailure -LogText $workerLog -KbPath $kb2) -notmatch 'OtherKbFailure') {
    throw 'A KB path that another KB path starts with must not hide the second KB failure.'
}
if ($null -ne (Get-SharedHostKbOpenFailure -LogText "[INFO] [KB-OPEN] elapsedMs=10 path=$kb1" -KbPath $kb1)) {
    throw 'A log without a KB open failure must report none.'
}
if ($null -ne (Get-SharedHostKbOpenFailure -LogText ($workerLog + "`n[INFO] [] [KB-OPEN] elapsedMs=900 path=$kb1") -KbPath $kb1)) {
    throw 'A later successful open of the same KB must clear an earlier open failure.'
}
$longOpen = Get-SharedHostKbOpenFailure -LogText ("[KB-OPEN-FAIL] elapsedMs=1 path=$kb1 error=" + ('x' * 2000)) -KbPath $kb1
if ([string]::IsNullOrEmpty($longOpen) -or $longOpen.Length -gt 500) { throw 'A KB open failure reason must be bounded.' }
$openFailed = Resolve-SharedHostKbListing -KbPath $kb1 -ObjectFound $false -Code 'IndexNotReady' -IsError $false -OpenFailure $openReason -TimedOut $false
if ((Get-Field $openFailed 'state') -ne 'failed' -or [string](Get-Field $openFailed 'detail') -notmatch 'InvalidDirectory' -or
    [string](Get-Field $openFailed 'detail') -match 'No object') {
    throw 'A KB the Worker could not open must fail with the open reason, not as an empty listing.'
}
foreach ($case in @(
        @{ found = $true; code = $null; isError = $false; open = $null; timedOut = $false; expected = 'ready' }
        @{ found = $false; code = 'IndexNotReady'; isError = $false; open = $null; timedOut = $false; expected = 'wait' }
        # The first run stopped after one listing whose code it could not read.
        @{ found = $false; code = $null; isError = $false; open = $null; timedOut = $false; expected = 'wait' }
        @{ found = $false; code = 'IndexNotReady'; isError = $false; open = $null; timedOut = $true; expected = 'failed' }
        @{ found = $false; code = 'Boom'; isError = $true; open = $null; timedOut = $false; expected = 'failed' })) {
    $resolved = Resolve-SharedHostKbListing -KbPath $kb1 -ObjectFound $case.found -Code $case.code -IsError $case.isError `
        -OpenFailure $case.open -TimedOut $case.timedOut
    if ((Get-Field $resolved 'state') -ne $case.expected) {
        throw "Listing (found=$($case.found), code=$($case.code), error=$($case.isError), timedOut=$($case.timedOut)) must be '$($case.expected)'; got '$(Get-Field $resolved 'state')'."
    }
    if ($case.expected -eq 'failed' -and [string]::IsNullOrWhiteSpace([string](Get-Field $resolved 'detail'))) {
        throw 'A failed listing must say why.'
    }
}
$timedOut = Resolve-SharedHostKbListing -KbPath $kb1 -ObjectFound $false -Code 'IndexNotReady' -IsError $false -OpenFailure $null -TimedOut $true
if ([string](Get-Field $timedOut 'detail') -notmatch 'IndexNotReady') { throw 'A listing that timed out must name the last listing code.' }
# --- Redaction and report fields ----------------------------------------------
# Synthetic identity only: the test never writes the real user, machine or profile.
$savedIdentity = @{}
foreach ($key in @('USERNAME', 'COMPUTERNAME', 'USERPROFILE')) { $savedIdentity[$key] = [Environment]::GetEnvironmentVariable($key) }
$reportRoot = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-shared-host-report-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
try {
    $env:USERNAME = 'synthetic-user-x1'
    $env:COMPUTERNAME = 'SYNTH-HOST-9'
    $env:USERPROFILE = 'C:\Users\synthetic-user-x1'
    $gx = 'C:\Program Files (x86)\GeneXus\GeneXus18'
    $profileConfig = 'C:\Users\synthetic-user-x1\.genexus-mcp\config.json'
    $redaction = New-SharedHostRedaction -KbPaths @($kb1, $kb2) -GxPath $gx -ProfileConfigPath $profileConfig
    $raw = "opened C:\KBs\KBTeste and c:\kbs\kbtestetwo from $profileConfig on synth-host-9 as SYNTHETIC-USER-X1 using $gx; log C:\Users\synthetic-user-x1\AppData\x.log"
    $expectedText = 'opened <kb-1> and <kb-2> from <profile-config> on <machine> as <user> using <gx-install>; log <user-profile>\AppData\x.log'
    $protected = Protect-SharedHostText -Text $raw -Redaction $redaction
    if ($protected -ne $expectedText) { throw "Redaction mismatch.`n  expected: $expectedText`n  actual:   $protected" }

    $checks = @(
        [pscustomobject]@{ name = 'two-gateways-one-kb'; status = 'passed'; detail = "two Gateways on $kb1 as synthetic-user-x1"; missingPrerequisites = @(); rejectedPath = $null }
        [pscustomobject]@{ name = 'one-gateway-two-kbs'; status = 'unavailable'; detail = "second KB missing next to $kb2"; missingPrerequisites = @('second-kb'); rejectedPath = $null }
        [pscustomobject]@{ name = 'cleanup'; status = 'failed'; detail = 'refused'; missingPrerequisites = @(); rejectedPath = 'C:\Users\synthetic-user-x1\KBs\Other' }
    )
    $report = New-SharedHostReport -RunDateUtc ([datetime]'2026-09-21T10:00:00Z').ToUniversalTime() -GeneXusVersion '18.0.12345' `
        -RepositoryCommit '0123456789abcdef0123456789abcdef01234567' -GatewayPids @(4100, 4104) -WorkerHostPids @(4200) -WorkerPids @(4300) `
        -QueueWaitSamplesMs @(10, 30, 20, 40) -MinAvailablePhysicalMemoryMb 5120 -MaxWorkerWorkingSetMb 900 -Checks $checks -Cleanup $cleanClean
    Assert-Fields $report @('schemaVersion', 'runDate', 'genexusVersion', 'repositoryCommit', 'pids', 'queueWaitMs',
        'minAvailablePhysicalMemoryMb', 'maxWorkerWorkingSetMb', 'checks', 'cleanup', 'status', 'allPassed') 'Report'
    Assert-Fields (Get-Field $report 'pids') @('gateway', 'workerHost', 'worker') 'Report pids'
    Assert-Fields (Get-Field $report 'queueWaitMs') @('min', 'median', 'max', 'samples') 'Report queueWaitMs'
    $wait = Get-Field $report 'queueWaitMs'
    if ((Get-Field $report 'schemaVersion') -ne 'gxmcp-live-shared-host/1' -or (Get-Field $report 'runDate') -ne '2026-09-21' -or
        [double](Get-Field $wait 'min') -ne 10 -or [double](Get-Field $wait 'median') -ne 25 -or [double](Get-Field $wait 'max') -ne 40 -or
        [int](Get-Field $wait 'samples') -ne 4) {
        throw 'Report must record the run date and the min, median and max queue wait.'
    }
    if ((Get-Field $report 'status') -ne 'failed' -or (Get-Field $report 'allPassed') -ne $false) {
        throw 'Report status must come from the gate: a failed check is not passed.'
    }
    $emptyWait = Get-Field (New-SharedHostReport -RunDateUtc ([datetime]::UtcNow) -GeneXusVersion $null -RepositoryCommit 'abc' -GatewayPids @() `
            -WorkerHostPids @() -WorkerPids @() -QueueWaitSamplesMs @() -MinAvailablePhysicalMemoryMb $null -MaxWorkerWorkingSetMb $null `
            -Checks @() -Cleanup $unmeasured) 'queueWaitMs'
    if ($null -ne (Get-Field $emptyWait 'median') -or [int](Get-Field $emptyWait 'samples') -ne 0) {
        throw 'No queue-wait samples must be recorded as null statistics, not zero.'
    }

    $markdownPath = Join-Path $reportRoot 'report.md'
    $jsonPath = Join-Path $reportRoot 'summary.json'
    Write-SharedHostReport -Report $report -ReportPath $markdownPath -SummaryPath $jsonPath -Redaction $redaction
    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    $jsonText = Get-Content -LiteralPath $jsonPath -Raw
    $json = $jsonText | ConvertFrom-Json
    foreach ($label in @('Run date', 'GeneXus version', 'Repository commit', 'Gateway PIDs', 'Worker_Host PIDs', 'Worker PIDs',
            'Queue wait (ms)', 'Minimum available physical memory (MB)', 'Maximum Worker working set (MB)',
            'Worker processes remaining', 'Worker_Host processes remaining', 'Ownership_Registry records remaining', 'Gate')) {
        if (-not $markdown.Contains($label)) { throw "Markdown report is missing '$label'." }
    }
    foreach ($value in @('2026-09-21', '18.0.12345', '0123456789abcdef0123456789abcdef01234567', '4100', '4104', '4200', '4300', '5120', '900')) {
        if (-not $markdown.Contains($value)) { throw "Markdown report is missing value '$value'." }
    }
    foreach ($check in $checks) {
        if ($markdown -notmatch ('\|\s*' + [regex]::Escape($check.name) + '\s*\|\s*' + $check.status + '\s*\|')) {
            throw "Markdown report must list '$($check.name)' with status '$($check.status)'."
        }
    }
    if ($markdown -notmatch 'second-kb') { throw 'Markdown report must name the missing prerequisite.' }
    if ((Get-Field $json 'schemaVersion') -ne 'gxmcp-live-shared-host/1' -or @((Get-Field $json 'checks')).Count -ne 3) {
        throw 'JSON summary must keep the schema version and every check.'
    }
    $refusedJson = @((Get-Field $json 'checks')) | Where-Object { (Get-Field $_ 'name') -eq 'cleanup' }
    if ((Get-Field $refusedJson 'rejectedPath') -ne '<user-profile>\KBs\Other') {
        throw 'The rejected path must be recorded, with only the profile part redacted.'
    }
    foreach ($text in @($markdown, $jsonText)) {
        foreach ($secret in @($kb1, $kb2, $gx, $profileConfig, 'synthetic-user-x1', 'SYNTH-HOST-9', ($kb1 -replace '\\', '\\'), ($gx -replace '\\', '\\'))) {
            if ($text.IndexOf($secret, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Report leaked '$secret'." }
        }
    }
}
finally {
    foreach ($key in $savedIdentity.Keys) { [Environment]::SetEnvironmentVariable($key, $savedIdentity[$key]) }
    Remove-Item -LiteralPath $reportRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Real entry point, fail-closed paths (no Gateway may start) ---------------
function Invoke-LiveSharedHost([string[]]$Arguments) {
    $savedErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& pwsh -NoProfile -File $scriptPath @Arguments 2>&1)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }
    finally { $ErrorActionPreference = $savedErrorAction }
}

function Assert-NoPersonalData([string]$Text, [string[]]$ConfiguredPaths) {
    $needles = @($ConfiguredPaths) + @($env:USERPROFILE, $env:USERNAME, $env:COMPUTERNAME) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 3 }
    foreach ($needle in $needles) {
        foreach ($form in @($needle, ($needle -replace '\\', '\\'))) {
            if ($Text.IndexOf($form, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'Live report leaked a configured path or the local identity.' }
        }
    }
}

$benchmarks = Join-Path $root 'docs\benchmarks'
$benchmarksBefore = @(Get-ChildItem -LiteralPath $benchmarks -File -ErrorAction SilentlyContinue | ForEach-Object Name)
$savedSecondKb = $env:GXMCP_TEST_KB2
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-shared-host-entry-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
try {
    $env:GXMCP_TEST_KB2 = $null
    $missingSdk = 'C:\gxmcp-missing-sdk-' + [guid]::NewGuid().ToString('N')

    # A primary KB outside the disposable rule is refused and failed, even with the SDK missing.
    $rejectedReport = Join-Path $runRoot 'rejected.md'
    $rejectedSummary = Join-Path $runRoot 'rejected.json'
    $rejected = Invoke-LiveSharedHost @('-KbPath', 'C:\KBs\GxMcpProductionLike', '-SecondKbPath', 'C:\KBs\KBTesteTwo',
        '-GxPath', $missingSdk, '-SkipBuild', '-ReportPath', $rejectedReport, '-SummaryPath', $rejectedSummary)
    if ($rejected.ExitCode -ne 1 -or $rejected.Output -notmatch 'live=failed') {
        throw "A refused KB path must exit 1 with live=failed; got exit $($rejected.ExitCode)."
    }
    $summary = Get-Content -LiteralPath $rejectedSummary -Raw | ConvertFrom-Json
    if ((Get-Field $summary 'status') -ne 'failed' -or (Get-Field $summary 'allPassed') -ne $false) {
        throw 'A refused KB path must leave the gate failed.'
    }
    $primaryChecks = @((Get-Field $summary 'checks')) | Where-Object { @('two-gateways-one-kb', 'one-gateway-two-kbs') -contains (Get-Field $_ 'name') }
    if (@($primaryChecks).Count -ne 2) { throw 'Both checks targeting the primary KB must be recorded.' }
    foreach ($check in $primaryChecks) {
        if ((Get-Field $check 'status') -ne 'failed' -or (Get-Field $check 'rejectedPath') -ne 'C:\KBs\GxMcpProductionLike') {
            throw "Check '$(Get-Field $check 'name')' must be failed with the rejected path."
        }
    }
    if (@((Get-Field (Get-Field $summary 'pids') 'gateway')).Count -ne 0) { throw 'No Gateway may start after a refused KB path.' }
    if (-not (Test-Path -LiteralPath $rejectedReport -PathType Leaf)) { throw 'A refused run must still write its report.' }

    # A missing SDK and KB are unavailable with the prerequisites named; nothing passes.
    $missingKb = 'C:\KBs\gxmcp-missing-teste-' + [guid]::NewGuid().ToString('N')
    $unavailableReport = Join-Path $runRoot 'unavailable.md'
    $unavailableSummary = Join-Path $runRoot 'unavailable.json'
    $missing = Invoke-LiveSharedHost @('-KbPath', $missingKb, '-GxPath', $missingSdk, '-SkipBuild',
        '-ReportPath', $unavailableReport, '-SummaryPath', $unavailableSummary)
    if ($missing.ExitCode -ne 2 -or $missing.Output -notmatch 'live=unavailable') {
        throw "Missing SDK and KB must exit 2 with live=unavailable; got exit $($missing.ExitCode)."
    }
    $summaryText = Get-Content -LiteralPath $unavailableSummary -Raw
    $summary = $summaryText | ConvertFrom-Json
    Assert-Fields $summary @('schemaVersion', 'runDate', 'genexusVersion', 'repositoryCommit', 'pids', 'queueWaitMs',
        'minAvailablePhysicalMemoryMb', 'maxWorkerWorkingSetMb', 'checks', 'cleanup', 'status', 'allPassed') 'Unavailable summary'
    if ((Get-Field $summary 'status') -ne 'unavailable' -or (Get-Field $summary 'allPassed') -ne $false) {
        throw 'Missing prerequisites must leave the gate unavailable, not passed.'
    }
    $names = @((Get-Field $summary 'checks')) | ForEach-Object { Get-Field $_ 'name' }
    if (($names -join ',') -ne 'two-gateways-one-kb,one-gateway-two-kbs,cleanup') { throw 'Every check must be recorded, in plan order.' }
    foreach ($check in @((Get-Field $summary 'checks'))) {
        if ((Get-Field $check 'status') -ne 'unavailable') { throw "Check '$(Get-Field $check 'name')' must be unavailable, never passed." }
        $named = @(Get-Field $check 'missingPrerequisites')
        if ($named -notcontains 'sdk' -or $named -notcontains 'kb') { throw "Check '$(Get-Field $check 'name')' must name the missing sdk and kb." }
    }
    $twoKbCheck = @((Get-Field $summary 'checks')) | Where-Object { (Get-Field $_ 'name') -eq 'one-gateway-two-kbs' }
    if (@(Get-Field $twoKbCheck 'missingPrerequisites') -notcontains 'second-kb') { throw "An unset second KB must be named as 'second-kb'." }
    $unavailableMarkdown = Get-Content -LiteralPath $unavailableReport -Raw
    foreach ($label in @('Run date', 'Repository commit', 'Queue wait (ms)', 'Ownership_Registry records remaining', 'Gate')) {
        if (-not $unavailableMarkdown.Contains($label)) { throw "Unavailable report is missing '$label'." }
    }
    foreach ($text in @($summaryText, $unavailableMarkdown, (Get-Content -LiteralPath $rejectedReport -Raw), (Get-Content -LiteralPath $rejectedSummary -Raw))) {
        Assert-NoPersonalData $text @($missingKb, $missingSdk, 'C:\KBs\KBTesteTwo')
    }
}
finally {
    $env:GXMCP_TEST_KB2 = $savedSecondKb
    Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$benchmarksAfter = @(Get-ChildItem -LiteralPath $benchmarks -File -ErrorAction SilentlyContinue | ForEach-Object Name)
if (($benchmarksBefore -join '|') -ne ($benchmarksAfter -join '|')) { throw 'An explicit -ReportPath must not write under docs/benchmarks.' }

Write-Host 'live-shared-host-script: KB guard, prerequisites, gate, cleanup, report fields and redaction contract passed' -ForegroundColor Green
