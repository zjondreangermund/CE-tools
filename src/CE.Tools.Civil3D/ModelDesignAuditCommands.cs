using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ModelDesignAuditCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Drawing-wide Civil 3D model inventory and health audit. The report reads
    /// current drawing state only: it does not rebuild, purge, repair or alter
    /// design objects. Findings are prioritised and exported with corrective actions.
    /// </summary>
    public sealed class ModelDesignAuditCommands
    {
        private const string CePrefix = "CE_";
        private static readonly string[] HandlePrefixes =
        {
            "Handle=", "Source=", "Boundary=", "Generated=", "Anchor="
        };

        [CommandMethod("CE_TOOLS", "CE_MODELREPORTTOOLS", CommandFlags.Modal)]
        public void ModelReportTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nCivil 3D model report [Report/Summary/Export] <Report>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Report");
            options.Keywords.Add("Summary");
            options.Keywords.Add("Export");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Report";
            string command = Equal(choice, "Summary")
                ? "CE_MODELREPORTINFO "
                : Equal(choice, "Export")
                    ? "CE_MODELREPORTEXPORT "
                    : "CE_MODELREPORT ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_MODELREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ModelReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ModelAuditSnapshot snapshot = BuildSnapshot(document);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Civil 3D Design Model Audit",
                BuildSubtitle(snapshot),
                BuildRows(snapshot, false),
                "CE TOOLS CIVIL 3D DESIGN MODEL AUDIT");
            WriteCompletion(document.Editor, "CE_MODELREPORT", snapshot);
        }

        [CommandMethod("CE_TOOLS", "CE_MODELREPORTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ModelReportInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ModelAuditSnapshot snapshot = BuildSnapshot(document);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Civil 3D Model Health Summary",
                BuildSubtitle(snapshot),
                BuildRows(snapshot, true),
                "CE TOOLS CIVIL 3D MODEL HEALTH SUMMARY");
            WriteCompletion(document.Editor, "CE_MODELREPORTINFO", snapshot);
        }

        [CommandMethod("CE_TOOLS", "CE_MODELREPORTEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportModelReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ModelAuditSnapshot snapshot = BuildSnapshot(document);
            string path;
            if (!PromptExcelPath(
                    document.Editor,
                    "CE-Tools-Civil3D-Model-Audit.xlsx",
                    out path))
                return;
            try
            {
                SimpleXlsxWriter.Write(
                    path,
                    "Model Audit",
                    BuildExportRows(snapshot));
                document.Editor.WriteMessage(
                    "\nCE_MODELREPORTEXPORT complete. Rows={0}; warnings={1}; errors={2}; workbook={3}",
                    snapshot.Inventory.Count + snapshot.Findings.Count,
                    snapshot.WarningCount,
                    snapshot.ErrorCount,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_MODELREPORTEXPORT failed. {0}",
                    exception.Message);
            }
        }

        private static ModelAuditSnapshot BuildSnapshot(Document document)
        {
            Database database = document.Database;
            var snapshot = new ModelAuditSnapshot
            {
                DrawingName = string.IsNullOrWhiteSpace(database.Filename)
                    ? "<Unsaved drawing>"
                    : database.Filename,
                AuditTime = DateTime.Now
            };

            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var civilNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var layerObjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ceAppCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var referencedHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staleHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ReadLayers(database, transaction, snapshot);
                ReadModelSpace(
                    database,
                    transaction,
                    snapshot,
                    typeCounts,
                    civilNames,
                    layerObjectCounts,
                    ceAppCounts,
                    referencedHandles,
                    staleHandles);
                ReadXrefs(database, transaction, snapshot);
                ReadLayouts(database, transaction, snapshot);
            }

            snapshot.CeReferencedHandleCount = referencedHandles.Count;
            snapshot.CeStaleHandleCount = staleHandles.Count;
            foreach (KeyValuePair<string, int> pair in ceAppCounts
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                snapshot.CeLinks.Add(new ModelInventoryItem(
                    pair.Key,
                    pair.Value,
                    "Linked entities / records"));
            }
            foreach (KeyValuePair<string, int> pair in typeCounts
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                snapshot.Inventory.Add(new ModelInventoryItem(
                    pair.Key,
                    pair.Value,
                    IsCivilType(pair.Key) ? "Civil 3D" : "AutoCAD"));
            }

            ReadCoordinateSystem(snapshot);
            BuildFindings(snapshot, civilNames, layerObjectCounts);
            return snapshot;
        }

        private static void ReadLayers(
            Database database,
            Transaction transaction,
            ModelAuditSnapshot snapshot)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null) return;
            foreach (ObjectId id in table)
            {
                LayerTableRecord layer = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer == null) continue;
                snapshot.LayerCount++;
                if (layer.IsLocked) snapshot.LockedLayerCount++;
                if (layer.IsFrozen) snapshot.FrozenLayerCount++;
                if (layer.IsOff) snapshot.OffLayerCount++;
                if (layer.IsDependent) snapshot.DependentLayerCount++;
            }
        }

        private static void ReadModelSpace(
            Database database,
            Transaction transaction,
            ModelAuditSnapshot snapshot,
            IDictionary<string, int> typeCounts,
            IDictionary<string, List<string>> civilNames,
            IDictionary<string, int> layerObjectCounts,
            IDictionary<string, int> ceAppCounts,
            ISet<string> referencedHandles,
            ISet<string> staleHandles)
        {
            BlockTable blockTable = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (blockTable == null || !blockTable.Has(BlockTableRecord.ModelSpace)) return;
            BlockTableRecord modelSpace = transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace],
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (modelSpace == null) return;

            foreach (ObjectId id in modelSpace)
            {
                Entity entity;
                try
                {
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch
                {
                    snapshot.UnreadableEntityCount++;
                    continue;
                }
                if (entity == null || entity.IsErased) continue;
                snapshot.ModelEntityCount++;

                string typeName = FriendlyTypeName(entity.GetType());
                Increment(typeCounts, typeName);
                Increment(layerObjectCounts, entity.Layer ?? "<No layer>");
                if (IsCivilType(entity.GetType().FullName))
                {
                    snapshot.CivilEntityCount++;
                    string name = ReadStringProperty(entity, "Name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        List<string> names;
                        if (!civilNames.TryGetValue(typeName, out names))
                        {
                            names = new List<string>();
                            civilNames[typeName] = names;
                        }
                        names.Add(name);
                    }
                    InspectCivilEntity(entity, typeName, snapshot);
                }
                if (entity is ProxyEntity) snapshot.ProxyEntityCount++;

                ScanResultBuffer(
                    database,
                    entity.XData,
                    ceAppCounts,
                    referencedHandles,
                    staleHandles);
                ScanExtensionDictionary(
                    database,
                    transaction,
                    entity,
                    ceAppCounts,
                    referencedHandles,
                    staleHandles);
            }
        }

        private static void InspectCivilEntity(
            Entity entity,
            string typeName,
            ModelAuditSnapshot snapshot)
        {
            bool reference = ReadBoolProperty(entity, "IsReferenceObject", false);
            bool stale = ReadBoolProperty(entity, "IsReferenceStale", false) ||
                         ReadBoolProperty(entity, "IsReferenceValid", true) == false;
            if (reference) snapshot.CivilReferenceCount++;
            if (reference && stale) snapshot.StaleCivilReferenceCount++;

            if (Contains(typeName, "Surface"))
            {
                snapshot.SurfaceCount++;
                double triangles = ReadDoubleProperty(entity, "NumberOfTriangles", -1.0);
                if (triangles == 0.0)
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "Surface",
                        DisplayName(entity, typeName),
                        "Surface reports zero triangles.",
                        "Review surface definition, boundaries and rebuild state."));
                }
            }
            else if (Contains(typeName, "Alignment"))
            {
                snapshot.AlignmentCount++;
                double length = ReadDoubleProperty(entity, "Length", -1.0);
                if (length == 0.0)
                {
                    snapshot.Findings.Add(ModelAuditFinding.Error(
                        "Alignment",
                        DisplayName(entity, typeName),
                        "Alignment length is zero.",
                        "Repair or remove the invalid alignment before production."));
                }
            }
            else if (Equal(typeName, "Profile")) snapshot.ProfileCount++;
            else if (Contains(typeName, "ProfileView")) snapshot.ProfileViewCount++;
            else if (Contains(typeName, "Corridor"))
            {
                snapshot.CorridorCount++;
                if (ReadBoolProperty(entity, "IsOutOfDate", false))
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "Corridor",
                        DisplayName(entity, typeName),
                        "Corridor is reported as out of date.",
                        "Review targets and rebuild the corridor."));
                }
            }
            else if (Contains(typeName, "FeatureLine")) snapshot.FeatureLineCount++;
            else if (Contains(typeName, "CogoPoint")) snapshot.CogoPointCount++;
            else if (Contains(typeName, "Structure")) snapshot.StructureCount++;
            else if (Contains(typeName, "Pipe")) snapshot.PipeCount++;
            else if (Contains(typeName, "Pressure")) snapshot.PressurePartCount++;
        }

        private static void ScanExtensionDictionary(
            Database database,
            Transaction transaction,
            Entity entity,
            IDictionary<string, int> ceAppCounts,
            ISet<string> referencedHandles,
            ISet<string> staleHandles)
        {
            if (entity.ExtensionDictionary.IsNull) return;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null) return;
            foreach (DBDictionaryEntry entry in dictionary)
            {
                if (entry.Key.StartsWith(CePrefix, StringComparison.OrdinalIgnoreCase))
                    Increment(ceAppCounts, entry.Key);
                Xrecord record = transaction.GetObject(
                    entry.Value,
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record != null)
                {
                    ScanResultBuffer(
                        database,
                        record.Data,
                        ceAppCounts,
                        referencedHandles,
                        staleHandles);
                }
            }
        }

        private static void ScanResultBuffer(
            Database database,
            ResultBuffer buffer,
            IDictionary<string, int> ceAppCounts,
            ISet<string> referencedHandles,
            ISet<string> staleHandles)
        {
            if (buffer == null) return;
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (value.TypeCode == (int)DxfCode.ExtendedDataRegAppName &&
                    text.StartsWith(CePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Increment(ceAppCounts, text);
                }
                foreach (string prefix in HandlePrefixes)
                {
                    if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string candidate = text.Substring(prefix.Length).Trim();
                    if (!IsHexHandle(candidate)) break;
                    referencedHandles.Add(candidate);
                    ObjectId id;
                    if (!TryResolveHandle(database, candidate, out id))
                        staleHandles.Add(candidate);
                    break;
                }
            }
        }

        private static void ReadXrefs(
            Database database,
            Transaction transaction,
            ModelAuditSnapshot snapshot)
        {
            BlockTable table = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (table == null) return;
            foreach (ObjectId id in table)
            {
                BlockTableRecord record = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (record == null ||
                    (!record.IsFromExternalReference && !record.IsFromOverlayReference))
                    continue;
                snapshot.Xrefs.Add(new XrefAuditItem(
                    record.Name,
                    record.PathName,
                    Convert.ToString(record.XrefStatus, CultureInfo.InvariantCulture),
                    record.IsUnloaded,
                    record.IsFromOverlayReference));
            }
        }

        private static void ReadLayouts(
            Database database,
            Transaction transaction,
            ModelAuditSnapshot snapshot)
        {
            DBDictionary dictionary = transaction.GetObject(
                database.LayoutDictionaryId,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null) return;
            foreach (DBDictionaryEntry entry in dictionary)
            {
                Layout layout = transaction.GetObject(
                    entry.Value,
                    OpenMode.ForRead,
                    false) as Layout;
                if (layout == null) continue;
                int viewports = 0;
                BlockTableRecord space = transaction.GetObject(
                    layout.BlockTableRecordId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (entity is Viewport) viewports++;
                    }
                }
                snapshot.Layouts.Add(new LayoutAuditItem(
                    layout.LayoutName,
                    layout.ModelType,
                    viewports,
                    layout.TabOrder,
                    layout.CanonicalMediaName,
                    layout.ConfigName));
            }
        }

        private static void ReadCoordinateSystem(ModelAuditSnapshot snapshot)
        {
            try
            {
                object civilDocument = CivilApplication.ActiveDocument;
                object settings = ReadProperty(civilDocument, "Settings");
                object drawingSettings = ReadProperty(settings, "DrawingSettings");
                object unitZone = ReadProperty(drawingSettings, "UnitZoneSettings");
                snapshot.CoordinateSystemCode = Convert.ToString(
                    ReadProperty(unitZone, "CoordinateSystemCode"),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                snapshot.CoordinateSystemCode = string.Empty;
            }
        }

        private static void BuildFindings(
            ModelAuditSnapshot snapshot,
            IDictionary<string, List<string>> civilNames,
            IDictionary<string, int> layerObjectCounts)
        {
            if (snapshot.DrawingName == "<Unsaved drawing>")
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "Drawing",
                    "File state",
                    "Drawing has not been saved.",
                    "Save to the controlled project folder before issuing or creating XREFs."));
            }
            if (string.IsNullOrWhiteSpace(snapshot.CoordinateSystemCode))
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "Coordinate system",
                    "Drawing assignment",
                    "No Civil 3D coordinate-system code was detected.",
                    "Confirm and assign the approved project coordinate system."));
            }
            else
            {
                snapshot.Findings.Add(ModelAuditFinding.Ok(
                    "Coordinate system",
                    "Drawing assignment",
                    snapshot.CoordinateSystemCode,
                    "Verify it matches the survey and project brief."));
            }
            if (snapshot.ProxyEntityCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "Compatibility",
                    "Proxy entities",
                    snapshot.ProxyEntityCount + " proxy entities detected.",
                    "Open with the required object enabler/product and confirm data fidelity."));
            }
            if (snapshot.UnreadableEntityCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Error(
                    "Database",
                    "Unreadable entities",
                    snapshot.UnreadableEntityCount + " model-space objects could not be opened.",
                    "Run AUDIT on a backup and investigate database corruption."));
            }
            if (snapshot.StaleCivilReferenceCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "Data shortcuts",
                    "Stale Civil references",
                    snapshot.StaleCivilReferenceCount + " stale/invalid Civil reference objects detected.",
                    "Synchronise or repair data shortcuts before publishing."));
            }
            if (snapshot.CeStaleHandleCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "CE links",
                    "Stale handles",
                    snapshot.CeStaleHandleCount + " linked handle references cannot be resolved.",
                    "Run the relevant CE information/refresh command and repair or detach stale links."));
            }
            else if (snapshot.CeReferencedHandleCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Ok(
                    "CE links",
                    "Handle integrity",
                    snapshot.CeReferencedHandleCount + " linked handles resolved without a stale reference.",
                    "Retest after source deletions and before issue."));
            }
            if (snapshot.LockedLayerCount > 0 ||
                snapshot.FrozenLayerCount > 0 ||
                snapshot.OffLayerCount > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Review(
                    "Layers",
                    "Visibility/editability",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Locked={0}; frozen={1}; off={2}; dependent={3}.",
                        snapshot.LockedLayerCount,
                        snapshot.FrozenLayerCount,
                        snapshot.OffLayerCount,
                        snapshot.DependentLayerCount),
                    "Confirm hidden or locked design information is intentional."));
            }
            int zeroObjects;
            if (layerObjectCounts.TryGetValue("0", out zeroObjects) && zeroObjects > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Review(
                    "Layers",
                    "Layer 0 usage",
                    zeroObjects + " model-space entities are on Layer 0.",
                    "Confirm these are intentional block-definition or drafting objects."));
            }
            int defpointsObjects;
            if (layerObjectCounts.TryGetValue("DEFPOINTS", out defpointsObjects) &&
                defpointsObjects > 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Warning(
                    "Layers",
                    "DEFPOINTS usage",
                    defpointsObjects + " model-space entities are on DEFPOINTS.",
                    "Move design/drafting geometry to controlled printable or non-print layers."));
            }
            foreach (XrefAuditItem xref in snapshot.Xrefs)
            {
                if (xref.Unloaded ||
                    (!Contains(xref.Status, "Resolved") && !Contains(xref.Status, "Loaded")))
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "XREF",
                        xref.Name,
                        "Status=" + xref.Status + "; unloaded=" + (xref.Unloaded ? "Yes" : "No"),
                        "Resolve, reload and verify the XREF path before publishing."));
                }
                if (string.IsNullOrWhiteSpace(xref.Path))
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "XREF",
                        xref.Name,
                        "The XREF path is empty.",
                        "Repair the source path or detach the unused reference."));
                }
            }
            foreach (LayoutAuditItem layout in snapshot.Layouts.Where(item => !item.Model))
            {
                if (layout.ViewportCount <= 1)
                {
                    snapshot.Findings.Add(ModelAuditFinding.Review(
                        "Layout",
                        layout.Name,
                        "No active paper-space viewport was detected.",
                        "Confirm the layout is intentionally a cover/register sheet or add the required viewport."));
                }
                if (string.IsNullOrWhiteSpace(layout.ConfigName) ||
                    Contains(layout.ConfigName, "None"))
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "Plot setup",
                        layout.Name,
                        "No controlled plotter configuration was detected.",
                        "Assign the office-approved PC3 and media before publishing."));
                }
            }
            foreach (KeyValuePair<string, List<string>> pair in civilNames)
            {
                foreach (IGrouping<string, string> duplicate in pair.Value
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1))
                {
                    snapshot.Findings.Add(ModelAuditFinding.Warning(
                        "Naming",
                        pair.Key,
                        duplicate.Count() + " objects share the name '" + duplicate.Key + "'.",
                        "Rename duplicate Civil objects to preserve clear production and reference workflows."));
                }
            }
            if (snapshot.Findings.Count == 0)
            {
                snapshot.Findings.Add(ModelAuditFinding.Ok(
                    "Model health",
                    "Automated checks",
                    "No automated warning or error was raised.",
                    "Complete engineer and drawing-office review before issue."));
            }
        }

        private static List<IList<string>> BuildRows(
            ModelAuditSnapshot snapshot,
            bool summaryOnly)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "CATEGORY", "ITEM / CHECK", "STATUS / COUNT", "DETAIL", "ACTION / NOTES" },
                new List<string> { "Summary", "Drawing", "Info", snapshot.DrawingName, "Audit time " + snapshot.AuditTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) },
                new List<string> { "Summary", "Model entities", snapshot.ModelEntityCount.ToString(CultureInfo.InvariantCulture), snapshot.CivilEntityCount + " Civil 3D entities", "Compare with expected project scope" },
                new List<string> { "Summary", "Civil design objects", snapshot.CivilEntityCount.ToString(CultureInfo.InvariantCulture), BuildCivilSummary(snapshot), "Review discipline completeness" },
                new List<string> { "Summary", "Layers / layouts / XREFs", snapshot.LayerCount + " / " + snapshot.Layouts.Count + " / " + snapshot.Xrefs.Count, "Coordinate system: " + ValueOrNotSet(snapshot.CoordinateSystemCode), "Verify project standards and issue setup" },
                new List<string> { "Summary", "CE linked handles", snapshot.CeReferencedHandleCount.ToString(CultureInfo.InvariantCulture), snapshot.CeStaleHandleCount + " stale", "Repair stale links before issue" },
                new List<string> { "Summary", "Findings", snapshot.Findings.Count.ToString(CultureInfo.InvariantCulture), "Errors=" + snapshot.ErrorCount + "; warnings=" + snapshot.WarningCount + "; review=" + snapshot.ReviewCount, "Resolve errors and warnings first" }
            };
            foreach (ModelAuditFinding finding in snapshot.Findings
                .OrderBy(item => SeverityOrder(item.Status))
                .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Item, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new List<string>
                {
                    finding.Category,
                    finding.Item,
                    finding.Status,
                    finding.Detail,
                    finding.Action
                });
            }
            if (summaryOnly) return rows;

            foreach (ModelInventoryItem item in snapshot.Inventory)
            {
                rows.Add(new List<string>
                {
                    "Inventory",
                    item.Name,
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    item.Note,
                    "Confirm required object types and counts"
                });
            }
            foreach (ModelInventoryItem item in snapshot.CeLinks)
            {
                rows.Add(new List<string>
                {
                    "CE links",
                    item.Name,
                    item.Count.ToString(CultureInfo.InvariantCulture),
                    item.Note,
                    "Use the matching CE information/refresh command"
                });
            }
            foreach (XrefAuditItem item in snapshot.Xrefs)
            {
                rows.Add(new List<string>
                {
                    "XREF inventory",
                    item.Name,
                    item.Status,
                    item.Path,
                    item.Overlay ? "Overlay" : "Attachment"
                });
            }
            foreach (LayoutAuditItem item in snapshot.Layouts)
            {
                rows.Add(new List<string>
                {
                    "Layout inventory",
                    item.Name,
                    item.ViewportCount.ToString(CultureInfo.InvariantCulture) + " viewports",
                    "PC3=" + ValueOrNotSet(item.ConfigName) + "; media=" + ValueOrNotSet(item.MediaName),
                    item.Model ? "Model tab" : "Tab order " + item.TabOrder
                });
            }
            return rows;
        }

        private static List<IList<string>> BuildExportRows(ModelAuditSnapshot snapshot)
        {
            var rows = BuildRows(snapshot, false);
            rows.Insert(0, new List<string>
            {
                "CE TOOLS CIVIL 3D DESIGN MODEL AUDIT",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty
            });
            return rows;
        }

        private static string BuildSubtitle(ModelAuditSnapshot snapshot)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "Read-only drawing audit. Entities={0}; Civil={1}; findings={2}; warnings={3}; errors={4}. Automated checks do not replace engineering or drawing-office review.",
                snapshot.ModelEntityCount,
                snapshot.CivilEntityCount,
                snapshot.Findings.Count,
                snapshot.WarningCount,
                snapshot.ErrorCount);
        }

        private static string BuildCivilSummary(ModelAuditSnapshot snapshot)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Surfaces={0}; alignments={1}; profiles={2}; profile views={3}; corridors={4}; feature lines={5}; COGO points={6}; pipes={7}; structures={8}; pressure parts={9}",
                snapshot.SurfaceCount,
                snapshot.AlignmentCount,
                snapshot.ProfileCount,
                snapshot.ProfileViewCount,
                snapshot.CorridorCount,
                snapshot.FeatureLineCount,
                snapshot.CogoPointCount,
                snapshot.PipeCount,
                snapshot.StructureCount,
                snapshot.PressurePartCount);
        }

        private static void WriteCompletion(
            Editor editor,
            string command,
            ModelAuditSnapshot snapshot)
        {
            editor.WriteMessage(
                "\n{0} complete. Entities={1}; Civil={2}; findings={3}; warnings={4}; errors={5}; stale CE handles={6}.",
                command,
                snapshot.ModelEntityCount,
                snapshot.CivilEntityCount,
                snapshot.Findings.Count,
                snapshot.WarningCount,
                snapshot.ErrorCount,
                snapshot.CeStaleHandleCount);
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            path = string.Empty;
            var options = new PromptSaveFileOptions(
                "\nChoose the model-audit Excel workbook path: ")
            {
                DialogCaption = "Export CE Tools Civil 3D Model Audit",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialFileName = defaultName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return false;
            path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return true;
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            PropertyInfo property = value.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetIndexParameters().Length != 0) return null;
            try { return property.GetValue(value, null); }
            catch { return null; }
        }

        private static string ReadStringProperty(object value, string name)
        {
            return Convert.ToString(ReadProperty(value, name), CultureInfo.InvariantCulture);
        }

        private static bool ReadBoolProperty(
            object value,
            string name,
            bool defaultValue)
        {
            object result = ReadProperty(value, name);
            if (result == null) return defaultValue;
            try { return Convert.ToBoolean(result, CultureInfo.InvariantCulture); }
            catch { return defaultValue; }
        }

        private static double ReadDoubleProperty(
            object value,
            string name,
            double defaultValue)
        {
            object result = ReadProperty(value, name);
            if (result == null) return defaultValue;
            try { return Convert.ToDouble(result, CultureInfo.InvariantCulture); }
            catch { return defaultValue; }
        }

        private static string DisplayName(object value, string fallback)
        {
            string name = ReadStringProperty(value, "Name");
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == null) return "Unknown";
            string name = type.Name;
            return name.EndsWith("Entity", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - "Entity".Length)
                : name;
        }

        private static bool IsCivilType(string typeName)
        {
            return !string.IsNullOrWhiteSpace(typeName) &&
                   typeName.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string value, string search)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Increment(
            IDictionary<string, int> values,
            string key)
        {
            int count;
            values.TryGetValue(key, out count);
            values[key] = count + 1;
        }

        private static bool IsHexHandle(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 16) return false;
            foreach (char character in value)
            {
                bool hex = (character >= '0' && character <= '9') ||
                           (character >= 'A' && character <= 'F') ||
                           (character >= 'a' && character <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static int SeverityOrder(string status)
        {
            if (Equal(status, "Error")) return 0;
            if (Equal(status, "Warning")) return 1;
            if (Equal(status, "Review")) return 2;
            return 3;
        }

        private static string ValueOrNotSet(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<Not set>" : value;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ModelAuditSnapshot
    {
        public ModelAuditSnapshot()
        {
            DrawingName = string.Empty;
            CoordinateSystemCode = string.Empty;
            Inventory = new List<ModelInventoryItem>();
            CeLinks = new List<ModelInventoryItem>();
            Xrefs = new List<XrefAuditItem>();
            Layouts = new List<LayoutAuditItem>();
            Findings = new List<ModelAuditFinding>();
        }

        public string DrawingName { get; set; }
        public DateTime AuditTime { get; set; }
        public string CoordinateSystemCode { get; set; }
        public int ModelEntityCount { get; set; }
        public int CivilEntityCount { get; set; }
        public int ProxyEntityCount { get; set; }
        public int UnreadableEntityCount { get; set; }
        public int LayerCount { get; set; }
        public int LockedLayerCount { get; set; }
        public int FrozenLayerCount { get; set; }
        public int OffLayerCount { get; set; }
        public int DependentLayerCount { get; set; }
        public int CivilReferenceCount { get; set; }
        public int StaleCivilReferenceCount { get; set; }
        public int CeReferencedHandleCount { get; set; }
        public int CeStaleHandleCount { get; set; }
        public int SurfaceCount { get; set; }
        public int AlignmentCount { get; set; }
        public int ProfileCount { get; set; }
        public int ProfileViewCount { get; set; }
        public int CorridorCount { get; set; }
        public int FeatureLineCount { get; set; }
        public int CogoPointCount { get; set; }
        public int PipeCount { get; set; }
        public int StructureCount { get; set; }
        public int PressurePartCount { get; set; }
        public IList<ModelInventoryItem> Inventory { get; private set; }
        public IList<ModelInventoryItem> CeLinks { get; private set; }
        public IList<XrefAuditItem> Xrefs { get; private set; }
        public IList<LayoutAuditItem> Layouts { get; private set; }
        public IList<ModelAuditFinding> Findings { get; private set; }

        public int ErrorCount
        {
            get { return Findings.Count(item => item.Status == "Error"); }
        }
        public int WarningCount
        {
            get { return Findings.Count(item => item.Status == "Warning"); }
        }
        public int ReviewCount
        {
            get { return Findings.Count(item => item.Status == "Review"); }
        }
    }

    internal sealed class ModelInventoryItem
    {
        public ModelInventoryItem(string name, int count, string note)
        {
            Name = name;
            Count = count;
            Note = note;
        }

        public string Name { get; private set; }
        public int Count { get; private set; }
        public string Note { get; private set; }
    }

    internal sealed class XrefAuditItem
    {
        public XrefAuditItem(
            string name,
            string path,
            string status,
            bool unloaded,
            bool overlay)
        {
            Name = name;
            Path = path;
            Status = status;
            Unloaded = unloaded;
            Overlay = overlay;
        }

        public string Name { get; private set; }
        public string Path { get; private set; }
        public string Status { get; private set; }
        public bool Unloaded { get; private set; }
        public bool Overlay { get; private set; }
    }

    internal sealed class LayoutAuditItem
    {
        public LayoutAuditItem(
            string name,
            bool model,
            int viewportCount,
            int tabOrder,
            string mediaName,
            string configName)
        {
            Name = name;
            Model = model;
            ViewportCount = viewportCount;
            TabOrder = tabOrder;
            MediaName = mediaName;
            ConfigName = configName;
        }

        public string Name { get; private set; }
        public bool Model { get; private set; }
        public int ViewportCount { get; private set; }
        public int TabOrder { get; private set; }
        public string MediaName { get; private set; }
        public string ConfigName { get; private set; }
    }

    internal sealed class ModelAuditFinding
    {
        private ModelAuditFinding(
            string category,
            string item,
            string status,
            string detail,
            string action)
        {
            Category = category;
            Item = item;
            Status = status;
            Detail = detail;
            Action = action;
        }

        public string Category { get; private set; }
        public string Item { get; private set; }
        public string Status { get; private set; }
        public string Detail { get; private set; }
        public string Action { get; private set; }

        public static ModelAuditFinding Ok(
            string category,
            string item,
            string detail,
            string action)
        {
            return new ModelAuditFinding(category, item, "OK", detail, action);
        }

        public static ModelAuditFinding Review(
            string category,
            string item,
            string detail,
            string action)
        {
            return new ModelAuditFinding(category, item, "Review", detail, action);
        }

        public static ModelAuditFinding Warning(
            string category,
            string item,
            string detail,
            string action)
        {
            return new ModelAuditFinding(category, item, "Warning", detail, action);
        }

        public static ModelAuditFinding Error(
            string category,
            string item,
            string detail,
            string action)
        {
            return new ModelAuditFinding(category, item, "Error", detail, action);
        }
    }
}
