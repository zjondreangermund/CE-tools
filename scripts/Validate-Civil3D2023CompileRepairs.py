#!/usr/bin/env python3
"""Protect repairs discovered by the first V61 Civil 3D 2023 build."""

from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(name: str) -> str:
    path = SRC / name
    if not path.is_file():
        errors.append(f"Missing Civil 3D source: {name}")
        return ""
    return path.read_text(encoding="utf-8-sig")


grid = read("GridReportPresenter.cs")
annotation = read("AnnotationCommands.cs")
feature_relative = read("FeatureLineRelativeCommands.cs")
coordinate = read("DynamicCoordinateLinkStore.cs")
ribbon = read("RibbonVisuals.cs")
workflow = read("WorkflowRepairCommands.cs")
survey = read("SurveyCoordinateWorkflowCommands.cs")

required = {
    "GridReportPresenter.cs": (
        'string tableTitle = ""',
    ),
    "AnnotationCommands.cs": (
        "IList<ObjectId> generatedIds",
        "AppendGeneratedIds(generatedIds, createdIds)",
        "private static void AppendGeneratedIds(",
    ),
    "FeatureLineRelativeCommands.cs": (
        "public static int RefreshAll(Document document)",
        "private static int RebuildChildren(",
    ),
    "DynamicCoordinateLinkStore.cs": (
        "double x = 0.0;",
        "double y = 0.0;",
        "double z = 0.0;",
    ),
    "RibbonVisuals.cs": (
        "internal enum RibbonIconMode",
        "public static RibbonIconMode Mode",
        "public static void SetMode(RibbonIconMode mode)",
        "public static ImageSource CommandSmall(string command)",
    ),
    "WorkflowRepairCommands.cs": (
        "internal static List<SurfaceChoice> ReadSurfaceChoices(Document document)",
    ),
    "SurveyCoordinateWorkflowCommands.cs": (
        "using System.Linq;",
    ),
}
texts = {
    "GridReportPresenter.cs": grid,
    "AnnotationCommands.cs": annotation,
    "FeatureLineRelativeCommands.cs": feature_relative,
    "DynamicCoordinateLinkStore.cs": coordinate,
    "RibbonVisuals.cs": ribbon,
    "WorkflowRepairCommands.cs": workflow,
    "SurveyCoordinateWorkflowCommands.cs": survey,
}
for name, markers in required.items():
    for marker in markers:
        if marker not in texts[name]:
            errors.append(f"{name} is missing compile-repair marker: {marker}")

if "private static List<SurfaceChoice> ReadSurfaceChoices" in workflow:
    errors.append("ReadSurfaceChoices regressed to private and breaks production modules")
if annotation.count("public static bool Create(") < 2:
    errors.append("AnnotationWriter generated-object compatibility overload is missing")

for name, text in texts.items():
    if text.count("{") != text.count("}"):
        errors.append(f"Brace imbalance in {name}")

if errors:
    print("Civil 3D 2023 V61 compile-repair validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Civil 3D 2023 V61 compile repairs passed: report compatibility, annotation "
    "object capture, feature-line refresh, coordinate initialization, ribbon "
    "icons, surface selection and survey LINQ are protected."
)
