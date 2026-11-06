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
    'check-build-warning-baseline.ps1',
    'rev-parse HEAD',
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
    if ($status.commit -notmatch '^[0-9a-f]{40}$') { throw "Status must bind the evidence to the checked-out commit, got '$($status.commit)'." }
    if ($status.commit -ne (& git -C (Join-Path $PSScriptRoot '../..') rev-parse HEAD).Trim()) { throw 'Status commit does not equal HEAD.' }

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
    $versionCatalog = Join-Path $outputRoot 'gx-versions.json'
    $catalog = @{ supportedMajors = @(@{ major = [string]$fixtureMajor }, @{ major = [string]($fixtureMajor + 1) }) }
    $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $versionCatalog -Encoding utf8
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
    Assert-SdkValidation 0 "supported major $fixtureMajor; reference major $($fixtureMajor + 1)"
    $catalog.supportedMajors = @(@{ major = [string]($fixtureMajor + 1) })
    $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $versionCatalog -Encoding utf8
    Assert-SdkValidation 1 'GXMCP_SDK_VERSION_MISMATCH'
    # An Evolution 3 Worker never accepts a GeneXus 16+ SDK, even with both majors catalogued.
    $catalog.supportedMajors = @(@{ major = '10.3' }, @{ major = [string]$fixtureMajor })
    $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $versionCatalog -Encoding utf8
    $spec.supportedVersion = '10.3.16.146128'
    Assert-SdkValidation 1 'GXMCP_SDK_VERSION_MISMATCH'
    $catalog.supportedMajors = @(@{ major = [string]$fixtureMajor }, @{ major = [string]($fixtureMajor + 1) })
    $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $versionCatalog -Encoding utf8
    $spec.supportedVersion = 'invalid'
    Assert-SdkValidation 1 'GXMCP_SDK_MANIFEST_INVALID'
    $spec.supportedVersion = $fixtureVersion
    $spec.assemblies[0].path = 'missing-required.dll'
    Assert-SdkValidation 1 'GXMCP_SDK_ASSEMBLY_MISSING'
    $spec.assemblies = @()
    Assert-SdkValidation 1 'GXMCP_SDK_MANIFEST_INVALID'

    # Exercise the CI entrypoint from an empty test-output tree, not a developer's warm bin/obj.
    $probeRoot = Join-Path $outputRoot 'clean-checkout'
    $probeCi = Join-Path $probeRoot 'scripts/ci'
    New-Item -ItemType Directory -Path $probeCi -Force | Out-Null
    $probeScript = Join-Path $probeCi 'sdk-validation.ps1'
    Copy-Item -LiteralPath $scriptPath -Destination $probeScript
    $probeHost = Join-Path $outputRoot 'invoke-clean-sdk-lane.ps1'
    @'
param($ScriptPath, $Sdk, $Kb, $Fixture, $OutputRoot, $BuildExit, $PreflightExit, $PackageExit)
function dotnet {
    if ($args[0] -ne 'build' -or $args[1] -notlike '*GxMcp.Worker.Tests.csproj' -or
        $args -notcontains 'Release' -or $args -notcontains "-p:GX_PATH=$Sdk") {
        throw 'Worker live tests must be built in Release against the selected SDK.'
    }
    Add-Content (Join-Path $OutputRoot 'calls.txt') 'worker-build'
    $global:LASTEXITCODE = [int]$BuildExit
}
function pwsh {
    $scriptName = [IO.Path]::GetFileName([string]$args[2])
    Add-Content (Join-Path $OutputRoot 'calls.txt') $scriptName
    $global:LASTEXITCODE = 0
    if ($scriptName -eq 'release-preflight.ps1') {
        if ($env:GXMCP_TEST_KB -or $env:GXMCP_TEST_FIXTURE -or $env:GX_CONFIG_PATH) {
            throw 'Offline preflight inherited live KB/configuration before the live harness prepared it.'
        }
        if (-not (Test-Path (Join-Path (Split-Path (Split-Path $ScriptPath)) '../publish/GxMcp.Gateway.exe'))) {
            throw 'Process smoke cannot bind a clean checkout without its current published package.'
        }
        $global:LASTEXITCODE = [int]$PreflightExit
    }
    if ($scriptName -eq 'build.ps1') {
        $publish = Join-Path (Split-Path $args[2]) 'publish'
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        Set-Content (Join-Path $publish 'GxMcp.Gateway.exe') 'test-package'
        $global:LASTEXITCODE = [int]$PackageExit
    }
    if ($scriptName -eq 'test-live.ps1') {
        if ($args -notcontains '-SkipBuild') { throw 'Live lane must use the exact package certified by process smoke.' }
        if (-not (Get-Content (Join-Path $OutputRoot 'calls.txt')).Contains('worker-build')) {
            throw 'Live lane ran before its Worker test assembly was built.'
        }
        if ($env:GXMCP_TEST_KB -ne $Kb -or $env:GXMCP_TEST_FIXTURE -ne $Fixture -or -not $env:GX_CONFIG_PATH) {
            throw 'Live fixture/configuration was not restored after offline preflight.'
        }
    }
}
& $ScriptPath -GxPath $Sdk -KbPath $Kb -FixtureManifest $Fixture -OutputRoot $OutputRoot
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $probeHost -Encoding utf8
    foreach ($case in @(@(0, 0, 0), @(7, 0, 0), @(0, 8, 0), @(0, 0, 9))) {
        $buildExit, $preflightExit, $packageExit = $case
        Remove-Item (Join-Path $probeRoot 'publish') -Recurse -Force -ErrorAction SilentlyContinue
        $probeOutput = Join-Path $outputRoot "probe-$buildExit-$preflightExit-$packageExit"
        & pwsh -NoProfile -File $probeHost $probeScript $sdk $kb $manifest $probeOutput $buildExit $preflightExit $packageExit
        $expectedExit = if ($buildExit -eq 0 -and $preflightExit -eq 0 -and $packageExit -eq 0) { 0 } else { 1 }
        if ($LASTEXITCODE -ne $expectedExit) { throw "Clean-checkout SDK lane expected exit $expectedExit, got $LASTEXITCODE." }
        $probeStatus = Get-Content (Join-Path $probeOutput 'status.json') -Raw | ConvertFrom-Json
        $expectedState = if ($expectedExit -eq 0) { 'pass' } else { 'fail' }
        if ($probeStatus.state -ne $expectedState) { throw "Clean-checkout SDK lane expected $expectedState." }
        $calls = @(Get-Content (Join-Path $probeOutput 'calls.txt'))
        if ($expectedExit -ne 0 -and $calls -contains 'test-live.ps1') { throw 'Failed compilation or offline preflight must block the live lane.' }
        if ($expectedExit -eq 0 -and $calls -notcontains 'test-live.ps1') { throw 'Offline preflight must not replace the mandatory live lane.' }
    }
}
finally {
    $env:GXMCP_SDK_CI_LICENSE_ACK = $oldAck
    Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PASS: SDK lane syntax, pass/skip/fail states, cleanup, and 9 no-KB build-validator catalog/major/fingerprint/required-assembly cases.'
