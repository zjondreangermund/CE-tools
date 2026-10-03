#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

split = (SRC / "August13RoadProfileSectionCommands.cs").read_text(encoding="utf-8-sig")
viewport = (SRC / "October03ViewportPlotProfileCommands.cs").read_text(encoding="utf-8-sig")
phase_one = (SRC / "PhaseOneUtilityCommands.cs").read_text(encoding="utf-8-sig")
feature = (SRC / "August21CrossDisciplineFatalSafety.cs").read_text(encoding="utf-8-sig")
dialogs = (SRC / "DisciplineWorkflowDialogs.cs").read_text(encoding="utf-8-sig")
plugin = (SRC / "PluginEntry.cs").read_text(encoding="utf-8-sig")
menu = (SRC / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8-sig")
fatal_finalizer = (ROOT / "scripts" / "Repair-August21-CrossDisciplineFatalSafety-Civil3D2023.ps1").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    '"CE_ROADPROFILEVIEWSPLIT"',
    'settings.AddText(',
    '"Start station"',
    'Zero is valid.',
    'ProductionSettingsDialogModel.TryDouble(',
    'start={1:0.###}',
]:
    if marker not in split:
        errors.append("Split road profile zero-chainage marker missing: " + marker)

if 'AddPositiveDouble(\n                "Start"' in split:
    errors.append("Split road profile start station regressed to PositiveDouble; 0+000 would be rejected.")

for marker in [
    '"CE_VIEWPORTREGENALL"',
    '"CE_PROFILEVIEWPORTFIT"',
    '"All layouts in drawing"',
    '"Selected profile views"',
    '"Smart fit - reuse smaller gaps"',
    '"Split across consecutive viewports"',
    '"Horizontal clearance"',
    '"Vertical clearance"',
    'BranchNumber(',
    'OrderBy(item => item.Branch)',
    'SortViewportSlots(',
    'FitsAtVerticalScale(',
    'CoreModelWidthAtVerticalScale(',
    'viewport.Locked = lockAfter',
    'document.Editor.Command("_.REGENALL")',
]:
    if marker not in viewport:
        errors.append("Viewport/profile production marker missing: " + marker)

for marker in [
    '"CE_VIEWPORTLOCKALL"',
    '"CE_VIEWPORTUNLOCKALL"',
    'October03ViewportPlotProfileCommands.SetViewportLock(locked)',
    'SetViewportLock(true)',
    'SetViewportLock(false)',
    '"CE_PROFILEVIEWPORTFIT"',
    '"CE_VIEWPORTREGENALL"',
]:
    if marker not in phase_one:
        errors.append("Phase 1 viewport command marker missing: " + marker)

for marker in [
    'October03PlotPdfManager',
    '"DefaultPlotToFilePath"',
    'DrawingFolder(document)',
    'Directory.GetFiles(',
    '"*.pdf"',
    'Process.Start(',
    'UseShellExecute = true',
    'document.CommandWillStart +=',
    'document.CommandEnded +=',
]:
    if marker not in viewport:
        errors.append("Plot/PDF convenience marker missing: " + marker)

for marker in [
    '"Layer", "02 Output", "Layer"',
    '"Color", "02 Output", "Colour (ACI)"',
    '"Site", "02 Output", "Civil 3D Site"',
    '"<Source layer>"',
    '"<Siteless>"',
    'ResolveOutputLayer(',
    'ParseAciColor(',
    'TryResolveOrCreateSite(',
    'siteId.IsNull',
    'temporaryId,\n                            siteId)',
    'featureLine.ColorIndex =',
]:
    if marker not in feature:
        errors.append("Fatal-safe feature-line output marker missing: " + marker)

if 'August21SafeFeatureLineCreation.RunCreateFromObjects(document);' not in fatal_finalizer:
    errors.append("Fatal-safety finalizer no longer delegates CE_FLCREATE to the updated safe helper.")

for marker in [
    'CrossDrawingProductionSettingsStore.Save(model)',
    'ProductionSettingsPersistenceStore.Save(document.Database, model)',
]:
    if marker not in dialogs:
        errors.append("Popup saved-default persistence marker missing: " + marker)

for marker in [
    'October03PlotPdfManager.Initialize();',
    'October03PlotPdfManager.Terminate();',
]:
    if marker not in plugin:
        errors.append("Plot/PDF manager lifecycle marker missing: " + marker)

for marker in [
    '"CE_PROFILEVIEWPORTFIT"',
    '"CE_VIEWPORTLOCKALL"',
    '"CE_VIEWPORTUNLOCKALL"',
    '"CE_VIEWPORTREGENALL"',
]:
    if marker not in menu:
        errors.append("Field Completion menu marker missing: " + marker)

if errors:
    print("October 3 profile/viewport/plot validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 3 profile/viewport/plot validation passed.")
