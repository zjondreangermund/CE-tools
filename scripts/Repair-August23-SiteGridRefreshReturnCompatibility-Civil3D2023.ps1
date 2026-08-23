[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 23 Site Grid RefreshAll compatibility source missing: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$marker = '        internal static int RefreshAll(Document document, ISet<ObjectId> dirtyIds)'
$start = $text.IndexOf($marker,[StringComparison]::Ordinal)
if ($start -lt 0) {
    throw 'August 23 Site Grid RefreshAll compatibility could not locate the int overload.'
}
$second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
if ($second -ge 0) {
    throw 'August 23 Site Grid RefreshAll compatibility found an ambiguous int overload.'
}
$open = $text.IndexOf('{',$start)
if ($open -lt 0) {
    throw 'August 23 Site Grid RefreshAll compatibility could not locate the opening brace.'
}

function Find-MethodClose([string]$source,[int]$openIndex) {
    $depth = 0
    $inString = $false
    $inVerbatim = $false
    $inChar = $false
    $lineComment = $false
    $blockComment = $false
    for ($i = $openIndex; $i -lt $source.Length; $i++) {
        $c = $source[$i]
        $n = if ($i + 1 -lt $source.Length) { $source[$i + 1] } else { [char]0 }
        if ($lineComment) {
            if ($c -eq "`n") { $lineComment = $false }
            continue
        }
        if ($blockComment) {
            if ($c -eq '*' -and $n -eq '/') { $blockComment = $false; $i++ }
            continue
        }
        if ($inVerbatim) {
            if ($c -eq '"') {
                if ($n -eq '"') { $i++; continue }
                $inVerbatim = $false
            }
            continue
        }
        if ($inString) {
            if ($c -eq '\') { $i++; continue }
            if ($c -eq '"') { $inString = $false }
            continue
        }
        if ($inChar) {
            if ($c -eq '\') { $i++; continue }
            if ($c -eq "'") { $inChar = $false }
            continue
        }
        if ($c -eq '/' -and $n -eq '/') { $lineComment = $true; $i++; continue }
        if ($c -eq '/' -and $n -eq '*') { $blockComment = $true; $i++; continue }
        if ($c -eq '@' -and $n -eq '"') { $inVerbatim = $true; $i++; continue }
        if ($c -eq '"') { $inString = $true; continue }
        if ($c -eq "'") { $inChar = $true; continue }
        if ($c -eq '{') { $depth++; continue }
        if ($c -eq '}') {
            $depth--
            if ($depth -eq 0) { return $i }
            if ($depth -lt 0) { break }
        }
    }
    return -1
}

$close = Find-MethodClose $text $open
if ($close -lt 0) {
    throw 'August 23 Site Grid RefreshAll compatibility could not locate the closing brace.'
}
$method = $text.Substring($start,$close - $start + 1)
if (-not $method.Contains('int refreshed = 0;')) {
    throw 'August 23 Site Grid RefreshAll compatibility refuses to guess: the staged int overload has no refreshed accumulator.'
}

# The packaged staged build reached CS0161 after a late field/display mutation left
# this int overload without a total return path. Preserve the complete staged body
# and add only the missing accumulator return. Correct variants remain unchanged.
$body = $text.Substring($open + 1,$close - $open - 1)
$trimmed = $body.TrimEnd()
$hasTerminalReturn = [regex]::IsMatch(
    $trimmed,
    '(?s)(?:return\s+(?:refreshed|0)\s*;|throw\s+[^;]+;)\s*$')
if (-not $hasTerminalReturn) {
    $insert = "`r`n            return refreshed;`r`n        "
    $text = $text.Substring(0,$close) + $insert + $text.Substring($close)
    [System.IO.File]::WriteAllText($path,$text,$utf8)
}

$check = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$checkStart = $check.IndexOf($marker,[StringComparison]::Ordinal)
$checkOpen = $check.IndexOf('{',$checkStart)
$checkClose = Find-MethodClose $check $checkOpen
if ($checkClose -lt 0) {
    throw 'August 23 Site Grid RefreshAll post-check could not locate the method end.'
}
$checkBody = $check.Substring($checkOpen + 1,$checkClose - $checkOpen - 1).TrimEnd()
if (-not [regex]::IsMatch($checkBody,'(?s)return\s+refreshed\s*;\s*$')) {
    throw 'August 23 Site Grid RefreshAll compatibility failed: the int overload still has no terminal return refreshed path.'
}
if ([regex]::IsMatch($checkBody,'(?m)^\s*return\s*;\s*$')) {
    throw 'August 23 Site Grid RefreshAll compatibility failed: a bare return survived inside the int overload.'
}

Write-Host 'Site Grid RefreshAll return compatibility normalized before compilation.' -ForegroundColor Green
