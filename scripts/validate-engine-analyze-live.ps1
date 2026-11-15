#Requires -Version 7.0
<#
.SYNOPSIS
  Live check of `gx-engine kb-analyze` against a disposable test Knowledge Base. Strictly read-only.

.DESCRIPTION
  Refuses any -KbPath that is not a direct child of C:\KBs whose name contains "teste". Builds the engine,
  runs the whole-KB analysis, prints the objects analyzed / skipped (with the top skip reasons) / findings
  per rule, the wall time, and the first findings of each rule in a form a person can look up in the
  GeneXus IDE. Runs the analysis a second time and requires the JSON to be byte-identical.

  Exit code: 0 when both runs succeed and are byte-identical, 1 otherwise (including a refused path).

  THIS SCRIPT CANNOT DECIDE WHETHER A FINDING IS CORRECT. Open a sample of each rule's findings in the
  GeneXus IDE and confirm them by hand; a false finding is a defect to report.

.PARAMETER KbPath
  The Knowledge Base directory, for example C:\KBs\KBTeste.

.PARAMETER DesignModel
.PARAMETER KbVersion
  The active Design model and KB version, together, when the KB has several versions.

.PARAMETER Samples
  How many findings of each rule to print (default 5).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KbPath,
    [int]$DesignModel,
    [int]$KbVersion,
    [ValidateRange(1, 50)][int]$Samples = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    exit 1
}

