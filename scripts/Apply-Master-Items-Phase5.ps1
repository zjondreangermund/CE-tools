[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Phase 5 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 5 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$testsFile = "tests\CE.Tools.Core.Tests\Program.cs"
Replace-ExactText `
    -RelativePath $testsFile `
    -OldText @'
                CameraPathHasHeadingAndPitch();
'@ `
    -NewText @'
                CameraPathHasHeadingAndPitch();
                ParkingOptimiserBuildsAlternatives();
                ParkingOptimiserRejectsObstacleBays();
                ParkingOptimiserAllocatesAccessibleBays();
                ParkingOptimiserInsertsIslands();
'@ `
    -Description "run parking optimiser core tests"

Replace-ExactText `
    -RelativePath $testsFile `
    -OldText @'
        private static HydrologyGridAnalysis CreateSingleOutletAnalysis()
'@ `
    -NewText @'
        private static void ParkingOptimiserBuildsAlternatives()
        {
            ParkingPolygon boundary = RectangleParkingPolygon(0.0, 0.0, 60.0, 40.0);
            ParkingLayoutSettings settings = DefaultParkingSettings(80, 2, 10, 2.0);
            IReadOnlyList<ParkingLayoutOption> options = ParkingLayoutOptimizer.Optimise(
                boundary,
                new ParkingPolygon[0],
                new ParkingPoint(3.0, 20.0),
                settings);

            Equal(6, options.Count);
            True(options[0].TotalBayCount > 0);
            True(options[0].Score >= options[options.Count - 1].Score);
            True(options.Any(item => Math.Abs(item.ParkingAngleDegrees - 90.0) < 0.001));
            True(options.Any(item => Math.Abs(item.ParkingAngleDegrees - 60.0) < 0.001));
            True(options.Any(item => Math.Abs(item.ParkingAngleDegrees - 45.0) < 0.001));
            Pass();
        }

        private static void ParkingOptimiserRejectsObstacleBays()
        {
            ParkingPolygon boundary = RectangleParkingPolygon(0.0, 0.0, 60.0, 40.0);
            ParkingLayoutSettings settings = DefaultParkingSettings(80, 0, 0, 0.0);
            ParkingLayoutOption clear = ParkingLayoutOptimizer.Optimise(
                boundary,
                new ParkingPolygon[0],
                new ParkingPoint(3.0, 20.0),
                settings)[0];
            ParkingLayoutOption obstructed = ParkingLayoutOptimizer.Optimise(
                boundary,
                new[] { RectangleParkingPolygon(20.0, 0.0, 40.0, 40.0) },
                new ParkingPoint(3.0, 20.0),
                settings)[0];

            True(obstructed.TotalBayCount < clear.TotalBayCount);
            True(obstructed.Rejections.ObstacleConflict > 0 ||
                 obstructed.Rejections.AisleBoundaryOrObstacle > 0);
            Pass();
        }

        private static void ParkingOptimiserAllocatesAccessibleBays()
        {
            ParkingLayoutOption best = ParkingLayoutOptimizer.Optimise(
                RectangleParkingPolygon(0.0, 0.0, 80.0, 50.0),
                new ParkingPolygon[0],
                new ParkingPoint(4.0, 25.0),
                DefaultParkingSettings(100, 3, 0, 0.0))[0];

            Equal(3, best.AccessibleBayCount);
            Equal(0, best.MissingAccessibleBays);
            True(best.Bays.Count(item => item.Type == ParkingElementType.AccessibleBay) == 3);
            True(best.Aisles.Count(item => item.Type == ParkingElementType.AccessAisle) == 3);
            Pass();
        }

        private static void ParkingOptimiserInsertsIslands()
        {
            ParkingLayoutOption best = ParkingLayoutOptimizer.Optimise(
                RectangleParkingPolygon(0.0, 0.0, 80.0, 50.0),
                new ParkingPolygon[0],
                new ParkingPoint(4.0, 25.0),
                DefaultParkingSettings(100, 0, 5, 2.0))[0];

            True(best.Islands.Count > 0);
            True(best.Islands.All(item => item.Type == ParkingElementType.LandscapeIsland));
            True(best.HasEntranceConnection);
            Pass();
        }

        private static ParkingLayoutSettings DefaultParkingSettings(
            int target,
            int accessible,
            int islandInterval,
            double islandWidth)
        {
            return new ParkingLayoutSettings(
                target,
                accessible,
                2.5,
                3.6,
                1.5,
                5.0,
                6.0,
                6.0,
                islandInterval,
                islandWidth,
                new[] { 90.0, 60.0, 45.0 },
                new[] { 0.0, 90.0 });
        }

        private static ParkingPolygon RectangleParkingPolygon(
            double minX,
            double minY,
            double maxX,
            double maxY)
        {
            return new ParkingPolygon(new[]
            {
                new ParkingPoint(minX, minY),
                new ParkingPoint(maxX, minY),
                new ParkingPoint(maxX, maxY),
                new ParkingPoint(minX, maxY)
            });
        }

        private static HydrologyGridAnalysis CreateSingleOutletAnalysis()
'@ `
    -Description "add deterministic parking optimiser tests"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldParkingTail = @'
                    Cmd("Parking Standards Check", "CE_PKSTANDARDS ", "Validate bay dimensions and report non-compliant bays."),
                    Cmd("Clear Parking Review Graphics", "CE_PKCLEARREVIEW ", "Erase only CE-generated parking review graphics.")),
'@
$newParkingTail = @'
                    Cmd("Parking Standards Check", "CE_PKSTANDARDS ", "Validate bay dimensions and report non-compliant bays."),
                    Cmd("Clear Parking Review Graphics", "CE_PKCLEARREVIEW ", "Erase only CE-generated parking review graphics."),
                    Cmd("Full Parking Optimiser Tools", "CE_PARKOPTIMIZERTOOLS ", "Open create, refresh, information, export and safe-clear workflows for obstacle-aware scored parking alternatives."),
                    Cmd("Optimise Parking with Obstacles", "CE_PARKOPTIMIZE ", "Score 90, 60 and 45 degree alternatives with obstacles, traffic aisles, accessible bays, islands and an entrance connection."),
                    Cmd("Refresh Optimised Parking", "CE_PARKOPTREFRESH ", "Regenerate the selected linked parking option from the current boundary, obstacles and stored criteria."),
                    Cmd("Optimised Parking Information", "CE_PARKOPTINFO ", "Review stored sources, criteria, option score, bay counts, shortfalls and rejection reasons."),
                    Cmd("Export Optimised Parking", "CE_PARKOPTEXPORT ", "Export the selected linked parking option and element register to Excel."),
                    Cmd("Clear Optimised Parking", "CE_PARKOPTCLEAR ", "Erase only tagged optimiser-generated bays, aisles, islands and labels for the linked boundary.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldParkingTail `
    -NewText $newParkingTail `
    -Description "add full obstacle-aware parking optimiser commands"

Write-Host "Master Items Phase 5 parking optimiser source is wired." -ForegroundColor Green
