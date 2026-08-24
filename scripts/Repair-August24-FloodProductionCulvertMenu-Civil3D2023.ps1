[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August14StructuredDisciplineProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Flood Production centre source missing: $path"
}

$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$command = 'CE_FLOODCULVERTDESIGN'
if (-not $text.Contains($command)) {
    $anchor = '                    A("CE-Quick Flood / Rational Review", "CE_FLOODQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),'
    $index = $text.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw 'August 24 Flood Production insertion anchor missing.'
    }
    $entry = '                    A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN", "Low point, longest watercourse, catchment, Q2-Q100, culvert sizing, water levels and Hydraflow snapshot review.", "03 CREATE"),' + "`r`n"
    $text = $text.Insert($index,$entry)
}

if (-not $text.Contains('A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN"')) {
    throw 'August 24 Flood Production culvert design menu guard missing.'
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 24 Flood Production catchment/culvert command present in final workflow menu.' -ForegroundColor Green
