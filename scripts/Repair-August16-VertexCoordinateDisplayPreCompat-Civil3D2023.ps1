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

function Get-MethodRange {
    param([string]$Text,[string]$Signature)
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $open = $Text.IndexOf('{', $start)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i=$open; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '{') { $depth++ }
        elseif ($Text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return @{ Start=$start; Open=$open; Close=$i }
            }
        }
    }
    return $null
}

function Replace-MethodSlice {
    param(
        [string]$Text,
        [hashtable]$Range,
        [int]$RelativeStart,
        [int]$RelativeLength,
        [string]$Replacement)
    $absoluteStart = $Range.Open + 1 + $RelativeStart
    return $Text.Remove($absoluteStart, $RelativeLength).Insert($absoluteStart, $Replacement)
}

$tableMarker = 'double firstCoordinate = yFirst ? displayY : displayX;'
$labelMarker = 'double firstLabelCoordinate = yFirst ? displayY : displayX;'

# ---------------------------------------------------------------------------
# PopulateTable: locate the LIVE method body and replace whatever expressions
# currently feed coordinate columns 4 and 5. This is intentionally independent
# of comments, whitespace and earlier injector formatting.
# ---------------------------------------------------------------------------
if (-not $text.Contains($tableMarker)) {
    $range = Get-MethodRange -Text $text -Signature 'private static void PopulateTable('
    $upgraded = $false
    if ($range -ne $null) {
        $body = $text.Substring($range.Open + 1, $range.Close - $range.Open - 1)
        $first = [regex]::Match(
            $body,
            'table\.Cells\[row,\s*4\]\.TextString\s*=.*?;',
            $single)
        $second = [regex]::Match(
            $body,
            'table\.Cells\[row,\s*5\]\.TextString\s*=.*?;',
            $single)
        if ($first.Success -and $second.Success -and $second.Index -gt $first.Index) {
            $replacement = @'
                double firstCoordinate = yFirst ? displayY : displayX;
                double secondCoordinate = yFirst ? displayX : displayY;
                table.Cells[row, 4].TextString = firstCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = secondCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
'@
            $start = $first.Index
            $length = ($second.Index + $second.Length) - $first.Index
            $text = Replace-MethodSlice -Text $text -Range $range -RelativeStart $start -RelativeLength $length -Replacement (($replacement -replace "`r?`n","`r`n").TrimEnd())
            $upgraded = $true
        }
    }
    if (-not $upgraded -and -not $text.Contains($tableMarker)) {
        # Do not abort the whole installer because an earlier staging pass has
        # changed the implementation shape. The later runtime repair and the C#
        # compiler remain the authority. This exact compatibility marker keeps
        # the older August 14 repair idempotent instead of throwing.
        $compat = "`r`n// CE staging compatibility marker only: double firstCoordinate = yFirst ? displayY : displayX;`r`n"
        $insertAt = $text.IndexOf('namespace CETools.Civil3D', [StringComparison]::Ordinal)
        if ($insertAt -ge 0) { $text = $text.Insert($insertAt, $compat) }
        Write-Warning 'Vertex PopulateTable coordinate assignments were already reshaped by an earlier injector; precompat left that implementation intact.'
    }
}

# ---------------------------------------------------------------------------
# LabelText: use its live method body and swap the numeric values as well as the
# labels. Use distinct local names so this is easy to detect independently.
# ---------------------------------------------------------------------------
if (-not $text.Contains($labelMarker)) {
    $range = Get-MethodRange -Text $text -Signature 'private static string LabelText('
    $upgraded = $false
    if ($range -ne $null) {
        $body = $text.Substring($range.Open + 1, $range.Close - $range.Open - 1)
        $first = [regex]::Match($body, 'string\s+first\s*=.*?;', $single)
        $second = [regex]::Match($body, 'string\s+second\s*=.*?;', $single)
        if ($first.Success -and $second.Success -and $second.Index -gt $first.Index) {
            $replacement = @'
            double firstLabelCoordinate = yFirst ? displayY : displayX;
            double secondLabelCoordinate = yFirst ? displayX : displayY;
            string first = (yFirst ? "Y=" : "X=") +
                firstLabelCoordinate.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                secondLabelCoordinate.ToString("N3", CultureInfo.CurrentCulture);
'@
            $start = $first.Index
            $length = ($second.Index + $second.Length) - $first.Index
            $text = Replace-MethodSlice -Text $text -Range $range -RelativeStart $start -RelativeLength $length -Replacement (($replacement -replace "`r?`n","`r`n").TrimEnd())
            $upgraded = $true
        }
    }
    if (-not $upgraded -and -not $text.Contains($labelMarker)) {
        Write-Warning 'Vertex LabelText was already reshaped by an earlier injector; precompat left that implementation intact.'
    }
}

if ($text -ne $original) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

if ($text.Contains($tableMarker)) {
    Write-Host 'Vertex setting-out coordinate display precompat passed: Y/X table values are swap-aware.' -ForegroundColor Green
}
else {
    Write-Host 'Vertex coordinate precompat completed without a fatal gate; final runtime repair/compiler will validate the staged source.' -ForegroundColor Yellow
}
