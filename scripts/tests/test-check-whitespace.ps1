[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts/ci/check-whitespace.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('gx-whitespace-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $temp 'repo'
$checks = 0

function Assert-Ws([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Invoke-Git([string[]]$Arguments) {
    $result = @(& git -C $repo @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($result -join ' ')" }
    return ($result -join "`n").Trim()
}

function Invoke-Check([string]$Event, [string]$Base, [string]$Head, [string]$Repository = $repo, [switch]$Enforce) {
    $extra = @(if ($Enforce) { '-Enforce' })
    $output = @(& pwsh -NoProfile -File $script -EventName $Event -BaseSha $Base -HeadSha $Head -Repository $Repository @extra 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Log = ($output -join "`n") }
}

try {
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    Invoke-Git @('init', '-q', '-b', 'main') | Out-Null
    Invoke-Git @('config', 'user.email', 'ci@example.invalid') | Out-Null
    Invoke-Git @('config', 'user.name', 'ci') | Out-Null
    Invoke-Git @('config', 'core.autocrlf', 'false') | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'a.js'), "one`n")
    Invoke-Git @('add', '-A') | Out-Null
    Invoke-Git @('commit', '-q', '-m', 'base') | Out-Null
    $base = Invoke-Git @('rev-parse', 'HEAD')

    # Clean change: exit 0, no warning.
    [IO.File]::WriteAllText((Join-Path $repo 'a.js'), "one`ntwo`n")
    Invoke-Git @('commit', '-q', '-am', 'clean') | Out-Null
    $clean = Invoke-Git @('rev-parse', 'HEAD')
    $r = Invoke-Check 'pull_request' $base $clean
    Assert-Ws ($r.ExitCode -eq 0 -and $r.Log -notmatch '::warning') "clean diff must pass without warnings: $($r.Log)"

    # Added trailing whitespace and EOF blank line: advisory warnings, still exit 0.
    [IO.File]::WriteAllText((Join-Path $repo 'a.js'), "one`ntwo`nthree `n`n")
    Invoke-Git @('commit', '-q', '-am', 'dirty') | Out-Null
    $dirty = Invoke-Git @('rev-parse', 'HEAD')
    $r = Invoke-Check 'pull_request' $base $dirty
    Assert-Ws ($r.ExitCode -eq 0) "findings are advisory and must not fail the job: $($r.Log)"
    Assert-Ws ($r.Log -match '::warning file=a\.js,title=whitespace::line 3: trailing whitespace') "missing trailing-whitespace annotation: $($r.Log)"
    Assert-Ws ($r.Log -match 'blank line at EOF') "missing EOF blank-line annotation: $($r.Log)"

    # Blocking phase: the same findings fail the job and still annotate.
    $r = Invoke-Check 'pull_request' $base $dirty -Enforce
    Assert-Ws ($r.ExitCode -ne 0 -and $r.Log -match '::error title=whitespace') "-Enforce must fail on findings: $($r.Log)"
    Assert-Ws ($r.Log -match '::warning file=a\.js,title=whitespace::line 3: trailing whitespace') "-Enforce must keep the annotations: $($r.Log)"

    # A pre-existing blank EOF line is not charged to a PR that does not touch it.
    $r = Invoke-Check 'pull_request' $dirty $dirty
    Assert-Ws ($r.ExitCode -eq 0 -and $r.Log -notmatch '::warning') "an empty diff must be clean: $($r.Log)"

    # Fail closed: an unknown base or a missing SHA is an error, never a clean pass.
    $r = Invoke-Check 'pull_request' ('0' * 40) $dirty
    Assert-Ws ($r.ExitCode -ne 0 -and $r.Log -match 'not a commit') "unknown base must fail closed: $($r.Log)"
    $r = Invoke-Check 'pull_request' '' $dirty
    Assert-Ws ($r.ExitCode -ne 0 -and $r.Log -match 'needs both BaseSha and HeadSha') "missing SHA must fail closed: $($r.Log)"
    $r = Invoke-Check 'pull_request' $base $dirty (Join-Path $temp 'missing-repo')
    Assert-Ws ($r.ExitCode -ne 0) "an unreadable repository must fail closed: $($r.Log)"

    # Non-PR events are skipped with an explicit notice, not silently.
    $r = Invoke-Check 'workflow_dispatch' '' ''
    Assert-Ws ($r.ExitCode -eq 0 -and $r.Log -match '::notice') "non-PR event needs an explicit notice: $($r.Log)"

    Write-Host "Whitespace gate: $checks checks passed."
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
