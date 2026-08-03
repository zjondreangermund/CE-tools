[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [System.IO.Path]::GetFullPath($SourceRoot.Trim('"'))
if (-not (Test-Path -LiteralPath (Join-Path $source 'CE.Tools.sln'))) {
    throw "CE Tools repository was not found at: $source"
}

$stageRoot = Join-Path $env:LOCALAPPDATA 'CE-Tools-Build\main-c3d2023'
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

Write-Host "Copying repository to short local build path:" -ForegroundColor Cyan
Write-Host $stageRoot

$excludeDirectories = @('.git', '.vs', 'bin', 'obj', 'artifacts')
$arguments = @(
    $source,
    $stageRoot,
    '/E',
    '/R:2',
    '/W:1',
    '/NFL',
    '/NDL',
    '/NJH',
    '/NJS',
    '/NP',
    '/XD'
) + $excludeDirectories

& robocopy @arguments | Out-Null
$copyExitCode = $LASTEXITCODE
if ($copyExitCode -ge 8) {
    throw "Could not stage the repository. Robocopy exit code: $copyExitCode"
}

$restore = Join-Path $stageRoot 'scripts\Restore-V60-ChunkedSources.ps1'
$repair = Join-Path $stageRoot 'scripts\Repair-Civil3D2023-Compatibility.ps1'
$finalRepair = Join-Path $stageRoot 'scripts\Repair-V60-RemainingCompatibility.ps1'
$build = Join-Path $stageRoot 'scripts\Build-Install-Civil3D2023.ps1'

foreach ($required in @($restore, $repair, $finalRepair, $build)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required staged script was not found: $required"
    }
    Unblock-File -LiteralPath $required -ErrorAction SilentlyContinue
}

# Robocopy uses non-zero success codes (1-7). Clear that native-process
# status before invoking PowerShell scripts so it cannot be mistaken for a
# failure after a successful script invocation.
$global:LASTEXITCODE = 0

Write-Host "`nRestoring verified V60 support sources..." -ForegroundColor Cyan
& $restore -RepoRoot $stageRoot

Write-Host "`nPreparing CE Tools sources for Civil 3D 2023..." -ForegroundColor Cyan
& $repair -RepoRoot $stageRoot

& $finalRepair -RepoRoot $stageRoot

Write-Host "`nBuilding from the short local path to avoid OneDrive and long-path compiler crashes..." -ForegroundColor Cyan
& $build -Clean

Write-Host "`nCE Tools was built and installed from:" -ForegroundColor Green
Write-Host $stageRoot
