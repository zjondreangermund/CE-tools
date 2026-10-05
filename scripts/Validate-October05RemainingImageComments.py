"""Guard the remaining October 5 Civil 3D image comments.

The native drawing still needs field verification, but these checks prevent the
shipped command paths from regressing back to surface mutation, structure-label
anchoring, non-persistent branch layers, or branch-local-only deep raising.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
src = root / "src" / "CE.Tools.Civil3D"

junction = (src / "JunctionSurfaceVertices.cs").read_text(encoding="utf-8-sig")
road = (src / "September18RoadJunctionCompletionCommands.cs").read_text(encoding="utf-8-sig")
branch = (src / "SewerBranchAlignmentCommands.cs").read_text(encoding="utf-8-sig")
settings = (src / "SewerProductionCommands.cs").read_text(encoding="utf-8-sig")
gravity = (src / "September09SewerSurfaceRulesRuntime.cs").read_text(encoding="utf-8-sig")

errors = []

apply_start = junction.find("internal static int Apply(")
apply_end = junction.find("\n        }", apply_start)
apply_block = junction[apply_start:apply_end] if apply_start >= 0 else ""
if "TryAssignFeatureLineElevations" not in apply_block:
    errors.append("Junction feature-line helper is no longer sampling TOP surfaces.")
if "AddLineVerticesToSurface" in apply_block:
    errors.append("Junction feature-line helper writes back into TOP surfaces.")
if "OpenMode.ForRead" not in junction:
    errors.append("TOP reference surfaces are not opened read-only.")

cmd_start = road.find('CE_ROADJUNCTIONFEATURELINESTOP')
cmd_end = road.find('CE_ROADTJUNCTIONASSEMBLYLIMITS', cmd_start)
cmd_block = road[cmd_start:cmd_end] if cmd_start >= 0 and cmd_end >= 0 else ""
for marker in [
    "TOP surfaces unchanged",
    "TryAssignFeatureLineElevations",
    "No TOP surface was changed",
]:
    if marker not in cmd_block:
        errors.append(f"Elevation-only junction command missing marker: {marker}")
if "AddLineVerticesToSurface" in cmd_block or 'TryInvoke(surface, "Rebuild")' in cmd_block:
    errors.append("Elevation-only junction command still mutates/rebuilds TOP surfaces.")

for marker in [
    "BranchLabelLayer",
    "<Create new CE Tools layer>",
    "ReadPipePlanLabelAnchors",
    "PipeLabel",
    "AnchorToNearestPipePlanLabel",
]:
    if marker not in branch:
        errors.append(f"Sewer branch-name integration missing: {marker}")
if '"Structure"' not in branch:
    errors.append("Sewer branch-name PipeLabel scan no longer excludes structure labels.")
for marker in [
    'public string BranchLabelLayer { get; set; } = "CE-BRANCH-LABELS";',
    'Value("BranchLabelLayer", BranchLabelLayer)',
    'key == "BranchLabelLayer"',
]:
    if marker not in settings:
        errors.append(f"Persistent branch-label layer setting missing: {marker}")

for marker in [
    "ReadDeepRaiseMetrics(",
    "movableHeadwaters",
    "grade.IncomingCount == 0",
    "step.HeadwaterInvert += raise",
]:
    if marker not in gravity:
        errors.append(f"Connected deep-run raising missing: {marker}")

if errors:
    raise SystemExit("\n".join(errors))

print("October 5 remaining image-comment integration passed.")
