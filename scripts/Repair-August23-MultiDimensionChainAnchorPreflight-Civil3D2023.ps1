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
$methodStart = $text.IndexOf($methodMarker,[StringComparison]::Ordinal)
if ($methodStart -lt 0) {
    throw 'Multi Dimensions public command marker missing for chain-anchor preflight.'
}
$methodEnd = $text.IndexOf($methodEndMarker,$methodStart,[StringComparison]::Ordinal)
if ($methodEnd -lt 0) {
    throw 'Multi Dimensions ProcessPolyline marker missing for chain-anchor preflight.'
}

$leaderBlock = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));
'@ -replace "`r?`n","`r`n"
$leaderBlock = $leaderBlock.TrimEnd("`r","`n")
$sourcesMarker = '            int sources = 0;'
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
$dispatchMarker = '            if (string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase))'

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
$between = $text.Substring($afterLeader,$sourcesAbsolute-$afterLeader)
$hasDispatch = $methodText.Contains($dispatchMarker)

# Preserve every late declaration/comment inserted between ArcLeader and sources,
# but remove the chain dispatch itself from that movable block when it already exists.
$preserved = $between
if ($hasDispatch) {
    $dispatchIndexInBetween = $preserved.IndexOf($dispatchBlock,[StringComparison]::Ordinal)
    if ($dispatchIndexInBetween -ge 0) {
        $preserved = $preserved.Remove($dispatchIndexInBetween,$dispatchBlock.Length)
    }
    elseif ($between.Contains($dispatchMarker)) {
        throw 'Multi Dimensions chain dispatch exists in an unexpected non-canonical shape.'
    }
    else {
        # A dispatch outside the ArcLeader/sources window is unsafe to silently duplicate.
        throw 'Multi Dimensions chain dispatch exists outside the ArcLeader/sources window.'
    }
}
$preserved = $preserved.Trim("`r","`n")

# Rebuild only the small ArcLeader -> sources window. If dispatch is not present yet,
# restore the exact historical OLD anchor so the August 21 finalizer can add it. If
# dispatch already exists, restore the exact historical NEW anchor so the finalizer
# recognises the completed state. All unrelated declarations are moved immediately
# after `int sources = 0;` and remain in the same method/scope.
$canonicalMiddle = if ($hasDispatch) {
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
if ($hasDispatch) {
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
