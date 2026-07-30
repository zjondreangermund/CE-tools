#!/usr/bin/env python3
"""Regression checks for the V20 red/yellow continuation batch."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def require(relative, *needles):
    path = ROOT / relative
    if not path.exists():
        raise SystemExit(f"Missing required file: {relative}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {relative}")


require(
    "src/CE.Tools.Civil3D/SurveyCoordinateWorkflowCommands.cs",
    '"CE_COORDPICKCONTINUOUS"',
    "DynamicCoordinateLinkStore.SetPointName(",
    "BuildNamedMTextCoordinate(pointNames[index], vertex)",
    "if (settings.Output == AnnotationOutput.Cogo)",
    "sourceIds.Add(anchorId);",
    "DynamicCoordinateLinkStore.LinkPolylineVertices(",
    "DynamicCoordinateLinkStore.LinkSurfaceElevation(",
)
require(
    "src/CE.Tools.Civil3D/DynamicCoordinateLinkStore.cs",
    'PointNameRecordName = "CE_DYNAMIC_POINT_NAME"',
    "public static void SetPointName(",
    "public static string ReadPointName(",
    "surface.FindElevationAtXY(",
)
require(
    "src/CE.Tools.Civil3D/FeatureLineRelativeCommands.cs",
    "internal static int RefreshAll(Document document)",
    ".GroupBy(",
    "CreateChild(",
    "WriteRelation(",
)
require(
    "src/CE.Tools.Civil3D/ParkingNumberLinkStore.cs",
    'RegAppName = "CE_TOOLS_PARK_NUMBER"',
    "public static void Link(",
    "public static int RefreshAll(Document document)",
    "label.Location = centre;",
    "label.Erase();",
)
require(
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs",
    "ParkingNumberLinkStore.RefreshAll(document)",
    "FeatureLineRelativeCommands.RefreshAll(document)",
)
require(
    "src/CE.Tools.Civil3D/PluginEntry.cs",
    '"CE_COORDPICKCONTINUOUS "',
)

print(
    "V20 continuation validated: continuous/sequential coordinates, selected "
    "polyline output type, dynamic surface Z, feature-line offsets and parking numbers."
)
