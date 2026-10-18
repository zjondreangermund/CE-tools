"""Regression guard for October 13 automatic long-road plan/profile sheeting."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
errors = []

civil = (SRC / "CE.Tools.Civil3D" / "RoadAutoSheetingCommands.cs").read_text(encoding="utf-8-sig")
core = (SRC / "CE.Tools.Core" / "RoadSheetingPlanner.cs").read_text(encoding="utf-8-sig")
menu = (SRC / "CE.Tools.Civil3D" / "August13RoadProductionCentres.cs").read_text(encoding="utf-8-sig")
tests = (ROOT / "tests" / "CE.Tools.Core.Tests" / "RoadSheetingTests.cs").read_text(encoding="utf-8-sig")

for marker in [
    'CE_ROADAUTOSHEETS',
    'Automatic Road Plan / Profile Sheets',
    'RoadSheetingPlanner.DefaultSegmentLength',
    'RoadSheetingPlanner.Create',
    'A0',
    'A1',
    'SegmentsPerSheet',
    'TemplateLayout',
    'TryCloneLayout',
    'BuildPaperPairs',
    'ReadCurveAwarePlanRanges',
    'BuildPlanSlots',
    'manager.CurrentLayout = layoutName',
    'document.Editor.SwitchToPaperSpace()',
    'sheet refresh failed at {1}',
    'ViewportSizing',
    'PlanViewportWidth',
    'PlanViewportHeight',
    'ProfileViewportWidth',
    'ProfileViewportHeight',
    'PlanScaleMode',
    'PlanScaleDenominator',
    'ProfileScaleMode',
    'ProfileScaleDenominator',
    'AddNorthArrow',
    'NorthArrowSource',
    'AddNorthArrow(',
    'arrow.Rotation = NormalizeTwist(-viewportTwist)',
    'CurveHeadingTolerance',
    'MinimumPlanSubrange',
    'PlanSubViewportGap',
    'FitPlanViewport',
    'FitProfileViewport',
    'NormalizeTwist(-angle)',
    'viewport.TwistAngle = twist',
    'viewport.Locked = lockAfter',
    'CE-SHEET:',
]:
    if marker not in civil:
        errors.append("road auto-sheeting runtime marker missing: " + marker)

for marker in [
    'return 2000.0;',
    'return 1000.0;',
    'segmentsPerSheet',
    'RoadSheetSegment',
    'RoadSheetPlan',
    'SplitPlanByHeading',
    'SmallestAxisAngleDifference',
    'StationText',
]:
    if marker not in core:
        errors.append("road auto-sheeting core marker missing: " + marker)

if '"CE_ROADAUTOSHEETS"' not in menu:
    errors.append("Road Design Production does not expose CE_ROADAUTOSHEETS")

for marker in [
    'RoadSheetingPlanner.Create(0.0, 8000.0, 2000.0, 2)',
    'Equal(2, a0.Count)',
    'Equal(4, a1.Count)',
    'Equal(560.0, curved[0].EndStation)',
    'Equal(560.0, curved[1].StartStation)',
]:
    if marker not in tests:
        errors.append("8 km A0/A1 sheeting test missing: " + marker)

if errors:
    print("October 13 road auto-sheeting regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 13 road auto-sheeting regression passed.")
