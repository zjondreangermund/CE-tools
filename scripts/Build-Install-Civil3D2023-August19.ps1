[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$Clean,
    [string]$Configuration = 'Release',
    [string]$SourceCommit = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$legacyBuild = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-DotNet.ps1'
$prepareCoordinateRepair = Join-Path $PSScriptRoot 'Repair-August19-PrepareCoordinateDisplayStage.ps1'
$adaptCoordinateValidation = Join-Path $PSScriptRoot 'Repair-August19-AdaptCoordinateValidation.ps1'
$august19Repair = Join-Path $PSScriptRoot 'Repair-August19-VertexSettingOutIntervalsAndAlignments-Civil3D2023.ps1'
$august19SewerBatchStage = Join-Path $PSScriptRoot 'Repair-August19-PrepareSewerGenericBatchStage.ps1'
$august19SurveyGridSewer = Join-Path $PSScriptRoot 'Repair-August19-SurveyGridSurfacesAndSewerMulti-Civil3D2023.ps1'
$august19SewerPartsResolution = Join-Path $PSScriptRoot 'Repair-August19-SewerPartsListResolution-Civil3D2023.ps1'
$august19CadastralSewer = Join-Path $PSScriptRoot 'Repair-August19-CadastralSewerRouting-Civil3D2023.ps1'
$august19CompilerFix = Join-Path $PSScriptRoot 'Repair-August19-VertexHeadingCompilerFix.ps1'
$august19SiteGridCompileFinalizer = Join-Path $PSScriptRoot 'Repair-August19-SiteGridCompileFinalizer.ps1'
$runtime = Join-Path $PSScriptRoot '.Build-Install-Civil3D2023-August19.runtime.ps1'

if (-not (Test-Path -LiteralPath $legacyBuild -PathType Leaf)) {
    throw "Existing August 18 build script was not found: $legacyBuild"
}
if (-not (Test-Path -LiteralPath $prepareCoordinateRepair -PathType Leaf)) {
    throw "August 19 staged coordinate compatibility repair was not found: $prepareCoordinateRepair"
}
if (-not (Test-Path -LiteralPath $adaptCoordinateValidation -PathType Leaf)) {
    throw "August 19 staged coordinate validation adapter was not found: $adaptCoordinateValidation"
}
if (-not (Test-Path -LiteralPath $august19Repair -PathType Leaf)) {
    throw "August 19 Vertex Setting-Out repair was not found: $august19Repair"
}
if (-not (Test-Path -LiteralPath $august19SewerBatchStage -PathType Leaf)) {
    throw "August 19 staged generic Sewer adapter was not found: $august19SewerBatchStage"
}
if (-not (Test-Path -LiteralPath $august19SurveyGridSewer -PathType Leaf)) {
    throw "August 19 Survey/Grid/Sewer repair was not found: $august19SurveyGridSewer"
}
if (-not (Test-Path -LiteralPath $august19SewerPartsResolution -PathType Leaf)) {
    throw "August 19 Sewer parts-list resolver was not found: $august19SewerPartsResolution"
}
if (-not (Test-Path -LiteralPath $august19CadastralSewer -PathType Leaf)) {
    throw "August 19 cadastral Sewer routing repair was not found: $august19CadastralSewer"
}
if (-not (Test-Path -LiteralPath $august19CompilerFix -PathType Leaf)) {
    throw "August 19 Vertex compiler finalizer was not found: $august19CompilerFix"
}
if (-not (Test-Path -LiteralPath $august19SiteGridCompileFinalizer -PathType Leaf)) {
    throw "August 19 Site Grid compile finalizer was not found: $august19SiteGridCompileFinalizer"
}

# Never run August 19 directly against the tracked source checkout. The official
# August 19 entry point is Stage-Build-Install-Civil3D2023-August19.ps1, which
# first copies the repository and runs every August 18 stage repair in isolation.
if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
    throw 'August 19 build refused to modify the tracked checkout. Run Stage-Build-Install-Civil3D2023-August19.ps1 so August 18 remains unchanged and August 19 is applied only to the staged copy.'
}

# The late August 18 recovery/sanitizer chain can change harmless Site Grid source
# wording before the final coordinate-display repair runs. Finalize Site Grid only
# in the staged copy, then adapt only the staged August 18 final guard so it checks
# actual X/Y sign-transform behavior rather than a historical local-variable name.
Write-Host "Preparing staged Site Grid coordinate-display compatibility for August 19..." -ForegroundColor Cyan
& $prepareCoordinateRepair -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Adapting staged Site Grid coordinate validation for August 19..." -ForegroundColor Cyan
& $adaptCoordinateValidation -RepoRoot $repo
$global:LASTEXITCODE = 0

$legacyText = [System.IO.File]::ReadAllText($legacyBuild) -replace "`r?`n","`r`n"

# Verify that this is still the complete August 18 final build sequence. If an
# August 18 stage changes later, update/test that pipeline first rather than
# silently placing August 19 in the wrong order.
$requiredAugust18BuildMarkers = @(
    'Repair-August18-DisableLegacyBackgroundWatchers-Civil3D2023.ps1',
    'Repair-August18-SourceOnlySettingOutRefresh-Civil3D2023.ps1',
    'Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1',
    'Applying final setting-out X/Y display and Site Grid text fixes...',
    'Push-Location $repo'
)
foreach ($marker in $requiredAugust18BuildMarkers) {
    if (-not $legacyText.Contains($marker)) {
        throw "August 19 build refused to run because the August 18 build marker is missing: $marker"
    }
}

