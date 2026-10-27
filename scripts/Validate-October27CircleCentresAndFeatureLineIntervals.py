#!/usr/bin/env python3
"""Regression checks for circle centres / midpoint modes and feature-line PI interval insertion.

These source checks cannot replace Autodesk Civil 3D 2023/24/25 runtime testing.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
geometry = (root / "src/CE.Tools.Civil3D/VertexSettingOutGeometry.cs").read_text(encoding="utf-8")
setting = (root / "src/CE.Tools.Civil3D/VertexSettingOutCommands.cs").read_text(encoding="utf-8")
densify = (root / "src/CE.Tools.Civil3D/FeatureLineDensifyCommands.cs").read_text(encoding="utf-8")

assert "entity is Circle" in geometry
assert "Circle circle = entity as Circle" in geometry
assert '"CIRCLE CENTER", 0, circle.Center' in geometry
assert 'ReadSources(' in geometry and 'bool includeAllMidpoints = false' in geometry
assert 'includeAllMidpoints || arc.Length > 10.0' in geometry
assert 'includeAllMidpoints || length > 20.0' in geometry
assert 'bool includeAllMidpoints = false)' in geometry
assert '"Circle centres only"' in setting
assert '"Circle centres and midpoints"' in setting
assert 'out rejected, UsesAllMidpoints(link.GenerationMode)' in setting, "Refresh must restore midpoint mode"
assert 'out geometryRejected, UsesAllMidpoints(generationMode)' in setting
assert 'string.Equals(kind, "CIRCLE CENTER"' in setting
assert 'kind.IndexOf("MIDPOINT"' in setting
assert 'CE_DENSIFYFEATURELINES' in densify
assert 'InsertPIPoint(targets[i])' in densify
assert 'InsertElevationPoint(targets[i])' in densify
assert 'settings.AddPositiveDouble("Interval"' in densify
assert 'DisciplineWorkflowDialogs.Confirm' in densify
assert densify.count('public void Execute()') == 1
print("PASS: circle centres, all-length true midpoints, persisted linked refresh and PI/elevation interval insertion")
