"""Regression guard for CE Top All / CE Bottom All / CE Final Surface production merges."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

def read(name):
    path = SRC / name
    if not path.is_file():
        errors.append("missing file: " + name)
        return ""
    return path.read_text(encoding="utf-8-sig")

merge = read("SurfaceMergeProductionCommands.cs")
surface_menu = read("SurfaceCommands.cs")
road_menu = read("August13RoadProductionCentres.cs")

for marker in [
    'private const string TopOutput = "CE Top All"',
    'private const string BottomOutput = "CE Bottom All"',
    'private const string FinalOutput = "CE Final Surface"',
    '"CE_ROADTOPSURFACEALL"',
    '"CE_ROADBOTTOMSURFACEALL"',
    '"CE_FINALSURFACEMERGE"',
    '"CE_ROADSURFACEMERGE"',
]:
    if marker not in merge:
        errors.append("production surface output/command missing: " + marker)

for marker in [
    '"TOP-RD-"',
    '"BOTTOM-RD-"',
    '"All road TOP and grading surfaces"',
    '"All road TOP surfaces only"',
    '"All grading surfaces only"',
    '"Select road TOP / grading surfaces"',
    "IsGradingSurface(",
]:
    if marker not in merge:
        errors.append("surface source scope missing: " + marker)

# Civil 3D 2023's supported production operation is TinSurface.PasteSurface.
if "target.PasteSurface(sourceId)" not in merge:
    errors.append("native TinSurface.PasteSurface is not used")
if "target.AddVertices(points)" in merge:
    errors.append("vertex-only pseudo merge fallback remains in production merge")

# Natural ground must be first so later road/grading paste operations override it.
for marker in [
    "var mergeSources = new List<SurfaceChoice>",
    "mergeSources.AddRange(sources)",
    "MergeIntoNewSurface(document, civil, FinalOutput, mergeSources, styleId)",
]:
    if marker not in merge:
        errors.append("final surface NG-first paste order missing: " + marker)

# Outputs must be exposed in both road production and general Surface Utilities.
for marker in [
    '"CE_ROADSURFACEMERGE"',
    '"CE-Road Surface Outputs"',
]:
    if marker not in road_menu:
        errors.append("road production surface action missing: " + marker)

for marker in [
    '"CE_ROADTOPSURFACEALL"',
    '"CE_ROADBOTTOMSURFACEALL"',
    '"CE_ROADSURFACEMERGE"',
    '"CE Top All"',
    '"CE Bottom All"',
    '"CE Final Surface"',
]:
    if marker not in surface_menu:
        errors.append("Surface Utilities production action missing: " + marker)

if errors:
    print("October 5 surface production merge FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 surface production merge passed.")
