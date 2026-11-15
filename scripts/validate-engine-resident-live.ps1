#Requires -Version 7.0
<#
.SYNOPSIS
  Live validation of the resident engine (`gx-engine serve`) against a disposable test Knowledge Base.

.DESCRIPTION
  Runs every read the Gateway routes to gx-engine twice, once as a one-shot process (what the Gateway
  does by default) and once through one long-lived `gx-engine serve` process (GXMCP_ENGINE_RESIDENT=1),
  against the real Knowledge Base, and compares stdout, stderr and exit code. It measures the median
  latency of each path, checks that the resident process sees an object saved in the GeneXus IDE
  (the operator performs the save), and checks that closing stdin ends the process.

  Read-only: nothing is written to the Knowledge Base. -IncludeBuild also runs `rebuild-all --skip-compile`
  through both paths into temporary output folders (the engine reads the KB; it does not write to it).

  Only a folder directly under C:\KBs whose name contains "teste" is accepted. Cloud stays off: the
  script never calls the GeneXus SDK, MSBuild or any deploy.

  Output differences are compared after line endings are normalized to LF (the resident protocol is
  read line by line). Latency numbers are wall-clock times of one request as a host sees it, including
  the process start for the one-shot path; they say nothing about other Knowledge Bases or machines.

.PARAMETER KbPath
  The disposable Knowledge Base folder, for example C:\KBs\MeuKBteste.

.PARAMETER EngineExe
  Path to gx-engine.exe. Default: GXMCP_ENGINE_EXE, then src\gx-engine\target\release, then \debug.

.PARAMETER Runs
  Timed requests per read and path after one warm-up request (default 5).

.PARAMETER DesignModel
.PARAMETER KbVersion
  Passed to every read when the KB has several Design models or versions (both or neither).

.PARAMETER ObjectName
  A Procedure to read, inspect and diagnose. Default: the first one kb-list-procedures returns.

.PARAMETER IncludeBuild
  Also compare `rebuild-all --skip-compile` one-shot against resident.

.PARAMETER SkipIdeStep
  Skip the step that waits for an IDE save (the check is then reported as SKIPPED, not PASS).

