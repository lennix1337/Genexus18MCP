[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/ci.yml') -Raw
$contracts = [regex]::Match($workflow, '(?ms)^  contracts:\r?\n.*?(?=^  [a-z][\w-]*:|\z)').Value
$scope = [regex]::Match($contracts, '(?ms)^      - name: Detect change scope\r?\n.*?^        run: \|\r?\n(?<body>(?:          [^\r\n]*\r?\n|\r?\n)+)').Groups['body'].Value
if (-not $scope) { throw 'Cannot extract the real Detect change scope step.' }
$scope = $scope -replace '(?m)^          ', ''
$temp = Join-Path ([IO.Path]::GetTempPath()) ('gx-ci-docs-scope-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $temp 'repo'
$checks = 0

function Assert-Scope([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Invoke-Git([string[]]$Arguments) {
    $result = @(& git -C $repo @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($result -join ' ')" }
    return ($result -join "`n").Trim()
}

function Invoke-Scope([string]$Body, [string]$Event, [string]$Base, [string]$Head, [string]$WorkingDirectory = $repo) {
    $scriptPath = Join-Path $temp 'scope.ps1'
    $outputPath = Join-Path $temp 'output.txt'
    [IO.File]::WriteAllText($scriptPath, $Body)
    [IO.File]::WriteAllText($outputPath, '')
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $scriptPath)) { $info.ArgumentList.Add($argument) }
    $info.Environment['GITHUB_EVENT_NAME'] = $Event
    $info.Environment['BASE_SHA'] = $Base
    $info.Environment['HEAD_SHA'] = $Head
    $info.Environment['GITHUB_OUTPUT'] = $outputPath
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw 'Scope fixture timed out.' }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = [IO.File]::ReadAllText($outputPath).Trim()
            Log = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        }
    } finally { $process.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    Invoke-Git @('init', '-q') | Out-Null
    Invoke-Git @('config', 'user.name', 'Scope fixture') | Out-Null
    Invoke-Git @('config', 'user.email', 'scope@example.invalid') | Out-Null
    Invoke-Git @('config', 'core.autocrlf', 'false') | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), "base`n")
    [IO.File]::WriteAllText((Join-Path $repo 'source.cs'), "class Example {}`n")
    Invoke-Git @('add', '.') | Out-Null
    Invoke-Git @('commit', '-qm', 'base') | Out-Null
    $base = Invoke-Git @('rev-parse', 'HEAD')
    [IO.File]::AppendAllText((Join-Path $repo 'README.md'), "documentation`n")
    Invoke-Git @('commit', '-qam', 'docs') | Out-Null
    $docs = Invoke-Git @('rev-parse', 'HEAD')
    $result = Invoke-Scope $scope 'pull_request' $base $docs
    Assert-Scope ($result.ExitCode -eq 0 -and $result.Output -eq 'docs_only=true') "Docs-only diff must skip heavy suites: $($result.Log)"
    $result = Invoke-Scope $scope 'pull_request' $docs $docs
    Assert-Scope ($result.ExitCode -eq 0 -and $result.Output -eq 'docs_only=false') 'Empty diff must not skip heavy suites.'
    Invoke-Git @('mv', 'source.cs', 'source.md') | Out-Null
    Invoke-Git @('commit', '-qm', 'rename source to markdown') | Out-Null
    $renamed = Invoke-Git @('rev-parse', 'HEAD')
    $result = Invoke-Scope $scope 'pull_request' $docs $renamed
    Assert-Scope ($result.ExitCode -eq 0 -and $result.Output -eq 'docs_only=false') 'Moving source into markdown must still run heavy suites.'
    foreach ($event in @('workflow_call', 'workflow_dispatch')) {
        $result = Invoke-Scope $scope $event '' ''
        Assert-Scope ($result.ExitCode -eq 0 -and $result.Output -eq 'docs_only=false') "$event must run heavy suites."
    }
    $result = Invoke-Scope $scope 'pull_request' '' $docs
    Assert-Scope ($result.ExitCode -eq 0 -and $result.Output -eq 'docs_only=false') 'Missing base SHA must run heavy suites.'
    $result = Invoke-Scope $scope 'pull_request' ('0' * 40) $docs
    Assert-Scope ($result.ExitCode -ne 0 -and $result.Output -ne 'docs_only=true' -and $result.Log -match 'git diff') 'Invalid revisions must fail with Git diagnostics, not docs_only=true.'

    $shallow = Join-Path $temp 'shallow'
    $uri = ([uri]($repo.Replace('\', '/') + '/')).AbsoluteUri
    $cloneLog = @(& git clone --quiet --depth 1 $uri $shallow 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Shallow fixture clone failed: $cloneLog" }
    $result = Invoke-Scope $scope 'pull_request' $base $renamed $shallow
    Assert-Scope ($result.ExitCode -ne 0 -and $result.Output -ne 'docs_only=true') 'Shallow history must never silently skip heavy suites.'

    $unchecked = $scope -replace '(?m)^\s*if \(\$LASTEXITCODE -ne 0\) \{ throw [^\r\n]+\}\r?\n', ''
    Assert-Scope ($unchecked -ne $scope) 'Mutation setup: missing native exit check.'
    $result = Invoke-Scope $unchecked 'pull_request' ('0' * 40) $docs
    Assert-Scope ($result.ExitCode -eq 0) 'Mutation must expose the removed failure guard.'
    $renamesAllowed = $scope.Replace(' --no-renames', '')
    $result = Invoke-Scope $renamesAllowed 'pull_request' $docs $renamed
    Assert-Scope ($result.Output -eq 'docs_only=true') 'Mutation must expose hidden source deletion when rename detection is enabled.'
    $emptyAllowed = $scope.Replace('$changed.Count -gt 0 -and ', '')
    $result = Invoke-Scope $emptyAllowed 'pull_request' $docs $docs
    Assert-Scope ($result.Output -eq 'docs_only=true') 'Mutation must expose the vacuous docs-only classification for an empty diff.'

    Assert-Scope ($contracts -match '(?ms)- name: Checkout\r?\n        uses: [^\r\n]+\r?\n        with:\r?\n          fetch-depth: 0') 'contracts needs full history to classify a three-dot PR diff.'
    Assert-Scope ($contracts -match '(?ms)- name: Docs scope regression tests\r?\n        shell: pwsh\r?\n        run: ./scripts/tests/test-ci-docs-scope.ps1\r?\n\r?\n      - name: Detect change scope') 'Regression must run unconditionally before the scope it protects.'
    Write-Host "PASS: $checks docs-scope assertions, including three behavioral mutations."
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
