$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Issue #645: behavior of scripts/workwith_persistence_smoke.ps1 against a stateful fake of the
# Gateway's HTTP MCP endpoint (fake-workwith-mcp-server.py). No Knowledge Base, no GeneXus: this
# proves the control flow of the script - what it writes, removes and restarts, the summary
# states and the exit codes, on the success path and on each failure path - not the behavior of
# the real genexus_workwith tool. The live proof still needs a disposable KB.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$smoke = Join-Path $root 'scripts\workwith_persistence_smoke.ps1'
$serverScript = Join-Path $PSScriptRoot 'fake-workwith-mcp-server.py'

$python = (Get-Command python -ErrorAction SilentlyContinue)
if ($null -eq $python) {
    if ($env:CI) { throw 'Python is required for the smoke behavior test and is missing on CI.' }
    Write-Host 'workwith-persistence-smoke-behavior: skipped (python is not available)'
    exit 0
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "workwith-persistence-smoke behavior: $Message" }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-ww-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$serverOut = Join-Path $work 'server.out'
$serverErr = Join-Path $work 'server.err'
$server = Start-Process -FilePath $python.Source -ArgumentList @($serverScript) -RedirectStandardOutput $serverOut `
    -RedirectStandardError $serverErr -WindowStyle Hidden -PassThru
try {
    $port = $null
    for ($i = 0; $i -lt 100 -and $null -eq $port; $i++) {
        Start-Sleep -Milliseconds 100
        $announced = if (Test-Path -LiteralPath $serverOut) { Get-Content -LiteralPath $serverOut -Raw } else { $null }
        if ($announced) {
            $match = [regex]::Match($announced, 'PORT=(\d+)')
            if ($match.Success) { $port = [int]$match.Groups[1].Value }
        }
    }
    Assert-That ($null -ne $port) 'the fake MCP server did not report its port.'
    $base = "http://127.0.0.1:$port"

    function Set-Scenario([string]$Scenario, [string]$Existing = '') {
        $null = Invoke-RestMethod -Uri "$base/_control" -Method Post -ContentType 'application/json' `
            -Body (@{ scenario = $Scenario; existing = $Existing } | ConvertTo-Json)
    }

    function Get-FakeState { Invoke-RestMethod -Uri "$base/_state" -Method Get }

    $script:RunCounter = 0
    function Invoke-Smoke([string[]]$Arguments, [string]$ScriptPath = $smoke, [string]$BaseUrl = "$base/mcp") {
        $script:RunCounter++
        $summaryPath = Join-Path $work "summary-$($script:RunCounter).json"
        $argumentList = @('-NoProfile', '-File', $ScriptPath, '-BaseUrl', $BaseUrl, '-Kb', 'kbteste', '-Transaction', 'Customer', '-SummaryPath', $summaryPath) + $Arguments
        $output = & pwsh @argumentList 2>&1 | Out-String
        $summary = if (Test-Path -LiteralPath $summaryPath) { Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json } else { $null }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output; Summary = $summary }
    }

    function Get-Calls($State, [string]$Action) { , @($State.log | Where-Object { $_.PSObject.Properties['action'] -and $_.action -eq $Action }) }
    function Get-LiveNames($State) { @($State.live.attributes | ForEach-Object { $_.name }) -join ',' }
    $baselineNames = 'CustomerName,CustomerCity'
    $baselineCondition = 'CustomerName like &CustomerName when not &CustomerName.IsEmpty();'

    function Assert-BackToBaseline($State, [string]$Label) {
        Assert-That ((Get-LiveNames $State) -eq $baselineNames) "$Label`: the live filter attributes are '$(Get-LiveNames $State)', expected the baseline."
        Assert-That (@($State.live.conditions).Count -eq 1 -and @($State.live.conditions)[0] -ceq $baselineCondition) "$Label`: the live conditions are not the baseline."
        Assert-That ((@($State.persisted.attributes | ForEach-Object { $_.name }) -join ',') -eq $baselineNames) "$Label`: the persisted attributes are not the baseline."
    }

    function Assert-SingleSummary($Run, [string]$Label) {
        $count = ([regex]::Matches($Run.Output, '"finishedAtUtc"')).Count
        Assert-That ($count -eq 1) "$Label`: the summary was written $count times, expected once."
    }

    # --- success: write, restart, cleanup, restart, equal to the baseline -----------------
    Set-Scenario 'success'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterA')
    Assert-That ($run.ExitCode -eq 0) "success: exit $($run.ExitCode)`n$($run.Output)"
    Assert-That ($run.Summary.outcome -eq 'pass' -and $run.Summary.durability -eq 'pass' -and $run.Summary.cleanup -eq 'pass') 'success: the summary must be pass/pass/pass.'
    Assert-SingleSummary $run 'success'
    $state = Get-FakeState
    Assert-That ($state.reloads -eq 2) "success: expected exactly 2 Worker restarts, saw $($state.reloads)."
    $removals = Get-Calls $state 'remove_filter_attribute'
    Assert-That ($removals.Count -eq 1) 'success: remove_filter_attribute must be called once.'
    Assert-That ((@($removals[0].args.removeConditions) -join '|') -ceq '&SmokeFilterA.IsEmpty();') 'success: removeConditions must be exactly the condition the run sent.'
    Assert-BackToBaseline $state 'success'

    # --- success with the optional action lane --------------------------------------------
    Set-Scenario 'success'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterB', '-Procedure', 'procPrint')
    Assert-That ($run.ExitCode -eq 0) "action lane: exit $($run.ExitCode)`n$($run.Output)"
    $state = Get-FakeState
    Assert-That ($state.reloads -eq 2) "action lane: expected exactly 2 Worker restarts, saw $($state.reloads)."
    Assert-That ((Get-Calls $state 'add_action').Count -eq 1 -and (Get-Calls $state 'remove_action').Count -eq 1) 'action lane: add_action and remove_action must each run once.'
    Assert-That (@($state.live.actions).Count -eq 0) 'action lane: the action must be gone.'
    Assert-BackToBaseline $state 'action lane'

    # --- ownership: an attribute that already exists is never written or removed ---------
    Set-Scenario 'success' 'ExistingFilter'
    $run = Invoke-Smoke @('-Attribute', '&ExistingFilter')
    Assert-That ($run.ExitCode -eq 1) "attribute exists: exit $($run.ExitCode), expected 1."
    Assert-That ($run.Summary.cleanup -eq 'not_attempted' -and $run.Summary.outcome -eq 'fail') 'attribute exists: nothing was written, so nothing is cleaned.'
    $state = Get-FakeState
    Assert-That ((Get-Calls $state 'add_filter_attribute').Count -eq 0 -and (Get-Calls $state 'remove_filter_attribute').Count -eq 0) 'attribute exists: no write may be sent.'
    Assert-That ($state.reloads -eq 0) 'attribute exists: no restart may happen.'
    Assert-That ((Get-LiveNames $state) -eq "$baselineNames,ExistingFilter") 'attribute exists: the existing attribute must stay.'

    # --- failure after the write: the primary error survives, the finalizer cleans -------
    Set-Scenario 'reload_fails'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterC')
    Assert-That ($run.ExitCode -eq 1) "restart fails: exit $($run.ExitCode), expected 1."
    Assert-That (($run.Summary.errors -join ' ') -match 'WorkerReloadFailed') 'restart fails: the primary error must stay in the summary.'
    Assert-That ($run.Summary.cleanup -eq 'unverified') "restart fails: cleanup must be unverified (no restart), was $($run.Summary.cleanup)."
    Assert-SingleSummary $run 'restart fails'
    $state = Get-FakeState
    Assert-That ((Get-Calls $state 'remove_filter_attribute').Count -eq 1) 'restart fails: the finalizer must remove the attribute once.'
    Assert-That ($state.reloads -eq 1) "restart fails: the finalizer must not add a restart (saw $($state.reloads))."
    Assert-BackToBaseline $state 'restart fails'

    # --- the write did not survive the restart: durability fails, nothing left to remove --
    Set-Scenario 'persist_lost'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterD')
    Assert-That ($run.ExitCode -eq 1 -and $run.Summary.durability -eq 'fail') 'persist lost: the durability check must fail with exit 1.'
    Assert-That ((Get-Calls (Get-FakeState) 'remove_filter_attribute').Count -eq 0) 'persist lost: there is nothing to remove, so no removal may be sent.'

    # --- an unverified removal receipt is never a pass --------------------------------------
    Set-Scenario 'unverified_receipt'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterE')
    Assert-That ($run.ExitCode -eq 1 -and $run.Summary.durability -eq 'pass' -and $run.Summary.cleanup -eq 'unverified') "unverified receipt: expected exit 1, durability pass, cleanup unverified (got $($run.ExitCode)/$($run.Summary.durability)/$($run.Summary.cleanup))."
    Assert-BackToBaseline (Get-FakeState) 'unverified receipt'

    # --- a refused removal is a cleanup failure and is not retried by the finalizer ----------
    Set-Scenario 'remove_refused'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterF')
    Assert-That ($run.ExitCode -eq 1 -and $run.Summary.durability -eq 'pass' -and $run.Summary.cleanup -eq 'fail') 'refused removal: expected exit 1, durability pass, cleanup fail.'
    $state = Get-FakeState
    Assert-That ((Get-Calls $state 'remove_filter_attribute').Count -eq 1) 'refused removal: the finalizer must not clean a second time.'
    Assert-That ($state.reloads -eq 1) 'refused removal: no restart after a failed cleanup.'

    # --- a cleanup that does not survive the restart fails on the absence assertion ----------
    Set-Scenario 'cleanup_not_persisted'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterG')
    Assert-That ($run.ExitCode -eq 1 -and $run.Summary.cleanup -eq 'fail') 'cleanup not persisted: expected exit 1 and cleanup fail.'
    Assert-That (($run.Summary.errors -join ' ') -match 'is still present after remove_filter_attribute') 'cleanup not persisted: the absence assertion must be the failure.'

    # --- a reader change is recorded and not compared ------------------------------------------
    Set-Scenario 'reader_changes'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterH')
    Assert-That ($run.ExitCode -eq 1 -and $run.Summary.cleanup -eq 'unverified') 'reader change: expected exit 1 and cleanup unverified.'
    Assert-That ($run.Summary.evidence.filterAfterCleanup.comparison -like 'not compared*') 'reader change: the summary must record that the values were not compared.'

    # --- preconditions: unavailable (2), before any write ----------------------------------------
    Set-Scenario 'success'
    $null = & pwsh -NoProfile -File $smoke -BaseUrl "$base/mcp" -Kb production -Transaction Customer -SummaryPath (Join-Path $work 'guard.json') 2>&1
    Assert-That ($LASTEXITCODE -eq 2) "disposable-name guard: exit $LASTEXITCODE, expected 2."
    Assert-That (@((Get-FakeState).log).Count -eq 0) 'disposable-name guard: nothing may reach the Gateway.'
    $unreachable = Invoke-Smoke @('-Attribute', 'SmokeFilterJ') -BaseUrl 'http://127.0.0.1:1/mcp'
    Assert-That ($unreachable.ExitCode -eq 2 -and $unreachable.Summary.outcome -eq 'unavailable') 'no Gateway: expected exit 2 (unavailable).'

    # --- mutation: without the cleanup call the absence assertion fails, and the finalizer cleans -
    $text = Get-Content -LiteralPath $smoke -Raw
    $call = '$cleanup = Invoke-SmokeCleanup -IncludeAction ([bool]$actionLane)'
    Assert-That ($text.Contains($call)) 'mutation setup: the cleanup call was not found.'
    $mutated = Join-Path $work 'workwith_persistence_smoke.mutated.ps1'
    Set-Content -LiteralPath $mutated -Value $text.Replace($call, '$cleanup = [pscustomobject]@{ errors = @(); receiptVerified = $true }') -Encoding utf8
    Set-Scenario 'success'
    $run = Invoke-Smoke @('-Attribute', 'SmokeFilterK') -ScriptPath $mutated
    Assert-That ($run.ExitCode -eq 1) 'mutation: a script that never cleans up must fail.'
    Assert-That (($run.Summary.errors -join ' ') -match 'is still present after remove_filter_attribute') 'mutation: the failure must come from the absence assertion.'
    Assert-BackToBaseline (Get-FakeState) 'mutation (finalizer)'
}
finally {
    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'workwith-persistence-smoke-behavior: success, failure, ownership, unavailable and mutation paths behave as documented'
