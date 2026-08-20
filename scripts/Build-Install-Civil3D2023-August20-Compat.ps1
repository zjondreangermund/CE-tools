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
$build = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-August19.ps1'

foreach ($required in @($preflight,$build)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 20 compatibility build prerequisite missing: $required"
    }
}

Write-Host "`nPreparing current Survey Production menu for the August 20 finalizer..." -ForegroundColor Cyan
& $preflight -RepoRoot $repo
$global:LASTEXITCODE = 0

$invoke = @{ Configuration = $Configuration }
if ($SkipInstall) { $invoke.SkipInstall = $true }
if ($Clean) { $invoke.Clean = $true }
if (-not [string]::IsNullOrWhiteSpace($SourceCommit)) { $invoke.SourceCommit = $SourceCommit }

& $build @invoke
if ($LASTEXITCODE -ne 0) {
    throw "August 20 compatibility build failed with exit code $LASTEXITCODE."
}
