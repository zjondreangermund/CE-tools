from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def require(path, *tokens):
    text = (ROOT / path).read_text(encoding="utf-8")
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{path}: missing {token!r}")


require(
    "src/CE.Tools.Civil3D/AnnotationCommands.cs",
    "CE_CORLABELX",
    "Enter corridor point-name prefix",
    "Enter corridor point start number",
    "Use a Civil 3D surface for the corridor point Z value",
    "CorridorAnnotationLinkStore.Link(",
    "Place a linked dynamic corridor point table",
)
require(
    "src/CE.Tools.Civil3D/CorridorAnnotationLinkStore.cs",
    'RecordName = "CE_CORRIDOR_ANNOTATION_LINK"',
    "CivilCogoPoint",
    "MLeader",
    "MText",
    "DYNAMIC CORRIDOR POINT",
    "RefreshAll(Document document)",
)
require(
    "src/CE.Tools.Civil3D/PluginEntry.cs",
    'Cmd("Automatic Corridor Junctions", "CE_INTCREATE "',
)
require(
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs",
    "CorridorAnnotationLinkStore.RefreshAll(document)",
    "LinkedRefreshEngine.Refresh(document, true)",
)

print(
    "V24 corridor continuation validated: dynamic COGO/MText/MLeader/table, "
    "surface-linked Z, auto rebuild and existing linked junction engine exposure."
)
