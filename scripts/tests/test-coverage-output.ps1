$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helper = Join-Path $root 'scripts/coverage/output-path.ps1'
. $helper
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $tempParent ('gxmcp-coverage-output-' + [guid]::NewGuid().ToString('N'))
$repository = Join-Path $fixture 'repository'
$temporary = Join-Path $fixture 'temporary'
$external = Join-Path $fixture 'external'
$checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:checks++
}
function Resolve-TestOutput([string]$Path, [string]$Allowed) {
    Resolve-CoverageOutputDirectory -OutputRoot $Path -RepoRoot $repository -AllowedOutputRoot $Allowed -TemporaryRoots @($temporary)
}
function Assert-Rejected([string]$Path, [string]$Allowed = '') {
    $rejected = $false
    try { $null = Resolve-TestOutput $Path $Allowed } catch { $rejected = $true }
    Assert-True $rejected "unsafe output was accepted: $Path"
}
function Invoke-Cleanup([string]$Path, [string]$Allowed = '') {
    $repoRoot = $repository
    $OutputRoot = $Path
    $AllowedOutputRoot = $Allowed
    $null = & ([scriptblock]::Create($cleanup))
}
try {
    New-Item -ItemType Directory -Path (Join-Path $repository 'scripts/coverage'), $temporary, $external -Force | Out-Null
    Copy-Item -LiteralPath $helper -Destination (Join-Path $repository 'scripts/coverage/output-path.ps1')
    $defaultOutput = Join-Path $repository 'artifacts/coverage'
    Assert-True ((Resolve-TestOutput $defaultOutput '') -eq $defaultOutput) 'default artifacts path changed'
    Assert-True ((Resolve-TestOutput (Join-Path $temporary 'coverage') '') -eq (Join-Path $temporary 'coverage')) 'runner temporary child refused'
    Assert-True ((Resolve-TestOutput (Join-Path $external 'coverage') $external) -eq (Join-Path $external 'coverage')) 'explicit external child refused'
    foreach ($bad in @([IO.Path]::GetPathRoot($fixture), $fixture, $repository,
            (Join-Path $repository 'src'), (Join-Path $repository 'artifacts'), $temporary,
            (Join-Path $fixture 'temporary-sibling/coverage'), (Join-Path $external 'coverage'))) {
        Assert-Rejected $bad
    }
    Assert-Rejected $external $external
    Assert-Rejected $repository $fixture
    Assert-Rejected $fixture $tempParent
    Assert-Rejected (Join-Path $external '../outside/coverage') $external
    Assert-Rejected (Join-Path $repository 'src') $repository
    Assert-Rejected (Join-Path $fixture 'unrelated') ([IO.Path]::GetPathRoot($fixture))
    $file = Join-Path $external 'file'
    Set-Content -LiteralPath $file -Value 'keep'
    Assert-Rejected $file $external

    $link = Join-Path $external 'alias'
    $kind = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $kind -Path $link -Target $temporary | Out-Null
    Assert-Rejected (Join-Path $link 'coverage') $external
    Assert-Rejected (Join-Path $link 'coverage') $link
    Assert-Rejected $external $fixture # Existing output contains a link.
    Remove-Item -LiteralPath $link -Force

    # Execute the real preparation block, stopping before it can build or run tests.
    $source = Get-Content -LiteralPath (Join-Path $root 'scripts/coverage/collect.ps1') -Raw
    $start = $source.IndexOf('if ([string]::IsNullOrWhiteSpace($OutputRoot)) {', [StringComparison]::Ordinal)
    $end = $source.IndexOf('# Ensure gxwire binary', $start, [StringComparison]::Ordinal)
    Assert-True ($start -ge 0 -and $end -gt $start) 'coverage preparation block not found'
    $cleanup = $source.Substring($start, $end - $start)
    $previousRunnerTemp = $env:RUNNER_TEMP
    try {
        $env:RUNNER_TEMP = $temporary
        Invoke-Cleanup ''
        Assert-True (Test-Path -LiteralPath (Join-Path $temporary 'gx-coverage-artifacts')) 'default RUNNER_TEMP output changed'
        $env:RUNNER_TEMP = $null
        Invoke-Cleanup ''
        Assert-True (Test-Path -LiteralPath $defaultOutput) 'default local output changed'
    } finally { $env:RUNNER_TEMP = $previousRunnerTemp }
    New-Item -ItemType Directory -Path $defaultOutput -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $defaultOutput 'stale.txt') -Value 'stale'
    Invoke-Cleanup $defaultOutput
    Assert-True (Test-Path -LiteralPath $defaultOutput -PathType Container) 'valid output was not recreated'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $defaultOutput 'stale.txt'))) 'valid cleanup retained stale report'
    $sentinel = Join-Path $repository 'keep.txt'
    Set-Content -LiteralPath $sentinel -Value 'preserve checkout'
    $rejected = $false
    try { Invoke-Cleanup $repository } catch { $rejected = $true }
    Assert-True $rejected 'collect bypassed its output guard'
    Assert-True (Test-Path -LiteralPath $sentinel) 'collect deleted the checkout before validating output'
    Write-Host "coverage-output: $checks checks passed (paths, junctions, external authorization, and real cleanup block)."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempParent.TrimEnd([char[]]'\/')) {
        throw 'Refusing cleanup outside the owned test fixture.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
