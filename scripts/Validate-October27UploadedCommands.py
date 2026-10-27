#!/usr/bin/env python3
"""Uploaded CE command integration smoke checks (source-only; not Civil 3D runtime)."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
src = root / "src" / "CE.Tools.Civil3D"

expected = {
    "FeatureLineDensifyCommands.cs": {"CE_DENSIFYFEATURELINES"},
    "RoadCrossingPviCommands.cs": {
        "CE_ROADCROSSINGPVIS", "CE_DRAPECROSSROADPVIS", "CE_REMOVEROADCROSSINGPVIS"
    },
    "BatchCrossSectionsCommands.cs": {"CE_BATCHCROSSSECTIONS"},
    "WaterExcavationCommands.cs": {
        "CE_WATEREXCAVATION", "CE_WATEREXCAVATIONBOQ",
        "CE_WATEREXCAVATIONREFRESH", "CE_WATEREXCAVATIONINFO"
    },
}
pattern = re.compile(r'CommandMethod\s*\(\s*"CE_TOOLS"\s*,\s*"([^"]+)"')
for filename, wanted in expected.items():
    source = (src / filename).read_text(encoding="utf-8")
    commands = set(pattern.findall(source))
    assert wanted <= commands, f"{filename}: missing {sorted(wanted - commands)}"
    assert "assembly: CommandClass" in source, f"{filename}: missing command class registration"
    assert source.count("{") == source.count("}"), f"{filename}: brace mismatch"
    print("PASS", filename, ", ".join(sorted(wanted)))

grading = (src / "August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8")
for token in (
    "CE_PLATFORMGRADETOSURFACE", "CE_JUNCTIONGRADETOSURFACE",
    "InfillWeedDistance", "InfillWeedAngle", "InfillMaxBoundarySegment",
    "maxBoundarySegment", "WeedInfillBoundary"
):
    assert token in grading, "Junction grading integration missing " + token
assert grading.count("public void GradeToSurface()") == 1, "Duplicate GradeToSurface"
road = (src / "RoadLayoutProductionCommands.cs").read_text(encoding="utf-8")
assert "CE_ROADDIMENSIONS" in road, "Existing dimensions command lost"
workflow = (src / "ProductionWorkflowCommands.cs").read_text(encoding="utf-8")
for wanted in expected.values():
    for command in wanted:
        assert command in workflow, "Command missing from workflow menu: " + command
print("PASS", "V27 grading updates, unique command entry and production menu wiring")
