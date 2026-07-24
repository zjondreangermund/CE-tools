#!/usr/bin/env python3
"""Source-shape validation for reversible surface correction and performance."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "SurfaceCorrectionCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    source = SOURCE.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")

    commands = (
        "CE_SURFCTOOLS",
        "CE_SURFCSETTINGS",
        "CE_SURFAUDIT",
        "CE_SURFCORRECT",
        "CE_SURFSIMPLIFY",
        "CE_SURFCRESTORE",
        "CE_SURFCINFO",
    )
    for command in commands:
        if f'"{command}"' not in source:
            errors.append(f"SurfaceCorrectionCommands.cs missing command: {command}")
        if f'"{command} "' not in plugin:
            errors.append(f"PluginEntry.cs missing ribbon command: {command}")

    markers = (
        "ZeroElevation",
        "LocalSpike",
        "LocalLow",
        "ExtremeHigh",
        "ExtremeLow",
        "Contamination",
        "PossibleHole",
        "ContaminationKeywords",
        "AnalyseOpenEdges",
        "ReadSurfaceVertices",
        "ReadSurfaceTriangles",
        "BuildCorrectedPoints",
        "SimplifyPoints",
        "InvokeCreateTinSurface",
        "AddPointsToTinSurface",
        "Original source was not modified",
        "CE_TOOLS_SURFACE_CORRECTION",
        "SURFACE_CORRECTION_SETTINGS",
        "GridReportPresenter.ShowReportAndOfferTable",
    )
    for marker in markers:
        if marker not in source:
            errors.append(f"SurfaceCorrectionCommands.cs missing marker: {marker}")

    ribbon_markers = (
        "CE_TOOLS_SURFACE_CORRECTION_MENU",
        "Surface Correction",
        "Audit Surface Quality",
        "Create Reversible Corrected Surface",
        "Create Reversible Simplified Surface",
    )
    for marker in ribbon_markers:
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing surface-correction ribbon marker: {marker}")

    unsafe_claims = (
        "automatically approved",
        "guaranteed correct surface",
        "no engineering review required",
        "safe to issue without review",
    )
    lower_source = source.lower()
    for claim in unsafe_claims:
        if claim in lower_source:
            errors.append(f"Surface source contains unsafe release claim: {claim}")

    for path, text in ((SOURCE, source), (PLUGIN, plugin)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {path.name}")
        if text.count("(") != text.count(")"):
            errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Surface correction source validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Surface correction source validation passed.")
