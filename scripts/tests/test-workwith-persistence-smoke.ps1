$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Issue #590 follow-up (native proof on a GeneXus 18 U16 test KB): the optional action lane
# of workwith_persistence_smoke.ps1 sent add_action without `parameters`, so any Procedure whose
# parm() rule takes arguments failed with ArityMismatch before the durability reread. These
# guards parse the script (no Knowledge Base, no Gateway) and pin the pass-through.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\workwith_persistence_smoke.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "workwith_persistence_smoke.ps1 does not parse: $($errors[0].Message)" }

$parameters = @($ast.ParamBlock.Parameters)
$actionParameters = @($parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ActionParameters' })
if ($actionParameters.Count -ne 1) { throw 'The smoke must declare -ActionParameters so a Procedure with a parm() rule can be used.' }
if ($actionParameters[0].StaticType -ne [string[]]) { throw '-ActionParameters must be a string[].' }
if (@($parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Procedure' }).Count -ne 1) { throw 'The optional action lane parameter -Procedure is missing.' }

# The add_action arguments must be a variable that can carry `parameters`, and `parameters`
# must be assigned from -ActionParameters (a literal hashtable without the key was the defect).
$assignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
    $node.Left.Target.Extent.Text -eq '$addActionArgs' -and
    $node.Left.Index.Extent.Text -match "^['""]parameters['""]$"
}, $true))
if ($assignments.Count -ne 1) { throw 'add_action must receive `parameters` through $addActionArgs[''parameters''].' }
if ($assignments[0].Right.Extent.Text -notmatch '\$ActionParameters') { throw '`parameters` must be taken from $ActionParameters.' }

$invocations = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Invoke-Tool' -and
    $node.Extent.Text -match "add_action|addActionArgs"
}, $true))
$addActionCall = @($invocations | Where-Object { $_.Extent.Text -match '\$addActionArgs' })
if ($addActionCall.Count -ne 1) { throw 'The add_action Invoke-Tool call must pass $addActionArgs.' }

# The documented example must not advertise a Procedure lane that cannot work with parameters.
$help = $ast.GetHelpContent()
if ($null -eq $help -or $help.Notes -notmatch 'ArityMismatch') { throw 'The smoke help must explain ArityMismatch and -ActionParameters.' }

# Issue #645: the smoke removes the filter attribute it creates. These guards are cheap structure
# checks on the script text (no Knowledge Base, no Gateway); each one is mutated below to prove it
# can fail. The live proof (write, restart, cleanup, restart, equality with the baseline) needs a
# disposable KB and is run separately.
function Get-SmokeScriptProblems([string]$Text) {
    $problems = @()
    $parseTokens = $null; $parseErrors = $null
    $tree = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$parseTokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { return @("does not parse: $($parseErrors[0].Message)") }

    $functions = @($tree.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
    $cleanupFunction = @($functions | Where-Object { $_.Name -eq 'Invoke-SmokeCleanup' })
    $isInsideFunction = {
        param($node)
        for ($parent = $node.Parent; $null -ne $parent; $parent = $parent.Parent) {
            if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $true }
        }
        return $false
    }

    # The removal is in the single cleanup function, and the main path calls it.
    if ($cleanupFunction.Count -ne 1) { return $problems + 'Invoke-SmokeCleanup must exist exactly once.' }
    $removeLiteral = @($cleanupFunction[0].FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value -eq 'remove_filter_attribute'
    }, $true))
    if ($removeLiteral.Count -eq 0) { $problems += 'Invoke-SmokeCleanup must call remove_filter_attribute.' }
    $mainCleanupCalls = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-SmokeCleanup' -and -not (& $isInsideFunction $n)
    }, $true))
    if ($mainCleanupCalls.Count -ne 1) { $problems += "the success path must call Invoke-SmokeCleanup once (found $($mainCleanupCalls.Count))." }

    # Only what this run created is removed, and only the condition text it sent.
    if ($Text -notmatch "removeConditions'\]\s*=\s*@\(\`$Condition\)") { $problems += 'removeConditions must carry exactly $Condition.' }
    if ($Text -notmatch 'conditionPresentBefore') { $problems += 'the condition must be passed only while it is still on the filter.' }

    # The ownership flag is set before the add call (an attempt can persist even when it reports failure).
    $flag = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $n.Left.Extent.Text -eq '$script:State.FilterWritten' -and $n.Right.Extent.Text -eq '$true'
    }, $true))
    $addCall = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-Tool' -and
        $n.Extent.Text -match "action\s*=\s*'add_filter_attribute'"
    }, $true))
    if ($flag.Count -ne 1) { $problems += 'FilterWritten must be set to $true exactly once.' }
    if ($addCall.Count -ne 1) { $problems += 'add_filter_attribute must be called exactly once.' }
    if ($flag.Count -eq 1 -and $addCall.Count -eq 1 -and $flag[0].Extent.StartOffset -gt $addCall[0].Extent.StartOffset) {
        $problems += 'FilterWritten must be set before add_filter_attribute is called.'
    }
    # A name that already exists in the baseline stops the run before the write.
    $exists = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Exit-Fail' -and $n.Extent.Text -match 'already exists'
    }, $true))
    if ($exists.Count -ne 1) { $problems += 'a baseline attribute with the same name must stop the run (Exit-Fail).' }
    elseif ($addCall.Count -eq 1 -and $exists[0].Extent.StartOffset -gt $addCall[0].Extent.StartOffset) { $problems += 'the ownership check must precede the write.' }

    # One summary, written after the cleanup; one restart after the write and one after the cleanup.
    $summaryCalls = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Summary'
    }, $true))
    if ($summaryCalls.Count -ne 1) { $problems += "Write-Summary must be called exactly once (found $($summaryCalls.Count))." }
    $restarts = @($tree.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-WorkerRestart' -and -not (& $isInsideFunction $n)
    }, $true))
    if ($restarts.Count -ne 2) { $problems += "the success path must restart the Worker exactly twice (found $($restarts.Count))." }

    # The finalizer, not an exit inside try, ends the run: Exit-Fail/Exit-Unavailable throw, and
    # `exit` appears neither in a function nor in a try block.
    foreach ($name in @('Exit-Fail', 'Exit-Unavailable')) {
        $function = @($functions | Where-Object { $_.Name -eq $name })
        if ($function.Count -ne 1) { $problems += "$name must exist exactly once."; continue }
        $throws = @($function[0].FindAll({ param($n) $n -is [System.Management.Automation.Language.ThrowStatementAst] }, $true))
        if ($throws.Count -eq 0) { $problems += "$name must throw." }
    }
    $exits = @($tree.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExitStatementAst] }, $true))
    foreach ($exitStatement in $exits) {
        if (& $isInsideFunction $exitStatement) { $problems += "exit at line $($exitStatement.Extent.StartLineNumber) is inside a function." }
        for ($parent = $exitStatement.Parent; $null -ne $parent; $parent = $parent.Parent) {
            if ($parent -is [System.Management.Automation.Language.TryStatementAst]) { $problems += "exit at line $($exitStatement.Extent.StartLineNumber) is inside a try block."; break }
        }
    }

    # The documented states exist.
    foreach ($state in @("durability   = 'not_attempted'", "cleanup      = 'not_attempted'", "'unverified'")) {
        if (-not $Text.Contains($state)) { $problems += "the summary must carry $state." }
    }
    return $problems
}