$pushMarker = 'Push-Location $repo'
$pushIndex = $legacyText.IndexOf($pushMarker,[StringComparison]::Ordinal)
$coordinateIndex = $legacyText.IndexOf(
    'Applying final setting-out X/Y display and Site Grid text fixes...',
    [StringComparison]::Ordinal)
if ($pushIndex -lt 0 -or $coordinateIndex -lt 0 -or $coordinateIndex -gt $pushIndex) {
    throw 'August 19 build could not prove that the final August 18 coordinate repair runs before compilation.'
}

$august19Block = @'
Write-Host "`nVerifying completed August 18 staged sources before adding August 19..." -ForegroundColor Cyan
$august19VertexRepair = Join-Path $repo 'scripts\Repair-August19-VertexSettingOutIntervalsAndAlignments-Civil3D2023.ps1'
$august19SewerBatchStage = Join-Path $repo 'scripts\Repair-August19-PrepareSewerGenericBatchStage.ps1'
$august19SurveyGridSewer = Join-Path $repo 'scripts\Repair-August19-SurveyGridSurfacesAndSewerMulti-Civil3D2023.ps1'
$august19SewerPartsResolution = Join-Path $repo 'scripts\Repair-August19-SewerPartsListResolution-Civil3D2023.ps1'
$august19CadastralSewer = Join-Path $repo 'scripts\Repair-August19-CadastralSewerRouting-Civil3D2023.ps1'
$august19VertexCompilerFix = Join-Path $repo 'scripts\Repair-August19-VertexHeadingCompilerFix.ps1'
$august19SiteGridCompileFinalizer = Join-Path $repo 'scripts\Repair-August19-SiteGridCompileFinalizer.ps1'
if (-not (Test-Path -LiteralPath $august19VertexRepair -PathType Leaf)) {
    throw "August 19 Vertex Setting-Out repair not found in staged repository: $august19VertexRepair"
}
if (-not (Test-Path -LiteralPath $august19SewerBatchStage -PathType Leaf)) {
    throw "August 19 staged generic Sewer adapter not found in staged repository: $august19SewerBatchStage"
}
if (-not (Test-Path -LiteralPath $august19SurveyGridSewer -PathType Leaf)) {
    throw "August 19 Survey/Grid/Sewer repair not found in staged repository: $august19SurveyGridSewer"
}
if (-not (Test-Path -LiteralPath $august19SewerPartsResolution -PathType Leaf)) {
    throw "August 19 Sewer parts-list resolver not found in staged repository: $august19SewerPartsResolution"
}
if (-not (Test-Path -LiteralPath $august19CadastralSewer -PathType Leaf)) {
    throw "August 19 cadastral Sewer routing repair not found in staged repository: $august19CadastralSewer"
}
if (-not (Test-Path -LiteralPath $august19VertexCompilerFix -PathType Leaf)) {
    throw "August 19 Vertex compiler finalizer not found in staged repository: $august19VertexCompilerFix"
}
if (-not (Test-Path -LiteralPath $august19SiteGridCompileFinalizer -PathType Leaf)) {
    throw "August 19 Site Grid compile finalizer not found in staged repository: $august19SiteGridCompileFinalizer"
}
& $august19VertexRepair -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Adapting the final staged generic Sewer batch to the true multi-source engine..." -ForegroundColor Cyan
& $august19SewerBatchStage -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying August 19 Site Grid visibility, Grid surface and Sewer multi-source fixes..." -ForegroundColor Cyan
& $august19SurveyGridSewer -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying August 19 Sewer Parts List family/size resolution..." -ForegroundColor Cyan
& $august19SewerPartsResolution -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying August 19 cadastral Sewer low-point routing and separate Sewer route workflows..." -ForegroundColor Cyan
& $august19CadastralSewer -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Finalizing August 19 Vertex table headings for compilation..." -ForegroundColor Cyan
& $august19VertexCompilerFix -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Normalizing final August 19 Site Grid compile block..." -ForegroundColor Cyan
& $august19SiteGridCompileFinalizer -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "August 19 source layer applied only after the complete August 18 build mutations." -ForegroundColor Green

'@ -replace "`n","`r`n"

$runtimeText = $legacyText.Substring(0,$pushIndex) + $august19Block + $legacyText.Substring($pushIndex)

# Parse the generated runtime script before execution. The original August 18
# build file itself is never edited.
[System.IO.File]::WriteAllText($runtime,$runtimeText,(New-Object System.Text.UTF8Encoding($false)))
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $runtime,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object {
        'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message
    }) -join ' | '
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
    throw "August 19 generated build script has a PowerShell syntax error: $details"
}

$invoke = @{
    Configuration = $Configuration
}
if ($SkipInstall) { $invoke.SkipInstall = $true }
if ($Clean) { $invoke.Clean = $true }
if (-not [string]::IsNullOrWhiteSpace($SourceCommit)) {
    $invoke.SourceCommit = $SourceCommit
}

try {
    & $runtime @invoke
    if ($LASTEXITCODE -ne 0) {
        throw "August 19 staged build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
}
