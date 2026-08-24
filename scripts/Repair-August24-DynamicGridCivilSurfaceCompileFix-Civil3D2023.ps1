[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August18DynamicGridSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 24 Dynamic Grid source missing for Civil Surface compile fix: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$alias = 'using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;'
if (-not $text.Contains($alias)) {
    $usingMarker = 'using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;'
    $usingIndex = $text.IndexOf($usingMarker,[StringComparison]::Ordinal)
    if ($usingIndex -lt 0) {
        throw 'August 24 Dynamic Grid Civil Surface alias insertion marker missing.'
    }
    $insertAt = $usingIndex + $usingMarker.Length
    $text = $text.Insert($insertAt,"`r`n" + $alias)
}

# August 23 injects Civil surface helpers into a source file that already imports
# Autodesk.AutoCAD.DatabaseServices and Autodesk.Civil.DatabaseServices. A bare
# Surface therefore becomes CS0104 during the real Civil 3D compile. Restrict
# qualification to the injected helper block so AutoCAD entities elsewhere are
# never changed.
$startMarker = '        private static List<string> ReadSurfaceNames('
$endMarker = '        private sealed class GridLineSpec'
$start = $text.IndexOf($startMarker,[StringComparison]::Ordinal)
if ($start -ge 0) {
    $end = $text.IndexOf($endMarker,$start + $startMarker.Length,[StringComparison]::Ordinal)
    if ($end -lt 0) {
        throw 'August 24 Dynamic Grid Civil Surface helper end marker missing.'
    }
    $block = $text.Substring($start,$end-$start)
    $block = [regex]::Replace($block,'(?<![A-Za-z0-9_\.])Surface(?![A-Za-z0-9_])','CivilSurface')
    $text = $text.Substring(0,$start) + $block + $text.Substring($end)
}

if (-not $text.Contains($alias)) {
    throw 'August 24 Dynamic Grid Civil Surface alias was not established.'
}
if ($start -ge 0) {
    $checkStart = $text.IndexOf($startMarker,[StringComparison]::Ordinal)
    $checkEnd = $text.IndexOf($endMarker,$checkStart + $startMarker.Length,[StringComparison]::Ordinal)
    $checkBlock = $text.Substring($checkStart,$checkEnd-$checkStart)
    if ([regex]::IsMatch($checkBlock,'(?<![A-Za-z0-9_\.])Surface(?![A-Za-z0-9_])')) {
        throw 'August 24 Dynamic Grid helper still contains an ambiguous bare Surface reference.'
    }
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 24 Dynamic Grid Civil Surface references qualified for compilation.' -ForegroundColor Green
