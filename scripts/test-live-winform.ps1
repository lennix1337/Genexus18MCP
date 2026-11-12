#requires -Version 7
<#
.SYNOPSIS
  End-to-end check of the WinForm part (issue #963) against a disposable KB: read the full
  Document, edit it (dry run, stale baseVersion, real write) and, after the gateway is
  restarted, read it back identical.
.DESCRIPTION
  Needs a scratch Streamable-HTTP gateway (docs/agent_playbook.md "Proving SDK fixes against a
  real KB") and a KB folder whose name contains "teste". Two phases share a state file so the
  gateway (and its Worker) can be killed and relaunched in between:
    -Phase Write   creates the WorkPanel, checks read/dry-run/stale/write/no-change
    -Phase Verify  (new gateway, same KB) reads the part and compares it to the Write receipt
  Exit code 0 = every check passed; any failure throws.
.EXAMPLE
  pwsh -File scripts/test-live-winform.ps1 -Phase Write  -KbPath C:/KBs/KBTeste963 -BaseUrl http://127.0.0.1:55963/mcp
  # kill and relaunch the scratch gateway, then:
  pwsh -File scripts/test-live-winform.ps1 -Phase Verify -KbPath C:/KBs/KBTeste963 -BaseUrl http://127.0.0.1:55963/mcp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Write', 'Verify')][string]$Phase,
    [Parameter(Mandatory = $true)][string]$KbPath,
    [string]$BaseUrl = 'http://127.0.0.1:55171/mcp',
    [string]$StateFile = (Join-Path ([IO.Path]::GetTempPath()) 'gxmcp-winform-live.json'),
    [string]$Alias = 'winformlive'
)

$ErrorActionPreference = 'Stop'
if ((Split-Path -Leaf $KbPath) -notmatch 'teste') { throw "Refusing to touch '$KbPath': only disposable KBs whose name contains 'teste' are allowed." }

$probe = Join-Path $PSScriptRoot 'mcp-probe.ps1'
$sessionFile = Join-Path ([IO.Path]::GetTempPath()) "gxmcp-winform-live.$Phase.session"
Remove-Item -LiteralPath $sessionFile -ErrorAction SilentlyContinue

function Invoke-Tool([string]$Tool, [hashtable]$ToolArgs) {
    $json = $ToolArgs | ConvertTo-Json -Depth 20 -Compress
    $text = & pwsh -NoProfile -File $probe -BaseUrl $BaseUrl -SessionFile $sessionFile -Tool $Tool -Arguments $json -TimeoutSec 300
    return ($text -join "`n") | ConvertFrom-Json
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "ok   $Message"
}

function Form([string]$Caption) {
    @"
<Part type="4c28dfb9-f83b-46f0-9cf3-f7e090b525d5"><Document><Form><Type>Text</Type><Class>Text</Class><ClassGuid>70027999-f0dc-4cf4-9373-8298a4f2dec1</ClassGuid><SizeUnit>ch</SizeUnit><Info><Properties><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties></Info><Canvas><Elements><Label><Properties><Property><Name>Name</Name><Value>LabelTextControl1</Value></Property><Property><Name>CaptionExpression</Name><Value>$Caption</Value></Property><Property><Name>Left</Name><Value>27</Value></Property><Property><Name>Top</Name><Value>9</Value></Property></Properties></Label><Label><Properties><Property><Name>Name</Name><Value>LabelTextControl2</Value></Property><Property><Name>CaptionExpression</Name><Value>SEGUNDO</Value></Property><Property><Name>Left</Name><Value>5</Value></Property><Property><Name>Top</Name><Value>12</Value></Property></Properties></Label></Elements></Canvas></Form></Document></Part>
"@
}

$opened = Invoke-Tool 'genexus_kb' @{ action = 'open'; alias = $Alias; path = $KbPath }
Assert-That ($opened.opened -eq $Alias) "KB $KbPath opened as $Alias"

if ($Phase -eq 'Write') {
    $name = 'WpWinForm' + (Get-Date -Format 'HHmmss')
    $created = Invoke-Tool 'genexus_create' @{ name = $name; type = 'WorkPanel'; kb = $Alias }
    Assert-That ($created.status -ne 'error') "WorkPanel $name created"
    $target = @{ name = $name; type = 'WorkPanel'; part = 'WinForm'; kb = $Alias }

    $empty = Invoke-Tool 'genexus_read' $target
    Assert-That ($empty.source -match '<Document>' -and $empty.source -match '<Form>') 'read of an untouched WinForm returns the Document, not an empty <Properties />'

    $form = Form 'TITULO'
    $dry = Invoke-Tool 'genexus_edit' ($target + @{ mode = 'full'; content = $form; dryRun = $true; baseVersion = $empty.versionToken })
    Assert-That ($dry.code -eq 'WriteDryRun' -and $dry.result.persisted -eq $false) 'dryRun validates and persists nothing'
    $afterDry = Invoke-Tool 'genexus_read' $target
    Assert-That ($afterDry.versionToken -eq $empty.versionToken) 'dryRun left the part untouched'

    $bad = Invoke-Tool 'genexus_edit' ($target + @{ mode = 'full'; content = '<Part><Document><Form><Class>Text</Class></Form></Document></Part>' })
    Assert-That ($bad.code -eq 'WinFormXmlInvalid') 'a Form without <Type> is refused, not silently ignored'

    $stale = Invoke-Tool 'genexus_edit' ($target + @{ mode = 'full'; content = $form; baseVersion = '0:stale:stale' })
    Assert-That ($stale.code -eq 'StaleObject') 'a stale baseVersion is rejected'

    $written = Invoke-Tool 'genexus_edit' ($target + @{ mode = 'full'; content = $form; baseVersion = $empty.versionToken })
    Assert-That ($written.code -eq 'WriteApplied' -and $written.persistedState -eq 'confirmed' -and $written.result.verification -eq 'fresh-reload') 'write applied and verified against a fresh reload'

    $reread = Invoke-Tool 'genexus_read' $target
    Assert-That ($reread.source -match 'LabelTextControl2' -and $reread.source -match 'TITULO') 'reread carries both controls and the caption'

    $same = Invoke-Tool 'genexus_edit' ($target + @{ mode = 'full'; content = $form; baseVersion = $reread.versionToken })
    Assert-That ($same.code -eq 'WriteNoChange') 'resending the same form is a verified no-op'

    @{ name = $name; versionToken = $reread.versionToken; source = $reread.source } | ConvertTo-Json | Set-Content -LiteralPath $StateFile
    Write-Host "State saved to $StateFile. Restart the gateway, then run -Phase Verify."
    return
}

$state = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
$after = Invoke-Tool 'genexus_read' @{ name = $state.name; type = 'WorkPanel'; part = 'WinForm'; kb = $Alias }
Assert-That ($after.source -eq $state.source) 'WinForm read after a restart is identical to the one read after the write'
Assert-That ($after.versionToken -eq $state.versionToken) 'versionToken is unchanged by the restart'
