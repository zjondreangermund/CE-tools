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
$global:LASTEXITCODE = 0

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

# Prefer the 64-bit Build Tools host. The 32-bit compiler process was exiting
# with CLR code 0x80131623 while processing the restored Civil 3D source set.
$amd64MsBuild = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe'
if (Test-Path -LiteralPath $amd64MsBuild) {
    $buildText = [System.IO.File]::ReadAllText($build)
    $oldCandidate = "(Join-Path `$programFilesX86 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe')"
    $newCandidate = "(Join-Path `$programFilesX86 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe')"
    if ($buildText.Contains($oldCandidate)) {
        $buildText = $buildText.Replace($oldCandidate, $newCandidate)
        [System.IO.File]::WriteAllText($build, $buildText, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host "Using 64-bit MSBuild compiler host:" -ForegroundColor Green
    Write-Host $amd64MsBuild
}
else {
    Write-Warning "64-bit MSBuild was not found; the normal MSBuild search will be used."
}

Write-Host "`nRestoring verified V60 support sources..." -ForegroundColor Cyan
& $restore -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nPreparing CE Tools sources for Civil 3D 2023..." -ForegroundColor Cyan
& $repair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

& $finalRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nBuilding from the short local path to avoid OneDrive and long-path compiler crashes..." -ForegroundColor Cyan
& $build -Clean
if ($LASTEXITCODE -ne 0) {
    throw "CE Tools build or installation failed with exit code $LASTEXITCODE."
}

Write-Host "`nCE Tools was built and installed from:" -ForegroundColor Green
Write-Host $stageRoot
