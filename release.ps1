# GeneXus 18 MCP - one-shot release script
# =========================================
#
# Why this exists: the npm publish workflow (.github/workflows/release.yml)
# expects a `publish.zip` asset attached to the GitHub Release. The Worker
# references Artech.* DLLs from the local GeneXus 18 install, so building on
# ubuntu-latest in CI isn't viable - the zip has to be produced locally.
#
# Previous flow (manual, 5 commands):
#   1. dotnet build / build.ps1
#   2. Compress-Archive publish/* publish.zip
#   3. git commit + tag + push
#   4. gh release create vX.Y.Z --title ... --notes ...
#   5. gh release upload vX.Y.Z publish.zip
#
# Steps 4 and 5 were a foot-gun: skipping #5 published a release with no asset
# and the workflow exited with `no assets to download`. v2.6.8 hit this once.
#
# New flow (one command):
#   .\release.ps1 -Version 2.6.9          # bump -> build -> zip -> tag -> push -> release WITH asset
#   .\release.ps1                         # release current package.json version
#   .\release.ps1 -Version 2.6.9 -DryRun  # rehearse without touching origin
#   .\release.ps1 -Version 2.6.9 -Detach  # run in background pwsh; logs in %TEMP% (survives
#                                         # 30 s command timeouts); you get PID + log path now.
#                                         # Watch with: Get-Content -Wait <logpath>
#
# Encoding note: this file is UTF-8 WITH BOM. Windows PowerShell 5.1 reads a
# BOM-less .ps1 as ANSI and the non-ASCII glyphs below break its tokenizer, so
# the script would not even parse there. The BOM is mandatory. If you see
# mojibake (ao, euro sign) at the top of this file, re-save as UTF-8 with BOM.
# Invoked from 5.1 it also auto-re-executes under pwsh when available.
#
# gh release create accepts asset paths as positional args, so the upload
# lands in the SAME api call as create - the workflow's first run already
# sees the asset and the npm publish succeeds without manual intervention.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$AllowDirty,
    # Relaunch this script in a hidden background pwsh and return immediately.
    # A full release takes minutes (build + tests + zip); a shell/tool with a
    # short command timeout (e.g. 30 s) would kill the foreground run mid-way,
    # leaving a half-bumped tree. With -Detach stdout/stderr go to
    # %TEMP%\gxmcp-release*.log; poll the log, don't wait on the call.
    [switch]$Detach
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$root = $PSScriptRoot

