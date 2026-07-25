[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$path = Join-Path $repositoryRoot "src\CE.Tools.Core\ParkingLayoutOptimizer.cs"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$old = @'
                List<ParkingElement> overlaps = bays
                    .Where(item => item != candidate && item.Polygon.IntersectsOrContains(envelope))
                    .ToList();
                foreach (ParkingElement overlap in overlaps) bays.Remove(overlap);
'@
$new = @'
                bool conflictsWithReservedAccessible = bays.Any(item =>
                        item.Type == ParkingElementType.AccessibleBay &&
                        item.Polygon.IntersectsOrContains(envelope)) ||
                    aisles.Any(item =>
                        item.Type == ParkingElementType.AccessAisle &&
                        item.Polygon.IntersectsOrContains(envelope));
                if (conflictsWithReservedAccessible)
                {
                    rejected.AccessibleEnvelope++;
                    continue;
                }

                List<ParkingElement> overlaps = bays
                    .Where(item =>
                        item != candidate &&
                        item.Type == ParkingElementType.StandardBay &&
                        item.Polygon.IntersectsOrContains(envelope))
                    .ToList();
                foreach (ParkingElement overlap in overlaps) bays.Remove(overlap);
'@
if ($text.Contains($new) -and -not $text.Contains($old)) {
    Write-Host "Accessible parking envelope protection is already applied." -ForegroundColor DarkGray
    return
}
if (-not $text.Contains($old)) {
    throw "Could not apply accessible parking envelope protection."
}
[System.IO.File]::WriteAllText($path, $text.Replace($old, $new), $utf8NoBom)
Write-Host "  protect previously allocated accessible bays and aisles" -ForegroundColor Green
