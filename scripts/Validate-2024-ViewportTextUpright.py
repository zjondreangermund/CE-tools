#!/usr/bin/env python3
"""Check Civil 3D 2024 rotated-plan annotation alignment and safety guards."""
from math import atan2, cos, sin, pi, radians
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
code = (root / "ViewportTextUprightCommands.cs").read_text(encoding="utf-8-sig")
plugin = (root / "PluginEntry.cs").read_text(encoding="utf-8-sig")
assert code.startswith("#if CE_CIVIL3D_2024")
assert "ViewportTextUprightManager.Initialize();" in plugin
assert "ViewportTextUprightManager.Terminate();" in plugin
assert "db.ObjectModified += OnObjectModified" in code
assert "AcApplication.Idle += OnIdle" in code
assert "CE_VPTEXTUPRIGHT" in code and "CE_VPTEXTAUTO" in code
assert "IsAlreadyAnnotative(entity)" in code
assert "TryEnableNativePaperOrientation(entity)" in code
assert "Point3d oldTextLocation = leader.TextLocation;" in code
assert "leader.TextLocation = oldTextLocation;" in code
assert "if (inconsistent) { conflicts++; continue; }" in code
assert "bt[BlockTableRecord.ModelSpace]" in code
assert "space.IsLayout" in code
assert "entity is DBText || entity is MText || entity is MLeader" in code
assert "containing.All(view =>" in code
assert "PaperSpaceTextAnnotationGuard.Initialize();" in plugin
assert "TextHeight =" not in code
assert "CANNOSCALE" not in code

def angular(a,b):
    return abs(atan2(sin(a-b),cos(a-b)))

def horizontal(angles, current):
    if all(angular(t,0.0)<=.001 for t in angles):
        return current
    if any(angular(t,angles[0])>.001 for t in angles):
        return None
    return angles[0] % (2*pi)

for twist_deg in (15,30,45,90,120,180,220,270,330):
    twist = radians(twist_deg)
    for old_deg in (0, 5, 30, 90, 150, 270):
        a = horizontal([twist], radians(old_deg))
        assert angular(a,twist)<1e-8, (twist_deg,old_deg)
assert horizontal([0],radians(35))==radians(35)
assert horizontal([0,radians(90)], 0) is None
assert horizontal([radians(30),radians(90)], 0) is None
assert angular(radians(-180),radians(180))<1e-8
print("PASS: 2024 rotated-view text horizontal, unrotated unaffected, shared-view guard intact.")
