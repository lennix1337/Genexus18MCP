<#
.SYNOPSIS
    Repeatable memory & CPU baseline for Genexus18MCP against disposable *teste* KBs.
.DESCRIPTION
    Starts GxMcp.Gateway.exe (stdio-isolated) against disposable C:\KBs\*teste* KBs,
    runs a fixed MCP workload (index build, 20 searches, 10 object reads, 1 list),
    and records per-process steady and peak Private Bytes, Working Set, Virtual Bytes,
    CPU time, and (when available) managed heap.
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "publish"),
    [string[]]$KBs = @("C:\KBs\KBTeste955", "C:\KBs\KBTeste"),
    [ValidateSet("isolated", "shared-host")]
    [string]$SharingMode = "isolated",
    [string]$OutJson = "",
    [string]$GcDumpToolPath = ""
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

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("gxmcp_bench_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
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
        HttpPort                 = 0
        McpStdio                 = $true
        WorkerSharingMode        = $SharingMode
        MaxOpenKbs               = [Math]::Max(3, $KBs.Count)
        SessionIdleTimeoutMinutes = 10
        WorkerIdleTimeoutMinutes  = 30
        EmitStructuredContent    = $false
        TerseResponses           = $true
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
    $byPid = @{}
    foreach ($p in $allProcs) {
        $byPid[[int]$p.ProcessId] = $p
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

    # In shared-host mode, a detached worker may have been spawned earlier or reparented;
    # include any GxMcp.Worker/gx-engine/gxwire whose CommandLine references our tempRoot or PublishDir.
    foreach ($p in $allProcs) {
        $pidInt = [int]$p.ProcessId
        if ($visited.Contains($pidInt)) { continue }
        if ($p.Name -match '^(GxMcp\.|gx-engine|gxwire)' -and $p.CommandLine -and ($p.CommandLine -like "*$PublishDir*" -or $p.CommandLine -like "*$tempRoot*")) {
            [void]$visited.Add($pidInt)
        }
    }

    $samples = @()
    foreach ($pidInt in $visited) {
        try {
            $gp = Get-Process -Id $pidInt -ErrorAction Stop
            $cim = $byPid[$pidInt]
            $role = if ($pidInt -eq $RootPid) { "Gateway" }
                    elseif ($gp.ProcessName -eq "GxMcp.Worker") { "Worker" }
                    elseif ($gp.ProcessName -eq "gx-engine") { "Rust-gx-engine" }
                    elseif ($gp.ProcessName -eq "gxwire") { "Rust-gxwire" }
                    else { $gp.ProcessName }
            $samples += [pscustomobject]@{
                Pid          = $pidInt
                Name         = $gp.ProcessName
                Role         = $role
                PrivateMB    = [Math]::Round($gp.PrivateMemorySize64 / 1MB, 2)
                WorkingSetMB = [Math]::Round($gp.WorkingSet64 / 1MB, 2)
                VirtualMB    = [Math]::Round($gp.VirtualMemorySize64 / 1MB, 2)
                CpuSec       = [Math]::Round($gp.TotalProcessorTime.TotalSeconds, 3)
                CmdLine      = if ($cim) { $cim.CommandLine } else { "" }
            }
        } catch { }
    }
    return $samples
}

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

$proc = [System.Diagnostics.Process]::Start($psi)
$stdin = [System.IO.StreamWriter]::new($proc.StandardInput.BaseStream, [System.Text.UTF8Encoding]::new($false))
$stdin.AutoFlush = $true

$stderrTask = $proc.StandardError.ReadToEndAsync()
$script:reqId = 0

function Invoke-Mcp {
    param([string]$Method, [hashtable]$Params)
    $script:reqId++
    $id = $script:reqId
    $payload = @{
        jsonrpc = "2.0"
        id      = $id
        method  = $Method
        params  = $Params
    } | ConvertTo-Json -Depth 12 -Compress
    $stdin.WriteLine($payload)
    while ($true) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "Gateway closed stdout unexpectedly while waiting for id=$id ($Method)." }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $null
        try { $msg = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($msg.PSObject.Properties["id"] -and $msg.id -eq $id) {
            return $msg
        }
    }
}

function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments)
    $resp = Invoke-Mcp -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments }
    if ($resp.PSObject.Properties["error"] -and $resp.error) {
        throw "Tool $Name failed: $($resp.error | ConvertTo-Json -Compress)"
    }
    $text = ""
    if ($resp.result -and $resp.result.content -and $resp.result.content.Count -gt 0) {
        $text = $resp.result.content[0].text
    }
    return [pscustomobject]@{
        Raw  = $resp
        Text = $text
        IsError = [bool]($resp.result.isError)
    }
}

