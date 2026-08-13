using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11NetworkBatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D's native Network From Object command accepts one source object per
    /// invocation. This manager lets the user select the complete source set once,
    /// then safely queues one native creation operation at a time and resumes only
    /// after the previous native command has ended. Completed source objects are
    /// tagged so rerunning the production step does not silently duplicate a network.
    /// </summary>
    public sealed class August11NetworkBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKFROMPOLYLINESBATCH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateNetworksBatch()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (NetworkFromObjectBatchManager.IsRunning)
            {
                document.Editor.WriteMessage("\nA CE network-from-polylines batch is already running.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Networks from Polylines",
                "Select the COMPLETE source set in one AutoCAD selection. Window/crossing selection and multiple picks are supported. CE Tools then feeds each selected source to Civil 3D's native network-from-object workflow in sequence.");
            model.AddChoice(
                "Discipline",
                "01 Network",
                "Discipline",
                "Sewer",
                "Choose gravity or pressure network production.",
                new[] { "Sewer", "Stormwater", "Water", "Bulk Water" });
            model.AddChoice(
                "Duplicate",
                "02 Safety",
                "Previously completed CE source",
                "Skip previously completed",
                "Skip sources already marked as completed for the selected discipline, or intentionally process them again.",
                new[] { "Skip previously completed", "Process again" });
            model.AddChoice(
                "SelectionMode",
                "03 Sources",
                "Source selection",
                "Select multiple now",
                "Select all source polylines/lines/feature lines now, or deliberately use an existing PickFirst selection.",
                new[] { "Select multiple now", "Use current preselection" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selected = null;
            bool usePreselection = string.Equals(
                model.Text("SelectionMode"),
                "Use current preselection",
                StringComparison.OrdinalIgnoreCase);

            if (usePreselection)
                selected = document.Editor.SelectImplied();

            if (selected == null ||
                selected.Status != PromptStatus.OK ||
                selected.Value == null ||
                selected.Value.Count == 0)
            {
                // A stale one-object PickFirst selection used to make this command
                // behave as if it only supported one source at a time. Clear it and
                // always open a real AutoCAD multi-selection prompt by default.
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect ALL line/polyline/feature-line network sources: ",
                    MessageForRemoval = "\nRemove network source objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            string discipline = model.Text("Discipline");
            bool skipCompleted = string.Equals(
                model.Text("Duplicate"),
                "Skip previously completed",
                StringComparison.OrdinalIgnoreCase);
            List<ObjectId> sources = FilterSources(
                document.Database,
                selected.Value.GetObjectIds());
            if (skipCompleted)
                sources = sources
                    .Where(id => !NetworkSourceMarker.IsCompleted(document.Database, id, discipline))
                    .ToList();

            if (sources.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_NETWORKFROMPOLYLINESBATCH: no new supported source objects remain for {0}.",
                    discipline);
                return;
            }

            NetworkFromObjectBatchManager.Start(document, sources, discipline);
            document.Editor.WriteMessage(
                "\nCE_NETWORKFROMPOLYLINESBATCH started. Sources queued={0}; discipline={1}. Complete each Civil 3D native network dialog normally; CE Tools will advance through the complete selected source set automatically.",
                sources.Count,
                discipline);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKCONNECTSELECTED", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConnectSelectedParts()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple pipes and structures to connect/review: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;
            ObjectId[] ids = selected.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToArray();
            if (ids.Length == 0) return;
            document.Editor.SetImpliedSelection(ids);
            document.Editor.WriteMessage("\nCE_NETWORKCONNECTSELECTED: selected parts={0}. Passing the complete selection to CE's multi-connect/open-end workflow.", ids.Length);
            document.SendStringToExecute("CE_NETWORKCONNECTALL ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKMULTIBATCH", CommandFlags.Modal)]
        public void NetworkMultiTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Multiple Network Production",
                "Select many source objects once, avoid duplicate source runs, connect/review many parts and continue directly into discipline production.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create multiple networks from polylines / feature lines", "CE_NETWORKFROMPOLYLINESBATCH", "Queue every selected source through Civil 3D's native network-from-object workflow.", "01 Create"),
                    new DisciplineWorkflowAction("Connect selected multiple pipes / structures", "CE_NETWORKCONNECTSELECTED", "Pass the selected network parts to the CE multi-connect/open-end workflow.", "02 Connect"),
                    new DisciplineWorkflowAction("Review / repair open ends", "CE_NETWORKCONNECTALL", "Run the existing drawing-wide connection/open-end repair workflow.", "02 Connect"),
                    new DisciplineWorkflowAction("Network data / schedules", "CE_NETWORKDATA", "Review selected network parts and linked schedules.", "03 Review"),
                    new DisciplineWorkflowAction("Sewer Production Centre", "CE_SEWERPRODUCTIONCENTRE", "Continue into the guided sewer workflow.", "04 Continue"),
                    new DisciplineWorkflowAction("Stormwater Production Centre", "CE_SWPRODUCTIONCENTRE", "Continue into the guided stormwater workflow.", "04 Continue"),
                    new DisciplineWorkflowAction("Water Production Centre", "CE_WATERPRODUCTIONCENTRE", "Continue into the guided water workflow.", "04 Continue")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSOURCEMARKERSCLEAR", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ClearSourceMarkers()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selected = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect source curves whose CE network-completion marker should be cleared: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;
            int cleared = NetworkSourceMarker.Clear(document.Database, selected.Value.GetObjectIds());
            document.Editor.WriteMessage("\nCE_NETWORKSOURCEMARKERSCLEAR complete. Markers cleared={0}.", cleared);
        }

        private static List<ObjectId> FilterSources(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    if (value is Curve || value.GetType().Name.IndexOf("FeatureLine", StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Add(id);
                }
            }
            return result;
        }
    }

    internal static class NetworkFromObjectBatchManager
    {
        private static Document _document;
        private static readonly Queue<ObjectId> Queue = new Queue<ObjectId>();
        private static ObjectId _current = ObjectId.Null;
        private static string _discipline = string.Empty;
        private static string _nativeCommand = string.Empty;
        private static bool _waitingForNativeCommand;
        private static bool _launchPending;
        private static bool _hooked;

        internal static bool IsRunning { get { return _hooked; } }

        internal static void Start(Document document, IEnumerable<ObjectId> sources, string discipline)
        {
            Stop(false);
            _document = document;
            _discipline = discipline ?? "Sewer";
            bool pressure = string.Equals(_discipline, "Water", StringComparison.OrdinalIgnoreCase) || string.Equals(_discipline, "Bulk Water", StringComparison.OrdinalIgnoreCase);
            _nativeCommand = pressure ? "CREATEPRESSURENETWORKFROMOBJ" : "CREATENETWORKFROMOBJECT";
            foreach (ObjectId id in sources.Where(value => !value.IsNull && !value.IsErased).Distinct()) Queue.Enqueue(id);
            if (Queue.Count == 0) return;
            Hook();
            _launchPending = true;
        }

        private static void Hook()
        {
            if (_hooked || _document == null) return;
            _hooked = true;
            _document.CommandEnded += OnCommandEnded;
            _document.CommandCancelled += OnCommandCancelled;
            _document.CommandFailed += OnCommandCancelled;
            AcApplication.Idle += OnIdle;
        }

        private static void Stop(bool report)
        {
            if (_hooked && _document != null)
            {
                _document.CommandEnded -= OnCommandEnded;
                _document.CommandCancelled -= OnCommandCancelled;
                _document.CommandFailed -= OnCommandCancelled;
            }
            if (_hooked) AcApplication.Idle -= OnIdle;
            if (report && _document != null)
                _document.Editor.WriteMessage("\nCE multiple network-from-polylines batch complete.");
            Queue.Clear();
            _current = ObjectId.Null;
            _discipline = string.Empty;
            _nativeCommand = string.Empty;
            _waitingForNativeCommand = false;
            _launchPending = false;
            _hooked = false;
            _document = null;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (!_hooked || !_launchPending || _waitingForNativeCommand || _document == null) return;
            if (!ReferenceEquals(AcApplication.DocumentManager.MdiActiveDocument, _document)) return;
            string names = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(names)) return;
            if (Queue.Count == 0)
            {
                Stop(true);
                return;
            }
            _current = Queue.Dequeue();
            if (_current.IsNull || _current.IsErased)
            {
                _launchPending = true;
                return;
            }
            try
            {
                _document.Editor.SetImpliedSelection(new[] { _current });
                _waitingForNativeCommand = true;
                _launchPending = false;
                string command = string.Equals(_nativeCommand, "CREATEPRESSURENETWORKFROMOBJ", StringComparison.OrdinalIgnoreCase)
                    ? "_.CreatePressureNetworkFromObj "
                    : "_.CreateNetworkFromObject ";
                _document.SendStringToExecute(command, true, false, true);
            }
            catch
            {
                _waitingForNativeCommand = false;
                _launchPending = true;
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (!_waitingForNativeCommand || e == null) return;
            string command = Normalize(e.GlobalCommandName);
            if (!string.Equals(command, _nativeCommand, StringComparison.OrdinalIgnoreCase)) return;
            if (_document != null && !_current.IsNull && !_current.IsErased)
                NetworkSourceMarker.Mark(_document.Database, _current, _discipline);
            _waitingForNativeCommand = false;
            _current = ObjectId.Null;
            _launchPending = true;
        }

        private static void OnCommandCancelled(object sender, CommandEventArgs e)
        {
            if (!_waitingForNativeCommand || e == null) return;
            string command = Normalize(e.GlobalCommandName);
            if (!string.Equals(command, _nativeCommand, StringComparison.OrdinalIgnoreCase)) return;
            _waitingForNativeCommand = false;
            _current = ObjectId.Null;
            _launchPending = true;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
        }
    }

    internal static class NetworkSourceMarker
    {
        private const string Key = "CE_NETWORK_SOURCE_CREATED";

        internal static bool IsCompleted(Database database, ObjectId id, string discipline)
        {
            if (database == null || id.IsNull || id.IsErased) return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { return false; }
                if (value == null || value.ExtensionDictionary.IsNull) return false;
                DBDictionary dictionary;
                try { dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary; }
                catch { return false; }
                if (dictionary == null || !dictionary.Contains(Key)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return false;
                TypedValue[] values = record.Data.AsArray();
                return values.Length > 0 && string.Equals(Convert.ToString(values[0].Value), discipline, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void Mark(Database database, ObjectId id, string discipline)
        {
            if (database == null || id.IsNull || id.IsErased) return;
            try
            {
                using (DocumentLock documentLock = AcApplication.DocumentManager.MdiActiveDocument == null ? null : AcApplication.DocumentManager.MdiActiveDocument.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    if (value.ExtensionDictionary.IsNull) value.CreateExtensionDictionary();
                    DBDictionary dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
                    if (dictionary == null) return;
                    Xrecord record;
                    if (dictionary.Contains(Key)) record = transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForWrite, false) as Xrecord;
                    else
                    {
                        record = new Xrecord();
                        dictionary.SetAt(Key, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    if (record != null)
                    {
                        record.Data = new ResultBuffer(
                            new TypedValue((int)DxfCode.Text, discipline ?? string.Empty),
                            new TypedValue((int)DxfCode.Text, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        internal static int Clear(Database database, IEnumerable<ObjectId> ids)
        {
            if (database == null) return 0;
            int count = 0;
            using (DocumentLock documentLock = AcApplication.DocumentManager.MdiActiveDocument == null ? null : AcApplication.DocumentManager.MdiActiveDocument.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    if (value == null || value.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary;
                    try { dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary; }
                    catch { continue; }
                    if (dictionary == null || !dictionary.Contains(Key)) continue;
                    try
                    {
                        DBObject record = transaction.GetObject(dictionary.GetAt(Key), OpenMode.ForWrite, false);
                        dictionary.Remove(Key);
                        if (record != null) record.Erase();
                        count++;
                    }
                    catch { }
                }
                transaction.Commit();
            }
            return count;
        }
    }
}
