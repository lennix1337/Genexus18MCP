$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts\validate-engine-tds-live.ps1'
$failures = @()

# -PlanOnly never builds and never opens a Knowledge Base: the path decision is all that runs.
function Invoke-Plan([string]$KbPath) {
    $output = @(& pwsh -NoProfile -File $script -KbPath $KbPath -PlanOnly 2>&1)
    return [pscustomobject]@{ exit = $LASTEXITCODE; text = ($output -join "`n") }
}

# Only a direct child of C:\KBs whose name contains "teste" is a disposable test KB.
foreach ($refused in @(
        'C:\Windows',
        'C:\KBs',
        'C:\KBs\Producao',
        'C:\KBs\KBTeste\..\Producao',
        'C:\KBs\Producao\teste',
        'D:\KBs\KBTeste',
        'C:\KBsteste\KBTeste',
        'KBTeste'
    )) {
    $r = Invoke-Plan $refused
    if ($r.exit -ne 2 -or $r.text -notmatch 'Refused: .* is not a test Knowledge Base') {
        $failures += "'$refused' was not refused with exit 2 (exit $($r.exit)): $($r.text)"
    }
}

foreach ($admitted in @('C:\KBs\KBTesteUnitPlan', 'c:/kbs/kbTESTE18/', 'C:\KBs\Other\..\MinhaTeste')) {
    $r = Invoke-Plan $admitted
    if ($r.exit -ne 0) {
        $failures += "'$admitted' was refused (exit $($r.exit)): $($r.text)"
        continue
    }
    $plan = $r.text | ConvertFrom-Json
    if ($plan.kb -notmatch '^C:\\KBs\\[^\\]*teste[^\\]*$') { $failures += "Plan targets an unexpected path: $($plan.kb)" }
    if (-not $plan.readOnly) { $failures += 'The plan does not declare itself read-only.' }
    # The transport needs its cargo feature: the plan must say which build has it and which does not.
    if ($plan.cargoFeature -ne 'tds' -or $plan.builds[0] -notmatch '--release .*--features tds --example udm_transport_probe') {
        $failures += "The TDS build does not name the 'tds' feature: $($plan.builds[0])"
    }
    if ($plan.builds[1] -match '--features') { $failures += "The fallback build must use the default features: $($plan.builds[1])" }
    $transports = @($plan.probes | ForEach-Object transport) -join ','
    if ($transports -ne 'sqlcmd,tds') { $failures += "The plan does not read through both transports: $transports" }
    foreach ($check in 'sqlcmd-read', 'tds-read', 'catalog-identical', 'parts-identical', 'fallback-no-feature', 'fallback-wrong-instance') {
        if ($plan.checks -notcontains $check) { $failures += "The plan lacks the $check check." }
    }
}

# The script issues no statement of its own and never writes under the Knowledge Base.
$source = Get-Content -LiteralPath $script -Raw
foreach ($forbidden in '&\s*sqlcmd\b', 'Invoke-Sqlcmd', '(?i)\b(INSERT|UPDATE|DELETE|ALTER|DROP)\s', 'Set-Content', 'Out-File') {
    if ($source -match $forbidden) { $failures += "The validation script contains /$forbidden/." }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'validate-engine-tds-live plan tests passed.'
