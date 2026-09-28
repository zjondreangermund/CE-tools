using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadAlignmentNameSyncCommands))]

namespace CETools.Civil3D
{
    /// <summary>Synchronizes CE road object names after an alignment is renamed in Civil 3D.</summary>
    internal static class RoadAlignmentNameSync
    {
        private static Document _document;
        private static readonly Dictionary<ObjectId, string> KnownNames = new Dictionary<ObjectId, string>();
        private static bool _pending;
        private static bool _busy;
        private static bool _alignmentChanged;

        internal static void Initialize()
        {
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            AcApplication.Idle -= OnIdle;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
            Detach();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            Attach(e == null ? null : e.Document);
        }

        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e != null && ReferenceEquals(_document, e.Document)) Detach();
        }

        private static void Attach(Document document)
        {
            if (ReferenceEquals(_document, document)) return;
            Detach();
            _document = document;
            if (document == null) return;
            Snapshot(document);
            document.Database.ObjectModified += OnObjectModified;
            document.CommandEnded += OnCommandEnded;
        }

        private static void Detach()
        {
            if (_document != null)
            {
                _document.CommandEnded -= OnCommandEnded;
                _document.Database.ObjectModified -= OnObjectModified;
            }
            _document = null;
            KnownNames.Clear();
            _pending = false;
            _alignmentChanged = false;
        }

        private static void Snapshot(Document document)
        {
            try
            {
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(document.Database);
                    foreach (ObjectId id in civil.GetAlignmentIds())
                    {
                        Alignment alignment = tr.GetObject(id, OpenMode.ForRead, false) as Alignment;
                        if (alignment != null) KnownNames[id] = alignment.Name;
                    }
                }
            }
            catch { }
        }

        private static void OnObjectModified(object sender, ObjectEventArgs e)
        {
            if (!_busy && e != null && e.DBObject is Alignment)
                _alignmentChanged = true;
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            // Work after Civil 3D has completed the rename transaction.
            if (!_busy && _alignmentChanged)
            {
                _pending = true;
                _alignmentChanged = false;
            }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            // The Properties palette can commit an alignment name without
            // issuing a CommandEnded event. Process that edit once Civil 3D
            // has left any active drawing command.
            if (_alignmentChanged && active != null)
            {
                try
                {
                    if (Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE")) != 0) return;
                    _pending = true;
                    _alignmentChanged = false;
                }
                catch { return; }
            }
            if (!_pending || _busy || active == null) return;
            _pending = false;
            Sync(active);
        }

        internal static int Sync(Document document)
        {
            if (document == null || _busy) return 0;
            _busy = true;
            int renamed = 0;
            try
            {
                using (DocumentLock locked = document.LockDocument())
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(document.Database);
                    foreach (ObjectId id in civil.GetAlignmentIds())
                    {
                        Alignment alignment = null;
                        try { alignment = tr.GetObject(id, OpenMode.ForRead, false) as Alignment; }
                        catch { }
                        if (alignment == null) continue;
                        string current = alignment.Name ?? string.Empty;
                        string previous;
                        if (KnownNames.TryGetValue(id, out previous) &&
                            !string.IsNullOrWhiteSpace(previous) &&
                            !string.Equals(previous, current, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(current))
                        {
                            renamed += RenameRelated(civil, alignment, previous, current, tr, document.Database);
                        }
                        KnownNames[id] = current;
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                try { document.Editor.WriteMessage("\nCE road name sync: " + ex.Message); }
                catch { }
            }
            finally { _busy = false; }
            return renamed;
        }

        private static int RenameRelated(CivilDocument civil, Alignment alignment,
            string oldName, string newName, Transaction tr, Database database)
        {
            int count = 0;
            foreach (ObjectId id in alignment.GetProfileIds())
                count += RenameObject(tr, id, oldName, newName);
            foreach (ObjectId id in alignment.GetProfileViewIds())
                count += RenameObject(tr, id, oldName, newName);

            foreach (ObjectId id in CorridorIds(civil, tr, database))
            {
                DBObject corridor = null;
                try { corridor = tr.GetObject(id, OpenMode.ForRead, false); }
                catch { }
                if (corridor == null || corridor.GetType().Name != "Corridor") continue;
                object baselines = Property(corridor, "Baselines");
                bool matches = false;
                foreach (object baseline in Enumerate(baselines))
                {
                    ObjectId alignmentId = AsId(Property(baseline, "AlignmentId"));
                    if (alignmentId.IsNull)
                        alignmentId = AsId(Property(baseline, "AlignmentObjectId"));
                    if (alignmentId != alignment.ObjectId) continue;
                    matches = true;
                    try { corridor.UpgradeOpen(); } catch { }
                    // Baseline names are independent of the corridor name.
                    count += RenameNamedValue(baseline, oldName, newName);
                }
                if (matches) count += RenameObject(tr, id, oldName, newName);
            }
            return count;
        }

        private static int RenameObject(Transaction tr, ObjectId id, string oldName, string newName)
        {
            try
            {
                DBObject value = tr.GetObject(id, OpenMode.ForWrite, false);
                return RenameNamedValue(value, oldName, newName);
            }
            catch { return 0; } // Civil 3D rejects duplicate or read-only names.
        }

        private static int RenameNamedValue(object value, string oldName, string newName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty("Name");
                if (property == null || !property.CanWrite) return 0;
                string name = property.GetValue(value, null) as string;
                string replacement = ReplaceRoadToken(name, oldName, newName);
                if (replacement == null) return 0;
                property.SetValue(value, replacement, null);
                return 1;
            }
            catch { return 0; }
        }

        internal static string ReplaceRoadToken(string name, string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(oldName) ||
                string.IsNullOrWhiteSpace(newName)) return null;
            // A linked object can be named RD-01-FG or LONGSECTION RD-01.
            // Do not change RD-010 when RD-01 is renamed.
            string pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(oldName) + @"(?![A-Za-z0-9])";
            string renamed = Regex.Replace(name, pattern, _ => newName,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return string.Equals(name, renamed, StringComparison.Ordinal) ? null : renamed;
        }

        private static IEnumerable<ObjectId> CorridorIds(CivilDocument civil, Transaction tr, Database db)
        {
            var ids = new HashSet<ObjectId>();
            foreach (object item in Enumerate(Property(civil, "CorridorCollection")))
            {
                ObjectId id = AsId(item);
                if (!id.IsNull) ids.Add(id);
            }
            try
            {
                BlockTableRecord model = tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                    foreach (ObjectId id in model)
                        if (!id.IsNull && id.ObjectClass != null &&
                            id.ObjectClass.Name.IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0)
                            ids.Add(id);
            }
            catch { }
            return ids;
        }

        private static IEnumerable Enumerate(object value)
        {
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null) return sequence;
            var items = new List<object>();
            try
            {
                int count = Convert.ToInt32(Property(value, "Count"));
                PropertyInfo indexer = value.GetType().GetProperty("Item", new[] { typeof(int) });
                if (indexer != null)
                    for (int i = 0; i < count; i++) items.Add(indexer.GetValue(value, new object[] { i }));
            }
            catch { }
            return items;
        }

        private static object Property(object value, string name)
        {
            try { return value == null ? null : value.GetType().GetProperty(name)?.GetValue(value, null); }
            catch { return null; }
        }

        private static ObjectId AsId(object value)
        {
            if (value is ObjectId) return (ObjectId)value;
            DBObject obj = value as DBObject;
            return obj == null ? ObjectId.Null : obj.ObjectId;
        }
    }

    public sealed class RoadAlignmentNameSyncCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADNAMESYNC", CommandFlags.Modal)]
        public void SynchronizeRoadNames()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            int renamed = RoadAlignmentNameSync.Sync(document);
            if (document != null)
                document.Editor.WriteMessage("\nCE_ROADNAMESYNC: related names updated={0}.", renamed);
        }
    }
}
