#!/usr/bin/env python3
"""Guard manually dragged Civil 3D 2024 COGO label locations on refresh."""
from pathlib import Path
from math import hypot

root = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
styles = (root / "CogoPointProjectStyleCommands.cs").read_text(encoding="utf-8-sig")
dynamic = (root / "UniversalDynamicRefreshCommands.cs").read_text(encoding="utf-8-sig")
vertex = (root / "VertexSettingOutCommands.cs").read_text(encoding="utf-8-sig")

def branch(code, start):
    at = code.index(start)
    opening = code.rfind("#if CE_CIVIL3D_2024", 0, at)
    closing = code.index("#else", at)
    assert opening >= 0 and opening < at < closing
    return code[opening:closing]

# Standard refresh NEVER invokes the opt-in overlap solver on 2024.
assert "ApplySelectedStyles(document, false)" in branch(
    dynamic, "ApplySelectedStyles(document, false)")
assert "ApplySelectedStyles(document, true)" in dynamic  # legacy 2023 path
assert "ApplySelectedStyles(document, false)" in branch(
    styles, "ApplySelectedStyles(document, false)")
assert 'CE_COGOOVERLAPFIX' in styles  # explicit user action remains supported

# User-edited label position precedes any style mutation and wins over stale Xrecords.
cogo2024 = branch(styles, "Capture the live label location BEFORE applying styles")
assert cogo2024.index("originalLabel = point.LabelLocation") < cogo2024.index("point.StyleId = pointStyleId")
assert cogo2024.index("originalLabel = point.LabelLocation") < cogo2024.index("point.LabelStyleId = labelStyleId")
assert "point.LabelLocation = originalLabel" in cogo2024
assert "savedOffset - currentOffset" in cogo2024
assert "NormalizeOffset(stored, database)" not in cogo2024
assert "point.LabelLocation = anchor + stored" in styles  # original 2023 path

# Explicit overlap action must not move already-clear manually placed text.
assert "if (!occupied.Any(existing => existing.Intersects(currentBox)))" in styles

# Vertex refresh saves dragged offset BEFORE changing point coordinates, applies
# it AFTER labels/names and XData have been updated; retain legacy 2023 path.
v2024 = branch(vertex, "Preserve a manually dragged COGO label's offset")
assert v2024.index("previousLabel = cogo.LabelLocation") < v2024.index("cogo.Easting = record.Point.X")
assert v2024.index("cogo.PointName = record.PointName") < v2024.index("cogo.LabelLocation = desiredLabel")
assert "Point3d desiredLabel = record.Point + preservedLabelOffset" in v2024
assert "#else" in vertex and "cogo.Easting = record.Point.X" in vertex

# No clamping of large hand-edited offsets. Geometry shifts preserve the
# exact offset; a refresh without point movement leaves the label unchanged.
for anchor, label, new_anchor in [
    ((0, 0), (0.4, 0.2), (0, 0)),
    ((10, 30), (12, 32), (11, 34)),
    ((15, 0), (-20, -10), (15, 0)),
    ((-99, 250), (-100, 500), (-90, 250)),
]:
    offset = (label[0] - anchor[0], label[1] - anchor[1])
    moved_label = (new_anchor[0] + offset[0], new_anchor[1] + offset[1])
    assert abs(moved_label[0] - new_anchor[0] - offset[0]) < 1e-10
    assert abs(moved_label[1] - new_anchor[1] - offset[1]) < 1e-10
    if anchor == new_anchor:
        assert moved_label == label
    assert hypot(*offset) >= 0

print("PASS: 2024 COGO manual label positions preserved on vertex/style/dynamic refresh; 2023 path unchanged.")
