$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Guards the opt-in Rust artifact cache of build.ps1 (scripts\rust-artifact-cache.ps1).
# The cache may hand a binary to a checkout that never compiled it, so what is
# tested here is when two checkouts share a key, when a checkout has no key at
# all, and that a stored binary is verified before it is reused. The Rust steps
# are cut out of build.ps1 and executed against a fake cargo, so a build.ps1 that
# stops consulting the cache, or reuses a binary for changed source, fails here.

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts\rust-artifact-cache.ps1')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Invoke-Git {
    param([string]$Directory, [string[]]$Arguments)
    $output = @(& git -C $Directory @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
}

function Set-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-BuildSegment {
    param([string]$Source, [string]$StartMarker, [string]$EndMarker)
    $start = $Source.IndexOf($StartMarker, [StringComparison]::Ordinal)
    Assert-True ($start -ge 0) "build.ps1 no longer contains '$StartMarker'."
    $end = $Source.IndexOf($EndMarker, $start, [StringComparison]::Ordinal)
    Assert-True ($end -gt $start) "build.ps1 no longer contains '$EndMarker' after '$StartMarker'."
    return $Source.Substring($start, $end - $start)
}

function Get-Entry {
    param([string]$Repository, [string]$Crate = 'src/gxwire', [string]$Binary = 'gxwire.exe', [string]$Toolchain = 'rustc test 1.0')
    return Get-RustArtifactCacheEntry -CacheRoot $cacheRoot -RepositoryRoot $Repository -CrateRelativePath $Crate -BinaryName $Binary -ToolchainFingerprint $Toolchain
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-rust-cache-' + [guid]::NewGuid().ToString('N'))
$first = Join-Path $temp 'first'
$second = Join-Path $temp 'second'
$cacheRoot = Join-Path $temp 'cache'
$savedEnvironment = @{}
foreach ($name in @('RUSTFLAGS', 'CARGO_TARGET_DIR', 'CARGO_BUILD_JOBS', 'CARGO_HOME')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    # CARGO_HOME is pinned to an empty directory so the developer's own cargo
    # configuration cannot change a key while the test runs.
    $env:CARGO_HOME = Join-Path $temp 'cargo-home'
    New-Item -ItemType Directory -Path $env:CARGO_HOME -Force | Out-Null
    foreach ($name in @('RUSTFLAGS', 'CARGO_TARGET_DIR', 'CARGO_BUILD_JOBS')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }

    foreach ($crate in @('gxwire', 'gx-engine')) {
        Set-TextFile (Join-Path $first "src\$crate\Cargo.toml") "[package]`nname = `"$crate`"`nversion = `"0.1.0`"`n"
        Set-TextFile (Join-Path $first "src\$crate\src\main.rs") "fn main() { println!(`"$crate`"); }`n"
        Set-TextFile (Join-Path $first "src\$crate\.gitignore") "target/`n"
    }
    Set-TextFile (Join-Path $first 'rust-toolchain.toml') "[toolchain]`nchannel = `"1.0.0`"`n"
    Invoke-Git $first @('init', '--quiet')
    Invoke-Git $first @('config', 'user.name', 'test')
    Invoke-Git $first @('config', 'user.email', 'test@example.invalid')
    Invoke-Git $first @('config', 'core.autocrlf', 'false')
    Invoke-Git $first @('add', '--all')
    Invoke-Git $first @('commit', '--quiet', '--message', 'crates')
    Invoke-Git $temp @('clone', '--quiet', $first, $second)
    Invoke-Git $second @('config', 'user.name', 'test')
    Invoke-Git $second @('config', 'user.email', 'test@example.invalid')

    # 1. Without a cache directory nothing is keyed, restored or stored.
    $disabled = Get-RustArtifactCacheEntry -CacheRoot '' -RepositoryRoot $first -CrateRelativePath 'src/gxwire' -BinaryName 'gxwire.exe' -ToolchainFingerprint 'rustc test 1.0'
    Assert-True (-not $disabled.Enabled -and $null -eq $disabled.Key -and -not $disabled.Reason) 'No cache directory must mean no key and no reason to report.'
    Assert-True (-not (Restore-RustArtifact -Entry $disabled -Destination (Join-Path $temp 'never.exe'))) 'A disabled cache must not restore.'
    Assert-True (-not (Save-RustArtifact -Entry $disabled -Source (Join-Path $first 'src\gxwire\Cargo.toml'))) 'A disabled cache must not store.'
    Assert-True (-not (Test-Path -LiteralPath $cacheRoot)) 'A disabled cache must not create the cache directory.'

    # 2. Two checkouts of the same commit share the key; that is the whole point.
    $baseline = Get-Entry $first
    Assert-True ($baseline.Enabled -and $baseline.Key -cmatch '^[0-9a-f]{64}$') "A clean committed crate must be keyed, got reason: $($baseline.Reason)"
    Assert-True ((Get-Entry $second).Key -ceq $baseline.Key) 'A second checkout of the same commit must produce the same key.'

    # 3. What decides the binary changes the key ...
    Assert-True ((Get-Entry $first -Toolchain 'rustc test 2.0').Key -cne $baseline.Key) 'A different compiler must change the key.'
    Assert-True ((Get-Entry $first -Crate 'src/gx-engine' -Binary 'gx-engine.exe').Key -cne $baseline.Key) 'A different crate must change the key.'
    Assert-True ((Get-Entry $first -Binary 'other.exe').Key -cne $baseline.Key) 'A different binary name must change the key.'
    $env:RUSTFLAGS = '-C target-cpu=native'
    Assert-True ((Get-Entry $first).Key -cne $baseline.Key) 'RUSTFLAGS must change the key.'
    [Environment]::SetEnvironmentVariable('RUSTFLAGS', $null, 'Process')
    Set-TextFile (Join-Path $first '.cargo\config.toml') "[build]`nrustflags = [`"-C`", `"debuginfo=2`"]`n"
    Assert-True ((Get-Entry $first).Key -cne $baseline.Key) 'A cargo configuration file above the crate must change the key.'
    Remove-Item -LiteralPath (Join-Path $first '.cargo') -Recurse -Force
    Set-TextFile (Join-Path $env:CARGO_HOME 'config.toml') "[profile.release]`ndebug = true`n"
    Assert-True ((Get-Entry $first).Key -cne $baseline.Key) 'A cargo configuration file in CARGO_HOME must change the key.'
    Remove-Item -LiteralPath (Join-Path $env:CARGO_HOME 'config.toml') -Force
    Assert-True ((Get-Entry $first).Key -ceq $baseline.Key) 'Removing the configuration files must restore the original key.'

    # ... and what only moves or parallelizes the build does not.
    $env:CARGO_TARGET_DIR = Join-Path $temp 'elsewhere'
    $env:CARGO_BUILD_JOBS = '2'
    Set-TextFile (Join-Path $first 'src\gxwire\target\release\gxwire.exe') 'ignored build output'
    Assert-True ((Get-Entry $first).Key -ceq $baseline.Key) 'The target directory, the job count and ignored build output must not change the key.'
    [Environment]::SetEnvironmentVariable('CARGO_TARGET_DIR', $null, 'Process')
    [Environment]::SetEnvironmentVariable('CARGO_BUILD_JOBS', $null, 'Process')
    Remove-Item -LiteralPath (Join-Path $first 'src\gxwire\target') -Recurse -Force

    # 4. Source that is not committed has no identity, so it is never cached.
    $mainSource = Join-Path $second 'src\gxwire\src\main.rs'
    $committedSource = [IO.File]::ReadAllText($mainSource)
    Set-TextFile $mainSource "fn main() { println!(`"edited`"); }`n"
    $edited = Get-Entry $second
    Assert-True (-not $edited.Enabled -and $null -eq $edited.Key -and $edited.Reason -match 'uncommitted or untracked') "An edited tracked file must leave the crate without a key, got: $($edited.Reason)"
    Invoke-Git $second @('add', 'src/gxwire/src/main.rs')
    Assert-True (-not (Get-Entry $second).Enabled) 'A staged change must leave the crate without a key.'
    Invoke-Git $second @('reset', '--quiet', 'HEAD', '--', 'src/gxwire/src/main.rs')
    Set-TextFile $mainSource $committedSource
    Assert-True ((Get-Entry $second).Key -ceq $baseline.Key) 'Restoring the committed content must restore the key.'
    Set-TextFile (Join-Path $second 'src\gxwire\src\extra.rs') "pub fn extra() {}`n"
    Assert-True (-not (Get-Entry $second).Enabled) 'An untracked source file must leave the crate without a key.'
    Remove-Item -LiteralPath (Join-Path $second 'src\gxwire\src\extra.rs') -Force
    Set-TextFile (Join-Path $second 'rust-toolchain.toml') "[toolchain]`nchannel = `"2.0.0`"`n"
    Assert-True (-not (Get-Entry $second).Enabled) 'An uncommitted toolchain pin must leave the crate without a key.'
    Invoke-Git $second @('checkout', '--quiet', '--', 'rust-toolchain.toml')
    Assert-True ((Get-Entry $second -Crate 'src/missing').Reason -match 'not a committed directory') 'A crate that is not committed must be reported, not keyed.'
    $plain = Join-Path $temp 'plain'
    Set-TextFile (Join-Path $plain 'src\gxwire\Cargo.toml') "[package]`n"
    Assert-True (-not (Get-Entry $plain).Enabled) 'A directory that is not a Git checkout must not be keyed.'
    $noCompiler = Get-RustArtifactCacheEntry -CacheRoot $cacheRoot -RepositoryRoot $first -CrateRelativePath 'src/gxwire' -BinaryName 'gxwire.exe' -ToolchainFingerprint ''
    Assert-True (-not $noCompiler.Enabled -and $noCompiler.Reason -match 'rustc or cargo was not found') 'An unidentified compiler must leave the crate without a key.'

    # 5. A committed change is a new key in both directions.
    Set-TextFile $mainSource "fn main() { println!(`"second commit`"); }`n"
    Invoke-Git $second @('commit', '--quiet', '--all', '--message', 'change gxwire')
    $changed = Get-Entry $second
    Assert-True ($changed.Enabled -and $changed.Key -cne $baseline.Key) 'A committed source change must produce a different key.'
    Assert-True ((Get-Entry $second -Crate 'src/gx-engine' -Binary 'gx-engine.exe').Key -ceq (Get-Entry $first -Crate 'src/gx-engine' -Binary 'gx-engine.exe').Key) 'A crate the commit did not touch must keep its key.'
    Set-TextFile (Join-Path $second 'rust-toolchain.toml') "[toolchain]`nchannel = `"2.0.0`"`n"
    Invoke-Git $second @('commit', '--quiet', '--all', '--message', 'change the toolchain pin')
    Assert-True ((Get-Entry $second).Key -cne $changed.Key) 'A committed toolchain pin change must produce a different key.'

    # 6. Store, restore, and refuse a stored binary that no longer matches its record.
    $built = Join-Path $temp 'built.exe'
    Set-TextFile $built 'binary built from the baseline source'
    Assert-True (Save-RustArtifact -Entry $baseline -Source $built) 'The first build of a key must store its binary.'
    Set-TextFile $built 'a later build of the same key'
    Assert-True (-not (Save-RustArtifact -Entry $baseline -Source $built)) 'A second build of the same key must keep the stored binary.'
    $restored = Join-Path $temp 'restored.exe'
    Assert-True (Restore-RustArtifact -Entry (Get-Entry $first) -Destination $restored) 'A checkout with the same key must restore the binary.'
    Assert-True ([IO.File]::ReadAllText($restored) -ceq 'binary built from the baseline source') 'The restored binary must be the stored one, byte for byte.'
    Assert-True (-not (Restore-RustArtifact -Entry $changed -Destination (Join-Path $temp 'changed.exe'))) 'A different key must not restore another key''s binary.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $temp 'changed.exe'))) 'A refused restore must leave no file behind.'

    Remove-Item -LiteralPath $restored -Force
    Set-TextFile (Join-Path $baseline.Directory 'gxwire.exe') 'tampered'
    $warnings = @(Restore-RustArtifact -Entry $baseline -Destination $restored 3>&1 | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
    Assert-True ($warnings.Count -eq 1 -and -not (Test-Path -LiteralPath $restored)) 'A stored binary that does not match its record must be refused with one warning and no copy.'
    Assert-True (Save-RustArtifact -Entry $baseline -Source $built) 'A build must replace an entry that does not verify.'
    Assert-True ((Restore-RustArtifact -Entry $baseline -Destination $restored) -and [IO.File]::ReadAllText($restored) -ceq 'a later build of the same key') 'The replacing binary must be the one restored afterwards.'
    $record = Join-Path $baseline.Directory 'artifact.json'
    $metadata = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $metadata.key = $changed.Key
    Set-TextFile $record ($metadata | ConvertTo-Json)
    Assert-True ($null -eq (Test-RustArtifactEntry -Entry $baseline)) 'A record written for another key must not verify.'
    Remove-Item -LiteralPath $restored -Force
    $warnings = @(Restore-RustArtifact -Entry $baseline -Destination $restored 3>&1 | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
    Assert-True ($warnings.Count -eq 1 -and -not (Test-Path -LiteralPath $restored)) 'A record written for another key must be refused with one warning and no copy.'
    Assert-True (@(Get-ChildItem -LiteralPath (Split-Path -Parent $baseline.Directory) -Force | Where-Object Name -like '.*').Count -eq 0) 'Staging and rejected directories must not be left behind.'
    Remove-Item -LiteralPath $cacheRoot -Recurse -Force

    # 7. The Rust steps of build.ps1, executed against a fake toolchain.
    $buildSource = Get-Content -LiteralPath (Join-Path $root 'build.ps1') -Raw
    Assert-True ($buildSource -match '(?m)^\. \(Join-Path \$root "scripts\\rust-artifact-cache\.ps1"\)\s*$') 'build.ps1 must load scripts\rust-artifact-cache.ps1.'
    $rustSegment = Get-BuildSegment $buildSource '# 6.05 Build and bundle GxWire (Rust engine)' '# 6.07 Release builds must ship the default backend'
    $script:cargoBuilds = New-Object System.Collections.Generic.List[string]
    $script:buildSerial = 0
    function rustc { $global:LASTEXITCODE = 0; 'rustc fake 1.0.0'; 'host: test' }
    function cargo {
        if ("$args" -eq '-V') { $global:LASTEXITCODE = 0; return 'cargo fake 1.0.0' }
        Assert-True ("$args" -eq 'build --release') "build.ps1 ran an unexpected cargo command: $args"
        $crate = Split-Path -Leaf (Get-Location).Path
        $script:cargoBuilds.Add($crate)
        # Every fake build writes different bytes, so a rebuilt binary can be told from a reused one.
        $script:buildSerial++
        Set-TextFile (Join-Path (Get-Location).Path "target\release\$crate.exe") "$crate build $($script:buildSerial)"
        $global:LASTEXITCODE = 0
    }
    function Invoke-RustSteps {
        param([string]$Repository, [string]$Cache)
        $root = $Repository
        $publishDir = Join-Path $Repository 'publish'
        $RustArtifactCache = $Cache
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
        New-Item -ItemType Directory -Path $publishDir | Out-Null
        $script:cargoBuilds.Clear()
        $output = & ([scriptblock]::Create($rustSegment)) 6>&1
        return @($output | ForEach-Object { [string]$_ })
    }
    function Get-Published {
        param([string]$Repository, [string]$Binary)
        return [IO.File]::ReadAllText((Join-Path $Repository "publish\$Binary"))
    }
    Set-TextFile (Join-Path $first '.gitignore') "publish/`n"
    Invoke-Git $first @('add', '.gitignore')
    Invoke-Git $first @('commit', '--quiet', '--message', 'ignore publish')
    $third = Join-Path $temp 'third'
    Invoke-Git $temp @('clone', '--quiet', $first, $third)

    $log = Invoke-RustSteps -Repository $first -Cache $cacheRoot
    Assert-True (($script:cargoBuilds -join ',') -ceq 'gxwire,gx-engine') "An empty cache must compile both crates, compiled: $($script:cargoBuilds -join ',')"
    Assert-True (@($log | Where-Object { $_ -match 'Stored (gxwire|gx-engine)\.exe in the Rust artifact cache' }).Count -eq 2) 'Both freshly built binaries must be stored.'
    $firstWire = Get-Published $first 'gxwire.exe'
    $firstEngine = Get-Published $first 'gx-engine.exe'

    $log = Invoke-RustSteps -Repository $third -Cache $cacheRoot
    Assert-True ($script:cargoBuilds.Count -eq 0) "A clean checkout of the same commit must not compile, compiled: $($script:cargoBuilds -join ',')"
    Assert-True (@($log | Where-Object { $_ -match 'Reused (gxwire|gx-engine)\.exe from the Rust artifact cache' }).Count -eq 2) 'Both reused binaries must be reported.'
    Assert-True ((Get-Published $third 'gxwire.exe') -ceq $firstWire -and (Get-Published $third 'gx-engine.exe') -ceq $firstEngine) 'The reused binaries must be the ones the first checkout built.'

    Set-TextFile (Join-Path $third 'src\gx-engine\src\main.rs') "fn main() { println!(`"work in progress`"); }`n"
    $log = Invoke-RustSteps -Repository $third -Cache $cacheRoot
    Assert-True (($script:cargoBuilds -join ',') -ceq 'gx-engine') "Only the crate with uncommitted source must compile, compiled: $($script:cargoBuilds -join ',')"
    Assert-True (@($log | Where-Object { $_ -match 'Rust artifact cache not used for gx-engine\.exe: uncommitted or untracked files' }).Count -eq 1) 'The reason a crate was not cached must be reported.'
    Assert-True ((Get-Published $third 'gx-engine.exe') -cne $firstEngine -and (Get-Published $third 'gxwire.exe') -ceq $firstWire) 'Uncommitted source must ship its own build, never the cached binary.'
    Assert-True (@($log | Where-Object { $_ -match 'Stored gx-engine\.exe' }).Count -eq 0) 'A build of uncommitted source must not be stored.'

    Set-TextFile (Join-Path $third 'src\gxwire\src\main.rs') "fn main() { println!(`"work in progress too`"); }`n"
    $log = Invoke-RustSteps -Repository $third -Cache $cacheRoot
    Assert-True (($script:cargoBuilds -join ',') -ceq 'gxwire,gx-engine') "Both crates with uncommitted source must compile, compiled: $($script:cargoBuilds -join ',')"
    Assert-True (@($log | Where-Object { $_ -match 'Rust artifact cache not used for gxwire\.exe: uncommitted or untracked files' }).Count -eq 1) 'The reason gxwire was not cached must be reported.'
    Assert-True ((Get-Published $third 'gxwire.exe') -cne $firstWire) 'Uncommitted gxwire source must ship its own build.'
    Assert-True (@($log | Where-Object { $_ -match 'Stored ' }).Count -eq 0) 'No build of uncommitted source may be stored.'

    $log = Invoke-RustSteps -Repository $first -Cache ''
    Assert-True (($script:cargoBuilds -join ',') -ceq 'gxwire,gx-engine') 'Without -RustArtifactCache both crates must compile, as before.'
    Assert-True (@($log | Where-Object { $_ -match 'Rust artifact cache' }).Count -eq 0) 'Without -RustArtifactCache the cache must not be mentioned.'

    # 8. A release build compiles its own binaries: the combination is refused.
    $releaseGuard = Get-BuildSegment $buildSource '# 0.0 A release compiles its own Rust binaries' 'Write-Host "[build] Preparing build..."'
    function Fail-Build([string]$message, [int]$exitCode = 1) { throw "Fail-Build: $message" }
    function Invoke-ReleaseGuard {
        param([bool]$Require, [string]$Cache)
        $RequireEngineBinaries = [switch]$Require
        $RustArtifactCache = $Cache
        & ([scriptblock]::Create($releaseGuard))
    }
    $threw = $null
    try { Invoke-ReleaseGuard -Require $true -Cache $cacheRoot } catch { $threw = $_.Exception.Message }
    Assert-True ($threw -match '-RustArtifactCache cannot be combined with -RequireEngineBinaries') "A release build must refuse the artifact cache, got: $threw"
    Invoke-ReleaseGuard -Require $true -Cache ''
    Invoke-ReleaseGuard -Require $false -Cache $cacheRoot
    $releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
    Assert-True ($releaseSource -notmatch 'RustArtifactCache') 'release.ps1 must not pass -RustArtifactCache.'
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'rust-artifact-cache: key, verification and build.ps1 reuse checks passed' -ForegroundColor Green
