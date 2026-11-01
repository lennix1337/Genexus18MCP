function Test-GxMcpReleaseHasField {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    # Two shapes reach these contracts and only one of them exposes keys through
    # PSObject.Properties. ConvertFrom-Json (every summary, snapshot, and evidence file)
    # produces PSCustomObject, whose keys DO surface there. release.ps1 builds its own
    # status as `[ordered]@{}` — an OrderedDictionary — whose keys do NOT.
    #
    # The old `$x.PSObject.Properties['key']` presence test was therefore always null for
    # the release status, so Test-GxMcpReleasePublicationEvidence returned
    # 'publication evidence commits do not match' on EVERY release even when the tag,
    # assets, workflow, and npm publication all verified. Because that check is the gate
    # immediately before the label-driven issue closure, no release ever reached the
    # close step and `fixed-pending-release` issues stayed open.
    if ($null -eq $Object) { return $false }
    if ($Object -is [System.Collections.IDictionary]) { return $Object.Contains($Name) }
    return ($null -ne $Object.PSObject.Properties[$Name])
}

# Reads a field's VALUE, accepting the same two shapes as Test-GxMcpReleaseHasField
# and in the same decision order: a dictionary is indexed by key, anything else is
# probed through PSObject.Properties. Without this, every reader that wanted a value
# re-derived the shape test and the ones that used PSObject.Properties alone were blind
# to the `[ordered]` phase and status objects that production actually builds.
#
# Presence and value are deliberately separate contracts: a key that is ABSENT and a
# key that is PRESENT with a null value both return $null here. Callers that care must
# ask Test-GxMcpReleaseHasField as well.
function Get-GxMcpReleaseFieldValue {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { return $null }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertTo-GxMcpReleasePath {
    param(
        [AllowNull()][string]$Path,
        [string]$BasePath
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $candidate = $Path.Trim()
    try {
        if (-not [IO.Path]::IsPathRooted($candidate) -and -not [string]::IsNullOrWhiteSpace($BasePath)) {
            $candidate = Join-Path $BasePath $candidate
        }
        $full = [IO.Path]::GetFullPath($candidate)
        if ($full.Length -gt 3) { $full = $full.TrimEnd('\', '/') }
        return $full
    } catch {
        return $candidate
    }
}

function ConvertTo-GxMcpReleaseBoolean {
    param([AllowNull()][object]$Value)

    if ($Value -is [bool]) { return [bool]$Value }
    if ($Value -is [System.Management.Automation.SwitchParameter]) { return $Value.IsPresent }
    if ($Value -is [string]) {
        switch ($Value.Trim().ToLowerInvariant()) {
            'true' { return $true }
            'false' { return $false }
        }
    }
    return $null
}

function ConvertTo-GxMcpReleasePositiveInteger {
    param([AllowNull()][object]$Value)

    if ($Value -is [byte] -or $Value -is [UInt16] -or $Value -is [UInt32] -or
        $Value -is [UInt64] -or $Value -is [sbyte] -or $Value -is [Int16] -or
        $Value -is [Int32] -or $Value -is [Int64]) {
        $number = [decimal]$Value
    } elseif ($Value -is [double] -or $Value -is [decimal]) {
        $number = [decimal]$Value
    } elseif ($Value -is [string] -and $Value.Trim() -match '^\d+$') {
        $number = [decimal]::Parse($Value.Trim(), [Globalization.CultureInfo]::InvariantCulture)
    } else {
        return $null
    }
    if ($number -le 0 -or $number -ne [Math]::Floor($number)) { return $null }
    if ($number -gt [Int64]::MaxValue) { return $null }
    return [int64]$number
}

function Test-GxMcpReleaseCommitId {
    param([AllowNull()][object]$Value)

    return [string]$Value -match '^[0-9a-fA-F]{40,64}$'
}

function Get-GxMcpReleaseTempRoot {
    return [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
}

# Issue #322: compares a path-shaped preflight/summary field across two sides
# that may carry equivalent but differently spelled forms of the same directory
# (8.3 short name vs long name, dot segments, trailing separator). Only rooted
# paths are normalized: a relative value has no stable base, so it is returned
# verbatim and stays a strict comparison.
function Get-GxMcpReleaseComparablePath {
    param([AllowNull()][object]$Value)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $text }
    try {
        if (-not [IO.Path]::IsPathRooted($text)) { return $text }
        return (ConvertTo-GxMcpReleasePath -Path $text)
    } catch {
        return $text
    }
}

function Get-GxMcpPreflightTrxTestCount {
    param([Parameter(Mandatory = $true)][string]$ResultsDirectory)

    if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) { return 0 }
    $total = [int64]0
    $files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File)
    foreach ($trx in $files) {
        try {
            [xml]$document = Get-Content -LiteralPath $trx.FullName -Raw
            $counter = $document.TestRun.ResultSummary.Counters
            $value = ConvertTo-GxMcpReleasePositiveInteger $counter.total
            if ($null -eq $value) { throw 'invalid test count' }
            $total += $value
        } catch {
            throw "Could not read process smoke test result $($trx.Name): $($_.Exception.Message)"
        }
    }
    return $total
}

function Get-GxMcpReleaseProcessSmokePairs {
    return @(
        [pscustomobject]@{
            Source = 'src/GxMcp.Gateway/bin/Release/net10.0-windows/GxMcp.Gateway.exe'
            Publish = 'publish/GxMcp.Gateway.exe'
        },
        [pscustomobject]@{
            Source = 'src/GxMcp.Worker/bin/x86/Release/GxMcp.Worker.exe'
            Publish = 'publish/worker/GxMcp.Worker.exe'
            AlternateSource = 'src/GxMcp.Worker/bin/Release/GxMcp.Worker.exe'
        }
    )
}

function Get-GxMcpReleaseProcessSmokeFingerprint {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $pairs = Get-GxMcpReleaseProcessSmokePairs
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($pair in $pairs) {
        $publishPath = Join-Path $RepositoryRoot ($pair.Publish -replace '/', '\')
        if (-not (Test-Path -LiteralPath $publishPath -PathType Leaf)) { return $null }
        $sourcePaths = @($pair.Source)
        if (Test-GxMcpReleaseHasField -Object $pair -Name 'AlternateSource') { $sourcePaths += [string]$pair.AlternateSource }
        $publishHash = (Get-FileHash -LiteralPath $publishPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $matchedSourceHash = $null
        foreach ($sourceRelative in $sourcePaths) {
            $sourcePath = Join-Path $RepositoryRoot ($sourceRelative -replace '/', '\')
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
            $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($sourceHash -ceq $publishHash) { $matchedSourceHash = $sourceHash; break }
        }
        if ($null -eq $matchedSourceHash) { return $null }
        [void]$parts.Add(('{0}={1}' -f $pair.Publish, $matchedSourceHash))
    }
    $payload = $parts -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

# Issue #329: Get-GxMcpReleaseProcessSmokeFingerprint returns $null for three
# different reasons, and the caller reported all of them with one sentence that
# named neither the cause nor the remedy. This reports each pair's actual state
# so the message can say WHICH file is missing or WHICH bytes differ, and can
# name .\build.ps1 as the remedy for the dominant case: publish\ built before a
# commit, where the SDK's revision stamp makes the rebuilt src\ binary differ.
function Get-GxMcpReleaseProcessSmokeBindingDetail {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $details = New-Object System.Collections.Generic.List[string]
    $missingPublish = $false
    $missingSource = $false
    $mismatched = $false
    foreach ($pair in (Get-GxMcpReleaseProcessSmokePairs)) {
        $publishPath = Join-Path $RepositoryRoot ($pair.Publish -replace '/', '\')
        $sourcePaths = @([string]$pair.Source)
        if (Test-GxMcpReleaseHasField -Object $pair -Name 'AlternateSource') { $sourcePaths += [string]$pair.AlternateSource }
        if (-not (Test-Path -LiteralPath $publishPath -PathType Leaf)) {
            $missingPublish = $true
            [void]$details.Add(('  - {0}: MISSING (expected in publish/)' -f $publishPath))
            continue
        }
        $publishHash = (Get-FileHash -LiteralPath $publishPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $existing = @($sourcePaths | Where-Object {
            Test-Path -LiteralPath (Join-Path $RepositoryRoot ($_ -replace '/', '\')) -PathType Leaf
        })
        if ($existing.Count -eq 0) {
            $missingSource = $true
            [void]$details.Add(('  - {0}: present, but no source build output exists at {1}' -f $publishPath, ($sourcePaths -join ' or ')))
            continue
        }
        $matched = $false
        foreach ($sourceRelative in $existing) {
            $sourcePath = Join-Path $RepositoryRoot ($sourceRelative -replace '/', '\')
            $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($sourceHash -ceq $publishHash) { $matched = $true; break }
        }
        if ($matched) {
            [void]$details.Add(('  - {0}: matches its source build output' -f $publishPath))
            continue
        }
        $mismatched = $true
        $sourcePath = Join-Path $RepositoryRoot (($existing[0] -replace '/', '\'))
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$details.Add(('  - {0}: sha256 {1} does not match {2} sha256 {3}' -f $publishPath, $publishHash, $sourcePath, $sourceHash))
    }
    return [pscustomobject]@{
        missingPublishArtifact = $missingPublish
        missingSourceBuild = $missingSource
        byteMismatch = $mismatched
        lines = @($details)
    }
}

function Format-GxMcpReleaseProcessSmokeBindingFailure {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Detail
    )

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('Process smoke binaries could not be bound to the current publish artifacts.')
    [void]$lines.Add('Compared paths:')
    foreach ($line in $Detail.lines) { [void]$lines.Add([string]$line) }
    $probable = if ($Detail.missingPublishArtifact) {
        'a publish artifact is missing; the process smoke lane certified a build that publish\ does not contain.'
    } elseif ($Detail.missingSourceBuild) {
        'the source build output the comparison needs does not exist; the process smoke lane ran against a different output than the one compared here.'
    } else {
        'publish\ is stale: it was built before the current commit. The SDK stamps the revision into the assembly, so a rebuilt src\... binary no longer matches the published bytes.'
    }
    [void]$lines.Add("Probable cause: $probable")
    [void]$lines.Add('Remedy: run .\build.ps1 from the repository root, then re-run scripts\release-preflight.ps1.')
    return ($lines -join [Environment]::NewLine)
}

# Releases before 3.0.0 predate the checksum, VSIX and npm gitHead contract; every
# major from 3 up must satisfy it.
function Test-GxMcpReleaseLegacyVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    if ($Version.Trim().TrimStart('v') -notmatch '^(?<major>\d+)\.') {
        throw "Release version '$Version' is not semver."
    }
    return [int]$Matches.major -lt 3
}

function Get-GxMcpNextSemVer {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentVersion,
        [Parameter(Mandatory = $true)][ValidateSet('major', 'minor', 'patch')][string]$Bump
    )

    $normalized = $CurrentVersion.Trim().TrimStart('v')
    $match = [regex]::Match($normalized, '^(\d+)\.(\d+)\.(\d+)')
    if (-not $match.Success) {
        throw "Version '$CurrentVersion' is not strict semver (X.Y.Z)."
    }
    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    $patch = [int]$match.Groups[3].Value
    switch ($Bump.ToLowerInvariant()) {
        'major' { return "$($major + 1).0.0" }
        'minor' { return "$major.$($minor + 1).0" }
        'patch' { return "$major.$minor.$($patch + 1)" }
    }
}

function Get-GxMcpReleaseRequiredAssetNames {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [switch]$AllowLegacy
    )

    $versionValue = $Version.TrimStart('v')
    if ($AllowLegacy) { return @('publish.zip') }
    return @('publish.zip', 'publish.zip.sha256', "nexus-ide-$versionValue.vsix")
}

# Issue #323: redaction with no length cap, for artifacts that must keep the
# whole log. This is the single canonical credential-redaction implementation for
# the repository: every other site (release-preflight phase logs, the publication
# verifier, the integration preflight, the diagnostics collector) delegates here
# and keeps only its own length cap. Protect-GxMcpReleaseMessage is this function
# plus the 1200-character cap; the cap has to stay in the shared place because
# release-doctor and release-status rely on it to bound their reports, but the
# union belongs here so a persisted phase log cannot leak a secret that the
# console tail would have masked.
function Protect-GxMcpReleaseText {
    param([AllowNull()][object]$Value)

    # Issue #326: a credential value is a UNIT, not a run of non-space characters.
    # The previous `[^\s,;]+` value class stopped at the first space, so a
    # phrase password, a multi-word bearer token, or any quoted value leaked
    # everything after that space into a persisted phase log and into a public
    # diagnostics bundle. The value now ends at the first structural boundary:
    # a `,`/`;`/`&`/`|`/newline, or the start of the next `name=value` pair. That
    # is a deliberate over-masking limit: prose after a credential on the SAME
    # line with no following pair is masked too, because the boundary between
    # "more secret" and "more sentence" is not recoverable from the text. Text on
    # a later line, and `name=value` pairs that follow the credential, survive.
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $text = $text -replace '(?i)(ghp_|github_pat_|npm_)[A-Za-z0-9_]+', '$1[REDACTED]'

    # Word-bounded so `usertoken=x` and `tokenizer=ok` are not credentials, while
    # `user id`, `userid`, `api_key` and `api-key` are.
    $key = '(?i:token|password|passwd|pwd|secret|api[_-]?key|api[_-]?secret|auth[_-]?token|authorization|connection\s*string|user[\s_-]?id|userid|client[\s_-]?secret|private[\s_-]?key|credential)'
    $quote = '["'']'
    # A quoted value is one unit regardless of what it contains. The quotes are
    # preserved so JSON and connection-string shapes stay readable.
    $text = [regex]::Replace(
        $text,
        '(?<![A-Za-z0-9_-])(' + $key + ')(?![A-Za-z0-9_-])(' + $quote + '?)(\s*[:=]\s*)(' + $quote + ')(?:(?!\4)[^\r\n])*\4',
        '$1$2$3$4[REDACTED]$4')
    $keyValue = [regex]::new(
        '(?<![A-Za-z0-9_-])(' + $key + ')(?![A-Za-z0-9_-])(' + $quote + '?)(\s*[:=]\s*)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $nextPairPattern = [regex]::new('[ \t]+[A-Za-z_][A-Za-z0-9_.-]*([ \t]+[A-Za-z_][A-Za-z0-9_.-]*)*?[ \t]*[:=]')

    $result = New-Object System.Text.StringBuilder
    $cursor = 0
    foreach ($match in $keyValue.Matches($text)) {
        if ($match.Index -lt $cursor) { continue }
        $valueStart = $match.Index + $match.Length
        if ($valueStart -ge $text.Length) { continue }
        # A quoted value was already replaced above; this pass must not mask the
        # closing quote of `"token":"[REDACTED]"`.
        if ($text[$valueStart] -eq '"' -or $text[$valueStart] -eq "'") { continue }
        $length = $text.Length - $valueStart
        # `&` and `|` bound a value in a URL query, a header line, and shell
        # output, all of which land in these logs; treating them as boundaries is
        # what keeps an unrelated trailing token on the same line readable.
        foreach ($delimiter in @(';', ',', '&', '|', "`r", "`n")) {
            $index = $text.IndexOf($delimiter, $valueStart)
            if ($index -ge 0 -and ($index - $valueStart) -lt $length) { $length = $index - $valueStart }
        }
        $nextPair = $nextPairPattern.Match($text, $valueStart, $length)
        if ($nextPair.Success -and $nextPair.Index -gt $valueStart) { $length = $nextPair.Index - $valueStart }
        [void]$result.Append($text.Substring($cursor, $valueStart - $cursor))
        [void]$result.Append('[REDACTED]')
        $cursor = $valueStart + $length
    }
    [void]$result.Append($text.Substring($cursor))
    return $result.ToString()
}

function Protect-GxMcpReleaseMessage {
    param([AllowNull()][object]$Value)

    $text = Protect-GxMcpReleaseText $Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    if ($text.Length -gt 1200) { $text = $text.Substring(0, 1200) + '…' }
    return $text
}

function Get-GxMcpSafeReleaseValue {
    param(
        [AllowNull()][object]$Value,
        [string]$PropertyName = '',
        [int]$Depth = 0
    )

    if ($Depth -gt 20) { return '[REDACTED:maximum-depth]' }
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) {
        if ($PropertyName -match '(?i)(error|diagnostic|detail|message|token|password|pwd|secret|credential|authorization|api.?key|connection.?string)') {
            return Protect-GxMcpReleaseMessage $Value
        }
        return (Protect-GxMcpReleaseMessage $Value)
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[[string]$key] = Get-GxMcpSafeReleaseValue -Value $Value[$key] -PropertyName ([string]$key) -Depth ($Depth + 1)
        }
        return [pscustomobject]$result
    }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $result[$property.Name] = Get-GxMcpSafeReleaseValue -Value $property.Value -PropertyName $property.Name -Depth ($Depth + 1)
        }
        return [pscustomobject]$result
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return @(foreach ($item in $Value) { Get-GxMcpSafeReleaseValue -Value $item -PropertyName $PropertyName -Depth ($Depth + 1) })
    }
    return $Value
}

function Get-GxMcpReleaseArtifactFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$Version,
        [switch]$AllowLegacy
    )

    $relativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($path in @('publish/GxMcp.Gateway.exe', 'publish/worker/GxMcp.Worker.exe', 'publish/tool_definitions.json')) {
        [void]$relativePaths.Add($path)
    }
    if (-not $AllowLegacy) { [void]$relativePaths.Add('publish/nexus-ide.vsix') }
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($relativePath in $relativePaths) {
        $path = Join-Path $RepositoryRoot ($relativePath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$parts.Add(('{0}={1}' -f $relativePath, $hash))
    }
    $payload = $parts -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-GxMcpLocalLiveKbPath {
    param(
        [Parameter(Mandatory = $true)][object]$Catalog,
        [string]$GxPath,
        [string]$KbRoot = 'C:/KBs'
    )

    $major = [string]$Catalog.primaryMajor
    if (-not [string]::IsNullOrWhiteSpace($GxPath)) {
        try {
            $normalizedGxPath = ([IO.Path]::GetFullPath($GxPath)).TrimEnd('\', '/')
            foreach ($entry in @($Catalog.supportedMajors)) {
                if ([string]::IsNullOrWhiteSpace([string]$entry.defaultInstallPath)) { continue }
                $normalizedDefault = ([IO.Path]::GetFullPath([string]$entry.defaultInstallPath)).TrimEnd('\', '/')
                if ([string]::Equals($normalizedGxPath, $normalizedDefault, [StringComparison]::OrdinalIgnoreCase)) {
                    $major = [string]$entry.major
                    break
                }
            }
        } catch { }
        $leaf = Split-Path -Leaf $GxPath
        if ($leaf -match '^GeneXus(?<major>\d+)') { $major = $Matches.major }
    }

    $fixtureName = switch ($major) {
        '17' { 'KBTeste17'; break }
        '18' { 'KBTeste'; break }
        default { "KBTeste$major"; break }
    }
    $candidate = Join-Path $KbRoot $fixtureName
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { return $null }
    return [pscustomobject]@{
        path = (Resolve-Path -LiteralPath $candidate).Path
        major = $major
        source = 'auto-local'
    }
}

function Resolve-GxMcpReleasePreflightInputs {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][object]$Catalog,
        [string]$GxPath,
        [string]$Version,
        [string]$SourceCommit,
        [string[]]$LiveMajors,
        [string[]]$LiveGxPathMap,
        [string]$LiveKbPath,
        [string]$LiveFixtureManifest,
        [switch]$RequireLive,
        [switch]$RequireBuildAll,
        [switch]$SkipLive,
        [switch]$SkipWarningBaseline,
        [AllowNull()][string]$ArtifactFingerprint
    )

    $majorValues = @($LiveMajors | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    if ($majorValues.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:GXMCP_LIVE_MAJORS)) {
        $majorValues = @($env:GXMCP_LIVE_MAJORS -split '[,;\s]+' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    }
    $mapValues = @($LiveGxPathMap | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    if ($mapValues.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:GXMCP_LIVE_GX_PATH_MAP)) {
        $mapValues = @($env:GXMCP_LIVE_GX_PATH_MAP -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    }
    $liveMode = if ($majorValues.Count -gt 0 -or $mapValues.Count -gt 0) { 'matrix' } else { 'single' }

    $liveKbSource = if ([string]::IsNullOrWhiteSpace($LiveKbPath)) { 'none' } else { 'explicit' }
    if ([string]::IsNullOrWhiteSpace($LiveKbPath) -and -not [string]::IsNullOrWhiteSpace($env:GXMCP_TEST_KB)) {
        $LiveKbPath = $env:GXMCP_TEST_KB
        $liveKbSource = 'environment'
    } elseif ([string]::IsNullOrWhiteSpace($LiveKbPath) -and $liveMode -eq 'single') {
        $localFixture = Get-GxMcpLocalLiveKbPath -Catalog $Catalog -GxPath $GxPath
        if ($null -ne $localFixture) {
            $LiveKbPath = [string]$localFixture.path
            $liveKbSource = [string]$localFixture.source
        }
    } elseif ([string]::IsNullOrWhiteSpace($LiveKbPath) -and $liveMode -eq 'matrix') {
        $liveKbSource = 'matrix-explicit-required'
    }

    $fixtureSource = if ([string]::IsNullOrWhiteSpace($LiveFixtureManifest)) { 'none' } else { 'explicit' }
    if ([string]::IsNullOrWhiteSpace($LiveFixtureManifest) -and -not [string]::IsNullOrWhiteSpace($env:GXMCP_TEST_FIXTURE)) {
        $LiveFixtureManifest = $env:GXMCP_TEST_FIXTURE
        $fixtureSource = 'environment'
    }
    $requireBuildAllValue = [bool]$RequireBuildAll -or $env:GXMCP_REQUIRE_LIVE_BUILD_ALL -eq '1'
    $normalizedRoot = ConvertTo-GxMcpReleasePath -Path $Root
    $normalizedGxPath = ConvertTo-GxMcpReleasePath -Path $GxPath -BasePath $normalizedRoot
    $normalizedLiveKbPath = ConvertTo-GxMcpReleasePath -Path $LiveKbPath -BasePath $normalizedRoot
    $normalizedFixturePath = ConvertTo-GxMcpReleasePath -Path $LiveFixtureManifest -BasePath $normalizedRoot

    return [pscustomobject][ordered]@{
        root = $normalizedRoot
        version = [string]$Version
        sourceCommit = [string]$SourceCommit
        gxPath = $normalizedGxPath
        liveKbPath = $normalizedLiveKbPath
        liveKbSource = $liveKbSource
        liveFixtureManifest = $normalizedFixturePath
        liveFixtureSource = $fixtureSource
        liveMode = $liveMode
        liveMajors = @($majorValues)
        liveGxPathMap = @($mapValues)
        requireLive = [bool]$RequireLive
        requireBuildAll = [bool]$requireBuildAllValue
        skipLive = [bool]$SkipLive
        skipWarningBaseline = [bool]$SkipWarningBaseline
        artifactFingerprint = if ([string]::IsNullOrWhiteSpace($ArtifactFingerprint)) { $null } else { [string]$ArtifactFingerprint }
    }
}

function Test-GxMcpReleasePreflightCompatibility {
    param(
        [Parameter(Mandatory = $true)][object]$Summary,
        [Parameter(Mandatory = $true)][object]$Expected,
        [switch]$RequirePassed
    )

    $requiredProperties = @(
        'root', 'version', 'sourceCommit', 'gxPath', 'liveKbPath', 'liveKbSource',
        'liveFixtureManifest', 'liveFixtureSource', 'liveMode', 'liveMajors',
        'liveGxPathMap', 'requireLive', 'requireBuildAll', 'skipLive',
        'skipWarningBaseline', 'artifactFingerprint'
    )
    foreach ($propertyName in $requiredProperties) {
        if (-not (Test-GxMcpReleaseHasField -Object $Summary -Name $propertyName)) { return $false }
    }
    if ($Summary.schemaVersion -ne 'gxmcp-release-preflight/1') { return $false }
    if ([string]$Summary.status -notin @('failed', 'running', 'passed')) { return $false }
    if ($RequirePassed -and [string]$Summary.status -ne 'passed') { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$Expected.artifactFingerprint) -or
        [string]::IsNullOrWhiteSpace([string]$Summary.artifactFingerprint) -or
        [string]$Summary.artifactFingerprint -cne [string]$Expected.artifactFingerprint) {
        return $false
    }

    $summaryRequireLive = ConvertTo-GxMcpReleaseBoolean $Summary.requireLive
    $summaryRequireBuildAll = ConvertTo-GxMcpReleaseBoolean $Summary.requireBuildAll
    $summarySkipLive = ConvertTo-GxMcpReleaseBoolean $Summary.skipLive
    $summarySkipWarning = ConvertTo-GxMcpReleaseBoolean $Summary.skipWarningBaseline
    $expectedRequireLive = ConvertTo-GxMcpReleaseBoolean $Expected.requireLive
    $expectedRequireBuildAll = ConvertTo-GxMcpReleaseBoolean $Expected.requireBuildAll
    $expectedSkipLive = ConvertTo-GxMcpReleaseBoolean $Expected.skipLive
    $expectedSkipWarning = ConvertTo-GxMcpReleaseBoolean $Expected.skipWarningBaseline
    if ($null -eq $summaryRequireLive -or $null -eq $summaryRequireBuildAll -or
        $null -eq $summarySkipLive -or $null -eq $summarySkipWarning -or
        $null -eq $expectedRequireLive -or $null -eq $expectedRequireBuildAll -or
        $null -eq $expectedSkipLive -or $null -eq $expectedSkipWarning) {
        return $false
    }

    $summaryMajors = @($Summary.liveMajors | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $expectedMajors = @($Expected.liveMajors | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $summaryMap = @($Summary.liveGxPathMap | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $expectedMap = @($Expected.liveGxPathMap | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique)

    return (Get-GxMcpReleaseComparablePath $Summary.root) -eq (Get-GxMcpReleaseComparablePath $Expected.root) -and
        [string]$Summary.version -eq [string]$Expected.version -and
        [string]$Summary.sourceCommit -eq [string]$Expected.sourceCommit -and
        (Get-GxMcpReleaseComparablePath $Summary.gxPath) -eq (Get-GxMcpReleaseComparablePath $Expected.gxPath) -and
        (Get-GxMcpReleaseComparablePath $Summary.liveKbPath) -eq (Get-GxMcpReleaseComparablePath $Expected.liveKbPath) -and
        [string]$Summary.liveKbSource -eq [string]$Expected.liveKbSource -and
        (Get-GxMcpReleaseComparablePath $Summary.liveFixtureManifest) -eq (Get-GxMcpReleaseComparablePath $Expected.liveFixtureManifest) -and
        [string]$Summary.liveFixtureSource -eq [string]$Expected.liveFixtureSource -and
        [string]$Summary.liveMode -eq [string]$Expected.liveMode -and
        ($summaryMajors -join "`n") -ceq ($expectedMajors -join "`n") -and
        ($summaryMap -join "`n") -ceq ($expectedMap -join "`n") -and
        $summaryRequireLive -eq $expectedRequireLive -and
        $summaryRequireBuildAll -eq $expectedRequireBuildAll -and
        $summarySkipLive -eq $expectedSkipLive -and
        $summarySkipWarning -eq $expectedSkipWarning
}

function Get-GxMcpMandatoryPreflightPhaseNames {
    return @(
        'release metadata parity',
        'tool contract validation',
        'operation contract inventory',
        'v3 plan readiness',
        'warning baseline documentation parity',
        'Python script tests',
        'PowerShell script tests',
        'CLI tests',
        'CLI lint',
        'Nexus IDE checks',
        'solution build and tests',
        'solution process smoke tests',
        'Release warning baseline',
        'live KB gate'
    )
}

function Test-GxMcpReleasePreflightPhaseCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][object]$Command,
        [switch]$AllowSkipped
    )

    $text = [string]$Command
    if ([string]::IsNullOrWhiteSpace($text) -or $text -match '[&|`>]') { return $false }
    if ($AllowSkipped -and $text -match '^pwsh(?:\s|$)') { return $true }
    $required = switch ($Name) {
        'release metadata parity' { @('verify-release-metadata.py', '--version') }
        'tool contract validation' { @('validate-tool-contracts.py') }
        'operation contract inventory' { @('generate-operation-contract-inventory.py', '--check') }
        'v3 plan readiness' { @('validate-v3-plan.py', '--require-ready') }
        'warning baseline documentation parity' { @('check-build-warning-baseline.ps1', '-ValidateOnly') }
        'Python script tests' { @('unittest discover', 'scripts[\\/]tests') }
        'PowerShell script tests' { @('run-release-script-tests.ps1') }
        'CLI tests' { @('npm test') }
        'CLI lint' { @('npm run lint') }
        'Nexus IDE checks' { @('npm', '--prefix', 'run check') }
        'solution build and tests' { @('dotnet test', 'Category!=ProcessSmoke', '-c Release') }
        'solution process smoke tests' { @('dotnet test', 'Category=ProcessSmoke', '--no-build', '--no-restore', '--logger', '--results-directory') }
        'Release warning baseline' { @('check-build-warning-baseline.ps1') }
        'live KB gate' { @('pwsh') }
        default { @() }
    }
    foreach ($marker in @($required)) {
        if ($text -notmatch [regex]::Escape($marker) -and $text -notmatch $marker) { return $false }
    }
    if ($Name -eq 'solution process smoke tests' -and $text -notmatch 'process-smoke') { return $false }
    if ($Name -eq 'live KB gate' -and $text -match 'pwsh\s*$' -and -not $AllowSkipped) { return $false }
    if ($Name -eq 'live KB gate' -and $text -notmatch 'test-live(?:-matrix)?\.ps1' -and -not $AllowSkipped) { return $false }
    return $true
}

function Test-GxMcpReleasePreflightCertificate {
    param(
        [Parameter(Mandatory = $true)][object]$Summary,
        [Parameter(Mandatory = $true)][object]$Expected
    )

    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $Summary -Expected $Expected -RequirePassed)) { return $false }
    if (-not (Test-GxMcpReleaseHasField -Object $Summary -Name 'processSmokeMode') -or
        [string]$Summary.processSmokeMode -ne 'serial-after-parallel' -or
        -not (Test-GxMcpReleaseHasField -Object $Summary -Name 'processSmokeTestCount') -or
        -not (Test-GxMcpReleaseHasField -Object $Summary -Name 'processSmokeResultsPath') -or
        -not (Test-GxMcpReleaseHasField -Object $Summary -Name 'processSmokeBinaryFingerprint') -or
        -not (Test-GxMcpReleaseHasField -Object $Summary -Name 'phases')) {
        return $false
    }
    $testCount = ConvertTo-GxMcpReleasePositiveInteger $Summary.processSmokeTestCount
    if ($null -eq $testCount) { return $false }
    $resultsPath = [string]$Summary.processSmokeResultsPath
    if ([string]::IsNullOrWhiteSpace($resultsPath)) { return $false }
    try {
        # Issue #322: normalize both sides through the same helper so a short
        # (8.3) form of the temp root is not rejected against its long form.
        $fullResultsPath = Get-GxMcpReleaseComparablePath $resultsPath
        $tempRoot = Get-GxMcpReleaseComparablePath (Get-GxMcpReleaseTempRoot)
        if (-not [IO.Path]::IsPathRooted($fullResultsPath)) { return $false }
        if (-not $fullResultsPath.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ((Get-GxMcpPreflightTrxTestCount -ResultsDirectory $fullResultsPath) -ne $testCount) { return $false }
    } catch { return $false }

    $processFingerprint = [string]$Summary.processSmokeBinaryFingerprint
    $currentProcessFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot ([string]$Expected.root)
    if ($processFingerprint -notmatch '^[0-9a-f]{64}$' -or $null -eq $currentProcessFingerprint -or
        $processFingerprint -cne $currentProcessFingerprint) { return $false }

    $phases = @($Summary.phases)
    $mandatoryNames = @(Get-GxMcpMandatoryPreflightPhaseNames)
    if ($phases.Count -ne $mandatoryNames.Count) { return $false }
    $requireLive = ConvertTo-GxMcpReleaseBoolean $Summary.requireLive
    $requireBuildAll = ConvertTo-GxMcpReleaseBoolean $Summary.requireBuildAll
    $skipWarning = ConvertTo-GxMcpReleaseBoolean $Summary.skipWarningBaseline
    foreach ($name in $mandatoryNames) {
        $phaseMatches = @($phases | Where-Object { [string]$_.name -ceq $name })
        if ($phaseMatches.Count -ne 1) { return $false }
        $phase = $phaseMatches[0]
        $status = [string]$phase.status
        $allowSkipped = ($name -eq 'live KB gate' -and -not ($requireLive -or $requireBuildAll)) -or
            ($name -eq 'Release warning baseline' -and $skipWarning)
        $allowed = if ($name -eq 'live KB gate') {
            if ($requireLive -or $requireBuildAll) { @('passed') } else { @('passed', 'unavailable', 'skipped') }
        } elseif ($name -eq 'Release warning baseline' -and $skipWarning) {
            @('passed', 'skipped')
        } else { @('passed') }
        $exitCodeValid = $status -in @('passed', 'skipped') -and (Test-GxMcpReleaseHasField -Object $phase -Name 'exitCode') -and [int]$phase.exitCode -eq 0
        if ($status -eq 'unavailable' -and $name -eq 'live KB gate') {
            $exitCodeValid = (Test-GxMcpReleaseHasField -Object $phase -Name 'exitCode') -and [int]$phase.exitCode -eq 2
        }
        if ($status -notin $allowed -or -not $exitCodeValid) { return $false }
        if (-not (Test-GxMcpReleasePreflightPhaseCommand -Name $name -Command $phase.command -AllowSkipped:$allowSkipped)) { return $false }
    }
    return $true
}

function ConvertTo-GxMcpReleaseDateTimeOffset {
    param([AllowNull()][object]$Value)

    if ($Value -is [DateTimeOffset]) { return [DateTimeOffset]$Value }
    if ($Value -is [DateTime]) {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
            $dateTime = [DateTime]::SpecifyKind($dateTime, [DateTimeKind]::Utc)
        }
        return [DateTimeOffset]::new($dateTime.ToUniversalTime())
    }
    $parsed = [DateTimeOffset]::MinValue
    $text = [string]$Value
    if (-not [string]::IsNullOrWhiteSpace($text) -and
        [DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) {
        return $parsed
    }
    return [DateTimeOffset]::MinValue
}

function Get-GxMcpReleaseAssetVerification {
    param(
        [AllowNull()][object[]]$Assets,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [switch]$AllowLegacy
    )

    $versionValue = $Version.TrimStart('v')
    $required = [ordered]@{}
    foreach ($name in (Get-GxMcpReleaseRequiredAssetNames -Version $versionValue -AllowLegacy:$AllowLegacy)) {
        $required[$name] = $name
    }
    $details = New-Object System.Collections.Generic.List[object]
    $latestUpdated = [DateTimeOffset]::MinValue
    $errors = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $required.GetEnumerator()) {
        $assetMatches = @($Assets | Where-Object { [string]$_.name -ceq [string]$entry.Key })
        if ($assetMatches.Count -ne 1) {
            [void]$errors.Add("$($entry.Key): expected one remote asset, found $($assetMatches.Count)")
            continue
        }
        $asset = $assetMatches[0]
        $localPath = Join-Path $RepositoryRoot ([string]$entry.Value -replace '/', '\')
        $digest = [string]$asset.digest
        if ([string]::IsNullOrWhiteSpace($localPath) -or -not (Test-Path -LiteralPath $localPath -PathType Leaf)) {
            [void]$errors.Add("$($entry.Key): local source artifact is missing")
            continue
        }
        $localItem = Get-Item -LiteralPath $localPath
        $localHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $remoteHash = if ($digest -match '^sha256:(?<hash>[0-9a-fA-F]{64})$') { $Matches.hash.ToLowerInvariant() } else { $null }
        if ([string]$asset.state -cne 'uploaded') { [void]$errors.Add("$($entry.Key): remote state is '$($asset.state)'") }
        if ([int64]$asset.size -ne [int64]$localItem.Length) { [void]$errors.Add("$($entry.Key): remote size does not match local bytes") }
        if ($null -eq $remoteHash) {
            if (-not $AllowLegacy) { [void]$errors.Add("$($entry.Key): remote SHA-256 does not match local bytes") }
        } elseif ($remoteHash -cne $localHash) {
            [void]$errors.Add("$($entry.Key): remote SHA-256 does not match local bytes")
        }
        $updated = ConvertTo-GxMcpReleaseDateTimeOffset $asset.updatedAt
        if ($updated -ne [DateTimeOffset]::MinValue) {
            if ($updated -gt $latestUpdated) { $latestUpdated = $updated }
        } else {
            [void]$errors.Add("$($entry.Key): remote updatedAt is missing or invalid")
        }
        [void]$details.Add([pscustomobject][ordered]@{
            name = [string]$entry.Key
            size = [int64]$asset.size
            digest = $digest
            state = [string]$asset.state
            updatedAt = $updated.ToUniversalTime().ToString('o')
        })
    }
    $isValid = $errors.Count -eq 0
    $latestText = if ($latestUpdated -eq [DateTimeOffset]::MinValue) { $null } else { $latestUpdated.UtcDateTime.ToString('o') }
    $errorText = if ($errors.Count -eq 0) { $null } else { ($errors -join '; ') }
    $assetArray = @($details.ToArray())
    return [pscustomobject][ordered]@{
        isValid = $isValid
        assets = $assetArray
        latestUpdatedAtUtc = $latestText
        error = $errorText
    }
}

function Test-GxMcpReleaseIssueSnapshotShape {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($Snapshot.schema -ne 'gxmcp-release-issues/1') { return [pscustomobject]@{ Valid = $false; Error = 'snapshot schema is invalid' } }
    if (-not (Test-GxMcpReleaseHasField -Object $Snapshot -Name 'version') -or -not (Test-GxMcpReleaseHasField -Object $Snapshot -Name 'tag') -or
        -not (Test-GxMcpReleaseHasField -Object $Snapshot -Name 'issues') -or $null -eq $Snapshot.issues) {
        return [pscustomobject]@{ Valid = $false; Error = 'snapshot issues must be a non-null array' }
    }
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($record in @($Snapshot.issues)) {
        if ($null -eq $record -or -not (Test-GxMcpReleaseHasField -Object $record -Name 'number') -or [int]$record.number -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$record.title) -or [string]::IsNullOrWhiteSpace([string]$record.url) -or
            -not $seen.Add([int]$record.number)) {
            return [pscustomobject]@{ Valid = $false; Error = 'snapshot contains an invalid or duplicate issue record' }
        }
    }
    return [pscustomobject]@{ Valid = $true; Error = $null }
}

function Test-GxMcpReleasePublicationEvidence {
    param(
        [Parameter(Mandatory = $true)][object]$Status,
        [AllowNull()][object]$Publication,
        [string]$RepositoryRoot,
        [string]$Repository = 'lennix1337/Genexus18MCP',
        [switch]$AllowLegacy
    )

    if ($null -eq $Publication) { return [pscustomobject]@{ Valid = $false; Error = 'publication evidence file is missing' } }
    if ($Publication.schemaVersion -ne 'gxmcp-release-publication/1') { return [pscustomobject]@{ Valid = $false; Error = 'publication evidence schema is invalid' } }
    if ([string]$Publication.state -ne 'verified') { return [pscustomobject]@{ Valid = $false; Error = "publication evidence state is '$($Publication.state)'" } }
    if ([string]::IsNullOrWhiteSpace([string]$Status.tag) -or [string]::IsNullOrWhiteSpace([string]$Status.version) -or
        [string]$Publication.tag -cne [string]$Status.tag -or [string]$Publication.version -cne [string]$Status.version) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence tag or version does not match status' }
    }
    if ((Test-GxMcpReleaseHasField -Object $Status -Name 'repository') -and -not [string]::IsNullOrWhiteSpace([string]$Status.repository) -and
        [string]$Status.repository -cne $Repository) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence repository does not match status' }
    }
    $commits = @([string]$Publication.expectedCommit, [string]$Publication.sourceCommit, [string]$Publication.tagCommit)
    if (@($commits | Where-Object { -not (Test-GxMcpReleaseCommitId $_) }).Count -gt 0) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence is missing a valid source commit' }
    }
    if (@($commits | Select-Object -Unique).Count -ne 1 -or
        -not (Test-GxMcpReleaseHasField -Object $Status -Name 'tagCommit') -or
        -not (Test-GxMcpReleaseCommitId $Status.tagCommit) -or [string]$Status.tagCommit -cne $commits[0]) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence commits do not match' }
    }
    $expectedReleaseUrl = "https://github.com/$Repository/releases/tag/$($Status.tag)"
    $runId = [string]$Publication.workflowRunId
    if ($runId -notmatch '^\d+$' -or [string]$Publication.workflowStatus -cne 'completed' -or
        [string]$Publication.workflowConclusion -cne 'success') {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence workflow completion is invalid' }
    }
    $expectedWorkflowUrl = "https://github.com/$Repository/actions/runs/$runId"
    if ([string]$Publication.workflowUrl -cne $expectedWorkflowUrl) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence workflow URL is invalid' }
    }
    if ([string]$Publication.npmVersion -cne [string]$Status.version -or
        [string]$Publication.releaseUrl -cne $expectedReleaseUrl) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence npm or release URL is invalid' }
    }
    if (-not $AllowLegacy -and (-not (Test-GxMcpReleaseCommitId $Publication.npmGitHead) -or
        [string]$Publication.npmGitHead -cne $commits[0])) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence npm commit binding is invalid' }
    }
    if ((ConvertTo-GxMcpReleaseDateTimeOffset $Publication.releasePublishedAtUtc) -eq [DateTimeOffset]::MinValue) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence release timestamp is invalid' }
    }

    $assets = @($Publication.assets)
    $requiredAssets = @(Get-GxMcpReleaseRequiredAssetNames -Version ([string]$Status.version) -AllowLegacy:$AllowLegacy)
    $latestUpdated = [DateTimeOffset]::MinValue
    foreach ($name in $requiredAssets) {
        $assetMatches = @($assets | Where-Object { [string]$_.name -ceq $name })
        if ($assetMatches.Count -ne 1) {
            return [pscustomobject]@{ Valid = $false; Error = "publication evidence asset '$name' is invalid" }
        }
        $asset = $assetMatches[0]
        $digestText = [string]$asset.digest
        $digestValid = $digestText -match '^sha256:[0-9a-fA-F]{64}$'
        if ([string]$asset.state -cne 'uploaded' -or
            -not (Test-GxMcpReleaseHasField -Object $asset -Name 'size') -or
            [int64]$asset.size -le 0 -or (-not $AllowLegacy -and -not $digestValid)) {
            return [pscustomobject]@{ Valid = $false; Error = "publication evidence asset '$name' is invalid" }
        }
        $updated = ConvertTo-GxMcpReleaseDateTimeOffset $asset.updatedAt
        if ($updated -eq [DateTimeOffset]::MinValue) {
            return [pscustomobject]@{ Valid = $false; Error = "publication evidence asset '$name' timestamp is invalid" }
        }
        if ($updated -gt $latestUpdated) { $latestUpdated = $updated }
    }
    $assetsUpdated = ConvertTo-GxMcpReleaseDateTimeOffset $Publication.assetsUpdatedAtUtc
    if ($assetsUpdated -eq [DateTimeOffset]::MinValue -or $assetsUpdated -ne $latestUpdated) {
        return [pscustomobject]@{ Valid = $false; Error = 'publication evidence asset update timestamp is invalid' }
    }
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $assetCheck = Get-GxMcpReleaseAssetVerification -Assets $assets -RepositoryRoot $RepositoryRoot -Version ([string]$Status.version) -AllowLegacy:$AllowLegacy
        if (-not $assetCheck.isValid) {
            return [pscustomobject]@{ Valid = $false; Error = "publication evidence assets do not match local bytes: $($assetCheck.error)" }
        }
    }
    return [pscustomobject]@{ Valid = $true; Error = $null }
}

