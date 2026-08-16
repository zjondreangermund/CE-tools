[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August13RoadProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Road Production V2 source was not found: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$original = $text

# Historical August packages used several temporary command names in the Road
# centre. Route every known retired token to the live command owner. Exact quoted
# tokens are used so CE_ROADNAME never corrupts CE_ROADNAMESYNC, etc.
$map = [ordered]@{
    '"CE_ROADOVERLAY"' = '"CE_ROADRESERVECENTERLINES"'
    '"CE_ROADJOINCENTRELINES"' = '"CE_ROADCONTINUITYFIX"'
    '"CE_ROADCURVES"' = '"CE_ROUTEHORIZONTALCURVES"'
    '"CE_ROADOFFSETS"' = '"CE_ROADOFFSET"'
    '"CE_ROADJUNCTIONMULTI"' = '"CE_ROADJUNCTIONBULK"'
    '"CE_ROADNAMING"' = '"CE_ROADNAMES"'
    '"CE_ROADNAME"' = '"CE_ROADNAMES"'
    '"CE_ROADANNOTATION"' = '"CE_ROUTEANNOTATIONSTYLE"'
    '"CE_ROADANNOTATIONSHIFT"' = '"CE_ROUTESHIFTANNOTATION"'
    '"CE_ROADDIM"' = '"CE_ROADDIMENSIONS"'
    '"CE_JUNCTIONMULTI"' = '"CE_JUNCTIONSETTINGOUT4"'
    '"CE_JUNCTIONSETTINGOUT4FIX"' = '"CE_JUNCTIONSETTINGOUT4"'
    '"CE_ROADREFRESH"' = '"CE_ROADLAYOUTREFRESH"'
}
foreach ($entry in $map.GetEnumerator()) {
    $text = $text.Replace([string]$entry.Key, [string]$entry.Value)
}

$required = @(
    '"CE_ROADRESERVECLOSE"',
    '"CE_ROADRESERVECENTERLINES"',
    '"CE_ROADCONTINUITYFIX"',
    '"CE_ROUTEHORIZONTALCURVES"',
    '"CE_ROADOFFSET"',
    '"CE_ROADJUNCTIONBULK"',
    '"CE_ROADJUNCTIONTRIM"',
    '"CE_ROADNAMES"',
    '"CE_ROUTEANNOTATIONSTYLE"',
    '"CE_ROUTESHIFTANNOTATION"',
    '"CE_ROADDIMENSIONS"',
    '"CE_JUNCTIONSETTINGOUT4"',
    '"CE_ROADLAYOUTREFRESH"'
)
foreach ($marker in $required) {
    if (-not $text.Contains($marker)) {
        throw "Road Production live-command wiring marker is missing: $marker"
    }
}

if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

Write-Host 'Road Production V2 now dispatches live Road command owners; retired August aliases are removed from the staged menu.' -ForegroundColor Green
