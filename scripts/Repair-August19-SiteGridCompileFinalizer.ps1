[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sitePath = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
if (-not (Test-Path -LiteralPath $sitePath -PathType Leaf)) {
    throw "August 19 Site Grid compile finalizer source missing: $sitePath"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$site = [System.IO.File]::ReadAllText($sitePath) -replace "`r?`n", "`r`n"

$rebuildStart = $site.IndexOf('        private static int RebuildOne(',[StringComparison]::Ordinal)
if ($rebuildStart -lt 0) {
    throw 'August 19 Site Grid compile finalizer could not locate RebuildOne.'
}
$modelHeightStart = $site.IndexOf(
    '            double modelTextHeight = ModelTextHeight(',
    $rebuildStart,
    [StringComparison]::Ordinal)
if ($modelHeightStart -lt 0) {
    throw 'August 19 Site Grid compile finalizer could not locate modelTextHeight.'
}
$modelHeightEnd = $site.IndexOf(';',$modelHeightStart,[StringComparison]::Ordinal)
if ($modelHeightEnd -lt 0) {
    throw 'August 19 Site Grid compile finalizer could not locate modelTextHeight terminator.'
}

# Replace every historical August 19 floor/ceiling/offset variant in one pass.
# The first X-label loop is the stable structural boundary after this settings block.
# Preserve the August 14 corner-clearance behavior because the first/last labels
# on all four frame edges use it to stay clear of the rectangle corners.
$nextLoop = $site.IndexOf(
    '            for (int xIndex = 0; xIndex < xValues.Count; xIndex++)',
    $modelHeightEnd,
    [StringComparison]::Ordinal)
if ($nextLoop -lt 0) {
    throw 'August 19 Site Grid compile finalizer could not locate the first coordinate-label loop.'
}

$canonical = @'

            double siteGridMinimumSpacing = Math.Max(
                0.001,
                Math.Min(settings.SpacingX, settings.SpacingY));
            double siteGridFrameSpan = Math.Max(
                0.001,
                Math.Min(
                    Math.Abs(bounds.MaxX - bounds.MinX),
                    Math.Abs(bounds.MaxY - bounds.MinY)));
            double siteGridTextFloor = Math.Max(
                Math.Min(siteGridMinimumSpacing * 0.04, siteGridFrameSpan * 0.01),
                0.01);
            double siteGridTextCeiling = Math.Max(
                siteGridTextFloor,
                Math.Min(siteGridMinimumSpacing * 0.16, siteGridFrameSpan * 0.025));
            modelTextHeight = Math.Max(
                siteGridTextFloor,
                Math.Min(modelTextHeight, siteGridTextCeiling));
            double insideOffsetLimit = Math.Max(
                0.01,
                Math.Min(siteGridMinimumSpacing * 0.35, siteGridFrameSpan * 0.08));
            double insideOffset = Math.Min(
                Math.Max(modelTextHeight * 1.35, 0.01),
                insideOffsetLimit);
            double cornerClearance = Math.Max(modelTextHeight * 4.5, 0.001);

'@ -replace "`n","`r`n"

$site = $site.Substring(0,$modelHeightEnd + 1) + $canonical + $site.Substring($nextLoop)
[System.IO.File]::WriteAllText($sitePath,$site,$utf8)

$check = [System.IO.File]::ReadAllText($sitePath)
$singleDeclarations = @(
    'siteGridMinimumSpacing',
    'siteGridFrameSpan',
    'siteGridTextFloor',
    'siteGridTextCeiling',
    'insideOffsetLimit',
    'insideOffset',
    'cornerClearance'
)
foreach ($name in $singleDeclarations) {
    $count = [regex]::Matches($check,'\bdouble\s+' + [regex]::Escape($name) + '\b').Count
    if ($count -ne 1) {
        throw "August 19 Site Grid compile finalizer failed: '$name' declarations=$count; expected exactly 1."
    }
}

foreach ($marker in @(
    'Math.Min(siteGridMinimumSpacing * 0.04, siteGridFrameSpan * 0.01)',
    'Math.Min(siteGridMinimumSpacing * 0.16, siteGridFrameSpan * 0.025)',
    'Math.Min(siteGridMinimumSpacing * 0.35, siteGridFrameSpan * 0.08)',
    'double cornerClearance = Math.Max(modelTextHeight * 4.5, 0.001);')) {
    if (-not $check.Contains($marker)) {
        throw "August 19 Site Grid compile finalizer failed: canonical marker missing: $marker"
    }
}

if ($check.Contains('siteGridMinimumSpacing * 0.40') -or
    $check.Contains('Math.Min(settings.SpacingX, settings.SpacingY) * 0.08')) {
    throw 'August 19 Site Grid compile finalizer failed: an obsolete Site Grid text-floor formula still survives.'
}

Write-Host 'August 19 Site Grid compile block normalized: spacing/frame/text/offset/corner-clearance locals are each declared exactly once.' -ForegroundColor Green
