#!/usr/bin/env python3
"""Validate collision sorting and freeze controls for sewer labels."""

from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (
    root
    / "src"
    / "CE.Tools.Civil3D"
    / "SewerLabelLayoutCommands.cs"
).read_text(encoding="utf-8")

required = (
    '"CE_SEWLABELSORT"',
    '"CE_SEWLABELFREEZE"',
    '"CE_SEWLABELUNFREEZE"',
    "OverlapsAny(",
    "Matrix3d.Displacement(",
    "CE_TOOLS_LABEL_FREEZE",
    "IsFrozen(",
)

missing = [marker for marker in required if marker not in source]
if missing:
    raise SystemExit(
        "Sewer-label layout validation failed: " + ", ".join(missing)
    )

print("Sewer-label collision sorting and freeze controls validated.")