function Test-GxMcpReleaseWorkflowRun {
    param(
        [Parameter(Mandatory = $true)][object]$Run,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$Tag,
        [string]$MinimumCreatedAtUtc,
        [switch]$AllowDispatch
    )

    $allowedEvents = @('release')
    if ($AllowDispatch) { $allowedEvents += 'workflow_dispatch' }
    if (-not [string]::IsNullOrWhiteSpace($MinimumCreatedAtUtc)) {
        $minimum = ConvertTo-GxMcpReleaseDateTimeOffset $MinimumCreatedAtUtc
        $created = ConvertTo-GxMcpReleaseDateTimeOffset $Run.createdAt
        if ($minimum -eq [DateTimeOffset]::MinValue -or $created -eq [DateTimeOffset]::MinValue -or $created -lt $minimum) {
            return $false
        }
    }
    return [string]$Run.headSha -ceq $ExpectedCommit -and
        [string]$Run.headBranch -ceq $Tag -and
        [string]$Run.event -cin $allowedEvents
}

# The release workflow (scripts/ci/verify-release-commit.ps1) hard-requires this check from
# GitHub Actions on the exact release SHA, whatever branch protection currently exposes.
# Checking it locally means a red or missing result stops the release before anything is
# tagged instead of after the tag and GitHub Release already exist.
$script:GxMcpReleaseWorkflowCheck = 'test'
$script:GxMcpReleaseWorkflowAppId = 15368

