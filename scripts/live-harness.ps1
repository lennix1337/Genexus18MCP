function Assert-LiveGatewayMaster {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [switch]$RequireStdio,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $log = Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue
            if ($log -match '(?i)(existing_master_detected|duplicate_instance_prevented|\[Proxy\] Proxy mode active)') {
                throw "Live Gateway entered proxy/duplicate mode; log=$LogPath"
            }
            $requiredMarker = if ($RequireStdio) {
                '\[Gateway\] Entering Stdio Loop'
            } else {
                'MCP stdio disabled\. Serving HTTP only\.'
            }
            if ($log -match "(?m)$requiredMarker") { return }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Live Gateway master marker was not observed within $TimeoutSeconds seconds; log=$LogPath"
}

function Assert-LiveGatewayProcessImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $expected = [IO.Path]::GetFullPath($ExpectedPath)
    $actual = $null
    try { $actual = $Process.Path } catch { }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        try {
            $actual = (Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction Stop).ExecutablePath
        } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        try { $actual = $Process.MainModule.FileName } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        throw "Live Gateway process image could not be resolved (expected '$expected')."
    }
    if ([IO.Path]::GetFullPath($actual) -ine $expected) {
        throw "Live Gateway process image mismatch (expected '$expected', actual '$actual')."
    }
}

function Get-LiveMissingTestAssemblies {
    # The live gates run the test projects with --no-build, so an unbuilt tree otherwise surfaces as a bare
    # "provided was not found" from dotnet after the KB was opened. Report each missing assembly with its build command.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$GatewayOnly
    )

    $targets = @(@{ Project = 'GxMcp.Gateway.Tests'; Platform = $null })
    if (-not $GatewayOnly) { $targets += @{ Project = 'GxMcp.Worker.Tests'; Platform = 'x86' } }
    foreach ($target in $targets) {
        $binRoot = Join-Path $Root "src\$($target.Project)\bin"
        $pattern = if ($target.Platform) { "*\$($target.Platform)\Release\*" } else { '*\Release\*' }
        $built = if (Test-Path -LiteralPath $binRoot -PathType Container) {
            Get-ChildItem -LiteralPath $binRoot -Recurse -Filter "$($target.Project).dll" -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like $pattern } | Select-Object -First 1
        }
        if (-not $built) {
            $platformArg = if ($target.Platform) { " -p:Platform=$($target.Platform)" } else { '' }
            "$($target.Project).dll (Release) not found; build it first: dotnet build src\$($target.Project)\$($target.Project).csproj -c Release$platformArg"
        }
    }
}
