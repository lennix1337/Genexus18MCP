[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [string]$ArtifactRoot,
    [string]$StatusFile,
    [ValidateRange(30, 7200)][int]$TimeoutSeconds = 900,
    [ValidateRange(1, 60)][int]$PollSeconds = 10,
    [switch]$DispatchIfMissing
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) { $ArtifactRoot = $root } else { $ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot) }
. (Join-Path $root 'scripts\release-contract.ps1')
$startedAtUtc = [DateTime]::UtcNow
$evidence = [ordered]@{
    schemaVersion = 'gxmcp-release-publication/1'
    state = 'running'
    tag = $Tag
    version = $Version
    expectedCommit = $ExpectedCommit
    sourceCommit = $null
    tagCommit = $null
    releaseUrl = $null
    releasePublishedAtUtc = $null
    assets = @()
    assetsUpdatedAtUtc = $null
    workflowRunId = $null
    workflowStatus = $null
    workflowConclusion = $null
    workflowUrl = $null
    npmVersion = $null
    npmGitHead = $null
    startedAtUtc = $startedAtUtc.ToString('o')
    updatedAtUtc = $startedAtUtc.ToString('o')
    endedAtUtc = $null
    error = $null
}

# Issue #326: the pattern set is the canonical one in release-contract.ps1, so
# this site keeps only its own 600-character cap instead of a narrower local
# copy that silently missed pwd/api_key/authorization/connection string/user id.
function Protect-PublicationMessage([object]$Value) {
    $text = Protect-GxMcpReleaseText $Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    if ($text.Length -gt 600) { $text = $text.Substring(0, 600) + '…' }
    return $text
}

