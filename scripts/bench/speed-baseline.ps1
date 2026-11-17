<#
.SYNOPSIS
    Repeatable CPU, latency & startup baseline for Genexus18MCP against disposable *teste* KBs.
.DESCRIPTION
    Measures:
    1. Cold start: Gateway start -> initialize, first tool answer, tools/list, Worker spawn -> KB open -> ready.
    2. Index build/load time: cold (force=true) and warm (from cache on fresh Gateway).
    3. Per-tool latency p50/p95 over 30 runs for common read tools (list, query, search_source, read, analyze) and one dry-run write (create_object dryRun=true).
    4. CPU seconds per process for the workload, and idle CPU over 120 seconds with nothing happening.
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "publish"),
    [string[]]$KBs = @("C:\KBs\KBTeste955", "C:\KBs\KBTeste"),
    [int]$Iterations = 30,
    [int]$IdleSeconds = 120,
    [string]$OutJson = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

foreach ($kbPath in $KBs) {
    if ($kbPath -notmatch '^[A-Za-z]:\\KBs\\[^\\]*teste[^\\]*$') {
        throw "Refusing non-disposable KB path '$kbPath'. Only C:\KBs\*teste* paths are allowed."
    }
    if (-not (Test-Path $kbPath)) {
        throw "KB path not found: $kbPath"
    }
}

