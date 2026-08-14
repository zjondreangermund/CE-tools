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
# corner-safe X/Y display coordinate directly after every matching display-prefix
# declaration. Each declaration lives inside its own loop scope, so repeated edge
# loops remain valid and get the same corner treatment.
if (-not $text.Contains('double xLabel = xValues[xIndex];')) {
    $xPrefixPattern = '(string\s+prefix\s*=\s*settings\.ReverseXY\s*\?\s*"Y:\s*"\s*:\s*"X:\s*"\s*;)'
    $xInsert = '$1' + "`r`n" + '                double xLabel = xValues[xIndex];' + "`r`n" + '                if (xIndex == 0) xLabel += cornerClearance;' + "`r`n" + '                else if (xIndex == xValues.Count - 1) xLabel -= cornerClearance;'
    $text = [regex]::Replace($text, $xPrefixPattern, $xInsert)
}

if (-not $text.Contains('double yLabel = yValues[yIndex];')) {
    $yPrefixPattern = '(string\s+prefix\s*=\s*settings\.ReverseXY\s*\?\s*"X:\s*"\s*:\s*"Y:\s*"\s*;)'
    $yInsert = '$1' + "`r`n" + '                double yLabel = yValues[yIndex];' + "`r`n" + '                if (yIndex == 0) yLabel += cornerClearance;' + "`r`n" + '                else if (yIndex == yValues.Count - 1) yLabel -= cornerClearance;'
    $text = [regex]::Replace($text, $yPrefixPattern, $yInsert)
}

# Use the corner-safe display coordinate for label placement. These replacements
# intentionally target only CreateLabel point constructors that already use the
# inside grid offsets; grid-line geometry and generated setting-out points remain
# at the true survey coordinates.
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

# The downstream final repair uses these as idempotency markers. Do not allow a
# formatting-only difference in an earlier staging pass to stop the build again.
if (-not $text.Contains('double xLabel = xValues[xIndex];')) {
    throw 'Survey Site Grid X-label loop could not be normalized.'
}
if (-not $text.Contains('double yLabel = yValues[yIndex];')) {
    throw 'Survey Site Grid Y-label loop could not be normalized.'
}
if (-not $text.Contains('cornerClearance')) {
    throw 'Survey Site Grid corner-clearance normalization failed.'
}

$text = $text -replace "`r?`n", "`r`n"
if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

Write-Host 'Survey Site Grid label staging normalized by structure; X/Y labels are corner-safe and annotative.' -ForegroundColor Green
