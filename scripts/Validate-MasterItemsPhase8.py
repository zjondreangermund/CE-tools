#!/usr/bin/env python3
"""Validate Master Items Phase 8 engineering asset catalog and Civil 3D manager."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "EngineeringAssetCatalog.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "EngineeringAssetLibraryCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.AssetCatalog.Tests" / "Program.cs"
TEST_PROJECT = ROOT / "tests" / "CE.Tools.AssetCatalog.Tests" / "CE.Tools.AssetCatalog.Tests.csproj"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase8.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    CORE,
    "public enum EngineeringAssetApprovalStatus",
    "Draft,",
    "ForReview,",
    "Reviewed,",
    "Approved,",
    "Superseded",
    "public const int MaximumAssets = 10000",
    "AssetId,Title,Category,Discipline,AssetType,RelativePath,Revision,ApprovalStatus",
    "UnitsPerMetre,Tags,Description,Sha256,IsActive",
    "Typical Details",
    "Furniture 2D",
    "Furniture 3D",
    "Specifications",
    "The catalog already exists and will not be overwritten",
    "The asset catalog exceeds the 10,000-record safety limit",
    "Duplicate AssetId",
    "Multiple catalog records reference the same source file",
    "Superseded asset is marked active",
    "Source file is missing",
    "Catalog SHA-256 is blank",
    "Source SHA-256 differs from the catalog value",
    "SHA256.Create()",
    "FileShare.ReadWrite | FileShare.Delete",
    "public static IList<EngineeringAssetRecord> Search(",
    "statuses.Contains(item.ApprovalStatus)",
    "activeOnly || item.IsActive",
    "public static string SanitizeBlockName",
)

require(
    CIVIL,
    '"CE_ASSETLIBTOOLS"',
    '"CE_ASSETLIBSETTINGS"',
    '"CE_ASSETCATALOGTEMPLATE"',
    '"CE_ASSETCATALOGAUDIT"',
    '"CE_ASSETSEARCH"',
    '"CE_ASSETINSERT"',
    '"CE_ASSETINFO"',
    '"CE_ASSETREVISIONCHECK"',
    'private const string RegAppName = "CE_ENGINEERING_ASSET"',
    "private const int MaximumDisplayedResults = 100",
    "EngineeringAssetCatalog.CreateTemplate(path)",
    "EngineeringAssetCatalog.Audit(catalogPath)",
    "EngineeringAssetCatalog.Search(",
    "EngineeringAssetCatalog.CalculateSha256(sourcePath)",
    "Controlled drawing insertion currently supports DWG assets only",
    "Superseded or inactive assets cannot be inserted",
    "Asset status is ",
    "The source checksum is blank or differs from the controlled catalog value",
    "drawingUnitsPerMetre / asset.UnitsPerMetre",
    "sourceDatabase.ReadDwgFile(",
    "FileOpenMode.OpenForReadAndAllShare",
    "database.Insert(blockName, sourceDatabase, false)",
    "new BlockReference(insertionPoint, blockId)",
    "ScaleFactors = new Scale3d(scale)",
    "DxfCode.ExtendedDataRegAppName",
    'TextValue("AssetId", asset.AssetId)',
    'TextValue("Revision", asset.Revision)',
    'TextValue("ApprovalStatus", asset.ApprovalStatus.ToString())',
    'TextValue("Catalog", Path.GetFullPath(catalogPath))',
    'TextValue("Source", Path.GetFullPath(sourcePath))',
    'TextValue("Sha256", sourceHash)',
    "Checks source/catalog identity and recorded revision/status",
    "without automatic replacement",
    "Source files are opened read-only and are never saved or overwritten",
    "SimpleXlsxWriter.Write(",
    "GridReportPresenter.ShowReportAndOfferTable(",
)

require(
    TESTS,
    "TemplateIsNonOverwriting(root);",
    "RelativePathAndChecksumResolve(root);",
    "AuditDetectsDuplicateAndChangedAssets(root);",
    "SearchHonoursApprovalAndTerms(root);",
    "Throws<IOException>(() => EngineeringAssetCatalog.CreateTemplate(path));",
    "Equal(0, audit.ErrorCount);",
    "True(audit.Findings.Any(item => item.Area == \"Checksum\"));",
    "Equal(\"KERB-001\", approvedOnly[0].AssetId);",
    "Equal(\"VALVE-001\", water[0].AssetId);",
)

require(
    TEST_PROJECT,
    "<TargetFramework>net8.0</TargetFramework>",
    "CE.Tools.Core.csproj",
)

require(
    NORMALIZER,
    "CE_TOOLS_ENGINEERING_ASSET_MENU",
    'Cmd("Engineering Asset Library Tools", "CE_ASSETLIBTOOLS "',
    'Cmd("Engineering Asset Library Settings", "CE_ASSETLIBSETTINGS "',
    'Cmd("Create Engineering Asset Catalog", "CE_ASSETCATALOGTEMPLATE "',
    'Cmd("Audit Engineering Asset Catalog", "CE_ASSETCATALOGAUDIT "',
    'Cmd("Search Engineering Asset Library", "CE_ASSETSEARCH "',
    'Cmd("Insert Controlled DWG Asset", "CE_ASSETINSERT "',
    'Cmd("Inserted Asset Information", "CE_ASSETINFO "',
    'Cmd("Check Inserted Asset Revisions", "CE_ASSETREVISIONCHECK "',
)

require(
    RIBBON,
    "CE_ASSETLIBTOOLS ",
    "CE_ASSETLIBSETTINGS ",
    "CE_ASSETCATALOGTEMPLATE ",
    "CE_ASSETCATALOGAUDIT ",
    "CE_ASSETSEARCH ",
    "CE_ASSETINSERT ",
    "CE_ASSETINFO ",
    "CE_ASSETREVISIONCHECK ",
)

for path in (CORE, CIVIL, TESTS):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

civil_text = CIVIL.read_text(encoding="utf-8")
for forbidden in (
    "sourceDatabase.SaveAs",
    "sourceDatabase.Save",
    "File.WriteAllBytes(sourcePath",
    "File.WriteAllText(sourcePath",
    "File.Move(sourcePath",
    "File.Delete(sourcePath",
    "Microsoft.Office.Interop",
):
    if forbidden in civil_text:
        raise SystemExit(f"Phase 8 source protection violation: {forbidden}")

if "entity.Erase();" in civil_text:
    raise SystemExit("Phase 8 must not provide automatic deletion/replacement of inserted assets")

print("Master Items Phase 8 engineering asset library validation passed.")
