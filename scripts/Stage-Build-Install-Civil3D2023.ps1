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

Write-Host "`nPreflighting all CE Tools PowerShell scripts..." -ForegroundColor Cyan
$scriptFolder = Join-Path $stageRoot 'scripts'
foreach ($scriptFile in Get-ChildItem -LiteralPath $scriptFolder -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $scriptFile.FullName,
        [ref]$tokens,
        [ref]$parseErrors) | Out-Null
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $details = ($parseErrors | ForEach-Object {
            'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message
        }) -join ' | '
        throw "PowerShell syntax error in $($scriptFile.Name): $details"
    }
    $scriptText = [System.IO.File]::ReadAllText($scriptFile.FullName)
    if ([regex]::IsMatch($scriptText,'(?im)^\s*elif\s*\(')) {
        throw "Invalid Python-style 'elif' found in PowerShell script: $($scriptFile.Name). Use 'elseif'."
    }
}
Write-Host 'All staged PowerShell scripts passed syntax preflight.' -ForegroundColor Green

$restore = Join-Path $stageRoot 'scripts\Restore-V60-ChunkedSources.ps1'
$repair = Join-Path $stageRoot 'scripts\Repair-Civil3D2023-Compatibility.ps1'
$finalRepair = Join-Path $stageRoot 'scripts\Repair-V60-RemainingCompatibility.ps1'
$productionExpansion = Join-Path $stageRoot 'scripts\Inject-ProductionExpansion-Civil3D2023.ps1'
$augustBehavior = Join-Path $stageRoot 'scripts\Inject-August10BehaviorFixes-Civil3D2023.ps1'
$finalAnnotation = Join-Path $stageRoot 'scripts\Inject-FinalAnnotationReview2-Civil3D2023.ps1'
$finalUtilityWorkflow = Join-Path $stageRoot 'scripts\Inject-FinalUtilityWorkflow-Civil3D2023.ps1'
$cogoOverlapRepair = Join-Path $stageRoot 'scripts\Repair-CogoOverlap-Civil3D2023.ps1'
$roadStyleRepair = Join-Path $stageRoot 'scripts\Repair-RoadStyleFallback-Civil3D2023.ps1'
$branchLabelRepair = Join-Path $stageRoot 'scripts\Repair-BranchLabelRefresh-Civil3D2023.ps1'
$midblockRepair = Join-Path $stageRoot 'scripts\Repair-MidblockRoutePlanner-Civil3D2023.ps1'
$profileStyleAutoImport = Join-Path $stageRoot 'scripts\Repair-ProfileStyleAutoImport-Civil3D2023.ps1'
$utilityProfileIsolation = Join-Path $stageRoot 'scripts\Repair-UtilityProfileIsolation-Civil3D2023.ps1'
$augustCompilerRepair = Join-Path $stageRoot 'scripts\Repair-August10-CompilerErrors-Civil3D2023.ps1'
$august11FieldIntegration = Join-Path $stageRoot 'scripts\Inject-August11FieldCompletion-Civil3D2023.ps1'
$august11CompilerRepair = Join-Path $stageRoot 'scripts\Repair-August11-FieldCompilerCompatibility-Civil3D2023.ps1'
$globalInterfaceSettingsRepair = Join-Path $stageRoot 'scripts\Repair-GlobalInterfaceAndStylePersistence-Civil3D2023.ps1'
$august12PersistentUi = Join-Path $stageRoot 'scripts\Repair-August12PersistentProductionUi-Civil3D2023.ps1'
$august17ProjectComments = Join-Path $stageRoot 'scripts\Repair-August17-ProjectProductionComments-Civil3D2023.ps1'
$august18SurveyDynamics = Join-Path $stageRoot 'scripts\Repair-August18-SurveyGoogleEarthAndVertexDynamics-Civil3D2023.ps1'
$august18SurveyDynamicsHotfix2 = Join-Path $stageRoot 'scripts\Repair-August18-SurveyDynamicsHotfix2-Civil3D2023.ps1'
$august18SurveyBackgroundMenu = Join-Path $stageRoot 'scripts\Repair-August18-SurveyBackgroundToolsMenu-Civil3D2023.ps1'
$closureValidation = Join-Path $stageRoot 'scripts\Validate-August10CommentClosure.ps1'
$august11Validation = Join-Path $stageRoot 'scripts\Validate-August11FieldCompletion.ps1'
$sanitize = Join-Path $stageRoot 'scripts\Sanitize-RestoredCSharpSources.ps1'
$diagnose = Join-Path $stageRoot 'scripts\Diagnose-RoslynSourceCrash.ps1'
$build = Join-Path $stageRoot 'scripts\Build-Install-Civil3D2023-DotNet.ps1'

