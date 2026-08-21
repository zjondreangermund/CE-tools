[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$geometry = Join-Path $root 'src\CE.Tools.Civil3D\August20GeometryFirstSewerCommands.cs'
if (-not (Test-Path -LiteralPath $geometry -PathType Leaf)) {
    throw "Final fatal-safety bootstrap cannot find geometry-first source: $geometry"
}

# A direct developer MSBuild may not have gone through the historical staged
# installer mutation sequence. If the raw bridge tokens are still present, run the
# geometry-first bridge repair now. In the normal ZIP installer the bridge is
# already applied, so this is a no-op.
$text = [System.IO.File]::ReadAllText($geometry)
if ($text.Contains('CE_AUG20MIDBLOCKBRIDGE') -or
    $text.Contains('CE_AUG20ROADRESERVEBRIDGE') -or
    $text.Contains('CE_AUG20ROADCENTERBRIDGE')) {
    $geometryRepair = Join-Path $root 'scripts\Repair-August20-GeometryFirstSewerAndDynamicSiteGrid-Civil3D2023.ps1'
    if (-not (Test-Path -LiteralPath $geometryRepair -PathType Leaf)) {
        throw "Final fatal-safety bootstrap cannot find geometry-first repair: $geometryRepair"
    }
    & $geometryRepair -RepoRoot $root
    if ($LASTEXITCODE -ne 0) { throw "Geometry-first bridge repair failed with exit code $LASTEXITCODE." }
    $global:LASTEXITCODE = 0
}

$finalizer = Join-Path $root 'scripts\Repair-August21-CrossDisciplineFatalSafety-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalizer -PathType Leaf)) {
    throw "Final fatal-safety pass not found: $finalizer"
}
& $finalizer -RepoRoot $root
if ($LASTEXITCODE -ne 0) { throw "Final fatal-safety pass failed with exit code $LASTEXITCODE." }
$global:LASTEXITCODE = 0

Write-Host 'Final Civil 3D fatal-safety boundary applied immediately before compilation.' -ForegroundColor Green
