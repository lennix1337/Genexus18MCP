$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts\Test-DomainVariableRoundTrip.ps1'
$variable = 'GXMCP_TEST_PROTECTED_KB_PATTERN'
$failures = @()

# The KB guard of the destructive regression runs before anything on disk is read and before any Worker is started,
# so nothing here needs a GeneXus installation or a Knowledge Base, and nothing is created under C:\KBs: the paths
# inside the rule are names that do not exist.

function Get-ScriptFunctionDefinition([string] $Path, [string] $Name) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $errors)
    if ($errors.Count) { throw $errors[0] }
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
    if (-not $definition) { throw "Missing production function in $(Split-Path -Leaf $Path): $Name" }
    return $definition.Extent.Text
}

function Get-Outcome([scriptblock] $Action) {
    try { return [pscustomobject]@{ value = [string] (& $Action); error = '' } }
    catch { return [pscustomobject]@{ value = ''; error = [string] $_.Exception.Message } }
}

$ruleRefusal = "only disposable test KBs directly under C:\\KBs whose name contains 'teste' are allowed"
$patternRefusal = 'it matches the protected KB path pattern'
$patternInvalid = 'ProtectedKbPathPattern is not a valid regular expression'
$notAKb = 'Not a GeneXus KB directory'

# --- The path rule, called directly -------------------------------------------
. ([scriptblock]::Create((Get-ScriptFunctionDefinition $script 'Resolve-DisposableKbPath')))
# The repository's existing disposable-KB guard is the oracle, as in test-live-shared-host.test.ps1.
. ([scriptblock]::Create((Get-ScriptFunctionDefinition (Join-Path $root 'scripts\sdk-local-build.ps1') 'Resolve-TestKbPath')))

foreach ($sample in @('C:\KBs\KBTeste', 'c:\kbs\kbteste\', 'C:\KBs\KBTeste17', 'C:\KBs\Production',
        'C:\KBs\nested\KBTeste', 'D:\KBs\KBTeste', 'C:\KBs\KBTeste\..\Production', 'C:\KBsX\KBTeste',
        'C:\KBs', 'C:\KBs\Production\..\KBTesteTwo', 'C:\KBs\KBTeste\...', 'C:/KBs/KBTeste')) {
    $oracle = Get-Outcome { Resolve-TestKbPath $sample }
    $actual = Get-Outcome { Resolve-DisposableKbPath $sample '' }
    if ($actual.value -cne $oracle.value -or $actual.error -cne $oracle.error) {
        $failures += "The rule must agree with Resolve-TestKbPath in sdk-local-build.ps1 for '$sample': expected [$($oracle.value)|$($oracle.error)], got [$($actual.value)|$($actual.error)]."
    }
}
# Two fixed points, so that the comparison above cannot pass with an oracle that accepts or refuses everything.
if ((Get-Outcome { Resolve-DisposableKbPath 'C:\KBs\KBTeste' '' }).value -cne 'C:\KBs\KBTeste') {
    $failures += 'A directory directly under C:\KBs whose name contains teste must be accepted and returned as its full path.'
}
if ((Get-Outcome { Resolve-DisposableKbPath 'C:\KBs\Production' '' }).error -notmatch $ruleRefusal) {
    $failures += 'A directory under C:\KBs whose name does not contain teste must be refused by the rule.'
}

# The pattern only adds refusals. Each row: path, pattern, and the refusal expected ('' = accepted).
$inside = 'C:\KBs\KBTesteGuarded'
foreach ($row in @(
        @{ name = 'pattern that matches, in another case'; path = $inside; pattern = 'kbtesteGUARDED'; refusal = $patternRefusal },
        @{ name = 'pattern that does not match'; path = $inside; pattern = 'another-area'; refusal = '' },
        @{ name = 'blank pattern'; path = $inside; pattern = '   '; refusal = '' },
        @{ name = 'pattern that does not parse'; path = $inside; pattern = 'guarded('; refusal = $patternInvalid },
        @{ name = 'pattern matched against the full path'; path = 'C:\KBs\.\KBTesteGuarded'; pattern = '^C:\\KBs\\KBTesteGuarded$'; refusal = $patternRefusal },
        @{ name = 'outside the rule, pattern that does not match'; path = 'D:\Elsewhere\KBTeste'; pattern = 'another-area'; refusal = $ruleRefusal },
        @{ name = 'outside the rule, pattern that can match nothing'; path = 'C:\KBs\Production'; pattern = '(?!)'; refusal = $ruleRefusal },
        @{ name = 'outside the rule, pattern that does not parse'; path = 'C:\KBs\Production'; pattern = 'guarded('; refusal = $ruleRefusal }
    )) {
    $outcome = Get-Outcome { Resolve-DisposableKbPath $row.path $row.pattern }
    if ($row.refusal) {
        if ($outcome.error -notmatch $row.refusal) { $failures += "Rule with a $($row.name): expected /$($row.refusal)/, got [$($outcome.value)|$($outcome.error)]." }
    }
    elseif ($outcome.error -or $outcome.value -cne $inside) {
        $failures += "Rule with a $($row.name): expected the path to be accepted, got [$($outcome.value)|$($outcome.error)]."
    }
}

