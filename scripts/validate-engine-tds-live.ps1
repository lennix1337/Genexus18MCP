#requires -Version 7.0
<#
.SYNOPSIS
  Validates gx-engine's native TDS read transport against a disposable test Knowledge Base.

.DESCRIPTION
  Reads the same Knowledge Base through sqlcmd and through the TDS transport
  (GXMCP_ENGINE_SQL_TRANSPORT=tds) and compares what the engine decoded, byte for byte.
  It is the check the unit tests cannot make: they run against an endpoint written for
  them, not against SQL Server or LocalDB.

  Strictly read-only. The engine issues its generated SELECT statements and nothing else;
  no file under the Knowledge Base folder is written. Connecting to a LocalDB instance
  starts it when it is stopped, as every client of "(LocalDB)\name" does. The wrong-instance
  check works on a copy of knowledgebase.connection in a temporary folder.

  Refuses any path that is not a direct child of C:\KBs whose name contains "teste".

  Builds, and says so:
    - the probe with the TDS transport, release:
        cargo build --release --features tds --example udm_transport_probe
    - the probe without it, debug (the default feature set), for the fallback check:
        cargo build --example udm_transport_probe
  Each goes to its own target directory under src/gx-engine/target, so neither rebuild
  disturbs the other or the engine binary.

  Checks, each printed as PASS or FAIL:
    sqlcmd-read          the probe reads the KB through sqlcmd
    tds-read             the probe reads the KB over TDS, and reports that TDS served it
    catalog-identical    the catalog read is byte-identical between the two transports
    parts-identical      the per-part reads are byte-identical between the two transports
    fallback-no-feature  a build without the `tds` feature that is asked for TDS reads
                         through sqlcmd, records why, and reads the same bytes
    fallback-wrong-instance  a connection naming an absent LocalDB instance records why TDS
                         was unavailable and ends in sqlcmd's own failure, not a TDS one

  Exit code 0 when every check passes, 1 otherwise, 2 when the path is refused.

.PARAMETER KbPath
  The Knowledge Base folder, for example C:\KBs\KBTeste.

.PARAMETER Runs
  Reads per transport; the reported time is the median.

.PARAMETER Parts
  Objects whose parts are read one statement at a time.

.PARAMETER PlanOnly
  Prints what would run as JSON and exits without building or touching the Knowledge Base.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KbPath,
    [ValidateRange(1, 50)][int]$Runs = 5,
    [ValidateRange(1, 5000)][int]$Parts = 40,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repoRoot 'src\gx-engine\Cargo.toml'
$transportVariable = 'GXMCP_ENGINE_SQL_TRANSPORT'

# Standing authorization covers disposable test KBs only (AGENTS.md): a direct child of
# C:\KBs whose name contains "teste". The path is normalized first so ".." cannot leave it.
$kb = [System.IO.Path]::GetFullPath($KbPath).TrimEnd('\', '/')
$kbRoot = 'C:\KBs'
$kbName = [System.IO.Path]::GetFileName($kb)
if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($kb), $kbRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $kbName -notmatch '(?i)teste') {
    [Console]::Error.WriteLine("Refused: '$kb' is not a test Knowledge Base. Only a direct child of C:\KBs whose name contains 'teste' is read.")
    exit 2
}

$tdsTarget = Join-Path $repoRoot 'src\gx-engine\target\tds-validation'
$plainTarget = Join-Path $repoRoot 'src\gx-engine\target\tds-validation-default'
$tdsBuild = @('build', '--release', '--manifest-path', $manifest, '--features', 'tds', '--example', 'udm_transport_probe', '--target-dir', $tdsTarget)
$plainBuild = @('build', '--manifest-path', $manifest, '--example', 'udm_transport_probe', '--target-dir', $plainTarget)
$tdsProbe = Join-Path $tdsTarget 'release\examples\udm_transport_probe.exe'
$plainProbe = Join-Path $plainTarget 'debug\examples\udm_transport_probe.exe'