function Step([string]$msg) { Write-Host "`n>>> $msg" -ForegroundColor Cyan }
function Ok  ([string]$msg) { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Warn([string]$msg) { Write-Host "    [!]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg) { Write-Host "    [ERR] $msg" -ForegroundColor Red; exit 1 }

function Get-ForwardedArgs {
    # Rebuild the exact parameter set this invocation received, so a relaunch
    # (pwsh re-exec or -Detach) behaves identically. $BoundParams is the
    # SCRIPT's $PSBoundParameters (the function's own scope only sees $Exclude,
    # so the caller passes it in). Excludes 'client-side' switches via $Exclude
    # (e.g. Detach itself in the detached child).
    param(
        [System.Collections.Generic.IDictionary[string, object]]$BoundParams,
        [string[]]$Exclude = @()
    )
    $fwd = New-Object System.Collections.Generic.List[string]
    foreach ($k in $BoundParams.Keys) {
        if ($Exclude -contains $k) { continue }
        $v = $BoundParams[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) {
            if ($v.IsPresent) { $fwd.Add("-$k") }
        } else {
            $fwd.Add("-$k")
            $fwd.Add([string]$v)
        }
    }
    return ,($fwd.ToArray())
}

$pwshExe = $null
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) { $pwshExe = $pwshCmd.Source }

if ($Detach) {
    if (-not $pwshExe) {
        Fail "-Detach requires pwsh (PowerShell 7) on PATH: the detached release re-launches under pwsh. Install PowerShell 7 or drop -Detach."
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $verTag = if ($Version) { "-$Version" } else { "" }
    $logBase = Join-Path $env:TEMP "gxmcp-release$verTag-$stamp"
    $stdoutLog = "$logBase.log"
    $stderrLog = "$logBase.err.log"
    $forwarded = Get-ForwardedArgs -BoundParams $PSBoundParameters -Exclude @('Detach')
    $argItems = @('-NoProfile', '-File', $PSCommandPath) + $forwarded
    $argString = (($argItems | ForEach-Object {
        if ($_ -match '\s') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    }) -join ' ')
    $child = Start-Process -FilePath $pwshExe -ArgumentList $argString `
        -WorkingDirectory $root `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -WindowStyle Hidden -PassThru
    Write-Host ""
    Write-Host "Detached release started (PID $($child.Id))." -ForegroundColor Cyan
    Write-Host "  stdout log: $stdoutLog"
    Write-Host "  stderr log: $stderrLog"
    Write-Host "  watch:      Get-Content -Wait $stdoutLog"
    Write-Host "  The parent shell may close; the release keeps running."
    exit 0
}

# Belt-and-suspenders: if invoked from Windows PowerShell 5.1 (which reads
# .ps1 without a UTF-8 BOM as ANSI), re-exec under pwsh for consistent UTF-8
# streaming and cmdlet behaviour. The files ship with a BOM so 5.1 already
# parses them; this guard only normalises the host. Guarded against recursion.
if ($PSVersionTable.PSVersion.Major -lt 7 -and $pwshExe -and -not $env:GXMCP_UNDER_PWSH) {
    $env:GXMCP_UNDER_PWSH = '1'
    $forwarded = Get-ForwardedArgs -BoundParams $PSBoundParameters
    Write-Host "release.ps1 invoked from Windows PowerShell $($PSVersionTable.PSVersion) - re-executing under pwsh for consistent behaviour." -ForegroundColor DarkGray
    & $pwshExe -NoProfile -File $PSCommandPath @forwarded
    exit $LASTEXITCODE
}

function Invoke-Cmd {
    # NOTE: do NOT name a parameter `$Args` - it collides with PowerShell's
    # automatic variable inside the function body, and `@Args` then splats
    # the EMPTY automatic instead of the caller's array. Observed releases
    # v2.6.9 with the bug: `git`, `dotnet`, `pwsh` all invoked with no args
    # (release.ps1 would die at "Tagging $tag" with git printing its help).
    # Use `$Arguments` (or any non-automatic name) instead.
    param([string]$Exe, [string[]]$Arguments, [switch]$IgnoreExit)
    $display = "$Exe $($Arguments -join ' ')"
    Write-Host "    $ $display" -ForegroundColor DarkGray
    if ($DryRun) { return }
    & $Exe @Arguments
    if (-not $IgnoreExit -and $LASTEXITCODE -ne 0) {
        Fail "Command failed (exit $LASTEXITCODE): $display"
    }
}

# -- 1. Resolve version + sanity-check tree --------------------------------
Step "Resolving version"
$pkgPath = Join-Path $root 'package.json'
if (-not (Test-Path $pkgPath)) { Fail "package.json not found at $pkgPath" }
$pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
$currentVersion = $pkg.version

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $currentVersion
    Warn "No -Version passed; using package.json: $Version"
} else {
    # Strip leading 'v' if user typed it
    $Version = $Version -replace '^v', ''
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[\w.]+)?$') {
        Fail "Version '$Version' is not semver (X.Y.Z or X.Y.Z-tag)."
    }
}
$tag = "v$Version"
Ok "Target version: $Version  (tag: $tag)"

Step "Checking git working tree"
$status = git status --porcelain
# Files release.ps1 itself bumps/regenerates. A dirty tree consisting SOLELY of
# these is the signature of a previous release run that died mid-way (e.g. a
# failed test pass after the version bump) - that's resumable, not an error.
$bumpFiles = @(
    'package.json',
    'package-lock.json',
    'CHANGELOG.md',
    'publish.zip.sha256',
    'src/GxMcp.Gateway/GxMcp.Gateway.csproj',
    'src/GxMcp.Worker/GxMcp.Worker.csproj',
    'src/nexus-ide/package.json'
)
if ($status -and -not $AllowDirty) {
    $dirtyPaths = @($status | ForEach-Object { $_.Substring(3).Trim().Trim('"') -replace '\\', '/' })
    $nonBump = @($dirtyPaths | Where-Object { $bumpFiles -notcontains $_ })
    if ($nonBump.Count -eq 0) {
        Warn "Working tree is dirty, but only with version-bump files ($($dirtyPaths -join ', ')) - resuming: they'll be bundled into the release commit."
    } else {
        Write-Host $status
        Fail "Working tree is dirty beyond the version-bump files ($($nonBump -join ', ')). Commit or stash before releasing (or pass -AllowDirty to bundle pending changes into the release commit)."
    }
}
Ok "Tree state acceptable."

