#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/September09SewerSurfaceRulesRuntime.cs": [
        "absoluteSumpElevation = lowestInvert - sumpDepth",
        '"ControlSumpBy"',
        '"Elevation"',
        'TrySetDoubleProperty(structure, "SumpElevation", absoluteSumpElevation)',
        'TrySetDoubleProperty(structure, "SumpDepth", Math.Abs(sumpDepth))',
    ],
    "src/CE.Tools.Civil3D/August24FieldCompletionCommands.cs": [
        "ResolveAbsoluteSumpElevation",
        'ReadFiniteDouble(structure, "SumpDepth")',
        "lowestConnectedInvert - sumpDepth",
        "Math.Max(0.0, lowInvert - sump)",
    ],
    "src/CE.Tools.Civil3D/SewerProductionCommands.cs": [
        "RepairLegacyRelativeStructureSumps(database, bindings)",
        "AddBranchPartsSafely(",
        "One native part per transaction",
        "Do not open the ProfileView",
        "looksRelative",
        "absoluteElevation = lowestInvert - Math.Max(0.0, depth)",
    ],
    "src/CE.Tools.Civil3D/September09FieldRefinementRuntime.cs": [
        "ChooseRoadContinuation(",
        "adjacency[n].Count == 1",
        "bestDot <= -0.5",
        "main road as one joined polyline",
    ],
    "src/CE.Tools.Civil3D/September16RuntimeRecoveryCommands.cs": [
        "detached side database",
        "detached.ReadDwgFile(",
        "detached.GetObjectId(",
        "using (DocumentLock targetLock = target.LockDocument())",
        "no clipboard or live source-database clone",
    ],
}

errors = []
for relative, markers in checks.items():
    path = ROOT / relative
    if not path.exists():
        errors.append(f"missing file: {relative}")
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            errors.append(f"{relative}: missing marker {marker!r}")

profile = (ROOT / "src/CE.Tools.Civil3D/SewerProductionCommands.cs").read_text(encoding="utf-8")
if "private static int AddBranchParts(" in profile:
    errors.append("legacy shared-transaction AddBranchParts implementation is still present")
if "binding.ProfileViewId,\n                        OpenMode.ForWrite" in profile and "partsAdded += AddBranchParts(" in profile:
    errors.append("profile view is still held open ForWrite while native parts are added")

assembly = (ROOT / "src/CE.Tools.Civil3D/September16RuntimeRecoveryCommands.cs").read_text(encoding="utf-8")
start = assembly.find('CE_ASSEMBLYCOPYSAFE')
end = assembly.find("private static List<NamedId> ReadAssemblies", start)
copy_block = assembly[start:end] if start >= 0 and end > start else ""
for forbidden in ["source.Database.Wblock(", "using (DocumentLock sourceLock = source.LockDocument())"]:
    if forbidden in copy_block:
        errors.append(f"assembly copy still uses freeze-prone live source path: {forbidden}")

sump = (ROOT / "src/CE.Tools.Civil3D/September09SewerSurfaceRulesRuntime.cs").read_text(encoding="utf-8")
if 'TrySetEnumProperty(structure, "ControlSumpBy", "Depth", "ByDepth");\n            bool depthSet' in sump:
    errors.append("depth-controlled sump remains the primary write path")

road = (ROOT / "src/CE.Tools.Civil3D/September09FieldRefinementRuntime.cs").read_text(encoding="utf-8")
if "adjacency[next].Count != 2) break" in road:
    errors.append("road centreline trace still stops at every T/X junction")

if errors:
    print("September 18 field-retest validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 18 field-retest validation passed.")
