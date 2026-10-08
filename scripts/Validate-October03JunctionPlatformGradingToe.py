#!/usr/bin/env python3
"""Regression guard for 3 Oct junction/platform grading and daylight presentation."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
dynamic_path = SRC / "August23PlatformDynamicGradingCommands.cs"

errors = []
if not dynamic_path.is_file():
    errors.append("Missing August23PlatformDynamicGradingCommands.cs")
    text = ""
else:
    text = dynamic_path.read_text(encoding="utf-8-sig")

required = [
    '"CE_PLATFORMGRADETOSURFACE"',
    '"CE_JUNCTIONGRADETOSURFACE"',
    '"Site", "07 Grading group", "Grading / toe Site"',
    '"<Auto: source site / CE-PLATFORM-SITE>"',
    '"SlopePatternMode"',
    '"Corridor-style long / short"',
    '"All rays full to toe"',
    '"CutColor"',
    '"FillColor"',
    '"ToeColor"',
    'out bool isCut',
    'sample.Cut = isCut;',
    'out List<SlopeRaySample> samples',
    'IList<SlopeRaySample> resolvedSamples',
    'TrySnapToToeVertex(',
    'sample.EndPoint',
    'CivilFeatureLine.MoveToSite(',
    'TryCreateGradingGroup(',
    'out string error)',
    '"AutomaticSurfaceCreation"',
    'true);',
    'TryCreateInfill(',
    'InteriorSeed(source.Points)',
    'NativeGroupReady',
    'CutSlopeLinesCreated',
    'FillSlopeLinesCreated',
    'SiteName = values.Length > 16',
    'SlopePatternMode = values.Length > 17',
    'CutColorIndex = values.Length > 18',
    'FillColorIndex = values.Length > 19',
    'ToeColorIndex = values.Length > 20',
]
for marker in required:
    if marker not in text:
        errors.append("Missing grading/toe marker: " + marker)

for forbidden in [
    'sample.Cut =\n                            endPoint.Z > sample.Point.Z + 0.005',
    'SetProperty(group, "AutomaticSurfaceCreation", false)',
]:
    if forbidden in text:
        errors.append("Obsolete grading behavior still present: " + forbidden)

# The exact resolved daylight samples must be handed directly to visible
# projection-line creation. This prevents a second surface solve from producing
# long lines that overshoot/miss the toe.
# Match semantically instead of depending on indentation. The call must pass
# the exact resolved daylight sample collection to visible slope-line creation.
compact = "".join(text.split())
build_call = "TryCreateSlopeLines(document.Database,source,daylight,resolvedSamples,link"
if build_call not in compact:
    errors.append("Visible slope lines are not using the same resolved samples as the toe.")

# Long rays must snap to the toe vertices; only the short corridor-style rays may
# intentionally stop halfway.
if "if (sample.HalfLength)" not in text or "GradingSlopeTicks.TryShortTick(" not in text:
    errors.append("Corridor-style short-ray handling is missing.")
if """else if (!TrySnapToToeVertex(
                                 daylight,
                                 sample.EndPoint,
                                 out rayEnd))""" not in text:
    errors.append("Long slope rays are not explicitly trimmed/snapped to toe vertices.")

# Native infill may only be attempted for a closed source in a valid Site.
if """if (link.NativeInfill &&
                source.Closed &&
                !source.SiteId.IsNull)""" not in text:
    errors.append("Native grading group/infill Site guard is missing.")

# Do not silently erase an existing working infill during a dynamic refresh.
if "Cleanup(document.Database, previousInfillId)" in text:
    errors.append("Existing native infill is still erased during grade refresh.")

if errors:
    print("October 3 junction/platform grading-toe validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 3 junction/platform grading-toe validation passed: cut/fill "
    "classification, shared toe/ray endpoints, corridor-style display, "
    "colour/Site persistence, grading-group creation and infill preservation "
    "are guarded."
)
