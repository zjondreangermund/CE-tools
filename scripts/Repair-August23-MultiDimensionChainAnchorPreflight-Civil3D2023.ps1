[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\MultiDimensionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Multi Dimensions source missing for chain-anchor preflight: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

$methodMarker = '        public void MultiDimension()'
$methodEndMarker = '        private static void ProcessPolyline('
$leaderBlock = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));
'@ -replace "`r?`n","`r`n"
$leaderBlock = $leaderBlock.TrimEnd("`r","`n")
$sourcesMarker = '            int sources = 0;'
$dispatchMarker = '            if (string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase))'
$dispatchBlock = @'
            if (string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase))
            {
                DimensionOpenPolylineChain(
                    document,
                    selection,
                    requestedStyle);
                return;
            }
'@ -replace "`r?`n","`r`n"
$dispatchBlock = $dispatchBlock.TrimEnd("`r","`n")

function Resolve-MethodWindow([string]$source) {
    $start = $source.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($start -lt 0) {
        throw 'Multi Dimensions public command marker missing for chain-anchor preflight.'
    }
    $end = $source.IndexOf($methodEndMarker,$start,[StringComparison]::Ordinal)
    if ($end -lt 0) {
        throw 'Multi Dimensions ProcessPolyline marker missing for chain-anchor preflight.'
    }
    return @($start,$end)
}

function Remove-ExistingChainDispatch([string]$source,[int]$methodStart,[int]$methodEnd) {
    $methodText = $source.Substring($methodStart,$methodEnd-$methodStart)
    $relative = $methodText.IndexOf($dispatchMarker,[StringComparison]::Ordinal)
    if ($relative -lt 0) {
        return @($source,$false)
    }
    if ($methodText.IndexOf($dispatchMarker,$relative+$dispatchMarker.Length,[StringComparison]::Ordinal) -ge 0) {
        throw 'Multi Dimensions chain dispatch marker is duplicated inside the public command.'
    }

    $absolute = $methodStart + $relative
    $open = $source.IndexOf('{',$absolute)
    if ($open -lt 0 -or $open -ge $methodEnd) {
        throw 'Multi Dimensions chain dispatch opening brace could not be resolved.'
    }

    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $methodEnd; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) {
        throw 'Multi Dimensions chain dispatch closing brace could not be resolved.'
    }

    # Remove only the complete if block. Keep all surrounding declarations/comments;
    # whitespace is normalized later when rebuilding the ArcLeader/sources window.
    $updated = $source.Remove($absolute,($close-$absolute)+1)
    return @($updated,$true)
}

$window = Resolve-MethodWindow $text
$methodStart = [int]$window[0]
$methodEnd = [int]$window[1]
$removed = Remove-ExistingChainDispatch $text $methodStart $methodEnd
$text = [string]$removed[0]
$hadDispatch = [bool]$removed[1]

# Re-resolve all offsets after structural dispatch removal.
$window = Resolve-MethodWindow $text
$methodStart = [int]$window[0]
$methodEnd = [int]$window[1]
$methodText = $text.Substring($methodStart,$methodEnd-$methodStart)

$leaderRelative = $methodText.IndexOf($leaderBlock,[StringComparison]::Ordinal)
if ($leaderRelative -lt 0) {
    throw 'Multi Dimensions ArcLeader block missing for chain-anchor preflight.'
}
$sourcesRelative = $methodText.IndexOf(
    $sourcesMarker,
    $leaderRelative + $leaderBlock.Length,
    [StringComparison]::Ordinal)
if ($sourcesRelative -lt 0) {
    throw 'Multi Dimensions sources marker missing after ArcLeader for chain-anchor preflight.'
}
if ($methodText.IndexOf(
        $sourcesMarker,
        $sourcesRelative + $sourcesMarker.Length,
        [StringComparison]::Ordinal) -ge 0) {
    throw 'Multi Dimensions sources marker is ambiguous inside the public command.'
}

$leaderAbsolute = $methodStart + $leaderRelative
$afterLeader = $leaderAbsolute + $leaderBlock.Length
$sourcesAbsolute = $methodStart + $sourcesRelative
$preserved = $text.Substring($afterLeader,$sourcesAbsolute-$afterLeader).Trim("`r","`n")

# Rebuild one canonical ArcLeader -> dispatch -> sources window. If no dispatch existed,
# restore the old anchor so the preserved August 21 finalizer can add it. If a dispatch
# already existed in any formatting/location inside MultiDimension(), canonicalize it.
$canonicalMiddle = if ($hadDispatch) {
    "`r`n`r`n" + $dispatchBlock + "`r`n`r`n"
}
else {
    "`r`n`r`n"
}

$rebuilt = $text.Substring(0,$afterLeader) +
    $canonicalMiddle +
    $text.Substring($sourcesAbsolute)

$rebuiltSources = $rebuilt.IndexOf(
    $sourcesMarker,
    $afterLeader,
    [StringComparison]::Ordinal)
if ($rebuiltSources -lt 0) {
    throw 'Multi Dimensions sources marker was lost during chain-anchor preflight.'
}
if (-not [string]::IsNullOrWhiteSpace($preserved)) {
    $insertAfterSources = $rebuiltSources + $sourcesMarker.Length
    $rebuilt = $rebuilt.Insert(
        $insertAfterSources,
        "`r`n" + $preserved + "`r`n")
}

$oldAnchor = $leaderBlock + "`r`n`r`n" + $sourcesMarker + "`r`n"
$newAnchor = $leaderBlock + "`r`n`r`n" + $dispatchBlock + "`r`n`r`n" + $sourcesMarker + "`r`n"
if ($hadDispatch) {
    if (-not $rebuilt.Contains($newAnchor)) {
        throw 'Multi Dimensions completed chain anchor could not be normalized for the August 21 finalizer.'
    }
}
else {
    if (-not $rebuilt.Contains($oldAnchor)) {
        throw 'Multi Dimensions pre-dispatch chain anchor could not be normalized for the August 21 finalizer.'
    }
}

[System.IO.File]::WriteAllText($path,$rebuilt,$utf8)
Write-Host 'Multi Dimensions chain anchor preflight normalized before the August 21 finalizer.' -ForegroundColor Green
