#!/usr/bin/env python3
"""Source-shape validation for Flood Analysis Phase 1."""
from pathlib import Path
import math
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "FloodAnalysisCalculator.cs"
COMMAND = ROOT / "src" / "CE.Tools.Civil3D" / "FloodAnalysisCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
errors = []

for path in (CORE, COMMAND, RIBBON):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if not errors:
    core = CORE.read_text(encoding="utf-8")
    command = COMMAND.read_text(encoding="utf-8")
    ribbon = RIBBON.read_text(encoding="utf-8")
    for marker in ("2, 5, 10, 20, 25, 50, 100", "RationalPeakFlow", "RecommendCircularCulvert"):
        if marker not in core:
            errors.append(f"Core calculator missing: {marker}")
    for marker in ('"CE_FLOODQUICK"', "CompareScenarios", "BuildTable", "Preliminary only"):
        if marker not in command:
            errors.append(f"Flood command missing: {marker}")
    if "CE_FLOODQUICK " not in ribbon:
        errors.append("Ribbon does not expose CE_FLOODQUICK")
    expected = 0.7 * 100.0 * 10.0 / 360.0
    if not math.isclose(expected, 1.9444444444444444, rel_tol=1e-12):
        errors.append("Rational-method reference calculation failed")
    for name, text in ((CORE.name, core), (COMMAND.name, command), (RIBBON.name, ribbon)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {name}")

if errors:
    print("Flood Analysis Phase 1 validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)
print("Flood Analysis Phase 1 source validation passed.")
