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
if (-not $text.Contains('double firstCoordinate = yFirst ? displayY : displayX;')) {
    $tablePattern = 'table\.Cells\[row,\s*4\]\.TextString\s*=\s*displayX\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;\s*table\.Cells\[row,\s*5\]\.TextString\s*=\s*displayY\s*\.ToString\("N3",\s*CultureInfo\.CurrentCulture\)\s*;'
    $tableReplacement = @'
                double firstCoordinate = yFirst ? displayY : displayX;
                double secondCoordinate = yFirst ? displayX : displayY;
                table.Cells[row, 4].TextString = firstCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = secondCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
'@
    if (-not [regex]::IsMatch($text, $tablePattern, $single)) {
        throw 'Vertex table coordinate assignments could not be located structurally.'
    }
    $text = [regex]::Replace($text, $tablePattern, ($tableReplacement -replace "`r?`n","`r`n").TrimEnd(), $single)
}

# Upgrade LabelText independently. Do not let the table marker cause this second
# conversion to be skipped: both the table and annotation must swap real values.
if (-not $text.Contains('double firstLabelCoordinate = yFirst ? displayY : displayX;')) {
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
        $text = [regex]::Replace($text, $labelPattern, ($labelReplacement -replace "`r?`n","`r`n").TrimEnd(), $single)
    }
}

if (-not $text.Contains('double firstCoordinate = yFirst ? displayY : displayX;')) {
    throw 'Vertex table true X/Y display swap was not established.'
}

if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

Write-Host 'Vertex setting-out coordinate display is staging-safe: Y/X order swaps the real displayed numeric values.' -ForegroundColor Green
