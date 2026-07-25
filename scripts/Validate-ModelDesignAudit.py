#!/usr/bin/env python3
"""Validate the comprehensive Civil 3D model-audit source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "ModelDesignAuditCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2-ModelReport.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_MODELREPORTTOOLS"',
    '"CE_MODELREPORT"',
    '"CE_MODELREPORTINFO"',
    '"CE_MODELREPORTEXPORT"',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'SimpleXlsxWriter.Write(',
    'CivilApplication.ActiveDocument',
    '"CoordinateSystemCode"',
    'ReadModelSpace(',
    'ReadLayers(',
    'ReadXrefs(',
    'ReadLayouts(',
    'ScanExtensionDictionary(',
    'ScanResultBuffer(',
    '"Handle=", "Source=", "Boundary=", "Generated=", "Anchor="',
    'snapshot.CeStaleHandleCount',
    'snapshot.StaleCivilReferenceCount',
    'snapshot.ProxyEntityCount',
    'snapshot.UnreadableEntityCount',
    '"Surface reports zero triangles."',
    '"Corridor is reported as out of date."',
    '"No Civil 3D coordinate-system code was detected."',
    '"No controlled plotter configuration was detected."',
    '"No active paper-space viewport was detected."',
    '"Automated checks do not replace engineering or drawing-office review."',
    'ModelAuditFinding.Error(',
    'ModelAuditFinding.Warning(',
    'ModelAuditFinding.Review(',
    'ModelAuditFinding.Ok(',
)
require(
    NORMALIZER,
    'Cmd("Civil 3D Model Audit Tools", "CE_MODELREPORTTOOLS "',
    'Cmd("Civil 3D Design Model Audit", "CE_MODELREPORT "',
    'Cmd("Civil 3D Model Health Summary", "CE_MODELREPORTINFO "',
    'Cmd("Export Civil 3D Model Audit", "CE_MODELREPORTEXPORT "',
)
require(
    RIBBON,
    'CE_MODELREPORTTOOLS ',
    'CE_MODELREPORT ',
    'CE_MODELREPORTINFO ',
    'CE_MODELREPORTEXPORT ',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in ModelDesignAuditCommands.cs")
if "Microsoft.Office.Interop" in text:
    raise SystemExit("Model audit must not introduce Office COM automation")
if "Erase(" in text or "UpgradeOpen(" in text:
    raise SystemExit("The model audit must remain read-only")

print("Comprehensive Civil 3D design-model audit validation passed.")