if ($PlanOnly) {
    [ordered]@{
        kb          = $kb
        readOnly    = $true
        cargoFeature = 'tds'
        builds      = @(('cargo ' + ($tdsBuild -join ' ')), ('cargo ' + ($plainBuild -join ' ')))
        probes      = @(
            [ordered]@{ transport = 'sqlcmd'; exe = $tdsProbe; runs = $Runs; parts = $Parts },
            [ordered]@{ transport = 'tds'; exe = $tdsProbe; runs = $Runs; parts = $Parts }
        )
        checks      = @('sqlcmd-read', 'tds-read', 'catalog-identical', 'parts-identical', 'fallback-no-feature', 'fallback-wrong-instance')
    } | ConvertTo-Json -Depth 5
    exit 0
}

if (-not (Test-Path -LiteralPath (Join-Path $kb 'knowledgebase.connection') -PathType Leaf)) {
    throw "'$kb' has no knowledgebase.connection: it is not a Knowledge Base folder."
}
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'sqlcmd is not on PATH: there is nothing to compare the TDS transport against on this machine.'
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $Passed })
    $verdict = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}  {1}  {2}" -f $verdict, $Name, $Detail)
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    return $sorted[[int][math]::Floor($sorted.Count / 2)]
}

# Runs the probe with one transport selected and returns its exit code, parsed report and stderr.
function Invoke-Probe([string]$Exe, [string]$Transport, [string]$Kb, [string]$DumpDir, [int]$RunCount) {
    $stderrFile = Join-Path $work ("stderr-" + [guid]::NewGuid().ToString('N') + '.txt')
    $arguments = @($Kb, '--runs', $RunCount, '--parts', $Parts)
    if ($DumpDir) {
        New-Item -ItemType Directory -Force -Path $DumpDir | Out-Null
        $arguments += @('--dump', $DumpDir)
    }
    $previous = [Environment]::GetEnvironmentVariable($transportVariable)
    try {
        [Environment]::SetEnvironmentVariable($transportVariable, $Transport)
        $stdout = @(& $Exe @arguments 2> $stderrFile)
        $exit = $LASTEXITCODE
    }
    finally {
        [Environment]::SetEnvironmentVariable($transportVariable, $previous)
    }
    $report = $null
    if ($exit -eq 0) { $report = ($stdout -join "`n") | ConvertFrom-Json }
    return [pscustomobject]@{
        Exit   = $exit
        Report = $report
        Stderr = (Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue)
    }
}

