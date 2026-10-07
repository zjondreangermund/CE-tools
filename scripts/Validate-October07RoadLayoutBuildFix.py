"""Regression guard for the Oct 7 real Civil 3D 2023 RoadLayoutAnnotation build failure."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
source = (ROOT / "src" / "CE.Tools.Civil3D" / "RoadLayoutAnnotation.cs").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    "using Autodesk.AutoCAD.EditorInput;",
    "PromptSelectionResult",
    "PromptSelectionOptions",
    "PromptStatus.OK",
]:
    if marker not in source:
        errors.append("EditorInput build support missing: " + marker)

if "AlignmentType.Centerline" in source:
    errors.append("RoadLayoutAnnotation still references unavailable AlignmentType.Centerline in the Civil 3D 2023 field build.")

for marker in [
    "RoadAnnotationPlan.RoadNumber(",
    '"CE road"',
]:
    if marker not in source:
        errors.append("Road-alignment identity fallback missing: " + marker)

if errors:
    print("October 7 RoadLayoutAnnotation build regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 7 RoadLayoutAnnotation build regression passed.")
