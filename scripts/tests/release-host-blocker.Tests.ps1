$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts/release-contract.ps1')

Describe 'Release host updater guard' {
    It 'rejects a running VS Code updater before creating release state' {
        Mock Get-CimInstance { @([pscustomobject]@{ Name = 'CodeSetup-x64.exe'; CommandLine = 'CodeSetup-x64.exe /verysilent /update' }) }
        $status = Join-Path $TestDrive 'status.json'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        { & (Join-Path $root 'release.ps1') -Version '9.0.0' -StatusFile $status } | Should Throw 'VS Code update in progress'
        $watch.Stop()
        Write-Host ("Mocked updater time-to-fail: {0:N3}s" -f $watch.Elapsed.TotalSeconds)
        $watch.Elapsed.TotalSeconds | Should BeLessThan 5
        Test-Path $status | Should Be $false
    }
    It 'allows unrelated setup processes' {
        Mock Get-CimInstance { @([pscustomobject]@{ Name = 'CodeSetup-x64.exe'; CommandLine = 'CodeSetup-x64.exe /install' }) }
        { Assert-GxMcpReleaseHostReady } | Should Not Throw
    }
    It 'rejects an updater at standalone preflight entry' {
        Mock Get-CimInstance { @([pscustomobject]@{ Name = 'CodeSetup-x64.exe'; CommandLine = 'CodeSetup-x64.exe /verysilent /update' }) }
        $summary = Join-Path $TestDrive 'summary.json'
        { & (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -SummaryPath $summary } | Should Throw 'VS Code update in progress'
        Test-Path $summary | Should Be $false
    }
}
