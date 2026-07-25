using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.EngineeringAssetLibraryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Controlled standards, typical-detail, symbol and civil/furniture asset catalog.
    /// Catalog approval states and checksums are user/office records; CE Tools does not
    /// independently verify engineering approval or professional authority.
    /// Source files are opened read-only and are never saved or overwritten.
    /// </summary>
    public sealed class EngineeringAssetLibraryCommands
    {
        private const string RootDictionaryName = "CE_TOOLS_ASSET_LIBRARY";
        private const string SettingsRecordName = "SETTINGS";
        private const string RegAppName = "CE_ENGINEERING_ASSET";
        private const int MaximumDisplayedResults = 100;

        [CommandMethod("CE_TOOLS", "CE_ASSETLIBTOOLS", CommandFlags.Modal)]
        public void AssetLibraryTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nEngineering asset library [Settings/Template/Audit/Search/Insert/Information/Revisions] <Search>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Settings", "Template", "Audit", "Search", "Insert", "Information", "Revisions"
            }) options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Search";
            string command = Equal(choice, "Settings") ? "CE_ASSETLIBSETTINGS " :
                Equal(choice, "Template") ? "CE_ASSETCATALOGTEMPLATE " :
                Equal(choice, "Audit") ? "CE_ASSETCATALOGAUDIT " :
                Equal(choice, "Insert") ? "CE_ASSETINSERT " :
                Equal(choice, "Information") ? "CE_ASSETINFO " :
                Equal(choice, "Revisions") ? "CE_ASSETREVISIONCHECK " :
                "CE_ASSETSEARCH ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETLIBSETTINGS", CommandFlags.Modal)]
        public void AssetLibrarySettings()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AssetLibrarySettings current = ReadSettings(document.Database);
            string catalogPath;
            if (!PromptCatalogPath(document.Editor, current.CatalogPath, true, out catalogPath)) return;
            double drawingUnitsPerMetre;
            if (!PromptPositiveDouble(
                    document.Editor,
                    "Drawing units per metre",
                    current.DrawingUnitsPerMetre > 0.0 ? current.DrawingUnitsPerMetre : 1.0,
                    out drawingUnitsPerMetre)) return;
            EngineeringAssetApprovalStatus visibility;
            if (!PromptVisibility(document.Editor, current.MinimumVisibility, out visibility)) return;

            var settings = new AssetLibrarySettings(catalogPath, drawingUnitsPerMetre, visibility);
            WriteSettings(document.Database, settings);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Engineering Asset Library Settings",
                "Catalog search and insertion use these drawing-specific settings. Approval is recorded metadata and still requires office governance.",
                new List<IList<string>>
                {
                    new List<string> { "PROPERTY", "VALUE" },
                    new List<string> { "Catalog", catalogPath },
                    new List<string> { "Drawing units per metre", Format(drawingUnitsPerMetre) },
                    new List<string> { "Default visibility", VisibilityLabel(visibility) },
                    new List<string> { "Source access", "Read-only; source assets are never saved or overwritten" }
                },
                "CE TOOLS ASSET LIBRARY SETTINGS");
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETCATALOGTEMPLATE", CommandFlags.Modal)]
        public void CreateCatalogTemplate()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptSaveFileOptions("\nChoose the new engineering asset catalog path: ")
            {
                Filter = "CE Tools Asset Catalog (*.csv)|*.csv",
                DialogCaption = "Create CE Tools Engineering Asset Catalog",
                InitialFileName = "CE-Tools-Engineering-Asset-Catalog.csv"
            };
            PromptFileNameResult result = document.Editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return;
            string path = EnsureExtension(result.StringResult, ".csv");
            try
            {
                EngineeringAssetCatalog.CreateTemplate(path);
                AssetLibrarySettings settings = ReadSettings(document.Database);
                WriteSettings(document.Database, new AssetLibrarySettings(
                    path,
                    settings.DrawingUnitsPerMetre > 0.0 ? settings.DrawingUnitsPerMetre : 1.0,
                    settings.MinimumVisibility));
                document.Editor.WriteMessage(
                    "\nCE_ASSETCATALOGTEMPLATE complete. Catalog and library folders created: {0}",
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ASSETCATALOGTEMPLATE stopped. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETCATALOGAUDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AuditCatalog()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string catalogPath;
            if (!ResolveCatalog(document, out catalogPath)) return;
            try
            {
                EngineeringAssetAuditResult audit = EngineeringAssetCatalog.Audit(catalogPath);
                var rows = new List<IList<string>>
                {
                    new List<string> { "SEVERITY", "ASSET ID", "AREA", "FINDING", "ACTION" }
                };
                rows.AddRange(audit.Findings.Select(item => (IList<string>)new List<string>
                {
                    item.Severity.ToString(), item.AssetId, item.Area, item.Finding, item.Action
                }));
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Engineering Asset Catalog Audit",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Assets={0}; errors={1}; warnings={2}; review={3}. File integrity does not certify engineering content.",
                        audit.Records.Count, audit.ErrorCount, audit.WarningCount, audit.ReviewCount),
                    rows,
                    "CE TOOLS ENGINEERING ASSET CATALOG AUDIT");

                if (PromptYesNo(document.Editor, "Export the catalog audit to Excel", true))
                {
                    string exportPath;
                    if (PromptExcelPath(document.Editor, "CE-Tools-Asset-Catalog-Audit.xlsx", out exportPath))
                    {
                        SimpleXlsxWriter.Write(exportPath, "Asset Audit", rows);
                        document.Editor.WriteMessage("\nCE_ASSETCATALOGAUDIT workbook created: {0}", exportPath);
                    }
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ASSETCATALOGAUDIT failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETSEARCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SearchCatalog()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string catalogPath;
            if (!ResolveCatalog(document, out catalogPath)) return;
            AssetLibrarySettings settings = ReadSettings(document.Database);
            string query;
            if (!PromptText(document.Editor, "Search text, tags or AssetId", string.Empty, true, out query)) return;
            string category;
            if (!PromptText(document.Editor, "Category filter <all>", string.Empty, true, out category)) return;
            string discipline;
            if (!PromptText(document.Editor, "Discipline filter <all>", string.Empty, true, out discipline)) return;
            EngineeringAssetApprovalStatus visibility;
            if (!PromptVisibility(document.Editor, settings.MinimumVisibility, out visibility)) return;

            try
            {
                IList<EngineeringAssetRecord> results = Search(
                    catalogPath, query, category, discipline, visibility);
                var rows = BuildSearchRows(results.Take(MaximumDisplayedResults));
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Engineering Asset Search",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Matches={0}; showing={1}; visibility={2}. Only active assets are listed.",
                        results.Count, Math.Min(results.Count, MaximumDisplayedResults), VisibilityLabel(visibility)),
                    rows,
                    "CE TOOLS ENGINEERING ASSET SEARCH");
                document.Editor.WriteMessage(
                    "\nCE_ASSETSEARCH complete. Matches={0}; displayed={1}.",
                    results.Count,
                    Math.Min(results.Count, MaximumDisplayedResults));
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ASSETSEARCH failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETINSERT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void InsertAsset()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string catalogPath;
            if (!ResolveCatalog(document, out catalogPath)) return;
            AssetLibrarySettings settings = ReadSettings(document.Database);
            EngineeringAssetRecord asset;
            if (!PromptAsset(document, catalogPath, settings.MinimumVisibility, true, out asset)) return;

            string sourcePath = asset.ResolvePath(catalogPath);
            if (!string.Equals(Path.GetExtension(sourcePath), ".dwg", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage(
                    "\nCE_ASSETINSERT stopped. Controlled drawing insertion currently supports DWG assets only. Selected type={0}.",
                    asset.AssetType);
                return;
            }
            if (!File.Exists(sourcePath))
            {
                document.Editor.WriteMessage("\nCE_ASSETINSERT stopped. Source file is missing: {0}", sourcePath);
                return;
            }
            if (asset.ApprovalStatus == EngineeringAssetApprovalStatus.Superseded || !asset.IsActive)
            {
                document.Editor.WriteMessage("\nCE_ASSETINSERT stopped. Superseded or inactive assets cannot be inserted.");
                return;
            }
            if (asset.ApprovalStatus != EngineeringAssetApprovalStatus.Approved &&
                !PromptYesNo(
                    document.Editor,
                    "Asset status is " + asset.ApprovalStatus + ". Insert for internal review only",
                    false)) return;

            string actualHash;
            try { actualHash = EngineeringAssetCatalog.CalculateSha256(sourcePath); }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ASSETINSERT stopped. SHA-256 could not be calculated. {0}", exception.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(asset.Sha256) ||
                !string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage(
                    "\nCE_ASSETINSERT stopped. The source checksum is blank or differs from the controlled catalog value. Audit and review the revision first.");
                return;
            }

            PromptPointResult pointResult = document.Editor.GetPoint("\nSelect asset insertion point: ");
            if (pointResult.Status != PromptStatus.OK) return;
            double rotationDegrees;
            if (!PromptDouble(document.Editor, "Rotation in degrees", 0.0, true, out rotationDegrees)) return;
            double drawingUnitsPerMetre = settings.DrawingUnitsPerMetre > 0.0
                ? settings.DrawingUnitsPerMetre
                : 1.0;
            double scale = drawingUnitsPerMetre / asset.UnitsPerMetre;
            if (scale <= 0.0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                document.Editor.WriteMessage("\nCE_ASSETINSERT stopped. The calculated units scale is invalid.");
                return;
            }

            var reviewRows = new List<IList<string>>
            {
                new List<string> { "PROPERTY", "VALUE" },
                new List<string> { "Asset", asset.AssetId + " - " + asset.Title },
                new List<string> { "Revision", asset.Revision },
                new List<string> { "Status", asset.ApprovalStatus.ToString() },
                new List<string> { "Approved by", asset.ApprovedBy },
                new List<string> { "Source", sourcePath },
                new List<string> { "SHA-256", actualHash },
                new List<string> { "Scale", Format(scale) },
                new List<string> { "Rotation", Format(rotationDegrees) + "°" },
                new List<string> { "Source protection", "Read-only; no source save/overwrite" }
            };
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Engineering Asset Insertion Review",
                "Review the asset, revision, approval record, units and source identity before insertion.",
                reviewRows,
                "CE TOOLS ENGINEERING ASSET INSERTION REVIEW");
            if (!PromptYesNo(document.Editor, "Insert this controlled asset", false)) return;

            try
            {
                ObjectId referenceId = InsertDwgAsset(
                    document,
                    asset,
                    catalogPath,
                    sourcePath,
                    actualHash,
                    pointResult.Value,
                    rotationDegrees * Math.PI / 180.0,
                    scale);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_ASSETINSERT complete. Asset={0}; revision={1}; reference={2}.",
                    asset.AssetId,
                    asset.Revision,
                    referenceId.Handle.ToString());
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ASSETINSERT failed. No source file was modified. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AssetInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AssetInsertionTag tag;
            ObjectId objectId;
            if (!PromptInsertedAsset(document, out objectId, out tag)) return;
            var rows = new List<IList<string>>
            {
                new List<string> { "PROPERTY", "VALUE" },
                new List<string> { "Reference handle", objectId.Handle.ToString() },
                new List<string> { "AssetId", tag.AssetId },
                new List<string> { "Title", tag.Title },
                new List<string> { "Revision", tag.Revision },
                new List<string> { "Approval status at insertion", tag.ApprovalStatus },
                new List<string> { "Catalog", tag.CatalogPath },
                new List<string> { "Source", tag.SourcePath },
                new List<string> { "Inserted source SHA-256", tag.SourceSha256 },
                new List<string> { "Inserted UTC", tag.InsertedUtc },
                new List<string> { "Units scale", tag.Scale },
                new List<string> { "Current source state", CurrentSourceState(tag.SourcePath, tag.SourceSha256) }
            };
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Inserted Engineering Asset Information",
                "This is traceability metadata. It does not prove the source remained approved after insertion.",
                rows,
                "CE TOOLS INSERTED ENGINEERING ASSET INFORMATION");
        }

        [CommandMethod("CE_TOOLS", "CE_ASSETREVISIONCHECK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CheckInsertedAssetRevisions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<InsertedAssetReview> reviews = ReadInsertedAssetReviews(document.Database);
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "HANDLE", "ASSET ID", "INSERTED REV", "CATALOG REV", "CATALOG STATUS",
                    "SOURCE STATE", "CATALOG STATE", "ACTION"
                }
            };
            foreach (InsertedAssetReview review in reviews)
            {
                rows.Add(new List<string>
                {
                    review.Handle, review.Tag.AssetId, review.Tag.Revision, review.CatalogRevision,
                    review.CatalogStatus, review.SourceState, review.CatalogState, review.Action
                });
            }
            if (reviews.Count == 0)
            {
                rows.Add(new List<string>
                {
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                    "No tagged inserted assets", string.Empty, "Insert controlled assets before revision checking."
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Inserted Asset Revision Check",
                "Checks source/catalog identity and recorded revision/status. It does not update or replace inserted geometry automatically.",
                rows,
                "CE TOOLS INSERTED ASSET REVISION CHECK");
            if (reviews.Count > 0 && PromptYesNo(document.Editor, "Export the revision check to Excel", true))
            {
                string exportPath;
                if (PromptExcelPath(document.Editor, "CE-Tools-Inserted-Asset-Revision-Check.xlsx", out exportPath))
                {
                    SimpleXlsxWriter.Write(exportPath, "Asset Revisions", rows);
                    document.Editor.WriteMessage("\nCE_ASSETREVISIONCHECK workbook created: {0}", exportPath);
                }
            }
        }

        private static ObjectId InsertDwgAsset(
            Document document,
            EngineeringAssetRecord asset,
            string catalogPath,
            string sourcePath,
            string sourceHash,
            Point3d insertionPoint,
            double rotation,
            double scale)
        {
            Database database = document.Database;
            string blockName = "CE_ASSET_" + EngineeringAssetCatalog.SanitizeBlockName(
                asset.AssetId + "_" + asset.Revision + "_" + sourceHash.Substring(0, 8));
            ObjectId blockId = ObjectId.Null;
            using (DocumentLock documentLock = document.LockDocument())
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (blockTable != null && blockTable.Has(blockName)) blockId = blockTable[blockName];
                    transaction.Commit();
                }

                if (blockId.IsNull)
                {
                    using (var sourceDatabase = new Database(false, true))
                    {
                        sourceDatabase.ReadDwgFile(
                            sourcePath,
                            FileOpenMode.OpenForReadAndAllShare,
                            false,
                            null);
                        sourceDatabase.CloseInput(true);
                        blockId = database.Insert(blockName, sourceDatabase, false);
                    }
                }

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(database, transaction);
                    BlockTableRecord currentSpace = transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (currentSpace == null) throw new InvalidOperationException("The current drawing space is unavailable.");
                    var reference = new BlockReference(insertionPoint, blockId)
                    {
                        Rotation = rotation,
                        ScaleFactors = new Scale3d(scale)
                    };
                    WriteTag(reference, asset, catalogPath, sourcePath, sourceHash, scale);
                    ObjectId referenceId = currentSpace.AppendEntity(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                    transaction.Commit();
                    return referenceId;
                }
            }
        }

        private static IList<EngineeringAssetRecord> Search(
            string catalogPath,
            string query,
            string category,
            string discipline,
            EngineeringAssetApprovalStatus visibility)
        {
            return EngineeringAssetCatalog.Search(
                catalogPath,
                query,
                category,
                discipline,
                VisibleStatuses(visibility),
                true);
        }

        private static IEnumerable<EngineeringAssetApprovalStatus> VisibleStatuses(
            EngineeringAssetApprovalStatus visibility)
        {
            if (visibility == EngineeringAssetApprovalStatus.Approved)
                return new[] { EngineeringAssetApprovalStatus.Approved };
            if (visibility == EngineeringAssetApprovalStatus.Reviewed)
                return new[]
                {
                    EngineeringAssetApprovalStatus.Approved,
                    EngineeringAssetApprovalStatus.Reviewed
                };
            return new[]
            {
                EngineeringAssetApprovalStatus.Approved,
                EngineeringAssetApprovalStatus.Reviewed,
                EngineeringAssetApprovalStatus.ForReview,
                EngineeringAssetApprovalStatus.Draft
            };
        }

        private static List<IList<string>> BuildSearchRows(IEnumerable<EngineeringAssetRecord> assets)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "ASSET ID", "TITLE", "CATEGORY", "DISCIPLINE", "TYPE", "REV",
                    "STATUS", "APPROVED BY", "TAGS", "SOURCE"
                }
            };
            rows.AddRange(assets.Select(asset => (IList<string>)new List<string>
            {
                asset.AssetId, asset.Title, asset.Category, asset.Discipline, asset.AssetType,
                asset.Revision, asset.ApprovalStatus.ToString(), asset.ApprovedBy, asset.Tags,
                asset.RelativePath
            }));
            return rows;
        }

        private static bool PromptAsset(
            Document document,
            string catalogPath,
            EngineeringAssetApprovalStatus defaultVisibility,
            bool dwgOnly,
            out EngineeringAssetRecord asset)
        {
            asset = null;
            string query;
            if (!PromptText(document.Editor, "Search asset title, tags or AssetId", string.Empty, false, out query)) return false;
            EngineeringAssetApprovalStatus visibility;
            if (!PromptVisibility(document.Editor, defaultVisibility, out visibility)) return false;
            IList<EngineeringAssetRecord> results = Search(
                catalogPath,
                query,
                string.Empty,
                string.Empty,
                visibility)
                .Where(item => !dwgOnly || string.Equals(item.AssetType, "DWG", StringComparison.OrdinalIgnoreCase))
                .Take(MaximumDisplayedResults)
                .ToList();
            if (results.Count == 0)
            {
                document.Editor.WriteMessage("\nNo matching active assets were found.");
                return false;
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Select Engineering Asset",
                "Enter the exact AssetId after reviewing the filtered results.",
                BuildSearchRows(results),
                "CE TOOLS SELECT ENGINEERING ASSET");
            string assetId;
            if (!PromptText(document.Editor, "Exact AssetId to select", results[0].AssetId, false, out assetId)) return false;
            List<EngineeringAssetRecord> matches = results
                .Where(item => string.Equals(item.AssetId, assetId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ApprovalStatus == EngineeringAssetApprovalStatus.Approved)
                .ThenByDescending(item => item.Revision, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (matches.Count == 0)
            {
                document.Editor.WriteMessage("\nThe entered AssetId is not in the displayed result set.");
                return false;
            }
            asset = matches[0];
            return true;
        }

        private static bool ResolveCatalog(Document document, out string catalogPath)
        {
            AssetLibrarySettings settings = ReadSettings(document.Database);
            catalogPath = settings.CatalogPath;
            if (!string.IsNullOrWhiteSpace(catalogPath) && File.Exists(catalogPath)) return true;
            document.Editor.WriteMessage(
                "\nThe configured engineering asset catalog is missing. Run CE_ASSETLIBSETTINGS or CE_ASSETCATALOGTEMPLATE.");
            return false;
        }

        private static bool PromptCatalogPath(
            Editor editor,
            string currentPath,
            bool mustExist,
            out string path)
        {
            var options = new PromptOpenFileOptions("\nSelect the engineering asset catalog CSV: ")
            {
                Filter = "CE Tools Asset Catalog (*.csv)|*.csv",
                DialogCaption = "Select CE Tools Engineering Asset Catalog"
            };
            if (!string.IsNullOrWhiteSpace(currentPath)) options.InitialDirectory = Path.GetDirectoryName(currentPath);
            PromptFileNameResult result = editor.GetFileNameForOpen(options);
            path = result.Status == PromptStatus.OK ? result.StringResult : string.Empty;
            return result.Status == PromptStatus.OK && (!mustExist || File.Exists(path));
        }

        private static bool PromptVisibility(
            Editor editor,
            EngineeringAssetApprovalStatus defaultValue,
            out EngineeringAssetApprovalStatus visibility)
        {
            string defaultKeyword = defaultValue == EngineeringAssetApprovalStatus.Reviewed
                ? "Reviewed"
                : defaultValue == EngineeringAssetApprovalStatus.Draft
                    ? "All"
                    : "Approved";
            var options = new PromptKeywordOptions(
                "\nAsset visibility [Approved/Reviewed/All] <" + defaultKeyword + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Approved");
            options.Keywords.Add("Reviewed");
            options.Keywords.Add("All");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                visibility = defaultValue;
                return false;
            }
            string value = result.Status == PromptStatus.OK ? result.StringResult : defaultKeyword;
            visibility = Equal(value, "Reviewed")
                ? EngineeringAssetApprovalStatus.Reviewed
                : Equal(value, "All")
                    ? EngineeringAssetApprovalStatus.Draft
                    : EngineeringAssetApprovalStatus.Approved;
            return true;
        }

        private static string VisibilityLabel(EngineeringAssetApprovalStatus value)
        {
            return value == EngineeringAssetApprovalStatus.Reviewed
                ? "Approved + Reviewed"
                : value == EngineeringAssetApprovalStatus.Draft
                    ? "Approved + Reviewed + ForReview + Draft"
                    : "Approved only";
        }

        private static bool PromptInsertedAsset(
            Document document,
            out ObjectId objectId,
            out AssetInsertionTag tag)
        {
            objectId = ObjectId.Null;
            tag = null;
            var options = new PromptEntityOptions("\nSelect one CE Tools inserted engineering asset: ");
            options.SetRejectMessage("\nSelect a block reference inserted by CE_ASSETINSERT.");
            options.AddAllowedClass(typeof(BlockReference), true);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                tag = ReadTag(entity);
                transaction.Commit();
            }
            if (tag == null)
            {
                document.Editor.WriteMessage("\nThe selected block has no CE engineering asset traceability record.");
                return false;
            }
            objectId = result.ObjectId;
            return true;
        }

        private static List<InsertedAssetReview> ReadInsertedAssetReviews(Database database)
        {
            var result = new List<InsertedAssetReview>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable == null) return result;
                foreach (ObjectId recordId in blockTable)
                {
                    BlockTableRecord record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (record == null || record.IsFromExternalReference) continue;
                    foreach (ObjectId objectId in record)
                    {
                        Entity entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                        AssetInsertionTag tag = ReadTag(entity);
                        if (tag == null) continue;
                        result.Add(BuildReview(objectId.Handle.ToString(), tag));
                    }
                }
            }
            return result.OrderBy(item => item.Tag.AssetId, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static InsertedAssetReview BuildReview(string handle, AssetInsertionTag tag)
        {
            string sourceState = CurrentSourceState(tag.SourcePath, tag.SourceSha256);
            string catalogRevision = string.Empty;
            string catalogStatus = string.Empty;
            string catalogState;
            string action;
            try
            {
                if (!File.Exists(tag.CatalogPath))
                {
                    catalogState = "Catalog missing";
                    action = "Restore or relocate the catalog; verify the inserted asset manually.";
                }
                else
                {
                    EngineeringAssetRecord record = EngineeringAssetCatalog.Load(tag.CatalogPath)
                        .Where(item => string.Equals(item.AssetId, tag.AssetId, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => item.IsActive)
                        .ThenByDescending(item => item.ApprovalStatus == EngineeringAssetApprovalStatus.Approved)
                        .ThenByDescending(item => item.Revision, StringComparer.CurrentCultureIgnoreCase)
                        .FirstOrDefault();
                    if (record == null)
                    {
                        catalogState = "AssetId absent";
                        action = "Restore the catalog record or treat the block as uncontrolled.";
                    }
                    else
                    {
                        catalogRevision = record.Revision;
                        catalogStatus = record.ApprovalStatus.ToString();
                        string currentCatalogHash = record.Sha256 ?? string.Empty;
                        bool sameRevision = string.Equals(record.Revision, tag.Revision, StringComparison.OrdinalIgnoreCase);
                        bool sameHash = string.Equals(currentCatalogHash, tag.SourceSha256, StringComparison.OrdinalIgnoreCase);
                        catalogState = sameRevision && sameHash
                            ? "Matches inserted record"
                            : "New/different catalog revision";
                        action = sameRevision && sameHash && sourceState == "Unchanged"
                            ? "No identity change detected; engineering content still requires normal review."
                            : "Review the current approved catalog revision and replace the block deliberately if required.";
                    }
                }
            }
            catch (Exception exception)
            {
                catalogState = "Catalog read failed";
                action = exception.Message;
            }
            return new InsertedAssetReview(
                handle, tag, catalogRevision, catalogStatus, sourceState, catalogState, action);
        }

        private static string CurrentSourceState(string sourcePath, string insertedHash)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return "Missing";
            try
            {
                string current = EngineeringAssetCatalog.CalculateSha256(sourcePath);
                return string.Equals(current, insertedHash, StringComparison.OrdinalIgnoreCase)
                    ? "Unchanged"
                    : "Changed";
            }
            catch { return "Unreadable"; }
        }

        private static void WriteTag(
            Entity entity,
            EngineeringAssetRecord asset,
            string catalogPath,
            string sourcePath,
            string sourceHash,
            double scale)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                TextValue("AssetId", asset.AssetId),
                TextValue("Title", asset.Title),
                TextValue("Revision", asset.Revision),
                TextValue("ApprovalStatus", asset.ApprovalStatus.ToString()),
                TextValue("Catalog", Path.GetFullPath(catalogPath)),
                TextValue("Source", Path.GetFullPath(sourcePath)),
                TextValue("Sha256", sourceHash),
                TextValue("Scale", scale.ToString("R", CultureInfo.InvariantCulture)),
                TextValue("InsertedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        }

        private static TypedValue TextValue(string key, string value)
        {
            return new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                key + "=" + (value ?? string.Empty));
        }

        private static AssetInsertionTag ReadTag(Entity entity)
        {
            if (entity == null) return null;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue typedValue in data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            string assetId;
            if (!values.TryGetValue("AssetId", out assetId) || string.IsNullOrWhiteSpace(assetId)) return null;
            return new AssetInsertionTag(
                assetId,
                Value(values, "Title"),
                Value(values, "Revision"),
                Value(values, "ApprovalStatus"),
                Value(values, "Catalog"),
                Value(values, "Source"),
                Value(values, "Sha256"),
                Value(values, "Scale"),
                Value(values, "InsertedUtc"));
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static AssetLibrarySettings ReadSettings(Database database)
        {
            var result = new AssetLibrarySettings(string.Empty, 1.0, EngineeringAssetApprovalStatus.Approved);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (namedObjects == null || !namedObjects.Contains(RootDictionaryName)) return result;
                DBDictionary root = transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (root == null || !root.Contains(SettingsRecordName)) return result;
                Xrecord record = transaction.GetObject(
                    root.GetAt(SettingsRecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return result;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (TypedValue typedValue in record.Data)
                {
                    string text = typedValue.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    int equals = text.IndexOf('=');
                    if (equals > 0) values[text.Substring(0, equals)] = text.Substring(equals + 1);
                }
                double units;
                if (!double.TryParse(Value(values, "DrawingUnitsPerMetre"), NumberStyles.Float, CultureInfo.InvariantCulture, out units) || units <= 0.0)
                    units = 1.0;
                EngineeringAssetApprovalStatus visibility;
                if (!Enum.TryParse(Value(values, "Visibility"), true, out visibility))
                    visibility = EngineeringAssetApprovalStatus.Approved;
                return new AssetLibrarySettings(Value(values, "Catalog"), units, visibility);
            }
        }

        private static void WriteSettings(Database database, AssetLibrarySettings settings)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                if (namedObjects == null) throw new InvalidOperationException("Named Objects Dictionary is unavailable.");
                DBDictionary root;
                if (namedObjects.Contains(RootDictionaryName))
                {
                    root = transaction.GetObject(namedObjects.GetAt(RootDictionaryName), OpenMode.ForWrite, false) as DBDictionary;
                }
                else
                {
                    root = new DBDictionary();
                    namedObjects.SetAt(RootDictionaryName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }
                if (root == null) throw new InvalidOperationException("Asset library settings dictionary is unavailable.");
                Xrecord record;
                if (root.Contains(SettingsRecordName))
                {
                    record = transaction.GetObject(root.GetAt(SettingsRecordName), OpenMode.ForWrite, false) as Xrecord;
                }
                else
                {
                    record = new Xrecord();
                    root.SetAt(SettingsRecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                record.Data = new ResultBuffer(
                    TextValue("Catalog", settings.CatalogPath),
                    TextValue("DrawingUnitsPerMetre", settings.DrawingUnitsPerMetre.ToString("R", CultureInfo.InvariantCulture)),
                    TextValue("Visibility", settings.MinimumVisibility.ToString()));
                transaction.Commit();
            }
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string defaultValue,
            bool allowSpaces,
            out string value)
        {
            var options = new PromptStringOptions(
                "\n" + label + (string.IsNullOrWhiteSpace(defaultValue) ? ": " : " <" + defaultValue + ">: "))
            {
                AllowSpaces = allowSpaces,
                UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
                DefaultValue = defaultValue ?? string.Empty
            };
            PromptResult result = editor.GetString(options);
            value = result.Status == PromptStatus.OK
                ? result.StringResult
                : result.Status == PromptStatus.None
                    ? defaultValue ?? string.Empty
                    : string.Empty;
            return result.Status != PromptStatus.Cancel;
        }

        private static bool PromptPositiveDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + Format(defaultValue) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PromptDouble(Editor editor, string label, double defaultValue, bool allowNegative, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + Format(defaultValue) + ">: ")
            {
                AllowNone = true,
                AllowNegative = allowNegative,
                AllowZero = true,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : Equal(result.StringResult, "Yes");
        }

        private static bool PromptExcelPath(Editor editor, string initialName, out string path)
        {
            var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Engineering Asset Report",
                InitialFileName = initialName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            path = result.Status == PromptStatus.OK
                ? EnsureExtension(result.StringResult, ".xlsx")
                : string.Empty;
            return result.Status == PromptStatus.OK;
        }

        private static string EnsureExtension(string path, string extension)
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class AssetLibrarySettings
    {
        public AssetLibrarySettings(
            string catalogPath,
            double drawingUnitsPerMetre,
            EngineeringAssetApprovalStatus minimumVisibility)
        {
            CatalogPath = catalogPath ?? string.Empty;
            DrawingUnitsPerMetre = drawingUnitsPerMetre;
            MinimumVisibility = minimumVisibility;
        }

        public string CatalogPath { get; private set; }
        public double DrawingUnitsPerMetre { get; private set; }
        public EngineeringAssetApprovalStatus MinimumVisibility { get; private set; }
    }

    internal sealed class AssetInsertionTag
    {
        public AssetInsertionTag(
            string assetId,
            string title,
            string revision,
            string approvalStatus,
            string catalogPath,
            string sourcePath,
            string sourceSha256,
            string scale,
            string insertedUtc)
        {
            AssetId = assetId;
            Title = title;
            Revision = revision;
            ApprovalStatus = approvalStatus;
            CatalogPath = catalogPath;
            SourcePath = sourcePath;
            SourceSha256 = sourceSha256;
            Scale = scale;
            InsertedUtc = insertedUtc;
        }

        public string AssetId { get; private set; }
        public string Title { get; private set; }
        public string Revision { get; private set; }
        public string ApprovalStatus { get; private set; }
        public string CatalogPath { get; private set; }
        public string SourcePath { get; private set; }
        public string SourceSha256 { get; private set; }
        public string Scale { get; private set; }
        public string InsertedUtc { get; private set; }
    }

    internal sealed class InsertedAssetReview
    {
        public InsertedAssetReview(
            string handle,
            AssetInsertionTag tag,
            string catalogRevision,
            string catalogStatus,
            string sourceState,
            string catalogState,
            string action)
        {
            Handle = handle;
            Tag = tag;
            CatalogRevision = catalogRevision;
            CatalogStatus = catalogStatus;
            SourceState = sourceState;
            CatalogState = catalogState;
            Action = action;
        }

        public string Handle { get; private set; }
        public AssetInsertionTag Tag { get; private set; }
        public string CatalogRevision { get; private set; }
        public string CatalogStatus { get; private set; }
        public string SourceState { get; private set; }
        public string CatalogState { get; private set; }
        public string Action { get; private set; }
    }
}
