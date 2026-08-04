#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def require(path, markers):
    text = path.read_text(encoding="utf-8")
    missing = [marker for marker in markers if marker not in text]
    if missing:
        raise SystemExit(
            f"{path.name} is missing production repair markers: " +
            ", ".join(missing)
        )


require(
    SRC / "CivilStyleDiscovery.cs",
    [
        '"GetEnumerator"',
        '"GetObjectIds"',
        "ReadCatalogue",
        'return "Profile Label Set Style"',
        'return "Profile View Band Set Style"',
        'return "Assembly Style"',
    ],
)
require(
    SRC / "CeAssemblyCommands.cs",
    [
        "FindAssemblyId",
        'parameterName.Contains("description")',
        'document.SendStringToExecute("_TOOLPALETTES ',
        '"AssemblyStyle"',
    ],
)
require(
    SRC / "ProfileViewBandDataBinder.cs",
    [
        '"GetBottomBandItems"',
        '"GetTopBandItems"',
        'name.Contains("DATASOURCE")',
        "networkBand",
    ],
)

for file_name in (
    "RoadProductionCommentCommands.cs",
    "SewerProductionCommands.cs",
    "StormwaterProductionCommands.cs",
    "WaterProductionCommands.cs",
):
    require(
        SRC / file_name,
        ["ProfileViewBandDataBinder.Bind"],
    )

for file_name, command in (
    ("RoadProductionCommentCommands.cs", "CE_PROJECTSTYLES"),
    ("SewerProductionCommands.cs", "CE_SEWSETTINGS"),
    ("StormwaterProductionCommands.cs", "CE_SWSETTINGS"),
    ("WaterProductionCommands.cs", "CE_WATERSETTINGS"),
):
    require(
        SRC / file_name,
        [
            '"Choose production styles"',
            command,
            '"0 — Production setup"',
        ],
    )

print(
    "CE Tools production style, assembly creation and profile-band data "
    "repair validation passed."
)
