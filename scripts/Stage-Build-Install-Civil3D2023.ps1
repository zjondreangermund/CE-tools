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

$sourceCommit = 'UNKNOWN'
try { $sourceCommit = (& git -C $source rev-parse HEAD 2>$null).Trim() }
catch { $sourceCommit = 'UNKNOWN' }
if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = 'UNKNOWN' }

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
$productionExpansion = Join-Path $stageRoot 'scripts\Inject-ProductionExpansion-Civil3D2023.ps1'
$augustBehavior = Join-Path $stageRoot 'scripts\Inject-August10BehaviorFixes-Civil3D2023.ps1'
$cogoOverlapRepair = Join-Path $stageRoot 'scripts\Repair-CogoOverlap-Civil3D2023.ps1'
$roadStyleRepair = Join-Path $stageRoot 'scripts\Repair-RoadStyleFallback-Civil3D2023.ps1'
$branchLabelRepair = Join-Path $stageRoot 'scripts\Repair-BranchLabelRefresh-Civil3D2023.ps1'
$closureValidation = Join-Path $stageRoot 'scripts\Validate-August10CommentClosure.ps1'
$sanitize = Join-Path $stageRoot 'scripts\Sanitize-RestoredCSharpSources.ps1'
$diagnose = Join-Path $stageRoot 'scripts\Diagnose-RoslynSourceCrash.ps1'
$build = Join-Path $stageRoot 'scripts\Build-Install-Civil3D2023-DotNet.ps1'

foreach ($required in @($restore, $repair, $finalRepair, $productionExpansion, $augustBehavior, $cogoOverlapRepair, $roadStyleRepair, $branchLabelRepair, $closureValidation, $sanitize, $diagnose, $build)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required staged script was not found: $required"
    }
    Unblock-File -LiteralPath $required -ErrorAction SilentlyContinue
}

Write-Host "`nChecking verified V60/V54 recovery fallbacks without overwriting active sources..." -ForegroundColor Cyan
& $restore -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nPreparing CE Tools sources for Civil 3D 2023..." -ForegroundColor Cyan
& $repair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0
& $finalRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nIntegrating road, platform, route-planner and final comment workflows..." -ForegroundColor Cyan
& $productionExpansion -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nIntegrating final August runtime behavior fixes..." -ForegroundColor Cyan
& $augustBehavior -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nRepairing COGO overlap movement bounds..." -ForegroundColor Cyan
& $cogoOverlapRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nRepairing road style fallback selection..." -ForegroundColor Cyan
& $roadStyleRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nIntegrating dedicated branch-label layer refresh..." -ForegroundColor Cyan
& $branchLabelRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nValidating complete comment closure before compilation..." -ForegroundColor Cyan
& $closureValidation -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nSanitizing recovered C# source encoding and hidden characters..." -ForegroundColor Cyan
& $sanitize -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nChecking restored source files for the Roslyn TextSpan parser crash..." -ForegroundColor Cyan
& $diagnose -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nBuilding with the pinned .NET SDK compiler..." -ForegroundColor Cyan
& $build -Clean -SourceCommit $sourceCommit
if ($LASTEXITCODE -ne 0) {
    throw "CE Tools .NET SDK build or installation failed with exit code $LASTEXITCODE."
}

Write-Host "`nCE Tools was built and installed from:" -ForegroundColor Green
Write-Host $stageRoot
Write-Host "Source commit: $sourceCommit"
