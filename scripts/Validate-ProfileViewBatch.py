#!/usr/bin/env python3
"""Validate batch profile-view cleanup and style source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "ProfileViewBatchCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2-ProfileViews.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_PROFILEVIEWBATCHTOOLS"',
    '"CE_PROFILEVIEWBATCH"',
    '"CE_PROFILEVIEWFITALL"',
    '"CE_PROFILEVIEWBATCHINFO"',
    'ReadObjectIds(alignment, "GetProfileViewIds")',
    '"StationRangeMode"',
    '"ElevationRangeMode"',
    '"AutomaticStationRange"',
    '"AutomaticElevationRange"',
    '"BandSetStyleId"',
    '"ProfileViewBandSetStyleId"',
    '"ImportBandSetStyle"',
    '"ApplyBandSetStyle"',
    '"SetBandSetStyle"',
    'TryInvokeNoArguments(view, "Rebuild")',
    'TryInvokeNoArguments(view, "Update")',
    'Unsupported API operations',
    'document.SendStringToExecute("CE_OVERLAPFIX "',
    'internal sealed class ProfileViewBatchWindow',
)
require(
    NORMALIZER,
    '"CE_PROFILEVIEWBATCHTOOLS "',
    '"CE_PROFILEVIEWBATCH "',
    '"CE_PROFILEVIEWFITALL "',
    '"CE_PROFILEVIEWBATCHINFO "',
)
# The extended Phase 2 profile workflow applies the normalizer before validation.
require(
    RIBBON,
    'CE_PROFILEVIEWBATCHTOOLS ',
    'CE_PROFILEVIEWBATCH ',
    'CE_PROFILEVIEWFITALL ',
    'CE_PROFILEVIEWBATCHINFO ',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in ProfileViewBatchCommands.cs")

print("Batch profile-view cleanup validation passed.")
