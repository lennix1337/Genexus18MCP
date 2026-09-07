$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $env:TEMP ('gxmcp-release-status-test-' + [guid]::NewGuid().ToString('N') + '.json')
function Write-TestStatus([string]$State) {
    [ordered]@{
        version = '3.0.1'; tag = 'v3.0.1'; phase = 'complete'; state = $State
        pid = 123; updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        releaseUrl = 'https://example.invalid/release'; workflowRunId = '42'; exitCode = if ($State -eq 'failed') { 1 } else { $null }
        error = if ($State -eq 'failed') { 'mock failure' } else { $null }
    } | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
}
try {
    Write-TestStatus 'succeeded'
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path)
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'state=succeeded' -or ($output -join "`n") -notmatch 'workflowRunId=42') {
        throw 'Successful status was not rendered or returned with exit 0.'
    }
    Write-TestStatus 'failed'
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path)
    if ($LASTEXITCODE -ne 1 -or ($output -join "`n") -notmatch 'error=mock failure') { throw 'Failed status did not return exit 1 with its error.' }
    Write-TestStatus 'running'
    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -WaitSeconds 0 *> $null
    if ($LASTEXITCODE -ne 2) { throw 'Running status must return timeout/in-progress exit 2.' }
    $releaseSource = Get-Content (Join-Path $root 'release.ps1') -Raw
    if ($releaseSource -notmatch '\[DRY-RUN\] would create GitHub release' -or $releaseSource -notmatch 'if \(\$DryRun\)') {
        throw 'Dry-run release wording is still ambiguous.'
    }
    Write-Host 'release-status: success, failure, timeout and dry-run wording checks passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}