$smokeText = Get-Content -LiteralPath $scriptPath -Raw
$smokeProblems = @(Get-SmokeScriptProblems $smokeText)
if ($smokeProblems.Count -gt 0) { throw "workwith_persistence_smoke.ps1 violates the #645 cleanup contract: $($smokeProblems -join '; ')" }

# Mutations: every guard above must be able to fail.
function Assert-Detected([string]$Name, [string]$Mutated, [string]$Expected) {
    if ($Mutated -ceq $smokeText) { throw "mutation setup failed: $Name did not change the script." }
    $found = @(Get-SmokeScriptProblems $Mutated)
    if (-not ($found | Where-Object { $_ -like "*$Expected*" })) {
        throw "mutation '$Name' was not detected (wanted '$Expected', got: $($found -join '; '))."
    }
}
Assert-Detected 'no remove_filter_attribute' ($smokeText.Replace("action = 'remove_filter_attribute'", "action = 'list_filters'")) 'must call remove_filter_attribute'
Assert-Detected 'cleanup never called' ($smokeText.Replace('$cleanup = Invoke-SmokeCleanup -IncludeAction ([bool]$actionLane)', '$cleanup = [pscustomobject]@{ errors = @(); receiptVerified = $true }')) 'must call Invoke-SmokeCleanup once'
Assert-Detected 'flag never set' ($smokeText.Replace('$script:State.FilterWritten = $true', '$script:State.FilterWritten = $false')) 'FilterWritten must be set'
$flagAfterAdd = $smokeText.Replace('    $script:State.FilterWritten = $true' + "`n", '').Replace('    Assert-Ok -Payload $add -Label ''add_filter_attribute''', '    $script:State.FilterWritten = $true' + "`n" + '    Assert-Ok -Payload $add -Label ''add_filter_attribute''')
Assert-Detected 'flag after the add' $flagAfterAdd 'before add_filter_attribute'
Assert-Detected 'second summary' ($smokeText + "`nWrite-Summary`n") 'Write-Summary must be called exactly once'
Assert-Detected 'exit inside Exit-Fail' ($smokeText.Replace("throw (New-SmokeStop 'fail' `$Reason)", 'exit 1')) 'Exit-Fail must throw'
Assert-Detected 'extra restart' ($smokeText.Replace("`$script:Summary.durability = 'pass'", "`$script:Summary.durability = 'pass'`n    `$null = Invoke-WorkerRestart -Label 'extra'")) 'exactly twice'
Assert-Detected 'removes every observed condition' ($smokeText.Replace("`$removeArgs['removeConditions'] = @(`$Condition)", "`$removeArgs['removeConditions'] = @(`$read.result.filters.conditions)")) 'removeConditions must carry exactly'

Write-Host 'workwith-persistence-smoke: -ActionParameters is declared, forwarded as add_action parameters and documented'
Write-Host 'workwith-persistence-smoke: the cleanup contract (#645) holds and every guard fails under mutation'
