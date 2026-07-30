#!/usr/bin/env python3
"""Validate project-wide XREF splitting and revision-control source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "XrefProjectManagementCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2-XrefProject.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_XREFPROJECTTOOLS"',
    '"CE_XREFDISCIPLINESPLIT"',
    '"CE_XREFREVISIONDASH"',
    '"CE_XREFBACKUPALL"',
    '"CE_XREFRESTORE"',
    '"SURVEY", "ARCHITECTURE", "ROAD", "STORMWATER"',
    '"SEWER", "WATER", "LANDSCAPE", "OTHER"',
    'BuildSplitPlan(',
    'ClassifyDiscipline(',
    '.Wblock(',
    'database.AttachXref(',
    'Existing discipline files will not be overwritten',
    'Overwrite existing files", "Never"',
    'ReadFileAudit(',
    'SHA256.Create()',
    'ReadRevisions(',
    '"Same hash"',
    '"Different hash"',
    'CreateRevisionBackup(',
    '"pre-restore"',
    'File.Copy(record.SourcePath, preRestorePath, false)',
    'File.Copy(revisionPath, record.SourcePath, true)',
    'TryInvokeXrefMethod(document.Database, "UnloadXrefs", ids)',
    'TryInvokeXrefMethod(document.Database, "ReloadXrefs", ids)',
    'IsInsideFolder(revisionPath, revisionsFolder)',
    'The selected file must be an existing DWG inside the source Revisions folder.',
    'GridReportPresenter.ShowReportAndOfferTable(',
)
require(
    NORMALIZER,
    'Cmd("Project XREF Management Tools", "CE_XREFPROJECTTOOLS "',
    'Cmd("Split Project by XREF Discipline", "CE_XREFDISCIPLINESPLIT "',
    'Cmd("XREF Revision Dashboard", "CE_XREFREVISIONDASH "',
    'Cmd("Backup All XREF Sources", "CE_XREFBACKUPALL "',
    'Cmd("Restore XREF Revision", "CE_XREFRESTORE "',
)
require(
    RIBBON,
    'CE_XREFPROJECTTOOLS ',
    'CE_XREFDISCIPLINESPLIT ',
    'CE_XREFREVISIONDASH ',
    'CE_XREFBACKUPALL ',
    'CE_XREFRESTORE ',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in XrefProjectManagementCommands.cs")
if "Microsoft.Office.Interop" in text:
    raise SystemExit("Project XREF tools must not introduce Office COM automation")
if 'File.Copy(revisionPath, record.SourcePath, true)' in text and 'preRestorePath' not in text:
    raise SystemExit("XREF restore must create a pre-restore backup")

print("Project-wide XREF management validation passed.")
