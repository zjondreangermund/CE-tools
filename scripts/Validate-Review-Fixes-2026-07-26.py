#!/usr/bin/env python3
"""Validate the 26 July 2026 Civil 3D runtime-review correction batch."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SEQUENCE = ROOT / "src" / "CE.Tools.Civil3D" / "SewerSequenceCommands.cs"
ALIGNMENT = ROOT / "src" / "CE.Tools.Civil3D" / "SewerBranchAlignmentCommands.cs"
SURVEY = ROOT / "src" / "CE.Tools.Civil3D" / "SurveyCorrectionComparisonCommands.cs"
SURFACE = ROOT / "src" / "CE.Tools.Civil3D" / "SurfaceSpikeHoleRepairCommands.cs"
BLOCK = ROOT / "src" / "CE.Tools.Civil3D" / "FastBlockEditCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Review-Fixes-2026-07-26.ps1"
EXTENSION = ROOT / "scripts" / "Apply-Review-Fixes-2026-07-26-Extension.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SEQUENCE,
    "OrientBranchStartTowardConnection(candidateNodes, candidateEdges);",
    "Numbering must start at the terminal/first",
    "SewerBranchAlignmentCommands.RequestAutomaticRun(",
    "plans.SelectMany(plan =>",
    "path.StructureIds.Concat(path.PipeIds)",
)
sequence_text = SEQUENCE.read_text(encoding="utf-8")
if "OrientHighestEndpointFirst" in sequence_text:
    raise SystemExit("Sewer sequencing still contains elevation-based branch reversal")

require(
    ALIGNMENT,
    "private const double BranchLabelPaperHeight = 5.0",
    "private const double BranchLabelRepeatSpacing = 50.0",
    "private const int MaximumLabelsPerBranch = 200",
    "internal static void RequestAutomaticRun(",
    "document.SendStringToExecute(\"CE_SEWALIGN \", true, false, true);",
    "bool automaticRequest = ConsumeAutomaticRequest();",
    "if (!automaticRequest &&",
    "GetStructureSequence(",
    '@"^MH(?<sequence>\\d+)$"',
    "BuildLabelPlacements(branch.PlanPoints)",
    "label.Annotative = AnnotativeStates.True;",
    "label.TextHeight = BranchLabelPaperHeight;",
    "label.Rotation = placement.Rotation;",
    "Math.Ceiling(totalLength / BranchLabelRepeatSpacing)",
    "alignment direction=first manhole to connection point",
)
alignment_text = ALIGNMENT.read_text(encoding="utf-8")
for forbidden in (
    "GetTextHeight(database)",
    "alignment direction=high cover to low cover",
    "label.TextHeight = database.Textsize",
):
    if forbidden in alignment_text:
        raise SystemExit(f"Forbidden legacy sewer-label behaviour remains: {forbidden}")

require(
    SURVEY,
    '"CE_SURVEYCOMPARETOOLS"',
    '"CE_SURVEYCHANGES"',
    '"CE_SURVEYCHANGEEXPORT"',
    "private const int MaximumSamplePoints = 250000",
    "Select ORIGINAL survey surface",
    "Select CORRECTED survey surface",
    "FindElevationAtXY",
    "Original Z",
    "Corrected Z",
    "Delta Z",
    "GridReportPresenter.ShowReportAndOfferTable(",
    "SimpleXlsxWriter.Write(",
    "The comparison is read-only",
)
survey_text = SURVEY.read_text(encoding="utf-8")
if "UpgradeOpen" in survey_text or ".Erase(" in survey_text:
    raise SystemExit("Survey comparison must keep both source surfaces read-only")

require(
    SURFACE,
    '"CE_SURFSPIKEHOLEFIX"',
    "private const int MaximumVertices = 250000",
    "FindSpikeReplacements(",
    "AnalyseOpenEdges(triangles)",
    "InternalComponents",
    "HoleFillPoints",
    "CreateTinSurface(",
    "AddPoints(generated, plan.OutputPoints);",
    "Source remains unchanged",
    "The original surface will not be edited",
    "GridReportPresenter.ShowReportAndOfferTable(",
)
surface_text = SURFACE.read_text(encoding="utf-8")
if "source.UpgradeOpen" in surface_text or "source.Erase" in surface_text:
    raise SystemExit("Spike/hole repair must not edit or erase the source surface")

require(
    BLOCK,
    '"CE_BLOCKEDITFAST"',
    'document.SendStringToExecute("_.XOPEN ", true, false, true);',
    '"_.BEDIT " + QuoteCommandArgument(blockName)',
    "definition.IsFromExternalReference",
    "definition.IsFromOverlayReference",
    "REFEDIT is not used",
)
block_text = BLOCK.read_text(encoding="utf-8")
if 'SendStringToExecute("_.REFEDIT' in block_text:
    raise SystemExit("Fast block editing must never launch REFEDIT")

require(
    RIBBON,
    "AddReviewFixesPanel(tab);",
    "CE_TOOLS_CATEGORY_REVIEW_FIXES",
    "CE_SEWSEQ ",
    "CE_SEWALIGN ",
    "CE_SURVEYCOMPARETOOLS ",
    "CE_SURVEYCHANGES ",
    "CE_SURVEYCHANGEEXPORT ",
    "CE_SURFSPIKEHOLEFIX ",
    "CE_BLOCKEDITFAST ",
)

require(
    NORMALIZER,
    "orient every sewer branch from its first manhole toward its connection point",
    "automatically queue sewer alignments after whole-network sequencing",
    "label.Annotative = AnnotativeStates.True;",
    "label.TextHeight = BranchLabelPaperHeight;",
    "label.Rotation = placement.Rotation;",
    "BuildLabelPlacements(branch.PlanPoints)",
    "AddReviewFixesPanel(tab);",
)
require(
    EXTENSION,
    "support dotted whole-network and simple selected-path manhole sequences",
    "CE_SURFSPIKEHOLEFIX ",
)

for path in (SEQUENCE, ALIGNMENT, SURVEY, SURFACE, BLOCK, RIBBON):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

combined = "\n".join(
    path.read_text(encoding="utf-8") for path in (SURVEY, SURFACE, BLOCK)
)
if "Microsoft.Office.Interop" in combined:
    raise SystemExit("Review fixes must not introduce Office COM automation")

print("26 July 2026 runtime review-fix validation passed.")
