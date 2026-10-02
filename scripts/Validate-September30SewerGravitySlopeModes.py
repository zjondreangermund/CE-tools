#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
runtime = (ROOT / "src/CE.Tools.Civil3D/September09SewerSurfaceRulesRuntime.cs").read_text(encoding="utf-8-sig")
menu = (ROOT / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8-sig")
workflow = (ROOT / ".github/workflows/core-tests.yml").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    '"Gravity - constant downhill slope by branch"',
    '"Follow natural ground at specified depth"',
    '"Natural-ground depth to pipe crown (m)"',
    "ApplyConstantGravitySlopes(",
    "ApplyGravitySegment(",
    "ApplyNaturalGroundDepth(",
    "downstreamZ =",
    "SewerGravityGradeSolver.Solve(",
    "grade.DownstreamInvert + step.InnerRadius",
    "SewerPipeConnections.PipeIds(structure)",
    "SewerManholeConnectionCommands.EnableAndReport(",
    "downstreamZ >= upstreamZ - 1e-9",
    "step.Slope * step.Run",
    "PipeNamePattern.Match(pipe.Name",
    "ReadStructureName(",
    "preservePipeEndpoints = false",
    "!preservePipeEndpoints",
    "MinimumSlope = slope",
    "step.Slope = minimumSlope",
    "downstreamCover < minimumCover",
    "requiredIncrease",
    "Math.Min(maximumSlope, requested)",
    "Minimum depth / cover at structures (m)",
    "Maximum pipe slope (%)",
    "minimumSlope > maximumSlope",
]:
    if marker not in runtime:
        errors.append("missing sewer gravity/NG marker: " + marker)

if 'double direction = naturalEndZ <= naturalStartZ ? -1.0 : 1.0;' in runtime.split(
        "internal static void LinkExistingPartsToSurface", 1)[1].split(
        "internal static void CreateBranchAlignmentsSafe", 1)[0]:
    errors.append("CE_SEWLINKSURFACE still chooses gravity direction from natural ground")

for marker in [
    "keeps P#.1 at the specified starting slope",
    "remaining pipes at the specified minimum slope by default",
    "only steepens where a lower incoming invert or minimum structure depth/cover requires it",
    "never above the specified maximum",
    "Natural-ground and Civil 3D rule-set modes remain separate",
]:
    if marker not in menu:
        errors.append("Field Completion description missing: " + marker)

if "Validate-September30SewerGravitySlopeModes.py" not in workflow:
    errors.append("core tests do not run the sewer gravity slope regression")

if errors:
    print("September 30 sewer gravity slope validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 30 sewer gravity slope validation passed.")
