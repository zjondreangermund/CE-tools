#!/usr/bin/env python3
"""Prevent recurrence of paper-space annotative title-block scaling in C3D 2024."""
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
sync = (root / "AnnotationScaleSyncCommands.cs").read_text(encoding="utf-8-sig")
plugin = (root / "PluginEntry.cs").read_text(encoding="utf-8-sig")
guard = (root / "PaperSpaceTextAnnotationGuard.cs").read_text(encoding="utf-8-sig")
assert "#if CE_CIVIL3D_2024" in sync
assert "!string.Equals(block.Name, BlockTableRecord.ModelSpace" in sync
assert "PaperSpaceTextAnnotationGuard.Initialize()" in plugin
assert "PaperSpaceTextAnnotationGuard.Terminate()" in plugin
assert guard.startswith("#if CE_CIVIL3D_2024")
assert 'CE_PAPERTEXTANNOOFF' in guard
assert "paper.IsLayout" in guard and "BlockTableRecord.ModelSpace" in guard
assert "AttributeCollection" in guard
assert "Annotative" in guard and '"False"' in guard
for forbidden in ("TextHeight =", "Height =", "TextString =", "Text ="):
    assert forbidden not in guard, forbidden
print("PASS: 2024 paper text is excluded from annotation sync; open-DWG repair is 2024 only.")
