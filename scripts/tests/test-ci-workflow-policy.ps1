$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ciScripts = Join-Path $root 'scripts/ci'
$ci = Get-Content -LiteralPath (Join-Path $root '.github/workflows/ci.yml') -Raw
$release = Get-Content -LiteralPath (Join-Path $root '.github/workflows/release.yml') -Raw

function Assert-Policy([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "CI workflow policy failed: $Message" }
}

# ---- Static workflow policy -------------------------------------------------

function Get-JobNames([string]$Workflow) {
    $jobs = $Workflow.Substring($Workflow.IndexOf("`njobs:`n"))
    @([regex]::Matches($jobs, '(?m)^  ([A-Za-z0-9_-]+):\s*$') | ForEach-Object { $_.Groups[1].Value })
}

function Get-JobBlock([string]$Workflow, [string]$Job) {
    $match = [regex]::Match($Workflow, "(?ms)^  $([regex]::Escape($Job)):\s*\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)")
    if (-not $match.Success) { return $null }
    return $match.Groups['body'].Value
}

function Get-JobNeeds([string]$Workflow, [string]$Job) {
    $block = Get-JobBlock $Workflow $Job
    if ($null -eq $block) { return $null }
    $needs = [regex]::Match($block, '(?m)^    needs:\s*\[(?<list>[^\]]*)\]\s*$')
    if (-not $needs.Success) { return @() }
    @($needs.Groups['list'].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# Every lane except the gate itself must gate `test`; a lane added or dropped without
# updating needs is the defect this catches (rust (gx-engine) was once left out).
function Test-GateDependencies([string]$Workflow) {
    $errors = @()
    $jobs = Get-JobNames $Workflow
    if ($jobs -notcontains 'test') { return @('job test is missing') }
    $needs = Get-JobNeeds $Workflow 'test'
    foreach ($job in ($jobs | Where-Object { $_ -ne 'test' })) {
        if ($needs -notcontains $job) { $errors += "test does not depend on $job" }
    }
    foreach ($need in $needs) {
        if ($jobs -notcontains $need) { $errors += "test depends on unknown job $need" }
    }
    $block = Get-JobBlock $Workflow 'test'
    if ($block -notmatch '(?m)^    if:\s*\$\{\{\s*!cancelled\(\)\s*\}\}\s*$') {
        $errors += 'test must run after a failed lane (if: !cancelled()); a skipped required check passes branch protection'
    }
    if ($block -notmatch 'assert-ci-gate\.ps1' -or $block -notmatch 'needs\.changes\.outputs\.sdk_evidence_required' -or $block -notmatch 'toJSON\(needs\)') {
        $errors += 'test must assert lane results and SDK evidence through assert-ci-gate.ps1'
    }
    return $errors
}

Assert-Policy (@(Test-GateDependencies $ci).Count -eq 0) ("gate dependencies: " + (@(Test-GateDependencies $ci) -join '; '))
foreach ($required in @('changes', 'dotnet', 'toolchain', 'rust', 'rust-engine', 'contracts', 'sdk-validation')) {
    Assert-Policy ((Get-JobNeeds $ci 'test') -contains $required) "test must depend on $required."
}

# Mutations: the checks above must be able to fail.
$withoutRustEngine = $ci -replace '(?m)^(    needs: \[[^\]]*?)\brust-engine, ', '$1'
Assert-Policy ($withoutRustEngine -ne $ci) 'mutation setup: rust-engine was not removed.'
Assert-Policy (@(Test-GateDependencies $withoutRustEngine) -contains 'test does not depend on rust-engine') 'missing rust-engine dependency was not detected.'
$wrongDependency = $ci -replace '(?m)^(    needs: \[[^\]]*?)\bcontracts\b', '$1contract'
Assert-Policy (@(Test-GateDependencies $wrongDependency) -contains 'test depends on unknown job contract') 'wrong dependency name was not detected.'
Assert-Policy (@(Test-GateDependencies $wrongDependency) -contains 'test does not depend on contracts') 'dropped contracts dependency was not detected.'
$noAlways = $ci -replace '(?m)^    if: \$\{\{ !cancelled\(\) \}\}\r?\n', ''
Assert-Policy (@(Test-GateDependencies $noAlways).Count -gt 0) 'missing !cancelled() on the gate was not detected.'
$withoutGate = $ci -replace 'assert-ci-gate\.ps1', 'removed.ps1'
Assert-Policy (@(Test-GateDependencies $withoutGate).Count -gt 0) 'removed SDK evidence assertion was not detected.'

$sdkJob = Get-JobBlock $ci 'sdk-validation'
Assert-Policy ($sdkJob -match 'runs-on:\s*\[self-hosted, windows, gx-sdk-18\]') 'the SDK lane must stay on the protected gx-sdk-18 runner.'
Assert-Policy ($sdkJob -match "GXMCP_SDK_CI_ENABLED == 'true'") 'the SDK lane must exist only while a runner is declared (no indefinite queue).'
Assert-Policy ($sdkJob -match 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository') 'the licensed runner must never execute fork code.'
Assert-Policy ($sdkJob -match 'state: \$\{\{ steps\.status\.outputs\.state \}\}' -and $sdkJob -match 'commit: \$\{\{ steps\.status\.outputs\.commit \}\}') 'the SDK lane must export state and commit for the gate.'

$contracts = Get-JobBlock $ci 'contracts'
foreach ($step in @('run-release-script-tests.ps1', 'check-line-endings.ps1', 'test-ci-workflow-policy.ps1', 'check-build-warning-baseline.ps1 -ValidateOnly')) {
    Assert-Policy ($contracts.Contains($step)) "contracts lane must run $step."
}
$sdkScript = Get-Content -LiteralPath (Join-Path $ciScripts 'sdk-validation.ps1') -Raw
Assert-Policy ($sdkScript -match 'check-build-warning-baseline\.ps1' -and $sdkScript -notmatch '-ValidateOnly') 'the SDK lane must compare real Release warning locations, not only validate the manifest.'

foreach ($workflowFile in Get-ChildItem -LiteralPath (Join-Path $root '.github/workflows') -Filter '*.yml') {
    $text = Get-Content -LiteralPath $workflowFile.FullName -Raw
    Assert-Policy ($text -notmatch '(?m)^\s*pull_request_target\s*:') "$($workflowFile.Name) must not use pull_request_target (fork code with a privileged token)."
    Assert-Policy ($text -notmatch '(?m)^\s*continue-on-error\s*:') "$($workflowFile.Name) must not mask failures with continue-on-error."
}

# A pwsh step exits with the code of the LAST native command, so a failing native command followed
# by a passing one leaves the step green (the gx-engine lane once ran 8 clippy errors and a failed
# test green). Every native command of a multi-command step must be followed by an explicit
# $LASTEXITCODE check; a step with a single native command needs none.
function Get-WorkflowSteps([string]$Workflow) {
    @([regex]::Split($Workflow, '(?m)^      - (?=name:|uses:)') | Select-Object -Skip 1)
}

function Get-StepRunLines([string]$Step) {
    $lines = @($Step -split '\r?\n')
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^        run:\s*(?<inline>.*)$') { continue }
        $inline = $Matches['inline'].Trim()
        $body = @()
        if ($inline -and $inline -notmatch '^[|>][-+]?$') { $body += $inline }
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -and $lines[$j] -notmatch '^          ') { break }
            $body += $lines[$j].Trim()
        }
        return @($body | Where-Object { $_ -and -not $_.StartsWith('#') })
    }
    return @()
}