$gatewayExe = Join-Path $PublishDir "GxMcp.Gateway.exe"
$workerExe = Join-Path $PublishDir "worker\GxMcp.Worker.exe"
if (-not (Test-Path $gatewayExe)) { throw "Gateway executable not found: $gatewayExe" }
if (-not (Test-Path $workerExe)) { throw "Worker executable not found: $workerExe" }

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("gxmcp_speed_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$logDir = Join-Path $tempRoot "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$kbEntries = @()
for ($i = 0; $i -lt $KBs.Count; $i++) {
    $kbEntries += [ordered]@{
        Alias = "kb$i"
        Path  = $KBs[$i]
    }
}

$configObj = [ordered]@{
    ConfigSchemaVersion = 2
    GatewayMode         = "stdio-isolated"
    GeneXus             = [ordered]@{
        InstallationPath = "C:\Program Files (x86)\GeneXus\GeneXus18"
        WorkerExecutable = $workerExe
    }
    Server              = [ordered]@{
        HttpPort                  = 0
        McpStdio                  = $true
        WorkerSharingMode         = "isolated"
        MaxOpenKbs                = [Math]::Max(3, $KBs.Count)
        SessionIdleTimeoutMinutes = 10
        WorkerIdleTimeoutMinutes  = 30
        EmitStructuredContent     = $false
        TerseResponses            = $true
    }
    Environment         = [ordered]@{
        ResolutionPolicy = "strict"
        KBs              = $kbEntries
        DefaultKb        = "kb0"
    }
}

$configPath = Join-Path $tempRoot "config.json"
[System.IO.File]::WriteAllText($configPath, ($configObj | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))

function Get-ProcessTreeSamples {
    param([int]$RootPid)
    $allProcs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    if (-not $allProcs) { return @() }

    $byParent = @{}
    foreach ($p in $allProcs) {
        $ppid = [int]$p.ParentProcessId
        if (-not $byParent.ContainsKey($ppid)) { $byParent[$ppid] = New-Object System.Collections.Generic.List[int] }
        $byParent[$ppid].Add([int]$p.ProcessId)
    }

    $visited = New-Object System.Collections.Generic.HashSet[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($RootPid)
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if (-not $visited.Add($cur)) { continue }
        if ($byParent.ContainsKey($cur)) {
            foreach ($child in $byParent[$cur]) { $queue.Enqueue($child) }
        }
    }

    $samples = @()
    foreach ($pidInt in $visited) {
        try {
            $gp = Get-Process -Id $pidInt -ErrorAction Stop
            $role = if ($pidInt -eq $RootPid) { "Gateway" }
                    elseif ($gp.ProcessName -eq "GxMcp.Worker") { "Worker" }
                    elseif ($gp.ProcessName -eq "gx-engine") { "Rust-gx-engine" }
                    elseif ($gp.ProcessName -eq "gxwire") { "Rust-gxwire" }
                    else { $gp.ProcessName }
            $samples += [pscustomobject]@{
                Pid       = $pidInt
                Name      = $gp.ProcessName
                Role      = $role
                CpuSec    = $gp.TotalProcessorTime.TotalSeconds
                PrivateMB = [Math]::Round($gp.PrivateMemorySize64 / 1MB, 2)
            }
        } catch { }
    }
    return $samples
}

function Start-GatewaySession {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $gatewayExe
    $psi.WorkingDirectory = $PublishDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.Environment["GX_CONFIG_PATH"] = $configPath
    $psi.Environment["GXMCP_LOG_DIR"] = $logDir
    $psi.Environment["GENEXUS_MCP_NO_UPDATE_CHECK"] = "1"

    $swBoot = [Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdin = [System.IO.StreamWriter]::new($proc.StandardInput.BaseStream, [System.Text.UTF8Encoding]::new($false))
    $stdin.AutoFlush = $true
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    return [pscustomobject]@{
        Proc       = $proc
        Stdin      = $stdin
        StderrTask = $stderrTask
        SwBoot     = $swBoot
        ReqId      = 0
    }
}

function Stop-GatewaySession {
    param($Session)
    if (-not $Session) { return }
    try {
        $rows = Get-ProcessTreeSamples -RootPid $Session.Proc.Id
        foreach ($r in $rows) {
            if ($r.Pid -ne $Session.Proc.Id) {
                Stop-Process -Id $r.Pid -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
    try { if ($Session.Stdin) { $Session.Stdin.Dispose() } } catch { }
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    try { $Session.Proc.Dispose() } catch { }
}

function Invoke-Mcp {
    param($Session, [string]$Method, [hashtable]$Params)
    $Session.ReqId++
    $id = $Session.ReqId
    $payload = @{
        jsonrpc = "2.0"
        id      = $id
        method  = $Method
        params  = $Params
    } | ConvertTo-Json -Depth 12 -Compress
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $Session.Stdin.WriteLine($payload)
    while ($true) {
        $line = $Session.Proc.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Gateway closed stdout unexpectedly while waiting for id=$id ($Method)." }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $null
        try { $msg = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($msg.PSObject.Properties["id"] -and $msg.id -eq $id) {
            $sw.Stop()
            return [pscustomobject]@{ Resp = $msg; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
        }
    }
}

function Invoke-Tool {
    param($Session, [string]$Name, [hashtable]$Arguments, [switch]$AllowToolError)
    $r = Invoke-Mcp -Session $Session -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments }
    if ($r.Resp.PSObject.Properties["error"] -and $r.Resp.error) {
        throw "Tool $Name RPC failed: $($r.Resp.error | ConvertTo-Json -Compress)"
    }
    $text = ""
    if ($r.Resp.result -and $r.Resp.result.content -and $r.Resp.result.content.Count -gt 0) {
        $text = $r.Resp.result.content[0].text
    }
    $isErr = [bool]($r.Resp.result.isError)
    if ($isErr -and -not $AllowToolError) {
        throw "Tool $Name returned isError=true: $text"
    }
    return [pscustomobject]@{
        Raw       = $r.Resp
        Text      = $text
        IsError   = $isErr
        ElapsedMs = $r.ElapsedMs
    }
}

function Wait-IndexReady {
    param($Session, [string]$Alias, [int]$TimeoutSec = 180)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $st = Invoke-Tool -Session $Session -Name "genexus_lifecycle" -Arguments @{ action = "status"; wait = 10; kb = $Alias }
        if ($st.Text -notmatch '"indexState"\s*:\s*"(cold|reindexing|indexing)"' -and $st.Text -notmatch '"status"\s*:\s*"(Cold|Indexing|Running)"') {
            $sw.Stop()
            return $sw.Elapsed.TotalMilliseconds
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Index on $Alias did not reach Ready within ${TimeoutSec}s."
}

function Get-Percentile {
    param([double[]]$Samples, [double]$Pct)
    if ($Samples.Count -eq 0) { return 0.0 }
    $sorted = $Samples | Sort-Object
    $k = ($Pct / 100.0) * ($sorted.Count - 1)
    $lo = [Math]::Floor($k)
    $hi = [Math]::Ceiling($k)
    if ($lo -eq $hi) { return [Math]::Round([double]$sorted[[int]$lo], 2) }
    $val = [double]$sorted[[int]$lo] * (1.0 - ($k - $lo)) + [double]$sorted[[int]$hi] * ($k - $lo)
    return [Math]::Round($val, 2)
}

function Summarize-Latencies {
    param([double[]]$Samples)
    if ($Samples.Count -eq 0) { return @{ count = 0; min = 0; p50 = 0; p95 = 0; max = 0; avg = 0 } }
    $m = $Samples | Measure-Object -Minimum -Maximum -Average
    return [ordered]@{
        count = $Samples.Count
        min   = [Math]::Round($m.Minimum, 2)
        p50   = Get-Percentile $Samples 50
        p95   = Get-Percentile $Samples 95
        max   = [Math]::Round($m.Maximum, 2)
        avg   = [Math]::Round($m.Average, 2)
    }
}

$session = $null
$warmSession = $null
try {
    # -------------------------------------------------------------------------
    # Phase 1: Cold start + Cold index build (on kb0 = KBTeste955)
    # -------------------------------------------------------------------------
    Write-Host "=== Phase 1: Cold start & Cold index ===" -ForegroundColor Cyan
    $session = Start-GatewaySession
    $null = Invoke-Mcp -Session $session -Method "initialize" -Params @{
        protocolVersion = "2025-06-18"
        capabilities    = @{}
        clientInfo      = @{ name = "speed-baseline"; version = "1.0.0" }
    }
    $gatewayStartToInitMs = [Math]::Round($session.SwBoot.Elapsed.TotalMilliseconds, 2)
    $session.Stdin.WriteLine((@{ jsonrpc = "2.0"; method = "notifications/initialized" } | ConvertTo-Json -Compress))

    $firstToolResp = Invoke-Tool -Session $session -Name "genexus_whoami" -Arguments @{}
    $gatewayStartToFirstToolMs = [Math]::Round($session.SwBoot.Elapsed.TotalMilliseconds, 2)

    $toolsListResp = Invoke-Mcp -Session $session -Method "tools/list" -Params @{}
    $toolsListMs = [Math]::Round($toolsListResp.ElapsedMs, 2)

    $swOpen = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-Tool -Session $session -Name "genexus_kb" -Arguments @{ action = "open"; alias = "kb0" }
    $kbOpenCallMs = [Math]::Round($swOpen.Elapsed.TotalMilliseconds, 2)
    $null = Wait-IndexReady -Session $session -Alias "kb0"
    $workerSpawnKbOpenToReadyMs = [Math]::Round($swOpen.Elapsed.TotalMilliseconds, 2)

    # Cold index build (force=true)
    $swColdIdx = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-Tool -Session $session -Name "genexus_lifecycle" -Arguments @{ action = "index"; force = $true; kb = "kb0" }
    $null = Wait-IndexReady -Session $session -Alias "kb0"
    $coldIndexMs = [Math]::Round($swColdIdx.Elapsed.TotalMilliseconds, 2)

    Write-Host ("  Gateway start -> initialize : {0,8:N1} ms" -f $gatewayStartToInitMs)
    Write-Host ("  Gateway start -> first tool : {0,8:N1} ms" -f $gatewayStartToFirstToolMs)
    Write-Host ("  tools/list                  : {0,8:N1} ms" -f $toolsListMs)
    Write-Host ("  KB open call (kb0)          : {0,8:N1} ms" -f $kbOpenCallMs)
    Write-Host ("  Worker spawn->KB open->ready: {0,8:N1} ms" -f $workerSpawnKbOpenToReadyMs)
    Write-Host ("  Cold index build (kb0)      : {0,8:N1} ms" -f $coldIndexMs)

    Stop-GatewaySession $session
    $session = $null

    # -------------------------------------------------------------------------
    # Phase 2: Warm start from cache + Workload + Idle CPU
    # -------------------------------------------------------------------------
    Write-Host "`n=== Phase 2: Warm start from cache ===" -ForegroundColor Cyan
    $warmSession = Start-GatewaySession
    $null = Invoke-Mcp -Session $warmSession -Method "initialize" -Params @{
        protocolVersion = "2025-06-18"
        capabilities    = @{}
        clientInfo      = @{ name = "speed-baseline-warm"; version = "1.0.0" }
    }
    $warmSession.Stdin.WriteLine((@{ jsonrpc = "2.0"; method = "notifications/initialized" } | ConvertTo-Json -Compress))

    $swWarmOpen = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-Tool -Session $warmSession -Name "genexus_kb" -Arguments @{ action = "open"; alias = "kb0" }
    $null = Wait-IndexReady -Session $warmSession -Alias "kb0"
    $warmIndexKb0Ms = [Math]::Round($swWarmOpen.Elapsed.TotalMilliseconds, 2)
    Write-Host ("  Warm KB open -> ready (kb0) : {0,8:N1} ms" -f $warmIndexKb0Ms)

    # Open remaining KBs (e.g. kb1 = KBTeste) and ensure ready
    for ($i = 1; $i -lt $kbEntries.Count; $i++) {
        $al = $kbEntries[$i].Alias
        $swWarmOther = [Diagnostics.Stopwatch]::StartNew()
        $null = Invoke-Tool -Session $warmSession -Name "genexus_kb" -Arguments @{ action = "open"; alias = $al }
        $null = Wait-IndexReady -Session $warmSession -Alias $al
        Write-Host ("  Warm KB open -> ready ($al) : {0,8:N1} ms" -f [Math]::Round($swWarmOther.Elapsed.TotalMilliseconds, 2))
    }

    # Snapshot CPU before workload
    $cpuBeforeWorkload = Get-ProcessTreeSamples -RootPid $warmSession.Proc.Id

    # -------------------------------------------------------------------------
    # Phase 3: Per-tool latency p50/p95 over N runs
    # -------------------------------------------------------------------------
    Write-Host "`n=== Phase 3: Per-tool latency (n=$Iterations per KB) ===" -ForegroundColor Cyan
    $toolLatenciesByKb = [ordered]@{}
    $allToolSamples = [ordered]@{
        list_objects  = New-Object System.Collections.Generic.List[double]
        query         = New-Object System.Collections.Generic.List[double]
        search_source = New-Object System.Collections.Generic.List[double]
        read          = New-Object System.Collections.Generic.List[double]
        analyze       = New-Object System.Collections.Generic.List[double]
        dryrun_write  = New-Object System.Collections.Generic.List[double]
    }

    foreach ($entry in $kbEntries) {
        $alias = $entry.Alias
        $listResp = Invoke-Tool -Session $warmSession -Name "genexus_list_objects" -Arguments @{ limit = 20; axiCompact = $false; kb = $alias }
        $objectNames = New-Object System.Collections.Generic.List[string]
        try {
            $parsed = $listResp.Text | ConvertFrom-Json -ErrorAction Stop
            $items = if ($parsed.results) { $parsed.results } elseif ($parsed.objects) { $parsed.objects } elseif ($parsed.items) { $parsed.items } else { @() }
            foreach ($it in $items) {
                $n = if ($it.name) { $it.name } elseif ($it.Name) { $it.Name } else { $null }
                if ($n -and $objectNames.Count -lt 10) { $objectNames.Add([string]$n) }
            }
        } catch {
            foreach ($line in ($listResp.Text -split "`n")) {
                if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*[|,]') {
                    if ($objectNames.Count -lt 10) { $objectNames.Add($Matches[1]) }
                }
            }
        }
        if ($objectNames.Count -eq 0) {
            throw "Could not resolve any object name from genexus_list_objects on $alias"
        }

        $kbSamples = [ordered]@{
            list_objects  = New-Object System.Collections.Generic.List[double]
            query         = New-Object System.Collections.Generic.List[double]
            search_source = New-Object System.Collections.Generic.List[double]
            read          = New-Object System.Collections.Generic.List[double]
            analyze       = New-Object System.Collections.Generic.List[double]
            dryrun_write  = New-Object System.Collections.Generic.List[double]
        }

        $queries = @("a", "e", "i", "o", "proc", "trn", "web", "sdt", "get", "set")
        $patterns = @("For Each", "EndFor", "If ", "New", "Call", "Return", "msg", "Parm", "Do ", "Sub")

        for ($iter = 0; $iter -lt $Iterations; $iter++) {
            $rList = Invoke-Tool -Session $warmSession -Name "genexus_list_objects" -Arguments @{ limit = 20; kb = $alias }
            $kbSamples.list_objects.Add($rList.ElapsedMs)
            $allToolSamples.list_objects.Add($rList.ElapsedMs)

            $q = $queries[$iter % $queries.Count]
            $rQuery = Invoke-Tool -Session $warmSession -Name "genexus_query" -Arguments @{ query = $q; limit = 20; kb = $alias }
            $kbSamples.query.Add($rQuery.ElapsedMs)
            $allToolSamples.query.Add($rQuery.ElapsedMs)

            $pat = $patterns[$iter % $patterns.Count]
            $rSearch = Invoke-Tool -Session $warmSession -Name "genexus_search_source" -Arguments @{ pattern = $pat; maxResults = 20; kb = $alias }
            $kbSamples.search_source.Add($rSearch.ElapsedMs)
            $allToolSamples.search_source.Add($rSearch.ElapsedMs)

            $targetObj = $objectNames[$iter % $objectNames.Count]
            $rRead = Invoke-Tool -Session $warmSession -Name "genexus_read" -Arguments @{ name = $targetObj; kb = $alias }
            $kbSamples.read.Add($rRead.ElapsedMs)
            $allToolSamples.read.Add($rRead.ElapsedMs)

            $rAnalyze = Invoke-Tool -Session $warmSession -Name "genexus_analyze" -Arguments @{ name = $targetObj; mode = "summary"; kb = $alias }
            $kbSamples.analyze.Add($rAnalyze.ElapsedMs)
            $allToolSamples.analyze.Add($rAnalyze.ElapsedMs)

            $rDry = Invoke-Tool -Session $warmSession -Name "genexus_create_object" -Arguments @{ type = "Procedure"; name = "BenchDryRunProc"; dryRun = $true; kb = $alias }
            $kbSamples.dryrun_write.Add($rDry.ElapsedMs)
            $allToolSamples.dryrun_write.Add($rDry.ElapsedMs)
        }

        $kbSummary = [ordered]@{}
        foreach ($k in $kbSamples.Keys) {
            $kbSummary[$k] = Summarize-Latencies ($kbSamples[$k].ToArray())
        }
        $toolLatenciesByKb[$alias] = $kbSummary
    }

    $overallLatencies = [ordered]@{}
    foreach ($k in $allToolSamples.Keys) {
        $s = Summarize-Latencies ($allToolSamples[$k].ToArray())
        $overallLatencies[$k] = $s
        Write-Host ("  {0,-15} p50={1,7:N2} ms  p95={2,7:N2} ms  avg={3,7:N2} ms" -f $k, $s.p50, $s.p95, $s.avg)
    }

    # Snapshot CPU after workload
    $cpuAfterWorkload = Get-ProcessTreeSamples -RootPid $warmSession.Proc.Id

    # -------------------------------------------------------------------------
    # Phase 4: Idle CPU over IdleSeconds
    # -------------------------------------------------------------------------
    Write-Host "`n=== Phase 4: Idle CPU over ${IdleSeconds}s ===" -ForegroundColor Cyan
    if ($IdleSeconds -gt 0) {
        Start-Sleep -Seconds $IdleSeconds
    }
    $cpuAfterIdle = Get-ProcessTreeSamples -RootPid $warmSession.Proc.Id

    function Compute-CpuDeltas {
        param($Before, $After)
        $beforeByPid = @{}
        foreach ($b in $Before) { $beforeByPid[$b.Pid] = $b }
        $deltas = @()
        foreach ($a in $After) {
            $prev = if ($beforeByPid.ContainsKey($a.Pid)) { $beforeByPid[$a.Pid].CpuSec } else { 0.0 }
            $deltas += [ordered]@{
                Pid       = $a.Pid
                Name      = $a.Name
                Role      = $a.Role
                TotalSec  = [Math]::Round($a.CpuSec, 3)
                DeltaSec  = [Math]::Round(($a.CpuSec - $prev), 3)
                PrivateMB = $a.PrivateMB
            }
        }
        return $deltas
    }

    $workloadCpu = Compute-CpuDeltas $cpuBeforeWorkload $cpuAfterWorkload
    $idleCpu = Compute-CpuDeltas $cpuAfterWorkload $cpuAfterIdle

    Write-Host "  Workload CPU delta:"
    foreach ($d in $workloadCpu) {
        Write-Host ("    {0,-16} (pid {1,6}) delta={2,7:N3}s  total={3,7:N3}s" -f $d.Role, $d.Pid, $d.DeltaSec, $d.TotalSec)
    }
    Write-Host "  Idle CPU delta (${IdleSeconds}s):"
    foreach ($d in $idleCpu) {
        Write-Host ("    {0,-16} (pid {1,6}) delta={2,7:N3}s" -f $d.Role, $d.Pid, $d.DeltaSec)
    }

    $report = [ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString("o")
        publishDir   = $PublishDir
        kbs          = $kbEntries
        iterations   = $Iterations
        idleSeconds  = $IdleSeconds
        coldStart    = [ordered]@{
            gatewayStartToInitMs       = $gatewayStartToInitMs
            gatewayStartToFirstToolMs  = $gatewayStartToFirstToolMs
            toolsListMs                = $toolsListMs
            kbOpenCallMs               = $kbOpenCallMs
            workerSpawnKbOpenToReadyMs = $workerSpawnKbOpenToReadyMs
            coldIndexMs                = $coldIndexMs
            warmIndexKb0Ms             = $warmIndexKb0Ms
        }
        latencyMs    = [ordered]@{
            overall = $overallLatencies
            byKb    = $toolLatenciesByKb
        }
        cpuSeconds   = [ordered]@{
            workload = $workloadCpu
            idle     = $idleCpu
        }
    }

    $jsonText = $report | ConvertTo-Json -Depth 10
    if (-not [string]::IsNullOrWhiteSpace($OutJson)) {
        $outDir = Split-Path $OutJson -Parent
        if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
        [System.IO.File]::WriteAllText($OutJson, $jsonText, [System.Text.UTF8Encoding]::new($false))
        Write-Host "`nSaved baseline JSON to $OutJson" -ForegroundColor Green
    }
}
finally {
    Stop-GatewaySession $session
    Stop-GatewaySession $warmSession
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
