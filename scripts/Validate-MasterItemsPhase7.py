#!/usr/bin/env python3
"""Validate Phase 7 dependency-free project presentation source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "SimplePresentationPackage.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectPresentationCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.Presentation.Tests" / "Program.cs"
TEST_PROJECT = ROOT / "tests" / "CE.Tools.Presentation.Tests" / "CE.Tools.Presentation.Tests.csproj"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase7.ps1"
WRAPPER = ROOT / "scripts" / "Invoke-Master-Items-Phase7.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    CORE,
    'public static class SimplePresentationPackage',
    'public static void Write(string path, PresentationDeck deck)',
    'if (File.Exists(path)) throw new IOException',
    'new ZipArchive(stream, ZipArchiveMode.Create',
    '"[Content_Types].xml"',
    '"_rels/.rels"',
    '"ppt/presentation.xml"',
    '"ppt/slideMasters/slideMaster1.xml"',
    '"ppt/slideLayouts/slideLayout1.xml"',
    '"ppt/theme/theme1.xml"',
    '"ppt/slides/slide"',
    'screen16x9',
    'SlideWidth = 12192000',
    'SlideHeight = 6858000',
    'PresentationMetric',
    'Presentation slide count exceeds the 100-slide limit.',
    'File.Move(temporary, path)',
    'if (File.Exists(temporary)) File.Delete(temporary)',
)

require(
    CIVIL,
    '"CE_PROJECTPRESENTATIONTOOLS"',
    '"CE_PRESENTATIONPREVIEW"',
    '"CE_PRESENTATIONCREATE"',
    'SimplePresentationPackage.Write(path, deck)',
    'BuildDeck(input, snapshot)',
    'ReadSnapshot(document.Database)',
    'CivilApplication.ActiveDocument',
    'CoordinateSystemCode',
    'ReadLayers(',
    'ReadLayouts(',
    'ReadBlocksAndXrefs(',
    'ReadModelSpace(',
    'CountCivil(',
    'BuildFindings(database, snapshot)',
    '"Civil 3D Design Inventory"',
    '"Drawing Production"',
    '"Automated Model Health Review"',
    '"Recommended Next Actions"',
    '"Review Close-Out"',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'Existing presentation files are not overwritten.',
    'automated drawing/model observations',
    'does not replace drawing, design or engineering approval',
    'OpenMode.ForRead',
)

require(
    TEST_PROJECT,
    '<ProjectReference Include="..\\..\\src\\CE.Tools.Core\\CE.Tools.Core.csproj" />',
)
require(
    TESTS,
    'SimplePresentationPackage.Write(path, deck)',
    'ValidatePackage(path, deck.Slides.Count)',
    'ExistingOutputIsProtected(path, deck)',
    'InvalidDeckIsRejected(folder)',
    '"[Content_Types].xml"',
    '"ppt/presentation.xml"',
    '"ppt/slideMasters/slideMaster1.xml"',
    '"ppt/slideLayouts/slideLayout1.xml"',
    'XDocument.Load(stream)',
    'Project Overview title missing',
    'Model Health title missing',
)

require(
    NORMALIZER,
    'Cmd("Project Presentation Tools", "CE_PROJECTPRESENTATIONTOOLS "',
    'Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW "',
    'Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE "',
)
require(
    WRAPPER,
    'corrected the Phase 7 WriteAllText call for PowerShell compatibility',
    '& $scriptPath',
)
require(
    RIBBON,
    'CE_PROJECTPRESENTATIONTOOLS ',
    'CE_PRESENTATIONPREVIEW ',
    'CE_PRESENTATIONCREATE ',
)

for path in (CORE, CIVIL, TESTS):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

combined = CORE.read_text(encoding="utf-8") + "\n" + CIVIL.read_text(encoding="utf-8")
if "Microsoft.Office.Interop" in combined:
    raise SystemExit("Presentation generation must not use Office COM automation")
if "PowerPoint.Application" in combined:
    raise SystemExit("Presentation generation must remain dependency-free")
if "FileMode.CreateNew" not in CORE.read_text(encoding="utf-8"):
    raise SystemExit("Presentation writer must protect output creation")
if "entity.UpgradeOpen" in CIVIL.read_text(encoding="utf-8"):
    raise SystemExit("Presentation snapshot must remain read-only")

print("Master Items Phase 7 automatic project presentation validation passed.")
