[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Survey Site Grid source was not found: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path)
$original = $text
$single = [System.Text.RegularExpressions.RegexOptions]::Singleline

# Normalize the label clearance without depending on the exact historical value.
$insidePattern = 'double\s+insideOffset\s*=\s*Math\.Max\(\s*modelTextHeight\s*\*\s*[0-9.]+\s*,\s*0\.001\s*\)\s*;'
if ([regex]::IsMatch($text, $insidePattern)) {
    $inside = 'double insideOffset = Math.Max(modelTextHeight * 2.75, 0.001);'
    if (-not $text.Contains('double cornerClearance =')) {
        $inside += "`r`n            double cornerClearance = Math.Max(modelTextHeight * 4.5, 0.001);"
    }
    $text = [regex]::Replace($text, $insidePattern, $inside, $single)
}
elseif (-not $text.Contains('double cornerClearance =')) {
    throw 'Survey Site Grid inside-offset declaration could not be located.'
}

# Earlier staging passes can add top/bottom/left/right label loops. Insert the
# corner-safe label location and the REAL displayed coordinate value directly
# after every matching prefix declaration. Reverse mode is the Namibia survey
# convention requested in field testing: displayed Y=-drawing X and X=-drawing Y.
if (-not $text.Contains('double xDisplayValue = settings.ReverseXY ? -xValues[xIndex] : xValues[xIndex];')) {
    $xPrefixPattern = '(string\s+prefix\s*=\s*settings\.ReverseXY\s*\?\s*"Y:\s*"\s*:\s*"X:\s*"\s*;)'
    $xInsert = '$1' + "`r`n" + '                double xDisplayValue = settings.ReverseXY ? -xValues[xIndex] : xValues[xIndex];' + "`r`n" + '                double xLabel = xValues[xIndex];' + "`r`n" + '                if (xIndex == 0) xLabel += cornerClearance;' + "`r`n" + '                else if (xIndex == xValues.Count - 1) xLabel -= cornerClearance;'
    $text = [regex]::Replace($text, $xPrefixPattern, $xInsert)
}
elseif (-not $text.Contains('double xLabel = xValues[xIndex];')) {
    throw 'Survey Site Grid X label location variable is missing.'
}

if (-not $text.Contains('double yDisplayValue = settings.ReverseXY ? -yValues[yIndex] : yValues[yIndex];')) {
    $yPrefixPattern = '(string\s+prefix\s*=\s*settings\.ReverseXY\s*\?\s*"X:\s*"\s*:\s*"Y:\s*"\s*;)'
    $yInsert = '$1' + "`r`n" + '                double yDisplayValue = settings.ReverseXY ? -yValues[yIndex] : yValues[yIndex];' + "`r`n" + '                double yLabel = yValues[yIndex];' + "`r`n" + '                if (yIndex == 0) yLabel += cornerClearance;' + "`r`n" + '                else if (yIndex == yValues.Count - 1) yLabel -= cornerClearance;'
    $text = [regex]::Replace($text, $yPrefixPattern, $yInsert)
}
elseif (-not $text.Contains('double yLabel = yValues[yIndex];')) {
    throw 'Survey Site Grid Y label location variable is missing.'
}

# Replace the numeric value shown beside X/Y labels. This changes presentation
# only; grid lines, setting-out points and source geometry stay at true XY.
$text = [regex]::Replace(
    $text,
    'prefix\s*\+\s*xValues\[xIndex\]\.ToString\("0\.###",\s*CultureInfo\.InvariantCulture\)',
    'prefix + xDisplayValue.ToString("0.###", CultureInfo.InvariantCulture)')
$text = [regex]::Replace(
    $text,
    'prefix\s*\+\s*yValues\[yIndex\]\.ToString\("0\.###",\s*CultureInfo\.InvariantCulture\)',
    'prefix + yDisplayValue.ToString("0.###", CultureInfo.InvariantCulture)')

# Use the corner-safe true coordinate for label placement.
$text = [regex]::Replace(
    $text,
    'new\s+Point3d\(\s*xValues\[xIndex\]\s*,\s*(bounds\.(?:MinY\s*\+|MaxY\s*-)\s*insideOffset)',
    'new Point3d(`r`n                        xLabel,`r`n                        $1',
    $single)
$text = [regex]::Replace(
    $text,
    'new\s+Point3d\(\s*(bounds\.(?:MinX\s*\+|MaxX\s*-)\s*insideOffset)\s*,\s*yValues\[yIndex\]',
    'new Point3d(`r`n                        $1,`r`n                        yLabel',
    $single)

# Site-grid labels must participate in CE annotation-scale synchronisation.
if (-not $text.Contains('PaperAnnotationScale.SetAnnotative(label);')) {
    $rotationPattern = 'label\.Rotation\s*=\s*rotation\s*;\s*(?=return\s+label\s*;)'
    $rotationReplacement = 'label.Rotation = rotation;' + "`r`n" + '            PaperAnnotationScale.SetAnnotative(label);' + "`r`n" + '            '
    $text = [regex]::Replace($text, $rotationPattern, $rotationReplacement, $single)
}

$required = @(
    'double xLabel = xValues[xIndex];',
    'double yLabel = yValues[yIndex];',
    'double xDisplayValue = settings.ReverseXY ? -xValues[xIndex] : xValues[xIndex];',
    'double yDisplayValue = settings.ReverseXY ? -yValues[yIndex] : yValues[yIndex];',
    'cornerClearance'
)
foreach ($marker in $required) {
    if (-not $text.Contains($marker)) { throw "Survey Site Grid normalization failed: $marker" }
}

$text = $text -replace "`r?`n", "`r`n"
if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

Write-Host 'Survey Site Grid normalized: labels are corner-safe/annotative and reverse mode swaps the displayed X/Y numeric values.' -ForegroundColor Green
