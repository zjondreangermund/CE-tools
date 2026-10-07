"""Regression guard for Oct 7 grading presentation/surface groups and sewer profile-view selection."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

grading = (SRC / "August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8-sig")
sewer = (SRC / "September09SewerSurfaceRulesRuntime.cs").read_text(encoding="utf-8-sig")

for marker in [
    '"ExistingPresentation"',
    '"Replace existing generated lines"',
    '"Keep existing generated lines"',
    "KeepExistingPresentation",
    "keepExistingToe",
    "keepExistingSlopeLines",
]:
    if marker not in grading:
        errors.append("existing slope/toe keep-replace behavior missing: " + marker)

for marker in [
    '"ConnectedSurfaces"',
    '"Create separate surface per connected group"',
    '"ConnectedSurfacePrefix"',
    '"ConnectedTolerance"',
    "CreateConnectedGradeSurfaces(",
    "ConnectedInPlan(",
    "TryCreateOrRefreshConnectedSurface(",
    "TryAddStandardBreaklines(",
    "CE-JUNCTION-GRADE",
]:
    if marker not in grading:
        errors.append("connected feature-line surface behavior missing: " + marker)

# The old named surface must only be cleaned up after the replacement TIN has
# been created/rebuilt so a failed refresh cannot erase the last valid result.
new_index = grading.find("newSurfaceId =")
rebuild_index = grading.find("surface.Rebuild()", new_index)
cleanup_index = grading.find("Cleanup(\n                        database,\n                        oldSurfaceId", rebuild_index)
if new_index < 0 or rebuild_index < 0 or cleanup_index < 0 or not (new_index < rebuild_index < cleanup_index):
    errors.append("connected-group surface replacement is not create-first / cleanup-after-success")

for marker in [
    "[AllNetworkParts/Select/ProfileViews]",
    'scope.Keywords.Add("ProfileViews")',
    "ExpandGravitySelection(",
    "FilterProfileViewIds(",
    "ReadGravityPartsDisplayedInProfileViews(",
    "GetProfileViewsDisplayingMe",
    "CivilProfileView",
]:
    if marker not in sewer:
        errors.append("profile-view sewer part selection missing: " + marker)

if errors:
    print("October 7 grading/surface/profile-sewer regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 7 grading/surface/profile-sewer regression passed.")
