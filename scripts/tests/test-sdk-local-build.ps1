$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts\sdk-local-build.ps1'
$failures = @()

function Invoke-Plan([string[]]$Arguments) {
    $output = @(& pwsh -NoProfile -File $script @Arguments -PlanOnly 2>&1)
    return [pscustomobject]@{ exit = $LASTEXITCODE; text = ($output -join "`n") }
}

function Expect-Refused([string]$Name, [string[]]$Arguments, [string]$Pattern) {
    $r = Invoke-Plan $Arguments
    if ($r.exit -eq 0 -or $r.text -notmatch $Pattern) {
        $script:failures += "$Name was not refused with /$Pattern/ (exit $($r.exit)): $($r.text)"
    }
}

$plan = Invoke-Plan @('-KbPath', 'C:\KBs\KBTesteUnitPlan', '-Object', 'DCM37961700,Procedure:Other', '-ForceRebuild')
if ($plan.exit -ne 0) { throw "Plan for a test KB failed: $($plan.text)" }
$json = $plan.text | ConvertFrom-Json
$xml = [xml]$json.projectXml
$ns = New-Object Xml.XmlNamespaceManager $xml.NameTable
$ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
$tasks = @($xml.SelectNodes('//m:Target[@Name="LocalBuild"]/*', $ns) | ForEach-Object LocalName)
$expected = @('OpenKnowledgeBase', 'Message', 'SpecifyOneOnly', 'Message', 'GenerateOnly', 'Message', 'CloseKnowledgeBase', 'OnError')
if (($tasks -join ',') -ne ($expected -join ',')) { $failures += "Unexpected task sequence: $($tasks -join ',')" }
$specify = $xml.SelectSingleNode('//m:SpecifyOneOnly', $ns)
if ($specify.ObjectNames -ne 'DCM37961700;Procedure:Other' -or $specify.ForceRebuild -ne 'true') {
    $failures += "SpecifyOneOnly arguments wrong: $($specify.OuterXml)"
}
if ($xml.SelectSingleNode('//m:OpenKnowledgeBase', $ns).Directory -ne 'C:\KBs\KBTesteUnitPlan') { $failures += 'OpenKnowledgeBase does not target the requested KB.' }
if ($json.msbuild -notmatch 'Framework\\v4\.0\.30319\\MSBuild\.exe$') { $failures += "Not the 32-bit Framework MSBuild: $($json.msbuild)" }
foreach ($forbidden in 'BuildOne', 'BuildAll', 'IdeWebBuildAndDeploy', 'Reorganize', 'Deploy') {
    if ($json.projectXml -match "<$forbidden") { $failures += "Generated project contains <$forbidden>." }
}

$specOnly = Invoke-Plan @('-KbPath', 'C:\KBs\KBTesteUnitPlan', '-All', '-Through', 'Specify')
$specXml = ($specOnly.text | ConvertFrom-Json).projectXml
if ($specOnly.exit -ne 0 -or $specXml -notmatch '<SpecifyAllOnly ForceRebuild="false" />' -or $specXml -match 'GenerateOnly') {
    $failures += "Specify-only whole-KB plan wrong: $($specOnly.text)"
}

Expect-Refused 'Non-test KB' @('-KbPath', 'C:\KBs\Producao', '-All') "only disposable test KBs"
Expect-Refused 'Test KB outside C:\KBs' @('-KbPath', 'D:\KBs\KBTeste', '-All') "only disposable test KBs"
Expect-Refused 'Traversal out of a test KB' @('-KbPath', 'C:\KBs\KBTeste\..\Producao', '-All') "only disposable test KBs"
Expect-Refused 'XML injection in object name' @('-KbPath', 'C:\KBs\KBTeste', '-Object', 'A"/><BuildOne ObjectName="B') "Refused object name"
Expect-Refused 'Neither -Object nor -All' @('-KbPath', 'C:\KBs\KBTeste') "either -Object"
Expect-Refused 'Both -Object and -All' @('-KbPath', 'C:\KBs\KBTeste', '-Object', 'A', '-All') "either -Object"
Expect-Refused '-Project without compile' @('-KbPath', 'C:\KBs\KBTeste', '-All', '-Through', 'Generate', '-Project', 'adcm1') "-Project only applies"

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'sdk-local-build plan tests passed: allow-listed task sequence, escaping, KB admission and argument refusals.'
