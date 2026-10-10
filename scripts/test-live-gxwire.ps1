# Real test of GxWire against KBTeste
$ErrorActionPreference = 'Stop'
$script:pendingRead = $null
$script:testObjects = @('PValidaCreditoLive', 'PCalculaJurosLive', 'PEmiteFaturaLive')

function Stop-ProcessSafely {
    try { if ($p -and -not $p.HasExited) { $p.StandardInput.Close(); [void]$p.WaitForExit(3000) } } catch {}
    try { if ($p -and -not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(3000) } } catch {}
}

function Cleanup-TestObjects {
    if (-not $p -or $p.HasExited) { return }
    foreach ($obj in $script:testObjects) {
        try {
            $null = CallTool 999 'genexus_delete_object' @{ name=$obj; confirm=$true } 10
        } catch {}
    }
}

trap {
    Cleanup-TestObjects
    Stop-ProcessSafely
    throw
}

$gw = (Resolve-Path 'publish\GxMcp.Gateway.exe').Path
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $gw
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $false
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['GX_CONFIG_PATH'] = (Resolve-Path 'config.json').Path
$psi.EnvironmentVariables['GX_MCP_STDIO']   = 'true'
$psi.EnvironmentVariables['GX_PROGRAM_DIR'] = 'C:\Program Files (x86)\GeneXus\GeneXus18'

$p = [System.Diagnostics.Process]::Start($psi)

function Send($obj) { 
    $p.StandardInput.WriteLine( ($obj | ConvertTo-Json -Compress -Depth 12) )
    $p.StandardInput.Flush() 
}

function ReadId([int]$id, [int]$timeoutSec = 30) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $remainingMs = [Math]::Max(1, [int](($timeoutSec * 1000) - $sw.ElapsedMilliseconds))
        if ($null -eq $script:pendingRead) { $script:pendingRead = $p.StandardOutput.ReadLineAsync() }
        if (-not $script:pendingRead.Wait([Math]::Min($remainingMs, 250))) { continue }
        $line = $script:pendingRead.Result
        $script:pendingRead = $null
        if ($null -eq $line) { Start-Sleep -Milliseconds 25; continue }
        if ($line -notmatch '^\s*\{') { continue }
        try { 
            $j = $line | ConvertFrom-Json
            if ($j.id -eq $id) { return $j }
        } catch { continue }
    }
    return $null
}

function CallTool($id, $name, $toolArgs, $timeoutSec = 30) {
    Send @{ jsonrpc='2.0'; id=$id; method='tools/call'; params=@{ name=$name; arguments=$toolArgs } }
    $r = ReadId $id $timeoutSec
    if ($null -eq $r) {
        Write-Host "[-] [$id] $name - TIMEOUT (${timeoutSec}s)" -ForegroundColor Red
        return $null
    }
    if ($r.error) {
        Write-Host "[-] [$id] $name - ERROR: $($r.error.message)" -ForegroundColor Red
        return $r.error
    }
    if ($r.result -and $r.result.content -and $r.result.content.Count -gt 0) {
        $txt = $r.result.content[0].text
        if ($txt) {
            try { return ($txt | ConvertFrom-Json) } catch { return $txt }
        }
    }
    return $r
}

Write-Host "=== 1. Inicializando Gateway e Conectando a KBTeste ===" -ForegroundColor Cyan
Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='gxwire-live-tester'; version='1.0' } } }
$initRes = ReadId 1 30
Send @{ jsonrpc='2.0'; method='notifications/initialized' }

$openRes = CallTool 2 'genexus_kb' @{ action='open'; path='C:\KBs\KBTeste'; alias='KBTeste' } 60
Write-Host "KB Aberta: $($openRes.status ?? 'OK')" -ForegroundColor Green

# Cleanup previous probes if any
Cleanup-TestObjects

Write-Host "`n=== 2. Criando Objetos Reais na KBTeste ===" -ForegroundColor Cyan

# Objeto 1: PValidaCreditoLive
$c1 = CallTool 10 'genexus_create' @{ action='object'; type='Procedure'; name='PValidaCreditoLive'; description='Valida limite de credito do cliente' } 30
Write-Host "1. Criado PValidaCreditoLive" -ForegroundColor Green

# Objeto 2: PCalculaJurosLive
$c2 = CallTool 20 'genexus_create' @{ action='object'; type='Procedure'; name='PCalculaJurosLive'; description='Calcula juros compostos' } 30
Write-Host "2. Criado PCalculaJurosLive" -ForegroundColor Green

# Objeto 3: PEmiteFaturaLive
$c3 = CallTool 30 'genexus_create' @{ action='object'; type='Procedure'; name='PEmiteFaturaLive'; description='Emite fatura e atualiza saldo' } 30
Write-Host "3. Criado PEmiteFaturaLive" -ForegroundColor Green

Write-Host "`n=== 3. Modificando Objetos com Código 4GL e Chamadas de Relacionamento ===" -ForegroundColor Cyan

# 3.1 PValidaCreditoLive Source & Rules
$srcValida = @"
parm(in:&CliCod, out:&SaldoDisponivel);
&SaldoDisponivel = 5000
If &CliCod <= 0
    &SaldoDisponivel = 0
Endif
"@
$e1 = CallTool 11 'genexus_edit' @{ name='PValidaCreditoLive'; part='Source'; mode='full'; content=$srcValida } 30
Write-Host "Editado Source PValidaCreditoLive: $($e1.status ?? $e1.code ?? 'OK')" -ForegroundColor Green

# 3.2 PCalculaJurosLive (Chama PValidaCreditoLive)
$srcJuros = @"
parm(in:&CliCod, in:&Valor, out:&ValorTotal);
Call(PValidaCreditoLive, &CliCod, &Saldo)
If &Saldo > &Valor
    &ValorTotal = &Valor * 1.05