function Test-NativeCommandExitCodes([string]$Workflow) {
    $errors = @()
    foreach ($step in (Get-WorkflowSteps $Workflow)) {
        $name = [regex]::Match($step, '(?m)^name:\s*(?<n>.+?)\s*$').Groups['n'].Value
        $lines = @(Get-StepRunLines $step)
        $native = @(for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^(cargo|python|dotnet|npm|node)(\.exe)?\s') { $i }
        })
        foreach ($index in ($native | Select-Object -SkipLast 1)) {
            $next = if ($index + 1 -lt $lines.Count) { $lines[$index + 1] } else { '' }
            if ($next -notmatch '\$LASTEXITCODE') {
                $errors += "step '$name' runs '$($lines[$index])' without checking `$LASTEXITCODE before the next command"
            }
        }
    }
    return $errors
}

Assert-Policy (@(Get-WorkflowSteps $ci).Count -gt 20) 'the step parser must find the workflow steps.'
Assert-Policy (@(Test-NativeCommandExitCodes $ci).Count -eq 0) ("native command exit codes: " + (@(Test-NativeCommandExitCodes $ci) -join '; '))
foreach ($lane in @('gxwire', 'gx-engine')) {
    Assert-Policy ($ci -match "(?m)^        run: cargo test --manifest-path src/$lane/Cargo.toml\s*$") "the $lane lane must run cargo test as a step of its own."
}
Assert-Policy ($ci -match '(?m)^        run: cargo clippy --manifest-path src/gx-engine/Cargo.toml --all-targets -- -D warnings\s*$') 'the gx-engine lint must be a step of its own (-D warnings).'
$mergedCargo = [regex]::Replace($ci, '(?m)^        run: (cargo clippy [^\r\n]+)\r?\n', [System.Text.RegularExpressions.MatchEvaluator] {
    param($match)
    "        run: |`n          $($match.Groups[1].Value)`n          cargo fmt --manifest-path src/gx-engine/Cargo.toml --check`n"
})
Assert-Policy ($mergedCargo -ne $ci) 'mutation setup: clippy and fmt were not merged.'
Assert-Policy (@(Test-NativeCommandExitCodes $mergedCargo).Count -gt 0) 'two cargo commands in one step without an exit-code check were not detected.'
$guardedCargo = $mergedCargo -replace '(?m)^(          cargo clippy [^\r\n]+)\r?\n', "`$1`n          if (`$LASTEXITCODE -ne 0) { throw 'clippy failed' }`n"
Assert-Policy ($guardedCargo -ne $mergedCargo) 'mutation setup: the exit-code check was not added.'
Assert-Policy (@(Test-NativeCommandExitCodes $guardedCargo).Count -eq 0) 'an explicit $LASTEXITCODE check after the first cargo command must satisfy the rule.'

