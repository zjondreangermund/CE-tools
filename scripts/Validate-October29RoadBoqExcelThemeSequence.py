#!/usr/bin/env python3
"""Source-level regression checks for Road BOQ, XLSX launch, theme, and sequence.

These checks do not substitute for Autodesk Civil 3D runtime compilation and
quantities checked against real corridor/subassembly surfaces.
"""
from pathlib import Path
import re
base = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
read = lambda f: (base / f).read_text(encoding="utf-8")
boq = read("August13RoadConstructionBoqCommands.cs")
grid = read("GridReportPresenter.cs")
exporter = read("TableExcelExportCommands.cs")
theme = read("CeInterfaceTheme.cs")
plugin = read("PluginEntry.cs")
centres = read("August13RoadProductionCentres.cs")
appearance = read("August11ProductionCentreCommands.cs")

tests = {
    "CE construction BOQ still includes datum cut/fill": "AddDatumCutFill(" in boq,
    "Road surface widths use non-overlapping XY offsets":
        "LinkOffsetRange(" in boq and "MergeIntervals(" in boq and
        "snapshot.RoadSurfaceWidth = UnionWidth(roadLinks);" in boq,
    "Each corridor paved area in BOQ":
        '"Road surface - " + road.Key' in boq and
        "totals.RoadSurfaceAreasByRoad" in boq,
    "Both sides' BasicShoulder top areas captured":
        "shoulderAsSidewalk ? first.ShoulderWidth + second.ShoulderWidth" in boq,
    "Sidewalk volume has shape/explicit depth fallback":
        "totals.SidewalkShapeVolume > 1e-9" in boq and
        "totals.SidewalkArea * sidewalkDepth" in boq,
    "Junction surface triangulated and merged without duplicate source counts":
        'string.Equals(surface.Name, "CE Junctions All"' in boq and
        "surface.GetTriangles(false)" in boq,
    "Junction grading included in gross cut/fill slopes":
        "totals.SideSlopeArea + totals.JunctionGradingArea3D" in boq,
    "Junction grade and road corridor areas separately identifiable":
        '"Cut/fill side slopes - corridors"' in boq and
        '"Junction grading surfaces - 3D area"' in boq,
    "Report exporter opens workbook after successful Save":
        "Process.Start(new ProcessStartInfo" in grid and
        "UseShellExecute = true" in grid,
    "Native AutoCAD table XLSX exporter still opens workbook":
        "Process.Start(" in exporter,
    "Dark theme registered on plugin startup":
        "CeInterfaceTheme.Initialize();" in plugin,
    "CE WPF panels and controls themed":
        "Panel panel = value as Panel;" in theme and
        "window.Background = palette.Window;" in theme,
    "Changing appearance refreshes existing windows":
        "CeInterfaceTheme.RefreshOpenWindows();" in appearance,
}
for key, value in tests.items():
    if not value:
        raise AssertionError("Road BoQ Excel Theme regression: " + key)
    print("PASS:", key)

actions = re.findall(r'A\("(\d+)\.[^"]+","(CE_[A-Z0-9_]+)".*?"(\d+) Road Sequence"\)', centres)
if len(actions) != 44:
    raise AssertionError("Expected 44 numbered Road Production commands, got %d" % len(actions))
if [int(x[0]) for x in actions] != list(range(1, 45)):
    raise AssertionError("Road Production numbering must be sequential")
names = [x[1] for x in actions]
for before, after in (
    ("CE_ROADCORRIDORFULL", "CE_ROADPROFILEVIEWSPLIT"),
    ("CE_ROADTOPBOTTOMPROFILE", "CE_ROADCROSSINGPVIS"),
    ("CE_ROADCROSSINGPVIS", "CE_REMOVEROADCROSSINGPVIS"),
    ("CE_REMOVEROADCROSSINGPVIS", "CE_ROADVERTICALCURVES"),
):
    if names.index(after) != names.index(before) + 1:
        raise AssertionError(after + " must directly follow " + before)
print("PASS: all 44 numbered Road Production command order and positions")
