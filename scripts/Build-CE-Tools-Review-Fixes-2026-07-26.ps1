[CmdletBinding()]
param(
    [ValidateSet("2023", "2024", "All")]
    [string]$Version = "2024",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$AutoCADRoot
)

$ErrorActionPreference = "Stop"

function Invoke-RequiredScript {
    param([string]$FileName)
    $path = Join-Path $PSScriptRoot $FileName
    if (-not (Test-Path $path)) { throw "Required script is missing: $path" }
    & $path
}

Write-Host "Preparing Phase 8 plus the 26 July 2026 runtime review fixes..." -ForegroundColor Cyan
Invoke-RequiredScript "Invoke-Comments-Normalizer.ps1"
Invoke-RequiredScript "Apply-Comments-Discipline.ps1"
Invoke-RequiredScript "Apply-Comments-Quantities.ps1"
Invoke-RequiredScript "Apply-Comments-Road.ps1"
Invoke-RequiredScript "Invoke-Master-Items-Phase1.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-Quantities.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-ProfileViews.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-DetailedSections.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-ModelReport.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-XrefProject.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase3.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase4.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase5.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase5-Accessible.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase6.ps1"
Invoke-RequiredScript "Invoke-Master-Items-Phase7.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase8.ps1"
Invoke-RequiredScript "Apply-Review-Fixes-2026-07-26.ps1"
Invoke-RequiredScript "Apply-Review-Fixes-2026-07-26-Extension.ps1"
Invoke-RequiredScript "Apply-Civil3D-Compatibility.ps1"
Invoke-RequiredScript "Apply-CSharp-MText-Escape-Fixes.ps1"

$compileOnlyBuild = Join-Path $PSScriptRoot "Build-CE-Tools-Compile-Only.ps1"
if (-not (Test-Path $compileOnlyBuild)) {
    throw "The compile-only build script is missing: $compileOnlyBuild"
}

if ([string]::IsNullOrWhiteSpace($AutoCADRoot)) {
    & $compileOnlyBuild -Version $Version -Configuration $Configuration
}
else {
    & $compileOnlyBuild `
        -Version $Version `
        -Configuration $Configuration `
        -AutoCADRoot $AutoCADRoot
}

if ($LASTEXITCODE -ne 0) {
    throw "The 26 July 2026 review-fix build failed."
}
Write-Host "26 July 2026 review-fix build completed." -ForegroundColor Green
