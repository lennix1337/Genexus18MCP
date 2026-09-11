$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../ci/sdk-validation.ps1'
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count) { throw $errors[0] }
$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($required in @(
    'GXMCP_SDK_CI_LICENSE_ACK',
    'validate-gx-sdk.ps1',
    'test-live.ps1',
    "Write-Status 'skip'",
    "Write-Status 'pass'",
    "Write-Status 'fail'",
    'finally',
    'Remove-Item -LiteralPath $runDirectory'
)) {
    if ($source -notmatch [regex]::Escape($required)) { throw "Missing SDK lane contract: $required" }
}

$outputRoot = Join-Path $env:TEMP ('gxmcp-sdk-contract-' + [guid]::NewGuid().ToString('N'))
$oldAck = $env:GXMCP_SDK_CI_LICENSE_ACK
$env:GXMCP_SDK_CI_LICENSE_ACK = $null
try {
    & pwsh -NoProfile -File $scriptPath -OutputRoot $outputRoot
    if ($LASTEXITCODE -ne 0) { throw "Missing license acknowledgement must skip, got exit code $LASTEXITCODE." }
    $statusPath = Join-Path $outputRoot 'status.json'
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'Skip status artifact was not written.' }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.state -ne 'skip') { throw "Expected skip state, got $($status.state)." }

    $sdk = Join-Path $outputRoot 'fake-sdk'
    $kb = Join-Path $outputRoot 'fake-kb'
    $manifest = Join-Path $outputRoot 'fake-fixture.json'
    New-Item -ItemType Directory -Path $sdk, $kb -Force | Out-Null
    '{}' | Set-Content -LiteralPath $manifest -Encoding utf8
    $env:GXMCP_SDK_CI_LICENSE_ACK = '1'
    & pwsh -NoProfile -File $scriptPath -GxPath $sdk -KbPath $kb -FixtureManifest $manifest -OutputRoot $outputRoot
    if ($LASTEXITCODE -ne 1) { throw "Validator failure must fail, got exit code $LASTEXITCODE." }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.state -ne 'fail') { throw "Expected fail state, got $($status.state)." }
    if (@(Get-ChildItem -LiteralPath $outputRoot -Filter 'run-*' -Directory).Count -ne 0) { throw 'Run directory was not cleaned after failure.' }

    # A copy of the test host's PE exercises the real validator without any GeneXus DLL or KB.
    $anchor = Join-Path $sdk 'Artech.Architecture.Common.dll'
    Copy-Item -LiteralPath ([System.Management.Automation.PSObject].Assembly.Location) -Destination $anchor
    $fixtureVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($anchor).ProductVersion
    $fixtureMajor = [int]($fixtureVersion -split '\.')[0]
    $compatManifest = Join-Path $outputRoot 'sdk-compatibility.json'
    $spec = @{
        supportedVersion = $fixtureVersion
        anchor = 'Artech.Architecture.Common.dll'
        assemblies = @(@{ path = 'Artech.Architecture.Common.dll'; sha256 = (Get-FileHash -LiteralPath $anchor -Algorithm SHA256).Hash })
    }
    $validator = Join-Path $PSScriptRoot '../validate-gx-sdk.ps1'
    function Assert-SdkValidation([int]$exitCode, [string]$diagnostic) {
        $spec | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $compatManifest -Encoding utf8
        $output = & powershell.exe -NoProfile -File $validator -GxPath $sdk -Manifest $compatManifest 2>&1 | Out-String
        if ($LASTEXITCODE -ne $exitCode -or -not $output.Contains($diagnostic)) {
            throw "SDK validator expected exit=$exitCode and '$diagnostic', got exit=$LASTEXITCODE`: $output"
        }
    }
    Assert-SdkValidation 0 'GXMCP_SDK_COMPATIBLE'
    $spec.assemblies[0].sha256 = '0' * 64
    Assert-SdkValidation 0 'GXMCP_SDK_FINGERPRINT_DRIFT'
    $spec.supportedVersion = "$fixtureMajor.0.10.184260"
    Assert-SdkValidation 0 'compatible major; patch/build drift'
    $spec.allowPatchVersionDrift = $false
    $spec.supportedVersion = "$fixtureMajor.1.0.0"
    Assert-SdkValidation 0 'GXMCP_SDK_COMPATIBLE'
    $spec.supportedVersion = "$($fixtureMajor + 1).0.16.189550"
    Assert-SdkValidation 1 'GXMCP_SDK_VERSION_MISMATCH'
    $spec.supportedVersion = 'invalid'
    Assert-SdkValidation 1 'GXMCP_SDK_VERSION_MISMATCH'
    $spec.supportedVersion = $fixtureVersion
    $spec.assemblies[0].path = 'missing-required.dll'
    Assert-SdkValidation 1 'GXMCP_SDK_ASSEMBLY_MISSING'
    $spec.assemblies = @()
    Assert-SdkValidation 1 'GXMCP_SDK_MANIFEST_INVALID'
}
finally {
    $env:GXMCP_SDK_CI_LICENSE_ACK = $oldAck
    Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PASS: SDK lane syntax, pass/skip/fail states, cleanup, and 8 no-KB build-validator major/fingerprint/required-assembly cases.'
