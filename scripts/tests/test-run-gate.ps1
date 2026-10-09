$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$gate = Join-Path $root 'scripts\run-gate.ps1'
$worktreeScript = Join-Path $root 'scripts\pr-review-worktrees.ps1'
$preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts\integration-preflight.ps1') -Raw

foreach ($script in @($gate, $worktreeScript)) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "$script has PowerShell parse errors: $($errors -join '; ')" }
}
if ($preflightSource -notmatch 'node_modules is missing') { throw 'Integration preflight must fail fast when node_modules is missing.' }

$temp = Join-Path $env:TEMP ('gxmcp-run-gate-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $gates = Join-Path $temp 'gates'
    function Invoke-Gate([string]$Action, [string]$Name, [string]$Extra = '') {
        $arguments = @('-NoProfile', '-File', $gate, $Action, '-Name', $Name, '-GateDirectory', $gates, '-WorkingDirectory', $temp)
        if ($Extra) { $arguments += @('-Command', $Extra) }
        $text = (& pwsh @arguments 2>&1 | Out-String)
        return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
    }

    # A passing gate reports its summary line and exit 0.
    [void](Invoke-Gate Start 'ok' 'Write-Output "Passed!  - Failed: 0, Passed: 3"')
    $result = & pwsh -NoProfile -File $gate Wait -Name ok -GateDirectory $gates -TimeoutSeconds 60 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $result -notmatch 'passed exit=0' -or $result -notmatch 'Passed!') { throw "Passing gate misreported (exit $LASTEXITCODE): $result" }

    # A failing native command surfaces its exit code.
    [void](Invoke-Gate Start 'bad' 'pwsh -NoProfile -Command "exit 7"')
    $result = & pwsh -NoProfile -File $gate Wait -Name bad -GateDirectory $gates -TimeoutSeconds 60 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or $result -notmatch 'failed exit=7') { throw "Failing gate misreported (exit $LASTEXITCODE): $result" }

    # A slow gate makes Wait return "still running" (exit 2) instead of blocking past its budget.
    [void](Invoke-Gate Start 'slow' 'Start-Sleep -Seconds 30')
    $result = & pwsh -NoProfile -File $gate Wait -Name slow -GateDirectory $gates -TimeoutSeconds 5 2>&1 | Out-String
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $state = Get-Content -LiteralPath (Join-Path $gates 'slow.json') -Raw | ConvertFrom-Json
        if ($state.pid -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($state.pid -gt 0) {
        Stop-Process -Id $state.pid -Force -ErrorAction SilentlyContinue
    }

    # A killed gate is reported as dead, never as running forever.
    $result = & pwsh -NoProfile -File $gate Wait -Name slow -GateDirectory $gates -TimeoutSeconds 20 2>&1 | Out-String
    if ($LASTEXITCODE -ne 3 -or $result -notmatch 'died') { throw "Killed gate should report died (exit $LASTEXITCODE): $result" }

    # node_modules linking: shared only for identical lockfiles; removal must not touch the shared install.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($worktreeScript, [ref]$tokens, [ref]$errors)
    foreach ($name in @('Link-NodeModules', 'Remove-NodeModulesLink')) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing worktree helper: $name" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $repo = Join-Path $temp 'repo'; $same = Join-Path $temp 'same'; $other = Join-Path $temp 'other'
    foreach ($directory in @($repo, $same, $other, (Join-Path $repo 'node_modules'))) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Set-Content -LiteralPath (Join-Path $repo 'node_modules\sentinel.txt') -Value 'keep'
    Set-Content -LiteralPath (Join-Path $repo 'package-lock.json') -Value '{"a":1}'
    Set-Content -LiteralPath (Join-Path $same 'package-lock.json') -Value '{"a":1}'
    Set-Content -LiteralPath (Join-Path $other 'package-lock.json') -Value '{"a":2}'
    if (-not (Link-NodeModules -RepositoryRoot $repo -Worktree $same)) { throw 'Identical lockfiles must link node_modules.' }
    if (-not (Test-Path -LiteralPath (Join-Path $same 'node_modules\sentinel.txt'))) { throw 'The linked install is not visible through the worktree.' }
    if (Link-NodeModules -RepositoryRoot $repo -Worktree $other) { throw 'A different lockfile must not share node_modules.' }
    Remove-NodeModulesLink -Worktree $same
    if (Test-Path -LiteralPath (Join-Path $same 'node_modules')) { throw 'The junction was not removed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'node_modules\sentinel.txt'))) { throw 'Removing the link deleted the shared install.' }

    Write-Host 'run-gate and review-worktree dependency link: passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) {
        foreach ($link in Get-ChildItem -LiteralPath $temp -Recurse -Force -Directory -ErrorAction SilentlyContinue | Where-Object LinkType) { [IO.Directory]::Delete($link.FullName) }
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