# Required status-check names for the release branch. Classic branch protection
# (GET branches/main -> .protection) and repository rulesets (GET rules/branches/main)
# are both readable without admin rights; either may be the one in force, so the
# union is required. The workflow check is always included, so protection that is hidden
# from the token or later removed cannot turn this into "nothing required". Emits a
# sorted, unique list; callers wrap it in @().
function Get-GxMcpReleaseRequiredCheckContexts {
    param(
        [AllowNull()][object]$Branch,
        [AllowNull()][object]$Rules
    )

    $contexts = New-Object System.Collections.Generic.List[string]
    [void]$contexts.Add($script:GxMcpReleaseWorkflowCheck)
    $protection = Get-GxMcpReleaseFieldValue -Object $Branch -Name 'protection'
    $classic = Get-GxMcpReleaseFieldValue -Object $protection -Name 'required_status_checks'
    foreach ($name in @(Get-GxMcpReleaseFieldValue -Object $classic -Name 'contexts')) { if ($name) { [void]$contexts.Add([string]$name) } }
    foreach ($check in @(Get-GxMcpReleaseFieldValue -Object $classic -Name 'checks')) {
        $name = Get-GxMcpReleaseFieldValue -Object $check -Name 'context'
        if ($name) { [void]$contexts.Add([string]$name) }
    }
    foreach ($rule in @($Rules)) {
        if ([string](Get-GxMcpReleaseFieldValue -Object $rule -Name 'type') -cne 'required_status_checks') { continue }
        $parameters = Get-GxMcpReleaseFieldValue -Object $rule -Name 'parameters'
        foreach ($check in @(Get-GxMcpReleaseFieldValue -Object $parameters -Name 'required_status_checks')) {
            $name = Get-GxMcpReleaseFieldValue -Object $check -Name 'context'
            if ($name) { [void]$contexts.Add([string]$name) }
        }
    }
    return @($contexts | Sort-Object -Unique)
}