$allSnapshots = New-Object System.Collections.Generic.List[object]
function Record-Snapshot {
    param([string]$Phase)
    $rows = Get-ProcessTreeSamples -RootPid $proc.Id
    $allSnapshots.Add([pscustomobject]@{ Phase = $Phase; TimestampUtc = [DateTime]::UtcNow.ToString("o"); Processes = $rows })
    Write-Host ("[{0}] Total Private={1:N2} MB, WS={2:N2} MB ({3} procs)" -f $Phase, (($rows | Measure-Object PrivateMB -Sum).Sum), (($rows | Measure-Object WorkingSetMB -Sum).Sum), $rows.Count)
}

try {
    $swTotal = [System.Diagnostics.Stopwatch]::StartNew()
    $initResp = Invoke-Mcp -Method "initialize" -Params @{
        protocolVersion = "2025-06-18"
        capabilities    = @{}
        clientInfo      = @{ name = "mem-baseline"; version = "1.0.0" }
    }
    $notif = @{ jsonrpc = "2.0"; method = "notifications/initialized" } | ConvertTo-Json -Compress
    $stdin.WriteLine($notif)
    Record-Snapshot "after_initialize"

    $kbTimings = @()
    foreach ($entry in $kbEntries) {
        $alias = $entry.Alias
        $kbPath = $entry.Path
        $swKb = [System.Diagnostics.Stopwatch]::StartNew()

        Write-Host "`n--- Workload on $alias ($kbPath) ---"
        $null = Invoke-Tool -Name "genexus_kb" -Arguments @{ action = "open"; kb = $alias }
        Record-Snapshot "$alias.after_open"

        $idxResp = Invoke-Tool -Name "genexus_lifecycle" -Arguments @{ action = "index"; force = $true; kb = $alias }
        # Wait for index completion
        for ($poll = 0; $poll -lt 40; $poll++) {
            $st = Invoke-Tool -Name "genexus_lifecycle" -Arguments @{ action = "status"; wait = 10; kb = $alias }
            if ($st.Text -notmatch '"indexState"\s*:\s*"(cold|reindexing|indexing)"' -and $st.Text -notmatch '"status"\s*:\s*"(Cold|Indexing|Running)"') {
                break
            }
            Start-Sleep -Milliseconds 500
        }
        Record-Snapshot "$alias.after_index"

        # 1 list
        $listResp = Invoke-Tool -Name "genexus_list_objects" -Arguments @{ limit = 20; axiCompact = $false; kb = $alias }
        $objectNames = New-Object System.Collections.Generic.List[string]
        try {
            $parsedList = $listResp.Text | ConvertFrom-Json -ErrorAction Stop
            $items = if ($parsedList.results) { $parsedList.results } elseif ($parsedList.objects) { $parsedList.objects } elseif ($parsedList.items) { $parsedList.items } else { @() }
            foreach ($it in $items) {
                $n = if ($it.name) { $it.name } elseif ($it.Name) { $it.Name } else { $null }
                if ($n -and $objectNames.Count -lt 10) { $objectNames.Add([string]$n) }
            }
        } catch {
            # Fallback for compact/TOON responses: extract object names from lines
            foreach ($line in ($listResp.Text -split "`n")) {
                if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*[|,]') {
                    if ($objectNames.Count -lt 10) { $objectNames.Add($Matches[1]) }
                }
            }
        }
        if ($objectNames.Count -eq 0) {
            Write-Warning "No object names parsed from genexus_list_objects on $alias. Sample: $($listResp.Text.Substring(0, [Math]::Min(200, $listResp.Text.Length)))"
        }
        Record-Snapshot "$alias.after_list"

        # 20 searches: 10 genexus_query + 10 genexus_search_source
        $queries = @("a", "e", "i", "o", "proc", "trn", "web", "sdt", "get", "set")
        foreach ($q in $queries) {
            $null = Invoke-Tool -Name "genexus_query" -Arguments @{ query = $q; limit = 20; kb = $alias }
        }
        $patterns = @("For Each", "EndFor", "If ", "New", "Call", "Return", "msg", "Parm", "Do ", "Sub")
        foreach ($p in $patterns) {
            $null = Invoke-Tool -Name "genexus_search_source" -Arguments @{ pattern = $p; maxResults = 20; kb = $alias }
        }
        Record-Snapshot "$alias.after_20_searches"

        # 10 object reads
        if ($objectNames.Count -eq 0) {
            Write-Warning "No object names parsed from genexus_list_objects on $alias; skipping reads."
        } else {
            for ($r = 0; $r -lt 10; $r++) {
                $targetObj = $objectNames[$r % $objectNames.Count]
                $null = Invoke-Tool -Name "genexus_read" -Arguments @{ name = $targetObj; kb = $alias }
            }
        }
        Record-Snapshot "$alias.after_10_reads"

        $kbListInfo = Invoke-Tool -Name "genexus_kb" -Arguments @{ action = "list" }
        $swKb.Stop()
        $kbTimings += [pscustomobject]@{
            Alias = $alias
            Path = $kbPath
            ElapsedSec = [Math]::Round($swKb.Elapsed.TotalSeconds, 2)
            KbListOutput = $kbListInfo.Text
        }
    }

    Start-Sleep -Seconds 3
    Record-Snapshot "steady_idle"

    # Optional dotnet-gcdump report on Gateway if tool path provided
    $gcSummary = $null
    if ($GcDumpToolPath -and (Test-Path (Join-Path $GcDumpToolPath "dotnet-gcdump.exe"))) {
        $gdExe = Join-Path $GcDumpToolPath "dotnet-gcdump.exe"
        $gdFile = Join-Path $tempRoot "gw.gcdump"
        $null = & $gdExe collect --process-id $proc.Id --output $gdFile 2>&1
        if (Test-Path $gdFile) {
            $reportLines = & $gdExe report $gdFile 2>&1 | Select-Object -First 45
            $gcSummary = ($reportLines | Out-String)
        }
    }

    $swTotal.Stop()

    # Aggregate peak and steady per Role+Pid
    $byProc = @{}
    foreach ($snap in $allSnapshots) {
        foreach ($p in $snap.Processes) {
            $key = "$($p.Role)#$($p.Pid)"
            if (-not $byProc.ContainsKey($key)) {
                $byProc[$key] = [pscustomobject]@{
                    Role             = $p.Role
                    Pid              = $p.Pid
                    PeakPrivateMB    = $p.PrivateMB
                    PeakWorkingSetMB = $p.WorkingSetMB
                    PeakVirtualMB    = $p.VirtualMB
                    SteadyPrivateMB  = $p.PrivateMB
                    SteadyWorkingSetMB = $p.WorkingSetMB
                    SteadyVirtualMB  = $p.VirtualMB
                    CpuSec           = $p.CpuSec
                }
            } else {
                $agg = $byProc[$key]
                if ($p.PrivateMB -gt $agg.PeakPrivateMB) { $agg.PeakPrivateMB = $p.PrivateMB }
                if ($p.WorkingSetMB -gt $agg.PeakWorkingSetMB) { $agg.PeakWorkingSetMB = $p.WorkingSetMB }
                if ($p.VirtualMB -gt $agg.PeakVirtualMB) { $agg.PeakVirtualMB = $p.VirtualMB }
                $agg.SteadyPrivateMB = $p.PrivateMB
                $agg.SteadyWorkingSetMB = $p.WorkingSetMB
                $agg.SteadyVirtualMB = $p.VirtualMB
                if ($p.CpuSec -gt $agg.CpuSec) { $agg.CpuSec = $p.CpuSec }
            }
        }
    }

    Write-Host "`n=== PROCESS SUMMARY ($SharingMode) ==="
    $summaryRows = $byProc.Values | Sort-Object Role, Pid
    $summaryRows | Format-Table Role, Pid, SteadyPrivateMB, PeakPrivateMB, SteadyWorkingSetMB, PeakWorkingSetMB, PeakVirtualMB, CpuSec -AutoSize

    $totalSteadyPriv = [Math]::Round(($summaryRows | Measure-Object SteadyPrivateMB -Sum).Sum, 2)
    $totalPeakPriv = [Math]::Round(($summaryRows | Measure-Object PeakPrivateMB -Sum).Sum, 2)
    $totalSteadyWs = [Math]::Round(($summaryRows | Measure-Object SteadyWorkingSetMB -Sum).Sum, 2)
    Write-Host ("TOTAL TREE: SteadyPrivate={0:N2} MB, SumPeakPrivate={1:N2} MB, SteadyWS={2:N2} MB, Elapsed={3:N2}s" -f $totalSteadyPriv, $totalPeakPriv, $totalSteadyWs, $swTotal.Elapsed.TotalSeconds)

    if ($OutJson) {
        $report = [ordered]@{
            TimestampUtc = [DateTime]::UtcNow.ToString("o")
            GitHead      = (git rev-parse --short HEAD 2>$null)
            SharingMode  = $SharingMode
            KBs          = $KBs
            ElapsedSec   = [Math]::Round($swTotal.Elapsed.TotalSeconds, 2)
            TotalSteadyPrivateMB = $totalSteadyPriv
            TotalSumPeakPrivateMB = $totalPeakPriv
            TotalSteadyWorkingSetMB = $totalSteadyWs
            Processes    = $summaryRows
            Snapshots    = $allSnapshots
            KbTimings    = $kbTimings
            GcCounters   = $gcSummary
        }
        $outDir = Split-Path $OutJson -Parent
        if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
        [System.IO.File]::WriteAllText($OutJson, ($report | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        Write-Host "Saved JSON report to $OutJson"
    }
}
finally {
    try { $stdin.Close() } catch { }
    if (-not $proc.WaitForExit(10000)) {
        try { $proc.Kill($true) } catch { }
    }
    # Ensure no stray children from this tempRoot remain
    $strays = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$tempRoot*" }
    foreach ($s in $strays) {
        try { Stop-Process -Id ([int]$s.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
