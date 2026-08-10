[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\CogoPointProjectStyleCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "COGO source missing: $path" }
$text = [System.IO.File]::ReadAllText($path)

if (-not $text.Contains('if (!occupied.Any(existing => existing.Intersects(currentBox)))')) {
    $pattern = '(?s)        private static Point3d FindClearLocation\(\s*CogoLabelItem item,\s*IList<Box2d> occupied,\s*double gap,\s*double textHeight\)\s*\{.*?\n        \}\n\n        private static Box2d LabelBox'
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) { throw "Could not isolate the COGO FindClearLocation method. Matches=$($matches.Count)" }
    $replacement = @'
        private static Point3d FindClearLocation(
            CogoLabelItem item,
            IList<Box2d> occupied,
            double gap,
            double textHeight)
        {
            // A label that is already clear must never move. This is the first
            // test, before considering any candidate closer to the source point.
            Box2d currentBox = LabelBox(
                item.LabelLocation,
                item.Width,
                item.Height,
                gap);
            if (!occupied.Any(existing => existing.Intersects(currentBox)))
                return item.LabelLocation;

            // Keep overlap correction close to the survey point. With the shared
            // 2.5 mm label height this searches about 2, 4 and 6 paper-mm rings.
            double maximumRadius = Math.Max(textHeight * 2.4, gap * 2.0);
            double step = maximumRadius / 3.0;
            Point3d best = item.LabelLocation;
            double bestDistance = double.MaxValue;
            for (int ring = 1; ring <= 3; ring++)
            {
                double radius = step * ring;
                for (int sector = 0; sector < 16; sector++)
                {
                    double angle = Math.PI * 2.0 * sector / 16.0;
                    Point3d candidate = new Point3d(
                        item.Anchor.X + Math.Cos(angle) * radius,
                        item.Anchor.Y + Math.Sin(angle) * radius,
                        item.Anchor.Z);
                    Box2d box = LabelBox(candidate, item.Width, item.Height, gap);
                    if (occupied.Any(existing => existing.Intersects(box))) continue;
                    double distance = candidate.DistanceTo(item.Anchor) +
                        candidate.DistanceTo(item.LabelLocation) * 0.05;
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
            }

            // If there is no clear close position, keep the original label rather
            // than jumping to an arbitrary far point.
            return bestDistance == double.MaxValue ? item.LabelLocation : best;
        }

        private static Box2d LabelBox
'@
    $text = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
    Write-Host 'Repaired COGO overlap search: clear labels stay fixed and movement is bounded.' -ForegroundColor Green
}
else {
    Write-Host 'COGO overlap search is already repaired.' -ForegroundColor DarkGreen
}

$oldMaximum = @'
            double maximum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 6.0),
                fallback * 2.0);
'@
$newMaximum = @'
            double maximum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 6.0),
                fallback);
'@
if ($text.Contains($oldMaximum)) {
    $text = $text.Replace($oldMaximum, $newMaximum)
    Write-Host 'Reduced stored COGO label-offset clamp to the close-label range.' -ForegroundColor Green
}

if ($text.Contains('return bestDistance == double.MaxValue ? candidates.Last() : best;')) {
    throw 'COGO overlap repair failed: farthest-candidate fallback is still present.'
}
if (-not $text.Contains('return bestDistance == double.MaxValue ? item.LabelLocation : best;')) {
    throw 'COGO overlap repair verification failed: safe fallback is missing.'
}
if (-not $text.Contains('if (!occupied.Any(existing => existing.Intersects(currentBox)))')) {
    throw 'COGO overlap repair verification failed: non-overlapping-label guard is missing.'
}

[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
Write-Host 'COGO overlap compatibility repair passed.' -ForegroundColor Cyan
