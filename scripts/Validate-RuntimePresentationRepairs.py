#!/usr/bin/env python3
"""Guard the Civil 3D runtime presentation repairs reported after V61 install."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

FILES = {
    "scale": SRC / "PaperAnnotationScale.cs",
    "ribbon": SRC / "PluginEntry.cs",
    "visuals": SRC / "RibbonVisuals.cs",
    "labels": SRC / "SewerBranchLabelPlacement.cs",
    "sewer": SRC / "SewerProductionCommands.cs",
    "stormwater": SRC / "StormwaterProductionCommands.cs",
    "coordinates": SRC / "SurveyCoordinateWorkflowCommands.cs",
    "links": SRC / "DynamicCoordinateLinkStore.cs",
    "styles": SRC / "ProjectStyleCenterCommands.cs",
    "grid": SRC / "GridReportPresenter.cs",
    "popup": SRC / "PopupTablePresenter.cs",
    "water": SRC / "WaterProductionCommands.cs",
}

errors: list[str] = []
texts: dict[str, str] = {}
for name, path in FILES.items():
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")
        continue
    texts[name] = path.read_text(encoding="utf-8")

required = {
    "scale": (
        'GetSystemVariable("CANNOSCALEVALUE")',
        'string.Equals(units, "Meters"',
        "paper * CurrentAnnotationScale(database) * DrawingUnitsPerMillimetre(database)",
        'string[] candidates = { "True", "Yes", "On" };',
    ),
    "ribbon": (
        "BuildPanels(tab);",
        "RibbonIconMode.TextOnly",
        'menuId ?? "CE_TOOLS_MENU"',
        '"_COMMAND_"',
    ),
    "visuals": ("RibbonIconMode.TextOnly",),
    "labels": (
        "PaperAnnotationScale.ModelTextHeight",
        "OffsetPoint",
        "RepeatSpacing",
    ),
    "sewer": (
        'model.AddChoice("BranchLabelSide"',
        'new[] { "Alternating", "Above", "Below" }',
        'Value("BranchLabelSide", BranchLabelSide)',
    ),
    "stormwater": (
        'model.AddChoice("BranchLabelSide"',
        "SewerBranchLabelPlacement.BuildPlacements",
        "DisciplineWorkflowDialogs.SelectWorkflow",
    ),
    "coordinates": (
        "cogo.RawDescription",
        "PaperAnnotationScale.ModelTextHeight",
        '"CE Tools - Point Naming"',
        '"CE Tools - Coordinate Register"',
    ),
    "links": (
        "CogoPoints.SetRawDescription",
        "pointNameRepairs",
        "SetPointName(database, repair.Key, repair.Value);",
    ),
    "styles": (
        "EnumerateStyleItems",
        '"GetObjectIds"',
        "GetIndexParameters",
        "root.Children.Remove(scroll);",
        "root.Children.Add(scroll);",
    ),
    "grid": ("PaperAnnotationScale.SetAnnotative(table);",),
    "popup": ("PaperAnnotationScale.SetAnnotative(table);",),
    "water": (
        "PaperAnnotationScale.ModelTextHeight(\n                            document.Database,",
        "PaperAnnotationScale.SetAnnotative(label);",
    ),
}

for name, markers in required.items():
    text = texts.get(name, "")
    for marker in markers:
        if marker not in text:
            errors.append(f"{FILES[name].name} is missing runtime repair marker: {marker}")

for name, text in texts.items():
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {FILES[name].name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses detected in {FILES[name].name}")

if errors:
    print("CE Tools runtime presentation validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools runtime ribbon, annotation, coordinate and style validation passed.")
