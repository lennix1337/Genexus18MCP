# Opt-in cache of the Rust release binaries that build.ps1 bundles.
#
# gxwire.exe and gx-engine.exe do not depend on the GeneXus SDK, yet a clean
# checkout compiles both from scratch: another worktree of the same commit, or
# the same commit built against a second GeneXus installation, repeats minutes
# of identical work. `build.ps1 -RustArtifactCache <dir>` keeps each binary under
# a key derived from what decides its bytes: the committed crate tree, the
# toolchain pin, the compiler that would run, the cargo configuration files in
# effect and the build-affecting environment. A crate with uncommitted or
# untracked files has no key and is always compiled. A stored binary is hashed
# against its record before it is reused.

Set-StrictMode -Version Latest

$script:RustArtifactCacheSchema = 'gxmcp-rust-artifact-v1'
$script:RustToolchainPinFiles = @('rust-toolchain.toml', 'rust-toolchain')

# Environment that changes what cargo compiles. Output location and parallelism
# do not change the binary, so they stay out of the key.
$script:RustArtifactEnvironmentPattern = '^(RUSTFLAGS|RUSTC|RUSTC_WRAPPER|RUSTC_WORKSPACE_WRAPPER|RUSTC_BOOTSTRAP|RUSTUP_TOOLCHAIN|CARGO_ENCODED_RUSTFLAGS|CARGO_BUILD_.+|CARGO_PROFILE_.+|CARGO_TARGET_.+|CARGO_UNSTABLE_.+)$'
$script:RustArtifactNeutralEnvironment = @('CARGO_BUILD_JOBS', 'CARGO_BUILD_TARGET_DIR', 'CARGO_TARGET_DIR')

function Get-RustArtifactTextSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') })
    } finally {
        $sha.Dispose()
    }
}

# The compiler identity is read from inside the crate directory: with rustup the
# toolchain is chosen by the working directory (rust-toolchain.toml), so the
# same command run elsewhere can answer for a different compiler.
function Get-RustToolchainFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$CrateDirectory)

    if (-not (Get-Command rustc -ErrorAction SilentlyContinue)) { return $null }
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { return $null }
    Push-Location -LiteralPath $CrateDirectory
    try {
        $compiler = @(& rustc -vV 2>$null)
        if ($LASTEXITCODE -ne 0 -or $compiler.Count -eq 0) { return $null }
        $driver = @(& cargo -V 2>$null)
        if ($LASTEXITCODE -ne 0 -or $driver.Count -eq 0) { return $null }
        $lines = @(@($compiler) + @($driver) | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
        return $lines -join "`n"
    } finally {
        Pop-Location
    }
}

# Cargo reads .cargo\config.toml (and the legacy .cargo\config) from the crate
# directory up to the drive root, then from CARGO_HOME. Any of them can change
# compiler flags, so each one found is part of the key. A file inside the
# repository is named by its repository-relative path, so two checkouts of the
# same commit agree; a file outside it is named by its full path.
function Get-RustCargoConfigInputs {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$CrateDirectory
    )

    $repositoryPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $candidates = New-Object System.Collections.Generic.List[string]
    $cursor = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($CrateDirectory))
    while ($null -ne $cursor) {
        $candidates.Add((Join-Path $cursor.FullName '.cargo'))
        $cursor = $cursor.Parent
    }
    $cargoHome = $env:CARGO_HOME
    if ([string]::IsNullOrWhiteSpace($cargoHome)) {
        $cargoHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cargo'
    }
    $candidates.Add($cargoHome)

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $inputs = New-Object System.Collections.Generic.List[string]
    foreach ($directory in $candidates) {
        foreach ($name in @('config.toml', 'config')) {
            $path = [IO.Path]::GetFullPath((Join-Path $directory $name))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or -not $seen.Add($path)) { continue }
            $label = if ($path.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $path.Substring($repositoryPrefix.Length).Replace('\', '/')
            } else {
                $path
            }
            $inputs.Add("$label=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())")
        }
    }
    return $inputs.ToArray()
}

