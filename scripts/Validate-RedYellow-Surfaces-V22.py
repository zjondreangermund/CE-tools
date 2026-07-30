from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def require(path, *tokens):
    text = (ROOT / path).read_text(encoding="utf-8")
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{path}: missing {token!r}")


require(
    "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs",
    "CE_SURFACEELEVATION2",
    "LinkSurfaceElevation(",
    "Place a linked dynamic X/Y/Z table",
    "CE_SURFACECOMPARE2",
    "SurfaceComparisonLinkStore.LinkEntities(",
    "Place a linked dynamic surface-comparison table",
    "SurfaceComparisonLinkStore.CreateLinkedTable(",
)
require(
    "src/CE.Tools.Civil3D/SurfaceComparisonLinkStore.cs",
    'RecordName = "CE_SURFACE_COMPARISON_LINK"',
    "FindElevationAtXY",
    "DYNAMIC SURFACE COMPARISON",
    "BASE Z:",
    "COMPARISON Z:",
    "DIFF:",
    "RefreshAll(Document document)",
)
require(
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs",
    "SurfaceComparisonLinkStore.RefreshAll(document)",
    "LinkedRefreshEngine.Refresh(document, true)",
    "Rebuild design sources first",
)

print(
    "V22 surface continuation validated: selectable linked elevation, "
    "dynamic comparison annotations/tables, and rebuild-before-refresh."
)