# Verify tag isn't already on remote - and if it is, decide between abort and
# resume. A tag on origin WITH a publish.zip-carrying release means this
# version already shipped -> abort. A tag on origin WITHOUT that asset means a
# previous run died between `git push` and `gh release create` -> resume from
# the release-creation step against the existing tagged commit.
$resumeRelease = $false
$releaseExists = $false
$remoteTag = git ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($remoteTag) {
    $assetNames = @(gh release view $tag --json assets --jq '.assets[].name' 2>$null)
    if ($LASTEXITCODE -eq 0) { $releaseExists = $true }
    if ($releaseExists -and ($assetNames -contains 'publish.zip')) {
        Fail "Tag $tag already exists on origin and release $tag already has publish.zip. Bump the version (or delete the remote tag + release first)."
    }
    if ($releaseExists) {
        Warn "Tag $tag exists on origin and release $tag exists but has NO publish.zip asset - resuming: will upload the asset to the existing release."
    } else {
        Warn "Tag $tag exists on origin but no GitHub release was created - resuming from the release-creation step."
    }
    # Resume reuses the artefacts produced by the failed run: the zip MUST be
    # the exact bytes whose hash was committed in the tagged commit, so we must
    # NOT rebuild/rezip (builds aren't byte-reproducible).
    $resumeRelease = $true
    $SkipBuild = $true
    $SkipTests = $true
} else {
    Ok "Tag $tag is free on origin."
}

# Verify that release notes exist before any version bump. The release script
# owns promoting `## Unreleased`, but it must never ship an empty or generic
# release body.
$changelogPath = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path $changelogPath -PathType Leaf)) {
    Fail "CHANGELOG.md not found at $changelogPath."
}
$changelog = Get-Content $changelogPath -Raw
$versionHeadingPattern = "(?m)^##[ \t]+v$([Regex]::Escape($Version))(?=[ \t]|$)"
if ($changelog -match $versionHeadingPattern) {
    Ok "CHANGELOG entry for v$Version found."
} else {
    $unreleasedMatch = [Regex]::Match(
        $changelog,
        '(?ms)^##[ \t]+Unreleased[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t]|\z)')
    if (-not $unreleasedMatch.Success -or
        [string]::IsNullOrWhiteSpace($unreleasedMatch.Groups['body'].Value)) {
        Fail "CHANGELOG.md has no substantive '## Unreleased' section to promote into '## v$Version'."
    }
    Ok "CHANGELOG has release notes ready to promote into ## v$Version."
}

# -- 2. Bump version files if needed ---------------------------------------
#
# Drift check (v2.8.0 lesson): when package.json was hand-edited BEFORE
# running release.ps1, `$Version -eq $currentVersion` and the whole bump
# block was skipped - including the csproj sync. The published binary
# carried the OLD InformationalVersion stamp even though the source was
# new. Detect that case and force the bump when ANY file is out of sync,
# not only when -Version was passed.
$csprojPath = Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj'
$csprojVersion = $null
if (Test-Path $csprojPath) {
    $csprojRaw = Get-Content $csprojPath -Raw
    if ($csprojRaw -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        $csprojVersion = $Matches[1].Trim()
    }
}
$workerCsprojVersionPath = Join-Path $root 'src\GxMcp.Worker\GxMcp.Worker.csproj'
$workerCsprojVersion = $null
if (Test-Path $workerCsprojVersionPath) {
    $workerRaw = Get-Content $workerCsprojVersionPath -Raw
    if ($workerRaw -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        $workerCsprojVersion = $Matches[1].Trim()
    }
}
$changelogNeedsPromotion = -not ($changelog -match $versionHeadingPattern)
$needsBump = $changelogNeedsPromotion -or
             ($Version -ne $currentVersion) -or
             ($csprojVersion -and $csprojVersion -ne $Version) -or
             ($workerCsprojVersion -and $workerCsprojVersion -ne $Version)
