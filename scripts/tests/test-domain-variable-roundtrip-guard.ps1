$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $root 'scripts\Test-DomainVariableRoundTrip.ps1'
$variable = 'GXMCP_TEST_PROTECTED_KB_PATTERN'
$failures = @()

# Assert-DisposableKb runs before any Worker is started, so these cases need no GeneXus installation and no
# Knowledge Base: a directory with or without a knowledgebase.connection file is enough, and -WorkerExe and
# -GeneXusPath are never reached. Every case ends in a refusal, which is what keeps the regression itself from running.
$temp = Join-Path ([IO.Path]::GetTempPath()) ('gxmcp-roundtrip-guard-' + [Guid]::NewGuid().ToString('N'))
$openDirectory = Join-Path $temp 'Open\FixtureKb'
$guardedDirectory = Join-Path $temp 'Guarded-Area\FixtureKb'
$sourceKb = Join-Path $temp 'Open\SourceKb'

function Invoke-Guard([string[]] $Arguments, $EnvironmentPattern, [string] $WorkingDirectory) {
    $previous = [Environment]::GetEnvironmentVariable($variable)
    if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory }
    try {
        # Untyped on purpose: a null value removes the variable from this process, and so from the child.
        [Environment]::SetEnvironmentVariable($variable, $EnvironmentPattern)
        $output = @(& pwsh -NoProfile -File $script -WorkerExe (Join-Path $temp 'no-worker.exe') -GeneXusPath $temp `
                @Arguments -ConfirmDisposableKbs 2>&1)
        return [pscustomobject]@{ exit = $LASTEXITCODE; text = ($output -join "`n") }
    }
    finally {
        [Environment]::SetEnvironmentVariable($variable, $previous)
        if ($WorkingDirectory) { Pop-Location }
    }
}

function Expect-Refusal([string] $Name, $Result, [string] $Pattern, [string] $NotPattern) {
    if ($Result.exit -eq 0 -or $Result.text -notmatch $Pattern) {
        $script:failures += "$Name was not refused with /$Pattern/ (exit $($Result.exit)): $($Result.text)"
    }
    elseif ($NotPattern -and $Result.text -match $NotPattern) {
        $script:failures += "$Name was refused, but the output also matched /$NotPattern/: $($Result.text)"
    }
}

$protectedPath = 'protected KB path'
$notAKb = 'Not a GeneXus KB directory'

try {
    foreach ($directory in $openDirectory, $guardedDirectory, $sourceKb) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    # Only its presence is read before the guard of the second KB runs.
    Set-Content -LiteralPath (Join-Path $sourceKb 'knowledgebase.connection') -Value '<ConnectionInformation />'

    # No pattern from either source: no path is refused for what it is called.
    Expect-Refusal 'No pattern' (Invoke-Guard @('-SourceKbPath', $guardedDirectory) $null) $notAKb $protectedPath

    # The parameter protects a path, whatever the case of the path or of the pattern.
    Expect-Refusal 'Pattern from the parameter' `
        (Invoke-Guard @('-SourceKbPath', $guardedDirectory, '-ProtectedKbPathPattern', 'guarded-AREA') $null) `
        $protectedPath $notAKb

    # The environment variable is the default of the parameter.
    Expect-Refusal 'Pattern from the environment' (Invoke-Guard @('-SourceKbPath', $guardedDirectory) 'guarded-area') `
        $protectedPath $notAKb

    # The pattern is matched against the resolved path, so a relative path cannot leave the protected part out.
    Expect-Refusal 'Relative path into a protected directory' `
        (Invoke-Guard @('-SourceKbPath', 'FixtureKb', '-ProtectedKbPathPattern', 'guarded-area') $null (Split-Path -Parent $guardedDirectory)) `
        $protectedPath $notAKb

    # A path the pattern does not match goes on to the next check.
    Expect-Refusal 'Path outside the pattern' `
        (Invoke-Guard @('-SourceKbPath', $openDirectory, '-ProtectedKbPathPattern', 'guarded-area') $null) `
        $notAKb $protectedPath

    # An explicit parameter replaces the environment default instead of adding to it.
    Expect-Refusal 'Parameter replaces the environment' `
        (Invoke-Guard @('-SourceKbPath', $guardedDirectory, '-ProtectedKbPathPattern', 'another-area') 'guarded-area') `
        $notAKb $protectedPath

    # A pattern that does not parse stops the run instead of protecting nothing.
    Expect-Refusal 'Pattern that does not parse' `
        (Invoke-Guard @('-SourceKbPath', $openDirectory, '-ProtectedKbPathPattern', 'guarded-area(') $null) `
        'ProtectedKbPathPattern is not a valid regular expression' $notAKb

    # The second KB is guarded as well: the first one passes both checks and the run still stops.
    Expect-Refusal 'Import KB under the pattern' `
        (Invoke-Guard @('-SourceKbPath', $sourceKb, '-ImportKbPath', $guardedDirectory, '-ProtectedKbPathPattern', 'guarded-area') $null) `
        $protectedPath $notAKb
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'domain-variable-roundtrip guard tests passed: protected paths by parameter and by environment, resolved paths, both KBs, and a pattern that does not parse.'