# Decides whether every required context is green on one commit, with the same rules as the
# release workflow: the newest run by started_at wins, and the workflow check must be a
# completed/success run created by GitHub Actions (app 15368) bound to exactly this commit.
# neutral, skipped, a run from another app, or a commit status cannot stand in for it, and
# an in-progress or absent context is not green. CheckRuns are objects with
# id/name/status/conclusion/app_id/started_at/head_sha; Statuses have context/state.
function Test-GxMcpReleaseRequiredChecks {
    param(
        [string[]]$RequiredContexts,
        [AllowNull()][object[]]$CheckRuns,
        [AllowNull()][object[]]$Statuses,
        [Parameter(Mandatory = $true)][string]$Commit
    )

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($context in @($RequiredContexts)) {
        $isWorkflowCheck = $context -ceq $script:GxMcpReleaseWorkflowCheck
        $runs = @($CheckRuns | Where-Object {
            $null -ne $_ -and [string]$_.name -ceq $context -and
            (-not $isWorkflowCheck -or [string]$_.app_id -ceq [string]$script:GxMcpReleaseWorkflowAppId)
        } | Sort-Object `
            @{ Expression = { ConvertTo-GxMcpReleaseDateTimeOffset $_.started_at }; Descending = $true }, `
            @{ Expression = { [int64]$_.id }; Descending = $true })
        $statusEntries = @($Statuses | Where-Object { $null -ne $_ -and [string]$_.context -ceq $context })
        if ($runs.Count -gt 0) {
            $latest = $runs[0]
            if ([string]$latest.head_sha -cne $Commit) { [void]$problems.Add("$context is bound to $($latest.head_sha), not $Commit") }
            elseif ([string]$latest.status -cne 'completed') { [void]$problems.Add("$context is $($latest.status)") }
            elseif ([string]$latest.conclusion -cne 'success') { [void]$problems.Add("$context concluded $($latest.conclusion)") }
        } elseif ($isWorkflowCheck) {
            [void]$problems.Add("$context has no check run from GitHub Actions (app $($script:GxMcpReleaseWorkflowAppId))")
        } elseif ($statusEntries.Count -gt 0) {
            if ([string]$statusEntries[0].state -cne 'success') { [void]$problems.Add("$context state is $($statusEntries[0].state)") }
        } else {
            [void]$problems.Add("$context has no result")
        }
    }
    return [pscustomobject]@{
        Valid = $problems.Count -eq 0
        Problems = @($problems)
        Error = if ($problems.Count -eq 0) { $null } else { 'required checks are not green: ' + ($problems -join '; ') }
    }
}

