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

Write-Host "Preparing the current Master Items Phase 2 source..." -ForegroundColor Cyan
Invoke-RequiredScript "Invoke-Comments-Normalizer.ps1"
Invoke-RequiredScript "Apply-Comments-Discipline.ps1"
Invoke-RequiredScript "Apply-Comments-Quantities.ps1"
Invoke-RequiredScript "Apply-Comments-Road.ps1"
Invoke-RequiredScript "Invoke-Master-Items-Phase1.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-Quantities.ps1"
Invoke-RequiredScript "Apply-Master-Items-Phase2-ProfileViews.ps1"

$baseBuild = Join-Path $PSScriptRoot "Build-CE-Tools-Master-Items.ps1"
if (-not (Test-Path $baseBuild)) {
    throw "The Master Items build script is missing: $baseBuild"
}

if ([string]::IsNullOrWhiteSpace($AutoCADRoot)) {
    & $baseBuild -Version $Version -Configuration $Configuration
}
else {
    & $baseBuild `
        -Version $Version `
        -Configuration $Configuration `
        -AutoCADRoot $AutoCADRoot
}

if ($LASTEXITCODE -ne 0) {
    throw "The current Master Items Phase 2 build failed."
}

Write-Host "Current Master Items Phase 2 build completed." -ForegroundColor Green
