#!/usr/bin/env python3
"""Validate active batches from the 25 July 2026 comments register."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectSetupCommands.cs"
PROJECT_WINDOW = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectSetupPopupWindow.cs"
PARKING = ROOT / "src" / "CE.Tools.Civil3D" / "ParkingCommands.cs"
PARKING_WORKFLOW = ROOT / "src" / "CE.Tools.Civil3D" / "ClosedParkingBayWorkflow.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
FLOATING = ROOT / "src" / "CE.Tools.Civil3D" / "FloatingToolsWindow.cs"
BUILD = ROOT / "scripts" / "Build-CE-Tools.ps1"
NORMALIZER = ROOT / "scripts" / "Apply-Comments-2026-07-25.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(
                f"Missing {needle!r} in {path.relative_to(ROOT)}"
            )


require(
    PROJECT,
    "new ProjectSetupPopupWindow(",
    "AcApplication.ShowModalWindow(setupWindow);",
    "setupWindow.GetValue(field)",
    "PopupTablePresenter.ShowReportAndOfferTable(",
)
require(
    PROJECT_WINDOW,
    "internal sealed class ProjectSetupPopupWindow",
    'Content = "Review and Save"',
    "public string GetValue(string field)",
)
require(
    PARKING,
    "ClosedParkingBayWorkflow.CreateSingleRow(document);",
    "ClosedParkingBayWorkflow.CreateDoubleRow(document);",
)
require(
    PARKING_WORKFLOW,
    "internal static class ClosedParkingBayWorkflow",
    "private static void AppendClosedBay(",
    "bay.Closed = true;",
    "CE_PKCOUNTX",
    "CE_PKNUMBER2",
    "CE_PKREPORTUI",
)
require(
    RIBBON,
    "Title = PrefixRibbonText(title).ToUpperInvariant()",
    "Text = PrefixRibbonText(text)",
    "Text = PrefixRibbonText(definition.Text)",
    "private static string PrefixRibbonText(string text)",
    'Cmd("Floating Tools Window", "CE_TOOLSPALETTE "',
)
require(
    FLOATING,
    '"CE_TOOLSPALETTE"',
    "AcApplication.ShowModelessWindow(_window);",
    'item.Id == "CE_TOOLS_RIBBON_TAB"',
    "document.SendStringToExecute(",
    "RibbonVisuals.Small(definition.Command)",
)
require(
    BUILD,
    'Apply-Comments-2026-07-25.ps1',
    "Applying active 25 July 2026 comment corrections",
)
require(
    NORMALIZER,
    "replace separate project prompts with one project setup popup",
    "route single parking rows to closed bay polyline generation",
    "route double parking rows to closed bay polyline generation",
    "prefix CE Tools ribbon panel names",
    "prefix CE Tools ribbon menu names",
    "prefix CE Tools ribbon command names",
    "add the floating CE Tools launcher to Project Setup",
)

print("25 July 2026 active-comment batch validation passed.")
