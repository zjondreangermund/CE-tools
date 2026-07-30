#!/usr/bin/env python3
"""Source-shape validation for Typical Details Phase 2 standards review."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "TypicalDetailsReviewCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
RIBBON_EXTENSION = ROOT / "src" / "CE.Tools.Civil3D" / "TypicalDetailsRibbonExtension.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN, RIBBON_EXTENSION):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    source = SOURCE.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")
    ribbon = RIBBON_EXTENSION.read_text(encoding="utf-8")

    commands = (
        "CE_DETAILREVIEWTOOLS",
        "CE_DETAILREVIEWSETTINGS",
        "CE_DETAILREVIEW",
        "CE_DETAILREVIEWLIB",
        "CE_DETAILREVIEWREPORT",
        "CE_DETAILREVIEWINFO",
    )
    for command in commands:
        if f'"{command}"' not in source:
            errors.append(f"TypicalDetailsReviewCommands.cs missing command: {command}")
        if f'"{command} "' not in ribbon:
            errors.append(f"TypicalDetailsRibbonExtension.cs missing ribbon command: {command}")

    review_areas = (
        "Title format",
        "Revision table",
        "General notes",
        "Legends",
        "North arrow",
        "Fonts and text styles",
        "Dimensions and dimension styles",
        "Company logo",
        "Sheet numbering",
        "Layer naming",
        "Layer lineweights",
        "Scales",
        "Symbols and blocks",
        "Missing dimensions",
        "Missing callouts",
        "Missing notes and labels",
    )
    for area in review_areas:
        if area not in source:
            errors.append(f"TypicalDetailsReviewCommands.cs missing review area: {area}")

    source_markers = (
        "ReadDwgFile",
        "DxfIn",
        "OpenForReadAndAllShare",
        "Manual visual review required for PDF content",
        "TYPICAL_DETAIL_REVIEW_SETTINGS",
        "TYPICAL_DETAIL_REVIEW_RESULTS",
        "ApprovedTextStyles",
        "ApprovedDimensionStyles",
        "LayerPrefix",
        "NonByLayerColourCount",
        "NonByLayerLineweightCount",
        "GridReportPresenter.ShowReportAndOfferTable",
        "Source DWG/DXF/PDF assets were not modified",
        "No reviewed source file was saved, changed, normalised or approved automatically",
    )
    for marker in source_markers:
        if marker not in source:
            errors.append(f"TypicalDetailsReviewCommands.cs missing marker: {marker}")

    ribbon_markers = (
        "CE_TOOLS_TYPICAL_DETAILS_REVIEW_MENU",
        "Details Standards\\nReview",
        "Review One Detail",
        "Review Complete Detail Library",
        "Show Stored Standards Review",
        "RibbonMenuButton",
        "RibbonMenuItem",
        "RibbonCommandHandler",
    )
    for marker in ribbon_markers:
        if marker not in ribbon:
            errors.append(f"TypicalDetailsRibbonExtension.cs missing details-review ribbon marker: {marker}")

    for incompatible in ("RibbonRow", "new RibbonButton"):
        if incompatible in ribbon:
            errors.append(f"Typical-details ribbon reintroduced incompatible type: {incompatible}")

    for preserved in (
        '"CE_DETAILTOOLS "',
        '"CE_DETAILSETROOT "',
        '"CE_DETAILSEARCH "',
        '"CE_DETAILINSERT "',
        '"CE_DETAILINFO "',
    ):
        if preserved not in plugin:
            errors.append(f"PluginEntry.cs lost Phase 1 command: {preserved}")

    unsafe_claims = (
        "automatically approved detail",
        "source file was modified",
        "no manual review required",
        "pdf content fully inspected",
    )
    lower_source = source.lower()
    for claim in unsafe_claims:
        if claim in lower_source:
            errors.append(f"Typical-details review contains unsafe claim: {claim}")

    for path, text in ((SOURCE, source), (PLUGIN, plugin), (RIBBON_EXTENSION, ribbon)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {path.name}")
        if text.count("(") != text.count(")"):
            errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Typical-details standards review validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Typical-details standards review validation passed.")
