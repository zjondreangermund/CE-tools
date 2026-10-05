"""Regression guard for the October 5 road annotation and junction setting-out follow-up."""
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

layout = read("RoadLayoutAnnotation.cs")
setting = read("VertexSettingOutCommands.cs")
rerun = read("VertexSettingOutRerun.cs")
sequence = read("JunctionSettingOutSequence.cs")
junction = read("September16FieldCommentCompletionCommands.cs")

# Road names: layer, above/below/centered, section midpoints and overlap avoidance.
for marker in [
    '"Road-name layer"',
    '"Name position"',
    '"Above", "Below", "Centered"',
    'SectionPlacement = "Midpoints between cross and T-junctions"',
    '"Name locations"',
    '"Avoid overlapping annotations"',
    "RoadAnnotationPlacement.Stations(",
    "occupancy.PlaceText(",
]:
    if marker not in layout:
        errors.append("road-name annotation option missing: " + marker)

# Road dimensions: layer, section-midpoint placement and collision avoidance.
for marker in [
    '"Dimension layer"',
    '"Dimension locations"',
    '"Lane and full road widths"',
    '"Avoid overlapping annotations"',
    "occupancy.Free(",
    "AlignedDimension(",
]:
    if marker not in layout:
        errors.append("road-width dimension option missing: " + marker)

# Setting-out presentation and explicit erase-all rerun.
for marker in [
    '"COGO point layer"',
    '"Leader / text layer"',
    '"Leader arrowhead"',
    '"Closed filled"',
    '"Arrow size (paper mm)"',
    '"Erase all junction setting-out output and tables"',
    "EraseAllJunctionSettingOutGroups(",
    "EraseAllJunctionOutput",
]:
    if marker not in setting + rerun:
        errors.append("junction setting-out presentation/rerun option missing: " + marker)

# Exact road identity should travel from generated bellmouth returns into setting-out.
for marker in [
    '"MAIN="',
    '"GROUP="',
    "candidate.MainSourceId",
    "candidate.JunctionGroup",
    "curveSourceIds",
]:
    if marker not in junction:
        errors.append("junction road-ownership metadata missing: " + marker)

for marker in [
    'GetXDataForApplication("CE_ROAD_JUNCTION")',
    '"MAIN="',
    '"GROUP="',
    "source.RoadHandle",
    "source.JunctionGroup",
]:
    if marker not in sequence:
        errors.append("setting-out does not read exact junction road ownership: " + marker)

# Bellmouth/return order is explicitly top-left clockwise to bottom-left.
if "RoadAnnotationPlan.ClockwiseFromTopLeft(" not in junction:
    errors.append("batch bellmouth labels are not ordered top-left clockwise")
if "Clockwise per junction from top left" not in setting:
    errors.append("vertex setting-out clockwise junction sequence is missing")
if "RoadAnnotationPlan.ClockwiseFromTopLeft(" not in setting:
    errors.append("setting-out source/point ordering does not use clockwise top-left rule")

# Old table road maps must not override newer exact source metadata.
for marker in [
    "Fresh source metadata is authoritative",
    "link.RoadNumbers[source.Handle] = source.RoadNumber",
]:
    if marker not in rerun:
        errors.append("old incorrect ROADMAP cannot self-heal: " + marker)

if errors:
    print("October 5 road annotation / setting-out numbering FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 road annotation / setting-out numbering passed.")
