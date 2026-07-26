#!/usr/bin/env python3
"""Validate source-shape corrections discovered by the Civil 3D 2023 compiler."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def require(path: str, *needles: str) -> str:
    source = ROOT / path
    if not source.exists():
        raise SystemExit(f"Missing required file: {path}")
    text = source.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path}")
    return text


grid = require(
    "src/CE.Tools.Civil3D/GridReportPresenter.cs",
    "IList<IList<string>> rowsWithHeader",
    "rowsWithHeader[0]",
    "columns.Add(value ?? string.Empty);",
    "rows.Add(rowsWithHeader[index] ?? new List<string>());",
)

presentation_core = require(
    "src/CE.Tools.Core/SimplePresentationPackage.cs",
    ": this(title, subject, author, company, DateTime.UtcNow, slides)",
    ": this(title, subtitle, bullets, metrics)",
)

presentation = require(
    "src/CE.Tools.Civil3D/ProjectPresentationCommands.cs",
    "using Autodesk.AutoCAD.Geometry;",
    "snapshot.LayerCount = layers.Cast<ObjectId>().Count();",
    "snapshot.BlockDefinitionCount = blocks.Cast<ObjectId>().Count();",
)

assets = require(
    "src/CE.Tools.Civil3D/EngineeringAssetLibraryCommands.cs",
    "catch (System.Exception exception)",
)

model_audit = require(
    "src/CE.Tools.Civil3D/ModelDesignAuditCommands.cs",
    'ReadProperty(layout, "ConfigName")',
    'ReadProperty(layout, "PlotConfigurationName")',
)

flood = require(
    "src/CE.Tools.Civil3D/FloodResultReviewCommands.cs",
    "SetEntityVisibility(entity, show);",
    "private static void SetEntityVisibility(Entity entity, bool visible)",
    '"Visible"',
    '"Visibility"',
)

sewer = require(
    "src/CE.Tools.Civil3D/SewerExcavationCommentCommands.cs",
    "double side = 0.0;",
    "double width = 0.0;",
    "double bedding = 0.0;",
    "double cover = 0.0;",
)

road = require(
    "src/CE.Tools.Civil3D/RoadProductionCommentCommands.cs",
    "string alignmentStyleName;",
    "result.AlignmentStyleName = alignmentStyleName;",
    "string alignmentLabelSetName;",
    "result.AlignmentLabelSetName = alignmentLabelSetName;",
    "string profileStyleName;",
    "result.ProfileStyleName = profileStyleName;",
    "string profileLabelSetName;",
    "result.ProfileLabelSetName = profileLabelSetName;",
    "string profileViewStyleName;",
    "result.ProfileViewStyleName = profileViewStyleName;",
    "string bandSetStyleName;",
    "result.BandSetStyleName = bandSetStyleName;",
)

surface = require(
    "src/CE.Tools.Civil3D/SurfaceSpikeHoleRepairCommands.cs",
    "foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())",
    "existingNames.Add(ReadName(existingSurface));",
)

for path, text, forbidden in (
    ("EngineeringAssetLibraryCommands.cs", assets, "catch (Exception exception)"),
    ("ModelDesignAuditCommands.cs", model_audit, "layout.ConfigName"),
    (
        "FloodResultReviewCommands.cs",
        flood,
        "entity.Visibility = show ? Visibility.Visible : Visibility.Invisible",
    ),
    ("RoadProductionCommentCommands.cs", road, "out result.AlignmentStyleName"),
    (
        "SurfaceSpikeHoleRepairCommands.cs",
        surface,
        ".Select(id => transaction.GetObject(",
    ),
):
    if forbidden in text:
        raise SystemExit(
            f"Obsolete compiler-incompatible source remains in {path}: {forbidden}"
        )

if grid.count("public static void ShowReportAndOfferTable(") < 2:
    raise SystemExit("GridReportPresenter must retain both report call shapes")

print("Civil 3D 2023 compiler compatibility fixes validated.")
