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
$preflight = Join-Path $PSScriptRoot 'Repair-August20-SurveyProductionMenuPreflight.ps1'
$lateSafety = Join-Path $PSScriptRoot 'Repair-August20-SewerFatalAndSiteGridVisibility-Civil3D2023.ps1'
$geometryFirst = Join-Path $PSScriptRoot 'Repair-August20-GeometryFirstSewerAndDynamicSiteGrid-Civil3D2023.ps1'
$multiDimensionFinal = Join-Path $PSScriptRoot 'Repair-August20-MultiDimensionCircleUnitsAndProperties-Civil3D2023.ps1'
$crossDisciplineFatalSafety = Join-Path $PSScriptRoot 'Repair-August20-BackgroundAndCrossDisciplineFatalSafety-Civil3D2023.ps1'
$runtimeStabilityWorkflow = Join-Path $PSScriptRoot 'Repair-August20-RuntimeStabilityWorkflowPass-Civil3D2023.ps1'
$build = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-August19.ps1'
$runtime = Join-Path $PSScriptRoot '.Build-Install-Civil3D2023-August20-Compat.runtime.ps1'

foreach ($required in @($preflight,$lateSafety,$geometryFirst,$multiDimensionFinal,$crossDisciplineFatalSafety,$runtimeStabilityWorkflow,$build)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 20 compatibility build prerequisite missing: $required"
    }
}

Write-Host "`nPreparing current Survey Production menu for the August 20 finalizer..." -ForegroundColor Cyan
& $preflight -RepoRoot $repo
$global:LASTEXITCODE = 0

# Build-Install-Civil3D2023-August19.ps1 owns the complete staged August 18/19/20
# mutation order. Do not edit that preserved pipeline here. Create a temporary
# runtime copy and insert the field fatal/site-grid guard, geometry-first repair,
# Multiple Dimensions repair, Background/cross-discipline fatal-safety pass, then
# the final runtime-stability/workflow pass immediately before MSBuild.
$text = [System.IO.File]::ReadAllText($build) -replace "`r?`n","`r`n"
$anchor = @'
& $august20FieldStability -RepoRoot $repo
$global:LASTEXITCODE = 0
'@.Trim() -replace "`r?`n","`r`n"
if (-not $text.Contains($anchor)) {
    throw 'August 20 compatibility build could not locate the existing field-stability finalizer anchor.'
}
$injected = @'
& $august20FieldStability -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying late Sewer fatal-safety and Site Grid field-visibility guard..." -ForegroundColor Cyan
$august20LateSafety = Join-Path $repo 'scripts\Repair-August20-SewerFatalAndSiteGridVisibility-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $august20LateSafety -PathType Leaf)) {
    throw "August 20 late field-safety repair not found in staged repository: $august20LateSafety"
}
& $august20LateSafety -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying geometry-first Sewer/Road Centreline workflows and dynamic Site Grid repair..." -ForegroundColor Cyan
$august20GeometryFirst = Join-Path $repo 'scripts\Repair-August20-GeometryFirstSewerAndDynamicSiteGrid-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $august20GeometryFirst -PathType Leaf)) {
    throw "August 20 geometry-first/dynamic repair not found in staged repository: $august20GeometryFirst"
}
& $august20GeometryFirst -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying final Multiple Dimensions circle/unit/property repair..." -ForegroundColor Cyan
$august20MultiDimensionFinal = Join-Path $repo 'scripts\Repair-August20-MultiDimensionCircleUnitsAndProperties-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $august20MultiDimensionFinal -PathType Leaf)) {
    throw "August 20 Multiple Dimensions final repair not found in staged repository: $august20MultiDimensionFinal"
}
& $august20MultiDimensionFinal -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying final Background and cross-discipline fatal-safety repair..." -ForegroundColor Cyan
$august20CrossDisciplineFatalSafety = Join-Path $repo 'scripts\Repair-August20-BackgroundAndCrossDisciplineFatalSafety-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $august20CrossDisciplineFatalSafety -PathType Leaf)) {
    throw "August 20 Background/cross-discipline fatal-safety repair not found in staged repository: $august20CrossDisciplineFatalSafety"
}
& $august20CrossDisciplineFatalSafety -RepoRoot $repo
$global:LASTEXITCODE = 0
Write-Host "Applying final August 20 runtime-stability/workflow pass..." -ForegroundColor Cyan
$august20RuntimeStabilityWorkflow = Join-Path $repo 'scripts\Repair-August20-RuntimeStabilityWorkflowPass-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $august20RuntimeStabilityWorkflow -PathType Leaf)) {
    throw "August 20 runtime-stability/workflow finalizer not found in staged repository: $august20RuntimeStabilityWorkflow"
}
& $august20RuntimeStabilityWorkflow -RepoRoot $repo
$global:LASTEXITCODE = 0
'@.Trim() -replace "`r?`n","`r`n"
$text = $text.Replace($anchor,$injected)
[System.IO.File]::WriteAllText($runtime,$text,(New-Object System.Text.UTF8Encoding($false)))

$tokens=$null; $parseErrors=$null
[System.Management.Automation.Language.Parser]::ParseFile($runtime,[ref]$tokens,[ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }) -join ' | '
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
    throw "August 20 compatibility runtime build has a PowerShell syntax error: $details"
}

$invoke = @{ Configuration = $Configuration }
if ($SkipInstall) { $invoke.SkipInstall = $true }
if ($Clean) { $invoke.Clean = $true }
if (-not [string]::IsNullOrWhiteSpace($SourceCommit)) { $invoke.SourceCommit = $SourceCommit }

try {
    & $runtime @invoke
    if ($LASTEXITCODE -ne 0) {
        throw "August 20 compatibility build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
}
