#requires -Version 7.0
<#
.SYNOPSIS
  Checks gx-engine's incremental rebuild and artifact cache against a real, disposable Knowledge Base.

.DESCRIPTION
  Runs `gx-engine rebuild-all` on a `C:\KBs\*teste*` Knowledge Base and compares output directories byte
  for byte. Every output goes to a new directory under -WorkDir (default: a new directory under the
  temporary folder), never into the Knowledge Base's own web or bin folders.

  Checks, each printed as PASS or FAIL:
    a) a full rebuild into directory A runs and reuses nothing;
    b) a second run into A reports an unchanged model and leaves A as it was;
    c) after the operator edits one object in the GeneXus IDE, a run into A is incremental and A equals a
       fresh full rebuild into directory B;
    d) with the artifact cache enabled, a rebuild into an empty directory C restores from the cache and
       equals B.

  "Equals" compares the names and SHA-256 of every file outside `obj\` and `bin\`. Those two folders hold
  build state and compiler outputs; an assembly compiled in another folder embeds that folder's path, so
  it is not expected to be identical and is not compared.

  Every run uses one fixed generation stamp (GXMCP_ENGINE_GENERATION_STAMP): a web class holds the time it
  was generated, so runs at different times compare byte for byte only under one stamp.

  The script reads the Knowledge Base through gx-engine and writes only under -WorkDir. The one change to
  the Knowledge Base is the edit the operator makes in the IDE for check (c).

.PARAMETER KbPath
  The Knowledge Base folder. Must be directly under C:\KBs and its name must contain "teste".

.PARAMETER EngineExe
  The gx-engine executable. Default: src\gx-engine\target\release\gx-engine.exe of this checkout.

.PARAMETER WorkDir
  Where the output directories and the cache are created. Must not exist, or be empty, and must not be
  inside the Knowledge Base.

.PARAMETER SkipCompile
  Passes --skip-compile: only generation is checked, and `dotnet` is not needed.

.EXAMPLE
  pwsh -NoProfile -File scripts\validate-engine-incremental-live.ps1 -KbPath C:\KBs\KBTeste
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KbPath,
    [string]$EngineExe,
    [string]$WorkDir,
    [switch]$SkipCompile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

# --- Guards: nothing below runs on a Knowledge Base that is not a disposable test one. ---
$kb = [System.IO.Path]::GetFullPath($KbPath).TrimEnd('\', '/')
$kbParent = [System.IO.Path]::GetDirectoryName($kb)
$kbLeaf = [System.IO.Path]::GetFileName($kb)
if ($kbParent -ine 'C:\KBs' -or $kbLeaf -notlike '*teste*') {
    Write-Error "Refused: '$kb' is not a C:\KBs\*teste* Knowledge Base. This script only runs on disposable test Knowledge Bases."
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $kb 'knowledgebase.connection') -PathType Leaf)) {
    Write-Error "Refused: '$kb' has no knowledgebase.connection, so it is not a Knowledge Base folder."
    exit 2
}

if (-not $EngineExe) {
    $EngineExe = Join-Path $repoRoot 'src\gx-engine\target\release\gx-engine.exe'
}
if (-not (Test-Path -LiteralPath $EngineExe -PathType Leaf)) {
    Write-Error "gx-engine not found at '$EngineExe'. Build it with: cargo build --release --manifest-path src\gx-engine\Cargo.toml"
    exit 2
}
$EngineExe = (Resolve-Path -LiteralPath $EngineExe).Path

if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gx-incremental-live-" + [System.Guid]::NewGuid().ToString('N'))
}
$work = [System.IO.Path]::GetFullPath($WorkDir).TrimEnd('\', '/')
if ($work -ieq $kb -or $work.StartsWith($kb + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Refused: -WorkDir '$work' is inside the Knowledge Base. Outputs are never written there."
    exit 2
}
if ((Test-Path -LiteralPath $work) -and @(Get-ChildItem -LiteralPath $work -Force).Count -gt 0) {
    Write-Error "Refused: -WorkDir '$work' is not empty. Use a new directory so no earlier output is compared."
    exit 2
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

$dirA = Join-Path $work 'A'
$dirB = Join-Path $work 'B'
$dirC = Join-Path $work 'C'
$cacheDir = Join-Path $work 'cache'

$checks = [System.Collections.Generic.List[object]]::new()
$timings = [System.Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $label = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}{2}" -f $label, $Name, $(if ($Detail) { ": $Detail" } else { '' }))
}

# Runs one rebuild. Returns the parsed report (or $null), the exit code, stderr and the wall time.
function Invoke-Rebuild([string]$Step, [string]$OutputDir, [bool]$UseCache) {
    $arguments = @('rebuild-all', '--kb', $kb, '--output', $OutputDir)
    if ($SkipCompile) { $arguments += '--skip-compile' }
    if ($UseCache) { $env:GXMCP_ENGINE_BUILD_CACHE_DIR = $cacheDir }
    else { Remove-Item Env:GXMCP_ENGINE_BUILD_CACHE_DIR -ErrorAction SilentlyContinue }
    $stderrFile = Join-Path $work "$Step.stderr.log"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $stdout = & $EngineExe @arguments 2> $stderrFile
    $exitCode = $LASTEXITCODE
    $watch.Stop()
    $stdoutText = ($stdout -join "`n")
    Set-Content -LiteralPath (Join-Path $work "$Step.report.json") -Value $stdoutText -Encoding utf8
    $report = $null
    try { $report = $stdoutText | ConvertFrom-Json -Depth 64 } catch { $report = $null }
    # A report of this engine: an object with the rebuild fields and the `reuse` summary.
    if ($report -isnot [pscustomobject] -or -not $report.PSObject.Properties['reuse'] -or -not $report.PSObject.Properties['emitted_files']) {
        $report = $null
    }
    $stderr = Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue
    $script:timings.Add([pscustomobject]@{
            Step        = $Step
            Exit        = $exitCode
            WallMs      = [int]$watch.ElapsedMilliseconds
            CatalogMs   = if ($report) { $report.catalog_load_ms } else { $null }
            ReadMs      = if ($report) { $report.read_ms } else { $null }
            SpecMs      = if ($report) { $report.spec_ms } else { $null }
            EmitMs      = if ($report) { $report.emit_ms } else { $null }
            CompileMs   = if ($report) { $report.compile_ms } else { $null }
            PipelineMs  = if ($report) { $report.total_ms } else { $null }
            Mode        = if ($report) { $report.reuse.mode } else { $null }
            Status      = if ($report) { $report.status } else { $null }
        })
    [pscustomobject]@{ Report = $report; ExitCode = $exitCode; Stderr = $stderr }
}

