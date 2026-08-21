[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August13RoadProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Road Production source missing for centreline menu compatibility repair: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$pattern = 'A\(\s*"CE-Road Reserve Centrelines"\s*,\s*"(?:CE_ROADRESERVECENTERLINES|CE_ROADCENTERLINEPOLY)"'
$matches = [regex]::Matches($text,$pattern)
if ($matches.Count -ne 1) {
    throw "Road Reserve centreline menu compatibility expected exactly one route; found $($matches.Count)."
}

$replacement = 'A("CE-Road Reserve Centrelines", "CE_ROADCENTERLINEPOLY"'
$text = [regex]::Replace($text,$pattern,$replacement)
[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
$safePattern = 'A\(\s*"CE-Road Reserve Centrelines"\s*,\s*"CE_ROADCENTERLINEPOLY"'
$legacyPattern = 'A\(\s*"CE-Road Reserve Centrelines"\s*,\s*"CE_ROADRESERVECENTERLINES"'
if (-not [regex]::IsMatch($check,$safePattern)) {
    throw 'Road Reserve centreline menu compatibility failed to route to CE_ROADCENTERLINEPOLY.'
}
if ([regex]::IsMatch($check,$legacyPattern)) {
    throw 'Legacy CE_ROADRESERVECENTERLINES route survived Road Production compatibility normalization.'
}

Write-Host 'Road Production Road Reserve centreline route normalized to CE_ROADCENTERLINEPOLY for field recovery.' -ForegroundColor Green
