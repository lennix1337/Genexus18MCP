$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = Join-Path $env:TEMP ('gxmcp-release-status-artifacts-' + [guid]::NewGuid().ToString('N'))
$path = Join-Path $artifactRoot 'status.json'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
foreach ($name in @('publish.zip', 'publish.zip.sha256', 'nexus-ide-3.0.1.vsix')) {
    Set-Content -LiteralPath (Join-Path $artifactRoot $name) -Value $name -NoNewline -Encoding ascii
}
function Write-TestStatus([string]$State) {
    [ordered]@{
        version = '3.0.1'; tag = 'v3.0.1'; repository = 'lennix1337/Genexus18MCP'; phase = 'complete'; state = $State; tagCommit = ('a' * 40)
        pid = 123; updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        statusFile = $path; stdoutLog = 'C:\temp\release.stdout.log'; stderrLog = 'C:\temp\release.stderr.log'
        releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.0.1'; workflowRunId = '42'; publicationState = 'verified'
        publicationEvidencePath = "$path.publication.json"; publicationError = $null; nextAction = 'complete'
        artifactFingerprint = 'abc123'; npmVersion = '3.0.1'
        exitCode = if ($State -eq 'failed') { 1 } else { $null }
        error = if ($State -eq 'failed') { 'mock failure' } else { $null }
    } | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
}
try {
    if ([IO.Path]::GetDirectoryName($path) -ne $artifactRoot) {
        throw 'Synthetic status must stay inside its isolated fixture directory, outside the live release watcher.'
    }
    Write-TestStatus 'succeeded'
    $assetEvidence = @(
        foreach ($assetName in @('publish.zip', 'publish.zip.sha256', 'nexus-ide-3.0.1.vsix')) {
            $assetPath = Join-Path $artifactRoot $assetName
            [ordered]@{
                name = $assetName; size = (Get-Item -LiteralPath $assetPath).Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                state = 'uploaded'; updatedAt = '2026-09-23T00:00:00Z'
            }
        }
    )
    [ordered]@{
        schemaVersion = 'gxmcp-release-publication/1'; state = 'verified'; tag = 'v3.0.1'; version = '3.0.1'
        expectedCommit = ('a' * 40); sourceCommit = ('a' * 40); tagCommit = ('a' * 40)
        assetsUpdatedAtUtc = '2026-09-23T00:00:00Z'; assets = $assetEvidence
        workflowRunId = '42'; workflowStatus = 'completed'; workflowConclusion = 'success'; workflowUrl = 'https://github.com/lennix1337/Genexus18MCP/actions/runs/42'
        releasePublishedAtUtc = '2026-09-22T23:00:00Z'; npmVersion = '3.0.1'; npmGitHead = ('a' * 40); releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.0.1'; error = $null
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$path.publication.json" -Encoding utf8
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot)

    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'state=succeeded' -or ($output -join "`n") -notmatch 'workflowRunId=42' -or ($output -join "`n") -notmatch 'stdoutLog=C:\\temp\\release.stdout.log' -or ($output -join "`n") -notmatch 'stderrLog=C:\\temp\\release.stderr.log' -or ($output -join "`n") -notmatch 'publicationState=verified' -or ($output -join "`n") -notmatch 'npmVersion=3.0.1') {
        throw 'Successful status was not rendered or returned with exit 0.'
    }
    $json = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot -Json)
    if ($LASTEXITCODE -ne 0 -or ($json -join "`n") -notmatch '"state": "succeeded"' -or ($json -join "`n") -notmatch '"npmVersion": "3.0.1"') {
        throw 'JSON status output did not preserve publication evidence.'
    }
    $validPublicationJson = Get-Content -LiteralPath "$path.publication.json" -Raw
    $failedPublication = $validPublicationJson | ConvertFrom-Json
    $failedPublication.state = 'failed'
    $failedPublication.error = 'token=publication-secret'
    $failedPublication | Add-Member -NotePropertyName diagnostics -NotePropertyValue ([pscustomobject]@{ detail = 'token=nested-secret' }) -Force
    $failedPublication | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$path.publication.json" -Encoding utf8
    $json = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot -Json)
    if ($LASTEXITCODE -ne 1 -or ($json -join "`n") -notmatch '"state": "failed"' -or ($json -join "`n") -match 'publication-secret|nested-secret') {
        throw 'Invalid publication evidence must fail closed and remain redacted.'
    }
    Set-Content -LiteralPath "$path.publication.json" -Value $validPublicationJson -Encoding utf8
    Write-TestStatus 'failed'
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot)
    if ($LASTEXITCODE -ne 1 -or ($output -join "`n") -notmatch 'error=mock failure') { throw 'Failed status did not return exit 1 with its error.' }
    Write-TestStatus 'running'
    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -WaitSeconds 0 *> $null
    if ($LASTEXITCODE -ne 2) { throw 'Running status must return timeout/in-progress exit 2.' }
    # -PrepareOnly succeeds without publishing, so the missing publication evidence is not a failure.
    Remove-Item -LiteralPath "$path.publication.json" -Force
    $prepared = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $prepared.state = 'succeeded'; $prepared.phase = 'prepared'; $prepared.publicationState = 'not-started'; $prepared.nextAction = 'push-candidate-and-run-ci'
    $prepared | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot)
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'phase=prepared state=succeeded') { throw "A prepared release must report success (exit $LASTEXITCODE): $($output -join ' ')" }
    $prepared.phase = 'complete'
    $prepared | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot *> $null
    if ($LASTEXITCODE -ne 1) { throw 'A completed release without publication evidence must still fail.' }
    Write-TestStatus 'running'
    # A release process that died without recording a result must not read as running forever.
    # A hidden console can inherit invalid output handles in a headless runner.
    # Redirect every stream so the fixture does not depend on the caller's console.
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -CommandType Application | Select-Object -First 1).Source)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', 'exit 0')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $deadProcess = [Diagnostics.Process]::Start($startInfo)
    try {
        $deadProcess.StandardInput.Close()
        if (-not $deadProcess.WaitForExit(10000)) {
            $deadProcess.Kill($true)
            throw 'Disposable headless PowerShell failed to exit within 10 seconds.'
        }
        if ($deadProcess.ExitCode -ne 0) {
            throw "Disposable headless PowerShell crashed: exit=$($deadProcess.ExitCode); $($deadProcess.StandardError.ReadToEnd())"
        }
        $deadPid = $deadProcess.Id
    }
    finally { $deadProcess.Dispose() }
    $interrupted = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $interrupted.pid = $deadPid
    $interrupted.updatedAtUtc = [DateTime]::UtcNow.AddMinutes(-5).ToString('o')
    $interrupted | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    $output = @(& pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot)
    if ($LASTEXITCODE -ne 1 -or ($output -join "`n") -notmatch 'never recorded a result') { throw "Status of a dead release process must fail (exit $LASTEXITCODE): $($output -join ' ')" }
    # ...but a fresh status inside the launcher-to-child handoff window is still running.
    $interrupted.updatedAtUtc = [DateTime]::UtcNow.ToString('o')
    $interrupted | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-status.ps1') -Path $path -RepositoryRoot $artifactRoot *> $null
    if ($LASTEXITCODE -ne 2) { throw 'A freshly written running status must stay in progress inside the grace window.' }
    $releaseSource = Get-Content (Join-Path $root 'release.ps1') -Raw
    foreach ($marker in @('\[DRY-RUN\] would create GitHub release', 'if \(\$DryRun\)', 'DetachedStdoutLog', 'DetachedStderrLog', 'stdoutLog', 'stderrLog', 'StatusFile')) {
        if ($releaseSource -notmatch $marker) { throw "Detached release handoff is missing: $marker" }
    }
    Write-Host 'release-status: success, failure, timeout and dry-run wording checks passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath "$path.publication.json") { Remove-Item -LiteralPath "$path.publication.json" -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
