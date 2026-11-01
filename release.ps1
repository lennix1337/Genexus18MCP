# GeneXus MCP - one-shot release script
# =========================================
#
# Why this exists: the npm publish workflow (.github/workflows/release.yml)
# expects a `publish.zip` asset attached to the GitHub Release. The Worker
# references Artech.* DLLs from the local primary SDK install, so building on
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
    [switch]$Minor,
    [switch]$Patch,
    [switch]$NoAutoCandidate,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [string]$LiveKbPath,
    [string]$LiveFixtureManifest,
    [switch]$RequireLive,
    [switch]$RequireBuildAll,
    [switch]$AllowDirty,
    # Prepare the exact release commit (bump, changelog, metadata) and stop before any
    # build, tag, push, or publication so CI can validate that SHA first.
    [switch]$PrepareOnly,
    # Issues explicitly completed by this release. Each issue must carry
    # fixed-pending-release, receives the release URL, and is then closed;
    # omitted issues are never touched.
    [int[]]$CloseIssues,
    # Optional text file with one issue number per line (commas are accepted).
    # Its entries are combined with -CloseIssues and deduplicated.
    [string]$CloseIssuesFile,
    # Include every open issue carrying fixed-pending-release. The generated
    # generated release-issues.txt is operational and intentionally ignored;
    # release-issues.json is the immutable committed snapshot.
    [switch]$SkipLabeledIssues,
    # Optional GitHub milestone number used with the automatic label filter.
    [int]$ReleaseMilestone,
    # Optional machine-readable progress file. Defaults to %TEMP% and is safe
    # to poll from another shell while a detached release is running.
    [string]$StatusFile,
    # Relaunch this script in a hidden background pwsh and return immediately.
    # A full release takes minutes (build + tests + zip); a shell/tool with a
    # short command timeout (e.g. 30 s) would kill the foreground run mid-way,
    # leaving a half-bumped tree. With -Detach stdout/stderr go to
    # %TEMP%\gxmcp-release*.log; poll the log, don't wait on the call.
    [switch]$Detach,
    [Parameter(DontShow = $true)][string]$DetachedStdoutLog,
    [Parameter(DontShow = $true)][string]$DetachedStderrLog
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
# Native `gh` output is UTF-8. Set both encodings so PowerShell does not
# decode issue titles with the OEM code page before JSON parsing.
$utf8 = [Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$root = $PSScriptRoot
. (Join-Path $root 'scripts\gx-version-catalog.ps1')
. (Join-Path $root 'scripts\release-contract.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $root
if ($Minor -and $Patch) {
    throw "Cannot specify both -Minor and -Patch."
}
if (($Minor -or $Patch) -and -not [string]::IsNullOrWhiteSpace($Version)) {
    throw "Cannot specify -Version together with -Minor or -Patch."
}
$versionWasImplicit = [string]::IsNullOrWhiteSpace($Version) -and -not $Minor -and -not $Patch
$statusPackagePath = Join-Path $root 'package.json'
if (-not (Test-Path -LiteralPath $statusPackagePath -PathType Leaf)) {
    throw "package.json not found at $statusPackagePath"
}
$currentPackageVersion = [string](Get-Content -LiteralPath $statusPackagePath -Raw | ConvertFrom-Json).version
if ($Minor -or $Patch) {
    $bumpType = if ($Minor) { 'minor' } else { 'patch' }
    $Version = Get-GxMcpNextSemVer -CurrentVersion $currentPackageVersion -Bump $bumpType
} elseif ($versionWasImplicit) {
    $Version = $currentPackageVersion
}
$Version = ([string]$Version).Trim().TrimStart('v')
$AutoCandidate = (-not $NoAutoCandidate) -and (-not $PrepareOnly)
$releaseDryRunBeforeIssueHelpers = $DryRun
$releaseCloseIssuesBeforeIssueHelpers = $CloseIssues
. (Join-Path $root 'scripts/release-issues.ps1') -DefineOnly
. (Join-Path $root 'scripts/release-candidate-ci.ps1')
$DryRun = $releaseDryRunBeforeIssueHelpers
$CloseIssues = $releaseCloseIssuesBeforeIssueHelpers
$statusToken = if ([string]::IsNullOrWhiteSpace($Version)) { 'pending' } else { $Version -replace '[^0-9A-Za-z.-]', '-' }
if ([string]::IsNullOrWhiteSpace($StatusFile)) {
    $StatusFile = Join-Path $env:TEMP ("gxmcp-release-status-$statusToken-$PID.json")
} else {
    $StatusFile = [IO.Path]::GetFullPath($StatusFile)
}
$statusState = [ordered]@{
    version = $Version
    tag = $null
    repository = 'lennix1337/Genexus18MCP'
    phase = 'starting'
    state = 'running'
    pid = $PID
    updatedAtUtc = [DateTime]::UtcNow.ToString('o')
    statusFile = $StatusFile
    stdoutLog = $DetachedStdoutLog
    stderrLog = $DetachedStderrLog
    releaseUrl = $null
    workflowRunId = $null
    publicationState = 'not-started'
    publicationEvidencePath = "$StatusFile.publication.json"
    publication = $null
    publicationError = $null
    tagCommit = $null
    npmVersion = $null
    artifactFingerprint = $null
    preflightSummaryPath = $null
    nextAction = 'inspect-status'
    exitCode = $null
    error = $null
    candidate = [ordered]@{
        branch = $null
        prNumber = $null
        prUrl = $null
    }
    issues = [ordered]@{
        discovered = @()
        validated = @()
        commented = @()
        closed = @()
        failed = @()
    }
}
$tag = $null
$releaseUrl = $null
$script:releaseIssueSnapshotReused = $false

$releaseIssuesPath = Join-Path $root 'release-issues.txt'
$releaseIssuesSnapshotPath = Join-Path $root 'release-issues.json'
function Get-LabeledReleaseIssues {
    if ($SkipLabeledIssues) { return @() }
    if ($ReleaseMilestone -lt 0) { Fail "ReleaseMilestone must be positive when supplied." }
    $milestoneQuery = if ($ReleaseMilestone -gt 0) { "&milestone=$ReleaseMilestone" } else { '' }
    $endpoint = "repos/{owner}/{repo}/issues?state=open&labels=fixed-pending-release&per_page=100$milestoneQuery"
    $numbers = @(gh api --paginate $endpoint --jq '.[] | select(.pull_request == null) | .number' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Fail "Could not list open issues with the fixed-pending-release label."
    }
    return @($numbers | ForEach-Object {
        $value = ([string]$_).Trim()
        if ($value -match '^\d+$') { [int]$value }
    } | Sort-Object -Unique)
}

function Get-ReleaseIssueNumbers {
    $values = New-Object System.Collections.Generic.List[int]
    foreach ($issue in @($CloseIssues)) {
        if ($issue -le 0) { Fail "Issue number must be positive: $issue" }
        $values.Add($issue)
    }
    if ($CloseIssuesFile) {
        if (-not (Test-Path -LiteralPath $CloseIssuesFile -PathType Leaf)) {
            Fail "CloseIssuesFile not found: $CloseIssuesFile"
        }
        $content = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $CloseIssuesFile))
        foreach ($token in ($content -split '[,\s]+')) {
            if ([string]::IsNullOrWhiteSpace($token)) { continue }
            if ($token -notmatch '^#?\d+$') { Fail "Invalid issue number in CloseIssuesFile: $token" }
            $number = [int]($token.TrimStart('#'))
            if ($number -le 0) { Fail "Issue number must be positive: $number" }
            $values.Add($number)
        }
    }
    return @($values | Select-Object -Unique)
}

function Get-ReleaseFileSha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Ensure-ReleaseVsixArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$VsixPath,
        [switch]$AllowMissing
    )

    $publishVsix = Join-Path $PublishDirectory 'nexus-ide.vsix'
    $rootHash = Get-ReleaseFileSha256 -Path $VsixPath
    $publishHash = Get-ReleaseFileSha256 -Path $publishVsix
    if ($null -eq $rootHash -and $null -eq $publishHash) {
        if ($AllowMissing) { return }
        throw "Nexus VSIX is missing from both the release root and publish directory: $VsixPath"
    }
    if ($null -ne $rootHash -and $null -ne $publishHash -and $rootHash -ne $publishHash) {
        throw "Nexus VSIX differs between the release root and publish directory; refusing -SkipBuild reuse."
    }
    if ($null -ne $rootHash -and $null -eq $publishHash) {
        Copy-Item -LiteralPath $VsixPath -Destination $publishVsix -Force
    } elseif ($null -eq $rootHash -and $null -ne $publishHash) {
        Copy-Item -LiteralPath $publishVsix -Destination $VsixPath -Force
    }

    $rootHash = Get-ReleaseFileSha256 -Path $VsixPath
    $publishHash = Get-ReleaseFileSha256 -Path $publishVsix
    if ($null -eq $rootHash -or $null -eq $publishHash -or $rootHash -ne $publishHash) {
        throw "Nexus VSIX could not be reconciled between $VsixPath and $publishVsix."
    }
}

function Get-ReleasePreflightInputs {
    param([AllowNull()][string]$ArtifactFingerprint)

    return Resolve-GxMcpReleasePreflightInputs `
        -Root $root `
        -Catalog $gxCatalog `
        -GxPath $preflightGxPath `
        -Version $Version `
        -SourceCommit $releaseSourceCommit `
        -LiveKbPath $LiveKbPath `
        -LiveFixtureManifest $LiveFixtureManifest `
        -RequireLive:$RequireLive `
        -RequireBuildAll:$RequireBuildAll `
        -ArtifactFingerprint $ArtifactFingerprint
}

