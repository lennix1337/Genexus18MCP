$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../live-build-all.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-JsonProperty', 'Convert-McpToolPayload', 'Find-BuildAllUnavailableReason', 'Resolve-BuildAllPayload', 'Get-BuildAllField', 'Test-BuildAllTerminal', 'Get-BuildAllEvidence', 'Get-BuildAllTimeoutResult')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "${Message}: expected '$Expected', got '$Actual'" }
}

$success = Get-BuildAllEvidence ([pscustomobject]@{
    status = 'Succeeded'; buildMode = 'BuildAll'; kbOpened = $true; buildAllDone = $true
    reorgRequired = $false; msBuildExitCode = 0; fullLogPath = 'C:\logs\build-all.log'
})
Assert-Equal $success.live 'pass' 'complete Build All evidence'

$nested = Get-BuildAllEvidence ([pscustomobject]@{
    structuredContent = [pscustomobject]@{
        message = '{"status":"Succeeded","buildMode":"BuildAll","kbOpened":true,"buildAllDone":true,"reorgRequired":false,"msBuildExitCode":0,"fullLogPath":"C:\\logs\\build-all.log"}'
    }
})
Assert-Equal $nested.live 'pass' 'nested structuredContent message is decoded'
$direct = Get-BuildAllEvidence ([pscustomobject]@{
    message = '{"status":"Succeeded","buildMode":"BuildAll","kbOpened":true,"buildAllDone":true,"reorgRequired":false,"msBuildExitCode":0,"fullLogPath":"C:\\logs\\build-all.log"}'
})
Assert-Equal $direct.live 'pass' 'direct message payload is decoded'
$actualShape = Get-BuildAllEvidence ([pscustomobject]@{
    result = [pscustomobject]@{
        content = @([pscustomobject]@{ text = '{"status":"failed","result":{"buildMode":"BuildAll","kbOpened":true,"buildAllDone":false,"reorgRequired":false,"msBuildExitCode":0,"fullLogPath":"C:\\logs\\build-all.log","Warnings":[],"warnings":[]},"message":"Input parameters are not complete: User"}' })
    }
})
Assert-Equal $actualShape.live 'unavailable' 'actual MCP content wrapper is classified unavailable'
Assert-Equal $actualShape.buildMode 'BuildAll' 'actual MCP content wrapper exposes build mode'
Assert-Equal $actualShape.kbOpened $true 'actual MCP content wrapper exposes KB evidence'
Assert-Equal $actualShape.msBuildExitCode 0 'actual MCP content wrapper preserves exit code'

$reorg = Get-BuildAllEvidence ([pscustomobject]@{
    status = 'ReorgRequired'; buildMode = 'BuildAll'; kbOpened = $true; buildAllDone = $false
    reorgRequired = $true; msBuildExitCode = 0; fullLogPath = 'C:\logs\build-all.log'
})
Assert-Equal $reorg.live 'fail' 'reorganization must block success'

$missingEvidence = Get-BuildAllEvidence ([pscustomobject]@{
    status = 'Succeeded'; buildMode = 'BuildAll'; kbOpened = $true; buildAllDone = $false
    reorgRequired = $false; msBuildExitCode = 0; fullLogPath = 'C:\logs\build-all.log'
})
Assert-Equal $missingEvidence.live 'fail' 'exit code 0 without completion evidence'

$missingUser = Get-BuildAllEvidence ([pscustomobject]@{
    status = 'Failed'; message = 'The User parameter is required by the GeneXus cloud build.'
    buildMode = 'BuildAll'; kbOpened = $true; buildAllDone = $false
    reorgRequired = $false; msBuildExitCode = 0; fullLogPath = 'C:\logs\build-all.log'
})
Assert-Equal $missingUser.live 'unavailable' 'missing cloud User must be unavailable'

$running = [pscustomobject]@{ status = 'Running'; operationId = 'abc123' }
if (Test-BuildAllTerminal $running) { throw 'Running operation must not be terminal.' }
$timeout = Get-BuildAllTimeoutResult 30
Assert-Equal $timeout.live 'fail' 'timeout must fail the gate'

$testLiveSource = Get-Content (Join-Path $PSScriptRoot '../test-live.ps1') -Raw
if ($testLiveSource -notmatch '\$RequireBuildAll' -or $testLiveSource -notmatch 'live-build-all\.ps1') {
    throw 'test-live.ps1 does not expose the explicit Build All gate.'
}
$buildAllSource = Get-Content $scriptPath -Raw
if ($buildAllSource -notmatch "notifications/initialized.*-Notification") {
    throw 'Build All handshake must encode notifications/initialized without a JSON-RPC id.'
}
Write-Host 'live-build-all: evidence, unavailable, reorg, missing completion and timeout checks passed' -ForegroundColor Green
