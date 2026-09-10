[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$multiPath = Join-Path $src 'MultiDimensionCommands.cs'
$universalPath = Join-Path $src 'UniversalDynamicRefreshCommands.cs'
$annotationPath = Join-Path $src 'AnnotationScaleSyncCommands.cs'
$corePath = Join-Path $root 'scripts\Repair-September10-SewerAuditSequenceDynamicMultiDimension-Core-Civil3D2023.ps1'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($path in @($multiPath,$universalPath,$annotationPath,$corePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "September 10 wrapper input missing: $path"
    }
}

# September 09 can legitimately rewrite the FeatureLine selection guard while
# preserving the same source/process block. Normalize only that small block to the
# canonical shape expected by the strict September 10 core finalizer.
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

# September 09's performance pass intentionally wraps metadata housekeeping in an
# explicit-only guard. Keep that optimization, but insert dynamic MultiDimension
# refresh directly after segment-label refresh. A temporary compatibility comment
# lets the older strict core text anchor recognize the already-applied live hook;
# it is removed in finally and never remains in the staged source.
$universal = [System.IO.File]::ReadAllText($universalPath) -replace "`r?`n", "`r`n"
$segmentLine = '                try { result.SegmentLabelSources += DynamicSegmentLabelManager.RefreshAll(document); }'
$dynamicLine = '                try { DynamicMultiDimensionManager.RefreshAll(document); }'
if (-not $universal.Contains($dynamicLine)) {
    $segmentAt = $universal.IndexOf($segmentLine,[StringComparison]::Ordinal)
    if ($segmentAt -lt 0) { throw 'September 10 segment-label refresh hook missing before dynamic dimension insertion.' }
    $catchAt = $universal.IndexOf('                catch { result.Warnings++; }',$segmentAt,[StringComparison]::Ordinal)
    if ($catchAt -lt 0) { throw 'September 10 segment-label catch hook missing before dynamic dimension insertion.' }
    $catchEnd = $universal.IndexOf("`n",$catchAt,[StringComparison]::Ordinal)
    if ($catchEnd -lt 0) { $catchEnd = $universal.Length - 1 }
    else { $catchEnd++ }
    $insert = $dynamicLine + "`r`n" + '                catch { result.Warnings++; }' + "`r`n"
    $universal = $universal.Insert($catchEnd,$insert)
}

$compatibilityBlock = @'
/* CE_SEPT10_UNIVERSAL_COMPAT_ANCHOR
                try { result.SegmentLabelSources += DynamicSegmentLabelManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { DynamicMultiDimensionManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }
CE_SEPT10_UNIVERSAL_COMPAT_ANCHOR */
'@ -replace "`r?`n", "`r`n"
if (-not $universal.Contains('CE_SEPT10_UNIVERSAL_COMPAT_ANCHOR')) {
    $universal += "`r`n" + $compatibilityBlock
}
[System.IO.File]::WriteAllText($universalPath,$universal,$utf8)

try {
    . $corePath -RepoRoot $root

    # CANNOSCALE changes do not necessarily modify a model entity, so the universal
    # geometry debounce is not guaranteed to run. Hook dynamic dimensions directly
    # into the annotation-scale Idle pass while that pass already owns the document
    # lock and has undo recording disabled.
    $annotation = [System.IO.File]::ReadAllText($annotationPath) -replace "`r?`n", "`r`n"
    $onIdleMarker = '        private static void OnIdle(object sender, EventArgs eventArgs)'
    $onIdleAt = $annotation.IndexOf($onIdleMarker,[StringComparison]::Ordinal)
    if ($onIdleAt -lt 0) { throw 'September 10 annotation-scale OnIdle marker missing.' }
    $applyLine = '                    ApplyCurrentScale(document);'
    $applyAt = $annotation.IndexOf($applyLine,$onIdleAt,[StringComparison]::Ordinal)
    if ($applyAt -lt 0) { throw 'September 10 annotation-scale ApplyCurrentScale marker missing.' }
    $scaleHook = '                    try { DynamicMultiDimensionManager.RefreshAll(document); } catch { }'
    $hookAt = $annotation.IndexOf($scaleHook,$onIdleAt,[StringComparison]::Ordinal)
    if ($hookAt -lt 0) {
        $annotation = $annotation.Insert($applyAt + $applyLine.Length,"`r`n" + $scaleHook)
        [System.IO.File]::WriteAllText($annotationPath,$annotation,$utf8)
    }
    if (-not $annotation.Contains($scaleHook)) {
        throw 'September 10 dynamic dimensions are not wired to annotation-scale changes.'
    }
    Write-Host 'September 10 annotation-scale dynamic dimension refresh hook applied.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $universalPath -PathType Leaf) {
        $clean = [System.IO.File]::ReadAllText($universalPath) -replace "`r?`n", "`r`n"
        $clean = $clean.Replace("`r`n" + $compatibilityBlock,'').Replace($compatibilityBlock,'')
        [System.IO.File]::WriteAllText($universalPath,$clean,$utf8)
    }
}