# --- The script itself ---------------------------------------------------------
$unique = [Guid]::NewGuid().ToString('N')
$outsideName = 'gxmcp-roundtrip-guard-' + $unique
$openName = 'KBTesteRoundTripOpen-' + $unique
$guardedName = 'KBTesteRoundTripGuarded-' + $unique
$temp = Join-Path ([IO.Path]::GetTempPath()) $outsideName
$noWorker = Join-Path $temp 'no-worker.exe'
$insideRule = 'C:\KBs\' + $openName
$insideRuleGuarded = 'C:\KBs\' + $guardedName

# Runs the script in this process and returns the message it stopped with. Every case stops at a refusal; a run
# that went past them would stop at the Worker executable, which does not exist.
function Invoke-RoundTrip([hashtable] $Arguments, $EnvironmentPattern) {
    $previous = [Environment]::GetEnvironmentVariable($variable)
    try {
        # Untyped on purpose: a null value removes the variable.
        [Environment]::SetEnvironmentVariable($variable, $EnvironmentPattern)
        try {
            & $script -WorkerExe $noWorker -GeneXusPath $temp @Arguments -ConfirmDisposableKbs | Out-Null
            return 'The script returned without stopping.'
        }
        catch { return [string] $_.Exception.Message }
    }
    finally { [Environment]::SetEnvironmentVariable($variable, $previous) }
}

# $About is the name of the directory the stop must be about: names are unique, and unlike a whole path they do
# not depend on how the temporary directory is spelled.
function Expect-Stop([string] $Name, [string] $Message, [string] $Pattern, [string] $About) {
    if ($Message -notmatch $Pattern) { $script:failures += "$Name did not stop with /$Pattern/: $Message" }
    elseif ($About -and -not $Message.Contains($About)) { $script:failures += "$Name did not stop for '$About': $Message" }
}

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null

    # The default, with no pattern from either source: outside the rule is refused, inside goes on to the next check.
    Expect-Stop 'A KB outside the rule, no pattern' (Invoke-RoundTrip @{ SourceKbPath = $temp } $null) $ruleRefusal $outsideName
    Expect-Stop 'A KB inside the rule, no pattern' (Invoke-RoundTrip @{ SourceKbPath = $insideRule } $null) $notAKb $openName

    # The second KB is judged by the same rule, before the first one is looked up on disk.
    Expect-Stop 'Import KB outside the rule' (Invoke-RoundTrip @{ SourceKbPath = $insideRule; ImportKbPath = $temp } $null) `
        $ruleRefusal $outsideName

    # The pattern adds refusals inside the rule, from the parameter or from its environment default, for both KBs.
    Expect-Stop 'Pattern from the parameter' `
        (Invoke-RoundTrip @{ SourceKbPath = $insideRuleGuarded; ProtectedKbPathPattern = 'roundtripGUARDED' } $null) `
        $patternRefusal $guardedName
    Expect-Stop 'Pattern from the environment' (Invoke-RoundTrip @{ SourceKbPath = $insideRuleGuarded } 'roundtripguarded') `
        $patternRefusal $guardedName
    Expect-Stop 'Import KB under the pattern' `
        (Invoke-RoundTrip @{ SourceKbPath = $insideRule; ImportKbPath = $insideRuleGuarded; ProtectedKbPathPattern = 'roundtripguarded' } $null) `
        $patternRefusal $guardedName
    Expect-Stop 'Pattern that does not parse' `
        (Invoke-RoundTrip @{ SourceKbPath = $insideRule; ProtectedKbPathPattern = 'roundtripguarded(' } $null) $patternInvalid ''

    # Nothing about the pattern relaxes the rule: not an explicit empty parameter, not an environment value.
    Expect-Stop 'Explicit empty pattern, KB outside the rule' `
        (Invoke-RoundTrip @{ SourceKbPath = $temp; ProtectedKbPathPattern = '' } 'another-area') $ruleRefusal $outsideName
    # An explicit parameter replaces the environment pattern; the rule is what still applies.
    Expect-Stop 'Parameter replaces the environment pattern' `
        (Invoke-RoundTrip @{ SourceKbPath = $insideRuleGuarded; ProtectedKbPathPattern = 'another-area' } 'roundtripguarded') `
        $notAKb $guardedName

    # After the rule, each KB still has to be a KB directory. Test-Path is shadowed for this one run, so that the
    # first KB counts as one without anything existing under C:\KBs, and the second one is the one that stops it.
    $firstIsAKb = & {
        function Test-Path([string] $LiteralPath) { return $LiteralPath -ieq (Join-Path $insideRule 'knowledgebase.connection') }
        Invoke-RoundTrip @{ SourceKbPath = $insideRule; ImportKbPath = $insideRuleGuarded } $null
    }
    Expect-Stop 'Import KB that is not a KB directory' $firstIsAKb $notAKb $guardedName

    # As the command it is run as: a new process, the variable unset, a KB outside the rule.
    $previous = [Environment]::GetEnvironmentVariable($variable)
    try {
        [Environment]::SetEnvironmentVariable($variable, $null)
        $output = @(& pwsh -NoProfile -File $script -WorkerExe $noWorker -GeneXusPath $temp -SourceKbPath $temp -ConfirmDisposableKbs 2>&1)
        $exit = $LASTEXITCODE
    }
    finally { [Environment]::SetEnvironmentVariable($variable, $previous) }
    if ($exit -eq 0 -or ($output -join "`n") -notmatch 'Refused KB') {
        $failures += "As a command, a KB outside the rule was not refused (exit $exit): $($output -join "`n")"
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'domain-variable-roundtrip guard tests passed: the C:\KBs\*teste* rule with no pattern, both KBs, and a pattern that only adds refusals.'
