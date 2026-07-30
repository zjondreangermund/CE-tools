#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
checks = {
    "src/CE.Tools.Civil3D/NetworkAssetScheduleCommands.cs": [
        "CE_NETWORKSCHEDULE",
        "Discipline, network, type, name, description, family, size, length, slope, bend angle, start/end levels",
        "internal static int RefreshAll",
        "CE_NETWORKSCHEDULEBOQ",
    ],
    "src/CE.Tools.Civil3D/BillOfQuantitiesCommands.cs": [
        "MatchesDiscipline",
        "Object does not match the selected",
        "BoqDiscipline.Stormwater",
        "BoqDiscipline.Sewer",
        "BoqDiscipline.Water",
        "BoqDiscipline.Platform",
    ],
    "src/CE.Tools.Civil3D/StandardQuantityTemplateCommands.cs": [
        "80 mm 35 MPa interlocks",
        "20 mm sand",
        "G5 @ 95% MOD AASHTO",
        "G6 @ 93% MOD AASHTO",
        "rip and recompact to 90% MOD AASHTO",
        "Cut (up to assembly datum)",
        "Fill - G9 @ 90% MOD AASHTO (up to assembly datum)",
    ],
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs": [
        "NetworkAssetScheduleCommands.RefreshAll(document)",
        "StandardQuantityTemplateCommands.RefreshAll(document)",
        "WaterSewerCostEstimateCommands.RefreshAll(document)",
    ],
}

errors = []
for relative, needles in checks.items():
    text = (ROOT / relative).read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            errors.append(f"{relative}: missing {needle!r}")
if errors:
    raise SystemExit("\n".join(errors))
print("Network-report and BOQ red-yellow V26 source validation passed.")