# A run is usable when it printed a report. Exit 0 is complete, 3 partial, 1 failed with a report: a
# Knowledge Base with refused objects still generates the rest, and the comparison covers that state.
function Test-RunUsable($Run) {
    $null -ne $Run.Report -and $Run.ExitCode -in 0, 1, 3
}

# Every file outside obj\ and bin\, by relative path, with its SHA-256.
function Get-Tree([string]$Root) {
    $tree = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $tree }
    $prefix = $Root.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($prefix.Length)
        if ($relative -like 'obj\*' -or $relative -like 'bin\*') { continue }
        $tree[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $tree
}

# The differences between two trees: names only in one, and names whose bytes differ (case-sensitive names).
function Compare-Tree($Left, $Right, [string]$LeftName, [string]$RightName) {
    $differences = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $Left.Keys) {
        if (-not $Right.ContainsKey($name)) { $differences.Add("only in ${LeftName}: $name") }
        elseif ($Left[$name] -ne $Right[$name]) { $differences.Add("differs: $name") }
        elseif (-not ($Right.Keys -ccontains $name)) { $differences.Add("name case differs: $name") }
    }
    foreach ($name in $Right.Keys) {
        if (-not $Left.ContainsKey($name)) { $differences.Add("only in ${RightName}: $name") }
    }
    $differences
}