.EXAMPLE
  pwsh scripts\validate-engine-resident-live.ps1 -KbPath C:\KBs\MeuKBteste
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KbPath,
    [string]$EngineExe,
    [ValidateRange(1, 100)][int]$Runs = 5,
    [string]$DesignModel,
    [string]$KbVersion,
    [string]$ObjectName,
    [switch]$IncludeBuild,
    [switch]$SkipIdeStep
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------- guard: disposable KBs only
$kbFull = [System.IO.Path]::GetFullPath($KbPath).TrimEnd('\', '/')
$parent = Split-Path -Parent $kbFull
$leaf = Split-Path -Leaf $kbFull
if ($kbFull -notlike 'C:\KBs\*teste*' -or $parent -ne 'C:\KBs' -or $leaf -notlike '*teste*') {
    Write-Host "REFUSED: '$kbFull' is not a disposable test KB. Only a folder directly under C:\KBs whose name contains 'teste' is accepted." -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath $kbFull -PathType Container)) {
    Write-Host "REFUSED: '$kbFull' does not exist." -ForegroundColor Red
    exit 2
}
if (($DesignModel -and -not $KbVersion) -or ($KbVersion -and -not $DesignModel)) {
    Write-Host 'REFUSED: pass -DesignModel and -KbVersion together or not at all.' -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- engine executable
$repo = Split-Path -Parent $PSScriptRoot
if (-not $EngineExe) { $EngineExe = $env:GXMCP_ENGINE_EXE }
if (-not $EngineExe) {
    $EngineExe = @('release', 'debug') |
        ForEach-Object { Join-Path $repo "src\gx-engine\target\$_\gx-engine.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $EngineExe -or -not (Test-Path -LiteralPath $EngineExe)) {
    Write-Host 'BLOCKED: gx-engine.exe not found. Build it (cargo build --release --manifest-path src\gx-engine\Cargo.toml) or pass -EngineExe.' -ForegroundColor Red
    exit 2
}
$EngineExe = (Resolve-Path -LiteralPath $EngineExe).Path

# ---------------------------------------------------------------- process helpers
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

public sealed class EngineRun { public string Out; public string Err; public int Exit; }

public static class EngineProcess
{
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    static ProcessStartInfo Start(string exe, bool stdin)
    {
        return new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = stdin, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            StandardInputEncoding = stdin ? Utf8 : null,
        };
    }

    public static EngineRun OneShot(string exe, string[] args, int timeoutMs)
    {
        var psi = Start(exe, false);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi))
        {
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException("one-shot process exceeded " + timeoutMs + " ms");
            }
            p.WaitForExit();
            return new EngineRun { Out = o.Result, Err = e.Result, Exit = p.ExitCode };
        }
    }

    static string Esc(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c < 0x20) b.AppendFormat("\\u{0:x4}", (int)c);
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    public sealed class Resident : IDisposable
    {
        readonly Process p;
        public string ReadyLine;

        public Resident(string exe, int timeoutMs)
        {
            var psi = Start(exe, true);
            psi.ArgumentList.Add("serve");
            p = Process.Start(psi);
            var ready = p.StandardOutput.ReadLineAsync();
            if (!ready.Wait(timeoutMs)) { Kill(); throw new TimeoutException("no ready line within " + timeoutMs + " ms"); }
            ReadyLine = ready.Result;
            if (ReadyLine == null || !ReadyLine.StartsWith("{\"gxe\":\"serve-ready\",\"protocol\":1,"))
            {
                Kill();
                throw new InvalidOperationException("unexpected first line: " + ReadyLine);
            }
        }

        public int ProcessId { get { return p.Id; } }
        public bool HasExited { get { return p.HasExited; } }

        public EngineRun Run(string[] args, int timeoutMs)
        {
            string id = Guid.NewGuid().ToString("N");
            var req = new StringBuilder("{\"id\":" + Esc(id) + ",\"args\":[");
            for (int i = 0; i < args.Length; i++) { if (i > 0) req.Append(','); req.Append(Esc(args[i])); }
            req.Append("]}");
            var so = Frame(p.StandardOutput, id, true);
            var se = Frame(p.StandardError, id, false);
            p.StandardInput.Write(req.ToString() + "\n");
            p.StandardInput.Flush();
            if (!Task.WaitAll(new Task[] { so, se }, timeoutMs))
            {
                Kill();
                throw new TimeoutException("resident request exceeded " + timeoutMs + " ms; process killed");
            }
            return new EngineRun { Out = so.Result.Item1, Err = se.Result.Item1, Exit = so.Result.Item2 };
        }

        static async Task<Tuple<string, int>> Frame(TextReader r, string id, bool withExit)
        {
            string begin = "{\"gxe\":\"serve-begin\",\"id\":\"" + id + "\"}";
            string first = await r.ReadLineAsync();
            if (first != begin) throw new InvalidDataException("stream out of step: " + first);
            string trailer = "{\"gxe\":\"serve-end\",\"id\":\"" + id + "\"";
            var lines = new List<string>();
            while (true)
            {
                string l = await r.ReadLineAsync();
                if (l == null) throw new EndOfStreamException("stream ended before the trailer");
                if (l.StartsWith(trailer))
                {
                    int exit = 0;
                    if (withExit)
                    {
                        const string key = "\"exit\":";
                        int at = l.IndexOf(key);
                        exit = int.Parse(l.Substring(at + key.Length).TrimEnd('}'));
                    }
                    // The process writes one line break before the trailer that is not the command's.
                    return Tuple.Create(string.Join("\n", lines), exit);
                }
                lines.Add(l);
            }
        }

        /// <summary>Closes stdin and waits for the process to end by itself.</summary>
        public bool CloseAndWait(int timeoutMs)
        {
            try { p.StandardInput.Close(); } catch { }
            return p.WaitForExit(timeoutMs);
        }

        public void Kill() { try { p.Kill(true); } catch { } }
        public void Dispose() { Kill(); p.Dispose(); }
    }
}
'@

