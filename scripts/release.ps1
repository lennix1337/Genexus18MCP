# GeneXus MCP - Release Script (maintainer-only)
# ==========================================
# Usage:
#   .\scripts\release.ps1 patch          # 2.1.2 -> 2.1.3
#   .\scripts\release.ps1 minor          # 2.1.2 -> 2.2.0
#   .\scripts\release.ps1 major          # 2.1.2 -> 3.0.0
#   .\scripts\release.ps1 -NoBump        # publish whatever version is already in package.json
#
# Pre-reqs: .NET 10 SDK, GeneXus 18 installed, `gh auth status` ok,
# clean working tree, on main branch.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false, Position = 0)]
    [ValidateScript({
        if ($_ -in @('patch', 'minor', 'major')) { return $true }
        if ($_ -match '^\d+\.\d+\.\d+(-[\w.\-]+)?$') { return $true }
        throw "BumpType must be 'patch', 'minor', 'major', or an explicit semver like '2.1.2'."
    })]
    [string]$BumpType,

    # Skip the npm-version bump and the version-mirroring into the csproj.
    # Useful when the working tree already carries the intended version (manual bump
    # commit) and you only want to build, tag, push, and create the GitHub release.
    [switch]$NoBump
)

if (-not $NoBump -and -not $BumpType) {
    throw "BumpType is required unless -NoBump is given."
}
if ($NoBump -and $BumpType) {
    throw "Pass BumpType OR -NoBump, not both."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

try {
    Write-Host "[release] Pre-flight checks..." -ForegroundColor Cyan

    if ((git rev-parse --abbrev-ref HEAD) -ne 'main') {
        throw "Must be on 'main' branch."
    }
    if ((git status --porcelain)) {
        throw "Working tree not clean. Commit or stash changes first."
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) not found in PATH."
    }
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh not authenticated. Run 'gh auth login'." }

    Write-Host "[release] Pulling latest main..." -ForegroundColor Cyan
    git pull --ff-only origin main

    $csprojPath = Join-Path $root 'src\GxMcp.Gateway\GxMcp.Gateway.csproj'

    if ($NoBump) {
        Write-Host "[release] -NoBump: using version from package.json..." -ForegroundColor Cyan
        $newVersion = (Get-Content "$root\package.json" -Raw | ConvertFrom-Json).version
        $tag = "v$newVersion"
        Write-Host "   > Version: $newVersion" -ForegroundColor Gray

        # Sanity-check that csproj is already in sync; if not, abort so we don't ship a lie.
        $csprojRaw = Get-Content $csprojPath -Raw
        if ($csprojRaw -notmatch "<InformationalVersion>$([regex]::Escape($newVersion))</InformationalVersion>") {
            throw "csproj InformationalVersion does not match package.json ($newVersion). Run without -NoBump or fix the csproj first."
        }

        # Abort if the tag already exists remotely.
        & git ls-remote --exit-code --tags origin "refs/tags/$tag" 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            throw "Tag $tag already exists on origin. Bump first or delete the remote tag manually."
        }
    }
    else {
        Write-Host "[release] Bumping version ($BumpType)..." -ForegroundColor Cyan
        npm version $BumpType --no-git-tag-version | Out-Null
        $newVersion = (Get-Content "$root\package.json" -Raw | ConvertFrom-Json).version
        $tag = "v$newVersion"
        Write-Host "   > New version: $newVersion" -ForegroundColor Gray

        # Mirror the bumped version into the Gateway csproj so whoami.serverVersion
        # (read at runtime from AssemblyInformationalVersion) stays in sync with
        # the npm-published version. Without this, the gateway surface lies.
        Write-Host "[release] Mirroring version into Gateway csproj..." -ForegroundColor Cyan
        $csproj = Get-Content $csprojPath -Raw
        $assemblyVersion = "$newVersion.0"
        $csproj = [regex]::Replace($csproj, '<Version>[^<]*</Version>',                 "<Version>$newVersion</Version>")
        $csproj = [regex]::Replace($csproj, '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>")
        $csproj = [regex]::Replace($csproj, '<FileVersion>[^<]*</FileVersion>',         "<FileVersion>$assemblyVersion</FileVersion>")
        $csproj = [regex]::Replace($csproj, '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$newVersion</InformationalVersion>")
        Set-Content -Path $csprojPath -Value $csproj -NoNewline
    }

    Write-Host "[release] Building .NET artifacts (build.ps1)..." -ForegroundColor Cyan
    & "$root\build.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[release] Build failed; reverting version bump." -ForegroundColor Red
        git checkout -- package.json 2>$null
        if (Test-Path "$root\package-lock.json") { git checkout -- package-lock.json 2>$null }
        throw "build.ps1 failed."
    }

    $publishDir = Join-Path $root 'publish'
    $gatewayExe = Join-Path $publishDir 'GxMcp.Gateway.exe'
    if (-not (Test-Path $gatewayExe)) {
        throw "Expected $gatewayExe after build; aborting."
    }

    Write-Host "[release] Creating publish.zip..." -ForegroundColor Cyan
    $zipPath = Join-Path $root 'publish.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "   > publish.zip: $zipSize MB" -ForegroundColor Gray

    if ($NoBump) {
        Write-Host "[release] -NoBump: skipping release commit (version already committed)." -ForegroundColor Cyan
        # Still ensure no uncommitted changes after the build (build.ps1 shouldn't dirty the tree).
        if ((git status --porcelain)) {
            throw "Working tree dirty after build under -NoBump. Inspect and commit/reset before retrying."
        }
        # Push pending commits (the user staged the version bump in an earlier commit).
        git push origin main
        git tag -a $tag -m "Release $tag"
        git push origin "refs/tags/$tag"
    }
    else {
        Write-Host "[release] Committing version bump and tagging..." -ForegroundColor Cyan
        git add package.json
        if (Test-Path "$root\package-lock.json") { git add package-lock.json }
        git add $csprojPath
        git commit -m "chore(release): $tag"
        # Annotated tag so `git push --follow-tags` actually pushes it.
        git tag -a $tag -m "Release $tag"
        git push origin main --follow-tags
    }

    Write-Host "[release] Extracting CHANGELOG section for $tag..." -ForegroundColor Cyan
    $changelogPath = Join-Path $root 'CHANGELOG.md'
    $notesFile = $null
    if (Test-Path $changelogPath) {
        $lines = Get-Content $changelogPath
        # Match the header for this version (## v2.3.0 ... or ## [2.3.0] ...). Tolerant of
        # leading whitespace and various date suffixes; first line that ends the version token
        # at a non-version char (space, dash, em-dash, bracket, end-of-line).
        $escaped = [regex]::Escape($newVersion)
        $versionPattern = "^##\s+\[?v?$escaped(?![0-9.])"
        $startIdx = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $versionPattern) { $startIdx = $i; break }
        }
        if ($startIdx -ge 0) {
            # Find next top-level version header (## ...) or end of file.
            $endIdx = $lines.Count
            for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^##\s') { $endIdx = $i; break }
            }
            $section = ($lines[$startIdx..($endIdx - 1)] -join "`n").TrimEnd()
            # Prepend a short callout linking to the full CHANGELOG for context.
            $body = @"
