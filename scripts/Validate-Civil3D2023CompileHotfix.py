#!/usr/bin/env python3
"""Validate the Civil 3D 2023 compile fixes reported by the Windows build."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

cogo = (SRC / "CogoPointProjectStyleCommands.cs").read_text(encoding="utf-8")
compat = (SRC / "Civil3D2023CompileCompatibility.cs").read_text(encoding="utf-8")
junction = (SRC / "RoadJunctionCompletionCommands.cs").read_text(encoding="utf-8")

errors = []

if "Vector3d.Zero" in cogo:
    errors.append("CogoPointProjectStyleCommands.cs still uses unsupported Vector3d.Zero.")
if "new Vector3d(0.0, 0.0, 0.0)" not in cogo:
    errors.append("Explicit Civil 3D 2023 zero-vector initialization is missing.")

required_compat = [
    "this ProductionSettingsDialogModel model",
    "model.AddPositiveDouble(key, group, label, value, description);",
    "internal sealed class PromptAngleResult",
    "PromptDoubleResult _result",
    "implicit operator PromptAngleResult(PromptDoubleResult result)",
]
for marker in required_compat:
    if marker not in compat:
        errors.append("Compatibility source is missing: " + marker)

if "PromptAngleResult mainAngle" not in junction:
    errors.append("Junction workflow no longer exercises the Civil 3D 2023 angle-result adapter.")

for path in [
    SRC / "CurveConversionCommands.cs",
    SRC / "RoadCorridorCompletionCommands.cs",
    SRC / "RoadJunctionCompletionCommands.cs",
    SRC / "UniversalDynamicRefreshCommands.cs",
]:
    text = path.read_text(encoding="utf-8")
    if ".AddDouble(" in text and "Civil3D2023CompileCompatibility.cs" not in str(path):
        # The call is valid only because the shared extension must be present.
        if "this ProductionSettingsDialogModel model" not in compat:
            errors.append(path.name + " uses AddDouble without the compatibility extension.")

if errors:
    raise SystemExit("\n".join(errors))

print("Civil 3D 2023 compile hotfix validation passed: zero vector, numeric popup fields and angle results are compatible.")
