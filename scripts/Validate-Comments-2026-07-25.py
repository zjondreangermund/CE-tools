#!/usr/bin/env python3
"""Validate active batches from the 25 July 2026 comments register."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectSetupCommands.cs"
PROJECT_WINDOW = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectSetupPopupWindow.cs"
PROJECT_STYLES = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectStyleCenterCommands.cs"
PARKING = ROOT / "src" / "CE.Tools.Civil3D" / "ParkingCommands.cs"
PARKING_WORKFLOW = ROOT / "src" / "CE.Tools.Civil3D" / "ClosedParkingBayWorkflow.cs"
ALIGNMENT = ROOT / "src" / "CE.Tools.Civil3D" / "AlignmentCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
FLOATING = ROOT / "src" / "CE.Tools.Civil3D" / "FloatingToolsWindow.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
DYNAMIC_COORDINATES = ROOT / "src" / "CE.Tools.Civil3D" / "DynamicCoordinateLinkStore.cs"
SURVEY = ROOT / "src" / "CE.Tools.Civil3D" / "SurveyCoordinateWorkflowCommands.cs"
BOQ = ROOT / "src" / "CE.Tools.Civil3D" / "BillOfQuantitiesCommands.cs"
BUILD = ROOT / "scripts" / "Build-CE-Tools.ps1"
NORMALIZER = ROOT / "scripts" / "Apply-Comments-2026-07-25.ps1"
WRAPPER = ROOT / "scripts" / "Invoke-Comments-Normalizer.ps1"


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
    PROJECT_STYLES,
    '"CE_PROJECTSTYLES"',
    '"CE_PROJECTSTYLEINFO"',
    '"CE_PROJECTSTYLECLEAR"',
    '"CE_UNDOSETTINGS"',
    '"CE_UNDO"',
    '"CE_REDO"',
    "internal sealed class ProjectStyleCenterWindow",
    'private const string RecordName = "PROJECT_STYLE_SELECTION"',
)
require(
    PARKING,
    "ClosedParkingBayWorkflow.CreateSingleRow(document);",
    "ClosedParkingBayWorkflow.CreateDoubleRow(document);",
)
require(
    PARKING_WORKFLOW,
    "internal static class ClosedParkingBayWorkflow",
    "private static ObjectId CreateBayBlockDefinition(",
    "private static void AppendBayBlock(",
    "new BlockReference(insertionPoint, definitionId)",
    "bay.Closed = true;",
    "CE_PKCOUNTX",
    "CE_PKNUMBER2",
    "CE_PKREPORTUI",
)
require(
    ALIGNMENT,
    'document.SendStringToExecute("CE_ALLABELX ", true, false, true);',
)
require(
    RIBBON,
    "Title = PrefixRibbonText(title).ToUpperInvariant()",
    "Text = PrefixRibbonText(text)",
    "Text = PrefixRibbonText(definition.Text)",
    "private static string PrefixRibbonText(string text)",
    'Cmd("Floating Tools Window", "CE_TOOLSPALETTE "',
    'Cmd("Project Style Centre", "CE_PROJECTSTYLES "',
    'Cmd("Presentation and Dynamic Tools", "CE_PRESENTATIONTOOLS "',
    'Cmd("Refresh All Dynamic Data", "CE_REFRESHALL "',
    "CommentAutoRefreshManager.Initialize();",
    "CommentAutoRefreshManager.Terminate();",
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
    PRESENTATION,
    '"CE_PRESENTATIONTOOLS"',
    '"CE_MAKEANNOTATIVE"',
    '"CE_TABLESCALE"',
    '"CE_OVERLAPFIX"',
    '"CE_PLREVERSE"',
    '"CE_REFRESHALL"',
    '"CE_REBUILDALL"',
    '"CE_AUTOREFRESH"',
    '"CE_REFRESHSTATUS"',
    '"CE_CLEANUPUI"',
    '"CE_HATCHUI"',
    "internal static class CommentAutoRefreshManager",
    "internal static class LinkedRefreshEngine",
)
require(
    DYNAMIC_COORDINATES,
    "internal static class DynamicCoordinateLinkStore",
    'private const string FollowerRecordName = "CE_DYNAMIC_COORDINATE_FOLLOWER"',
    'private const string PolylineVertexRecordName = "CE_DYNAMIC_POLYLINE_VERTEX"',
    "public static void LinkGeneratedObjects(",
    "public static void LinkPolylineVertices(",
    "public static int Refresh(Document document)",
    "POINT NAME:",
    "X-COORDINATE:",
    "Y-COORDINATE:",
    "Z-COORDINATE:",
)
require(
    SURVEY,
    "DynamicCoordinateLinkStore.LinkGeneratedObjects(",
    "DynamicCoordinateLinkStore.LinkPolylineVertices(",
    '"POINT NAME"',
    '"X-COORDINATE"',
    '"Y-COORDINATE"',
    '"Z-COORDINATE"',
    "const int columns = 4;",
    "Math.Max(textHeight * 0.25, 0.001)",
)
require(
    BOQ,
    "return TryGetEndpointDistance(databaseObject, out length);",
    "private static bool TryGetEndpointDistance(",
    '"StartPointLocation"',
    '"EndPointLocation"',
)
require(
    BUILD,
    'Invoke-Comments-Normalizer.ps1',
    'Apply-Comments-Discipline.ps1',
    "Applying active 25 July 2026 comment corrections",
)
require(
    WRAPPER,
    'Apply-Comments-2026-07-25.ps1',
    ".Apply-Comments-2026-07-25.tolerant.ps1",
    "Skipped comment change",
    "$literalCleanup",
    "$literalHatch",
)
require(
    NORMALIZER,
    "replace separate project prompts with one project setup popup",
    "route single parking rows to closed bay polyline generation",
    "route double parking rows to closed bay polyline generation",
    "route alignment labels to shared 1.8, 2.0 and 5.0 annotation settings",
    "prefix CE Tools ribbon panel names",
    "prefix CE Tools ribbon menu names",
    "prefix CE Tools ribbon command names",
    "add the floating CE Tools launcher to Project Setup",
    "add project styles and undo controls to the Project ribbon menu",
    "link coordinate annotations markers and crosses dynamically",
    "remove point-number column and use Point Name X Y Z coordinate columns",
    "read actual pipe and service lengths from endpoint properties",
)

print("25 July 2026 active-comment batch validation passed.")
