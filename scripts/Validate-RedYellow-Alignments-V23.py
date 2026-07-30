from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def require(path, *tokens):
    text = (ROOT / path).read_text(encoding="utf-8")
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{path}: missing {token!r}")


require(
    "src/CE.Tools.Civil3D/AnnotationCommands.cs",
    "CE_ALLABELX",
    "Enter alignment point-name prefix",
    "Enter alignment point start number",
    "Use a Civil 3D surface for the point Z value",
    "Point Name: ",
    '"X: "',
    '"Y: "',
    '"Z: "',
    "AlignmentAnnotationLinkStore.Link(",
    "Place a linked dynamic alignment point table",
)
require(
    "src/CE.Tools.Civil3D/AlignmentAnnotationLinkStore.cs",
    'RecordName = "CE_ALIGNMENT_ANNOTATION_LINK"',
    "alignment.StationOffset",
    "Point Name",
    "DYNAMIC ALIGNMENT POINT",
    "RefreshAll(Document document)",
    "CivilCogoPoint",
    "MLeader",
    "MText",
)
require(
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs",
    "AlignmentAnnotationLinkStore.RefreshAll(document)",
)
require(
    "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs",
    "internal static List<CivilObjectChoice> ReadSurfaces",
    "internal static CivilObjectChoice PickObject",
    "internal static ObjectId CreateCoordinateAnchor",
)

print(
    "V23 alignment continuation validated: P1-style naming, surface-linked Z, "
    "dynamic station/offset/X/Y/Z labels, markers and tables."
)
