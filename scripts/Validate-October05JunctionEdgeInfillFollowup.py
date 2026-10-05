"""Regression guard for the October 5 junction edge grading / infill follow-up."""
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

dynamic = read("August23PlatformDynamicGradingCommands.cs")
fallback = read("August13JunctionFallbackCommands.cs")
grading_menu = read("GradingSlopeWorkflowCommands.cs")

for marker in [
    '"SourceScope"',
    '"Multiple selected shoulder/sidewalk bellmouth edge lines"',
    '"All shoulder/sidewalk bellmouth edge lines"',
    '"EdgeLayers"',
    "ResolveGradeSourceIds(",
    "MatchesShoulderSidewalkBellmouthEdge(",
    '"sidewalks,shoulders"',
]:
    if marker not in dynamic:
        errors.append("shoulder/sidewalk edge grading scope missing: " + marker)

for marker in [
    '"InfillScope"',
    '"Multiple selected closed junction feature lines"',
    '"All closed junction feature lines"',
    '"Closed lines in grading selection"',
    '"CE_JUNCTIONINFILL"',
    "ResolveClosedJunctionInfillIds(",
    "CreateOrRefreshNativeInfill(",
    "JunctionInfillKey",
    "WriteNativeInfillLink(",
    "ReadNativeInfillLink(",
]:
    if marker not in dynamic:
        errors.append("closed junction infill workflow missing: " + marker)

for marker in [
    "EnsureSourceHasSite(",
    "source.SiteId.IsNull",
    "TryCreateGradingGroup(",
    "TryCreateInfill(",
    "InteriorSeed(source.Points)",
]:
    if marker not in dynamic:
        errors.append("native infill site/group pipeline missing: " + marker)

for marker in [
    "Point2d",
    "TryBuildInfillArguments(",
    "TryReadCreatedObjectId(",
    "BindingFlags.Instance",
]:
    if marker not in dynamic:
        errors.append("Civil 3D infill compatibility fallback missing: " + marker)

if '"CE_JUNCTIONINFILL"' not in fallback:
    errors.append("junction fallback menu still does not route to CE_JUNCTIONINFILL")
if '"CE_JUNCTIONINFILL"' not in grading_menu:
    errors.append("grading workflow does not expose CE_JUNCTIONINFILL")

if '"CE_SURFTOOLS","Create or review the dedicated junction surface and add closed controls."' in fallback:
    errors.append("obsolete generic junction infill menu action remains")

if errors:
    print("October 5 junction edge/infill follow-up FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 junction edge/infill follow-up passed.")
