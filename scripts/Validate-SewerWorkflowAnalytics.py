#!/usr/bin/env python3
"""Guard the automatic sewer-label and workflow analytics acceptance batch."""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def read(name: str) -> str:
    return (SRC / name).read_text(encoding="utf-8")


checks = {
    "SewerBranchLabelPlacement.cs": [
        "DefaultPaperHeight = 5.0",
        "label.ColorIndex = 3",
        "label.TextHeight = PaperAnnotationScale.ModelTextHeight",
        "OffsetFactor = 2.75",
        "aboveOffsetPaperDistance",
        "belowOffsetPaperDistance",
    ],
    "SewerNetworkLabelCommands.cs": [
        'CommandMethod("CE_SEWLABELS"',
        'FindCivilType("PipeLabel")',
        'FindCivilType("StructureLabel")',
        "ReadExistingLabelledParts",
    ],
    "SewerSequenceCommands.cs": [
        "SewerNetworkLabelCommands.EnsureLabels",
    ],
    "SewerProductionCommands.cs": [
        'model.AddChoice("PipePlanLabelStyle"',
        'model.AddChoice("StructurePlanLabelStyle"',
        'model.AddPositiveDouble("BranchLabelAboveOffset"',
        'model.AddPositiveDouble("BranchLabelBelowOffset"',
        'Value("BranchLabelAboveOffset", BranchLabelAboveOffset.ToString',
        'Value("BranchLabelBelowOffset", BranchLabelBelowOffset.ToString',
        'new DisciplineWorkflowAction("Create / refresh Civil labels"',
    ],
    "FloatingToolsWindow.cs": [
        'AddUsageTab("favorites", "⭐ Favorites")',
        'AddUsageTab("mostused", "🔥 Most Used")',
        'AddUsageTab("recent", "🕒 Recent")',
        "CommandUsageTracker.Projects(10)",
        "CommandUsageTracker.ClearProject(summary.Key)",
        '"Total project time {0}',
        "_projectSelector",
        "_usageTotals",
        "DispatcherTimer",
        "_activeStep.Matches(item.Definition)",
        'Step("Open Sewer workflow", "CE_SEWTOOLS"',
    ],
    "CommandUsageTracker.cs": [
        "document.CommandWillStart += OnCommandWillStart",
        "DocumentManager.DocumentActivated += OnDocumentActivated",
        "ProjectsByKey",
        '"UNSAVED|" + Guid.NewGuid()',
        "ProjectUsageSummary",
        "ClearProject(string projectKey)",
        "Legacy global rows are deliberately ignored",
        "EstimatedClicksSaved",
        "EstimatedSecondsSaved",
        "ToggleFavorite",
    ],
    "ProjectStyleCenterCommands.cs": [
        "CollectKnownStyleCollections",
        '"Alignment Label Set Style"',
        '"Structure Style"',
    ],
    "PopupTablePresenter.cs": [
        "requested = Math.Min(2.0, Math.Max(1.8, requested))",
    ],
}

errors = []
for filename, markers in checks.items():
    text = read(filename)
    for marker in markers:
        if marker not in text:
            errors.append(f"{filename} is missing: {marker}")

tracker = read("CommandUsageTracker.cs")
for obsolete in [
    "Favorites()",
    "MostUsed(int maximum)",
    "Recent(int maximum)",
    "Read(string command)",
    "ToggleFavorite(string command)",
]:
    if obsolete in tracker:
        errors.append(
            "CommandUsageTracker.cs still exposes global analytics: " + obsolete
        )

if errors:
    print("CE Tools sewer workflow analytics validation failed:")
    for error in errors:
        print(f"- {error}")
    raise SystemExit(1)

print("CE Tools sewer labels, branch presentation, workflow filters and usage analytics passed.")