# Describes the cache slot of one Rust binary. Enabled is false when no cache
# directory was given (Reason stays empty) or when this checkout cannot be keyed
# (Reason says why); the caller then compiles exactly as it does without a cache.
# -ToolchainFingerprint is a test seam; when omitted the compiler is asked.
function Get-RustArtifactCacheEntry {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$CrateRelativePath,
        [Parameter(Mandatory = $true)][string]$BinaryName,
        [AllowEmptyString()][string]$ToolchainFingerprint
    )

    $crate = $CrateRelativePath.Replace('\', '/').Trim('/')
    $entry = [ordered]@{
        Enabled    = $false
        Reason     = $null
        Key        = $null
        Directory  = $null
        BinaryName = $BinaryName
        Crate      = $crate
        CrateTree  = $null
        Toolchain  = $null
    }
    if ([string]::IsNullOrWhiteSpace($CacheRoot)) { return $entry }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        $entry.Reason = 'git was not found, so the crate source cannot be identified'
        return $entry
    }
    $crateDirectory = Join-Path $RepositoryRoot $crate.Replace('/', '\')
    $tree = @(& git -C $RepositoryRoot rev-parse --verify --quiet "HEAD:$crate" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tree.Count -ne 1 -or -not (Test-Path -LiteralPath $crateDirectory -PathType Container)) {
        $entry.Reason = "$crate is not a committed directory of a Git checkout"
        return $entry
    }
    $entry.CrateTree = ([string]$tree[0]).Trim()

    # Only committed source has an identity. Ignored paths (target\) are not listed.
    $pinFiles = $script:RustToolchainPinFiles
    $pending = @(& git --no-optional-locks -C $RepositoryRoot status --porcelain --untracked-files=all -- $crate $pinFiles 2>$null)
    if ($LASTEXITCODE -ne 0) {
        $entry.Reason = 'git status failed, so pending changes cannot be ruled out'
        return $entry
    }
    if ($pending.Count -gt 0) {
        $entry.Reason = "uncommitted or untracked files ($(([string]$pending[0]).Trim()))"
        return $entry
    }

    $pins = foreach ($name in $script:RustToolchainPinFiles) {
        $blob = @(& git -C $RepositoryRoot rev-parse --verify --quiet "HEAD:$name" 2>$null)
        if ($LASTEXITCODE -eq 0 -and $blob.Count -eq 1) { "$name=$(([string]$blob[0]).Trim())" } else { "$name=absent" }
    }

    $toolchain = if ($PSBoundParameters.ContainsKey('ToolchainFingerprint')) {
        $ToolchainFingerprint
    } else {
        Get-RustToolchainFingerprint -CrateDirectory $crateDirectory
    }
    if ([string]::IsNullOrWhiteSpace($toolchain)) {
        $entry.Reason = 'rustc or cargo was not found, so the compiler cannot be identified'
        return $entry
    }
    $entry.Toolchain = ($toolchain -split "`n")[0]

    $environment = @(Get-ChildItem Env: |
        Where-Object { $_.Name -match $script:RustArtifactEnvironmentPattern -and $_.Name -notin $script:RustArtifactNeutralEnvironment } |
        Sort-Object Name |
        ForEach-Object { "$($_.Name.ToUpperInvariant())=$($_.Value)" })
    $configuration = @(Get-RustCargoConfigInputs -RepositoryRoot $RepositoryRoot -CrateDirectory $crateDirectory)

    $identity = @(
        $script:RustArtifactCacheSchema
        'command=cargo build --release'
        "crate=$crate"
        "binary=$BinaryName"
        "tree=$($entry.CrateTree)"
        "pins=$(@($pins) -join ';')"
        "toolchain=$toolchain"
        "environment=$($environment -join ';')"
        "configuration=$($configuration -join ';')"
    ) -join "`n"
    $entry.Key = Get-RustArtifactTextSha256 -Text $identity
    $entry.Directory = Join-Path (Join-Path $CacheRoot ($crate -split '/')[-1]) $entry.Key
    $entry.Enabled = $true
    return $entry
}