function Test-ReleasePublicationAlreadyVerified {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [string]$RepositoryRoot
    )

    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = $root }
    try {
        $peeled = @(& git -C $RepositoryRoot ls-remote --tags origin "refs/tags/$Tag^{}" 2>$null)
        if ($LASTEXITCODE -ne 0 -or $peeled.Count -eq 0) { return $false }
        $commit = (($peeled[0].ToString().Trim()) -split '\s+')[0]
        if (-not (Test-GxMcpReleaseCommitId $commit)) { return $false }
        $releaseRaw = @(& gh release view $Tag --json tagName,isDraft,assets,url,publishedAt 2>$null)
        if ($LASTEXITCODE -ne 0 -or $releaseRaw.Count -eq 0) { return $false }
        $release = (($releaseRaw -join [Environment]::NewLine) | ConvertFrom-Json)
        if ($release.tagName -cne $Tag -or $release.isDraft) { return $false }
        $allowLegacy = Test-GxMcpReleaseLegacyVersion $PackageVersion
        $assetState = Get-GxMcpReleaseAssetVerification -Assets @($release.assets) -RepositoryRoot $RepositoryRoot -Version $PackageVersion -AllowLegacy:$allowLegacy
        if (-not $assetState.isValid) {
            Write-Verbose "Release assets do not match the local transaction: $($assetState.error)"
            return $false
        }
        $runs = @(& gh run list --workflow release.yml --limit 50 --json databaseId,status,conclusion,headSha,headBranch,event,createdAt 2>$null | ConvertFrom-Json)
        $successfulRun = @($runs | Where-Object {
            (Test-GxMcpReleaseWorkflowRun -Run $_ -ExpectedCommit $commit -Tag $Tag `
                -MinimumCreatedAtUtc $assetState.latestUpdatedAtUtc -AllowDispatch) -and
            [string]$_.status -eq 'completed' -and [string]$_.conclusion -eq 'success'
        } | Sort-Object { [datetime]$_.createdAt } -Descending | Select-Object -First 1)
        if ($successfulRun.Count -eq 0) { return $false }
        $npmRaw = @(& npm view "genexus-mcp@$PackageVersion" version gitHead --json --fetch-retries=0 --fetch-timeout=5000 2>$null)
        if ($LASTEXITCODE -ne 0 -or $npmRaw.Count -eq 0) { return $false }
        $npmRecord = (($npmRaw -join [Environment]::NewLine) | ConvertFrom-Json)
        if ($npmRecord -is [array]) { $npmRecord = @($npmRecord)[0] }
        if ($null -eq $npmRecord -or [string]$npmRecord.version -cne $PackageVersion) { return $false }
        if (-not $allowLegacy -and [string]$npmRecord.gitHead -cne $commit) { return $false }
        return $true
    } catch {
        Write-Verbose "Could not prove that $Tag is already published: $($_.Exception.Message)"
        return $false
    }
}

function Get-ReleaseIssueSnapshot {
    $snapshotMatched = $false
    if (-not $DryRun -and (Test-Path -LiteralPath $releaseIssuesSnapshotPath -PathType Leaf)) {
        try {
            $existing = Get-Content -LiteralPath $releaseIssuesSnapshotPath -Raw | ConvertFrom-Json
            $snapshotMatched = $existing.schema -eq 'gxmcp-release-issues/1' -and
                $existing.version -eq $Version -and $existing.tag -eq $tag
            if ($remoteTag -and -not $snapshotMatched) {
                throw 'existing snapshot header does not match the resumed release tag/version.'
            }
            if ($snapshotMatched) {
                $snapshotShape = Test-GxMcpReleaseIssueSnapshotShape -Snapshot $existing
                if (-not $snapshotShape.Valid) { throw "Snapshot shape is invalid: $($snapshotShape.Error)" }
                $snapshotIssues = @($existing.issues)
                $expectedReleaseUrl = if ($releaseUrl) { [string]$releaseUrl } else { "https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag" }
                $activeRecords = New-Object System.Collections.Generic.List[object]
                foreach ($record in $snapshotIssues) {
                    $issueNumber = [int]$record.number
                    $current = Get-ReleaseIssueData -IssueNumber $issueNumber
                    $currentState = ([string]$current.state).Trim().ToLowerInvariant()
                    if ($currentState -eq 'closed') {
                        if (-not (Test-ReleaseIssuePublicationComment -IssueData $current -ReleaseUrl $expectedReleaseUrl)) {
                            throw "Issue #$issueNumber is already closed without this release's publication comment; refusing to reuse the snapshot."
                        }
                        Warn "Issue #$issueNumber is already closed with this release's publication comment; excluding it from the resumed release batch."
                        continue
                    }
                    Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber $issueNumber -IssueData $current
                    $activeRecords.Add($record)
                }
                $script:releaseIssueSnapshotReused = $true
                return $activeRecords.ToArray()
            }
        } catch {
            if ($snapshotMatched -or $remoteTag) {
                throw "Could not validate the existing release-issues.json snapshot: $($_.Exception.Message)"
            }
            Warn "Ignoring invalid release-issues.json; rebuilding the snapshot."
        }
    }
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($issue in @($CloseIssues | Select-Object -Unique)) {
        $raw = @(gh api "repos/{owner}/{repo}/issues/$issue" --jq '{number,title,url:.html_url,state,labels,milestone}' 2>$null)
        if ($LASTEXITCODE -ne 0 -or $raw.Count -eq 0) {
            Fail "Could not read issue #$issue before the release."
        }
        $record = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
        $recordState = ([string]$record.state).ToUpperInvariant()
        if ($recordState -ne 'OPEN') {
            Fail "Issue #$issue is not open at release preparation time."
        }
        Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber $issue -IssueData $record
        $records.Add([ordered]@{
            number = [int]$record.number
            title = [string]$record.title
            url = [string]$record.url
            state = [string]$record.state
            labels = @($record.labels | ForEach-Object { [string]$_.name })
            milestone = if ($record.milestone) { [string]$record.milestone.title } else { $null }
        })
    }
    return $records.ToArray()
}

function Assert-ChangelogIssueReferences {
    param(
        [int[]]$IssueNumbers,
        [string]$ChangelogPath,
        [string]$Version
    )

    $numbers = @($IssueNumbers | Where-Object { $_ -gt 0 } | Select-Object -Unique)
    if ($numbers.Count -eq 0) { return }
    if (-not $ChangelogPath) { $ChangelogPath = Join-Path $root 'CHANGELOG.md' }
    if (-not (Test-Path -LiteralPath $changelogPath -PathType Leaf)) {
        throw "CHANGELOG.md is missing; cannot verify release issue references."
    }
    $changelog = [IO.File]::ReadAllText($changelogPath)
    $unreleased = [regex]::Match(
        $changelog,
        '(?ms)^## Unreleased\s*(?<body>.*?)(?=^## v|\z)')
    if (-not $unreleased.Success) {
        throw 'CHANGELOG.md must contain a readable ## Unreleased section before issue-reference validation.'
    }

    # A rerun after a failed mid-release attempt finds the issue links already
    # promoted under the version heading; accept that state as long as every
    # tracked issue is referenced there.
    $promotedBody = ''
    if ($Version -and $changelog -match "(?m)^##[ \t]+v$([Regex]::Escape($Version))(?=[ \t]|$)") {
        $promoted = [regex]::Match(
            $changelog,
            "(?ms)^## v$([Regex]::Escape($Version))\s*(?<body>.*?)(?=^## v|\z)")
        if ($promoted.Success) { $promotedBody = $promoted.Groups['body'].Value }
    }

    $missing = New-Object System.Collections.Generic.List[int]
    foreach ($number in $numbers) {
        $url = "https://github.com/lennix1337/Genexus18MCP/issues/$number"
        if ($unreleased.Groups['body'].Value.IndexOf($url, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $promotedBody.IndexOf($url, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            [void]$missing.Add([int]$number)
        }
    }
    if ($missing.Count -gt 0) {
        throw "CHANGELOG.md ## Unreleased is missing canonical issue links for: $($missing -join ', ')."
    }
}

# Issue #328: `## Unreleased` with no subsection under it is the state a plain
# heading insertion leaves behind. It passes the "the heading disappeared" guard,
# and the natural next edit - append a bullet to the first visible `### Fixed` -
# files new work under the release that was just published. These two helpers
# make the anchor explicit and keep the "substantive notes" guard honest about
# what counts as an entry.
function Get-ReleaseChangelogUnreleasedAnchor {
    # An HTML comment plus the standard empty subsections: the comment names the
    # contract for whoever edits next, and the headings give the bullet a home
    # without inventing content.
    return @(
        '<!-- Next release: put new entries under a `### Added` / `### Changed` / `### Fixed` / `### Internal`',
        '     subsection of THIS `## Unreleased` section, not under a published `## v...` heading. -->',
        '',
        '### Added',
        '',
        '### Changed',
        '',
        '### Fixed',
        '',
        '### Internal'
    ) -join "`r`n"
}

function Test-ReleaseChangelogSubstantiveBody {
    param([AllowNull()][string]$Body)

    # Substantive means: a line that is not blank, not part of an HTML comment,
    # and not a bare heading. The anchor above is deliberately all three, so an
    # untouched `## Unreleased` still fails the guard and cannot ship an empty
    # release. Comments are stripped as blocks because the anchor wraps across
    # two lines.
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    $withoutComments = [regex]::Replace($Body, '(?s)<!--.*?-->', '')
    foreach ($line in ($withoutComments -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed -match '^#{1,6}\s') { continue }
        return $true
    }
    return $false
}

# Returns a result object: status is 'promoted', 'already-present' or
# 'no-unreleased'. Kept as a function so the promotion is unit-testable against
# a fixture instead of only through a real release.
function Update-ReleaseChangelogPromotion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Date
    )

    $result = [ordered]@{ status = 'no-unreleased'; content = $null; anchored = $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.status = 'missing-file'
        return [pscustomobject]$result
    }
    $raw = [System.IO.File]::ReadAllText($Path)
    if ($raw -match "(?m)^##[ \t]+v$([Regex]::Escape($Version))(?=[ \t]|$)") {
        $result.status = 'already-present'
        $result.content = $raw
        return [pscustomobject]$result
    }
    if ($raw -notmatch '(?m)^##[ \t]+Unreleased[ \t]*\r?\n') { return [pscustomobject]$result }

    $promoted = [Regex]::Replace(
        $raw,
        '(?m)^##[ \t]+Unreleased[ \t]*\r?\n',
        "## Unreleased`r`n`r`n## v$Version - $Date`r`n`r`n")
    # The promoted body is now the version's; `## Unreleased` is bare. Insert the
    # anchor right under the new Unreleased heading, before the version heading.
    $anchor = Get-ReleaseChangelogUnreleasedAnchor
    $anchored = [Regex]::Replace(
        $promoted,
        '(?m)^##[ \t]+Unreleased[ \t]*\r?\n',
        "## Unreleased`r`n`r`n" + $anchor + "`r`n", 1)
    [System.IO.File]::WriteAllText($Path, $anchored, [System.Text.UTF8Encoding]::new($false))
    $result.status = 'promoted'
    $result.content = $anchored
    $result.anchored = $true
    return [pscustomobject]$result
}

function Write-ReleaseIssueSnapshot {
    param([object[]]$Records)
    $snapshot = [ordered]@{
        schema = 'gxmcp-release-issues/1'
        version = $Version
        tag = $tag
        collectedAtUtc = [DateTime]::UtcNow.ToString('o')
        issues = @($Records)
    }
    if ($DryRun) {
        Warn "[DRY-RUN] would snapshot $($Records.Count) release issue(s) with titles in release-issues.json."
        return
    }
    if ($script:releaseIssueSnapshotReused) {
        Ok "Reusing immutable release-issues.json snapshot with $($Records.Count) issue(s)."
        return
    }
    [IO.File]::WriteAllText($releaseIssuesSnapshotPath, ($snapshot | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Ok "release-issues.json snapshot written with $($Records.Count) issue(s)."
}

function Write-ReleaseStatus {
    param(
        [string]$Phase,
        [ValidateSet('running', 'succeeded', 'failed')][string]$State = 'running',
        [string]$ReleaseUrl,
        [string]$WorkflowRunId,
        [ValidateSet('not-started', 'pending', 'verified', 'failed')][string]$PublicationState,
        [string]$PublicationEvidencePath,
        [string]$PublicationError,
        [string]$TagCommit,
        [string]$NpmVersion,
        [string]$ArtifactFingerprint,
        [string]$PreflightSummaryPath,
        [string]$NextAction,
        [int]$ExitCode,
        [string]$ErrorMessage,
        [int]$ProcessId
    )
    try {
        if ($null -eq $statusState.publication -and (Test-Path -LiteralPath $StatusFile -PathType Leaf)) {
            try {
                $persisted = Get-Content -LiteralPath $StatusFile -Raw | ConvertFrom-Json
                if ($persisted.PSObject.Properties.Name -contains 'publication' -and $null -ne $persisted.publication) {
                    $statusState.publication = $persisted.publication
                }
            } catch { }
        }
        if ($PSBoundParameters.ContainsKey('Phase')) { $statusState.phase = $Phase }
        if ($PSBoundParameters.ContainsKey('State')) { $statusState.state = $State }
        if ($PSBoundParameters.ContainsKey('ReleaseUrl')) { $statusState.releaseUrl = $ReleaseUrl }
        if ($PSBoundParameters.ContainsKey('WorkflowRunId')) { $statusState.workflowRunId = $WorkflowRunId }
        if ($PSBoundParameters.ContainsKey('PublicationState')) { $statusState.publicationState = $PublicationState }
        if ($PSBoundParameters.ContainsKey('PublicationEvidencePath')) { $statusState.publicationEvidencePath = $PublicationEvidencePath }
        if ($PSBoundParameters.ContainsKey('PublicationError')) { $statusState.publicationError = $PublicationError }
        if ($PSBoundParameters.ContainsKey('TagCommit')) { $statusState.tagCommit = $TagCommit }
        if ($PSBoundParameters.ContainsKey('NpmVersion')) { $statusState.npmVersion = $NpmVersion }
        if ($PSBoundParameters.ContainsKey('ArtifactFingerprint')) { $statusState.artifactFingerprint = $ArtifactFingerprint }
        if ($PSBoundParameters.ContainsKey('PreflightSummaryPath')) { $statusState.preflightSummaryPath = $PreflightSummaryPath }
        if ($PSBoundParameters.ContainsKey('NextAction')) { $statusState.nextAction = $NextAction }
        if ($PSBoundParameters.ContainsKey('ExitCode')) { $statusState.exitCode = $ExitCode }
        if ($PSBoundParameters.ContainsKey('ErrorMessage')) { $statusState.error = Protect-GxMcpReleaseMessage $ErrorMessage }
        if ($PSBoundParameters.ContainsKey('ProcessId')) { $statusState.pid = $ProcessId }
        if ($null -ne $statusState.error) { $statusState.error = Protect-GxMcpReleaseMessage $statusState.error }
        if ($null -ne $statusState.publicationError) { $statusState.publicationError = Protect-GxMcpReleaseMessage $statusState.publicationError }
        if ($null -ne $statusState.publication) { $statusState.publication = Get-GxMcpSafeReleaseValue $statusState.publication }
        $statusState.version = $Version
        $statusState.tag = $tag
        $statusState.updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        $parent = Split-Path -Parent $StatusFile
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $tmp = "$StatusFile.$([guid]::NewGuid().ToString('N')).tmp"
        [IO.File]::WriteAllText($tmp, (($statusState | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tmp -Destination $StatusFile -Force
    } catch {
        # Status reporting must never hide the actual release failure.
        Write-Verbose "Could not update release status: $($_.Exception.Message)"
    }
}

Write-ReleaseStatus -Phase 'starting' -State 'running'
Write-Host "Release status: $StatusFile" -ForegroundColor DarkGray

function Step([string]$msg) { Write-Host "`n>>> $msg" -ForegroundColor Cyan; Write-ReleaseStatus -Phase $msg -State 'running' }
function Ok  ([string]$msg) { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Warn([string]$msg) { Write-Host "    [!]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg) {
    $safeMessage = Protect-GxMcpReleaseMessage $msg
    Write-Host "    [ERR] $safeMessage" -ForegroundColor Red
    Write-ReleaseStatus -Phase 'failed' -State 'failed' -ExitCode 1 -ErrorMessage $safeMessage
    exit 1
}

trap {
    $message = Protect-GxMcpReleaseMessage $_.Exception.Message
    if (Get-Command Write-ReleaseStatus -ErrorAction SilentlyContinue) {
        Write-ReleaseStatus -Phase 'failed' -State 'failed' -ExitCode 1 -ErrorMessage $message
    }
    throw $message
}

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

function Close-ReleaseIssues {
    param([Parameter(Mandatory = $true)][string]$ReleaseUrl)
    $issues = @($CloseIssues | Select-Object -Unique)
    if (-not $DryRun) {
        foreach ($issue in $issues) {
            if ($issue -le 0) { Fail "Issue number must be positive: $issue" }
            $record = Get-ReleaseIssueData -IssueNumber $issue
            try { Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber $issue -IssueData $record }
            catch { Fail $_.Exception.Message }
        }
        Ok "Pre-validated $($issues.Count) release issue(s); beginning closure batch."
        $statusState.issues.validated = @($issues)
        Write-ReleaseStatus -Phase 'release-issues-prevalidated' -State 'running'
    }
    # Keep issue mutations serial: comment -> close -> verify is a correctness
    # boundary, and parallel GitHub mutations would make retries harder to reason about.
    foreach ($issue in $issues) {
        if ($issue -le 0) { Fail "Issue number must be positive: $issue" }
        if ($DryRun) {
            Warn "[DRY-RUN] would comment release URL and close issue #$issue."
            continue
        }

        # Re-read immediately before each mutation; labels/state can change after the batch preflight.
        $current = Get-ReleaseIssueData -IssueNumber $issue
        try { Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber $issue -IssueData $current }
        catch { Fail $_.Exception.Message }
        Invoke-Cmd 'gh' @('issue', 'comment', [string]$issue, '--body', "Released in $ReleaseUrl")
        $commented = Get-ReleaseIssueData -IssueNumber $issue
        if (-not (Test-ReleaseIssuePublicationComment -IssueData $commented -ReleaseUrl $ReleaseUrl)) {
            Fail "Issue #$issue did not expose the exact release publication comment after posting."
        }
        $statusState.issues.commented = @($statusState.issues.commented + $issue | Select-Object -Unique)
        Write-ReleaseStatus -Phase "issue-$issue-commented" -State 'running'
        $current = Get-ReleaseIssueData -IssueNumber $issue
        try { Assert-ReleaseIssueAction -Action 'CloseAfterRelease' -IssueNumber $issue -IssueData $current }
        catch { Fail "Issue #$issue changed before closure: $($_.Exception.Message)" }
        Invoke-Cmd 'gh' @('issue', 'close', [string]$issue, '--reason', 'completed')
        $verifiedState = ([string](Get-ReleaseIssueData -IssueNumber $issue).state).Trim().ToLowerInvariant()
        if ($verifiedState -ne 'closed') {
            Fail "Issue #$issue was not verified as closed after the release comment."
        }
        $statusState.issues.closed = @($statusState.issues.closed + $issue | Select-Object -Unique)
        Write-ReleaseStatus -Phase "issue-$issue-closed" -State 'running'
        Ok "Issue #$issue closed with release link."
    }
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
    $forwarded = Get-ForwardedArgs -BoundParams $PSBoundParameters -Exclude @('Detach', 'Minor', 'Patch')
    if ($forwarded -notcontains '-Version') { $forwarded += @('-Version', $Version) }
    if ($forwarded -notcontains '-StatusFile') { $forwarded += @('-StatusFile', $StatusFile) }
    $forwarded += @('-DetachedStdoutLog', $stdoutLog, '-DetachedStderrLog', $stderrLog)
    $argItems = @('-NoProfile', '-File', $PSCommandPath) + $forwarded
    $child = Start-Process -FilePath $pwshExe -ArgumentList $argItems `
        -WorkingDirectory $root `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -WindowStyle Hidden -PassThru
    Write-Host ""
    Write-Host "Detached release started (PID $($child.Id))." -ForegroundColor Cyan
    Write-Host "  stdout log: $stdoutLog"
    Write-Host "  stderr log: $stderrLog"
    Write-Host "  watch:      Get-Content -Wait $stdoutLog"
    Write-Host "  status:     pwsh -NoProfile -File scripts/release-status.ps1 -Path $StatusFile"
    Write-Host "  The parent shell may close; the release keeps running."
    $statusState.stdoutLog = $stdoutLog
    $statusState.stderrLog = $stderrLog
    Write-ReleaseStatus -Phase 'detached' -State 'running' -ProcessId $child.Id
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

$labeledIssues = @(Get-LabeledReleaseIssues)
if (-not $SkipLabeledIssues) {
    if ($DryRun) {
        Warn "[DRY-RUN] would populate release-issues.txt with $($labeledIssues.Count) open fixed-pending-release issue(s)."
    } else {
        $content = if ($labeledIssues.Count -gt 0) { (($labeledIssues | ForEach-Object { "#$($_)" }) -join [Environment]::NewLine) + [Environment]::NewLine } else { '' }
        [IO.File]::WriteAllText($releaseIssuesPath, $content, [Text.UTF8Encoding]::new($false))
        Ok "release-issues.txt populated from fixed-pending-release ($($labeledIssues.Count) issue(s))."
    }
}
$explicitIssues = if ($null -eq $CloseIssues) { @() } else { @($CloseIssues) }
$CloseIssues = @(
    foreach ($issueNumber in @($explicitIssues)) { $issueNumber }
    foreach ($issueNumber in @($labeledIssues)) { $issueNumber }
)
$CloseIssues = @(Get-ReleaseIssueNumbers)
$statusState.issues.discovered = @($CloseIssues)
Write-ReleaseStatus -Phase 'release-issues-collected' -State 'running'

function Invoke-Cmd {
    # NOTE: do NOT name a parameter `$Args` - it collides with PowerShell's
    # automatic variable inside the function body, and `@Args` then splats
    # the EMPTY automatic instead of the caller's array. Observed releases
    # v2.6.9 with the bug: `git`, `dotnet`, `pwsh` all invoked with no args
    # (release.ps1 would die at "Tagging $tag" with git printing its help).
    # Use `$Arguments` (or any non-automatic name) instead.
    param([string]$Exe, [string[]]$Arguments, [switch]$IgnoreExit)
    $display = "$Exe $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "    [DRY-RUN] would run: $display" -ForegroundColor DarkGray
        return
    }
    Write-Host "    $ $display" -ForegroundColor DarkGray
    & $Exe @Arguments
    if (-not $IgnoreExit -and $LASTEXITCODE -ne 0) {
        Fail "Command failed (exit $LASTEXITCODE): $display"
    }
}

function Set-LockfileVersion {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$TargetVersion)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    if ($DryRun) { Ok "$(Split-Path -Leaf $Path) -> $TargetVersion"; return }
    $raw = [IO.File]::ReadAllText($Path)
    # npm lockfiles repeat the package version at the document root and at
    # packages[""]. Process only those two object levels so dependency
    # versions elsewhere in the file remain untouched.
    $newline = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = $raw -split "\r?\n"
    $topDone = $false
    $rootPackage = $false
    $rootDone = $false
    $rootIndent = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if (-not $topDone -and $line -match '^\s*"version"\s*:\s*"[^"]+"') {
            $lines[$index] = [Regex]::Replace($line, '("version"\s*:\s*)"[^"]+"', ('$1"' + $TargetVersion + '"'), 1)
            $topDone = $true
            continue
        }
        if (-not $rootPackage -and $line -match '^(?<indent>\s*)""\s*:\s*\{\s*$') {
            $rootPackage = $true
            $rootIndent = $Matches.indent.Length
            continue
        }
        if ($rootPackage -and -not $rootDone -and $line -match '^\s*"version"\s*:\s*"[^"]+"') {
            $lines[$index] = [Regex]::Replace($line, '("version"\s*:\s*)"[^"]+"', ('$1"' + $TargetVersion + '"'), 1)
            $rootDone = $true
            continue
        }
        if ($rootPackage -and -not $rootDone -and $line -match '^\s*}\s*,?\s*$' -and (($line -replace '^\s*', '').Length -le 2 -or $line.Length - $line.TrimStart().Length -le $rootIndent)) {
            $rootDone = $true
        }
    }
    if (-not $topDone -or -not $rootDone) { throw "Could not locate both npm lockfile version fields in $Path." }
    $updated = $lines -join $newline
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
    Ok "$(Split-Path -Leaf $Path) -> $TargetVersion"
}

# -- 1. Resolve version + sanity-check tree --------------------------------
function Test-StrictSemVer([string]$Value) {
    return $Value -match '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
}

function Test-ReleaseBaseAlignment {
    param(
        [string]$LocalHead,
        [string]$RemoteMainHead,
        [string]$LocalParent,
        [string]$LocalSubject,
        [string]$Tag
    )

    if ($LocalHead -ceq $RemoteMainHead) { return $true }
    return -not [string]::IsNullOrWhiteSpace($LocalParent) -and
        $LocalParent -ceq $RemoteMainHead -and
        $LocalSubject -ceq "release: $Tag"
}

# Reads the live origin/main head and fails unless local HEAD is that head or the
# single unpublished `release: <tag>` commit directly on top of it. Used both when a
# release starts and again immediately before tagging, because the preflight and
# build between the two take minutes during which main can advance.
function Assert-ReleaseBaseFresh {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $remoteMainOutput = @(git ls-remote --heads origin 'refs/heads/main' 2>$null)
    $remoteMainExitCode = $LASTEXITCODE
    if ($remoteMainExitCode -ne 0 -or $remoteMainOutput.Count -ne 1) {
        Fail "Could not read the current origin/main head before preparing the release."
    }
    $remoteMainHead = (($remoteMainOutput[0].ToString().Trim()) -split '\s+')[0]
    if ($remoteMainHead -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
        Fail "origin/main returned an invalid commit id before preparing the release."
    }

    $localHead = (& git rev-parse HEAD 2>$null).Trim()
    $localHeadExitCode = $LASTEXITCODE
    if ($localHeadExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($localHead)) {
        Fail "Could not read local HEAD before preparing the release."
    }

    $localParent = $null
    $localSubject = $null
    $parentExitCode = 0
    $subjectExitCode = 0
    if ($localHead -cne $remoteMainHead) {
        $localParent = (& git rev-parse 'HEAD^' 2>$null).Trim()
        $parentExitCode = $LASTEXITCODE
        $localSubject = (& git log -1 --format=%s 2>$null).Trim()
        $subjectExitCode = $LASTEXITCODE
    }

    if ($localHead -cne $remoteMainHead -and ($parentExitCode -ne 0 -or $subjectExitCode -ne 0)) {
        Fail "Could not verify the pending release commit against origin/main."
    }
    $baseAlignment = @{
        LocalHead = $localHead
        RemoteMainHead = $remoteMainHead
        LocalParent = $localParent
        LocalSubject = $localSubject
        Tag = $Tag
    }
    if (-not (Test-ReleaseBaseAlignment @baseAlignment)) {
        Fail "Local main is stale or diverged from origin/main (local=$localHead, remote=$remoteMainHead). Fetch and fast-forward main before preparing a new release."
    }
    return [pscustomobject]@{ RemoteMainHead = $remoteMainHead; LocalHead = $localHead; AtRemoteHead = ($localHead -ceq $remoteMainHead) }
}

# Verifies that every required status check is green on the exact release source
# commit. CI runs on a candidate branch (or PR) carrying that SHA before this script
# moves main or the tag; only main, the tag, and publication are gated here, so
# pushing the candidate branch needs no CI first. No bypass: a missing, pending, or
# failing required check fails the release before anything is tagged or pushed.
# Unreadable branch rules or check data fail closed instead of reading as absent, and
# the `test` check from GitHub Actions is required even when protection exposes none.
function Assert-ReleaseRequiredChecksGreen {
    param([Parameter(Mandatory = $true)][string]$Commit)

    $report = Get-GxMcpReleaseRequiredChecksReport -Commit $Commit
    if (-not $report.Valid) { Fail $report.Error }
    Ok "Required checks ($($report.Required -join ', ')) are green on $Commit."
}

# Last gate before the release becomes visible. Re-reads everything that can go stale
# while the preflight and packaging run: HEAD must still be the committed source the
# manifest binds to, origin/main must still be its base, the passed preflight
# certificate must still describe this commit and these exact artifacts, and required
# CI must be green on this exact commit.
function Assert-ReleasePrePublicationFresh {
    param(
        [Parameter(Mandatory = $true)][string]$SourceCommit,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$SummaryPath
    )

    $head = (& git rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) {
        Fail "HEAD ($head) is no longer the release source commit $SourceCommit; rerun the release so the manifest and preflight bind to the new commit."
    }
    [void](Assert-ReleaseBaseFresh -Tag $Tag)
    $summary = $null
    if (Test-Path -LiteralPath $SummaryPath -PathType Leaf) {
        try { $summary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json } catch { $summary = $null }
    }
    $fingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:$allowLegacyAssets
    $certificateValid = $null -ne $summary -and
        (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected (Get-ReleasePreflightInputs -ArtifactFingerprint $fingerprint))
    if (-not $certificateValid) {
        Fail "The passed preflight certificate does not match commit $SourceCommit and the current artifacts ($SummaryPath); rerun the complete preflight instead of publishing from an older snapshot."
    }
    Assert-ReleaseRequiredChecksGreen -Commit $SourceCommit
}

Step "Resolving version"
$pkgPath = Join-Path $root 'package.json'
if (-not (Test-Path $pkgPath)) { Fail "package.json not found at $pkgPath" }
$pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
$currentVersion = $pkg.version

if ($versionWasImplicit) {
    Warn "No -Version passed; using package.json: $Version"
}
if (-not (Test-StrictSemVer $Version)) {
    Fail "Version '$Version' is not strict semver (X.Y.Z[-prerelease][+build])."
}
$tag = "v$Version"
$allowLegacyAssets = Test-GxMcpReleaseLegacyVersion $Version
$numericVersion = ([regex]::Match($Version, '^\d+\.\d+\.\d+')).Value
Ok "Target version: $Version  (tag: $tag)"

$branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
if ($branch -ne 'main') {
    if ($DryRun) { Warn "[DRY-RUN] current branch is '$branch'; a real release requires main." }
    else { Fail "Releases must be cut from the main branch (current: '$branch')." }
}

Step "Checking release base freshness"
$remoteTag = @(git ls-remote --tags origin "refs/tags/$tag" 2>$null)
$remoteTagExitCode = $LASTEXITCODE
if ($remoteTagExitCode -ne 0) {
    Fail "Could not check remote tag $tag before preparing the release."
}

if ($branch -eq 'main' -and -not $remoteTag) {
    $baseState = Assert-ReleaseBaseFresh -Tag $tag
    if ($baseState.AtRemoteHead) {
        Ok "Local main matches the current origin/main head."
    } else {
        Ok "Pending release commit is directly based on the current origin/main head; resuming is safe."
    }
} elseif ($remoteTag) {
    Ok "Remote tag $tag exists; the release will resume from its pinned source commit."
} elseif ($DryRun) {
    Warn "[DRY-RUN] origin/main freshness check skipped because the current branch is not main."
}

Step "Snapshotting release issues"
$releaseIssueRecords = @(Get-ReleaseIssueSnapshot)
Assert-ChangelogIssueReferences -IssueNumbers @($releaseIssueRecords | ForEach-Object { [int]$_.number }) -Version $Version
Write-ReleaseIssueSnapshot -Records $releaseIssueRecords
$CloseIssues = @($releaseIssueRecords | ForEach-Object { [int]$_.number } | Select-Object -Unique)
$statusState.issues.validated = @($releaseIssueRecords | ForEach-Object { [int]$_.number })
Write-ReleaseStatus -Phase 'release-issues-validated' -State 'running'

# Keep release-facing version, SDK-major, and documentation metadata in sync
# before the dirty-tree gate. A clean release can therefore repair generated
# metadata and include it in the release commit automatically.
Step "Synchronizing release-facing metadata"
$syncArguments = @(
    (Join-Path $root 'scripts\sync-release-metadata.py'),
    '--root', $root,
    '--version', $Version
)
if ($DryRun) {
    $syncArguments += '--check'
    $syncDisplay = "python $($syncArguments -join ' ')"
    Write-Host "    `$ $syncDisplay" -ForegroundColor DarkGray
    & python @syncArguments
    $syncExitCode = $LASTEXITCODE
    if ($syncExitCode -eq 1) {
        Warn '[DRY-RUN] release-facing metadata is out of sync and would be regenerated.'
    } elseif ($syncExitCode -ne 0) {
        Fail "Release metadata check failed (exit $syncExitCode): $syncDisplay"
    } else {
        Ok '[DRY-RUN] release-facing metadata is already synchronized.'
    }
} else {
    $syncArguments += '--write'
    Invoke-Cmd 'python' $syncArguments
    Ok 'Release-facing metadata synchronized.'
}

Step "Checking git working tree"
$status = git status --porcelain
# Files release.ps1 itself bumps/regenerates. A dirty tree consisting SOLELY of
# these is the signature of a previous release run that died mid-way (e.g. a
# failed test pass after the version bump) - that's resumable, not an error.
$releaseManagedPaths = @(
    'package.json',
    'package-lock.json',
    'CHANGELOG.md',
    'config/gx-versions.json',
    'server.json',
    'config.sample.json',
    'README.md',
    'AGENTS.md',
    'docs/generated/supported-versions.md',
    'release-issues.json',
    'src/GxMcp.Gateway/GxMcp.Gateway.csproj',
    'src/GxMcp.Worker/GxMcp.Worker.csproj',
    'src/nexus-ide/package.json',
    'src/nexus-ide/package-lock.json'
)
if ($status -and -not $AllowDirty) {
    $dirtyPaths = @($status | ForEach-Object { $_.Substring(3).Trim().Trim('"') -replace '\\', '/' })
    $nonBump = @($dirtyPaths | Where-Object { $releaseManagedPaths -notcontains $_ })
    if ($nonBump.Count -eq 0) {
        Warn "Working tree is dirty, but only with version-bump files ($($dirtyPaths -join ', ')) - resuming: they'll be bundled into the release commit."
    } else {
        Write-Host $status
        Fail "Working tree is dirty beyond the version-bump files ($($nonBump -join ', ')). Commit or stash before releasing (or pass -AllowDirty to bundle pending changes into the release commit)."
    }
}
Ok "Tree state acceptable."

# Verify tag isn't already on remote - and if it is, decide between abort and
# resume. A complete successful publication is immutable and aborts. A release
# with assets but an incomplete/failed workflow is resumable, which avoids the
# old dead end where a post-create workflow failure could not be retried.
$resumeRelease = $false
$releaseExists = $false
$existingAssetsComplete = $false
$remoteAssetsNeedRepair = $false
$publicationAlreadyVerified = $false
if ($remoteTag) {
    # Only GitHub's explicit "release not found" means absent. Any other request
    # failure leaves the release state unknown, and an unknown state must not fall
    # through to creating a release or re-uploading assets.
    $remoteReleaseOutput = @(gh release view $tag --json tagName,isDraft,assets 2>&1)
    $remoteReleaseExit = $LASTEXITCODE
    $remoteReleaseRaw = @($remoteReleaseOutput | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    if ($remoteReleaseExit -eq 0 -and $remoteReleaseRaw.Count -gt 0) {
        $releaseExists = $true
    } elseif (($remoteReleaseOutput -join ' ') -notmatch 'release not found') {
        Fail "Could not determine whether GitHub Release $tag exists (gh exit $remoteReleaseExit); reconcile with scripts\release-doctor.ps1 -Remote before retrying."
    }
    $remoteRelease = if ($releaseExists) { (($remoteReleaseRaw -join [Environment]::NewLine) | ConvertFrom-Json) } else { $null }
    $assetNames = if ($releaseExists) { @($remoteRelease.assets | ForEach-Object { [string]$_.name }) } else { @() }
    $requiredReleaseAssets = @(Get-GxMcpReleaseRequiredAssetNames -Version $Version -AllowLegacy:$allowLegacyAssets)
    $hasCompleteAssetSet = $releaseExists -and @($requiredReleaseAssets | Where-Object { $assetNames -notcontains $_ }).Count -eq 0
    $remoteAssetState = if ($releaseExists) {
        Get-GxMcpReleaseAssetVerification -Assets @($remoteRelease.assets) -RepositoryRoot $root -Version $Version -AllowLegacy:$allowLegacyAssets
    } else { $null }
    $remoteAssetsNeedRepair = $hasCompleteAssetSet -and ($null -eq $remoteAssetState -or -not $remoteAssetState.isValid)
    $existingAssetsComplete = $hasCompleteAssetSet -and -not $remoteAssetsNeedRepair
    $publicationAlreadyVerified = $existingAssetsComplete -and (Test-ReleasePublicationAlreadyVerified -Tag $tag -PackageVersion $Version)
    if ($publicationAlreadyVerified -and @($CloseIssues).Count -eq 0) {
        Fail "Tag $tag already has a successful publication workflow, exact assets, and exact npm version. Bump the version (or delete the remote tag + release first)."
    }
    if ($releaseExists) {
        if ($publicationAlreadyVerified) {
            Warn "Tag $tag is already published; resuming the remaining issue-closure step without creating a duplicate publication."
        } elseif ($remoteAssetsNeedRepair) {
            Warn "Tag $tag has remote assets that do not match the local transaction; resuming with an explicit asset repair."
        } elseif ($hasCompleteAssetSet) {
            Warn "Tag $tag has complete release assets but publication is not fully verified - resuming the idempotent workflow/verification path."
        } else {
            Warn "Tag $tag exists on origin with incomplete release assets - resuming: will upload the missing assets."
        }
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
    # Issue #328: the promotion now seeds `## Unreleased` with an anchor comment
    # and empty `###` headings, so "non-whitespace" would let an untouched
    # section ship an empty release. Substantive means a real entry line.
    if (-not $unreleasedMatch.Success -or
        -not (Test-ReleaseChangelogSubstantiveBody $unreleasedMatch.Groups['body'].Value)) {
        Fail "CHANGELOG.md has no substantive '## Unreleased' section to promote into '## v$Version'."
    }
    Ok "CHANGELOG has release notes ready to promote into ## v$Version."
}

$unreleasedBodyMatch = [Regex]::Match(
    $changelog,
    '(?ms)^##[ \t]+Unreleased[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t]|\z)')
$hasTrackedIssuesInUnreleased = $unreleasedBodyMatch.Success -and
    $unreleasedBodyMatch.Groups['body'].Value -match '(?m)^###\s+Tracked issues\s*$'
if (@($CloseIssues).Count -gt 0 -and $changelog -notmatch $versionHeadingPattern) {
    $issueLines = @($releaseIssueRecords | ForEach-Object {
        "- [#$($_.number)]($($_.url)) — $($_.title)"
    }) -join "`r`n"
    $trackedIssues = "### Tracked issues`r`n`r`n$issueLines`r`n"
    if ($DryRun) {
        Warn "[DRY-RUN] would add $(@($CloseIssues).Count) tracked issue link(s) to the release changelog."
    } elseif (-not $hasTrackedIssuesInUnreleased) {
        $updatedChangelog = [Regex]::Replace(
            $changelog,
            '(?m)^##[ \t]+Unreleased[ \t]*',
            "## Unreleased`r`n`r`n$trackedIssues",
            1)
        [IO.File]::WriteAllText($changelogPath, $updatedChangelog, [Text.UTF8Encoding]::new($false))
        $changelog = $updatedChangelog
        Ok "CHANGELOG.md -> added tracked issue links."
    }
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
$extPackageVersion = $null
$extPackagePath = Join-Path $root 'src\nexus-ide\package.json'
if (Test-Path $extPackagePath) {
    try { $extPackageVersion = (Get-Content $extPackagePath -Raw | ConvertFrom-Json).version } catch { $extPackageVersion = $null }
}
$lockfileNeedsSync = $false
foreach ($lockfilePath in @(
    (Join-Path $root 'package-lock.json'),
    (Join-Path $root 'src\nexus-ide\package-lock.json')
)) {
    if (-not (Test-Path -LiteralPath $lockfilePath -PathType Leaf)) { continue }
    try {
        $lockfile = Get-Content -LiteralPath $lockfilePath -Raw | ConvertFrom-Json -AsHashtable
        $rootPackageVersion = if ($lockfile.ContainsKey('packages') -and $lockfile['packages'] -is [System.Collections.IDictionary]) {
            $rootPackage = $lockfile['packages']['']
            if ($rootPackage -is [System.Collections.IDictionary]) { $rootPackage['version'] } else { $null }
        } else { $null }
        if ($lockfile['version'] -ne $Version -or $rootPackageVersion -ne $Version) { $lockfileNeedsSync = $true }
    } catch { $lockfileNeedsSync = $true }
}
$changelogNeedsPromotion = -not ($changelog -match $versionHeadingPattern)
$needsBump = $changelogNeedsPromotion -or
             ($Version -ne $currentVersion) -or
             ($csprojVersion -and $csprojVersion -ne $Version) -or
             ($workerCsprojVersion -and $workerCsprojVersion -ne $Version) -or
             ($extPackageVersion -and $extPackageVersion -ne $Version) -or
             $lockfileNeedsSync
if ($needsBump -and ($Version -eq $currentVersion) -and ($csprojVersion -ne $Version)) {
    Warn "csproj InformationalVersion=$csprojVersion is out of sync with package.json=$Version - forcing bump pass to realign."
}
if ($resumeRelease) {
    Ok "Release metadata is already committed; skipping version mutation during resume."
} elseif ($needsBump) {
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
                -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)',       "`${1}$numericVersion.0`${2}" `
                -replace '(<FileVersion>)[^<]+(</FileVersion>)',               "`${1}$numericVersion.0`${2}" `
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
                -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)',       "`${1}$numericVersion.0`${2}" `
                -replace '(<FileVersion>)[^<]+(</FileVersion>)',               "`${1}$numericVersion.0`${2}" `
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

    Set-LockfileVersion -Path (Join-Path $root 'package-lock.json') -TargetVersion $Version
    Set-LockfileVersion -Path (Join-Path $root 'src\nexus-ide\package-lock.json') -TargetVersion $Version

    # CHANGELOG.md - promote ## Unreleased to ## v$Version - YYYY-MM-DD if ## v$Version does not exist yet.
    $changelogPath = Join-Path $root 'CHANGELOG.md'
    if (Test-Path $changelogPath) {
        if (-not $DryRun) {
            $dateStr = (Get-Date).ToString('yyyy-MM-dd')
            $promotion = Update-ReleaseChangelogPromotion -Path $changelogPath -Version $Version -Date $dateStr
            if ($promotion.status -eq 'promoted') {
                Ok "CHANGELOG.md -> promoted ## Unreleased to ## v$Version"
                # Issue #328: the promotion must leave the fresh `## Unreleased`
                # with an explicit anchor, or the next bullet lands in the
                # release that was just published.
                if ($promotion.anchored) { Ok "CHANGELOG.md -> anchored the new '## Unreleased' section." }
                else { Warn "CHANGELOG.md -> could not anchor '## Unreleased'; add a '### Added/Changed/Fixed' subsection before the next release." }
            } elseif ($promotion.status -eq 'already-present') {
                Ok "CHANGELOG.md -> ## v$Version already present."
            } else {
                # No '## Unreleased' heading (e.g. freshly released without one),
                # so the promotion would silently skip the entry. Fail loudly
                # instead of shipping a release whose notes fall back to generic
                # text.
                Fail "CHANGELOG.md has no '## Unreleased' section to promote into '## v$Version'. Add the section (and the version's entries under it), then retry."
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

# -- 3. Commit the source state before building -----------------------------
# The manifest is written from the exact commit that will receive the tag.
# Committing here prevents a successful build from being represented as the
# ambiguous `working-tree` provenance marker.
$releaseSourceCommit = $null
if ($resumeRelease) {
    if (-not $DryRun) {
        $releaseSourceCommit = (git rev-list -n 1 $tag).Trim()
        if ([string]::IsNullOrWhiteSpace($releaseSourceCommit)) {
            Fail "Could not resolve the existing tag $tag while resuming the release."
        }
        $resumeHead = (git rev-parse HEAD).Trim()
        if ($resumeHead -ne $releaseSourceCommit) {
            Fail "Cannot resume $tag from HEAD $resumeHead because the tag is pinned to $releaseSourceCommit."
        }
        Ok "Resuming from tagged source commit: $releaseSourceCommit"
    }
} elseif (-not $DryRun) {
    $pendingMetadata = @(git status --porcelain --untracked-files=no)
    if ($pendingMetadata) {
        Step "Committing release source state"
        if ($AllowDirty) {
            Invoke-Cmd 'git' @('add', '-A')
        } else {
            Invoke-Cmd 'git' (@('add', '--') + $releaseManagedPaths)
        }
        git diff --cached --quiet
        $hasStagedChanges = $LASTEXITCODE -ne 0
        if ($hasStagedChanges) {
            Invoke-Cmd 'git' @('commit', '-m', "release: $tag")
            Ok "Committed release source state: release: $tag"
        } else {
            Ok "Release metadata was already committed."
        }
    }
    $releaseSourceCommit = (git rev-parse HEAD).Trim()
    if ([string]::IsNullOrWhiteSpace($releaseSourceCommit)) {
        Fail "Could not resolve the committed release source state."
    }
    Ok "Release source commit: $releaseSourceCommit"
} else {
    $releaseSourceCommit = 'dry-run'
    Warn "[DRY-RUN] would commit release metadata before building."
}

Step "Validating release metadata parity"
Invoke-Cmd 'python' @(
    (Join-Path $root 'scripts\verify-release-metadata.py'),
    '--root', $root,
    '--version', $Version
)
if ($DryRun) { Warn "[DRY-RUN] would validate package and lockfile versions at $Version." } else { Ok "Package and lockfile versions are synchronized at $Version." }

# -PrepareOnly stops here: the version, changelog, metadata and the single `release: <tag>`
# commit exist, so that exact SHA can be pushed as a candidate branch/PR and validated by CI.
# Nothing is built, tagged, pushed, or published. The release is then rerun for the same
# version, which reuses this commit (no second bump) once required CI is green on it.
if ($PrepareOnly) {
    if ($resumeRelease) { Fail "-PrepareOnly cannot be used while $tag exists on origin; the release is already prepared and published or resumable." }
    if ($DryRun) {
        Warn "[DRY-RUN] would stop after preparing the release commit (-PrepareOnly); nothing is built or published."
        Write-ReleaseStatus -Phase 'dry-run-complete' -State 'succeeded' -ExitCode 0 -NextAction 'run-prepare-only'
        exit 0
    } else {
        Ok "Release $tag prepared at commit $releaseSourceCommit. Push that SHA as a candidate branch/PR, wait for required CI, then rerun: .\release.ps1 -Version $Version"
        Write-Host "    prepared commit: $releaseSourceCommit" -ForegroundColor Cyan
        Write-ReleaseStatus -Phase 'prepared' -State 'succeeded' -ExitCode 0 -NextAction 'push-candidate-and-run-ci'
        exit 0
    }
}

# Cheap CI-presence check before the multi-minute build and preflight: the release commit is
# fixed here, so required CI is either already green on this exact SHA or the release cannot
# be published and building it would only fail later. Re-verified right before tagging.
if (-not $DryRun -and -not $resumeRelease) {
    Step "Checking required CI on the release source commit"
    if ($AutoCandidate) {
        $ciState = Test-ReleaseRequiredChecksQuiet -Commit $releaseSourceCommit
        if ($ciState.Valid) {
            Ok "Required checks ($($ciState.Required -join ', ')) are already green on $releaseSourceCommit."
        } else {
            Ensure-ReleaseCandidateCi -Commit $releaseSourceCommit -Tag $tag -DryRun:$DryRun
            Ok "Candidate CI initiated for $releaseSourceCommit ($tag). Overlapping local build/preflight with remote CI."
        }
    } else {
        Assert-ReleaseRequiredChecksGreen -Commit $releaseSourceCommit
    }
}

$preflightSummaryPath = Join-Path $env:TEMP "gxmcp-release-preflight-$Version.json"
$preflightGxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) { $env:GX_PATH } else { Get-GxPrimaryInstallPath -Catalog $gxCatalog }
if (-not $DryRun -and -not $SkipBuild -and -not $SkipTests -and (Test-Path -LiteralPath $preflightSummaryPath -PathType Leaf)) {
    try {
        $priorPreflight = Get-Content -LiteralPath $preflightSummaryPath -Raw | ConvertFrom-Json
        $currentArtifactFingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:$allowLegacyAssets
        $expectedPreflightInputs = Get-ReleasePreflightInputs -ArtifactFingerprint $currentArtifactFingerprint
        $canResumeBuild = Test-GxMcpReleasePreflightCompatibility -Summary $priorPreflight -Expected $expectedPreflightInputs
        if ($canResumeBuild) {
            $SkipBuild = $true
            Warn "Reusing publish/ and VSIX from the matching preflight summary; build will not be repeated."
        }
    } catch {
        Warn "Ignoring preflight summary that could not be validated for build reuse: $($_.Exception.Message)"
    }
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
    'worker\GxMcp.Worker.exe',
    'tool_definitions.json'
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
        # Keep the extension in the same publish directory so the manifest,
        # archive and VSIX all describe one reproducible build.
        Copy-Item -LiteralPath $vsixPath -Destination (Join-Path $publishDir 'nexus-ide.vsix') -Force
        Ok "nexus-ide-$Version.vsix packaged."
    } else {
        Write-Host "    $ npx --yes @vscode/vsce package --no-dependencies -o $vsixPath  (cwd: $extDir)" -ForegroundColor DarkGray
        Ok "[dry-run] would package nexus-ide-$Version.vsix"
    }
} else {
    Warn "-SkipBuild set; reusing existing $vsixPath if present."
}
if ($DryRun) {
    Warn "[DRY-RUN] would reconcile the Nexus VSIX between the release root and publish directory."
} else {
    try {
        Ensure-ReleaseVsixArtifacts -PublishDirectory $publishDir -VsixPath $vsixPath -AllowMissing:$allowLegacyAssets
        if ($allowLegacyAssets -and -not (Test-Path -LiteralPath $vsixPath -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path $publishDir 'nexus-ide.vsix') -PathType Leaf)) {
            Ok "Legacy release does not require a Nexus VSIX asset."
        } else {
            Ok "Nexus VSIX is synchronized between the release root and publish directory."
        }
    } catch {
        Fail $_.Exception.Message
    }
    $publishVsixPath = Join-Path $publishDir 'nexus-ide.vsix'
    if (-not $allowLegacyAssets -and -not (Test-Path -LiteralPath $publishVsixPath -PathType Leaf)) {
        Fail 'Required publish artifact missing after VSIX reconciliation: nexus-ide.vsix'
    }
    if ($SkipBuild) {
        $skipBuildEvidenceValid = $false
        try {
            if (Test-Path -LiteralPath $preflightSummaryPath -PathType Leaf) {
                $skipBuildSummary = Get-Content -LiteralPath $preflightSummaryPath -Raw | ConvertFrom-Json
                $currentSkipBuildFingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:$allowLegacyAssets
                $expectedPreflightInputs = Get-ReleasePreflightInputs -ArtifactFingerprint $currentSkipBuildFingerprint
                $skipBuildEvidenceValid = if ($SkipTests) {
                    Test-GxMcpReleasePreflightCertificate -Summary $skipBuildSummary -Expected $expectedPreflightInputs
                } else {
                    Test-GxMcpReleasePreflightCompatibility -Summary $skipBuildSummary -Expected $expectedPreflightInputs
                }
            }
        } catch { }
        if (-not $skipBuildEvidenceValid) {
            Fail "-SkipBuild requires a matching artifact fingerprint and preflight inputs; -SkipTests additionally requires a complete passed preflight certificate."
        }
        Ok "Skip-build reuse validated against the matching preflight inputs."
    }
    $statusState.artifactFingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $root -Version $Version -AllowLegacy:$allowLegacyAssets
    $statusState.preflightSummaryPath = $preflightSummaryPath
    Write-ReleaseStatus -Phase 'artifacts-ready' -State 'running' -ArtifactFingerprint $statusState.artifactFingerprint `
        -PreflightSummaryPath $preflightSummaryPath -NextAction 'run-preflight'
}

# -- 4. Optional test pass -------------------------------------------------
if (-not $SkipTests) {
    Step "Running complete release preflight"
    $releasePreflightArgs = @(
        '-NoProfile',
        '-File', (Join-Path $root 'scripts/release-preflight.ps1'),
        '-Version', $Version,
        '-GxPath', $preflightGxPath,
        '-SummaryPath', $preflightSummaryPath,
        '-ResumeSummaryPath', $preflightSummaryPath
    )
    if (-not [string]::IsNullOrWhiteSpace($LiveKbPath)) { $releasePreflightArgs += @('-LiveKbPath', $LiveKbPath) }
    if (-not [string]::IsNullOrWhiteSpace($LiveFixtureManifest)) { $releasePreflightArgs += @('-LiveFixtureManifest', $LiveFixtureManifest) }
    if ($RequireLive) { $releasePreflightArgs += '-RequireLive' }
    if ($RequireBuildAll) { $releasePreflightArgs += '-RequireBuildAll' }
    Invoke-Cmd 'pwsh' $releasePreflightArgs
    if ($DryRun) { Warn '[DRY-RUN] would run the complete release preflight.' } else { Ok 'Complete release preflight passed.' }
} else {
    Warn "-SkipTests set; preflight is intentionally skipped (warning baseline still runs)."
    Write-ReleaseStatus -Phase 'tests-skipped' -State 'running'
}

# A release build can succeed while adding a new nullable/analyzer warning.
# The complete preflight owns this gate on the normal path. Keep the direct
# invocation only for -SkipTests so that skipping the broader suites does not
# silently skip the warning-surface regression check.
if ($SkipTests) {
    Step "Checking Release warning baseline"
    Invoke-Cmd 'pwsh' @(
        '-NoProfile',
        '-File',
        (Join-Path $root 'scripts\check-build-warning-baseline.ps1'),
        '-BaselineFile',
        (Join-Path $root 'docs\build_warning_baseline.json')
    )
    if ($DryRun) { Warn '[DRY-RUN] would run the Release warning baseline gate.' } else { Ok 'Release warning baseline passed.' }
} elseif ($DryRun) {
    Warn '[DRY-RUN] warning baseline is included in the complete preflight.'
} else {
    Ok 'Release warning baseline was included in the complete preflight.'
}

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

# -- 4c. Write a machine-readable manifest into publish/ ------------------
# Corporate installers validate this manifest inside publish.zip before any
# existing installation is moved. The shared writer keeps local candidate and
# maintainer release manifests identical, including the packaged VSIX hash.
if ($resumeRelease) {
    Step "Verifying existing release manifest"
} else {
    Step "Writing release manifest"
    Invoke-Cmd 'pwsh' @(
        '-NoProfile',
        '-File', (Join-Path $root 'scripts\write-release-manifest.ps1'),
        '-PublishDirectory', $publishDir,
        '-Version', $Version,
        '-SourceRoot', $root,
        '-SourceCommit', $releaseSourceCommit
    )
    if ($DryRun) { Warn "[DRY-RUN] would write gxmcp-manifest.json for $Version." } else { Ok "gxmcp-manifest.json written for $Version." }
}
Step "Verifying release manifest"
Invoke-Cmd 'python' @(
    (Join-Path $root 'scripts\verify-release-manifest.py'),
    $publishDir,
    '--version', $Version,
    '--source-commit', $releaseSourceCommit
)
if ($DryRun) { Warn '[DRY-RUN] would verify release manifest artifacts.' } else { Ok 'Release manifest artifacts verified.' }

# -- 5. Zip publish/ -> publish.zip -----------------------------------------
Step "Packing publish.zip"
$zipPath = Join-Path $root 'publish.zip'
$shaPath = "$zipPath.sha256"
if ($resumeRelease) {
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $shaPath -PathType Leaf)) {
        Fail 'Cannot resume the release because publish.zip or publish.zip.sha256 is missing.'
    }
    $resumeZipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $resumeDeclaredHash = (Get-Content -LiteralPath $shaPath -Raw).Trim().Split()[0].ToLowerInvariant()
    if ($resumeZipHash -ne $resumeDeclaredHash) {
        Fail 'Cannot resume the release because publish.zip does not match publish.zip.sha256.'
    }
    Ok "Existing publish.zip and checksum reused ($resumeZipHash)."
} elseif (-not $DryRun) {
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
if (-not $DryRun) {
    Step 'Verifying publish.zip against the release manifest'
    Invoke-Cmd 'python' @(
        (Join-Path $root 'scripts\verify-release-manifest.py'),
        $publishDir,
        '--version', $Version,
        '--source-commit', $releaseSourceCommit,
        '--archive', $zipPath
    )
    Ok 'publish.zip matches the staged release manifest and publish tree.'
}

# -- 6. Confirm packaging did not alter the committed source ----------------
# publish/, publish.zip, the VSIX, and the checksum are generated/ignored.
# Any other change means the artifact no longer corresponds to the source
# commit captured in the manifest and must stop the release.
if ($resumeRelease) {
    Step "Verifying existing tag and source state"
    if (-not $DryRun) {
        $pendingResume = @(git status --porcelain --untracked-files=no)
        if ($pendingResume) {
            Write-Host ($pendingResume -join "`n")
            Fail "Cannot resume $tag with a dirty source tree. Clean or commit the pending changes first."
        }
        $headAfterPackage = (git rev-parse HEAD).Trim()
        $localTagCommit = (git rev-list -n 1 $tag).Trim()
        if ($headAfterPackage -ne $releaseSourceCommit -or $localTagCommit -ne $releaseSourceCommit) {
            Fail "Resume source mismatch: HEAD=$headAfterPackage localTag=$localTagCommit expected=$releaseSourceCommit."
        }
        $remoteResumeTag = @(git ls-remote --tags origin "refs/tags/$tag^{}" 2>$null)
        if ($LASTEXITCODE -ne 0 -or $remoteResumeTag.Count -eq 0 -or
            ((($remoteResumeTag[0].ToString().Trim()) -split '\s+')[0]) -ne $releaseSourceCommit) {
            Fail "Remote tag $tag is not pinned to the expected source commit $releaseSourceCommit."
        }
    }
    Ok "Existing tag and source state reused for $tag."
} elseif (-not $DryRun) {
    $pendingAfterPackage = @(git status --porcelain --untracked-files=no)
    if ($pendingAfterPackage) {
        Write-Host ($pendingAfterPackage -join "`n")
        Fail "Packaging left tracked changes after source commit $releaseSourceCommit. Inspect the files before tagging."
    }
    $headAfterPackage = (git rev-parse HEAD).Trim()
    if ($headAfterPackage -ne $releaseSourceCommit) {
        Fail "HEAD changed after packaging ($headAfterPackage) but the manifest records $releaseSourceCommit."
    }
    Ok "Packaged artifacts still bind to source commit $releaseSourceCommit."
} else {
    Warn "[DRY-RUN] would verify a clean tree before tagging."
}

# -- 7. Tag (annotated) + push ---------------------------------------------
if ($resumeRelease) {
    Step "Reusing existing tag $tag"
    if ($DryRun) { Warn "[DRY-RUN] would reuse the existing tag $tag." } else { Ok 'Existing local and remote tag reused.' }
} else {
    Step "Verifying pre-publication freshness"
    if ($DryRun) {
        Warn "[DRY-RUN] would re-verify HEAD, origin/main, the preflight certificate, and required CI on $releaseSourceCommit before tagging."
    } else {
        if ($AutoCandidate) {
            Wait-ReleaseRequiredChecksGreen -Commit $releaseSourceCommit
        }
        Assert-ReleasePrePublicationFresh -SourceCommit $releaseSourceCommit -Tag $tag -SummaryPath $preflightSummaryPath
        Ok 'HEAD, origin/main, preflight certificate, and required CI are fresh for the release source commit.'
    }

    Step "Tagging $tag"
    # A previous run can have created the local tag and then failed to push. Reuse
    # it only when it already points at this source commit; never move or recreate it.
    $existingLocalTag = if ($DryRun) { $null } else { (& git rev-parse -q --verify "refs/tags/$tag^{commit}" 2>$null) }
    if ($existingLocalTag) {
        if (([string]$existingLocalTag).Trim() -cne $releaseSourceCommit) {
            Fail "Local tag $tag points at $existingLocalTag, not the release source commit $releaseSourceCommit. Inspect it; the release will not move or delete a tag."
        }
        Ok "Local tag $tag already points at the release source commit; reusing it."
    } else {
        Invoke-Cmd 'git' @('tag', '-a', $tag, $releaseSourceCommit, '-m', "$tag")
        if ($DryRun) { Warn "[DRY-RUN] would create local tag $tag." } else { Ok 'Local tag created.' }
    }

    if (-not $DryRun) {
        Step "Pushing main + $tag to origin"
        Invoke-Cmd 'git' @('push', 'origin', 'HEAD')
        Invoke-Cmd 'git' @('push', 'origin', $tag)
        Ok "Pushed."
    }
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

# GitHub rejects a release body over 125000 characters with an unhelpful
# HTTP 422 "Validation Failed", and this repository's changelog voice is
# deliberately long: each entry explains why the change was made rather than
# what changed, so a section that documents a release honestly can exceed the
# cap. Cutting mid-bullet produces a body that reads as though the release
# stopped early, and failing outright would mean a release cannot ship because
# its notes are thorough - which is the wrong reason to not ship.
#
# So: cut at the last complete bullet that fits, and say plainly in the body
# that it was cut and where the rest lives. The full text is not lost - it is in
# CHANGELOG.md at the tag, which is the same place a reader would look for it.
$GitHubReleaseBodyLimit = 125000
if ($notes.Length -gt $GitHubReleaseBodyLimit) {
    $originalLength = $notes.Length
    $budget = $GitHubReleaseBodyLimit - 1000
    $cut = $notes.LastIndexOf("`n- ", $budget)
    if ($cut -lt 1) { $cut = $notes.LastIndexOf("`n##", $budget) }
    if ($cut -lt 1) { $cut = $notes.LastIndexOf("`n", $budget) }
    if ($cut -lt 1) { $cut = $budget }
    $omitted = $originalLength - $cut
    $pointer = @"

---

_This release body was truncated to fit GitHub's $GitHubReleaseBodyLimit-character limit. The full entry for $tag is $originalLength characters; $omitted characters were omitted here. The complete text is in ``CHANGELOG.md`` at tag ``$tag``._
"@
    $notes = $notes.Substring(0, $cut).TrimEnd() + $pointer
    Warn "Release notes exceeded the GitHub body limit: $originalLength -> $($notes.Length) chars. Truncated at a bullet boundary; the full entry stays in CHANGELOG.md at $tag."
}

$notesTmp = Join-Path $env:TEMP "release-notes-$tag.md"
if (-not $DryRun) {
    [System.IO.File]::WriteAllText($notesTmp, $notes, [System.Text.UTF8Encoding]::new($false))
}
# Key insight: `gh release create <tag> [files...]` uploads the assets in
# the same call as create, so the workflow's FIRST `release.published` event
# already has publish.zip attached -> npm publish succeeds on the first run.
# If a previous run created the release and failed before uploading assets,
# resume with `gh release upload` instead of trying to create a duplicate.
$assetArgs = @($zipPath)
# Attach the checksum sidecar so the gateway self-updater can verify the download.
if (Test-Path $shaPath) { $assetArgs += $shaPath }
# Attach the Nexus IDE VSIX - ships in lockstep with the MCP release.
if ($DryRun -or (Test-Path $vsixPath)) { $assetArgs += $vsixPath }
if ($releaseExists -and $existingAssetsComplete) {
    $releaseUrl = "https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag"
    Ok "Existing GitHub Release assets reused: $releaseUrl"
    Write-ReleaseStatus -Phase 'release-assets-reused' -State 'running' -ReleaseUrl $releaseUrl `
        -PublicationState 'pending' -NextAction 'verify-publication'
} elseif ($releaseExists) {
    Step "Uploading assets to existing GitHub release $tag"
    $uploadArgs = @('release', 'upload', $tag, '--clobber') + $assetArgs
    Invoke-Cmd 'gh' $uploadArgs
    if ($DryRun) {
        Warn "[DRY-RUN] would upload publish.zip, checksum, and VSIX assets to existing release $tag."
    } else {
        $releaseUrl = "https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag"
        Ok "Release assets uploaded: $releaseUrl"
        Write-ReleaseStatus -Phase 'release-assets-uploaded' -State 'running' -ReleaseUrl $releaseUrl `
            -PublicationState 'pending' -NextAction 'verify-publication'
    }
} else {
    Step "Creating GitHub release $tag (with publish.zip)"
    $createArgs = @(
        'release', 'create', $tag,
        '--title', "$tag",
        '--notes-file', $notesTmp,
        '--target', 'main'
    ) + $assetArgs
    Invoke-Cmd 'gh' $createArgs
    if ($DryRun) {
        Warn "[DRY-RUN] would create GitHub release $tag with publish.zip, checksum, and VSIX assets."
    } else {
        $releaseUrl = "https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag"
        Ok "Release created: $releaseUrl"
        Write-ReleaseStatus -Phase 'release-created' -State 'running' -ReleaseUrl $releaseUrl `
            -PublicationState 'pending' -NextAction 'verify-publication'
    }
}
if (Test-Path -LiteralPath $notesTmp) { Remove-Item -LiteralPath $notesTmp -Force -ErrorAction SilentlyContinue }

# Verify the complete publication transaction before mutating issue state. The
# release script creates the GitHub Release synchronously, but npm publication
# is owned by the trusted workflow and may still be propagating.
if (-not $DryRun) {
    Step "Verifying GitHub Release and npm publication"
    $publicationArgs = @(
        '-NoProfile', '-File', (Join-Path $root 'scripts\verify-release-publication.ps1'),
        '-Tag', $tag,
        '-Version', $Version,
        '-ExpectedCommit', $releaseSourceCommit,
        '-EvidencePath', $statusState.publicationEvidencePath,
        '-StatusFile', $StatusFile,
        '-TimeoutSeconds', '900',
        '-PollSeconds', '10'
    )
    if ($releaseExists) { $publicationArgs += '-DispatchIfMissing' }
    Invoke-Cmd 'pwsh' $publicationArgs
    $publicationEvidence = Get-Content -LiteralPath $statusState.publicationEvidencePath -Raw | ConvertFrom-Json
    $statusState.publication = $publicationEvidence
    $statusState.publicationState = 'verified'
    $statusState.publicationError = $null
    $statusState.releaseUrl = [string]$publicationEvidence.releaseUrl
    $statusState.workflowRunId = [string]$publicationEvidence.workflowRunId
    $statusState.tagCommit = [string]$publicationEvidence.tagCommit
    $statusState.npmVersion = [string]$publicationEvidence.npmVersion
    $publicationCheck = Test-GxMcpReleasePublicationEvidence -Status $statusState -Publication $publicationEvidence -RepositoryRoot $root -AllowLegacy:$allowLegacyAssets
    if (-not $publicationCheck.Valid) {
        Fail "Publication evidence failed the local contract recheck: $($publicationCheck.Error)"
    }
    $statusState.nextAction = if (@($CloseIssues).Count -gt 0) { 'close-issues' } else { 'complete' }
    Write-ReleaseStatus -Phase 'publication-verified' -State 'running' -PublicationState 'verified' `
        -PublicationEvidencePath $statusState.publicationEvidencePath -TagCommit $statusState.tagCommit `
        -NpmVersion $statusState.npmVersion -WorkflowRunId $statusState.workflowRunId `
        -ReleaseUrl $statusState.releaseUrl -NextAction $statusState.nextAction
    Ok "Publication verified: workflow #$($statusState.workflowRunId), npm $($statusState.npmVersion)."
}

# Close only issues explicitly associated with this release, and only after
# GitHub assets, the publish workflow, and npm visibility have been verified.
if (@($CloseIssues).Count -gt 0) {
    Step "Closing released issues"
    $issueReleaseUrl = if ($releaseUrl) { $releaseUrl } else { "https://github.com/lennix1337/Genexus18MCP/releases/tag/$tag" }
    Close-ReleaseIssues -ReleaseUrl $issueReleaseUrl
}

if ($AutoCandidate -and -not $DryRun) {
    Cleanup-ReleaseCandidate -Tag $tag
}

Write-Host ""
if ($DryRun) {
    Write-Host "    Dry run: inspect the planned workflow and npm verification manually." -ForegroundColor Cyan
    Write-Host "      gh run list --workflow release.yml" -ForegroundColor Gray
    Write-Host "      npm view genexus-mcp@$Version version" -ForegroundColor Gray
} else {
    Write-Host "    Publication evidence: $($statusState.publicationEvidencePath)" -ForegroundColor DarkGray
    Write-Host "    npm exact version: $($statusState.npmVersion)" -ForegroundColor DarkGray
}
Write-Host ""

if ($DryRun) {
    $statusState.nextAction = 'run-real-release'
    Warn "DRY RUN - no remote changes were made. Re-run without -DryRun to publish."
    Write-ReleaseStatus -Phase 'dry-run-complete' -State 'succeeded' -ExitCode 0 -NextAction $statusState.nextAction
} else {
    $statusState.nextAction = 'complete'
    Write-ReleaseStatus -Phase 'complete' -State 'succeeded' -ExitCode 0 -NextAction $statusState.nextAction
}
