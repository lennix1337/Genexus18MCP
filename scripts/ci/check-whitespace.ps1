[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha,
    # Blocking phase (#757): findings fail the job instead of only warning.
    [switch]$Enforce,
    # Test seam: the repository whose base...head diff is checked.
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

# Whitespace gate (#757): runs `git diff --check` over the lines a pull
# request adds. Findings become ::warning annotations; they fail the job only with -Enforce;
# anything that is not "clean" or "findings with parseable diagnostics" is a fatal
# error, so a broken diff can never be read as a clean one.
#   exit 0, no output          -> clean
#   exit 2 + parseable lines   -> findings (warnings; exit 0, or exit 1 with -Enforce)
#   anything else              -> throw

$ErrorActionPreference = 'Stop'

if ($EventName -ne 'pull_request') {
    Write-Host "::notice title=whitespace check::Skipped: event '$EventName' is not a pull_request, so there is no base...head diff to check."
    exit 0
}
if ([string]::IsNullOrWhiteSpace($BaseSha) -or [string]::IsNullOrWhiteSpace($HeadSha)) {
    throw 'whitespace check: a pull_request event needs both BaseSha and HeadSha.'
}

foreach ($rev in @($BaseSha, $HeadSha)) {
    & git -C $Repository rev-parse --verify --quiet "$rev^{commit}" *> $null
    if ($LASTEXITCODE -ne 0) { throw "whitespace check: '$rev' is not a commit in $Repository." }
}
$mergeBase = & git -C $Repository merge-base $BaseSha $HeadSha 2>&1
if ($LASTEXITCODE -ne 0) { throw "whitespace check: no merge base for $BaseSha...$HeadSha ($($mergeBase -join ' ')); the checkout needs full history." }

$output = @(& git -C $Repository diff --check --no-renames "$BaseSha...$HeadSha" 2>&1)
$code = $LASTEXITCODE

if ($code -eq 0 -and $output.Count -eq 0) {
    Write-Host 'whitespace check: clean.'
    exit 0
}

$findings = New-Object System.Collections.Generic.List[object]
foreach ($line in $output) {
    # Diagnostics are "<path>:<line>: <message>."; the echoed content lines start with '+'.
    if ("$line" -match '^(.*):(\d+): (.+)$') {
        $findings.Add([pscustomobject]@{ Path = $Matches[1]; Line = [int]$Matches[2]; Message = $Matches[3] })
    }
}

if ($code -ne 2 -or $findings.Count -eq 0) {
    throw "whitespace check: unclassifiable 'git diff --check' result (exit $code): $($output -join ' | ')"
}

foreach ($f in $findings) {
    # No line= property: a path with ':' or a rename must not misplace the annotation.
    Write-Host "::warning file=$($f.Path),title=whitespace::line $($f.Line): $($f.Message)"
}
if ($Enforce) {
    Write-Host "::error title=whitespace::whitespace check: $($findings.Count) finding(s) on added lines; fix them (git diff --check)."
    exit 1
}
Write-Host "whitespace check: $($findings.Count) advisory finding(s); not blocking."
exit 0
