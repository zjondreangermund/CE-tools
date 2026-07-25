#!/usr/bin/env python3
"""Validate Phase 5 obstacle-aware parking optimiser source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "ParkingLayoutOptimizer.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "ParkingOptimiserCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.Core.Tests" / "Program.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase5.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    CORE,
    'public static class ParkingLayoutOptimizer',
    'public static IReadOnlyList<ParkingLayoutOption> Optimise(',
    'private const int MaximumCandidateSlots = 250000',
    'FindPrimaryOrientation(boundary)',
    'settings.OrientationOffsetsDegrees',
    'settings.ParkingAnglesDegrees',
    'BuildOption(',
    'AddRow(',
    'boundary.ContainsPolygon(bay)',
    'obstacles.Any(obstacle => obstacle.IntersectsOrContains(bay))',
    'ApplyIslands(bays, islands, settings)',
    'ApplyAccessibleBays(',
    'settings.AccessibleBayWidthMetres +',
    'settings.AccessAisleWidthMetres',
    'ParkingElementType.AccessibleBay',
    'ParkingElementType.AccessAisle',
    'ParkingElementType.TrafficAisle',
    'ParkingElementType.LandscapeIsland',
    'ParkingElementType.EntranceConnection',
    'BuildEntranceConnection(',
    'targetShortfall * 1500.0',
    'accessibleShortfall * 5000.0',
    'Concept screening only; verify all governing standards and swept paths.',
    'The parking entrance point must lie inside or on the boundary.',
    'Parking optimisation exceeded the 250,000-slot safety limit.',
    'public bool IntersectsOrContains(ParkingPolygon other)',
    'public bool ContainsPolygon(ParkingPolygon other)',
    'is self-intersecting.',
)

require(
    CIVIL,
    '"CE_PARKOPTIMIZERTOOLS"',
    '"CE_PARKOPTIMIZE"',
    '"CE_PARKOPTREFRESH"',
    '"CE_PARKOPTINFO"',
    '"CE_PARKOPTEXPORT"',
    '"CE_PARKOPTCLEAR"',
    'private const string RegAppName = "CE_PARK_OPTIMISER"',
    'private const int MaximumObstacles = 500',
    'ParkingLayoutOptimizer.Optimise(',
    'new[] { 90.0, 60.0, 45.0 }',
    'new[] { 0.0, 90.0 }',
    'Select closed obstacle/island/building polylines',
    'Pick the preferred parking entrance/access point',
    'ShowOptions(document, options',
    'PromptOptionIndex(',
    'ReplaceLinkedLayout(',
    'EraseLinked(space, transaction, input.BoundaryHandle)',
    'ParkingElementType.AccessibleBay',
    'ParkingElementType.AccessAisle',
    'ParkingElementType.LandscapeIsland',
    'Text("Obstacle=" + handle)',
    'GetXDataForApplication(RegAppName)',
    'TryResolveHandle(database, link.BoundaryHandle',
    'SimpleXlsxWriter.Write(path, "Parking Optimiser", rows)',
    'Source boundary and obstacles were unchanged.',
    'Concept screening only; verify accessibility, circulation, swept paths, fire access, gradients, drainage, markings and governing standards.',
    'OpenMode.ForRead',
)

require(
    TESTS,
    'ParkingOptimiserBuildsAlternatives();',
    'ParkingOptimiserRejectsObstacleBays();',
    'ParkingOptimiserAllocatesAccessibleBays();',
    'ParkingOptimiserInsertsIslands();',
    'Equal(6, options.Count);',
    'True(obstructed.TotalBayCount < clear.TotalBayCount);',
    'Equal(3, best.AccessibleBayCount);',
    'True(best.Islands.Count > 0);',
    'True(best.HasEntranceConnection);',
)

require(
    NORMALIZER,
    'run parking optimiser core tests',
    'add deterministic parking optimiser tests',
    'Cmd("Full Parking Optimiser Tools", "CE_PARKOPTIMIZERTOOLS "',
    'Cmd("Optimise Parking with Obstacles", "CE_PARKOPTIMIZE "',
    'Cmd("Refresh Optimised Parking", "CE_PARKOPTREFRESH "',
    'Cmd("Optimised Parking Information", "CE_PARKOPTINFO "',
    'Cmd("Export Optimised Parking", "CE_PARKOPTEXPORT "',
    'Cmd("Clear Optimised Parking", "CE_PARKOPTCLEAR "',
)

require(
    RIBBON,
    'CE_PARKOPTIMIZERTOOLS ',
    'CE_PARKOPTIMIZE ',
    'CE_PARKOPTREFRESH ',
    'CE_PARKOPTINFO ',
    'CE_PARKOPTEXPORT ',
    'CE_PARKOPTCLEAR ',
)

for path in (CORE, CIVIL):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

civil_text = CIVIL.read_text(encoding="utf-8")
if "Microsoft.Office.Interop" in civil_text:
    raise SystemExit("Parking optimiser must not introduce Office COM automation")
if "boundary.UpgradeOpen" in civil_text:
    raise SystemExit("Parking optimiser must keep source boundary read-only")
if "ObstacleHandles" not in civil_text or 'Text("Obstacle=" + handle)' not in civil_text:
    raise SystemExit("Parking optimiser must persist obstacle handles")
if "entity.Erase();" not in civil_text or "ReadLink(entity)" not in civil_text:
    raise SystemExit("Parking optimiser clear workflow must erase only linked generated elements")

print("Master Items Phase 5 full parking optimiser validation passed.")
