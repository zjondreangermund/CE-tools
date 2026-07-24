#!/usr/bin/env python3
"""Source-shape validation for CE Tools report presentation commands.

This check runs without Autodesk assemblies. It does not replace Civil 3D
compilation or manual testing; it catches missing commands, broken ribbon links
and accidental removal of the shared pop-up/table infrastructure.
"""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
REPORTS = ROOT / "src" / "CE.Tools.Civil3D" / "ReportPresentationCommands.cs"
PRESENTER = ROOT / "src" / "CE.Tools.Civil3D" / "GridReportPresenter.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

required_commands = [
    "CE_ALREPORTUI",
    "CE_PRREPORTUI",
    "CE_SFREPORTUI",
    "CE_CORREPORTUI",
    "CE_CORBASEUI",
    "CE_FLREPORTUI",
    "CE_PKREPORTUI",
]

errors: list[str] = []

for path in (REPORTS, PRESENTER, RIBBON):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

reports_text = REPORTS.read_text(encoding="utf-8")
presenter_text = PRESENTER.read_text(encoding="utf-8")
ribbon_text = RIBBON.read_text(encoding="utf-8")

for command in required_commands:
    if f'"{command}"' not in reports_text:
        errors.append(f"Report command is not declared: {command}")
    if f'"{command} "' not in ribbon_text:
        errors.append(f"Ribbon is not linked to report command: {command}")

for required_text in (
    "ShowReportAndOfferTable",
    "Place Table",
    "new Table",
    "DataGrid",
    "FrozenColumnCount",
):
    if required_text not in presenter_text:
        errors.append(f"Grid presenter is missing expected source marker: {required_text}")

for name, text in (
    ("ReportPresentationCommands.cs", reports_text),
    ("GridReportPresenter.cs", presenter_text),
    ("PluginEntry.cs", ribbon_text),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if "CE_CORREBUILD " not in ribbon_text:
    errors.append("Corridor rebuild ribbon command was removed or renamed")
if "CE_FLRAISE " not in ribbon_text:
    errors.append("Feature-line raise/lower ribbon command was removed or renamed")
if "CE_BMVERT " not in ribbon_text:
    errors.append("Bellmouth Densifier ribbon command was removed or renamed")
if "CE_TLENGTH " not in ribbon_text or "CE_TAREA " not in ribbon_text:
    errors.append("Working quantity commands were removed or renamed")

if errors:
    print("CE Tools report presentation validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools report presentation source validation passed.")
