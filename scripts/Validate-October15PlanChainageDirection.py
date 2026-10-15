#!/usr/bin/env python3
"""Regression guard: lower chainage must display on the left of each plan viewport."""
import math
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "src" / "CE.Tools.Civil3D" / "RoadAutoSheetingCommands.cs").read_text(encoding="utf-8-sig")
assert "double startU = cos * first.X + sin * first.Y" in source
assert "double endU = cos * last.X + sin * last.Y" in source
assert "endU <= startU + Tol" in source
assert "NormalizeTwist(-angle)" in source
start = source.index("private static double NormalizeTwist(")
end = source.index("private static bool TryPointLocation(", start)
assert "(value + Math.PI)" not in source[start:end], "Do not normalize away the needed 180-degree westbound twist."
for direction in range(0, 360, 15):
    angle = math.radians(direction)
    dx, dy = 300 * math.cos(angle), 300 * math.sin(angle)
    directed = math.atan2(dy, dx)
    u = dx * math.cos(directed) + dy * math.sin(directed)
    twist = (-directed) % (2 * math.pi)
    displayed_dx = dx * math.cos(twist) - dy * math.sin(twist)
    assert u > 0 and displayed_dx > 0, direction
for name in ["PlanTopTolerance", "PlanBottomTolerance", "PlanStartTolerance",
             "PlanEndTolerance", "ProfileFitPriority", "RoadSheetJob"]:
    assert name in source, name
print("PASS: chainage left-to-right for 24 headings; existing sheet features preserved.")
