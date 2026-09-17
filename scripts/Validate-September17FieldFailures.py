#!/usr/bin/env python3
"""Static regression gate for September 17 Civil 3D field failures."""
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/August21SurfaceSafety.cs": [
        "FeatureLinePointType.PIPoint",
        "new[] { typeof(Point3d), typeof(double) }",
        "featureLine.SetPointElevation(piIndex",
    ],
    "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs": [
        "ApplySite(id, window.SelectedSiteId)",
        "SynchronizeLinkedAppearance",
    ],
    "src/CE.Tools.Civil3D/September11FieldCompletionCommands.cs": [
        "September09FieldRefinementRuntime.RoadReserveCentrePolylines(document)",
    ],
    "src/CE.Tools.Civil3D/RoadCorridorCompletionCommands.cs": [
        "createdProfiles",
        "TrySetObjectIdCollection",
        "MouseDoubleClick",
    ],
    "src/CE.Tools.Civil3D/August17ProductionFeatureLineCommands.cs": [
        "GeometryFingerprint",
        "CodePriority",
        "SIDEWALK",
    ],
    "src/CE.Tools.Civil3D/September09SewerSurfaceRulesRuntime.cs": [
        "TrySetEnumProperty(structure, \"ControlSumpBy\"",
        "TrySetDoubleProperty(structure, \"SumpDepth\"",
    ],
    "src/CE.Tools.Civil3D/SewerProductionCommands.cs": [
        "var bindings = new List<SewerProfileBinding>()",
        "Civil 3D materialises profile-view band collections",
    ],
    "src/CE.Tools.Civil3D/FloodProductionCulvertDesignCommands.cs": [
        "using (DocumentLock documentLock = document.LockDocument())",
        "entity.SetDatabaseDefaults(database)",
        "entity.LayerId = layerId",
    ],
    "src/CE.Tools.Civil3D/September16RuntimeRecoveryCommands.cs": [
        "source.Database.Wblock(ids, Point3d.Origin)",
        "ReadAssemblyIds(staging)",
    ],
}

failed = False
for relative, markers in checks.items():
    path = root / relative
    if not path.is_file():
        print(f"FAIL missing file: {relative}")
        failed = True
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            print(f"FAIL {relative}: missing {marker}")
            failed = True

surface = (root / "src/CE.Tools.Civil3D/August21SurfaceSafety.cs").read_text(encoding="utf-8")
if '"SetPointsElevation"' in surface:
    print("FAIL unsafe PI-only SetPointsElevation reflection path returned")
    failed = True

if failed:
    sys.exit(1)
print("September 17 Civil 3D field-failure regression passed.")
