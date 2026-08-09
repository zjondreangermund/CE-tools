using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.UniversalDynamicRefreshCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.ProductionMetadataDynamicCommands))]

namespace CETools.Civil3D
{
    public sealed class UniversalDynamicRefreshCommands
    {
        [CommandMethod("CE_TOOLS", "CE_DYNAMICREFRESHALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAll()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            UniversalRefreshResult result = UniversalDynamicRefreshManager.RefreshNow(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_DYNAMICREFRESHALL complete. Linked engine={0}; vertex tables={1}; junction labels={2}; metadata attributes={3}; warnings={4}.",
                result.LinkedEngineRuns,
                result.VertexTables,
                result.JunctionLabels,
                result.MetadataAttributes,
                result.Warnings);
        }

        [CommandMethod("CE_TOOLS", "CE_DYNAMICREFRESHSETTINGS", CommandFlags.Modal)]
        public void Settings()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Universal Dynamic Refresh",
                "Refresh every CE-linked table, leader, MText, COGO point, junction, sewer sequence, drawing register and title block after relevant drawing changes.");
            model.AddChoice(
                "Enabled", "Automatic Refresh", "Deferred automatic refresh",
                UniversalDynamicRefreshManager.Enabled ? "Enabled" : "Disabled",
                "Refresh runs only when Civil 3D is idle and no command is active.",
                new[] { "Enabled", "Disabled" });
            model.AddDouble(
                "Delay", "Automatic Refresh", "Idle delay in seconds",
                UniversalDynamicRefreshManager.DelaySeconds,
                "Wait after the latest drawing change before refreshing linked outputs.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            UniversalDynamicRefreshManager.Enabled = string.Equals(model.Text("Enabled"), "Enabled", StringComparison.OrdinalIgnoreCase);
            UniversalDynamicRefreshManager.DelaySeconds = Math.Max(model.Double("Delay", 1.2), 0.5);
            UniversalDynamicRefreshManager.Queue();
        }
    }

    internal static class UniversalDynamicRefreshManager
    {
        private static Database _database;
        private static Document _document;
        private static bool _initialised;
        private static bool _pending;
        private static bool _busy;
        private static bool _undoRedoActive;
        private static DateTime _lastChangeUtc = DateTime.MinValue;
        private static DateTime _lastRefreshUtc = DateTime.MinValue;

        internal static bool Enabled { get; set; } = true;
        internal static double DelaySeconds { get; set; } = 1.8;

        internal static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentActivated += OnDocumentActivated;
            documents.DocumentCreated += OnDocumentActivated;
            documents.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(documents.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialised) return;
            _initialised = false;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentActivated -= OnDocumentActivated;
            documents.DocumentCreated -= OnDocumentActivated;
            documents.DocumentToBeDestroyed -= OnDocumentDestroyed;
            AcApplication.Idle -= OnIdle;
            Detach();
        }

        internal static void Queue()
        {
            if (_busy || _undoRedoActive) return;
            _pending = true;
            _lastChangeUtc = DateTime.UtcNow;
        }

        internal static UniversalRefreshResult RefreshNow(Document document)
        {
            return RefreshNow(document, false);
        }

        private static UniversalRefreshResult RefreshNow(
            Document document,
            bool suppressUndoRecording)
        {
            var result = new UniversalRefreshResult();
            if (document == null || _busy || _undoRedoActive) return result;
            bool undoRecordingDisabled = false;
            _busy = true;
            try
            {
                if (suppressUndoRecording)
                {
                    document.Database.DisableUndoRecording(true);
                    undoRecordingDisabled = true;
                }
                try
                {
                    LinkedRefreshEngine.Refresh(document, false);
                    result.LinkedEngineRuns++;
                }
                catch { result.Warnings++; }
                try { result.VertexTables += VertexSettingOutCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); }
                catch { result.Warnings++; }
                try { RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true); }
                catch { result.Warnings++; }
                try { SewerNetworkDynamicSequenceCommands.ResequenceAll(document, false); }
                catch { result.Warnings++; }
                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { SewerPlanLabelRuntimeManager.Apply(document); }
                catch { result.Warnings++; }
                try { ProfileViewBandRuntimeManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }
                catch { result.Warnings++; }
                try { FinalFeatureLineReportCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { CeTablePresentationManager.CenterCeTables(document); }
                catch { result.Warnings++; }
                _pending = false;
                _lastRefreshUtc = DateTime.UtcNow;
            }
            finally
            {
                if (undoRecordingDisabled)
                {
                    try { document.Database.DisableUndoRecording(false); }
                    catch { }
                }
                _busy = false;
            }
            return result;
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            Attach(e == null ? null : e.Document);
        }

        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e != null && ReferenceEquals(e.Document, _document)) Detach();
        }

        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _database = document == null ? null : document.Database;
            if (_document == null || _database == null) return;
            _database.ObjectModified += OnObjectChanged;
            _database.ObjectAppended += OnObjectChanged;
            _database.ObjectErased += OnObjectErased;
            _document.CommandWillStart += OnCommandWillStart;
            _document.CommandEnded += OnCommandEnded;
            _document.CommandCancelled += OnCommandEnded;
            _document.CommandFailed += OnCommandEnded;
            Queue();
        }

        private static void Detach()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnObjectChanged;
                _database.ObjectAppended -= OnObjectChanged;
                _database.ObjectErased -= OnObjectErased;
            }
            if (_document != null)
            {
                _document.CommandWillStart -= OnCommandWillStart;
                _document.CommandEnded -= OnCommandEnded;
                _document.CommandCancelled -= OnCommandEnded;
                _document.CommandFailed -= OnCommandEnded;
            }
            _database = null;
            _document = null;
            _undoRedoActive = false;
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (!IsUndoRedo(command)) return;
            _undoRedoActive = true;
            _pending = false;
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (IsUndoRedo(command))
            {
                // Object events raised while AutoCAD is undoing must not queue a
                // background CE refresh, otherwise the refresh becomes a new
                // undo item immediately after the user's undo.
                _undoRedoActive = false;
                _pending = false;
                _lastChangeUtc = DateTime.UtcNow;
                return;
            }
            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("MOVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("STRETCH", StringComparison.OrdinalIgnoreCase) >= 0)
                Queue();
        }

        private static string NormalizeCommand(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
        }

        private static bool IsUndoRedo(string command)
        {
            return string.Equals(command, "U", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "UNDO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "REDO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "MREDO", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("UNDO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("REDO", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs e)
        {
            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;
            DBObject value = e.DBObject;
            if (value is Entity || value is Xrecord || value is DBDictionary ||
                value is CogoPoint || value is Pipe || value is Structure ||
                value is Autodesk.Civil.DatabaseServices.Network)
                Queue();
        }

        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;
            Queue();
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!Enabled || !_pending || _busy || _undoRedoActive || active == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalSeconds < DelaySeconds) return;
            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;
            // Idle/background refresh must not create an undo item.
            RefreshNow(active, true);
        }
    }

    public sealed class ProductionMetadataDynamicCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROJECTMETADATAREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshMetadata()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int updated = ProductionMetadataDynamicManager.Refresh(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PROJECTMETADATAREFRESH complete. Title-block attributes updated={0}.", updated);
        }

        [CommandMethod("CE_TOOLS", "CE_TITLEBLOCKSYNC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SyncTitleBlocks()
        {
            RefreshMetadata();
        }
    }

    internal static class ProductionMetadataDynamicManager
    {
        internal static int Refresh(Document document)
        {
            if (document == null) return 0;
            Database database = document.Database;
            ProductionDrawingRegisterData register = ProductionDrawingRegisterStore.Read(database);
            register.ApplyProjectDefaults(ProjectSetupCommands.ReadSharedProjectMetadata(database));
            register.MergeSeeds(ProductionDrawingRegisterCommands.ReadLayoutSeeds(database));
            register.ApplyRowDefaults();
            ProductionDrawingRegisterStore.Write(database, register);

            int modelCount = CountModelObjects(database);
            string updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            int changed = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                if (layouts == null) return 0;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    BlockTableRecord paper = transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (paper == null) continue;
                    ProductionDrawingRegisterRow row = register.Find(layout.LayoutName) ?? new ProductionDrawingRegisterRow
                    {
                        Layout = layout.LayoutName,
                        DrawingNumber = layout.LayoutName,
                        Title = layout.LayoutName,
                        Purpose = "Project drawing",
                        Scale = "As shown",
                        Stage = register.Header("Project Stage"),
                        Revision = register.Header("Revision"),
                        IssueDate = register.Header("Issue Date")
                    };
                    IDictionary<string, string> values = BuildValues(register, row, modelCount, updatedAt);
                    foreach (ObjectId id in paper)
                    {
                        BlockReference reference;
                        try { reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference; }
                        catch { continue; }
                        if (reference == null || reference.AttributeCollection.Count == 0) continue;
                        foreach (ObjectId attributeId in reference.AttributeCollection)
                        {
                            AttributeReference attribute;
                            try { attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference; }
                            catch { continue; }
                            if (attribute == null) continue;
                            string key = Normalize(attribute.Tag);
                            string value = Resolve(key, values);
                            if (value == null || string.Equals(attribute.TextString, value, StringComparison.Ordinal)) continue;
                            attribute.TextString = value;
                            changed++;
                        }
                    }
                }
                transaction.Commit();
            }
            try { ProjectSetupCommands.RefreshInformationTables(document); }
            catch { }
            return changed;
        }

        private static int CountModelObjects(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
                return model == null ? 0 : model.Cast<ObjectId>().Count(id => !id.IsNull && !id.IsErased);
            }
        }

        private static IDictionary<string, string> BuildValues(ProductionDrawingRegisterData data, ProductionDrawingRegisterRow row, int modelCount, string updatedAt)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PROJECT", data.Header("Project Name") }, { "PROJECTNAME", data.Header("Project Name") },
                { "PROJECTNO", data.Header("Project Number") }, { "PROJECTNUMBER", data.Header("Project Number") },
                { "CLIENT", data.Header("Client") }, { "COMPANY", data.Header("Company") },
                { "DRAWINGNO", row.DrawingNumber }, { "DRAWINGNUMBER", row.DrawingNumber }, { "DWGNO", row.DrawingNumber },
                { "TITLE", row.Title }, { "DRAWINGTITLE", row.Title }, { "SHEETTITLE", row.Title },
                { "PURPOSE", row.Purpose }, { "DISCIPLINE", row.Purpose }, { "SCALE", row.Scale },
                { "STAGE", row.Stage }, { "STATUS", row.Stage }, { "REV", row.Revision }, { "REVISION", row.Revision },
                { "DATE", row.IssueDate }, { "ISSUEDATE", row.IssueDate },
                { "DESIGNED", data.Header("Designed By") }, { "DESIGNEDBY", data.Header("Designed By") },
                { "DRAWN", data.Header("Drawn By") }, { "DRAWNBY", data.Header("Drawn By") },
                { "CHECKED", data.Header("Checked By") }, { "CHECKEDBY", data.Header("Checked By") },
                { "APPROVED", data.Header("Approved By") }, { "APPROVEDBY", data.Header("Approved By") },
                { "LAYOUT", row.Layout }, { "SHEET", row.Layout },
                { "LASTMODELUPDATE", updatedAt }, { "LASTUPDATE", updatedAt },
                { "DESIGNOBJECTS", modelCount.ToString(CultureInfo.CurrentCulture) },
                { "MODELCOUNT", modelCount.ToString(CultureInfo.CurrentCulture) }
            };
            return values;
        }

        private static string Resolve(string key, IDictionary<string, string> values)
        {
            string value;
            if (values.TryGetValue(key, out value)) return value ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in values)
                if (key.Contains(pair.Key) || pair.Key.Contains(key)) return pair.Value ?? string.Empty;
            return null;
        }

        private static string Normalize(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        }
    }

    internal sealed class UniversalRefreshResult
    {
        internal int LinkedEngineRuns { get; set; }
        internal int VertexTables { get; set; }
        internal int JunctionLabels { get; set; }
        internal int MetadataAttributes { get; set; }
        internal int Warnings { get; set; }
    }
}
