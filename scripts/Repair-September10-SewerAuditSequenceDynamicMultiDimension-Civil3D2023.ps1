[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$multiPath = Join-Path $root 'src\CE.Tools.Civil3D\MultiDimensionCommands.cs'
$corePath = Join-Path $root 'scripts\Repair-September10-SewerAuditSequenceDynamicMultiDimension-Core-Civil3D2023.ps1'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $multiPath -PathType Leaf)) {
    throw "September 10 Multiple Dimensions source missing: $multiPath"
}
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    throw "September 10 core finalizer missing: $corePath"
}

# September 09 can legitimately rewrite the FeatureLine selection guard while
# preserving the same source/process block. Normalize only that small block back to
# the canonical shape expected by the September 10 core finalizer. This keeps the
# repair resilient without weakening any of the strict final behavior checks.
$text = [System.IO.File]::ReadAllText($multiPath) -replace "`r?`n", "`r`n"
$canonical = @'
                        CivilFeatureLine featureLine = entity as CivilFeatureLine;
                        if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
                        {
                            sources++;
'@ -replace "`r?`n", "`r`n"

if (-not $text.Contains($canonical)) {
    $marker = '                        CivilFeatureLine featureLine = entity as CivilFeatureLine;'
    $markerAt = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($markerAt -lt 0) { throw 'September 10 FeatureLine source marker missing before canonicalization.' }

    $processMarker = '                            ProcessFeatureLine('
    $processAt = $text.IndexOf($processMarker,$markerAt,[StringComparison]::Ordinal)
    if ($processAt -lt 0) { throw 'September 10 FeatureLine ProcessFeatureLine marker missing before canonicalization.' }

    $sourcesAt = $text.LastIndexOf('sources++;',$processAt,[StringComparison]::Ordinal)
    if ($sourcesAt -lt $markerAt) { throw 'September 10 FeatureLine sources++ marker missing before canonicalization.' }
    $sourcesEnd = $sourcesAt + 'sources++;'.Length

    $text = $text.Substring(0,$markerAt) + $canonical + $text.Substring($sourcesEnd)
    [System.IO.File]::WriteAllText($multiPath,$text,$utf8)
    Write-Host 'September 10 FeatureLine Multiple Dimensions staged anchor normalized.' -ForegroundColor Green
}

. $corePath -RepoRoot $root
