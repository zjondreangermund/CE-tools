#!/usr/bin/env python3
"""Validate Master Items Phase 2 dynamic parking and grading source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "ParkingDynamicGradingCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_PARKAUTOMONITOR"',
    '"CE_PARKAUTOREFRESHALL"',
    '"CE_PARKAUTOSTATUS"',
    '"CE_PARKGRADETOOLS"',
    '"CE_PARKGRADECREATE"',
    '"CE_PARKGRADEREFRESH"',
    '"CE_PARKGRADEINFO"',
    '"CE_PARKGRADECLEAR"',
    'internal static class ParkingOptionAutoRefreshManager',
    'AcApplication.Idle += OnIdle;',
    '_database.ObjectModified += OnObjectModified;',
    '_database.ObjectErased += OnObjectErased;',
    'ParkingBoundaries.Contains(handle)',
    'GradingBoundaries.Contains(handle)',
    'typeof(AdvancedParkingPlanningCommands)',
    '"ReadBoundary"',
    '"TryReadLinkedSettings"',
    '"BuildOption"',
    '"ReplaceLinkedOption"',
    'internal static class ParkingGradeGuideStore',
    'private const string RegAppName = "CE_PARK_GRADE_GUIDE"',
    'ParkingGradeMode.LowPoint',
    'ParkingGradeMode.Crown',
    'ParkingGradeMode.Valley',
    'settings.ReferenceElevation +',
    'halfWidth * settings.SlopeRatio',
    'new Polyline3d(',
    '"Boundary=" + boundaryHandle',
    'PointInPolygon(',
    'VerticalIntersections(',
    'Design assistance — verify grading, drainage paths, tie-ins and earthworks',
)
require(
    NORMALIZER,
    'Cmd("Dynamic Parking Monitor", "CE_PARKAUTOMONITOR "',
    'Cmd("Refresh All Linked Parking", "CE_PARKAUTOREFRESHALL "',
    'Cmd("Dynamic Parking Status", "CE_PARKAUTOSTATUS "',
    'Cmd("Parking Grading Guide Tools", "CE_PARKGRADETOOLS "',
    'Cmd("Create Parking Grading Guides", "CE_PARKGRADECREATE "',
    'Cmd("Refresh Parking Grading Guides", "CE_PARKGRADEREFRESH "',
    'Cmd("Parking Grading Guide Information", "CE_PARKGRADEINFO "',
    'Cmd("Clear Parking Grading Guides", "CE_PARKGRADECLEAR "',
    'ParkingOptionAutoRefreshManager.Initialize();',
    'ParkingOptionAutoRefreshManager.Terminate();',
)
# The Phase 2 normalizer executes before this validator in its dedicated CI job.
require(
    RIBBON,
    'CE_PARKAUTOMONITOR ',
    'CE_PARKAUTOREFRESHALL ',
    'CE_PARKAUTOSTATUS ',
    'CE_PARKGRADETOOLS ',
    'CE_PARKGRADECREATE ',
    'CE_PARKGRADEREFRESH ',
    'CE_PARKGRADEINFO ',
    'CE_PARKGRADECLEAR ',
    'ParkingOptionAutoRefreshManager.Initialize();',
    'ParkingOptionAutoRefreshManager.Terminate();',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in ParkingDynamicGradingCommands.cs")

print("Master Items Phase 2 dynamic parking and grading validation passed.")
