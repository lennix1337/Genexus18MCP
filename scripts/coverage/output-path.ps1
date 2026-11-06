function Resolve-CoverageOutputDirectory {
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$AllowedOutputRoot,
        [string[]]$TemporaryRoots = @([IO.Path]::GetTempPath(), $env:RUNNER_TEMP)
    )

    $comparison = if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        [StringComparison]::OrdinalIgnoreCase
    } else { [StringComparison]::Ordinal }

    function Full-Directory([string]$Path) {
        $full = [IO.Path]::GetFullPath($Path)
        if ($full.Equals([IO.Path]::GetPathRoot($full), $comparison)) { return $full }
        return $full.TrimEnd([char[]]'\/')
    }
    function Is-Within([string]$Path, [string]$Parent) {
        return $Path.StartsWith($Parent.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar, $comparison)
    }
    function Assert-NoReparsePoint([string]$Path) {
        $current = $Path
        while ($current) {
            $item = $null
            try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
            catch [System.Management.Automation.ItemNotFoundException] { }
            if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Coverage output cannot use a symbolic link or junction: $current"
            }
            $current = [IO.Path]::GetDirectoryName($current)
        }
    }

    $output = Full-Directory $OutputRoot
    $repository = Full-Directory $RepoRoot
    $artifacts = Full-Directory (Join-Path $repository 'artifacts')
    if ($output.Equals([IO.Path]::GetPathRoot($output), $comparison) -or
        $output.Equals($repository, $comparison) -or (Is-Within $repository $output)) {
        throw 'Coverage output cannot be a filesystem root, checkout, or ancestor of the checkout.'
    }
    if ((Is-Within $output $repository) -and -not (Is-Within $output $artifacts)) {
        throw 'Coverage output inside the checkout must be a child of artifacts.'
    }

    $roots = @($artifacts)
    foreach ($temporary in $TemporaryRoots) {
        if (-not [string]::IsNullOrWhiteSpace($temporary)) { $roots += Full-Directory $temporary }
    }
    if (-not [string]::IsNullOrWhiteSpace($AllowedOutputRoot)) {
        $roots += Full-Directory $AllowedOutputRoot
    }
    $allowed = $false
    foreach ($parent in $roots) {
        if ($output.Equals($parent, $comparison)) {
            throw 'Coverage output must be a strict child of its allowed directory.'
        }
        # An accidental TEMP=C:\ or -AllowedOutputRoot C:\ must not authorize the disk.
        if ($parent.Equals([IO.Path]::GetPathRoot($parent), $comparison)) { continue }
        if (Is-Within $output $parent) {
            Assert-NoReparsePoint $parent
            $allowed = $true
        }
    }
    if (-not $allowed) {
        throw 'Coverage output must be under artifacts, TEMP/RUNNER_TEMP, or an explicitly supplied -AllowedOutputRoot.'
    }
    Assert-NoReparsePoint $output
    if (Test-Path -LiteralPath $output) {
        if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw 'Coverage output must be a directory.' }
        if (Get-ChildItem -LiteralPath $output -Force -Recurse -Attributes ReparsePoint -ErrorAction Stop | Select-Object -First 1) {
            throw 'Coverage output contains a symbolic link or junction; refusing recursive cleanup.'
        }
    }
    return $output
}
