from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

def text(name):
    return (SRC / name).read_text(encoding="utf-8")

def require(condition, message):
    if not condition:
        raise SystemExit("FINAL COMMENTS VALIDATION FAILED: " + message)

final = text("FinalAllCommentsCompletionCommands.cs")
gaps = text("FinalWorkflowGapCommands.cs")
runtime = text("FinalRuntimeCompletionCommands.cs")
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
cogo = text("CogoPointProjectStyleCommands.cs")
universal = text("UniversalDynamicRefreshCommands.cs")
junction = text("RoadJunctionCompletionCommands.cs")
sewer = text("SewerProductionCommands.cs")
cost = text("WaterSewerCostEstimateCommands.cs")

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
require("CoordinateWorkbookReader" in final and ".xlsx" in final and ".xlsm" in final, "Excel/XLSM coordinate conversion missing")

# PDF/DWG, surface, survey helpers.
for command in ["CE_PDFTODWG", "CE_SURFACEDUPLICATE", "CE_POINTCIRCLE", "CE_GRIDSETTINGOUT", "CE_ANNOTATIONSCALESYNC"]:
    require(command in final, command + " missing")
require("-PDFIMPORT" in final and "SAVEAS" in final, "PDF to DWG does not use native import/save pipeline")

# Feature-line and network creation completion.
for command in ["CE_FLBREAKLINE", "CE_NETWORKFROMPOLYLINES", "CE_NETWORKCONNECT", "CE_NETWORKCREATEHUB", "CE_NETWORKCONNECTALL"]:
    require(command in gaps or command in runtime, command + " missing")
for command in ["CE_FLDYNAMICREPORT", "CE_FLREPORTREFRESH"]:
    require(command in runtime, command + " missing")
require("FinalFeatureLineReportCommands.RefreshAll(document)" in universal, "dynamic feature-line report is not in universal refresh")

# Vertex table/levels/dynamics/performance.
require("DesignSurface" in vertex and "DesignSurfaceHandle" in vertex, "independent design/comparison surface link missing")
require("ResolveHandle(document.Database, link.DesignSurfaceHandle)" in vertex, "design surface is not dynamic on refresh")
require('"NG LEVEL", "DESIGN LEVEL", "DIFFERENCE"' in vertex, "NG/design/difference columns missing")
require('"POINT NAME", "TYPE", "SOURCE", "SEGMENT", xHeading, yHeading, "Z"' not in vertex, "obsolete Z table column remains")
require("RemoveDuplicateClosingVertices" in vertex, "closed-polyline duplicate start/end COGO suppression missing")
require("ApplySelectedStyles(document, false)" in vertex, "bulk COGO style synchronization missing")
require("AttachmentPoint.BottomLeft" in vertex, "MLeader text is not anchored above the leader")

# Excavation physical values and table presentation.
require("actual endpoint geometry first" in exc, "physical pipe-length priority missing")
require("NominalDiameterMm" in exc and "NOMINAL Ø mm" in exc, "nominal pipe diameter presentation missing")
require("CellAlignment.MiddleCenter" in exc, "excavation cells are not centered")
require("RecomputeTableBlock(true)" in exc, "excavation table does not force graphics refresh")

# Spike/low detection.
require("OrderBy(item => PlanDistanceSquared" in surface, "surface high/low nearest-neighbour fallback missing")

# COGO overlap/reference-point behavior.
require("Select COGO points" in cogo, "selective COGO overlap scope missing")
require("restrictedPointIds" in cogo, "COGO overlap solver cannot restrict moved labels")
require("ModelDistance(database, 6.0)" in cogo, "COGO labels are not bounded close enough to their reference points")

# Sewer sequence production options.
for command in ["CE_SEWSEQWORKFLOW", "CE_SEWSEQMAINWORKFLOW", "CE_SEWPOSTSEQUENCE"]:
    require(command in runtime, command + " missing")
require("CE_SEWSEQWORKFLOW" in sewer and "CE_SEWSEQMAINWORKFLOW" in sewer, "sewer workflow does not route sequence commands through production options")
require("Branch-name layer" in runtime and "Freeze generated sewer alignment layers" in runtime, "sewer branch-layer/freeze options missing")

# Junction production and setting-out.
require("sx * (width + radius)" in junction and "sy * (width + radius)" in junction, "road bellmouths are not generated on inside corner offsets")
require("CE_JUNCTIONSETTINGOUT" in runtime, "single/all junction multi-source setting-out handoff missing")

# Workflow/usage/UI.
require("InputManager.Current.PreProcessInput" in floating, "global Ctrl+F capture missing")
require("CE_MOSTUSEDOVERALL" in floating, "overall most-used shortcut missing")
require("overallmostused" in floating and "MostUsedOverall" in usage, "overall most-used workflow missing")
for command in ["CE_BOOKTOOLS", "CE_DRAWINGBOOK", "CE_CLIENTBOOK"]:
    require(command in floating or command in final, command + " is not exposed through workflows")
require('"CE_BOOKTOOLS"' in floating, "book hub not included in every workflow")
require('"CE-" + definition.Text' in floating, "workflow card CE- prefix missing")
require('"CE-" + title.ToUpperInvariant()' in ribbon, "ribbon panel CE- prefix missing")

# Cost-estimate approved-template handoff.
for command in ["CE_COSTTEMPLATESELECT", "CE_COSTTEMPLATEINFO"]:
    require(command in gaps, command + " missing")
require("CostEstimateTemplateStore.Read()" in cost, "selected approved cost-estimate template is not preferred")
require('".xlsm"' in cost, "macro-enabled XLSM output preservation missing")

# Assembly and ribbon access.
require("Road assembly preset / use" in assembly, "road assembly preset choices missing")
for command in ["CE_PDFTODWG", "CE_COORDTRANSFORM", "CE_COORDTRANSFORMBULK", "CE_FLBREAKLINE", "CE_NETWORKCREATEHUB", "CE_COSTTEMPLATESELECT"]:
    require(command in ribbon, command + " missing from ribbon")

print("Final all-comments completion validation passed.")