$script:results = [System.Collections.Generic.List[object]]::new()
$script:failures = 0

function Add-Result([string]$Check, [string]$Status, [string]$Detail = '') {
    $script:results.Add([pscustomobject]@{ Check = $Check; Status = $Status; Detail = $Detail })
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0}] {1}{2}" -f $Status, $Check, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor $color
    if ($Status -eq 'FAIL') { $script:failures++ }
}

function ConvertTo-Lf([string]$Text) { if ($null -eq $Text) { '' } else { $Text -replace "`r`n", "`n" -replace "`r", "`n" } }

function Get-Median([double[]]$Values) {
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return $sorted[($n - 1) / 2] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

function Get-Excerpt([string]$Text) {
    $t = (ConvertTo-Lf $Text).Trim()
    if ($t.Length -gt 160) { $t = $t.Substring(0, 160) + '...' }
    return $t
}

$timeoutMs = 120000
$versionArgs = if ($DesignModel) { @('--design-model', $DesignModel, '--kb-version', $KbVersion) } else { @() }

function Find-FirstName([object]$Node) {
    if ($null -eq $Node) { return $null }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Node.PSObject.Properties) {
            if ($prop.Name -eq 'name' -and $prop.Value -is [string] -and $prop.Value) { return $prop.Value }
        }
        foreach ($prop in $Node.PSObject.Properties) {
            $found = Find-FirstName $prop.Value
            if ($found) { return $found }
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) {
            $found = Find-FirstName $item
            if ($found) { return $found }
        }
    }
    return $null
}

Write-Host "Engine : $EngineExe"
Write-Host "KB     : $kbFull"
Write-Host "Runs   : $Runs timed requests per read and path (after one warm-up)"
Write-Host ''

