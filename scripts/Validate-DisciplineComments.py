#!/usr/bin/env python3
"""Validate feature-line, profile, surface and network active-comment source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "FeatureProfileSurfaceCommentCommands.cs"
NETWORK = ROOT / "src" / "CE.Tools.Civil3D" / "NetworkCommentCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Comments-Discipline.ps1"
BUILD = ROOT / "scripts" / "Build-CE-Tools.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_FLREPORT2"',
    '"CE_FLAPPEARANCE"',
    '"CE_FLVERTEXLABELS"',
    '"CE_PROFILEREPORT2"',
    '"CE_PROFILEELEVATION2"',
    '"CE_SURFACEREPORT2"',
    '"CE_SURFACEELEVATION2"',
    '"CE_SURFACECOMPARE2"',
    "GridReportPresenter.ShowReportAndOfferTable(",
    "AnnotationSettingsStore.Prepare(",
    "AnnotationWriter.Create(",
    "FeatureLinePointType.AllPoints",
    "MoveToSite",
    "MoveToNoneSite",
)
require(
    NETWORK,
    '"CE_NETWORKREPORT2"',
    '"CE_NETWORKPARTREPORT2"',
    '"CE_SERVICEPROFILES"',
    '"CE_NETWORKDATA"',
    '"GetPipeNetworkIds"',
    '"GetPressurePipeNetworkIds"',
    '"CE_SWPROFILE "',
    '"CE_SEWPROFILE "',
    '"CE_WATERPROFILE "',
    '"CE_WATERPLACE "',
    "GridReportPresenter.ShowReportAndOfferTable(",
)
require(
    NORMALIZER,
    'Cmd("Detailed Feature Line Popup Report", "CE_FLREPORT2 "',
    'Cmd("Feature Line Colour and Site", "CE_FLAPPEARANCE "',
    'Cmd("Annotate Every Feature Line Vertex", "CE_FLVERTEXLABELS "',
    'Cmd("All Profiles Popup Report", "CE_PROFILEREPORT2 "',
    'Cmd("Profile Elevation Popup and Annotation", "CE_PROFILEELEVATION2 "',
    'Cmd("All Surfaces Popup Report", "CE_SURFACEREPORT2 "',
    'Cmd("Surface Elevation Popup and Annotation", "CE_SURFACEELEVATION2 "',
    'Cmd("Surface Comparison Popup and Annotation", "CE_SURFACECOMPARE2 "',
    'Cmd("All Network Summary Popup", "CE_NETWORKREPORT2 "',
    'Cmd("Selected Network Part Data", "CE_NETWORKPARTREPORT2 "',
    'Cmd("Service Alignment and Profile Production Window", "CE_SERVICEPROFILES "',
    'Cmd("Network Data and Refresh Window", "CE_NETWORKDATA "',
)
require(
    BUILD,
    'Apply-Comments-Discipline.ps1',
    'Applying feature-line, profile and surface comment corrections',
)

# The normalizer runs before validation, therefore the generated ribbon source
# must include the new commands when this script is executed in CI.
require(
    RIBBON,
    'CE_FLREPORT2',
    'CE_FLAPPEARANCE',
    'CE_FLVERTEXLABELS',
    'CE_PROFILEREPORT2',
    'CE_PROFILEELEVATION2',
    'CE_SURFACEREPORT2',
    'CE_SURFACEELEVATION2',
    'CE_SURFACECOMPARE2',
    'CE_NETWORKREPORT2',
    'CE_NETWORKPARTREPORT2',
    'CE_SERVICEPROFILES',
    'CE_NETWORKDATA',
)

print("Feature-line, profile, surface and network active-comment validation passed.")
