[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\MultiDimensionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Multiple Dimensions source missing for chain-dispatch compatibility: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$leaderBlock = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));
'@ -replace "`r?`n", "`r`n"
$unitsBlock = @'
            bool outputMillimetres = string.Equals(
                settings.Text("ValueUnits"),
                "Millimetres",
                StringComparison.OrdinalIgnoreCase);
            double measurementFactor = outputMillimetres ? 1000.0 : 1.0;
'@ -replace "`r?`n", "`r`n"
$sourcesMarker = '            int sources = 0;'

$leaderIndex = $text.IndexOf($leaderBlock,[StringComparison]::Ordinal)
$sourcesIndex = $text.IndexOf($sourcesMarker,[StringComparison]::Ordinal)
$unitsIndex = $text.IndexOf($unitsBlock,[StringComparison]::Ordinal)
if ($leaderIndex -lt 0 -or $sourcesIndex -lt 0) {
    throw 'Multiple Dimensions ArcLeader/sources chain anchors were not found.'
}

if ($unitsIndex -ge 0 -and $unitsIndex -gt $leaderIndex -and $unitsIndex -lt $sourcesIndex) {
    $text = $text.Remove($unitsIndex,$unitsBlock.Length)
    $sourcesIndex = $text.IndexOf($sourcesMarker,[StringComparison]::Ordinal)
    if ($sourcesIndex -lt 0) {
        throw 'Multiple Dimensions sources marker was lost while normalizing unit declarations.'
    }
    $insertAt = $sourcesIndex + $sourcesMarker.Length
    $text = $text.Insert($insertAt,"`r`n" + $unitsBlock)
}

[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$legacyAnchor = $leaderBlock + "`r`n`r`n" + $sourcesMarker
$chainDispatch = 'if (string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase))'
if (-not $check.Contains($chainDispatch) -and -not $check.Contains($legacyAnchor)) {
    throw 'Multiple Dimensions chain-dispatch compatibility could not restore the historical ArcLeader/sources anchor.'
}
if ($check.Contains('bool outputMillimetres = string.Equals(') -and
    -not $check.Contains('double measurementFactor = outputMillimetres ? 1000.0 : 1.0;')) {
    throw 'Multiple Dimensions measurement-unit declarations are incomplete after chain compatibility normalization.'
}

Write-Host 'Multiple Dimensions chain-dispatch compatibility normalized for the final pre-build pass.' -ForegroundColor Green