$resident = $null
try {
    # ------------------------------------------------------------ engine + resident handshake
    $version = [EngineProcess]::OneShot($EngineExe, @('--version'), 30000)
    if ($version.Exit -ne 0) { Add-Result 'engine --version' 'FAIL' "exit $($version.Exit)"; exit 1 }
    Add-Result 'engine --version' 'PASS' (Get-Excerpt $version.Out)

    try {
        $resident = [EngineProcess+Resident]::new($EngineExe, 30000)
        Add-Result 'resident process starts and announces protocol 1' 'PASS' $resident.ReadyLine
    }
    catch {
        Add-Result 'resident process starts and announces protocol 1' 'FAIL' $_.Exception.Message
        exit 1
    }

    # ------------------------------------------------------------ pick an object
    if (-not $ObjectName) {
        $list = [EngineProcess]::OneShot($EngineExe, (@('kb-list-procedures', $kbFull, '--limit', '5') + $versionArgs), $timeoutMs)
        if ($list.Exit -eq 0) {
            try { $ObjectName = Find-FirstName ($list.Out | ConvertFrom-Json -Depth 20) } catch { }
        }
        if (-not $ObjectName) {
            Add-Result 'discover a Procedure to read' 'FAIL' "kb-list-procedures exit $($list.Exit): $(Get-Excerpt ($list.Out + $list.Err)). Pass -ObjectName, and -DesignModel/-KbVersion if the KB has versions."
            exit 1
        }
    }
    Write-Host "Object : $ObjectName"
    Write-Host ''

    # ------------------------------------------------------------ routed reads: bytes and latency
    $reads = [ordered]@{
        'kb-environment'     = @('kb-environment', $kbFull)
        'kb-versions'        = @('kb-versions', $kbFull)
        'kb-query (type)'    = @('kb-query', $kbFull, '--type', 'Procedure', '--limit', '5')
        'kb-list-procedures' = @('kb-list-procedures', $kbFull, '--offset', '0', '--limit', '5')
        'kb-list-objects'    = @('kb-list-objects', $kbFull, '--offset', '0', '--limit', '5')
        'kb-read'            = @('kb-read', $kbFull, $ObjectName)
        'kb-inspect'         = @('kb-inspect', $kbFull, $ObjectName)
        'kb-diagnose'        = @('--diagnostics=json', 'kb-diagnose', $kbFull, $ObjectName)
    }
    $latency = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $reads.Keys) {
        $arguments = [string[]]($reads[$name] + $versionArgs)
        try {
            $one = [EngineProcess]::OneShot($EngineExe, $arguments, $timeoutMs)
            $res = $resident.Run($arguments, $timeoutMs)
        }
        catch {
            Add-Result "$name one-shot == resident" 'FAIL' $_.Exception.Message
            if ($resident.HasExited) { exit 1 }
            continue
        }
        $same = ((ConvertTo-Lf $one.Out) -ceq (ConvertTo-Lf $res.Out)) -and
                ((ConvertTo-Lf $one.Err) -ceq (ConvertTo-Lf $res.Err)) -and ($one.Exit -eq $res.Exit)
        if ($same) {
            $note = "exit $($one.Exit), stdout $($one.Out.Length) chars, stderr $($one.Err.Length) chars"
            if ($one.Exit -ne 0) { $note += " (the one-shot command itself refused: $(Get-Excerpt $one.Err))" }
            Add-Result "$name one-shot == resident" 'PASS' $note
        }
        else {
            Add-Result "$name one-shot == resident" 'FAIL' "one-shot exit $($one.Exit) / resident exit $($res.Exit); stdout equal: $((ConvertTo-Lf $one.Out) -ceq (ConvertTo-Lf $res.Out)); stderr equal: $((ConvertTo-Lf $one.Err) -ceq (ConvertTo-Lf $res.Err))"
            continue
        }

        $oneTimes = @(); $resTimes = @()
        for ($i = 0; $i -lt $Runs; $i++) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            [void][EngineProcess]::OneShot($EngineExe, $arguments, $timeoutMs)
            $oneTimes += $sw.Elapsed.TotalMilliseconds
            $sw.Restart()
            [void]$resident.Run($arguments, $timeoutMs)
            $resTimes += $sw.Elapsed.TotalMilliseconds
        }
        $latency.Add([pscustomobject]@{
                Read = $name
                'OneShot median ms' = [math]::Round((Get-Median $oneTimes), 1)
                'Resident median ms' = [math]::Round((Get-Median $resTimes), 1)
            })
    }
    if ($latency.Count -gt 0) {
        Write-Host ''
        Write-Host "Median wall-clock latency over $Runs runs per read (measured here, this KB and machine only):"
        $latency | Format-Table -AutoSize | Out-String | Write-Host
    }

    # ------------------------------------------------------------ freshness: IDE save
    $freshArgs = [string[]](@('kb-read', $kbFull, $ObjectName) + $versionArgs)
    if ($SkipIdeStep) {
        Add-Result 'resident process sees an object saved in the IDE' 'SKIPPED' '-SkipIdeStep was given'
    }
    else {
        $before = $resident.Run($freshArgs, $timeoutMs)
        $beforeOne = [EngineProcess]::OneShot($EngineExe, $freshArgs, $timeoutMs)
        Write-Host ''
        Write-Host "IDE STEP: open this KB in the GeneXus IDE, change the source of '$ObjectName' (add a comment line), SAVE the object (Ctrl+S)," -ForegroundColor Cyan
        Write-Host 'then press Enter here. Type "skip" and Enter to skip this check.' -ForegroundColor Cyan
        $answer = Read-Host 'Saved?'
        if ($answer -eq 'skip') {
            Add-Result 'resident process sees an object saved in the IDE' 'SKIPPED' 'the operator skipped it'
        }
        else {
            $after = $resident.Run($freshArgs, $timeoutMs)
            $afterOne = [EngineProcess]::OneShot($EngineExe, $freshArgs, $timeoutMs)
            $oneChanged = (ConvertTo-Lf $afterOne.Out) -cne (ConvertTo-Lf $beforeOne.Out)
            $resChanged = (ConvertTo-Lf $after.Out) -cne (ConvertTo-Lf $before.Out)
            $agree = ((ConvertTo-Lf $after.Out) -ceq (ConvertTo-Lf $afterOne.Out)) -and ($after.Exit -eq $afterOne.Exit)
            if (-not $oneChanged) {
                Add-Result 'resident process sees an object saved in the IDE' 'FAIL' 'inconclusive: a one-shot read did not see a change either (was the object saved? the IDE writes the KB on save). Run again.'
            }
            elseif ($resChanged -and $agree) {
                Add-Result 'resident process sees an object saved in the IDE' 'PASS' 'the resident read changed after the save and equals a fresh one-shot read'
            }
            else {
                Add-Result 'resident process sees an object saved in the IDE' 'FAIL' "STALE ANSWER: one-shot saw the change; resident changed: $resChanged, equals one-shot: $agree"
            }
        }
    }

    # ------------------------------------------------------------ optional build
    if ($IncludeBuild) {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) ("gxe-resident-live-" + [guid]::NewGuid().ToString('N'))
        $outOne = Join-Path $work 'oneshot'
        $outRes = Join-Path $work 'resident'
        try {
            New-Item -ItemType Directory -Force -Path $outOne, $outRes | Out-Null
            $buildBase = @('rebuild-all', '--kb', $kbFull, '--skip-compile') + $versionArgs
            $one = [EngineProcess]::OneShot($EngineExe, [string[]]($buildBase + @('--output', $outOne)), 600000)
            $res = $resident.Run([string[]]($buildBase + @('--output', $outRes)), 600000)
            $norm = {
                param($text, $dir)
                (ConvertTo-Lf $text).Replace($dir, '<OUT>')
            }
            $sameBuild = ((& $norm $one.Out $outOne) -ceq (& $norm $res.Out $outRes)) -and
                         ((& $norm $one.Err $outOne) -ceq (& $norm $res.Err $outRes)) -and ($one.Exit -eq $res.Exit)
            if ($sameBuild) {
                Add-Result 'rebuild-all --skip-compile one-shot == resident' 'PASS' "exit $($one.Exit) (0 complete, 3 partial)"
            }
            else {
                Add-Result 'rebuild-all --skip-compile one-shot == resident' 'FAIL' "one-shot exit $($one.Exit) / resident exit $($res.Exit); output folders normalized"
            }
            $filesOne = @(Get-ChildItem -LiteralPath $outOne -Recurse -File | ForEach-Object { $_.FullName.Substring($outOne.Length) } | Sort-Object)
            $filesRes = @(Get-ChildItem -LiteralPath $outRes -Recurse -File | ForEach-Object { $_.FullName.Substring($outRes.Length) } | Sort-Object)
            if ((($filesOne -join "`n") -ceq ($filesRes -join "`n"))) {
                Add-Result 'rebuild-all generated the same file list' 'PASS' "$($filesOne.Count) files"
            }
            else {
                Add-Result 'rebuild-all generated the same file list' 'FAIL' "one-shot $($filesOne.Count) files, resident $($filesRes.Count) files"
            }
        }
        finally {
            if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        }
    }
    else {
        Add-Result 'rebuild-all one-shot == resident' 'SKIPPED' 'pass -IncludeBuild to run it'
    }

    # ------------------------------------------------------------ the process ends with its stdin
    $pidValue = $resident.ProcessId
    if ($resident.CloseAndWait(10000)) {
        Add-Result 'resident process ends when its stdin closes' 'PASS' "pid $pidValue"
    }
    else {
        Add-Result 'resident process ends when its stdin closes' 'FAIL' "pid $pidValue still running after 10 s"
    }
}
finally {
    if ($null -ne $resident) { $resident.Dispose() }
}

# ---------------------------------------------------------------- summary
Write-Host ''
Write-Host 'Summary' -ForegroundColor Cyan
$script:results | Format-Table Status, Check -AutoSize | Out-String | Write-Host
$skipped = @($script:results | Where-Object Status -eq 'SKIPPED').Count
if ($script:failures -gt 0) {
    Write-Host "FAILED: $($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "PASSED: every executed check passed ($skipped skipped)." -ForegroundColor Green
exit 0
