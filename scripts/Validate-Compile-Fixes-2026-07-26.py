#!/usr/bin/env python3
"""Validate the exact source-shape corrections found by the Civil 3D 2023 compiler."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    source = ROOT / path
    if not source.exists():
        raise SystemExit(f"Missing required file: {path}")
    return source.read_text(encoding="utf-8")


def require(path: str, text: str, *needles: str) -> None:
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path}")


def forbid(path: str, text: str, *needles: str) -> None:
    for needle in needles:
        if needle in text:
            raise SystemExit(f"Compiler-incompatible source remains in {path}: {needle}")


grid_path = "src/CE.Tools.Civil3D/GridReportPresenter.cs"
grid = read(grid_path)
require(
    grid_path,
    grid,
    "IList<IList<string>> rowsWithHeader",
    "rowsWithHeader[0]",
    "columns.Add(value ?? string.Empty);",
)
if grid.count("public static void ShowReportAndOfferTable(") < 2:
    raise SystemExit("GridReportPresenter must retain both report call shapes")

core_path = "src/CE.Tools.Core/SimplePresentationPackage.cs"
core = read(core_path)
require(
    core_path,
    core,
    ": this(title, subject, author, company, DateTime.UtcNow, slides)",
    ": this(title, subtitle, bullets, metrics)",
)

presentation_path = "src/CE.Tools.Civil3D/ProjectPresentationCommands.cs"
presentation = read(presentation_path)
require(
    presentation_path,
    presentation,
    "using Autodesk.AutoCAD.Geometry;",
    "layers.Cast<ObjectId>().Count()",
    "blocks.Cast<ObjectId>().Count()",
)

asset_path = "src/CE.Tools.Civil3D/EngineeringAssetLibraryCommands.cs"
assets = read(asset_path)
require(asset_path, assets, "catch (System.Exception exception)")
forbid(asset_path, assets, "catch (Exception exception)")

model_path = "src/CE.Tools.Civil3D/ModelDesignAuditCommands.cs"
model = read(model_path)
require(
    model_path,
    model,
    'ReadProperty(layout, "ConfigName")',
    'ReadProperty(layout, "PlotConfigurationName")',
)
forbid(
    model_path,
    model,
    "layout.ConfigName",
    '\\"ConfigName\\"',
    '\\"PlotConfigurationName\\"',
    "`\"ConfigName",
    "`\"PlotConfigurationName",
)

flood_path = "src/CE.Tools.Civil3D/FloodResultReviewCommands.cs"
flood = read(flood_path)
require(
    flood_path,
    flood,
    "SetEntityVisibility(entity, show);",
    "private static void SetEntityVisibility(Entity entity, bool visible)",
)
forbid(
    flood_path,
    flood,
    "entity.Visibility = show ? Visibility.Visible : Visibility.Invisible",
)

sewer_path = "src/CE.Tools.Civil3D/SewerExcavationCommentCommands.cs"
sewer = read(sewer_path)
require(
    sewer_path,
    sewer,
    "double units = 0.0;",
    "double side = 0.0;",
    "double width = 0.0;",
    "double bedding = 0.0;",
    "double cover = 0.0;",
)

road_path = "src/CE.Tools.Civil3D/RoadProductionCommentCommands.cs"
road = read(road_path)
require(
    road_path,
    road,
    "string alignmentStyleName;",
    "result.AlignmentStyleName = alignmentStyleName;",
    "string bandSetStyleName;",
    "result.BandSetStyleName = bandSetStyleName;",
)
forbid(
    road_path,
    road,
    "out result.AlignmentStyleName",
    "out result.AlignmentLabelSetName",
    "out result.ProfileStyleName",
    "out result.ProfileLabelSetName",
    "out result.ProfileViewStyleName",
    "out result.BandSetStyleName",
)

surface_path = "src/CE.Tools.Civil3D/SurfaceSpikeHoleRepairCommands.cs"
surface = read(surface_path)
require(
    surface_path,
    surface,
    "foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())",
    "existingNames.Add(ReadName(existingSurface));",
)

print("Civil 3D 2023 compiler compatibility fixes validated.")