function Write-AtomicJson([string]$Path, [object]$Value) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, (($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Save-Evidence {
    $evidence.updatedAtUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtomicJson -Path $EvidencePath -Value $evidence
    if (-not [string]::IsNullOrWhiteSpace($StatusFile) -and (Test-Path -LiteralPath $StatusFile -PathType Leaf)) {
        try {
            $status = Get-Content -LiteralPath $StatusFile -Raw | ConvertFrom-Json
            $status | Add-Member -NotePropertyName publication -NotePropertyValue $evidence -Force
            Write-AtomicJson -Path $StatusFile -Value $status
        } catch {
            Write-Verbose "Could not update release status publication evidence: $($_.Exception.Message)"
        }
    }
}

function Invoke-GhJson {
    param([string[]]$Arguments)
    $all = @(& gh @Arguments 2>&1)
    $exit = $LASTEXITCODE
    $raw = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    if ($exit -ne 0) {
        $detail = (@($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join ' ').Trim()
        throw "gh $($Arguments -join ' ') failed (exit $exit)$(if ($detail) { ": $detail" })."
    }
    $text = ($raw -join [Environment]::NewLine)
    if ([string]::IsNullOrWhiteSpace($text)) { throw "gh $($Arguments -join ' ') returned no JSON." }
    return ($text | ConvertFrom-Json)
}

function Get-RequiredAssetNames([string]$PackageVersion) {
    return @(Get-GxMcpReleaseRequiredAssetNames -Version $PackageVersion -AllowLegacy:(Test-GxMcpReleaseLegacyVersion $PackageVersion))
}

function Get-ReleaseAssetState {
    $release = Invoke-GhJson @('release', 'view', $Tag, '--json', 'tagName,url,isDraft,isPrerelease,assets,publishedAt')
    if ($release.tagName -cne $Tag) { throw "GitHub release tag mismatch: expected $Tag, got $($release.tagName)." }
    if ($release.isDraft) { throw "GitHub release $Tag is still a draft." }
    $expectedReleaseUrl = "https://github.com/lennix1337/Genexus18MCP/releases/tag/$Tag"
    if ([string]$release.url -cne $expectedReleaseUrl) { throw "GitHub release URL mismatch: expected $expectedReleaseUrl, got $($release.url)." }
    $allowLegacy = Test-GxMcpReleaseLegacyVersion $Version
    $assetState = Get-GxMcpReleaseAssetVerification -Assets @($release.assets) -RepositoryRoot $ArtifactRoot -Version $Version -AllowLegacy:$allowLegacy
    if (-not $assetState.isValid) { throw "GitHub release assets do not match the local release transaction: $($assetState.error)" }
    $evidence.releaseUrl = [string]$release.url
    $publishedAt = ConvertTo-GxMcpReleaseDateTimeOffset $release.publishedAt
    if ($publishedAt -eq [DateTimeOffset]::MinValue) { throw 'GitHub release publishedAt timestamp is invalid.' }
    $evidence.releasePublishedAtUtc = $publishedAt.ToUniversalTime().ToString('o')
    $evidence.assets = @($assetState.assets)
    $evidence.assetsUpdatedAtUtc = [string]$assetState.latestUpdatedAtUtc
    Save-Evidence
    return $release
}

function Get-TagCommit {
    $localTagCommit = (& git -C $root rev-list -n 1 $Tag 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $localTagCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Could not resolve local tag $Tag to a commit."
    }
    if ($localTagCommit -cne $ExpectedCommit) {
        throw "Tag $Tag resolves to $localTagCommit, expected $ExpectedCommit."
    }

    $remoteTagLines = @(& git -C $root ls-remote --tags origin "refs/tags/$Tag^{}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $remoteTagLines.Count -eq 0) {
        throw "Remote peeled tag refs/tags/$Tag^{} was not found."
    }
    $remoteTagCommit = (($remoteTagLines[0].ToString().Trim()) -split '\s+')[0]
    if ($remoteTagCommit -cne $ExpectedCommit) {
        throw "Remote peeled tag $Tag resolves to $remoteTagCommit, expected $ExpectedCommit."
    }
    $evidence.sourceCommit = $ExpectedCommit
    $evidence.tagCommit = $localTagCommit
    Save-Evidence
    return $localTagCommit
}

function Get-MatchingWorkflowRuns {
    $runs = @(Invoke-GhJson @('run', 'list', '--workflow', 'release.yml', '--limit', '50', '--json', 'databaseId,status,conclusion,headSha,headBranch,event,createdAt,url'))
    return @($runs | Where-Object {
        Test-GxMcpReleaseWorkflowRun -Run $_ -ExpectedCommit $ExpectedCommit -Tag $Tag `
            -MinimumCreatedAtUtc ([string]$evidence.assetsUpdatedAtUtc) -AllowDispatch:$DispatchIfMissing
    } | Sort-Object { [datetime]$_.createdAt } -Descending)
}

function Set-WorkflowEvidence([object]$Run) {
    $evidence.workflowRunId = [string]$Run.databaseId
    $evidence.workflowStatus = [string]$Run.status
    $evidence.workflowConclusion = [string]$Run.conclusion
    $evidence.workflowUrl = [string]$Run.url
    Save-Evidence
}

function Wait-ReleaseWorkflow {
    $workflowWaitStarted = [DateTime]::UtcNow
    $deadline = $workflowWaitStarted.AddSeconds($TimeoutSeconds)
    $discoverySeconds = [Math]::Min(60, $TimeoutSeconds)
    $discoveryDeadline = $workflowWaitStarted.AddSeconds($discoverySeconds)
    $dispatched = $false
    $ignoredRunIds = @{}

    while ([DateTime]::UtcNow -lt $deadline) {
        $runs = @(Get-MatchingWorkflowRuns)
        $visibleRuns = @($runs | Where-Object { -not $ignoredRunIds.ContainsKey([string]$_.databaseId) })
        $active = @($visibleRuns | Where-Object { $_.status -in @('queued', 'in_progress', 'requested', 'waiting', 'pending', 'action_required') } | Select-Object -First 1)
        $successful = @($visibleRuns | Where-Object { $_.status -eq 'completed' -and $_.conclusion -eq 'success' } | Select-Object -First 1)

        if ($active.Count -gt 0) {
            $selected = $active[0]
            Set-WorkflowEvidence -Run $selected
            if ($selected.status -eq 'completed' -and $selected.conclusion -eq 'success') { return $selected }
        } elseif ($successful.Count -gt 0) {
            $selected = $successful[0]
            Set-WorkflowEvidence -Run $selected
            return $selected
        } elseif (-not $dispatched -and $DispatchIfMissing -and
            (([DateTime]::UtcNow -gt $workflowWaitStarted.AddSeconds(15)) -or
                @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' }).Count -gt 0)) {
            foreach ($oldRun in $runs) { $ignoredRunIds[[string]$oldRun.databaseId] = $true }
            & gh workflow run release.yml --ref $Tag -f "tag=$Tag" 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Could not dispatch release.yml for $Tag." }
            $dispatched = $true
            $evidence.workflowStatus = 'dispatch-requested'
            Save-Evidence
        } elseif ($dispatched) {
            $failed = @($visibleRuns | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' } | Select-Object -First 1)
            if ($failed.Count -gt 0) {
                Set-WorkflowEvidence -Run $failed[0]
                throw "Release workflow #$($failed[0].databaseId) concluded with '$($failed[0].conclusion)'."
            }
        } elseif (-not $DispatchIfMissing -and $runs.Count -gt 0) {
            $failed = @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' } | Select-Object -First 1)
            if ($failed.Count -gt 0) {
                Set-WorkflowEvidence -Run $failed[0]
                throw "Release workflow #$($failed[0].databaseId) concluded with '$($failed[0].conclusion)'."
            }
        } elseif (-not $dispatched -and -not $DispatchIfMissing -and [DateTime]::UtcNow -ge $discoveryDeadline) {
            throw "No release workflow for $Tag appeared within $discoverySeconds seconds."
        }
        $remainingSeconds = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds)
        if ($remainingSeconds -gt 0) {
            Start-Sleep -Seconds ([Math]::Min($PollSeconds, $remainingSeconds))
        }
    }
    throw "Release workflow for $Tag did not complete within $TimeoutSeconds seconds."
}

function Wait-ReleaseNpmVersion {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $attempt = 0
    $allowLegacy = Test-GxMcpReleaseLegacyVersion $Version
    while ($true) {
        $attempt++
        $raw = @(& npm view "genexus-mcp@$Version" version gitHead --json --fetch-retries=0 --fetch-timeout=5000 2>$null)
        $observed = ''
        $gitHead = ''
        if ($LASTEXITCODE -eq 0 -and $raw.Count -gt 0) {
            try {
                $record = (($raw -join [Environment]::NewLine) | ConvertFrom-Json)
                if ($record -is [array]) { $record = @($record)[0] }
                $observed = [string]$record.version
                $gitHead = [string]$record.gitHead
            } catch { }
        }
        if ($observed -ceq $Version -and ($allowLegacy -or $gitHead -ceq $ExpectedCommit)) {
            $evidence.npmVersion = $observed
            $evidence.npmGitHead = $gitHead
            Save-Evidence
            return $observed
        }
        if ($observed -ceq $Version -and -not $allowLegacy -and -not [string]::IsNullOrWhiteSpace($gitHead) -and $gitHead -cne $ExpectedCommit) {
            throw "npm package genexus-mcp@$Version is bound to commit $gitHead, expected $ExpectedCommit."
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "npm package genexus-mcp@$Version was not visible with the expected commit within $TimeoutSeconds seconds (last observed version: $observed)."
        }
        $remainingSeconds = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds)
        if ($remainingSeconds -gt 0) {
            Start-Sleep -Seconds ([Math]::Min($PollSeconds, $remainingSeconds))
        }
    }
}

try {
    Get-RequiredAssetNames -PackageVersion $Version | Out-Null
    Get-ReleaseAssetState | Out-Null
    Get-TagCommit | Out-Null
    Wait-ReleaseWorkflow | Out-Null
    Wait-ReleaseNpmVersion | Out-Null
    $statusForEvidence = [pscustomobject]@{
        version = $Version
        tag = $Tag
        repository = 'lennix1337/Genexus18MCP'
        tagCommit = $evidence.tagCommit
    }
    $evidence.state = 'verified'
    $publicationCheck = Test-GxMcpReleasePublicationEvidence -Status $statusForEvidence -Publication $evidence -RepositoryRoot $ArtifactRoot -AllowLegacy:(Test-GxMcpReleaseLegacyVersion $Version)
    if (-not $publicationCheck.Valid) { throw "Publication evidence contract failed: $($publicationCheck.Error)" }
    $evidence.endedAtUtc = [DateTime]::UtcNow.ToString('o')
    Save-Evidence
    Write-Host "Release publication verified for $Tag (npm $Version, workflow #$($evidence.workflowRunId))." -ForegroundColor Green
    exit 0
} catch {
    $evidence.state = 'failed'
    $evidence.error = Protect-PublicationMessage $_.Exception.Message
    $evidence.endedAtUtc = [DateTime]::UtcNow.ToString('o')
    Save-Evidence
    Write-Error "Release publication verification failed: $($evidence.error)"
    exit 1
}