foreach ($required in @(
    $restore,
    $repair,
    $finalRepair,
    $productionExpansion,
    $augustBehavior,
    $finalAnnotation,
    $finalUtilityWorkflow,
    $cogoOverlapRepair,
    $roadStyleRepair,
    $branchLabelRepair,
    $midblockRepair,
    $profileStyleAutoImport,
    $utilityProfileIsolation,
    $augustCompilerRepair,
    $august11FieldIntegration,
    $august11CompilerRepair,
    $globalInterfaceSettingsRepair,
    $august12PersistentUi,
    $august17ProjectComments,
    $august18SurveyDynamics,
    $august18SurveyDynamicsHotfix2,
    $august18SurveyBackgroundMenu,
    $closureValidation,
    $august11Validation,
    $sanitize,
    $diagnose,
    $build)) {
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

Write-Host "`nExposing final annotation/table review workflows..." -ForegroundColor Cyan
& $finalAnnotation -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nExposing final Sewer, Midblock and profile-style workflows..." -ForegroundColor Cyan
& $finalUtilityWorkflow -RepoRoot $stageRoot
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

Write-Host "`nCompleting Midblock sewer route + visible offset handoff..." -ForegroundColor Cyan
& $midblockRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nAuto-importing bundled profile/band styles when the drawing library is missing..." -ForegroundColor Cyan
& $profileStyleAutoImport -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nIsolating Sewer / Stormwater / Water profile creation per alignment..." -ForegroundColor Cyan
& $utilityProfileIsolation -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nApplying final Civil 3D 2023 compiler compatibility fixes..." -ForegroundColor Cyan
& $augustCompilerRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nIntegrating August 11 field-test production, network, road, survey and table completion..." -ForegroundColor Cyan
& $august11FieldIntegration -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nApplying August 11 Civil 3D 2023 compiler compatibility and wiring guard..." -ForegroundColor Cyan
& $august11CompilerRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nApplying global CE theme, persistent workflow and all-discipline settings defaults..." -ForegroundColor Cyan
& $globalInterfaceSettingsRepair -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nKeeping Production Centres open and isolating discipline style centres..." -ForegroundColor Cyan
& $august12PersistentUi -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nApplying final August 17 Project Production comments..." -ForegroundColor Cyan
& $august17ProjectComments -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nApplying final August 18 Survey/Vertex dynamic refresh, Site Grid loop, scale and Background Tools menu fixes..." -ForegroundColor Cyan
& $august18SurveyDynamics -RepoRoot $stageRoot
$global:LASTEXITCODE = 0
& $august18SurveyDynamicsHotfix2 -RepoRoot $stageRoot
$global:LASTEXITCODE = 0
& $august18SurveyBackgroundMenu -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nValidating previous comment closure before compilation..." -ForegroundColor Cyan
& $closureValidation -RepoRoot $stageRoot
$global:LASTEXITCODE = 0

Write-Host "`nValidating August 11 field-test comment closure before compilation..." -ForegroundColor Cyan
& $august11Validation -RepoRoot $stageRoot
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