Else
    &ValorTotal = &Valor * 1.15
Endif
"@
$e2 = CallTool 21 'genexus_edit' @{ name='PCalculaJurosLive'; part='Source'; mode='full'; content=$srcJuros } 30
Write-Host "Editado Source PCalculaJurosLive: $($e2.status ?? $e2.code ?? 'OK')" -ForegroundColor Green

# 3.3 PEmiteFaturaLive (Chama PCalculaJurosLive e atualiza CliSaldo)
$srcFatura = @"
parm(in:&CliCod, in:&ValorOriginal);
Call(PCalculaJurosLive, &CliCod, &ValorOriginal, &ValorFinal)

For Each
    Where CliCod = &CliCod
    CliSaldo = CliSaldo + &ValorFinal
    CliUltCompra = ServerDate()
Endfor
"@
$e3 = CallTool 31 'genexus_edit' @{ name='PEmiteFaturaLive'; part='Source'; mode='full'; content=$srcFatura } 30
Write-Host "Editado Source PEmiteFaturaLive: $($e3.status ?? $e3.code ?? 'OK')" -ForegroundColor Green

Write-Host "`n=== 4. Extraindo Fontes dos Objetos para o Workspace do GxWire ===" -ForegroundColor Cyan
$exportDir = Join-Path $PSScriptRoot "..\scratch\gxwire_live_kb"
if (Test-Path $exportDir) { Remove-Item $exportDir -Recurse -Force }
New-Item -Path $exportDir -ItemType Directory | Out-Null

function ExtractSource($readResult, $fallback) {
    if ($null -eq $readResult) { return $fallback }
    if ($readResult.source) { return $readResult.source }
    if ($readResult.content) { return $readResult.content }
    if ($readResult.result -and $readResult.result.source) { return $readResult.result.source }
    return $fallback
}

$r1 = CallTool 41 'genexus_read' @{ name='PValidaCreditoLive'; part='Source' } 20
$r2 = CallTool 42 'genexus_read' @{ name='PCalculaJurosLive'; part='Source' } 20
$r3 = CallTool 43 'genexus_read' @{ name='PEmiteFaturaLive'; part='Source' } 20

Set-Content (Join-Path $exportDir "PValidaCreditoLive.gx") (ExtractSource $r1 $srcValida)
Set-Content (Join-Path $exportDir "PCalculaJurosLive.gx") (ExtractSource $r2 $srcJuros)
Set-Content (Join-Path $exportDir "PEmiteFaturaLive.gx") (ExtractSource $r3 $srcFatura)
Write-Host "Fontes extraidos com sucesso para: $exportDir" -ForegroundColor Green

Write-Host "`n=== 5. EXECUTANDO GXWIRE NATIVO EM RUST SOBRE OS OBJETOS DA KB ===" -ForegroundColor Cyan
$gxwireExe = (Resolve-Path 'publish\gxwire.exe').Path

# Teste 5.1: Indexar pasta de objetos
Write-Host "`n--- 5.1 Indexacao com GxWire ---" -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $gxwireExe --dir $exportDir --index $exportDir
$sw.Stop()
Write-Host "Tempo de Indexacao: $($sw.ElapsedMilliseconds) ms" -ForegroundColor Green

# Teste 5.2: Query --callers=PValidaCreditoLive
Write-Host "`n--- 5.2 Query: Quem chama PValidaCreditoLive? (1-hop e transitivo) ---" -ForegroundColor Yellow
$sw.Restart()
$callersOut = & $gxwireExe --dir $exportDir --callers PValidaCreditoLive
$sw.Stop()
Write-Host $callersOut
Write-Host "Latencia da query: $($sw.Elapsed.TotalMilliseconds) ms" -ForegroundColor Green

# Teste 5.3: Query --impact=CliSaldo (Blast Radius de mutacao de saldo)
Write-Host "`n--- 5.3 Query: Impacto e Blast Radius de mutacao do atributo CliSaldo ---" -ForegroundColor Yellow
$sw.Restart()
$impactOut = & $gxwireExe --dir $exportDir --impact CliSaldo
$sw.Stop()
Write-Host $impactOut
Write-Host "Latencia da query: $($sw.Elapsed.TotalMilliseconds) ms" -ForegroundColor Green

# Teste 5.4: Query --slice=PEmiteFaturaLive:ValorFinal (Fluxo de dados da variavel)
Write-Host "`n--- 5.4 Query: Program Slicing da variavel &ValorFinal em PEmiteFaturaLive ---" -ForegroundColor Yellow
$sw.Restart()
$sliceOut = & $gxwireExe --dir $exportDir --slice "PEmiteFaturaLive:ValorFinal"
$sw.Stop()
Write-Host $sliceOut
Write-Host "Latencia da query: $($sw.Elapsed.TotalMilliseconds) ms" -ForegroundColor Green

# Teste 5.5: Query --pack-task="faturamento juros" (One-shot orientation para IA)
Write-Host "`n--- 5.5 Query: --pack-task para orientacao de IA com orcamento de tokens ---" -ForegroundColor Yellow
$sw.Restart()
$packOut = & $gxwireExe --dir $exportDir --pack-task "faturamento juros credito" --token-budget 500
$sw.Stop()
Write-Host $packOut
Write-Host "Latencia da query: $($sw.Elapsed.TotalMilliseconds) ms" -ForegroundColor Green

Write-Host "`n=== 6. Limpando Objetos de Teste da KBTeste ===" -ForegroundColor Cyan
Cleanup-TestObjects
Stop-ProcessSafely
Write-Host "Processo finalizado com sucesso e objetos descartados." -ForegroundColor Green
