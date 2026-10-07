"""Regression guard for Oct 7 grading presentation/surface grouping and sewer profile-part selection."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

grading = (SRC / "August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8-sig")
sewer = (SRC / "September09SewerSurfaceRulesRuntime.cs").read_text(encoding="utf-8-sig")

for marker in [
    '"Existing slope / toe lines"',
    '"Replace existing slope and toe lines"',
    '"Keep existing slope and toe lines"',
    "KeepExistingPresentation",
    "keepExistingToe",
    "keepExistingSlopeLines",
]:
    if marker not in grading:
        errors.append("slope/toe keep-replace behavior missing: " + marker)

for marker in [
    '"Connected feature-line surfaces"',
    '"Create separate surface for every connected feature-line group"',
    "CreateConnectedGradeSurfaces(",
    "ConnectedInPlan(",
    "TryCreateOrRefreshConnectedSurface(",
]:
    if marker not in grading:
        errors.append("separate connected surface behavior missing: " + marker)

for marker in [
    "ProfileViewParts",
    "PickGravityPartsFromProfileViews(",
    "PromptNestedEntityOptions",
    "PromptNestedEntityResult",
    "GetNestedEntity(",
    "GetContainers()",
    "CivilPipe",
    "CivilStructure",
]:
    if marker not in sewer:
        errors.append("individual profile-view sewer-part selection missing: " + marker)

if errors:
    print("October 7 grade/surface/profile-part regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 7 grade/surface/profile-part regression passed.")
