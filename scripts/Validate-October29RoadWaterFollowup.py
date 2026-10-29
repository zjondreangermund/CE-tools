#!/usr/bin/env python3
"""Source-level regression checks for Road/Water October 29 follow-up.

Civil 3D host build and design-DWG verification are separate requirements.
"""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1] / "src/CE.Tools.Civil3D"
read = lambda name: (root / name).read_text(encoding="utf-8")

water = read("WaterBranchNamesCommands.cs")
dim = read("RoadLayoutAnnotation.cs")
scales = read("AnnotationScaleSyncCommands.cs")
regions = read("CorridorRegionToggleCommands.cs")
seq = read("August13RoadProductionCentres.cs")
popup = read("DisciplineWorkflowDialogs.cs")
last = read("PopupLastUsedDefaults.cs")

checks = {
    "Water branch labels can be updated, not simply retained":
        'Existing branch names updated:' in water and 'old.Contents = displayName;' in water,
    "Water branch names come from alignment by default":
        '"NameSource", "02 Presentation", "Label text", "Alignment name"' in water,
    "Water label geometry and matching safeguard":
        'new Point2d(t.Location.X, t.Location.Y)' in water and
        'old.UpgradeOpen();' in water,
    "road dimension metres displayed as mm":
        'dim.Dimlfac = recipe.MillimetreFactor;' in dim and
        'dim.Dimpost = "<> mm";' in dim,
    "Road dimensions annotative and current scale":
        'PaperAnnotationScale.SetAnnotative(dim);' in dim and
        'AnnotationScaleSyncManager.AddAllAnnotationScaleContexts(dim, db);' in dim,
    "Road widths manually configurable":
        '"Manual lane and road widths"' in dim and 'ManualLane' in dim and 'ManualRoad' in dim,
    "manual dimensions do not modify road geometry":
        'manual ? centre + normal * recipe.ManualLane' in dim,
    "road dimensions persist manual settings in annotation links":
        '"ANNOT4|"' in dim,
    "all annotation scales no longer enumerated":
        'object current = ResolveCurrentAnnotationContext(database);' in scales and
        'foreach (object context in enumerable)' not in scales,
    "region enabled on every baseline":
        'baseline.NeedsProcessing = true;' in regions,
    "region all-on repeatable with existing snapshot":
        'Existing corridor-region snapshot preserved.' in regions,
    "region restore retains baseline enabled snapshot":
        'BaselineEnabled = originalBaseline' in regions,
    "last user popup values override stale DWG records":
        popup.index("ProductionSettingsPersistenceStore.Load(document.Database, model)") <
        popup.index("CrossDrawingProductionSettingsStore.Load(model);"),
    "popup last-used restored regardless of saved style selection":
        'CrossDrawingProductionSettingsStore.Load(remembered);' in last
}
for title, passed in checks.items():
    if not passed: raise AssertionError("Oct29 regression: " + title)
    print("PASS:", title)

actions = re.findall(r'A\("(\d+)\.[^"]+","(CE_[A-Z0-9_]+)".*?"(\d+) Road Sequence"\)', seq)
if len(actions) != 41: raise AssertionError("Expected 41 final Road sequence steps; got " + str(len(actions)))
if [int(x[0]) for x in actions] != list(range(1, 42)):
    raise AssertionError("Final Road sequence numbering must be contiguous")
ordered = [x[1] for x in actions]
for before, after in (("CE_ALIGNLABELSETMULTI", "CE_ROADNAMES"),
                      ("CE_JUNCTIONGRADETOSURFACE", "CE_PLATFORMDRAPEMULTI")):
    if ordered.index(after) != ordered.index(before) + 1:
        raise AssertionError("Incorrect sequence: " + after + " must directly follow " + before)
print("PASS: all final Road production steps numbered and moved in sequence")
