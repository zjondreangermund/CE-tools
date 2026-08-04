using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BackgroundXrefManagementCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Background and XREF workflows from the CE Tools master item list.
    /// Selected drawing content can be audited, copied/moved to controlled light
    /// background layers, written to a separate DWG and attached as an XREF.
    /// </summary>
    public sealed class BackgroundXrefManagementCommands
    {
        private const string BackgroundPrefix = "CE-BG-";
        private const short BackgroundColour = 253;

        [CommandMethod("CE_TOOLS", "CE_BACKGROUNDTOOLS", CommandFlags.Modal)]
        public void BackgroundTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nBackground/XREF tools [Review/LightCopy/SplitXref/XrefInfo/Backup] <Review>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Review");
            options.Keywords.Add("LightCopy");
            options.Keywords.Add("SplitXref");
            options.Keywords.Add("XrefInfo");
            options.Keywords.Add("Backup");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Review";
            string command;
            if (string.Equals(choice, "LightCopy", StringComparison.OrdinalIgnoreCase))
                command = "CE_BACKGROUNDLIGHT ";
            else if (string.Equals(choice, "SplitXref", StringComparison.OrdinalIgnoreCase))
                command = "CE_XREFSPLIT ";
            else if (string.Equals(choice, "XrefInfo", StringComparison.OrdinalIgnoreCase))
                command = "CE_XREFINFO ";
            else if (string.Equals(choice, "Backup", StringComparison.OrdinalIgnoreCase))
                command = "CE_XREFBACKUP ";
            else
                command = "CE_BACKGROUNDREVIEW ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BACKGROUNDREVIEW",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReviewBackground()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect architectural/survey background objects to audit: ");
            if (selection.Status != PromptStatus.OK) return;

            BackgroundAudit audit = ReadAudit(document.Database, selection);
            List<KeyValuePair<string, string>> rows = BuildAuditRows(audit);
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Background Drawing Audit",
                "The audit reports layer/type/colour concentration and locked-layer issues. It does not modify the selected objects.",
                rows,
                "CE TOOLS BACKGROUND DRAWING AUDIT");
            document.Editor.WriteMessage(
                "\nCE_BACKGROUNDREVIEW complete. Objects={0}; layers={1}; types={2}; locked-layer objects={3}.",
                audit.ObjectCount,
                audit.LayerCounts.Count,
                audit.TypeCounts.Count,
                audit.LockedLayerObjects);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BACKGROUNDLIGHT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateLightBackground()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect architectural/survey objects for controlled light-background presentation: ");
            if (selection.Status != PromptStatus.OK) return;

            var modeOptions = new PromptKeywordOptions(
                "\nBackground operation [Copy/Move] <Copy>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("Copy");
            modeOptions.Keywords.Add("Move");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return;
            bool copy = modeResult.Status != PromptStatus.OK ||
                string.Equals(modeResult.StringResult, "Copy", StringComparison.OrdinalIgnoreCase);

            BackgroundAudit audit = ReadAudit(document.Database, selection);
            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Selected objects", audit.ObjectCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Source layers", audit.LayerCounts.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Operation", copy ? "Create light-background copies" : "Move selected objects to light-background layers"),
                Pair("Background colour", BackgroundColour.ToString(CultureInfo.InvariantCulture)),
                Pair("Layer naming", BackgroundPrefix + "<source layer>"),
                Pair("Result remains selected", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Light Background",
                    copy
                        ? "Copies will be placed on controlled CE background layers. Original objects remain unchanged."
                        : "Selected objects will be moved to controlled CE background layers. No geometry is deleted.",
                    review,
                    copy ? "Create Copies" : "Move Objects"))
            {
                editor.WriteMessage("\nCE_BACKGROUNDLIGHT cancelled.");
                return;
            }

            try
            {
                ObjectId[] resultIds = ApplyLightBackground(
                    document.Database,
                    selection,
                    copy);
                editor.SetImpliedSelection(resultIds);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_BACKGROUNDLIGHT complete. Result objects={0}; mode={1}. The result remains selected for Properties inspection.",
                    resultIds.Length,
                    copy ? "Copy" : "Move");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_BACKGROUNDLIGHT cancelled. No background transaction was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XREFSPLIT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SplitSelectionToXref()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect objects to export into a separate DWG and attach as an XREF: ");
            if (selection.Status != PromptStatus.OK) return;

            var saveOptions = new PromptSaveFileOptions(
                "\nSelect the new XREF drawing path: ")
            {
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                DialogCaption = "Create CE Tools XREF Drawing",
                InitialFileName = "CE-Discipline-Background.dwg"
            };
            PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
            if (fileResult.Status != PromptStatus.OK) return;
            string path = fileResult.StringResult;
            if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                path += ".dwg";

            PromptPointOptions pointOptions = new PromptPointOptions(
                "\nSpecify XREF base point or press Enter for 0,0,0: ")
            {
                AllowNone = true
            };
            PromptPointResult pointResult = editor.GetPoint(pointOptions);
            if (pointResult.Status == PromptStatus.Cancel) return;
            Point3d basePoint = pointResult.Status == PromptStatus.OK
                ? pointResult.Value
                : Point3d.Origin;

            var sourceOptions = new PromptKeywordOptions(
                "\nAfter attaching the XREF [Keep/Replace] original selected objects <Replace>: ")
            {
                AllowNone = true
            };
            sourceOptions.Keywords.Add("Keep");
            sourceOptions.Keywords.Add("Replace");
            PromptResult sourceResult = editor.GetKeywords(sourceOptions);
            if (sourceResult.Status == PromptStatus.Cancel) return;
            bool replace = sourceResult.Status != PromptStatus.OK ||
                string.Equals(sourceResult.StringResult, "Replace", StringComparison.OrdinalIgnoreCase);

            string xrefName = SanitizeName(Path.GetFileNameWithoutExtension(path));
            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Objects to export", selection.Value.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Output DWG", path),
                Pair("XREF name", xrefName),
                Pair("Base point", FormatPoint(basePoint)),
                Pair("Original objects", replace ? "Replace after successful attach" : "Keep"),
                Pair("Revision folder", Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "Revisions"))
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Split Selection to XREF",
                    "The selected objects and required drawing dependencies will be written to a separate DWG. The new file is then attached to the current drawing.",
                    review,
                    "Create XREF"))
            {
                editor.WriteMessage("\nCE_XREFSPLIT cancelled.");
                return;
            }

            try
            {
                CreateXrefFile(document.Database, selection, basePoint, path);
                ObjectId referenceId = AttachXref(
                    document.Database,
                    selection,
                    basePoint,
                    path,
                    xrefName,
                    replace);
                editor.SetImpliedSelection(new[] { referenceId });
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_XREFSPLIT complete. File={0}; XREF={1}; originals={2}.",
                    path,
                    xrefName,
                    replace ? "replaced" : "kept");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_XREFSPLIT stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_XREFINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void XrefInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<XrefRecord> records = ReadXrefs(document.Database);
            if (records.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_XREFINFO: no attached XREF definitions were found.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("XREF definitions", records.Count.ToString(CultureInfo.InvariantCulture))
            };
            foreach (XrefRecord record in records)
            {
                rows.Add(Pair(
                    record.Name,
                    record.Status + "; " + record.Path));
            }
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - XREF Information",
                "Paths and AutoCAD XREF states are read from the current drawing.",
                rows,
                "CE TOOLS XREF INFORMATION");
        }

        [CommandMethod("CE_TOOLS", "CE_XREFBACKUP", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BackupXrefSource()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            var options = new PromptEntityOptions(
                "\nSelect an attached XREF block reference to create a revision backup: ");
            options.SetRejectMessage("\nSelect a block reference that points to an attached XREF.");
            options.AddAllowedClass(typeof(BlockReference), false);
            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return;

            XrefRecord xref = ReadXrefFromReference(
                document.Database,
                result.ObjectId);
            if (xref == null || string.IsNullOrWhiteSpace(xref.Path))
            {
                editor.WriteMessage("\nCE_XREFBACKUP stopped. The selected block is not a resolved file-based XREF.");
                return;
            }
            string resolvedPath = ResolveXrefPath(document.Database, xref.Path);
            if (!File.Exists(resolvedPath))
            {
                editor.WriteMessage(
                    "\nCE_XREFBACKUP stopped. The source file was not found: {0}",
                    resolvedPath);
                return;
            }

            string folder = Path.Combine(
                Path.GetDirectoryName(resolvedPath) ?? string.Empty,
                "Revisions");
            Directory.CreateDirectory(folder);
            string backupPath = Path.Combine(
                folder,
                Path.GetFileNameWithoutExtension(resolvedPath) + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                ".dwg");

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("XREF", xref.Name),
                Pair("Source", resolvedPath),
                Pair("Backup", backupPath),
                Pair("Current drawing changed", "No")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - XREF Revision Backup",
                    "A timestamped file copy will be created before the external drawing is revised.",
                    rows,
                    "Create Backup"))
            {
                editor.WriteMessage("\nCE_XREFBACKUP cancelled.");
                return;
            }

            File.Copy(resolvedPath, backupPath, false);
            editor.WriteMessage(
                "\nCE_XREFBACKUP complete. Backup created: {0}",
                backupPath);
        }

        private static BackgroundAudit ReadAudit(
            Database database,
            PromptSelectionResult selection)
        {
            var audit = new BackgroundAudit();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    Entity entity = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null) continue;
                    audit.ObjectCount++;
                    Increment(audit.LayerCounts, entity.Layer);
                    Increment(audit.TypeCounts, entity.GetType().Name);
                    Increment(audit.ColourCounts, DescribeColour(entity));
                    LayerTableRecord layer = transaction.GetObject(
                        entity.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked)
                        audit.LockedLayerObjects++;
                    if (entity is BlockReference)
                    {
                        BlockReference reference = (BlockReference)entity;
                        BlockTableRecord definition = transaction.GetObject(
                            reference.BlockTableRecord,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                        if (definition != null && definition.IsFromExternalReference)
                            audit.XrefReferences++;
                    }
                }
            }
            return audit;
        }

        private static List<KeyValuePair<string, string>> BuildAuditRows(
            BackgroundAudit audit)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Selected objects", audit.ObjectCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Distinct layers", audit.LayerCounts.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Distinct object types", audit.TypeCounts.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Locked-layer objects", audit.LockedLayerObjects.ToString(CultureInfo.InvariantCulture)),
                Pair("Selected XREF references", audit.XrefReferences.ToString(CultureInfo.InvariantCulture))
            };
            foreach (KeyValuePair<string, int> item in audit.LayerCounts
                .OrderByDescending(item => item.Value)
                .Take(25))
            {
                rows.Add(Pair("Layer: " + item.Key, item.Value.ToString(CultureInfo.InvariantCulture)));
            }
            foreach (KeyValuePair<string, int> item in audit.TypeCounts
                .OrderByDescending(item => item.Value)
                .Take(15))
            {
                rows.Add(Pair("Type: " + item.Key, item.Value.ToString(CultureInfo.InvariantCulture)));
            }
            foreach (KeyValuePair<string, int> item in audit.ColourCounts
                .OrderByDescending(item => item.Value)
                .Take(10))
            {
                rows.Add(Pair("Colour: " + item.Key, item.Value.ToString(CultureInfo.InvariantCulture)));
            }
            return rows;
        }

        private static ObjectId[] ApplyLightBackground(
            Database database,
            PromptSelectionResult selection,
            bool copy)
        {
            var results = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");
                var layerMap = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    Entity source = transaction.GetObject(
                        selected.ObjectId,
                        copy ? OpenMode.ForRead : OpenMode.ForWrite,
                        false) as Entity;
                    if (source == null) continue;
                    LayerTableRecord sourceLayer = transaction.GetObject(
                        source.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (sourceLayer != null && sourceLayer.IsLocked) continue;

                    ObjectId layerId;
                    if (!layerMap.TryGetValue(source.Layer, out layerId))
                    {
                        layerId = GetOrCreateBackgroundLayer(
                            database,
                            transaction,
                            source.Layer);
                        layerMap[source.Layer] = layerId;
                    }

                    if (copy)
                    {
                        Entity clone = source.Clone() as Entity;
                        if (clone == null) continue;
                        clone.LayerId = layerId;
                        clone.ColorIndex = 256;
                        currentSpace.AppendEntity(clone);
                        transaction.AddNewlyCreatedDBObject(clone, true);
                        results.Add(clone.ObjectId);
                    }
                    else
                    {
                        source.LayerId = layerId;
                        source.ColorIndex = 256;
                        results.Add(source.ObjectId);
                    }
                }
                transaction.Commit();
            }
            return results.ToArray();
        }

        private static void CreateXrefFile(
            Database database,
            PromptSelectionResult selection,
            Point3d basePoint,
            string path)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);
            if (File.Exists(path))
                throw new InvalidOperationException("The selected XREF output file already exists. Choose a new file name or create a revision backup first.");

            var ids = new ObjectIdCollection(selection.Value.GetObjectIds());
            using (Database output = database.Wblock(ids, basePoint))
            {
                output.SaveAs(path, DwgVersion.Current);
            }
            if (!File.Exists(path))
                throw new InvalidOperationException("AutoCAD did not create the requested XREF drawing.");
        }

        private static ObjectId AttachXref(
            Database database,
            PromptSelectionResult selection,
            Point3d basePoint,
            string path,
            string xrefName,
            bool replace)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId definitionId = database.AttachXref(path, xrefName);
                if (definitionId.IsNull)
                    throw new InvalidOperationException("AutoCAD did not return an XREF definition.");
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                var reference = new BlockReference(basePoint, definitionId);
                reference.SetDatabaseDefaults(database);
                currentSpace.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);

                if (replace)
                {
                    foreach (ObjectId objectId in selection.Value.GetObjectIds())
                    {
                        Entity entity = transaction.GetObject(
                            objectId,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (entity != null) entity.Erase();
                    }
                }

                transaction.Commit();
                return reference.ObjectId;
            }
        }

        private static List<XrefRecord> ReadXrefs(Database database)
        {
            var records = new List<XrefRecord>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (table == null) return records;
                foreach (ObjectId objectId in table)
                {
                    BlockTableRecord definition = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (definition == null || !definition.IsFromExternalReference)
                        continue;
                    records.Add(new XrefRecord(
                        definition.Name,
                        definition.PathName ?? string.Empty,
                        definition.XrefStatus.ToString()));
                }
            }
            return records.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static XrefRecord ReadXrefFromReference(
            Database database,
            ObjectId referenceId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference reference = transaction.GetObject(
                    referenceId,
                    OpenMode.ForRead,
                    false) as BlockReference;
                if (reference == null) return null;
                BlockTableRecord definition = transaction.GetObject(
                    reference.BlockTableRecord,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (definition == null || !definition.IsFromExternalReference)
                    return null;
                return new XrefRecord(
                    definition.Name,
                    definition.PathName ?? string.Empty,
                    definition.XrefStatus.ToString());
            }
        }

        private static string ResolveXrefPath(
            Database hostDatabase,
            string path)
        {
            if (Path.IsPathRooted(path)) return path;
            string hostFolder = string.IsNullOrWhiteSpace(hostDatabase.Filename)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(hostDatabase.Filename);
            return Path.GetFullPath(Path.Combine(hostFolder ?? string.Empty, path));
        }

        private static ObjectId GetOrCreateBackgroundLayer(
            Database database,
            Transaction transaction,
            string sourceLayer)
        {
            string name = BackgroundPrefix + SanitizeName(sourceLayer);
            if (name.Length > 255) name = name.Substring(0, 255);
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            LayerTableRecord layer;
            if (layers.Has(name))
            {
                layer = transaction.GetObject(
                    layers[name],
                    OpenMode.ForWrite,
                    false) as LayerTableRecord;
                if (layer == null) return layers[name];
            }
            else
            {
                layers.UpgradeOpen();
                layer = new LayerTableRecord { Name = name };
                ObjectId id = layers.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
            }
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, BackgroundColour);
            layer.IsOff = false;
            layer.IsFrozen = false;
            layer.IsLocked = false;
            layer.IsPlottable = true;
            return layer.ObjectId;
        }

        private static string DescribeColour(Entity entity)
        {
            if (entity.ColorIndex == 256) return "ByLayer";
            if (entity.ColorIndex == 0) return "ByBlock";
            return "ACI " + entity.ColorIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static void Increment(
            IDictionary<string, int> values,
            string key)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "<Unnamed>";
            int count;
            values.TryGetValue(key, out count);
            values[key] = count + 1;
        }

        private static PromptSelectionResult GetSelection(
            Editor editor,
            string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "BACKGROUND";
            char[] invalid = Path.GetInvalidFileNameChars();
            var result = new string(value
                .Select(character => invalid.Contains(character) ||
                    character == '|' ||
                    character == ':' ||
                    character == '*' ||
                    character == '?' ||
                    character == '<' ||
                    character == '>' ||
                    character == '/' ||
                    character == '\\'
                        ? '-'
                        : character)
                .ToArray());
            return result.Trim();
        }

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class BackgroundAudit
    {
        public BackgroundAudit()
        {
            LayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            TypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ColourCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public int ObjectCount { get; set; }
        public int LockedLayerObjects { get; set; }
        public int XrefReferences { get; set; }
        public Dictionary<string, int> LayerCounts { get; private set; }
        public Dictionary<string, int> TypeCounts { get; private set; }
        public Dictionary<string, int> ColourCounts { get; private set; }
    }

    internal sealed class XrefRecord
    {
        public XrefRecord(string name, string path, string status)
        {
            Name = name;
            Path = path;
            Status = status;
        }

        public string Name { get; private set; }
        public string Path { get; private set; }
        public string Status { get; private set; }
    }
}
