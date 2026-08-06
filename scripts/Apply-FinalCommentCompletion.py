#!/usr/bin/env python3
"""Wire final comment modules into CE Tools menus, startup and workflows."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")


def replace_once(path: Path, old: str, new: str) -> None:
    text = read(path)
    if new in text:
        return
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"Expected exactly one marker in {path.relative_to(ROOT)}; found {count}: {old[:120]}")
    write(path, text.replace(old, new, 1))


plugin = CIVIL / "PluginEntry.cs"
replace_once(
    plugin,
    "            CogoPointProjectStyleManager.Initialize();\n            SewerNetworkDynamicSequenceManager.Initialize();\n            AcApplication.Idle += OnApplicationIdle;",
    "            CogoPointProjectStyleManager.Initialize();\n            SewerNetworkDynamicSequenceManager.Initialize();\n            CeInteractionTelemetryManager.Initialize();\n            UniversalDynamicRefreshManager.Initialize();\n            AcApplication.Idle += OnApplicationIdle;",
)
replace_once(
    plugin,
    "            AcApplication.Idle -= OnApplicationIdle;\n            SewerNetworkDynamicSequenceManager.Terminate();\n            CogoPointProjectStyleManager.Terminate();",
    "            AcApplication.Idle -= OnApplicationIdle;\n            UniversalDynamicRefreshManager.Terminate();\n            CeInteractionTelemetryManager.Terminate();\n            SewerNetworkDynamicSequenceManager.Terminate();\n            CogoPointProjectStyleManager.Terminate();",
)
replace_once(
    plugin,
    '                        Cmd("Resolve COGO Label Overlaps", "CE_COGOOVERLAPFIX ", "Move COGO labels only while keeping survey point coordinates fixed.")),',
    '                        Cmd("Resolve COGO Label Overlaps", "CE_COGOOVERLAPFIX ", "Move COGO labels only while keeping survey point coordinates fixed."),\n                        Cmd("Convert Curves and Polylines", "CE_CURVECONVERT ", "Convert lines, arcs, circles, splines, lightweight and 3D polylines through one popup.")),',
)
replace_once(
    plugin,
    '                        Cmd("Automatic Linked-Table Refresh", "CE_AUTOREFRESH ", "Turn deferred automatic coordinate, setting-out and BOQ table refresh on or off for the active drawing."),',
    '                        Cmd("Automatic Linked-Table Refresh", "CE_AUTOREFRESH ", "Turn deferred automatic coordinate, setting-out and BOQ table refresh on or off for the active drawing."),\n                        Cmd("Universal Dynamic Refresh", "CE_DYNAMICREFRESHALL ", "Refresh every linked CE table, leader, MText, COGO point, junction, sewer sequence and title block."),\n                        Cmd("Universal Refresh Settings", "CE_DYNAMICREFRESHSETTINGS ", "Configure deferred automatic refresh after CE commands and source edits."),\n                        Cmd("CE Click and Time Statistics", "CE_CLICKSTATS ", "Show command starts, every in-command click, right-click, undo/redo and elapsed time per DWG."),',
)

road = CIVIL / "RoadProductionCommentCommands.cs"
replace_once(
    road,
    '                    RoadAction("Create road profiles", "CE_ROADPROFILES", "Create existing-ground profiles and ordered profile views.", "2 — Profiles"),',
    '                    RoadAction("Create NGL and final road profiles", "CE_ROADPROFILEFULL", "Create existing-ground profiles, final editable design profiles and ordered profile views.", "2 — Profiles"),',
)
replace_once(
    road,
    '                    RoadAction("Create road corridors", "CE_ROADCORRIDORS", "Create one corridor for every CE road alignment/profile pair.", "4 — Corridors"),',
    '                    RoadAction("Create complete road corridors", "CE_ROADCORRIDORFULL", "Create corridors and complete baselines, regions, assemblies, targets, TOP/DATUM surfaces, boundaries and slope patterns.", "4 — Corridors"),',
)
replace_once(
    road,
    '                    RoadAction("Create dynamic intersections", "CE_INTCREATE", "Create linked road intersection output.", "5 — Intersections"),',
    '                    RoadAction("Create dynamic intersections", "CE_INTCREATE", "Create linked road intersection output.", "5 — Intersections"),\n                    RoadAction("Create T-junction bellmouths", "CE_ROADTJUNCTION", "Create linked T-junction bellmouth returns and number them clockwise.", "5 — Intersections"),\n                    RoadAction("Create cross-junction bellmouths", "CE_ROADCROSSJUNCTION", "Create four linked cross-junction bellmouth returns and number them clockwise.", "5 — Intersections"),\n                    RoadAction("Number selected junction bellmouths", "CE_JUNCTIONNUMBER", "Group roads top-left to bottom-right and apply J1.1/J1.2 sequences clockwise.", "5 — Intersections"),\n                    RoadAction("Refresh linked junctions", "CE_JUNCTIONREFRESH", "Refresh linked bellmouth labels after road edits.", "5 — Intersections"),',
)

production = CIVIL / "ProductionCommentCommands.cs"
replace_once(
    production,
    '                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),\n                    new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),',
    '                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),\n                    new ProductionChoice("Refresh project information and title blocks", "CE_PROJECTMETADATAREFRESH "),\n                    new ProductionChoice("Synchronize title-block attributes", "CE_TITLEBLOCKSYNC "),\n                    new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),',
)
replace_once(
    production,
    '                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),\n                    new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),',
    '                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),\n                    new ProductionChoice("Refresh project information and title blocks", "CE_PROJECTMETADATAREFRESH "),\n                    new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),',
)

# Polyline.GetArcSegmentAt returns geometry rather than an AutoCAD Curve in Civil 3D 2023.
# Sample the complete lightweight polyline through its Curve interface instead.
convert = CIVIL / "CurveConversionCommands.cs"
replace_once(
    convert,
    "            Polyline lightweight = entity as Polyline;\n            if (lightweight != null)\n            {\n                for (int index = 0; index < lightweight.NumberOfVertices; index++)\n                {\n                    Point3d start = lightweight.GetPoint3dAt(index);\n                    AddDistinct(points, start);\n                    int next = index + 1;\n                    if (next >= lightweight.NumberOfVertices)\n                    {\n                        if (!lightweight.Closed) continue;\n                        next = 0;\n                    }\n                    if (Math.Abs(lightweight.GetBulgeAt(index)) > 1e-12)\n                    {\n                        Curve segment = lightweight.GetArcSegmentAt(index);\n                        AddSamples(segment, maximumSegment, points, false);\n                    }\n                }\n                return points;\n            }",
    "            Polyline lightweight = entity as Polyline;\n            if (lightweight != null)\n            {\n                AddSamples(lightweight, maximumSegment, points, true);\n                return points;\n            }",
)

validator = ROOT / "scripts" / "Validate-FinalCommentCompletion.py"
replace_once(
    validator,
    '        "WriteLink",',
    '        "TryReadLink",',
)

workflow = ROOT / ".github" / "workflows" / "core-tests.yml"
replace_once(
    workflow,
    "      - name: Validate dynamic COGO vertex and sewer links\n        run: python3 scripts/Validate-DynamicCogoVertexSewerLinks.py\n\n      - name: Set up .NET",
    "      - name: Validate dynamic COGO vertex and sewer links\n        run: python3 scripts/Validate-DynamicCogoVertexSewerLinks.py\n\n      - name: Validate final remaining comment completion\n        run: python3 scripts/Validate-FinalCommentCompletion.py\n\n      - name: Set up .NET",
)

print("Final comment modules wired into CE Tools.")
