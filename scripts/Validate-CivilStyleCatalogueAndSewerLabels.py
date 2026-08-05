#!/usr/bin/env python3
"""Validate strict Civil 3D style discovery and automatic sewer labels."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
HELPER = CIVIL / "CivilStyleCatalogV2.cs"
PROJECT = CIVIL / "ProjectStyleCenterCommands.cs"
DISCOVERY = CIVIL / "CivilStyleDiscovery.cs"
SEWER = CIVIL / "SewerProductionCommands.cs"
LABELS = CIVIL / "SewerNetworkLabelCommands.cs"
WORKFLOW = ROOT / ".github" / "workflows" / "core-tests.yml"

errors: list[str] = []

for path in (HELPER, PROJECT, DISCOVERY, SEWER, LABELS, WORKFLOW):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

helper = HELPER.read_text(encoding="utf-8") if HELPER.exists() else ""
project = PROJECT.read_text(encoding="utf-8") if PROJECT.exists() else ""
discovery = DISCOVERY.read_text(encoding="utf-8") if DISCOVERY.exists() else ""
sewer = SEWER.read_text(encoding="utf-8") if SEWER.exists() else ""
labels = LABELS.read_text(encoding="utf-8") if LABELS.exists() else ""
workflow = WORKFLOW.read_text(encoding="utf-8") if WORKFLOW.exists() else ""

required_helper_markers = (
    "internal static class CivilStyleCatalogV2",
    "StyleBase style = OpenStyle",
    'LooksLikeRuntimeClassName',
    '"LabelStyles.PipeLabelStyles.PlanProfileLabelStyles"',
    '"LabelStyles.StructureLabelStyles.LabelStyles"',
    '"LabelSetStyles.AlignmentLabelSetStyles"',
    '"LabelSetStyles.ProfileLabelSetStyles"',
    '"ProfileViewBandSetStyles"',
    '"PipeRuleSetStyles"',
    '"StructureRuleSetStyles"',
    '"SectionViewBandSetStyles"',
    '"CorridorStyles"',
    '"AssemblyStyles"',
    '"CodeSetStyles"',
)
for marker in required_helper_markers:
    if marker not in helper:
        errors.append(f"Strict style catalogue is missing marker: {marker}")

for marker in (
    '"Alignment Label Style"',
    '"Profile Label Style"',
    '"Profile View Band Set Style"',
    '"Pipe Rule Set"',
    '"Structure Rule Set"',
    '"Section Label Set Style"',
    '"Section View Band Set Style"',
    '"Pipe Table Style"',
    '"Structure Table Style"',
):
    if marker not in project:
        errors.append(f"Project Style Centre is missing category: {marker}")

if ("CivilStyleCatalogV2.ReadProjectCatalogue(" not in project or
        "SelectionKeys" not in project):
    errors.append("Project Style Centre is not using strict typed style discovery")

if "value as StyleBase" not in discovery:
    errors.append("Named Objects Dictionary fallback is not restricted to StyleBase")

if "ProductionStyleCatalog.ReadNames(" in sewer:
    errors.append("Sewer Settings still uses the legacy style catalogue")
if "CivilStyleCatalogV2.ReadNames(" not in sewer:
    errors.append("Sewer Settings is not using strict typed style discovery")

for marker in (
    'pipe ? "PlanProfileLabelStyles" : "LabelStyles"',
    "TryCreatePipeLabel(",
    "TryCreateStructureLabel(",
    "typeof(double)",
    "typeof(Point3d)",
    "CivilStyleCatalogV2.ReadObjectIds(collection, transaction)",
):
    if marker not in labels:
        errors.append(f"Automatic sewer label source is missing marker: {marker}")

if "StructureLabelStyles.PlanProfileLabelStyles" in project + labels:
    errors.append("Invalid structure PlanProfileLabelStyles path remains")
if re.search(r'Name\s*=\s*"AeccDb', project + helper + discovery):
    errors.append("A hard-coded AeccDb runtime class name remains in style choices")
if "Validate-CivilStyleCatalogueAndSewerLabels.py" not in workflow:
    errors.append("Core tests do not run the Civil style/sewer-label validator")

for path, text in (
    (HELPER, helper),
    (PROJECT, project),
    (DISCOVERY, discovery),
    (SEWER, sewer),
    (LABELS, labels),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces: {path.relative_to(ROOT)}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses: {path.relative_to(ROOT)}")

if errors:
    print("Civil style catalogue / sewer label validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Civil style catalogue / sewer label validation passed: typed StyleBase "
    "choices, label sets, band sets, rule sets and explicit pipe/structure "
    "label creation are wired."
)
