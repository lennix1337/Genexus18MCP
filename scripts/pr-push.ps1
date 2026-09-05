[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$PullRequest,

    [switch]$ForceWithLease
)

$ErrorActionPreference = 'Stop'

function Fail-Push([string]$Message) {
    Write-Error "PR push failed: $Message"
    exit 1
}

function Get-GhJson([string[]]$Arguments) {
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail-Push "gh $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    try {
        return ($output -join "`n") | ConvertFrom-Json
    } catch {
        Fail-Push "gh $($Arguments -join ' ') returned invalid JSON."
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail-Push "GitHub CLI (gh) is required."
}

$branch = (& git branch --show-current 2>&1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    Fail-Push "The push helper requires a named local branch."
}
if ($branch -eq 'main') {
    Fail-Push "Refusing to push from main. Check out the PR branch first."
}

$pr = Get-GhJson @(
    'pr', 'view', $PullRequest.ToString(),
    '--json', 'number,state,headRefName,headRefOid,headRepository,url'
)
if ($pr.state -ne 'OPEN') {
    Fail-Push "PR #$PullRequest must be OPEN; current state is '$($pr.state)'."
}

$headRepo = $pr.headRepository.nameWithOwner
$headRef = $pr.headRefName
$headOid = $pr.headRefOid
if ([string]::IsNullOrWhiteSpace($headRepo) -or
    [string]::IsNullOrWhiteSpace($headRef) -or
    [string]::IsNullOrWhiteSpace($headOid)) {
    Fail-Push "PR #$PullRequest did not expose a complete head repository/ref/OID."
}

$targetUrl = "https://github.com/$headRepo.git"
$targetRef = "refs/heads/$headRef"
$destination = "HEAD:$targetRef"
$pushArguments = @('push')
if ($ForceWithLease) {
    $lease = '--force-with-lease={0}:{1}' -f $targetRef, $headOid
    $pushArguments += $lease
}
$pushArguments += @($targetUrl, $destination)

Write-Host "PR #$PullRequest push target resolved:" -ForegroundColor Cyan
Write-Host "  repository: $headRepo"
Write-Host "  ref:        $headRef"
Write-Host "  expected:   $headOid"
Write-Host "  lease:      $ForceWithLease"

if ($PSCmdlet.ShouldProcess("$headRepo/$headRef", "push local HEAD")) {
    & git @pushArguments
    if ($LASTEXITCODE -ne 0) {
        Fail-Push "git push failed with exit code $LASTEXITCODE."
    }
    Write-Host "Push completed." -ForegroundColor Green
}
