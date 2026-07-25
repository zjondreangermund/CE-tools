#!/usr/bin/env python3
"""Validate Typical Details Phase 3 and cached ribbon-icon integration."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
files = {
    "dynamic_commands": SRC / "DynamicTypicalDetailCommands.cs",
    "dynamic_engine": SRC / "DynamicTypicalDetailEngine.cs",
    "dynamic_storage": SRC / "DynamicTypicalDetailStorage.cs",
    "ribbon_extension": SRC / "TypicalDetailsRibbonExtension.cs",
    "visuals": SRC / "RibbonVisuals.cs",
    "icon_commands": SRC / "RibbonIconCommands.cs",
}
errors: list[str] = []
texts: dict[str, str] = {}
for key, path in files.items():
    if not path.exists():
        errors.append(f"Missing Phase 3 file: {path.relative_to(ROOT)}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8")

dynamic = texts["dynamic_commands"] + texts["dynamic_engine"] + texts["dynamic_storage"]
extension = texts["ribbon_extension"]
visuals = texts["visuals"]
icon_commands = texts["icon_commands"]

commands = (
    "CE_DETAILPARAMTOOLS",
    "CE_DETAILPARAMSETTINGS",
    "CE_DETAILPARAMCREATE",
    "CE_DETAILPARAMEDIT",
    "CE_DETAILPARAMREFRESH",
    "CE_DETAILPARAMBOQ",
    "CE_DETAILPARAMBOQEXPORT",
    "CE_DETAILPARAMREVIEW",
    "CE_DETAILPARAMINFO",
    "CE_DETAILPARAMDETACH",
    "CE_DETAILPARAMCLEAR",
)
for command in commands:
    if f'"{command}"' not in dynamic:
        errors.append(f"Dynamic source missing command: {command}")
    if f'"{command} "' not in extension:
        errors.append(f"Phase 3 ribbon missing command: {command}")

for detail_type in ("TrenchDrain", "PipeTrench", "ValveChamber", "Kerb", "Headwall"):
    if detail_type not in dynamic:
        errors.append(f"Dynamic source missing detail type: {detail_type}")

for marker in (
    'LinkRecordName = "CE_DYNAMIC_TYPICAL_DETAIL"',
    'GeneratedRecordName = "CE_DYNAMIC_TYPICAL_DETAIL_GENERATED"',
    'BoqLinkRecordName = "CE_DYNAMIC_DETAIL_BOQ_LINK"',
    'SchemaVersion = "2"',
    "WidthMillimetres",
    "DepthMillimetres",
    "LengthMetres",
    "WallThicknessMillimetres",
    "PipeDiameterMillimetres",
    "BeddingDepthMillimetres",
    "ConcreteStrength",
    "Reinforcement",
    "GratingType",
    "CalculateQuantities",
    "BuildParameterTable",
    "BuildBoqTable",
    "WriteBoqLink",
    "SourceHash",
    "SourceModifiedUtc",
    "ComputeSha256",
    "ReviewStatus",
    "Reviewer",
    "ReviewedAtUtc",
    "anchor.OwnerId",
    "RemoveExtensionRecord",
    "source templates modified=0",
    "source template was unchanged",
    "The uncommitted transaction preserved the previous linked output",
):
    if marker not in dynamic:
        errors.append(f"Dynamic source missing safety/linkage marker: {marker}")

for unsafe in ("ReadDwgFile", "DxfIn", ".SaveAs(", "FileMode.CreateNew"):
    if unsafe in dynamic:
        errors.append(f"Dynamic source may write/open source templates unsafely: {unsafe}")

for marker in (
    "CE_TOOLS_TYPICAL_DETAILS_REVIEW_MENU",
    "CE_TOOLS_DYNAMIC_TYPICAL_DETAILS_MENU",
    "Details Standards\\nReview",
    "Dynamic Typical\\nDetails",
    "RibbonMenuButton",
    "RibbonMenuItem",
    "RibbonCommandHandler",
    "TypicalDetailsRibbonExtension.Schedule()",
):
    if marker not in extension + visuals:
        errors.append(f"Ribbon integration missing marker: {marker}")

for forbidden in ("RibbonRow", "new RibbonButton"):
    if forbidden in extension:
        errors.append(f"Phase 3 ribbon reintroduced incompatible type: {forbidden}")

for marker in (
    "Dictionary<string, ImageSource>",
    "CacheSync",
    "RibbonIconMode.TextOnly",
    "RibbonIconMode.Cached",
    "RibbonIconMode.Full",
    'Create("CE_TOOLS_GENERIC_COMMAND", 16)',
    "IsTopLevelIcon",
    "bitmap.Freeze()",
):
    if marker not in visuals:
        errors.append(f"Ribbon icon cache missing marker: {marker}")

if 'CommandMethod("CE_RIBBONICONS"' not in icon_commands:
    errors.append("Ribbon icon command CE_RIBBONICONS is missing")
if "RibbonIconMode.Cached" not in visuals:
    errors.append("Cached ribbon mode is not the default")
if "TypicalDetailsRibbonExtension.EnsureCreated()" not in icon_commands:
    errors.append("Ribbon icon rebuild does not restore additive Typical Details menus")

for path, text in ((files[key], texts[key]) for key in files):
    stripped = re.sub(r'@?"(?:[^"\\]|\\.)*"', '""', text)
    if stripped.count("{") != stripped.count("}"):
        errors.append(f"Unbalanced braces in {path.relative_to(ROOT)}")
    if stripped.count("(") != stripped.count(")"):
        errors.append(f"Unbalanced parentheses in {path.relative_to(ROOT)}")

if errors:
    print("Dynamic Typical Details validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Dynamic Typical Details and cached ribbon-icon source validation passed.")
