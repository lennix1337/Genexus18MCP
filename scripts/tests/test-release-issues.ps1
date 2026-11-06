$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts/release-issues.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw 'Release issue helper is missing.'
}
$readIssuePath = Join-Path $root 'scripts/read-issue.ps1'
if (-not (Test-Path -LiteralPath $readIssuePath -PathType Leaf)) {
    throw 'Explicit JSON issue reader is missing.'
}

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$readTokens = $null
$readErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($readIssuePath, [ref]$readTokens, [ref]$readErrors) | Out-Null
if ($readErrors.Count) { throw $readErrors[0] }
foreach ($name in @('Get-GhJson', 'Get-ReleaseIssueData', 'Get-ReleaseIssueLabelNames', 'Assert-ReleaseIssueAction', 'Test-ReleaseIssuePublicationComment', 'ConvertTo-ReleaseIssueNumbers')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$script:ReleaseIssueLabel = 'fixed-pending-release'

function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

# `pwsh -File ... -Issue 889,880` hands the list over as the single string
# '889,880'; an [int[]] parameter turned it into issue 889880.
$fromFile = @(ConvertTo-ReleaseIssueNumbers -Value @('889,880'))
if (($fromFile -join ' ') -ne '889 880') { throw "A comma list from pwsh -File was parsed as '$($fromFile -join ' ')'." }
$mixed = @(ConvertTo-ReleaseIssueNumbers -Value @('#184', '185, 186', '184'))
if (($mixed -join ' ') -ne '184 185 186') { throw "Mixed issue list was parsed as '$($mixed -join ' ')'." }
Expect-Failure { ConvertTo-ReleaseIssueNumbers -Value @('88a') } 'A non-numeric issue was accepted.'
Expect-Failure { ConvertTo-ReleaseIssueNumbers -Value @('0') } 'Issue number zero was accepted.'
Expect-Failure { ConvertTo-ReleaseIssueNumbers -Value @(' , ') } 'An empty issue list was accepted.'

$openWithoutLabel = [pscustomobject]@{ state = 'OPEN'; labels = @() }
$openWithLabel = [pscustomobject]@{
    state = 'open'
    labels = @([pscustomobject]@{ name = 'fixed-pending-release' })
}
$closedWithLabel = [pscustomobject]@{
    state = 'closed'
    labels = @([pscustomobject]@{ name = 'fixed-pending-release' })
}

$labelNames = @(Get-ReleaseIssueLabelNames -Labels $openWithLabel.labels)
if ($labelNames.Count -ne 1 -or $labelNames[0] -ne 'fixed-pending-release') {
    throw 'Release issue label extraction did not preserve the exact label name.'
}
Assert-ReleaseIssueAction -Action 'MarkFixedPendingRelease' -IssueNumber 184 -IssueData $openWithoutLabel
Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber 185 -IssueData $openWithLabel
Expect-Failure { Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber 186 -IssueData $openWithoutLabel } 'Unlabeled issue closure was allowed.'
Expect-Failure { Assert-ReleaseIssueAction -Action 'MarkFixedPendingRelease' -IssueNumber 187 -IssueData $closedWithLabel } 'Closed issue was allowed in the mark-fixed workflow.'
Expect-Failure { Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber 188 -IssueData $closedWithLabel } 'Closed issue was allowed in the release closure workflow.'
$releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.9.1'
$withReleaseComment = [pscustomobject]@{ comments = @([pscustomobject]@{ body = "Released in $releaseUrl" }) }
$withoutReleaseComment = [pscustomobject]@{ comments = @([pscustomobject]@{ body = 'unrelated comment' }) }
if (-not (Test-ReleaseIssuePublicationComment -IssueData $withReleaseComment -ReleaseUrl $releaseUrl)) { throw 'Release publication comment was not recognized.' }
if (Test-ReleaseIssuePublicationComment -IssueData $withoutReleaseComment -ReleaseUrl $releaseUrl) { throw 'Unrelated issue comment was accepted as release evidence.' }

$temp = Join-Path $env:TEMP ('gxmcp-release-issue-json-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $fakeGh = Join-Path $temp 'gh.cmd'
    @(
        '@echo off'
        'echo {"number":184,"title":"Test issue","body":"Details","state":"OPEN","labels":[],"comments":[]}'
        'exit /b 0'
    ) | Set-Content -LiteralPath $fakeGh -Encoding ascii
    $issue = Get-ReleaseIssueData -IssueNumber 184 -GhPath $fakeGh
    if ([int]$issue.number -ne 184 -or $issue.title -ne 'Test issue') { throw 'Valid GitHub issue JSON was not read back.' }
    $readOutput = @(& pwsh -NoProfile -File $readIssuePath -Issue 184 -GhPath $fakeGh 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Explicit issue reader failed: $($readOutput -join ' ')" }
    $readIssue = ($readOutput -join "`n") | ConvertFrom-Json
    if ([int]$readIssue.number -ne 184 -or $readIssue.title -ne 'Test issue') { throw 'Explicit issue reader did not emit the validated JSON record.' }

    @('@echo off', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    Expect-Failure { Get-ReleaseIssueData -IssueNumber 184 -GhPath $fakeGh } 'Empty GitHub output was accepted.'

    @('@echo off', 'echo not-json', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    Expect-Failure { Get-ReleaseIssueData -IssueNumber 184 -GhPath $fakeGh } 'Invalid GitHub JSON was accepted.'

    @('@echo off', 'echo {"number":185,"title":"Wrong issue","state":"OPEN","labels":[],"comments":[]}', 'exit /b 0') | Set-Content -LiteralPath $fakeGh -Encoding ascii
    Expect-Failure { Get-ReleaseIssueData -IssueNumber 184 -GhPath $fakeGh } 'Mismatched GitHub issue number was accepted.'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($marker in @('MarkFixedPendingRelease', 'CloseAfterRelease', 'fixed-pending-release', 'gh', 'issue', 'edit', 'comment', 'close', 'ReleaseUrl', 'DefineOnly', 'number,title,body,state,labels,comments', 'empty output', 'invalid JSON')) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Release issue helper is missing contract marker: $marker" }
}
$readSource = Get-Content -LiteralPath $readIssuePath -Raw
foreach ($marker in @('GhPath', 'release-issues.ps1', '-DefineOnly', 'Get-ReleaseIssueData', 'ConvertTo-Json')) {
    if ($readSource -notmatch [regex]::Escape($marker)) { throw "Issue reader is missing contract marker: $marker" }
}
Write-Host 'release-issues: mark/close separation and label guard passed' -ForegroundColor Green