function Get-GxMcpReleaseRequiredChecksReport {
    param([Parameter(Mandatory = $true)][string]$Commit)

    $branchRaw = @(gh api 'repos/{owner}/{repo}/branches/main' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $branchRaw.Count -eq 0) {
        return [pscustomobject]@{ Valid = $false; Error = 'Could not read the main branch protection; refusing to publish without verifying required CI.'; Required = @(); CheckRuns = @(); Statuses = @() }
    }
    $rulesRaw = @(gh api 'repos/{owner}/{repo}/rules/branches/main' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Valid = $false; Error = 'Could not read the main branch rules; refusing to publish without verifying required CI.'; Required = @(); CheckRuns = @(); Statuses = @() }
    }
    $rules = if ($rulesRaw.Count -gt 0) { ($rulesRaw -join [Environment]::NewLine) | ConvertFrom-Json } else { @() }
    $required = @(Get-GxMcpReleaseRequiredCheckContexts -Branch (($branchRaw -join [Environment]::NewLine) | ConvertFrom-Json) -Rules $rules)
    $runLines = @(gh api --paginate "repos/{owner}/{repo}/commits/$Commit/check-runs?per_page=100" --jq '.check_runs[] | [.id,.name,.status,(.conclusion // ""),(.app.id // 0),(.started_at // ""),.head_sha] | @tsv' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Valid = $false; Error = "Could not read check runs for $Commit. A commit GitHub does not know yet has none: run release.ps1 -PrepareOnly, push that SHA as a candidate branch/PR so CI runs on it, then rerun."; Required = $required; CheckRuns = @(); Statuses = @() }
    }
    $statusLines = @(gh api "repos/{owner}/{repo}/commits/$Commit/status?per_page=100" --jq '.statuses[] | [.context,.state] | @tsv' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Valid = $false; Error = "Could not read commit statuses for $Commit."; Required = $required; CheckRuns = @(); Statuses = @() }
    }
    $checkRuns = @($runLines | Where-Object { $_ } | ForEach-Object {
        $parts = ([string]$_) -split "`t"
        if ($parts.Count -lt 7) {
            return [pscustomobject]@{ Valid = $false; Error = "Unexpected check-run record for $Commit; refusing to publish without verifying required CI."; Required = $required; CheckRuns = @(); Statuses = @() }
        }
        [pscustomobject]@{ id = $parts[0]; name = $parts[1]; status = $parts[2]; conclusion = $parts[3]; app_id = $parts[4]; started_at = $parts[5]; head_sha = $parts[6] }
    })
    $statuses = @($statusLines | Where-Object { $_ } | ForEach-Object {
        $parts = ([string]$_) -split "`t"
        [pscustomobject]@{ context = $parts[0]; state = $parts[1] }
    })
    $verdict = Test-GxMcpReleaseRequiredChecks -RequiredContexts $required -CheckRuns $checkRuns -Statuses $statuses -Commit $Commit
    return [pscustomobject]@{
        Valid = [bool]$verdict.Valid
        Error = if ($verdict.Valid) { $null } else { "Required CI is not green on $Commit ($($verdict.Error)). Wait for or fix CI, then rerun the same version. For an unpublished release commit, run release.ps1 -PrepareOnly, push that SHA as a candidate branch/PR so CI runs on it, then rerun." }
        Required = $required
        CheckRuns = $checkRuns
        Statuses = $statuses
    }
}