# Reads the record of a slot. Returns the recorded SHA-256 when the record was
# written for this key (which already covers the crate and the binary name),
# otherwise $null.
function Read-RustArtifactRecord {
    param([Parameter(Mandatory = $true)]$Entry)

    if (-not $Entry.Enabled) { return $null }
    $record = Join-Path $Entry.Directory 'artifact.json'
    if (-not (Test-Path -LiteralPath $record -PathType Leaf)) { return $null }
    try {
        $metadata = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        $expected = [string]$metadata.sha256
        if ([string]$metadata.key -cne $Entry.Key) { return $null }
        return $expected
    } catch {
        return $null
    }
}

# Returns the recorded SHA-256 when the slot holds a binary that matches its
# record, otherwise $null.
function Test-RustArtifactEntry {
    param([Parameter(Mandatory = $true)]$Entry)

    $expected = Read-RustArtifactRecord -Entry $Entry
    if ($null -eq $expected) { return $null }
    $binary = Join-Path $Entry.Directory $Entry.BinaryName
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { return $null }
    try {
        if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { return $null }
    } catch {
        return $null
    }
    return $expected
}

# Copies the stored binary to -Destination. The copy is what ships, so the copy
# is what is hashed against the record. Returns $false, leaving no file behind,
# when there is no cache, no entry, or the entry does not verify.
function Restore-RustArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not $Entry.Enabled -or -not (Test-Path -LiteralPath $Entry.Directory -PathType Container)) { return $false }
    $copied = $false
    try {
        $expected = Read-RustArtifactRecord -Entry $Entry
        if ($null -eq $expected) { throw 'its record is missing or was written for another key' }
        Copy-Item -LiteralPath (Join-Path $Entry.Directory $Entry.BinaryName) -Destination $Destination -Force -ErrorAction Stop
        $copied = $true
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
            throw 'the binary does not match its recorded SHA-256'
        }
        return $true
    } catch {
        Write-Warning "Rust artifact cache entry $($Entry.Key.Substring(0, 12)) for $($Entry.BinaryName) was not reused: $($_.Exception.Message)"
        if ($copied -and (Test-Path -LiteralPath $Destination)) { Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue }
        return $false
    }
}

# Stores a freshly built binary. The slot is staged beside its final name and
# renamed into place, so a reader never sees a partial entry. Returns $true only
# when this call created the entry; an equal entry stored first by a concurrent
# build is kept, and one that does not verify is replaced.
function Save-RustArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$Source
    )

    if (-not $Entry.Enabled) { return $false }
    $staging = $null
    try {
        if ($null -ne (Test-RustArtifactEntry -Entry $Entry)) { return $false }
        $parent = Split-Path -Parent $Entry.Directory
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $staging = Join-Path $parent ('.staging-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging | Out-Null
        $staged = Join-Path $staging $Entry.BinaryName
        Copy-Item -LiteralPath $Source -Destination $staged
        $metadata = [ordered]@{
            schema    = $script:RustArtifactCacheSchema
            key       = $Entry.Key
            binary    = $Entry.BinaryName
            sha256    = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant()
            crate     = $Entry.Crate
            crateTree = $Entry.CrateTree
            toolchain = $Entry.Toolchain
            storedUtc = [DateTime]::UtcNow.ToString('o')
        }
        [IO.File]::WriteAllText((Join-Path $staging 'artifact.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

        if (Test-Path -LiteralPath $Entry.Directory) {
            if ($null -ne (Test-RustArtifactEntry -Entry $Entry)) { return $false }
            $rejected = Join-Path $parent ('.rejected-' + [guid]::NewGuid().ToString('N'))
            [IO.Directory]::Move($Entry.Directory, $rejected)
            Remove-Item -LiteralPath $rejected -Recurse -Force -ErrorAction SilentlyContinue
        }
        [IO.Directory]::Move($staging, $Entry.Directory)
        return $true
    } catch {
        # Losing the rename to a concurrent build of the same key is not a failure.
        if ($null -eq (Test-RustArtifactEntry -Entry $Entry)) {
            Write-Warning "Rust artifact cache could not store $($Entry.BinaryName): $($_.Exception.Message)"
        }
        return $false
    } finally {
        if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