$section

---
**Install / upgrade:** ``npx genexus-mcp@$newVersion init`` or pin in your config.
**Full changelog:** [CHANGELOG.md](https://github.com/lennix1337/Genexus18MCP/blob/$tag/CHANGELOG.md)
"@
            $notesFile = Join-Path $env:TEMP "gxmcp-release-notes-$newVersion.md"
            Set-Content -Path $notesFile -Value $body -Encoding UTF8
            Write-Host "   > Notes extracted to $notesFile ($($body.Length) chars)" -ForegroundColor Gray
        }
        else {
            Write-Host "   > No CHANGELOG entry found for $newVersion; falling back to auto-generated notes." -ForegroundColor Yellow
        }
    }

    Write-Host "[release] Creating GitHub Release with asset..." -ForegroundColor Cyan
    if ($notesFile) {
        & gh release create $tag $zipPath --title $tag --notes-file $notesFile
    }
    else {
        & gh release create $tag $zipPath --title $tag --generate-notes
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
    if ($notesFile) { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }

    Write-Host "[release] Release created. Workflow 'release.yml' will publish to npm with provenance." -ForegroundColor Green
    Write-Host "   > Watch:  gh run watch" -ForegroundColor Gray
    Write-Host "   > Verify: npm view genexus-mcp@$newVersion dist" -ForegroundColor Gray

    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}
