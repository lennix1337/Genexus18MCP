$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts/collect-diagnostics.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw 'Diagnostics collector is missing.' }

$temp = Join-Path $env:TEMP ('gxmcp-collect-diagnostics-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$workerLog = Join-Path $temp 'worker_debug.log'
$outFile = Join-Path $temp 'genexus-mcp-diagnostics.txt'
$mockToken = 'abc123' + 'def456'
$mockGitHubToken = 'gh' + 'p_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345'
$mockBearer = 'eyJhbGci' + 'OiJIUzI1NiJ9'
$mockPassword = 'hun' + 'ter2'
$mockApiKey = 'sk' + '-abc'
$mockNpmToken = 'npm' + '_abcdefghijkl'
try {
    # Issue #327: the bundle is what the bug-report template tells users to paste
    # into a public issue, and it carries the crash ledger and the worker log
    # markers verbatim. Every line below is a shape that used to reach the
    # artifact unmasked: the collector's own Redact had no credential pattern.
    @(
        ('[COLD-START] token=' + $mockToken + ' ' + $mockGitHubToken),
        ('[WORKER-CRASH] authorization: Bearer ' + $mockBearer + ' payload'),
        ('[TOOL-LATENCY] connection string=Server=tcp:srv,1600;User Id=sa;Password=' + $mockPassword),
        '[WORKER-CRASH] pwd=phrase with spaces and more',
        ('[WORKER-CRASH] api_key=' + $mockApiKey + ' def'),
        ("[WORKER-CRASH] password=`"quoted secret value`" " + $mockNpmToken),
        '[WORKER-CRASH] WorkerNativeCrashRecovered marker DIAG-TAIL-MARKER',
        ('[WORKER-CRASH] kb at C:\KBs\MyFixtureKB and ' + $env:USERPROFILE)
    ) | Set-Content -LiteralPath $workerLog -Encoding utf8

    & pwsh -NoProfile -File $script -WorkerLog $workerLog -OutFile $outFile -LogLines 200 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The collector exited with $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $outFile -PathType Leaf)) { throw 'The collector wrote no bundle.' }
    $bundle = Get-Content -LiteralPath $outFile -Raw

    $mustNotSurvive = @(
        $mockToken,                                               # token=
        $mockGitHubToken,                                           # gh_ literal prefix
        $mockBearer,                                              # authorization bearer
        'tcp:srv',                                             # connection string server
        $mockPassword,                                            # connection string password
        'phrase with spaces and more',                         # pwd with spaces
        $mockApiKey,                                              # api_key
        'quoted secret value',                                 # quoted password
        $mockNpmToken,                                            # npm_ literal prefix
        $env:USERNAME,                                         # local user identity
        $env:COMPUTERNAME                                      # local host identity
    )
    foreach ($secret in $mustNotSurvive) {
        if ([string]::IsNullOrWhiteSpace($secret)) { continue }
        if ($bundle -match [regex]::Escape($secret)) {
            throw "A sensitive value survived into the public diagnostics bundle: $secret"
        }
    }

    # Redaction must not destroy the bundle's purpose: the marker lines and the
    # redaction markers themselves have to remain.
    if ($bundle -notmatch 'DIAG-TAIL-MARKER') { throw 'The collector masked unrelated log content.' }
    if ($bundle -notmatch '\[COLD-START\]' -or $bundle -notmatch '\[WORKER-CRASH\]' -or $bundle -notmatch '\[TOOL-LATENCY\]') {
        throw 'The collector dropped the log markers it exists to collect.'
    }
    if ($bundle -notmatch '\[REDACTED\]') { throw 'The bundle contains no redaction marker at all.' }
    # The existing identity redaction must survive alongside the new layer.
    if ($bundle -notmatch '<KB>') { throw 'KB path redaction regressed.' }
    if ($bundle -match '(?i)[A-Z]:\\KBs\\MyFixtureKB') { throw 'The KB path survived redaction.' }

    $header = ($bundle -split "`r?`n" | Select-Object -First 4) -join ' '
    if ($header -notmatch 'credential') { throw 'The bundle header must announce credential redaction.' }

    Write-Host 'collect-diagnostics: credential values, token prefixes and local identity all redacted in the public bundle' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
