$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\release-issues.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw 'Release issue helper is missing.'
}

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-ReleaseIssueLabelNames', 'Assert-ReleaseIssueAction')) {
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

$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($marker in @('MarkFixedPendingRelease', 'CloseAfterRelease', 'fixed-pending-release', 'gh', 'issue', 'edit', 'comment', 'close', 'ReleaseUrl', 'DefineOnly')) {
    if ($source -notmatch [regex]::Escape($marker)) { throw "Release issue helper is missing contract marker: $marker" }
}
Write-Host 'release-issues: mark/close separation and label guard passed' -ForegroundColor Green
