#!/usr/bin/env python3
"""Guard CE Tools features and build repairs recovered from preserved releases."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

errors: list[str] = []


def read(relative: str) -> str:
    path = ROOT / relative
    if not path.exists():
        errors.append(f"Missing preserved file: {relative}")
        return ""
    return path.read_text(encoding="utf-8")


project = read("src/CE.Tools.Civil3D/CE.Tools.Civil3D.csproj")
sewer_alignment = read("src/CE.Tools.Civil3D/SewerBranchAlignmentCommands.cs")

# Civil 3D 2023 is the user's installed and tested production host.
for fragment in (
    '<AutoCADVersion Condition="\'$(AutoCADVersion)\' == \'\'">2023</AutoCADVersion>',
    "<UseWindowsForms>true</UseWindowsForms>",
    '<Reference Include="System.Windows.Forms" />',
    "Contents\\Windows\\$(AutoCADVersion)",
):
    if fragment not in project:
        errors.append(f"Civil 3D project lost required build configuration: {fragment}")

# Branch labels must remain visibly offset from the generated alignment.
offset_match = re.search(
    r"BranchLabelOffsetFactor\s*=\s*([0-9]+(?:\.[0-9]+)?)",
    sewer_alignment,
)
if not offset_match:
    errors.append("Sewer branch alignment source has no BranchLabelOffsetFactor")
elif float(offset_match.group(1)) < 2.75:
    errors.append(
        "Sewer branch label offset regressed below the approved 2.75 paper-height factor"
    )

for fragment in (
    "BuildLabelPlacements",
    "ResolveScaleAwarePaperDistance",
    "labelNormal",
    "label.Rotation = placement.Rotation",
    "label.BackgroundFill = true",
):
    if fragment not in sewer_alignment:
        errors.append(f"Sewer branch label placement lost required behaviour: {fragment}")

# Files recovered in V54/V55 must not silently disappear during reconciliation.
required_sources = (
    "CivilObjectBatchStyleCommands.cs",
    "FeatureProfileSurfaceCommentCommands.cs",
    "FloatingToolsWindow.cs",
    "ParkingSkewValidationCommands.cs",
    "RoadDriveReviewCommands.cs",
    "RoadProductionCommentCommands.cs",
    "DynamicTypicalDetailCommands.cs",
    "DynamicTypicalDetailStorage.cs",
    "WaterSewerCostEstimateCommands.cs",
    "TypicalDetailsReviewCommands.cs",
    "SettingOutScheduleCommands.cs",
)
for name in required_sources:
    if not (SRC / name).exists():
        errors.append(f"Recovered Civil 3D source is still missing: {name}")

if errors:
    print("Preservation regression gate failed:")
    for error in errors:
        print(f"- {error}")
    sys.exit(1)

print("Preservation regression gate passed.")
