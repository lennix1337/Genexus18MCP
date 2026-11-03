$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Walks the whole release.ps1 flow in -DryRun with git/gh stubs (release-dryrun-harness.ps1).
# Script errors in the candidate, preflight, manifest and publication steps (the v4.3.0 failures
# that only surfaced after a real push) fail here, before any release is attempted. The stubs
# record every write call; a dry run that reaches one of them fails.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$releaseScript = Join-Path $root 'release.ps1'
$harness = Join-Path $PSScriptRoot 'release-dryrun-harness.ps1'
$version = ((Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version).Trim()
$changelog = Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Raw
# A dry run rehearses the released version, whose changelog entry already exists, so it needs
# no unreleased notes; a real release of a new version still requires them.
if ($changelog -notmatch "(?m)^##[ \t]+v$([regex]::Escape($version))(?=[ \t]|$)") {
    throw "CHANGELOG.md has no '## v$version' entry to rehearse the release against."
}

$writePattern = '^gh (pr (create|reopen|close)|release (create|upload)|issue (comment|close|edit))|^git (push|commit|add|reset|checkout|rm|restore)|^git tag -[ad]|^git branch -D'
$closedPr = '[{"number":841,"state":"CLOSED","url":"https://github.com/lennix1337/Genexus18MCP/pull/841"}]'
$scenarios = @(
    [pscustomobject]@{ Name = 'new candidate'; PrListJson = '[]'; RemoteCandidateLine = ''; Expect = 'would create the candidate PR' },
    [pscustomobject]@{ Name = 'closed candidate PR'; PrListJson = $closedPr; RemoteCandidateLine = ''; Expect = 'would reopen candidate PR #841' },
    [pscustomobject]@{ Name = 'stale candidate branch'; PrListJson = '[]'; RemoteCandidateLine = ('a' * 40) + "`trefs/heads/candidate/v$version"; Expect = 'Replacing the stale candidate branch' }
)

$temp = Join-Path $env:TEMP ('gxmcp-release-dryrun-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $index = 0
    foreach ($scenario in $scenarios) {
        $index++
        $statusFile = Join-Path $temp "status-$index.json"
        $callLog = Join-Path $temp "calls-$index.log"
        $output = & pwsh -NoProfile -File $harness -ReleaseScript $releaseScript -Version $version -StatusFile $statusFile `
            -CallLog $callLog -PrListJson $scenario.PrListJson -RemoteCandidateLine $scenario.RemoteCandidateLine 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "dry run [$($scenario.Name)] crashed (exit $LASTEXITCODE):`n$output" }
        $status = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
        if ($status.state -ne 'succeeded' -or $status.phase -ne 'dry-run-complete') {
            throw "dry run [$($scenario.Name)] did not complete: phase=$($status.phase) state=$($status.state) error=$($status.error)`n$output"
        }
        if ($output -notmatch [regex]::Escape($scenario.Expect)) {
            throw "dry run [$($scenario.Name)] did not reach the step '$($scenario.Expect)'."
        }
        if ($output -notmatch "RELEASE SUMMARY: version=$([regex]::Escape($version)) tag=v$([regex]::Escape($version)) release=none assets=\S+ npm=not-checked-dry-run rc=0\s") {
            throw "dry run [$($scenario.Name)] printed no successful RELEASE SUMMARY line."
        }
        $writes = @(if (Test-Path -LiteralPath $callLog) { Get-Content -LiteralPath $callLog | Where-Object { $_ -match $writePattern } })
        if ($writes.Count -gt 0) { throw "dry run [$($scenario.Name)] reached write calls: $($writes -join ' | ')" }
    }

    # Negative control: a version the flow must reject has to fail the dry run, so a broken
    # harness cannot pass every scenario.
    $controlStatus = Join-Path $temp 'status-control.json'
    $controlOutput = & pwsh -NoProfile -File $harness -ReleaseScript $releaseScript -Version '01.2.3' -StatusFile $controlStatus `
        -CallLog (Join-Path $temp 'calls-control.log') 2>&1 | Out-String
    $control = Get-Content -LiteralPath $controlStatus -Raw | ConvertFrom-Json
    if ($control.state -ne 'failed' -or $controlOutput -notmatch "RELEASE SUMMARY: .* rc=1 failed-step='") {
        throw 'The dry-run harness passed a version the flow must reject: the rehearsal cannot be trusted.'
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "release-dryrun: $($scenarios.Count) rehearsals of v$version completed without writes; failure control rejected" -ForegroundColor Green