# ---- path guard: a direct child of C:\KBs whose name contains "teste" -----------------------------------
$kbRoot = 'C:\KBs'
try { $full = [System.IO.Path]::GetFullPath($KbPath).TrimEnd('\', '/') } catch { Fail "invalid path '$KbPath'" }
$parent = [System.IO.Path]::GetDirectoryName($full)
$leaf = [System.IO.Path]::GetFileName($full)
if (-not $parent -or -not [string]::Equals($parent, $kbRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Fail "refused: '$full' is not a direct child of $kbRoot"
}
if ($leaf.IndexOf('teste', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    Fail "refused: '$leaf' does not contain 'teste'; only disposable test Knowledge Bases are allowed"
}
if (-not (Test-Path -LiteralPath $full -PathType Container)) { Fail "'$full' does not exist" }
if ((Get-Item -LiteralPath $full).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    Fail "refused: '$full' is a link, not a real directory"
}
if (($PSBoundParameters.ContainsKey('DesignModel')) -ne ($PSBoundParameters.ContainsKey('KbVersion'))) {
    Fail '-DesignModel and -KbVersion go together: give both or neither'
}

# ---- build the engine ----------------------------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repoRoot 'src\gx-engine\Cargo.toml'
Write-Host "Building gx-engine (release) ..."
& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { Fail "cargo build failed with exit code $LASTEXITCODE" }
$exe = Join-Path $repoRoot 'src\gx-engine\target\release\gx-engine.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail "engine binary not found at $exe" }

# ---- one analysis run ------------------------------------------------------------------------------------
function Invoke-Analyze {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    foreach ($arg in @('--diagnostics=json', 'kb-analyze', $full)) { $psi.ArgumentList.Add($arg) }
    if ($script:UseVersion) {
        foreach ($arg in @('--design-model', "$script:DesignModel", '--kb-version', "$script:KbVersion")) { $psi.ArgumentList.Add($arg) }
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = [System.IO.MemoryStream]::new()
    $copy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $copy.Wait()
    $watch.Stop()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Bytes    = $stdout.ToArray()
        Stderr   = $stderrTask.Result
        Seconds  = $watch.Elapsed.TotalSeconds
    }
}
$script:UseVersion = $PSBoundParameters.ContainsKey('DesignModel')
$script:DesignModel = $DesignModel
$script:KbVersion = $KbVersion

Write-Host "Analyzing $full (read-only) ..."
$first = Invoke-Analyze
if ($first.ExitCode -ne 0) { Fail "kb-analyze exited with $($first.ExitCode): $($first.Stderr.Trim())" }
try { $result = [System.Text.Encoding]::UTF8.GetString($first.Bytes) | ConvertFrom-Json -Depth 20 }
catch { Fail "kb-analyze output is not valid JSON: $($_.Exception.Message)" }

$summary = $result.summary
Write-Host ''
Write-Host '=== kb-analyze result ===' -ForegroundColor Cyan
Write-Host ("Objects in catalog : {0}" -f $summary.objects)
Write-Host ("Procedures         : {0}" -f $summary.procedures)
Write-Host ("  analyzed         : {0}" -f $summary.analyzed)
Write-Host ("  skipped          : {0}" -f $summary.skipped)
Write-Host ("  rules withheld   : {0} object(s)" -f $summary.withheld)
Write-Host ("Findings           : {0}" -f $summary.findings)
Write-Host ("Wall time (run 1)  : {0:N2} s (engine process, including the catalog read)" -f $first.Seconds)

Write-Host ''
Write-Host 'Skipped by category:'
foreach ($property in $summary.skipped_by_category.PSObject.Properties) {
    Write-Host ("  {0,-14} {1}" -f $property.Name, $property.Value)
}
Write-Host 'Top skip reasons (location prefix removed):'
$result.skipped |
    ForEach-Object { '{0}: {1}' -f $_.category, ($_.reason -replace '^line \d+, column \d+: ', '') } |
    Group-Object | Sort-Object Count -Descending | Select-Object -First 8 |
    ForEach-Object { Write-Host ("  {0,6}  {1}" -f $_.Count, $_.Name) }

Write-Host ''
Write-Host 'Findings per rule:'
foreach ($rule in $result.rules) {
    $count = 0
    if (@($summary.findings_by_rule.PSObject.Properties.Name) -contains $rule.id) { $count = $summary.findings_by_rule.($rule.id) }
    Write-Host ("  {0,-7} {1,6}  {2} [{3}]" -f $rule.id, $count, $rule.title, $rule.severity)
}

Write-Host ''
Write-Host "=== First $Samples finding(s) per rule: open each in the GeneXus IDE and confirm ===" -ForegroundColor Cyan
foreach ($rule in $result.rules) {
    $mine = @($result.findings | Where-Object { $_.rule -eq $rule.id } | Select-Object -First $Samples)
    if ($mine.Count -eq 0) { continue }
    Write-Host ''
    Write-Host ("{0} {1}" -f $rule.id, $rule.title) -ForegroundColor Yellow
    foreach ($finding in $mine) {
        $where = if ($null -ne ($finding.PSObject.Properties['line'])) { "{0} part, line {1}, column {2}" -f $finding.part, $finding.line, $finding.column } else { "{0} part" -f $finding.part }
        Write-Host ("  {0}  ({1})" -f $finding.object, $where)
        Write-Host ("      {0}" -f $finding.message)
    }
}

# ---- second run: byte-identical -------------------------------------------------------------------------
Write-Host ''
Write-Host 'Second run for the determinism check ...'
$second = Invoke-Analyze
if ($second.ExitCode -ne 0) { Fail "second kb-analyze run exited with $($second.ExitCode): $($second.Stderr.Trim())" }
$hashA = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($first.Bytes))
$hashB = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($second.Bytes))
Write-Host ("Wall time (run 2)  : {0:N2} s" -f $second.Seconds)
Write-Host ("Run 1: {0} bytes, SHA-256 {1}" -f $first.Bytes.Length, $hashA)
Write-Host ("Run 2: {0} bytes, SHA-256 {1}" -f $second.Bytes.Length, $hashB)
if ($hashA -ne $hashB) { Fail 'the two runs produced different JSON: the analysis is not deterministic' }
Write-Host 'Determinism: the two runs are byte-identical.' -ForegroundColor Green

Write-Host ''
Write-Host 'NOT VERIFIED BY THIS SCRIPT: whether any finding is correct. Open a sample of each rule in the' -ForegroundColor Yellow
Write-Host 'GeneXus IDE (object, part, line, column above) and confirm it. Report every false finding.' -ForegroundColor Yellow
exit 0