function Get-GxMcpVsCodeUpdateMutexName {
    <#
      .SYNOPSIS
        Resolves the mutex name VS Code checks when it refuses to start mid-update.
      .DESCRIPTION
        Mirrors src\nexus-ide\src\test\runTest.ts so the probe inspects the runtime
        the phase actually launches: the version comes from NEXUS_IDE_VSCODE_VERSION
        and falls back to 1.111.0, the cache root is <extension>\.vscode-test, and
        the runtime unpacks to vscode-win32-x64-archive-<version>\<hash>\resources\app.
        Only the top-level win32MutexName counts. The key also appears inside the
        "embedded" object for the Sessions sub-product, and VS Code's
        checkInnoSetupMutex gates on the top-level value. Windows-only: returns
        $null everywhere else, and on any unresolvable layout, so the caller can
        report "not confirmed" instead of guessing.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [AllowNull()][string]$Version
    )

    if ($env:OS -ne 'Windows_NT') { return $null }
    $effectiveVersion = if ([string]::IsNullOrWhiteSpace($Version)) { [string]$env:NEXUS_IDE_VSCODE_VERSION } else { $Version }
    if ([string]::IsNullOrWhiteSpace($effectiveVersion)) { $effectiveVersion = '1.111.0' }
    $archiveRoot = Join-Path $RepositoryRoot "src\nexus-ide\.vscode-test\vscode-win32-x64-archive-$effectiveVersion"
    if (-not (Test-Path -LiteralPath $archiveRoot -PathType Container)) { return $null }

    $products = @(Get-ChildItem -LiteralPath $archiveRoot -Recurse -Filter 'product.json' -File -ErrorAction SilentlyContinue)
    foreach ($product in $products) {
        if ([string]$product.DirectoryName -notmatch 'resources[\\/]app$') { continue }
        $parsed = $null
        try { $parsed = Get-Content -LiteralPath $product.FullName -Raw | ConvertFrom-Json } catch { continue }
        $mutexName = [string]$parsed.win32MutexName
        if (-not [string]::IsNullOrWhiteSpace($mutexName)) { return "$mutexName-updating" }
    }
    return $null
}

