"""Regression checks for the October 5 profile/BOQ/junction follow-up."""
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

plot = read("October03ViewportPlotProfileCommands.cs")
boq = read("August13RoadConstructionBoqCommands.cs")
profiles = read("ProfileViewBatchCommands.cs")
edge = read("RoadEdgeLevelProfileCommands.cs")
road_settings = read("RoadProjectSettingsCommands.cs")
junction = read("September16FieldCommentCompletionCommands.cs")
menu = read("September11FieldCompletionMenu.cs")

for marker in [
    "PlotToFileFolder()",
    "SearchOption.AllDirectories",
    "startedUtc.AddSeconds(-2)",
    "UseShellExecute = true",
]:
    if marker not in plot:
        errors.append("plot PDF open-after-save missing: " + marker)

for marker in [
    '"Road shoulders"',
    "ShoulderArea",
    "ShoulderWidth",
    "LinkQuantityClass.Shoulder",
    '"SHOULDER"',
    '"SHLD"',
    '"VERGE"',
]:
    if marker not in boq:
        errors.append("road shoulder BOQ missing: " + marker)

for marker in [
    '"CE_ROADEDGELEVELS"',
    '"HalfWidth"',
    '"SampleInterval"',
    '"-LEFT-EDGE"',
    '"-RIGHT-EDGE"',
    "alignment.PointLocation(",
    "surface.FindElevationAtXY(",
    "CivilProfile.CreateByLayout(",
    '"CE_ROADPROFILEVIEWFINAL "',
]:
    if marker not in edge:
        errors.append("road edge-level profiles missing: " + marker)

for marker in [
    "EdgeLevelHalfWidth",
    "EdgeLevelSampleInterval",
]:
    if marker not in road_settings:
        errors.append("road edge-level defaults not persisted: " + marker)

if '"Left / Right Road Edge Levels"' not in menu or '"CE_ROADEDGELEVELS"' not in menu:
    errors.append("road edge-level command not exposed in Field Completion")

for marker in [
    '"CE_PROFILEVIEWFITALL"',
    '"RowHeight"',
    '"BottomRows"',
    '"ProfileToTopLabelRows"',
    '"TopLabelToFrameRows"',
    "FitProfileViewElevationRanges(",
    "Math.Floor(minimum / rowHeight)",
    "Math.Ceiling(maximum / rowHeight)",
    "ViewsLabelMarginFit",
]:
    if marker not in profiles:
        errors.append("profile label-clearance fit missing: " + marker)

for marker in [
    'model.AddText("Layer", "04 Output", "Bellmouth layer"',
    '"LimitLayer"',
    '"ExistingBellmouthScope"',
    '"Selected junction bellmouths"',
    '"All junction bellmouths"',
    "ReadAllBellmouthFeatureLineIds(",
    "IsJunctionBellmouthFeatureLine(",
    "limitLayerId",
    "bellmouthLayerId",
    "JunctionLimitLayer",
]:
    if marker not in junction:
        errors.append("junction scope/layer split missing: " + marker)

batch_start = junction.find('public void CreateAllJunctionReturns()')
batch_end = junction.find('private static bool TryBuildCandidate', batch_start)
batch = junction[batch_start:batch_end] if batch_start >= 0 and batch_end > batch_start else ""
if "surface.Rebuild()" in batch:
    errors.append("batch junction workflow still rebuilds reference TOP surfaces")

limit_start = junction.find("private static void AddTJunctionLimitFeatureLines(")
limit_end = junction.find("private static double PlanDistance", limit_start)
limit_block = junction[limit_start:limit_end] if limit_start >= 0 and limit_end > limit_start else ""
if "surface.Rebuild()" in limit_block:
    errors.append("T-limit workflow still rebuilds reference TOP surfaces")

if errors:
    print("October 5 profile/BOQ/junction follow-up FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 profile/BOQ/junction follow-up passed.")
