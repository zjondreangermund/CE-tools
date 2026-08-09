from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

def text(name):
    return (SRC / name).read_text(encoding="utf-8")

def require(condition, message):
    if not condition:
        raise SystemExit("FINAL COMMENTS VALIDATION FAILED: " + message)

final = text("FinalAllCommentsCompletionCommands.cs")
dialogs = text("DisciplineWorkflowDialogs.cs")
project = text("ProjectSetupCommands.cs")
coord = text("ProjectCoordinationCommands.cs")
vertex = text("VertexSettingOutCommands.cs")
exc = text("SewerExcavationCommentCommands.cs")
surface = text("SurfaceSpikeHoleRepairCommands.cs")
floating = text("FloatingToolsWindow.cs")
usage = text("CommandUsageTracker.cs")
ribbon = text("PluginEntry.cs")
assembly = text("CeAssemblyCommands.cs")

# Cross-DWG production settings.
require("CrossDrawingProductionSettingsStore.Load(model)" in dialogs, "shared settings are not loaded before DWG overrides")
require("CrossDrawingProductionSettingsStore.Save(model)" in dialogs, "shared settings are not saved")
require("ProductionSettings.ceps" in final, "user-local settings store missing")
require("NamibiaCoordinateSystemCatalog.PreferredLoName" in project, "Project Setup town does not suggest LO coordinate system")

# Coordinate/map tools.
for command in ["CE_COORDTRANSFORM", "CE_COORDTRANSFORMBULK"]:
    require(command in final, command + " missing")
require("TransformToLonLatAlt" in final and "TransformFromLonLatAlt" in final, "true GeoLocationData coordinate transforms missing")
require("Drawing X / Y -> WGS84 Lat / Long" in coord, "map popup DWG->WGS84 action missing")
require("WGS84 Lat / Long -> Drawing X / Y" in coord, "map popup WGS84->DWG action missing")
require("CoordinateWorkbookReader" in final and ".xlsx" in final, "Excel coordinate conversion missing")

# PDF/DWG, surface, survey helpers.
for command in ["CE_PDFTODWG", "CE_SURFACEDUPLICATE", "CE_POINTCIRCLE", "CE_GRIDSETTINGOUT", "CE_ANNOTATIONSCALESYNC"]:
    require(command in final, command + " missing")
require("-PDFIMPORT" in final and "SAVEAS" in final, "PDF to DWG does not use native import/save pipeline")

# Vertex table/levels.
require("DesignSurface" in vertex and "DesignSurfaceHandle" in vertex, "independent design/comparison surface link missing")
require("ResolveHandle(document.Database, link.DesignSurfaceHandle)" in vertex, "design surface is not dynamic on refresh")
require('"NG LEVEL", "DESIGN LEVEL", "DIFFERENCE"' in vertex, "NG/design/difference columns missing")
require('"POINT NAME", "TYPE", "SOURCE", "SEGMENT", xHeading, yHeading, "Z"' not in vertex, "obsolete Z table column remains")

# Excavation physical values and table presentation.
require("actual endpoint geometry first" in exc, "physical pipe-length priority missing")
require("NominalDiameterMm" in exc and "NOMINAL Ø mm" in exc, "nominal pipe diameter presentation missing")
require("CellAlignment.MiddleCenter" in exc, "excavation cells are not centered")
require("RecomputeTableBlock(true)" in exc, "excavation table does not force graphics refresh")

# Spike/low detection.
require("OrderBy(item => PlanDistanceSquared" in surface, "surface high/low nearest-neighbour fallback missing")

# Workflow/usage/UI.
require("InputManager.Current.PreProcessInput" in floating, "global Ctrl+F capture missing")
require("overallmostused" in floating and "MostUsedOverall" in usage, "overall most-used workflow missing")
for command in ["CE_BOOKTOOLS", "CE_DRAWINGBOOK", "CE_CLIENTBOOK"]:
    require(command in floating or command in final, command + " is not exposed through workflows")
require('"CE_BOOKTOOLS"' in floating, "book hub not included in every workflow")
require('"CE-" + definition.Text' in floating, "workflow card CE- prefix missing")

# Assembly and ribbon access.
require("Road assembly preset / use" in assembly, "road assembly preset choices missing")
for command in ["CE_PDFTODWG", "CE_COORDTRANSFORM", "CE_COORDTRANSFORMBULK"]:
    require(command in ribbon, command + " missing from ribbon")

print("Final all-comments completion validation passed.")
