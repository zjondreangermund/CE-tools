#!/usr/bin/env python3
"""Regression guards for the Civil 3D 2023 compile failures reported 4 Oct."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "boq": SRC / "August13RoadConstructionBoqCommands.cs",
    "plot": SRC / "October03ViewportPlotProfileCommands.cs",
    "platform": SRC / "PlatformProductionCommands.cs",
    "corridor": SRC / "RoadCorridorCompletionCommands.cs",
}

texts = {}
errors = []
for key, path in files.items():
    if not path.is_file():
        errors.append("Missing source: " + str(path))
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8-sig")

boq = texts["boq"]
plot = texts["plot"]
platform = texts["platform"]
corridor = texts["corridor"]

if "using FeatureLinePointType = Autodesk.Civil.FeatureLinePointType;" not in boq:
    errors.append("Road BOQ does not qualify Civil 3D 2023 FeatureLinePointType.")

if "entry.Value is ObjectId" not in plot or "(ObjectId)entry.Value" not in plot:
    errors.append("Layout dictionary entry is not converted from object to ObjectId.")
if "DateTime before =" not in plot or "DateTime.MinValue" not in plot:
    errors.append("PDF baseline timestamp local is still potentially unassigned.")
if 'SetSystemVariable(\n                    "PLOTTOFILEPATH"' not in plot:
    errors.append("Plot folder does not use the AutoCAD 2023 PLOTTOFILEPATH system variable.")
if "AcApplication.AcadApplication" in plot:
    errors.append("Unsupported AutoCAD 2023 AcadApplication managed property remains.")

if "foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())" not in platform:
    errors.append("Platform slope command does not enumerate the current selection directly.")
if "pattern.Rebuild();" in corridor:
    errors.append("Unsupported Civil 3D 2023 CorridorSlopePattern.Rebuild remains.")
if "typedCorridor.Rebuild();" not in corridor:
    errors.append("Owning corridor rebuild is missing after slope-pattern creation.")

if errors:
    print("October 4 Civil 3D 2023 compile regression FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 Civil 3D 2023 compile regression passed: FeatureLinePointType, "
    "layout ObjectId conversion, PDF baseline assignment, plot-folder API, "
    "platform selection scope and corridor slope-pattern rebuild compatibility "
    "are guarded."
)