# Publication workflow: gate precedes every trust decision, and nothing can bypass it.
$gateIndex = $release.IndexOf('name: Verify release commit identity and required checks')
Assert-Policy ($gateIndex -ge 0) 'release.yml must verify the release commit identity and required checks.'
foreach ($later in @('name: Upgrade npm for Trusted Publishing', 'name: Download release artifacts', 'name: Publish to npm with provenance')) {
    Assert-Policy ($release.IndexOf($later) -gt $gateIndex) "the gate must precede '$later'."
}
Assert-Policy ($release.IndexOf('name: Resolve release commit') -lt $gateIndex) 'the gate must run on the resolved release commit.'
Assert-Policy ($release -match 'verify-release-commit\.ps1') 'release.yml must call verify-release-commit.ps1.'
Assert-Policy ($release -match '(?m)^  checks: read\s*$') 'release.yml needs checks: read to read the commit check runs.'
Assert-Policy ($release -match '(?ms)fetch-depth:\s*0') 'the release checkout needs full history for the main-ancestry proof.'
$dispatchInputs = [regex]::Match($release, '(?ms)workflow_dispatch:\s*\n\s*inputs:\s*\n(?<body>.*?)(?=^permissions:)').Groups['body'].Value
$inputNames = @([regex]::Matches($dispatchInputs, '(?m)^      ([A-Za-z0-9_-]+):\s*$') | ForEach-Object { $_.Groups[1].Value })
Assert-Policy ($inputNames.Count -gt 0 -and (@($inputNames | Where-Object { $_ -ne 'tag' }).Count -eq 0)) "workflow_dispatch may only accept the tag (found: $($inputNames -join ', ')); no skip/force/bypass input."
$gateBlock = $release.Substring($gateIndex, $release.IndexOf('name: Upgrade npm for Trusted Publishing') - $gateIndex)
Assert-Policy ($gateBlock -notmatch '(?m)^\s*if:') 'the release gate step must be unconditional.'
$verifier = Get-Content -LiteralPath (Join-Path $ciScripts 'verify-release-commit.ps1') -Raw
Assert-Policy ($verifier -notmatch '(?i)\b(skip|bypass|force|override)\b\s*[\]=)]') 'the release verifier must not expose a bypass switch.'

# ---- Behavior ---------------------------------------------------------------

