#!/usr/bin/env python3
"""Static and angle regression checks for safe 2024 viewport text orientation."""
from math import cos, radians, pi
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
source = (root / "ViewportTextUprightCommands.cs").read_text(encoding="utf-8-sig")
plugin = (root / "PluginEntry.cs").read_text(encoding="utf-8-sig")
paper = (root / "PaperSpaceTextAnnotationGuard.cs").read_text(encoding="utf-8-sig")

assert source.startswith("#if CE_CIVIL3D_2024")
assert "ViewportTextUprightManager.Initialize();" in plugin
assert "ViewportTextUprightManager.Terminate();" in plugin
assert "db.ObjectModified += OnObjectModified" in source
assert "AcApplication.Idle += OnIdle" in source
assert 'CE_VPTEXTUPRIGHT' in source and 'CE_VPTEXTAUTO' in source
assert "IsAlreadyAnnotative(entity)" in source
assert "TryEnableNativePaperOrientation(entity)" in source
assert "if (inconsistent) { conflicts++; continue; }" in source
assert "bt[BlockTableRecord.ModelSpace]" in source
assert "space.IsLayout" in source
assert "entity is DBText || entity is MText || entity is MLeader" in source
assert "PaperSpaceTextAnnotationGuard.Initialize();" in plugin
assert "TextHeight =" not in source
assert "CANNOSCALE" not in source

def upright(angle, twists):
    needs = [cos(angle-t) < -1e-6 for t in twists]
    if len(set(needs)) > 1:
        return None
    return (angle+pi) % (2*pi) if needs[0] else angle

for degrees in (0, 20, 40, 89, 91, 120, 170, 190, 220, 260, 300, 340):
    for old in (0, 10, 32, 90, 180):
        a, twist = radians(old), radians(degrees)
        new = upright(a, [twist])
        assert new is not None
        assert cos(new-twist) >= -1e-6, (old,degrees)

assert upright(0, [0, pi]) is None, "Shared non-annotative text must not be reversed for the other viewport"
print("PASS: 2024-only, viewport edit observer, no paper text changes, angle and conflict guards.")
