$ErrorActionPreference = 'Stop'
$gw = 'C:\Projetos\Genexus18MCP\src\GxMcp.Gateway\bin\Debug\net10.0-windows\GxMcp.Gateway.exe'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gw
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $false   # let Debug logs inherit; draining a redirected stderr would deadlock
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['GX_CONFIG_PATH'] = 'C:\Projetos\Genexus18MCP\config.json'
$psi.EnvironmentVariables['GX_MCP_STDIO']   = 'true'

$p = [System.Diagnostics.Process]::Start($psi)

function Send($obj) { $p.StandardInput.WriteLine( ($obj | ConvertTo-Json -Compress -Depth 12) ) ; $p.StandardInput.Flush() }

function ReadId([int]$id, [int]$timeoutSec) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    $line = $p.StandardOutput.ReadLine()
    if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
    if ($line -notmatch '^\s*\{') { continue }
    try { $j = $line | ConvertFrom-Json } catch { continue }
    if ($j.id -eq $id) { return $j }
  }
  return $null
}

# 1) initialize
Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='val'; version='1' } } }
$init = ReadId 1 30
Write-Host "=== initialize ok: $([bool]$init) ==="

Send @{ jsonrpc='2.0'; method='notifications/initialized' }

function Call($id, $name, $toolArgs, $timeout) {
  Send @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
  $r = ReadId $id $timeout
  Write-Host "`n===== [$id] $name ====="
  if ($null -eq $r) { Write-Host "(no response within ${timeout}s)"; return }
  # tools/call result content[0].text holds the tool JSON envelope
  $txt = $r.result.content[0].text
  if ($txt) { Write-Host $txt } else { Write-Host ($r | ConvertTo-Json -Compress -Depth 12) }
}

function CallRaw($id, $name, $toolArgs, $timeout) {
  Send @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
  $r = ReadId $id $timeout
  if ($null -eq $r) { return $null }
  return $r.result.content[0].text
}

# Kick the index load (lazy), then poll whoami until it leaves Cold/Reindexing.
$null = CallRaw 90 'genexus_inspect' @{ name='Acao' } 120
$ready = $false
for ($i = 0; $i -lt 24 -and -not $ready; $i++) {
  Start-Sleep -Seconds 10
  $w = CallRaw (100 + $i) 'genexus_whoami' @{} 60
  if ($w) {
    try { $st = ($w | ConvertFrom-Json).index.status } catch { $st = '?' }
    Write-Host ("  [poll $i] index.status = {0}" -f $st)
    if ($st -in @('Ready','Complete','LiteReady','Enriching','UltraLiteReady')) { $ready = $true }
  }
}
Write-Host "=== index ready: $ready ==="

Call 3 'genexus_inspect'  @{ name='Acao' } 120
Call 4 'genexus_analyze'  @{ mode='impact'; name='Acao' } 120
Call 6 'genexus_db'       @{ action='optimize_suggest'; target='Acao' } 150
Call 7 'genexus_doctor'   @{} 60

try { $p.StandardInput.Close() } catch {}
try { $p.Kill() } catch {}
Write-Host "`n=== done ==="