function Invoke-CiScript([string]$Script, [hashtable]$Arguments) {
    $argumentList = @('-NoProfile', '-File', (Join-Path $ciScripts $Script))
    foreach ($key in $Arguments.Keys) { $argumentList += "-$key"; $argumentList += [string]$Arguments[$key] }
    $output = & pwsh @argumentList 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-ci-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # classify-sdk-evidence.ps1
    function Assert-Classification([string]$Name, [hashtable]$Arguments, [string]$Expected) {
        $outputFile = Join-Path $work "$($Name -replace '\W', '_').out"
        # -Command keeps ChangedFile a real array; -File would pass "a,b" as one string.
        $invocation = "& '$(Join-Path $ciScripts 'classify-sdk-evidence.ps1')' -GitHubOutput '$outputFile'"
        foreach ($key in $Arguments.Keys) {
            $values = @([string]$Arguments[$key] -split ',' | ForEach-Object { "'$_'" })
            $invocation += " -$key $($values -join ',')"
        }
        $output = & pwsh -NoProfile -Command $invocation 2>&1 | Out-String
        Assert-Policy ($LASTEXITCODE -eq 0) "classify '$Name' exited $($LASTEXITCODE): $output"
        $written = Get-Content -LiteralPath $outputFile -Raw
        Assert-Policy ($written -match "(?m)^sdk_evidence_required=$Expected\s*$") "classify '$Name' expected $Expected, got: $written"
    }
    Assert-Classification 'default-flip' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/EngineBackendSettings.cs,docs/x.md' } 'true'
    Assert-Classification 'registry' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/EngineCapabilityRegistry.cs' } 'true'
    Assert-Classification 'client' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/GxEngineClient.cs' } 'true'
    Assert-Classification 'configuration' @{ EventName = 'pull_request'; ChangedFile = 'src/GxMcp.Gateway/Configuration.cs' } 'true'
    Assert-Classification 'backslash-path' @{ EventName = 'pull_request'; ChangedFile = 'src\GxMcp.Gateway\EngineRoutedOperations.cs' } 'true'
    Assert-Classification 'unrelated' @{ EventName = 'pull_request'; ChangedFile = 'README.md,src/GxMcp.Worker/Services/PropertyService.cs,src/GxMcp.Gateway.Tests/EngineBackendTests.cs' } 'false'
    Assert-Classification 'no-event' @{ EventName = 'workflow_dispatch'; BaseSha = 'a'; HeadSha = 'b' } 'true'
    Assert-Classification 'missing-shas' @{ EventName = 'pull_request' } 'true'
    Assert-Classification 'diff-failure' @{ EventName = 'pull_request'; BaseSha = ('0' * 40); HeadSha = ('1' * 40) } 'true'

    # assert-ci-gate.ps1
    $commit = 'a' * 40
    function New-NeedsJson([hashtable]$Overrides = @{}, [string]$SdkResult = 'skipped', [string]$SdkState = '', [string]$SdkCommit = '') {
        $needs = [ordered]@{}
        foreach ($lane in @('changes', 'dotnet', 'toolchain', 'rust', 'rust-engine', 'contracts')) {
            $needs[$lane] = @{ result = $(if ($Overrides.ContainsKey($lane)) { $Overrides[$lane] } else { 'success' }); outputs = @{} }
        }
        $needs['sdk-validation'] = @{ result = $SdkResult; outputs = @{ state = $SdkState; commit = $SdkCommit } }
        $needs | ConvertTo-Json -Depth 5 -Compress
    }
    function Assert-Gate([string]$Name, [string]$NeedsJson, [hashtable]$Arguments, [int]$ExpectedExit, [string]$ExpectedText = '') {
        $summary = Join-Path $work "$($Name -replace '\W', '_').summary"
        $all = @{ NeedsJson = $NeedsJson; EventName = 'pull_request'; Repository = 'owner/repo'; HeadRepository = 'owner/repo'; ExpectedCommit = $commit; SummaryPath = $summary }
        foreach ($key in $Arguments.Keys) { $all[$key] = $Arguments[$key] }
        $result = Invoke-CiScript 'assert-ci-gate.ps1' $all
        Assert-Policy ($result.ExitCode -eq $ExpectedExit) "gate '$Name' expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
        if ($ExpectedText) { Assert-Policy ($result.Output.Contains($ExpectedText)) "gate '$Name' output lacks '$ExpectedText': $($result.Output)" }
    }
    $passLane = New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit $commit
    Assert-Gate 'unrelated-optional-lane-skipped' (New-NeedsJson) @{ SdkEvidenceRequired = 'false'; SdkEnabled = '' } 0 'not required'
    Assert-Gate 'unrelated-optional-lane-failed' (New-NeedsJson -SdkResult 'failure' -SdkState 'fail') @{ SdkEvidenceRequired = 'false'; SdkEnabled = 'true' } 0
    Assert-Gate 'rust-engine-failed' (New-NeedsJson @{ 'rust-engine' = 'failure' }) @{ SdkEvidenceRequired = 'false' } 1 'rust-engine=failure'
    Assert-Gate 'lane-skipped-is-not-success' (New-NeedsJson @{ contracts = 'skipped' }) @{ SdkEvidenceRequired = 'false' } 1 'contracts=skipped'
    Assert-Gate 'classification-missing' (New-NeedsJson) @{ SdkEvidenceRequired = '' } 1 'sdk_evidence_required'
    Assert-Gate 'backend-change-no-runner' (New-NeedsJson) @{ SdkEvidenceRequired = 'true'; SdkEnabled = '' } 1 'GXMCP_SDK_CI_ENABLED'
    Assert-Gate 'backend-change-lane-skipped' (New-NeedsJson) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "concluded 'skipped'"
    Assert-Gate 'backend-change-lane-failed' (New-NeedsJson -SdkResult 'failure' -SdkState 'fail') @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "concluded 'failure'"
    Assert-Gate 'backend-change-skip-state' (New-NeedsJson -SdkResult 'success' -SdkState 'skip' -SdkCommit $commit) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 "state 'skip'"
    Assert-Gate 'backend-change-wrong-commit' (New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit ('b' * 40)) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'bound to commit'
    Assert-Gate 'backend-change-unknown-commit' (New-NeedsJson -SdkResult 'success' -SdkState 'pass' -SdkCommit 'unknown') @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'bound to commit'
    Assert-Gate 'backend-change-fork' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true'; HeadRepository = 'fork/repo' } 1 'fork pull request'
    Assert-Gate 'backend-change-fork-unknown' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true'; HeadRepository = '' } 1 'fork pull request'
    Assert-Gate 'backend-change-proven' $passLane @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 0 'pass'
    Assert-Gate 'backend-change-other-lane-failed' (New-NeedsJson @{ dotnet = 'failure' } 'success' 'pass' $commit) @{ SdkEvidenceRequired = 'true'; SdkEnabled = 'true' } 1 'dotnet=failure'

    # verify-release-commit.ps1 against a throwaway repository
    $repo = Join-Path $work 'repo'
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    function Invoke-TestGit([string[]]$Arguments) {
        $output = & git -C $repo -c user.name=policy -c user.email=policy@example.invalid @Arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $output" }
        return $output.Trim()
    }
    Invoke-TestGit @('init', '-q', '-b', 'main') | Out-Null
    'one' | Set-Content -LiteralPath (Join-Path $repo 'a.txt')
    Invoke-TestGit @('add', '.') | Out-Null
    Invoke-TestGit @('commit', '-q', '-m', 'release') | Out-Null
    $releaseCommit = Invoke-TestGit @('rev-parse', 'HEAD')
    Invoke-TestGit @('tag', 'v1.0.0') | Out-Null
    Invoke-TestGit @('update-ref', 'refs/remotes/origin/main', $releaseCommit) | Out-Null
    Invoke-TestGit @('checkout', '-q', '-b', 'candidate') | Out-Null
    'two' | Set-Content -LiteralPath (Join-Path $repo 'b.txt')
    Invoke-TestGit @('add', '.') | Out-Null
    Invoke-TestGit @('commit', '-q', '-m', 'unmerged') | Out-Null
    $unmergedCommit = Invoke-TestGit @('rev-parse', 'HEAD')
    Invoke-TestGit @('tag', 'v1.0.1') | Out-Null

    function New-CheckRuns([string]$Sha, [object[]]$Runs) {
        $path = Join-Path $work ([guid]::NewGuid().ToString('N') + '.ndjson')
        ($Runs | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 }) | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }
    function New-Run([string]$Sha, [string]$Name = 'test', [int]$AppId = 15368, [string]$Status = 'completed', [string]$Conclusion = 'success', [string]$Started = '2026-10-21T10:00:00Z') {
        @{ name = $Name; app = @{ id = $AppId }; status = $Status; conclusion = $Conclusion; started_at = $Started; head_sha = $Sha }
    }
    function Assert-Release([string]$Name, [string]$Sha, [string]$Tag, [object[]]$Runs, [int]$ExpectedExit, [string]$ExpectedText = '') {
        $result = Invoke-CiScript 'verify-release-commit.ps1' @{ Commit = $Sha; Tag = $Tag; CheckRunsPath = (New-CheckRuns $Sha $Runs); Repository = $repo }
        Assert-Policy ($result.ExitCode -eq $ExpectedExit) "release '$Name' expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
        if ($ExpectedText) { Assert-Policy ($result.Output.Contains($ExpectedText)) "release '$Name' output lacks '$ExpectedText': $($result.Output)" }
    }
    Assert-Release 'verified' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit) 0 'verified'
    Assert-Release 'no-test-check' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Name 'dotnet') 1 "no 'test' check run"
    Assert-Release 'no-checks' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit -Name 'other')) 1 "no 'test' check run"
    Assert-Release 'forged-app' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -AppId 1) 1 "no 'test' check run"
    Assert-Release 'check-failed' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Conclusion 'failure') 1 'completed/success'
    Assert-Release 'check-running' $releaseCommit 'v1.0.0' @(New-Run $releaseCommit -Status 'in_progress' -Conclusion '') 1 'completed/success'
    Assert-Release 'rerun-failed-after-success' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit), (New-Run $releaseCommit -Conclusion 'failure' -Started '2026-10-21T11:00:00Z')) 1 'completed/success'
    Assert-Release 'rerun-passed-after-failure' $releaseCommit 'v1.0.0' @((New-Run $releaseCommit -Conclusion 'failure'), (New-Run $releaseCommit -Started '2026-10-21T11:00:00Z')) 0
    Assert-Release 'check-for-other-sha' $releaseCommit 'v1.0.0' @(New-Run $unmergedCommit) 1 'not'
    Assert-Release 'tag-points-elsewhere' $releaseCommit 'v1.0.1' @(New-Run $releaseCommit) 1 'not the checked-out'
    Assert-Release 'not-on-main' $unmergedCommit 'v1.0.1' @(New-Run $unmergedCommit) 1 'not reachable'
    Assert-Release 'unknown-tag' $releaseCommit 'v9.9.9' @(New-Run $releaseCommit) 1 'does not resolve'
    Assert-Release 'abbreviated-commit' $releaseCommit.Substring(0, 12) 'v1.0.0' @(New-Run $releaseCommit) 1 'full SHA'

    # check-line-endings.ps1
    function New-LineEndingRepo([string]$Name, [string]$Attributes) {
        $dir = Join-Path $work $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        & git -C $dir init -q -b main
        & git -C $dir config core.autocrlf false
        [IO.File]::WriteAllBytes((Join-Path $dir 'crlf.txt'), [Text.Encoding]::ASCII.GetBytes("a`r`nb`r`n"))
        [IO.File]::WriteAllBytes((Join-Path $dir 'lf.txt'), [Text.Encoding]::ASCII.GetBytes("a`nb`n"))
        & git -C $dir add crlf.txt lf.txt
        if ($Attributes) {
            [IO.File]::WriteAllText((Join-Path $dir '.gitattributes'), $Attributes)
            & git -C $dir add .gitattributes
        }
        return $dir
    }
    $violating = New-LineEndingRepo 'eol-violation' ''
    & git -C $violating -c user.name=policy -c user.email=policy@example.invalid commit -q -m 'crlf in index'
    [IO.File]::WriteAllText((Join-Path $violating '.gitattributes'), "* text=auto eol=lf`n")
    & git -C $violating add .gitattributes
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $violating }
    Assert-Policy ($result.ExitCode -eq 1 -and $result.Output.Contains('crlf.txt (i/crlf)')) "CRLF text blob was not rejected: $($result.Output)"
    $exempt = New-LineEndingRepo 'eol-exempt' "* text=auto eol=lf`ncrlf.txt -text`n"
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $exempt }
    Assert-Policy ($result.ExitCode -eq 0) "A -text CRLF file must be exempt: $($result.Output)"
    $clean = New-LineEndingRepo 'eol-clean' "* text=auto eol=lf`n"
    & git -C $clean rm -q --cached crlf.txt
    $result = Invoke-CiScript 'check-line-endings.ps1' @{ Repository = $clean }
    Assert-Policy ($result.ExitCode -eq 0) "LF-only repository must pass: $($result.Output)"
    $result = Invoke-CiScript 'check-line-endings.ps1' @{}
    Assert-Policy ($result.ExitCode -eq 0) "This repository must have no CRLF text blobs: $($result.Output)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'ci-workflow-policy: gate dependencies, backend-change evidence, fork/skip refusal, release commit verification and line endings passed' -ForegroundColor Green
