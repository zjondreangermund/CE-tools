#!/usr/bin/env python3
"""Protect sewer style selection, absolute paper heights and popup production metadata."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(name: str) -> str:
    path = SRC / name
    if not path.exists():
        errors.append(f"Missing source: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8-sig")


catalog = read("CivilStyleCatalogV2.cs")
alignment = read("SewerBranchAlignmentCommands.cs")
sewer = read("SewerProductionCommands.cs")
paper = read("PaperAnnotationScale.cs")
label = read("SewerBranchLabelPlacement.cs")
project = read("ProjectSetupCommands.cs")
popup = read("ProjectSetupPopupWindow.cs")
register = read("ProductionDrawingRegisterCommands.cs")
production = read("ProductionReportCommands.cs")
centre = read("ProductionCommentCommands.cs")

for marker in (
    "ReadCategoryObjectIds(",
    "ScanStyleDictionary(",
    "ResolveStyleId(",
    '"Alignment Label Set Style"',
    '"Profile View Band Set Style"',
    "property.GetGetMethod() == null",
):
    if marker not in catalog:
        errors.append(f"Safe Civil style resolver is missing: {marker}")

for forbidden in (
    "civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles",
    "property.GetValue(style, null)",
):
    if forbidden in alignment:
        errors.append(f"Unsafe sewer alignment style access remains: {forbidden}")
if "CivilStyleCatalogV2.ResolveStyleId(" not in alignment:
    errors.append("CE_SEWALIGN is not using the category-safe style resolver")

for forbidden in (
    "civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles",
    "civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles",
    "civilDocument.Styles.ProfileViewBandSetStyles",
):
    if forbidden in sewer:
        errors.append(f"Unsafe Sewer Settings/Profile property access remains: {forbidden}")
if sewer.count("CivilStyleCatalogV2.ReadNames(document.Database, civilDocument,") < 6:
    errors.append("Sewer Settings does not use category-safe lists for all style selectors")
if sewer.count("CivilStyleCatalogV2.ResolveStyleId(") < 5:
    errors.append("Sewer production does not use the safe style resolver for format/profile creation")

for marker in (
    'GetSystemVariable("CANNOSCALE")',
    "ReadNamedAnnotationScale()",
    "drawing / paper",
    "DrawingUnitsPerMillimetre",
):
    if marker not in paper:
        errors.append(f"Absolute paper height scale repair is missing: {marker}")
if "NormalizeConfiguredPaperHeight" not in label:
    errors.append("Sewer branch labels do not normalize the selected absolute paper height")

for marker in (
    "new ProjectSetupPopupWindow(",
    "AcApplication.ShowModalWindow(window)",
    '"Project Number"',
    '"Drawing Number Prefix"',
    '"Approved By"',
    "ReadSharedProjectMetadata(",
    "MergeSharedProjectMetadata(",
):
    if marker not in project:
        errors.append(f"Project setup popup/shared metadata is missing: {marker}")
if "PromptForValue(editor, field" in project:
    errors.append("CE_PROJECTSETUP still loops through command-line field prompts")
if "class ProjectSetupPopupWindow" not in popup:
    errors.append("Project setup popup window is missing")

for marker in (
    '"CE_DRAWINGREGISTEREDIT"',
    "class ProductionDrawingRegisterWindow",
    "DataGridTextColumn",
    '"DRAWING_REGISTER_METADATA"',
    '"Title Block Source"',
    "ProductionTitleBlockManager",
    "WblockCloneObjects(",
    "AttributeReference",
    "ProjectSetupCommands.MergeSharedProjectMetadata",
):
    if marker not in register:
        errors.append(f"Drawing-register/title-block popup is missing: {marker}")

for marker in (
    "ProductionDrawingRegisterCommands.EditForProduction(",
    '"Save & Generate"',
    "ProductionTitleBlockManager.TryInsert(",
    "drawingRegister.Find(package.LayoutName)",
    "ProductionDrawingRegisterRow",
    '"DRAWING NO."',
    '"ISSUE DATE"',
):
    if marker not in production:
        errors.append(f"Drawing-book linkage is missing: {marker}")
if "Create or refresh the A4/A3 client and A1/A0 construction-book layouts" in production:
    errors.append("CE_DRAWINGBOOK still relies on its old command-line confirmation")
if centre.count('"CE_DRAWINGREGISTEREDIT "') < 2:
    errors.append("Production and Print popup centres do not expose the drawing-register editor")

for name, text in (
    ("CivilStyleCatalogV2.cs", catalog),
    ("SewerBranchAlignmentCommands.cs", alignment),
    ("SewerProductionCommands.cs", sewer),
    ("PaperAnnotationScale.cs", paper),
    ("SewerBranchLabelPlacement.cs", label),
    ("ProjectSetupCommands.cs", project),
    ("ProductionDrawingRegisterCommands.cs", register),
    ("ProductionReportCommands.cs", production),
    ("ProductionCommentCommands.cs", centre),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses in {name}")

if errors:
    print("Sewer/project/production popup validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Sewer/project/production popup validation passed: safe Civil style resolution, "
    "absolute paper heights, project popup, editable drawing register and title-block linkage are present."
)