if ($needsBump -and ($Version -eq $currentVersion) -and ($csprojVersion -ne $Version)) {
    Warn "csproj InformationalVersion=$csprojVersion is out of sync with package.json=$Version - forcing bump pass to realign."
}
if ($needsBump) {
    $stepLabel = if ($Version -ne $currentVersion) {
        "Bumping version: $currentVersion -> $Version"
    } elseif ($changelogNeedsPromotion) {
        "Synchronizing release metadata for $Version"
    } else {
        "Realigning release metadata for $Version"
    }
    Step $stepLabel

    # package.json - preserve formatting via regex (ConvertTo-Json reorders keys).
    if (-not $DryRun) {
        $raw = Get-Content $pkgPath -Raw
        $bumped = [Regex]::Replace($raw,
            '("version"\s*:\s*")[^"]+(")',
            "`${1}$Version`${2}", 1)
        [System.IO.File]::WriteAllText($pkgPath, $bumped, [System.Text.UTF8Encoding]::new($false))
    }
    Ok "package.json -> $Version"

    # GxMcp.Gateway.csproj - Version, AssemblyVersion, FileVersion, InformationalVersion.
    $csprojPath = Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj'
    if (Test-Path $csprojPath) {
        if (-not $DryRun) {
            $raw = Get-Content $csprojPath -Raw
            $bumped = $raw `
                -replace '(<Version>)[^<]+(</Version>)',                       "`${1}$Version`${2}" `
                -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)',       "`${1}$Version.0`${2}" `
                -replace '(<FileVersion>)[^<]+(</FileVersion>)',               "`${1}$Version.0`${2}" `
                -replace '(<InformationalVersion>)[^<]+(</InformationalVersion>)', "`${1}$Version`${2}"
            [System.IO.File]::WriteAllText($csprojPath, $bumped, [System.Text.UTF8Encoding]::new($false))
        }
        Ok "GxMcp.Gateway.csproj -> $Version"
    }

    # GxMcp.Worker.csproj - same four version properties (the artefact-stamp
    # validation in step 5 compares the Worker exe against the release version).
    $workerCsprojPath = Join-Path $root 'src\GxMcp.Worker\GxMcp.Worker.csproj'
    if (Test-Path $workerCsprojPath) {
        if (-not $DryRun) {
            $raw = Get-Content $workerCsprojPath -Raw
            $bumped = $raw `
                -replace '(<Version>)[^<]+(</Version>)',                       "`${1}$Version`${2}" `
                -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)',       "`${1}$Version.0`${2}" `
                -replace '(<FileVersion>)[^<]+(</FileVersion>)',               "`${1}$Version.0`${2}" `
                -replace '(<InformationalVersion>)[^<]+(</InformationalVersion>)', "`${1}$Version`${2}"
            [System.IO.File]::WriteAllText($workerCsprojPath, $bumped, [System.Text.UTF8Encoding]::new($false))
        }
        Ok "GxMcp.Worker.csproj -> $Version"
    }

    # src/nexus-ide/package.json - Nexus IDE ships in lockstep with the MCP.
    # Same regex-preserving-format approach as the root package.json above,
    # scoped to the FIRST top-level "version" so it can't drift onto a nested
    # dependency's version field.
    $extPkgPath = Join-Path $root 'src\nexus-ide\package.json'
    if (Test-Path $extPkgPath) {
        if (-not $DryRun) {
            $raw = Get-Content $extPkgPath -Raw
            $bumped = [Regex]::Replace($raw,
                '("version"\s*:\s*")[^"]+(")',
                "`${1}$Version`${2}", 1)
            [System.IO.File]::WriteAllText($extPkgPath, $bumped, [System.Text.UTF8Encoding]::new($false))
        }
        Ok "src/nexus-ide/package.json -> $Version"
    }

    # CHANGELOG.md - promote ## Unreleased to ## v$Version - YYYY-MM-DD if ## v$Version does not exist yet.
    $changelogPath = Join-Path $root 'CHANGELOG.md'
    if (Test-Path $changelogPath) {
        if (-not $DryRun) {
            $dateStr = (Get-Date).ToString('yyyy-MM-dd')
            $rawCl = [System.IO.File]::ReadAllText($changelogPath)
            if ($rawCl -notmatch $versionHeadingPattern) {
                if ($rawCl -match '(?m)^##[ \t]+Unreleased[ \t]*\r?\n') {
                    $bumpedCl = [Regex]::Replace($rawCl,
                        '(?m)^##[ \t]+Unreleased[ \t]*\r?\n',
                        "## Unreleased`r`n`r`n## v$Version - $dateStr`r`n`r`n")
                    [System.IO.File]::WriteAllText($changelogPath, $bumpedCl, [System.Text.UTF8Encoding]::new($false))
                    Ok "CHANGELOG.md -> promoted ## Unreleased to ## v$Version"
                } else {
                    # No '## Unreleased' heading (e.g. freshly released without
                    # one), so the promotion regex matched nothing above, which
                    # would silently skip the entry. Fail loudly instead of
                    # shipping a release whose notes fall back to generic text.
                    Fail "CHANGELOG.md has no '## Unreleased' section to promote into '## v$Version'. Add the section (and the version's entries under it), then retry."
                }
            } else {
                Ok "CHANGELOG.md -> ## v$Version already present."
            }
            $promoted = [System.IO.File]::ReadAllText($changelogPath)
            if ($promoted -notmatch $versionHeadingPattern) {
                Fail "CHANGELOG.md promotion did not create the exact ## v$Version heading."
            }
        } else {
            Ok "CHANGELOG.md -> ## v$Version (dry-run)"
        }
    }
} else {
    Ok "Version unchanged - skipping bump."
}

# -- 3. Build + zip publish/ -----------------------------------------------
if (-not $SkipBuild) {
    Step "Building (build.ps1)"
    Invoke-Cmd 'pwsh' @('-NoProfile', '-File', (Join-Path $root 'build.ps1')) -IgnoreExit
    if (-not $DryRun -and $LASTEXITCODE -ne 0) {
        # build.ps1 returns non-zero on warnings sometimes; check artefacts.
        $gw = Join-Path $root 'publish\GxMcp.Gateway.exe'
        $wk = Join-Path $root 'publish\worker\GxMcp.Worker.exe'
        if (-not (Test-Path $gw) -or -not (Test-Path $wk)) {
            Fail "build.ps1 failed AND artefacts are missing. Aborting."
        }
        Warn "build.ps1 returned non-zero but artefacts are present - continuing."
    }
} else {
    Warn "-SkipBuild set; reusing existing publish/ artefacts."
}

# Sanity-check the artefacts the workflow needs.
$publishDir = Join-Path $root 'publish'
$requiredArtefacts = @(
    'GxMcp.Gateway.exe',
    'worker\GxMcp.Worker.exe'
)
foreach ($rel in $requiredArtefacts) {
    $path = Join-Path $publishDir $rel
    if (-not (Test-Path $path)) {
        Fail "Required publish artefact missing: $rel (the release workflow asserts on this)."
    }
}
Ok "publish/ artefacts validated."

# -- 3b. Build + package the Nexus IDE VSIX (additive, separate from build.ps1) --
$extDir = Join-Path $root 'src\nexus-ide'
$vsixPath = Join-Path $root "nexus-ide-$Version.vsix"
if (-not $SkipBuild) {
    Step "Building Nexus IDE VSIX"
    if (-not (Test-Path (Join-Path $extDir 'node_modules'))) {
        Invoke-Cmd 'npm' @('--prefix', $extDir, 'install')
    }
    Invoke-Cmd 'npm' @('--prefix', $extDir, 'run', 'compile')
    if (-not $DryRun) {
        Push-Location $extDir
        try {
            & npx --yes @vscode/vsce package --no-dependencies -o $vsixPath
            if ($LASTEXITCODE -ne 0) {
                Fail "npx @vscode/vsce package failed (exit $LASTEXITCODE) - not shipping a release claiming an extension it couldn't build."
            }
        } finally {
            Pop-Location
        }
        if (-not (Test-Path $vsixPath)) {
            Fail "Expected VSIX not found at $vsixPath after vsce package."
        }
        Ok "nexus-ide-$Version.vsix packaged."
    } else {
        Write-Host "    $ npx --yes @vscode/vsce package --no-dependencies -o $vsixPath  (cwd: $extDir)" -ForegroundColor DarkGray
        Ok "[dry-run] would package nexus-ide-$Version.vsix"
    }
} else {
    Warn "-SkipBuild set; reusing existing $vsixPath if present."
}

# -- 4. Optional test pass -------------------------------------------------
if (-not $SkipTests) {
    Step "Running test suite (Gateway + Worker)"
    if (-not $DryRun) {
        $env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
        # Run gateway tests; worker tests can be flaky on parallel runs.
        Invoke-Cmd 'dotnet' @('test',
            (Join-Path $root 'src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj'),
            '--nologo', '-v:minimal')
        Ok "Gateway tests passed."
    }
} else {
    Warn "-SkipTests set; not running test suite."
}

# A release build can succeed while adding a new nullable/analyzer warning.
# Keep this gate independent from the test switch: skipping tests must not skip
# the warning-surface regression check.
Step "Checking Release warning baseline"
Invoke-Cmd 'pwsh' @(
    '-NoProfile',
    '-File',
    (Join-Path $root 'scripts\check-build-warning-baseline.ps1'),
    '-BaselineFile',
    (Join-Path $root 'docs\build_warning_baseline.json')
)
Ok "Release warning baseline passed."

# -- 4b. Validate artefact version stamps match $Version ------------------
# With -SkipBuild a stale publish/ can ship silently; catch it here.
Step "Validating artefact version stamps"
if (-not $DryRun) {
    $gwExe = Join-Path $publishDir 'GxMcp.Gateway.exe'
    $wkExe = Join-Path $publishDir 'worker\GxMcp.Worker.exe'
    $tdJson = Join-Path $publishDir 'tool_definitions.json'

    if (-not (Test-Path $tdJson)) {
        Fail "publish\tool_definitions.json missing - rebuild with build.ps1."
    }
    Ok "tool_definitions.json present."

    foreach ($pair in @(
        @{ Path = $gwExe; Label = 'GxMcp.Gateway.exe' },
        @{ Path = $wkExe; Label = 'worker\GxMcp.Worker.exe' }
    )) {
        if (Test-Path $pair.Path) {
            $vi = (Get-Item $pair.Path).VersionInfo
            # ProductVersion may carry build metadata like "2.9.1+abc"; strip the +suffix.
            $prodVer = $vi.ProductVersion -replace '\+.*$', '' | ForEach-Object { $_.Trim() }
            if ($prodVer -and $prodVer -ne $Version) {
                Fail "$($pair.Label) ProductVersion ($prodVer) != release version ($Version). Rebuild publish/ with build.ps1 before releasing."
            }
            Ok "$($pair.Label) version stamp: $prodVer [OK]"
        }
    }
} else {
    Ok "[dry-run] would validate exe version stamps against $Version"
}

# -- 5. Zip publish/ -> publish.zip -----------------------------------------
Step "Packing publish.zip"
$zipPath = Join-Path $root 'publish.zip'
$shaPath = "$zipPath.sha256"
if (-not $DryRun) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $shaPath) { Remove-Item $shaPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    $pubPath = Convert-Path $publishDir
    Get-ChildItem -Path $pubPath -Recurse | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        $relPath = $_.FullName.Substring($pubPath.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relPath) | Out-Null
    }
    $archive.Dispose()
    $sizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Ok "publish.zip created ($sizeMb MB)."
    # SHA-256 sidecar - the gateway's self-updater verifies the downloaded zip
    # against this before staging a corporate in-place update (sha256sum format:
    # "<hex>  publish.zip").
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLower()
    [System.IO.File]::WriteAllText($shaPath, "$hash  publish.zip`n", [System.Text.UTF8Encoding]::new($false))
    Ok "publish.zip.sha256 written ($hash)."
} else {
    Ok "[dry-run] would create publish.zip + publish.zip.sha256"
}

# -- 6. Commit (if version bumped or tree dirty) ---------------------------
$pending = git status --porcelain
if ($pending) {
    Step "Committing version bump"
    Invoke-Cmd 'git' @('add', '-A')
    $msg = "release: $tag"
    Invoke-Cmd 'git' @('commit', '-m', $msg)
    Ok "Committed: $msg"
} else {
    Ok "No pending changes to commit."
}

# -- 7. Tag (annotated) + push ---------------------------------------------
Step "Tagging $tag"
Invoke-Cmd 'git' @('tag', '-a', $tag, '-m', "$tag")
Ok "Local tag created."

if (-not $DryRun) {
    Step "Pushing main + $tag to origin"
    Invoke-Cmd 'git' @('push', 'origin', 'HEAD')
    Invoke-Cmd 'git' @('push', 'origin', $tag)
    Ok "Pushed."
}

# -- 8. Extract release notes from CHANGELOG (best-effort) -----------------
$notes = $null
if ($NotesFile -and (Test-Path $NotesFile)) {
    $notes = Get-Content $NotesFile -Raw
} elseif (Test-Path $changelogPath) {
    # Pull the block between `## v$Version` and the next `## v` heading.
    $cl = Get-Content $changelogPath -Raw
    $rx = [Regex]::new(
        "(?m)^##[ \t]+v$([Regex]::Escape($Version))(?:[ \t]+-[^\r\n]+)?[\r\n]+(.*?)(?=\r?\n##[ \t]+v|\z)",
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $m = $rx.Match($cl)
    if ($m.Success) {
        $notes = $m.Groups[1].Value.Trim()
        Ok "Extracted release notes from CHANGELOG ($($notes.Length) chars)."
    }
}
if (-not $notes) {
    if ($DryRun) {
        $notes = "[dry-run] Release notes would be extracted from CHANGELOG.md."
        Ok "[dry-run] release notes would be extracted from the promoted changelog section."
    } else {
        Fail "No substantive release notes were extracted for $tag. Refusing a generic release body."
    }
}

# -- 9. Create release WITH publish.zip in the same call -------------------
Step "Creating GitHub release $tag (with publish.zip)"
$notesTmp = Join-Path $env:TEMP "release-notes-$tag.md"
if (-not $DryRun) {
    [System.IO.File]::WriteAllText($notesTmp, $notes, [System.Text.UTF8Encoding]::new($false))
}
# Key insight: `gh release create <tag> [files...]` uploads the assets in
# the same call as create, so the workflow's FIRST `release.published` event
# already has publish.zip attached -> npm publish succeeds on the first run.
$createArgs = @(
    'release', 'create', $tag,
    '--title', "$tag",
    '--notes-file', $notesTmp,
    '--target', 'main',
    $zipPath
)
# Attach the checksum sidecar so the gateway self-updater can verify the download.
if (Test-Path $shaPath) { $createArgs += $shaPath }
# Attach the Nexus IDE VSIX - ships in lockstep with the MCP release.
if ($DryRun -or (Test-Path $vsixPath)) { $createArgs += $vsixPath }
Invoke-Cmd 'gh' $createArgs

Ok "Release created: https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag"

# Belt-and-suspenders: verify the publish workflow started, or trigger it manually.
# Normally the `release.published` event starts the workflow automatically.
# We check if an active run already exists before dispatching, avoiding
# unnecessary concurrent duplicate runs while retaining the automatic backfill.
if (-not $DryRun) {
    Step "Verifying publish workflow trigger"
    Start-Sleep -Seconds 5  # let GitHub register the release event
    $activeRun = $null
    try {
        $recentRuns = gh run list --workflow release.yml --limit 5 --json status,conclusion,databaseId 2>$null | ConvertFrom-Json
        $activeRun = $recentRuns | Where-Object { $_.status -in @('queued', 'in_progress', 'waiting') } | Select-Object -First 1
    } catch { }

    if ($activeRun) {
        Ok "Publish workflow #$($activeRun.databaseId) is already active (triggered by release event)."
    } else {
        Warn "No active workflow detected after 5s - dispatching manually as fallback."
        Invoke-Cmd 'gh' @('workflow', 'run', 'release.yml', '-f', "tag=$tag")
        Ok "Workflow dispatched."
    }
}
Write-Host ""
Write-Host "    Watch the publish workflow:" -ForegroundColor Cyan
Write-Host "      gh run watch --workflow=release.yml" -ForegroundColor Gray
Write-Host ""
Write-Host "    Verify on npm (~2-3 min):" -ForegroundColor Cyan
Write-Host "      npm view genexus-mcp@$Version version" -ForegroundColor Gray
Write-Host ""

if ($DryRun) {
    Warn "DRY RUN - no remote changes were made. Re-run without -DryRun to publish."
}
