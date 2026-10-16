#!/usr/bin/env python3
"""Regression guard: 2024 grid boundary scopes and feature lines, legacy 2023 retained."""
from pathlib import Path
root = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
front = (root / "FinalAllCommentsCompletionCommands.cs").read_text(encoding="utf-8-sig")
engine = (root / "August18DynamicGridSettingOutCommands.cs").read_text(encoding="utf-8-sig")

assert '"CE_GRIDSETTINGOUT"' in front
assert "Grid boundary [Selected/All/Rectangle]" in front
assert 'scope.Keywords.Add("Selected")' in front
assert 'scope.Keywords.Add("All")' in front
assert 'scope.Keywords.Add("Rectangle")' in front
assert "SelectClosedGridBoundaries(" in front
assert "SelectImplied()" in front
assert "GetSelection(" in front
assert "bt[BlockTableRecord.ModelSpace]" in front
assert "Polyline2d" in front and "Polyline3d" in front
assert "feature.Closed" in front
assert "feature.GetPoints(" in front
assert "August18DynamicGridSettingOutCommands.CreateFromClosedBoundaries(" in front
assert "PromptPointResult first = document.Editor.GetPoint" in front # Rectangle maintained
assert "#if CE_CIVIL3D_2024" in front
assert "internal static void CreateFromClosedBoundaries(" in engine
assert "FilterClosedPolylines(document.Database, boundaries)" in engine
assert "feature != null && feature.Closed" in engine
assert "feature.GetPoints(" in engine
assert "#if CE_CIVIL3D_2024" in engine
assert "BuildRecords(sources, link)" in engine
assert "link.SourceHandles" in engine
assert "CE_GRIDSETTINGOUTREFRESH" in engine
assert "Vector3d labelOffset = Vector3d.Zero" in engine
assert "point.LabelLocation = record.Point + labelOffset" in engine
assert "if (moved)" in engine
print("PASS: 2024 All/Selected/Rectangle closed polyline and feature-line grid; 2023 rectangle preserved.")
