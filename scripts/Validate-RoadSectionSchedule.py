#!/usr/bin/env python3
"""Validate linked road cross-section setting-out schedules."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "RoadCrossSectionScheduleCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase1-RoadSections.ps1"
WRAPPER = ROOT / "scripts" / "Invoke-Master-Items-Phase1.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_ROADSECTIONDATATOOLS"',
    '"CE_ROADSECTIONDATA"',
    '"CE_ROADSECTIONDATAREFRESH"',
    '"CE_ROADSECTIONDATAEXPORT"',
    '"CE_ROADSECTIONDATAINFO"',
    'private const string LinkRecordName = "CE_ROAD_SECTION_SCHEDULE"',
    'ItemsSource = new[] { 5.0, 10.0, 20.0 }',
    '"LEFT EDGE"',
    '"ROAD CENTERLINE"',
    '"RIGHT EDGE"',
    'alignment.PointLocation(station, offset, ref x, ref y)',
    'surface.FindElevationAtXY(x, y)',
    '"CHAINAGE"',
    '"X COORDINATE"',
    '"Y COORDINATE"',
    '"GROUND ELEVATION"',
    '"DESIGN ELEVATION"',
    '"DIFFERENCE"',
    'SimpleXlsxWriter.Write(path, "Road Section Data", cells)',
    'internal static int RefreshAll(Document document)',
)
require(
    NORMALIZER,
    'Cmd("Road Cross-Section Data Tools", "CE_ROADSECTIONDATATOOLS "',
    'Cmd("Create Road Cross-Section Data", "CE_ROADSECTIONDATA "',
    'Cmd("Refresh Road Cross-Section Data", "CE_ROADSECTIONDATAREFRESH "',
    'Cmd("Export Road Cross-Section Data", "CE_ROADSECTIONDATAEXPORT "',
    'Cmd("Road Cross-Section Data Information", "CE_ROADSECTIONDATAINFO "',
    'summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);',
)
require(
    WRAPPER,
    'Apply-Master-Items-Phase1-RoadSections.ps1',
)
# Normalizers execute before validation in CI.
require(
    RIBBON,
    'CE_ROADSECTIONDATATOOLS ',
    'CE_ROADSECTIONDATA ',
    'CE_ROADSECTIONDATAREFRESH ',
    'CE_ROADSECTIONDATAEXPORT ',
    'CE_ROADSECTIONDATAINFO ',
)
require(
    PRESENTATION,
    'summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in RoadCrossSectionScheduleCommands.cs")

print("Road cross-section setting-out schedule validation passed.")
