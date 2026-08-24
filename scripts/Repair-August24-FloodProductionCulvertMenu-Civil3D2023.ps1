[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$menuPath = Join-Path $src 'August14StructuredDisciplineProductionCentres.cs'
$floodPath = Join-Path $src 'FloodProductionCulvertDesignCommands.cs'
$bridgePath = Join-Path $src 'FloodNativeCatchmentBridge.cs'
foreach ($required in @($menuPath,$floodPath,$bridgePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Flood Production prerequisite missing: $required"
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)

# Historical structured-production repairs can overwrite the Flood Analysis menu.
$menu = [System.IO.File]::ReadAllText($menuPath) -replace "`r?`n", "`r`n"
$command = 'CE_FLOODCULVERTDESIGN'
if (-not $menu.Contains($command)) {
    $anchor = '                    A("CE-Quick Flood / Rational Review", "CE_FLOODQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),'
    $index = $menu.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw 'August 24 Flood Production insertion anchor missing.'
    }
    $entry = '                    A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN", "Low point, longest watercourse, native catchment, Q2-Q100, culvert sizing, water levels and Hydraflow snapshot review.", "03 CREATE"),' + "`r`n"
    $menu = $menu.Insert($index,$entry)
}
if (-not $menu.Contains('A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN"')) {
    throw 'August 24 Flood Production culvert design menu guard missing.'
}
[System.IO.File]::WriteAllText($menuPath,$menu,$utf8)

# Civil 3D 2023 exposes FeatureLinePointType in Autodesk.Civil (not in the
# DatabaseServices namespace). Normalize the authored alias after all old repair
# packs have run, immediately before CoreCompile.
$flood = [System.IO.File]::ReadAllText($floodPath) -replace "`r?`n", "`r`n"
$flood = $flood.Replace(
    'using CivilFeatureLinePointType = Autodesk.Civil.DatabaseServices.FeatureLinePointType;',
    'using CivilFeatureLinePointType = Autodesk.Civil.FeatureLinePointType;')

# Create a native Civil 3D Catchment as part of the same CE command. Keep CE plan
# graphics even when a drawing has no catchment style; the bridge reports that
# condition without corrupting source terrain or centreline objects.
$nativeCall = '                FloodNativeCatchmentBridge.TryCreate(document.Database, result);'
if (-not $flood.Contains($nativeCall)) {
    $anchor = '                CreateDrawingOutput(document.Database, result);'
    $index = $flood.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw 'August 24 Flood Production native Catchment insertion anchor missing.'
    }
    $insertAt = $index + $anchor.Length
    $flood = $flood.Insert($insertAt,"`r`n" + $nativeCall)
}

if ($flood.Contains('Autodesk.Civil.DatabaseServices.FeatureLinePointType')) {
    throw 'August 24 Flood Production Civil 3D 2023 FeatureLinePointType compatibility failed.'
}
if (-not $flood.Contains('using CivilFeatureLinePointType = Autodesk.Civil.FeatureLinePointType;')) {
    throw 'August 24 Flood Production FeatureLinePointType alias missing.'
}
if (([regex]::Matches($flood,[regex]::Escape($nativeCall))).Count -ne 1) {
    throw 'August 24 Flood Production expected exactly one native Catchment bridge call.'
}
[System.IO.File]::WriteAllText($floodPath,$flood,$utf8)

Write-Host 'August 24 Flood Production catchment/culvert workflow finalized for Civil 3D 2023.' -ForegroundColor Green
Write-Host ' - integrated Flood menu entry present.' -ForegroundColor Green
Write-Host ' - FeatureLinePointType qualified for Civil 3D 2023.' -ForegroundColor Green
Write-Host ' - native Civil 3D Catchment bridge chained after CE plan output.' -ForegroundColor Green
