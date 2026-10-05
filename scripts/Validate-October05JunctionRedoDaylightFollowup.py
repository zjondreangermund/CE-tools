"""Regression gate for the October 5 junction/redo/daylight field follow-up."""
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

junction = read("September16FieldCommentCompletionCommands.cs")
surface = read("JunctionSurfaceVertices.cs")
junction_top = read("September18RoadJunctionCompletionCommands.cs")
redo = read("August24RoadElevationDynamicManager.cs")
corridor = read("RoadCorridorCompletionCommands.cs")
output_fix = read("August13RoadProfileCorridorOutputFixCommands.cs")
menu = read("September11FieldCompletionMenu.cs")

for marker in [
    '"RoadSourceScope"',
    '"All road alignments"',
    '"Selected road alignments"',
    '"Selected road-centre curves"',
    "ResolveRoadSourceIds(",
    "BuildAlignmentPlanCurve(",
    "alignment.PointLocation(",
]:
    if marker not in junction:
        errors.append("junction alignment scope missing: " + marker)

for marker in [
    '"ExistingJunctions"',
    '"Erase existing before re-run"',
    "EraseExistingBatchJunctionOutputs(",
    "GetXDataForApplication(",
]:
    if marker not in junction:
        errors.append("junction rerun erase option missing: " + marker)

# The old field key is deliberately retained so previously modified values
# continue loading from the shared popup persistence store.
if 'model.AddText("Layer", "04 Output", "Bellmouth layer"' not in junction:
    errors.append("legacy junction layer key was not retained for saved defaults")
for marker in ['"LimitLayer"', '"TopSurfaceVertices"']:
    if marker not in junction:
        errors.append("junction saved-output field missing: " + marker)

apply_start = surface.find("internal static int Apply(")
apply_end = surface.find("\n        }", apply_start)
apply_block = surface[apply_start:apply_end] if apply_start >= 0 else ""
if "AddLineVerticesToSurface" in apply_block:
    errors.append("junction TOP reference helper still writes surface vertices")
if "OpenMode.ForRead" not in surface:
    errors.append("junction TOP reference surfaces are not opened read-only")

endpoint_start = junction_top.find('CE_JUNCTIONENDPOINTSTOTOPSURFACES')
endpoint_end = junction_top.find('CE_PIPESLOPETOOUTLET', endpoint_start)
endpoint_block = junction_top[endpoint_start:endpoint_end] if endpoint_start >= 0 and endpoint_end > endpoint_start else ""
for marker in [
    "JunctionSurfaceVertices.Apply(",
    "OpenMode.ForRead",
    "TOP surfaces unchanged",
    "no vertices or breaklines were added",
]:
    if marker not in endpoint_block:
        errors.append("read-only junction endpoint workflow missing: " + marker)
for forbidden in [
    "TryAddSurfaceVertex(",
    'TryInvoke(surface, "Rebuild")',
]:
    if forbidden in endpoint_block:
        errors.append("junction endpoint workflow still mutates TOP surfaces: " + forbidden)

for marker in [
    "CommandWillStart += OnCommandWillStart",
    "UndoRedoActive",
    "SuppressUntilUtc",
    "DisableUndoRecording(true)",
    "DisableUndoRecording(false)",
    "IsUndoRedo(command)",
]:
    if marker not in redo:
        errors.append("road elevation redo preservation missing: " + marker)

for marker in [
    "RepairExistingDaylightSlopePatterns(",
    "Math.Abs(outerOffset) >",
    "Math.Abs(hingeOffset) + 0.05",
    "FindSameSideSlopeHinge(",
    "TrySharedSlopeStationRange(",
]:
    if marker not in corridor:
        errors.append("corridor cut/fill daylight repair missing: " + marker)

for marker in [
    "Math.Abs(item.Offset) >",
    "Math.Abs(hingeOffset) + 0.05",
    "FindSameSideSlopeLine(",
]:
    if marker not in output_fix:
        errors.append("corridor output daylight pairing guard missing: " + marker)

if "no surface vertices or breaklines are added" not in menu:
    errors.append("Field Completion still describes the old surface-writing junction workflow")

if errors:
    print("October 5 junction/redo/daylight follow-up FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 junction/redo/daylight follow-up passed.")