function Test-SameBytes([string]$Left, [string]$Right) {
    if (-not (Test-Path -LiteralPath $Left) -or -not (Test-Path -LiteralPath $Right)) { return $false }
    $a = [System.IO.File]::ReadAllBytes($Left)
    $b = [System.IO.File]::ReadAllBytes($Right)
    return $a.Length -gt 0 -and [System.Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$b)
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("gxe-tds-validation-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Write-Host "Knowledge Base: $kb (read-only)"
    Write-Host "Building the probe with the TDS transport (cargo feature 'tds', release): cargo $($tdsBuild -join ' ')"
    & cargo @tdsBuild
    if ($LASTEXITCODE -ne 0) { throw "The build with --features tds failed (exit $LASTEXITCODE)." }
    Write-Host "Building the probe without the TDS transport (default features, debug): cargo $($plainBuild -join ' ')"
    & cargo @plainBuild
    if ($LASTEXITCODE -ne 0) { throw "The default-feature build failed (exit $LASTEXITCODE)." }

    $sqlcmdDump = Join-Path $work 'sqlcmd'
    $tdsDump = Join-Path $work 'tds'

    $sqlcmd = Invoke-Probe $tdsProbe 'sqlcmd' $kb $sqlcmdDump $Runs
    $sqlcmdOk = $sqlcmd.Exit -eq 0 -and $sqlcmd.Report.transport.used -eq 'sqlcmd'
    Add-Check 'sqlcmd-read' $sqlcmdOk $(if ($sqlcmdOk) {
            "catalog $($sqlcmd.Report.catalog_bytes) bytes, $($sqlcmd.Report.parts) parts ($($sqlcmd.Report.part_bytes) bytes)"
        } else { "exit $($sqlcmd.Exit): $($sqlcmd.Stderr)" })

    $tds = Invoke-Probe $tdsProbe 'tds' $kb $tdsDump $Runs
    $tdsOk = $tds.Exit -eq 0 -and $tds.Report.transport.requested -eq 'tds' -and $tds.Report.transport.used -eq 'tds' -and
        $null -eq $tds.Report.transport.fallback_reason
    Add-Check 'tds-read' $tdsOk $(if ($tdsOk) {
            "served over TDS, encryption $($tds.Report.transport.encryption)"
        } elseif ($tds.Exit -eq 0) {
            "TDS was not used; reads fell back to sqlcmd: $($tds.Report.transport.fallback_reason)"
        } else { "exit $($tds.Exit): $($tds.Stderr)" })

    # Only when TDS really served the reads: two sqlcmd reads being equal proves nothing about TDS.
    Add-Check 'catalog-identical' ($sqlcmdOk -and $tdsOk -and (Test-SameBytes (Join-Path $sqlcmdDump 'catalog.bin') (Join-Path $tdsDump 'catalog.bin'))) 'catalog snapshot, sqlcmd against TDS, byte for byte'
    Add-Check 'parts-identical' ($sqlcmdOk -and $tdsOk -and (Test-SameBytes (Join-Path $sqlcmdDump 'parts.bin') (Join-Path $tdsDump 'parts.bin'))) 'decoded part payloads and properties, sqlcmd against TDS, byte for byte'

    if ($sqlcmdOk -and $tdsOk) {
        Write-Host ''
        Write-Host ("Median of {0} runs (each run a new connection; not a controlled benchmark):" -f $Runs)
        Write-Host ("  catalog read   sqlcmd {0,9:N1} ms   tds {1,9:N1} ms" -f (Get-Median $sqlcmd.Report.catalog_ms), (Get-Median $tds.Report.catalog_ms))
        Write-Host ("  per-part reads sqlcmd {0,9:N1} ms   tds {1,9:N1} ms" -f (Get-Median $sqlcmd.Report.parts_ms), (Get-Median $tds.Report.parts_ms))
        Write-Host ''
    }

    # A binary without the feature that is asked for TDS: sqlcmd reads, the reason is recorded,
    # and the bytes are the ones sqlcmd read above.
    $plain = Invoke-Probe $plainProbe 'tds' $kb $null 1
    $plainOk = $plain.Exit -eq 0 -and $plain.Report.transport.requested -eq 'tds' -and $plain.Report.transport.used -eq 'sqlcmd' -and
        "$($plain.Report.transport.fallback_reason)" -match 'tds. cargo feature' -and $sqlcmdOk -and
        $plain.Report.catalog_sha256 -eq $sqlcmd.Report.catalog_sha256 -and $plain.Report.parts_sha256 -eq $sqlcmd.Report.parts_sha256
    Add-Check 'fallback-no-feature' $plainOk $(if ($plain.Exit -eq 0) {
            "used $($plain.Report.transport.used); reason: $($plain.Report.transport.fallback_reason)"
        } else { "exit $($plain.Exit): $($plain.Stderr)" })

    # A LocalDB instance that does not exist: TDS cannot resolve its pipe before anything is
    # read, the reason is recorded, and the failure that ends the run is sqlcmd's.
    $absentKb = Join-Path $work 'absent-instance'
    New-Item -ItemType Directory -Path $absentKb | Out-Null
    $connection = [xml](Get-Content -LiteralPath (Join-Path $kb 'knowledgebase.connection') -Raw)
    $server = $connection.SelectSingleNode('//ServerInstance')
    if ($null -eq $server) { throw 'knowledgebase.connection has no ServerInstance element.' }
    $server.InnerText = '(LocalDB)\GxeAbsent' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $connection.Save((Join-Path $absentKb 'knowledgebase.connection'))
    $absent = Invoke-Probe $tdsProbe 'tds' $absentKb $null 1
    $absentOk = $absent.Exit -ne 0 -and "$($absent.Stderr)" -match 'SQL transport: reads used sqlcmd:' -and "$($absent.Stderr)" -match 'sqlcmd (read|execution|reader)'
    Add-Check 'fallback-wrong-instance' $absentOk ("exit $($absent.Exit): " + ("$($absent.Stderr)".Trim() -replace '\s+', ' '))
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "PASS  all $($results.Count) checks"
    exit 0
}
Write-Host "FAIL  $($failed.Count) of $($results.Count) checks: $(($failed | ForEach-Object Name) -join ', ')"
exit 1