function Format-Differences($Differences) {
    if ($Differences.Count -eq 0) { return '' }
    $shown = @($Differences | Select-Object -First 10) -join '; '
    if ($Differences.Count -gt 10) { $shown += "; ... $($Differences.Count - 10) more" }
    $shown
}

# What two runs of one model must agree on, whatever each reused.
function Get-Outcome($Report, [string]$Root) {
    $prefix = $Root.TrimEnd('\') + '\'
    [ordered]@{
        status         = $Report.status
        errors         = @($Report.errors | ForEach-Object { $_.Replace($prefix, '') })
        generated      = $Report.generated_count
        skipped        = $Report.skipped_count
        not_generated  = $Report.not_generated_count
        refused        = $Report.refused_count
        emitted        = @($Report.emitted_files | ForEach-Object { $_.Substring($prefix.Length) })
    } | ConvertTo-Json -Depth 8 -Compress
}

$savedCache = $env:GXMCP_ENGINE_BUILD_CACHE_DIR
$savedStamp = $env:GXMCP_ENGINE_GENERATION_STAMP
$savedJson = $env:GXMCP_JSON_REPORT
try {
    $env:GXMCP_JSON_REPORT = '1'
    $env:GXMCP_ENGINE_GENERATION_STAMP = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')

    Write-Host "Knowledge Base : $kb"
    Write-Host "Engine         : $EngineExe"
    Write-Host "Work directory : $work"
    Write-Host ''

    # --- (a) full rebuild into A ---
    $runA = Invoke-Rebuild 'a-full' $dirA $false
    $usable = Test-RunUsable $runA
    Add-Check '(a) full rebuild into A' ($usable -and $runA.Report.reuse.mode -eq 'full' -and @($runA.Report.emitted_files).Count -gt 0) `
        $(if (-not $usable) { "no usable report (exit $($runA.ExitCode)); see $work\a-full.stderr.log" }
          else { "status $($runA.Report.status), mode $($runA.Report.reuse.mode), $(@($runA.Report.emitted_files).Count) files" })
    if (-not $usable) { throw 'the first rebuild did not run; nothing else can be checked' }
    if ($runA.Report.status -eq 'failed') {
        Write-Host "note: the Knowledge Base builds with errors ($(@($runA.Report.errors).Count)); the comparisons below cover that state."
    }
    $treeA = Get-Tree $dirA

    # --- (b) no-op run into A ---
    $runB = Invoke-Rebuild 'b-noop' $dirA $false
    $usable = Test-RunUsable $runB
    $differences = Compare-Tree (Get-Tree $dirA) $treeA 'A after' 'A before'
    Add-Check '(b) no-op run reports an unchanged model' ($usable -and $runB.Report.reuse.mode -eq 'unchanged') `
        $(if ($usable) { "mode $($runB.Report.reuse.mode)" } else { "no usable report (exit $($runB.ExitCode))" })
    Add-Check '(b) no-op run leaves A as it was' ($usable -and $differences.Count -eq 0) (Format-Differences $differences)
    Add-Check '(b) no-op run reports the same outcome' ($usable -and (Get-Outcome $runB.Report $dirA) -eq (Get-Outcome $runA.Report $dirA)) ''

    # --- (c) the operator edits one object; incremental into A equals a fresh full rebuild into B ---
    Write-Host ''
    Write-Host 'ACTION: in the GeneXus IDE, open this Knowledge Base, change ONE object (for example a line of a'
    Write-Host '        Procedure''s Source) and SAVE it. Do not build in the IDE.'
    [void](Read-Host 'Press Enter when the object is saved')
    $runC = Invoke-Rebuild 'c-incremental' $dirA $false
    $usableC = Test-RunUsable $runC
    Add-Check '(c) the run after the edit is incremental' ($usableC -and $runC.Report.reuse.mode -eq 'incremental') `
        $(if (-not $usableC) { "no usable report (exit $($runC.ExitCode))" }
          elseif ($runC.Report.reuse.mode -eq 'unchanged') { 'the engine read the same model: the edit was not saved, or is not in the model the build reads' }
          else { "mode $($runC.Report.reuse.mode)$(if ($runC.Report.reuse.PSObject.Properties['reason']) { ' (' + $runC.Report.reuse.reason + ')' }), $($runC.Report.reuse.procedures_kept) Procedures kept, $($runC.Report.reuse.procedures_emitted) emitted" })

    # B is built with the cache enabled and empty, so it is a full rebuild that also fills the cache for (d).
    $runFresh = Invoke-Rebuild 'c-fresh-full' $dirB $true
    $usableFresh = Test-RunUsable $runFresh
    Add-Check '(c) fresh full rebuild into B' ($usableFresh -and $runFresh.Report.reuse.mode -eq 'full' -and $runFresh.Report.reuse.cache_entries_restored -eq 0) `
        $(if ($usableFresh) { "status $($runFresh.Report.status), $($runFresh.Report.reuse.cache_entries_stored) cache entries stored" } else { "no usable report (exit $($runFresh.ExitCode))" })
    $treeB = Get-Tree $dirB
    $differences = Compare-Tree (Get-Tree $dirA) $treeB 'A' 'B'
    Add-Check '(c) A after the incremental run equals B, byte for byte' ($usableC -and $usableFresh -and $treeB.Count -gt 0 -and $differences.Count -eq 0) `
        $(if ($differences.Count -eq 0) { "$($treeB.Count) files" } else { Format-Differences $differences })
    Add-Check '(c) the incremental run reports the same outcome as the fresh one' `
        ($usableC -and $usableFresh -and (Get-Outcome $runC.Report $dirA) -eq (Get-Outcome $runFresh.Report $dirB)) ''

    # --- (d) empty directory C, restored from the cache, equals B ---
    $runCache = Invoke-Rebuild 'd-cache' $dirC $true
    $usableCache = Test-RunUsable $runCache
    Add-Check '(d) the rebuild into C restores from the cache' `
        ($usableCache -and $runCache.Report.reuse.cache_entries_restored -gt 0 -and $runCache.Report.reuse.cache_entries_stored -eq 0) `
        $(if ($usableCache) { "$($runCache.Report.reuse.cache_entries_restored) entries restored, $($runCache.Report.reuse.cache_entries_stored) stored" } else { "no usable report (exit $($runCache.ExitCode))" })
    $differences = Compare-Tree (Get-Tree $dirC) $treeB 'C' 'B'
    Add-Check '(d) C equals B, byte for byte' ($usableCache -and $differences.Count -eq 0) `
        $(if ($differences.Count -eq 0) { "$($treeB.Count) files" } else { Format-Differences $differences })
    Add-Check '(d) the cached run reports the same outcome as the fresh one' `
        ($usableCache -and $usableFresh -and (Get-Outcome $runCache.Report $dirC) -eq (Get-Outcome $runFresh.Report $dirB)) ''
}
catch {
    Add-Check 'script completed' $false $_.Exception.Message
}
finally {
    foreach ($pair in @(
            @('GXMCP_ENGINE_BUILD_CACHE_DIR', $savedCache),
            @('GXMCP_ENGINE_GENERATION_STAMP', $savedStamp),
            @('GXMCP_JSON_REPORT', $savedJson))) {
        if ($null -eq $pair[1]) { Remove-Item "Env:$($pair[0])" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$($pair[0])" $pair[1] }
    }
}

Write-Host ''
Write-Host 'Timings (WallMs is the whole process; the other columns are the engine''s own report, in ms):'
$timings | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Reports, stderr logs and the output directories are under: $work"

$failed = @($checks | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) {
    Write-Host ("RESULT: FAIL ({0} of {1} checks failed)" -f $failed.Count, $checks.Count)
    exit 1
}
Write-Host ("RESULT: PASS ({0} checks)" -f $checks.Count)
exit 0
