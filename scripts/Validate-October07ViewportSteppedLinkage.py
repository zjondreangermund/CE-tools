"""Regression guard for Oct 7 viewport fitting and stepped-junction linkage comments."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

viewport = (SRC / "October03ViewportPlotProfileCommands.cs").read_text(encoding="utf-8-sig")
viewport_create = (SRC / "ProfileViewportCreation.cs").read_text(encoding="utf-8-sig")
relative = (SRC / "FeatureLineRelativeCommands.cs").read_text(encoding="utf-8-sig")

for marker in [
    '"ExistingViewports"',
    '"Keep existing viewports"',
    '"Remove existing model viewports"',
]:
    if marker not in viewport_create + viewport:
        errors.append("viewport keep/remove option missing: " + marker)

for marker in [
    "RemoveExistingModelViewports(",
    "viewport.ViewTarget =",
    "new Point3d(centerX, centerY, 0.0)",
    "viewport.ViewCenter =",
    "new Point2d(0.0, 0.0)",
]:
    if marker not in viewport:
        errors.append("viewport cleanup/centering behavior missing: " + marker)

for marker in [
    "NormalizeRelationsToRoot(document)",
    "TryResolveRootRelation(",
    "inheritedHorizontal + horizontal",
    "inheritedVertical + vertical",
    "original road-edge source",
]:
    if marker not in relative:
        errors.append("root-linked stepped-offset behavior missing: " + marker)

# The root normalizer must preserve each child's sequence while replacing the
# immediate parent handle with the original road-edge handle and cumulative offsets.
for marker in [
    "existing.Sequence",
    "cumulativeHorizontal",
    "cumulativeVertical",
    "rootId.Handle.ToString()",
]:
    if marker not in relative:
        errors.append("stepped relation flattening detail missing: " + marker)

if errors:
    print("October 7 viewport / stepped linkage regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 7 viewport / stepped linkage regression passed.")
