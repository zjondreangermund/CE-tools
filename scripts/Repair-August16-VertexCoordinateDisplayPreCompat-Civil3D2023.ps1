[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\VertexSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Vertex setting-out source was not found: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$original = $text
$single = [System.Text.RegularExpressions.RegexOptions]::Singleline

# Earlier injectors can reformat the old comment and assignment block. Upgrade
# by the actual table assignments instead of one historical multi-line string.
# IMPORTANT: these are PowerShell single-quoted regex strings, so regex escapes
# use ONE backslash (\.), not the C#/JSON-style doubled form (\\.).
$tableMarker = 'double firstCoordinate = yFirst ? displayY : displayX;'
if (-not $text.Contains($tableMarker)) {
    $tablePattern = 'table\.Cells\[row,\s*4\]\.TextString\s*=\s*displayX\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;\s*table\.Cells\[row,\s*5\]\.TextString\s*=\s*displayY\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;'
    $tableReplacement = @'
                double firstCoordinate = yFirst ? displayY : displayX;
                double secondCoordinate = yFirst ? displayX : displayY;
                table.Cells[row, 4].TextString = firstCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = secondCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
'@

    if ([regex]::IsMatch($text, $tablePattern, $single)) {
        $text = [regex]::Replace(
            $text,
            $tablePattern,
            ($tableReplacement -replace "`r?`n","`r`n").TrimEnd(),
            $single)
    }
    else {
        # Fallback for harmless formatting differences: locate each old value
        # assignment independently and replace the complete pair between them.
        $firstPattern = 'table\.Cells\[row,\s*4\]\.TextString\s*=\s*displayX\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;'
        $secondPattern = 'table\.Cells\[row,\s*5\]\.TextString\s*=\s*displayY\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;'
        $firstMatch = [regex]::Match($text, $firstPattern, $single)
        $secondMatch = [regex]::Match($text, $secondPattern, $single)
        if ($firstMatch.Success -and $secondMatch.Success -and $secondMatch.Index -gt $firstMatch.Index) {
            $betweenStart = $firstMatch.Index
            $betweenLength = ($secondMatch.Index + $secondMatch.Length) - $betweenStart
            $text = $text.Remove($betweenStart, $betweenLength).Insert(
                $betweenStart,
                ($tableReplacement -replace "`r?`n","`r`n").TrimEnd())
        }
    }
}

# Upgrade LabelText independently. Do not let the table marker cause this second
# conversion to be skipped: both the table and annotation must swap real values.
$labelMarker = 'double firstLabelCoordinate = yFirst ? displayY : displayX;'
if (-not $text.Contains($labelMarker)) {
    $labelPattern = 'string\s+first\s*=\s*\(yFirst\s*\?\s*"Y="\s*:\s*"X="\)\s*\+\s*displayX\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;\s*string\s+second\s*=\s*\(yFirst\s*\?\s*"X="\s*:\s*"Y="\)\s*\+\s*displayY\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;'
    $labelReplacement = @'
            double firstLabelCoordinate = yFirst ? displayY : displayX;
            double secondLabelCoordinate = yFirst ? displayX : displayY;
            string first = (yFirst ? "Y=" : "X=") +
                firstLabelCoordinate.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                secondLabelCoordinate.ToString("N3", CultureInfo.CurrentCulture);
'@
    if ([regex]::IsMatch($text, $labelPattern, $single)) {
        $text = [regex]::Replace(
            $text,
            $labelPattern,
            ($labelReplacement -replace "`r?`n","`r`n").TrimEnd(),
            $single)
    }
}

# This marker is deliberately the same marker consumed by the older August 14
# runtime-field repair. Establishing it here makes that older repair idempotent.
if (-not $text.Contains($tableMarker)) {
    throw 'Vertex table true X/Y display swap was not established. Current source did not contain the old displayX/displayY assignments or the upgraded marker.'
}

if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

Write-Host 'Vertex setting-out coordinate display is staging-safe: Y/X order swaps the real displayed numeric values.' -ForegroundColor Green
