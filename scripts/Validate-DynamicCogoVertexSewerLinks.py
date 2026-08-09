#!/usr/bin/env python3
"""Validate dynamic COGO styles, vertex anchors and sewer topology links."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
PLUGIN = CIVIL / "PluginEntry.cs"
PROJECT = CIVIL / "ProjectStyleCenterCommands.cs"
VERTEX = CIVIL / "VertexSettingOutCommands.cs"
GEOMETRY = CIVIL / "VertexSettingOutGeometry.cs"
SURVEY = CIVIL / "SurveyCoordinateWorkflowCommands.cs"
REFRESH = CIVIL / "CommentPresentationCommands.cs"
COGO = CIVIL / "CogoPointProjectStyleCommands.cs"
SEWER = CIVIL / "SewerNetworkDynamicSequenceManager.cs"
WORKFLOW = ROOT / ".github" / "workflows" / "core-tests.yml"

paths = (PLUGIN, PROJECT, VERTEX, GEOMETRY, SURVEY, REFRESH, COGO, SEWER, WORKFLOW)
errors: list[str] = []
texts: dict[Path, str] = {}
for path in paths:
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")
        texts[path] = ""
    else:
        texts[path] = path.read_text(encoding="utf-8-sig")

required = {
    PLUGIN: (
        "CogoPointProjectStyleManager.Initialize();",
        "SewerNetworkDynamicSequenceManager.Initialize();",
        '"CE_COGOPOINTSYNC "',
        '"CE_COGOOVERLAPFIX "',
        '"CE_SEWAUTOSEQ "',
        '"CE_SEWAUTOSEQALL "',
        '"CE_SEWAUTOSEQSETTINGS "',
    ),
    PROJECT: (
        "CogoPointProjectStyleManager.Queue();",
        "CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);",
    ),
    GEOMETRY: (
        "public Vector3d? AnnotationOffset { get; set; }",
    ),
    VERTEX: (
        'private const string SchemaVersion = "2";',
        '"Polyline vertices only"',
        '"Select Civil 3D surface"',
        '"Select feature line"',
        "ApplyElevationReference(",
        "FindElevationAtXY",
        "GetClosestPointTo",
        "CaptureCurrentAnnotationOffset(",
        "TryReadOutputAnchor(",
        "GEN=",
        "ELEVHANDLE=",
        "SRC=",
        "CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);",
        "RemoveDuplicateClosingVertices",
        "leader.ArrowSymbolId = ObjectId.Null;",
        "ObjectId arrow = ObjectId.Null;",
        "SetClosedFilledDimensionArrow",
        "table.Columns[1].Width",
        "CellAlignment.MiddleCenter",
    ),
    SURVEY: (
        "CE_COORDPOLY2 now uses the shared dynamic vertex setting-out popup",
        '"CE_VERTEXSETTINGOUT "',
        'requestedPoint = "RSA_Circle";',
        'requestedLabel = "Description Only";',
        '"Point Style"',
        '"Point Label Style"',
        "leader.ArrowSymbolId = ObjectId.Null;",
    ),
    REFRESH: (
        "CogoPointProjectStyleCommands.ApplySelectedStyles(",
        "VertexSettingOutCommands.RefreshAll(document);",
    ),
    COGO: (
        '"CE_COGOPOINTSYNC"',
        '"CE_COGOOVERLAPFIX"',
        '"RSA_Circle"',
        '"Description Only"',
        "point.StyleId = pointStyleId;",
        "point.LabelStyleId = labelStyleId;",
        "point.LabelLocation = anchor + stored;",
        "CE_COGO_LABEL_OFFSET",
        "ObjectModified += OnObjectChanged",
        "ObjectAppended += OnObjectChanged",
    ),
    SEWER: (
        '"CE_SEWAUTOSEQ"',
        '"CE_SEWAUTOSEQALL"',
        '"CE_SEWAUTOSEQSETTINGS"',
        "BuildTopology(",
        "BuildBranches(",
        '"Branch-" +',
        '"P" +',
        '"MH" +',
        "edge.IsBranchOne",
        "ObjectErased += OnObjectErased",
        "SewerNetworkLabelCommands.EnsureLabels(",
        "SewerLabelStyleSyncCommands.ApplySelectedStyles(document);",
        "RefreshGeneratedAlignments(",
        "LinkedRefreshEngine.Refresh(document, false);",
    ),
    WORKFLOW: (
        "Validate-DynamicCogoVertexSewerLinks.py",
    ),
}

for path, markers in required.items():
    text = texts[path]
    for marker in markers:
        if marker not in text:
            errors.append(f"{path.name} is missing marker: {marker}")

for path in (PLUGIN, PROJECT, VERTEX, GEOMETRY, SURVEY, REFRESH, COGO, SEWER):
    text = texts[path]
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {path.name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses in {path.name}")

if ".Database;" in texts[SEWER] and "pipeId.Database" in texts[SEWER]:
    errors.append("Sewer dynamic manager still accesses ObjectId.Database")

if errors:
    print("Dynamic COGO / vertex / sewer validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Dynamic COGO/vertex/sewer validation passed: saved point styles, bulk point-style sync, "
    "closed-polyline deduplication, point-safe overlap, shared vertex popup, linked Z references, "
    "closed-filled leaders/dimensions, table spacing and automatic sewer branch compaction are wired."
)
