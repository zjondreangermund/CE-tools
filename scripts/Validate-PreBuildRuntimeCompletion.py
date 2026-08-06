#!/usr/bin/env python3
"""Validate the complete final screenshot comment batch before a Civil 3D build."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def source(name: str) -> str:
    path = SRC / name
    if not path.exists():
        raise SystemExit(f"Missing required source file: {path}")
    return path.read_text(encoding="utf-8")


def require(text: str, needle: str, label: str) -> None:
    if needle not in text:
        raise SystemExit(f"Missing {label}: {needle}")


def forbid(text: str, needle: str, label: str) -> None:
    if needle in text:
        raise SystemExit(f"Forbidden {label} remains: {needle}")


runtime = source("PreBuildRuntimeCompletionCommands.cs")
curve = source("CurveConversionCommands.cs")
vertex = source("VertexSettingOutCommands.cs")
preset = source("ProjectStylePresetManager.cs")
cogo = source("CogoPointProjectStyleCommands.cs")
presentation = source("CommentPresentationCommands.cs")
sewer_labels = source("SewerLabelStyleSyncCommands.cs")
sewer_dynamic = source("SewerNetworkDynamicSequenceManager.cs")
universal = source("UniversalDynamicRefreshCommands.cs")
sewer_production = source("SewerProductionCommands.cs")
ribbon = source("PluginEntry.cs")

# Runtime centre and permanent command surface.
for command in (
    "CE_RUNTIMEFINISH",
    "CE_ANNOTATIONLINKREPAIR",
    "CE_PIPELABELPRESENTATION",
    "CE_PROFILEBANDSBATCH",
    "CE_PROFILEBANDREFRESH",
):
    require(runtime, command, f"runtime command {command}")

# Curve conversion is explicit and visibly segmented.
for token in (
    '"ArcVertices"',
    '"CircleVertices"',
    "minimumArcVertices",
    "minimumCircleVertices",
    'new[] { "Keep originals", "Replace originals" }',
    "output.Color = source.Color",
):
    require(curve, token, "curve conversion control")
forbid(
    curve,
    '(string.Equals(mode, "Auto-detect selected objects", StringComparison.OrdinalIgnoreCase) && (source is Polyline || source is Polyline2d))',
    "automatic 2D polyline to 3D conversion",
)

# Vertex popup display options never alter true XY, and links persist them.
for token in (
    '"CoordinateOrder"',
    '"XSign"',
    '"YSign"',
    '"ORDER="',
    '"XSIGN="',
    '"YSIGN="',
    "DisplayX(record.Point, link)",
    "DisplayY(record.Point, link)",
    "SetClosedFilledDimensionArrow",
    "RuntimeAnnotationLinkManager.ClampLinkedAnnotations",
):
    require(vertex, token, "vertex setting-out completion")
require(
    vertex,
    "record.Point.X,\n                                record.Point.Y,\n                                elevation",
    "Z-only reference update that preserves XY",
)

# Saved style prompt must wait for quiescence and lock every drawing write.
require(preset, "using (DocumentLock documentLock = document.LockDocument())", "project style document lock")
require(preset, 'GetSystemVariable("CMDNAMES")', "project style command-state guard")
require(preset, 'GetSystemVariable("CMDACTIVE")', "project style active-command guard")
require(preset, "PromptedDocuments.Remove(document);", "failed style apply requeue")

# Point labels remain visible and close to their true COGO anchor.
for token in (
    "TrySetLabelVisible(point)",
    "NormalizeOffset(stored, database)",
    "ModelDistance(database, 15.0)",
    "UniversalDynamicRefreshManager.Queue();",
):
    require(cogo, token, "COGO runtime repair")
forbid(cogo, "for (int ring = 1; ring <= 10; ring++)", "unbounded COGO overlap rings")

# Old independent automatic refresh loops must delegate to one central cycle.
require(presentation, "if (UniversalDynamicRefreshManager.Enabled)", "presentation refresh delegation")
require(presentation, "RuntimeAnnotationLinkManager.ClampLinkedAnnotations", "linked-anchor overlap repair")
require(cogo, "watcher only forwards the request", "COGO refresh delegation")
require(sewer_dynamic, "if (UniversalDynamicRefreshManager.Enabled)", "sewer refresh delegation")

# Sewer sequencing safely compacts branch names without duplicate-name errors.
for token in (
    "CE_TMP_PIPE_",
    "CE_TMP_MH_",
    "Guid.NewGuid().ToString(\"N\")",
):
    require(sewer_dynamic, token, "collision-safe sewer resequencing")

# Selected Civil label styles remain authoritative; no hard-coded label content.
require(
    sewer_labels,
    "SewerPlanLabelRuntimeManager.ConfigureLabel(label, transaction);",
    "selected Civil label style presentation",
)
forbid(
    sewer_labels,
    '" m\\\\P@ "',
    "hard-coded three-line pipe text override",
)
for token in (
    "Length and Slope",
    "Flow Direction",
    "PlanReadability",
    "ShowAnchorMarker",
    "maximumOffset",
):
    require(runtime, token, "pipe and structure label presentation")

# Profile bands import/apply/link multiple styles, profiles and network sources.
for token in (
    '"Primary"',
    '"Secondary"',
    '"Select multiple profile views"',
    "ImportBundledStyles",
    "Profile1Id",
    "Profile2Id",
    "PipeNetworkId",
    "AddToProfileView",
):
    require(runtime, token, "profile band batch/data linkage")
require(
    sewer_production,
    "ProfileViewBandRuntimeManager.RefreshAll(document);",
    "automatic profile band refresh after sewer profile production",
)

# The universal cycle is quiescent and covers all final linked outputs.
for token in (
    "RuntimeAnnotationLinkManager.ClampLinkedAnnotations",
    "SewerPlanLabelRuntimeManager.Apply",
    "ProfileViewBandRuntimeManager.RefreshAll",
    'GetSystemVariable("CMDACTIVE")',
):
    require(universal, token, "universal runtime refresh")

# Ribbon access is direct and logically grouped.
for command in (
    "CE_ANNOTATIONLINKREPAIR",
    "CE_RUNTIMEFINISH",
    "CE_PIPELABELPRESENTATION",
    "CE_PROFILEBANDSBATCH",
    "CE_PROFILEBANDREFRESH",
):
    require(ribbon, command, f"ribbon command {command}")

# Command uniqueness is intentionally delegated to the repository's dedicated
# Validate-CommandRegistry.py, which correctly understands both CommandMethod
# overloads and command groups.
print(
    "Pre-build runtime completion validation passed: curve conversion, bounded dynamic annotations, "
    "saved-style locking, COGO visibility, collision-safe sewer resequencing, plan-readable Civil labels, "
    "multi-profile band data linking and ribbon access are present."
)