function Test-GxMcpVsCodeUpdateMutexHeld {
    <#
      .SYNOPSIS
        Probes whether a named mutex currently exists on this host.
      .DESCRIPTION
        Returns $true when the mutex is held, $false when it is free, and $null
        when the answer cannot be established (non-Windows, blank name, or a
        platform error). Callers must treat $null as "unconfirmed".
    #>
    param(
        [Parameter(Mandatory = $true)][AllowNull()][string]$MutexName
    )

    if ($env:OS -ne 'Windows_NT' -or [string]::IsNullOrWhiteSpace($MutexName)) { return $null }
    $createdNew = $false
    $opened = $false
    try {
        $opened = [System.Threading.Mutex]::TryOpenExisting($MutexName, [ref]$createdNew)
    } catch {
        return $null
    } finally {
        if ($opened) { try { $opened.Dispose() } catch { } }
    }
    return [bool]$opened
}

function Get-GxMcpPreflightHostBlocker {
    <#
      .SYNOPSIS
        Labels a failed preflight phase whose output identifies host state.
      .DESCRIPTION
        Pure classification. It never changes status, exitCode, or whether the
        command runs: a consumer may label the cause, never the outcome. The
        preflight aggregate already accepts 'unavailable' as an approved terminal
        status, so downgrading a phase here would certify a release that ran no
        Electron test at all.

        The trigger is a specific signature in the phase's own captured output;
        an optional probe only adds confirmation. Inject -MutexProbe in tests.
        Returns $null when nothing matches, otherwise a details object carrying
        code, cause, remediation, evidence and mutexConfirmed.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][AllowEmptyString()][string]$Stdout = '',
        [AllowNull()][AllowEmptyString()][string]$Stderr = '',
        [string]$RepositoryRoot,
        [AllowNull()][scriptblock]$MutexProbe
    )

    # Scoped deliberately: the signature is VS Code startup text, and labelling
    # some other phase from it would be worse than not labelling at all.
    if ($Name -ne 'Nexus IDE checks') { return $null }
    $signature = 'Code is currently being updated'
    $combined = [string]$Stdout + "`n" + [string]$Stderr
    if ($combined.IndexOf($signature, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $null }

    $probe = $MutexProbe
    if ($null -eq $probe) {
        $probe = {
            param($probeRoot)
            $mutexName = Get-GxMcpVsCodeUpdateMutexName -RepositoryRoot $probeRoot
            if ($null -eq $mutexName) { return $null }
            return (Test-GxMcpVsCodeUpdateMutexHeld -MutexName $mutexName)
        }
    }
    $mutexHeld = $null
    try { $mutexHeld = & $probe $RepositoryRoot } catch { $mutexHeld = $null }
    if ($mutexHeld -isnot [bool]) { $mutexHeld = $null }

    return [ordered]@{
        code = 'vscode-update-in-progress'
        cause = 'VS Code update in progress on this host: the Electron test runtime refused to start, so this is host state and not a repository regression.'
        remediation = 'Finish the VS Code update (close every VS Code window and let the installer complete), then re-run. Pass -ResumeSummaryPath <previous summary> to reuse the phases that already passed.'
        evidence = $signature
        mutexConfirmed = $mutexHeld
    }
}
