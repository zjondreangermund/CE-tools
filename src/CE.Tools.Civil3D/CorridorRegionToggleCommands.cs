using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CorridorRegionToggleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Snapshot enabled/disabled corridor regions before temporarily enabling all.
    /// The snapshot lives in the drawing and is restored by explicit command.
    /// No corridor rebuilding or region deletion is performed here.
    /// </summary>
    public sealed class CorridorRegionToggleCommands
    {
        private const string SnapshotKey = "CE_CORRIDOR_REGION_ENABLED_SNAPSHOT_V1";
        [CommandMethod("CE_TOOLS", "CE_CORRIDORREGIONSALLON",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AllOn() { Run(false); }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORREGIONSRESTORE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Restore() { Run(true); }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORREGIONSTOOLS",
            CommandFlags.Modal)]
        public void Menu()
        {
            Document d = AcApplication.DocumentManager.MdiActiveDocument;
            if (d == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(d, "CE Tools - Corridor Region Toggle",
                "Enable every region temporarily and restore the exact previously enabled/disabled states.",
                new List<DisciplineWorkflowAction> {
                    new DisciplineWorkflowAction("Automatic rebuild ON / OFF", "CE_AUTOMATICREBUILD", "Set native corridor/surface automatic rebuild flags.", "00 Rebuild"),
                    new DisciplineWorkflowAction("Enable all regions (save current state)",
                        "CE_CORRIDORREGIONSALLON", "Snapshot region states before switching all on.", "01 Enable"),
                    new DisciplineWorkflowAction("Restore saved region states",
                        "CE_CORRIDORREGIONSRESTORE", "Restore saved state and keep the snapshot until the next enable.", "02 Restore")
                });
        }

        private static void Run(bool restore)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var all = new List<ObjectId>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTable table = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null) return;
                BlockTableRecord model = tr.GetObject(table[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead) as BlockTableRecord;
                if (model == null) return;
                foreach (ObjectId id in model)
                    if (tr.GetObject(id, OpenMode.ForRead, false) is Corridor)
                        all.Add(id);
            }
            if (all.Count == 0) { doc.Editor.WriteMessage("\nNo corridors found."); return; }
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                List<CorridorRegionSnapshot> saved = Load(tr, doc.Database);
                if (restore && saved.Count == 0)
                {
                    doc.Editor.WriteMessage("\nNo region snapshot exists. Enable all once to save the original states.");
                    return;
                }
                if (!restore && saved.Count > 0)
                {
                    doc.Editor.WriteMessage("\nExisting corridor-region snapshot preserved. All baseline and region processing flags will be enabled again.");
                }
                // Capture every flag before changing any baseline. A baseline switch
                // must never contaminate the snapshot of its region flags.
                var live = new List<CorridorRegionSnapshot>();
                var regions = new Dictionary<CorridorRegionSnapshot, BaselineRegion>();
                var baselines = new Dictionary<CorridorRegionSnapshot, Baseline>();
                foreach (ObjectId id in all)
                {
                    var corridor = tr.GetObject(id, OpenMode.ForWrite, false) as Corridor;
                    if (corridor == null || corridor.IsReferenceObject) continue;
                    for (int b = 0; b < corridor.Baselines.Count; b++)
                    {
                        var baseline = corridor.Baselines[b];
                        bool originalBaseline = baseline.NeedsProcessing;
                        for (int n = 0; n < baseline.BaselineRegions.Count; n++)
                        {
                            var region = baseline.BaselineRegions[n];
                            var state = new CorridorRegionSnapshot { CorridorHandle = id.Handle.ToString(), CorridorName = corridor.Name,
                                Baseline = b, BaselineName = baseline.Name, Region = n, RegionName = region.Name,
                                Start = region.StartStation, End = region.EndStation, Enabled = region.NeedsProcessing,
                                BaselineEnabled = originalBaseline };
                            live.Add(state); regions[state] = region; baselines[state] = baseline;
                        }
                    }
                }
                int switched = 0, unchanged = 0, missing = 0;
                var matched = new HashSet<CorridorRegionSnapshot>();
                var baselineRestore = new Dictionary<Baseline, bool>();
                if (!restore && saved.Count == 0) Save(tr, doc.Database, live);
                foreach (var current in live)
                {
                    var baseline = baselines[current]; var region = regions[current];
                    if (!restore)
                    {
                        baseline.NeedsProcessing = true;
                        if (!region.NeedsProcessing) { region.NeedsProcessing = true; switched++; } else unchanged++;
                        continue;
                    }
                    var matches = CorridorRegionSnapshot.Find(current, saved, live);
                    if (matches.Count == 0)
                    {
                        missing++;
                        doc.Editor.WriteMessage("\nUnmatched: {0} / {1} / {2} [{3:0.###}–{4:0.###}]", current.CorridorName,
                            current.BaselineName, current.RegionName, current.Start, current.End);
                        continue;
                    }
                    foreach (var match in matches) matched.Add(match);
                    var old = matches[0];
                    if (region.NeedsProcessing != old.Enabled) { region.NeedsProcessing = old.Enabled; switched++; } else unchanged++;
                    if (old.BaselineEnabled.HasValue) baselineRestore[baseline] = old.BaselineEnabled.Value;
                }
                // Restore baseline flags LAST so region writes cannot re-enable a disabled baseline.
                foreach (var item in baselineRestore) item.Key.NeedsProcessing = item.Value;
                int unusedSaved = restore ? saved.Count(s => !matched.Contains(s)) : 0;
                if (restore && missing == 0 && unusedSaved == 0) ClearSnapshot(tr, doc.Database);
                if (restore && unusedSaved > 0)
                    doc.Editor.WriteMessage("\nSaved regions not matched to current geometry: {0}. Snapshot retained.", unusedSaved);
                tr.Commit();
                doc.Editor.WriteMessage(
                    restore
                        ? "\nCE_CORRIDORREGIONSRESTORE: restored={0}, unchanged={1}, unmatched regions={2}. Snapshot is cleared only when all regions match; see separate saved-region count above."
                        : "\nCE_CORRIDORREGIONSALLON: enabled={0}, already on={1}, unmatched={2}. Every baseline (including first, last and intermediate) is marked for corridor rebuilding.",
                    switched, unchanged, missing);
            }
            doc.Editor.Regen();
        }

        private static void ClearSnapshot(Transaction tr, Database database)
        {
            DBDictionary dictionary = tr.GetObject(database.NamedObjectsDictionaryId,
                OpenMode.ForWrite) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(SnapshotKey)) return;
            ObjectId recordId = dictionary.GetAt(SnapshotKey);
            dictionary.Remove(SnapshotKey);
            DBObject record = tr.GetObject(recordId, OpenMode.ForWrite, false);
            if (record != null) record.Erase();
        }

        private static List<CorridorRegionSnapshot> Load(Transaction tr, Database database)
        {
            DBDictionary dictionary = tr.GetObject(database.NamedObjectsDictionaryId,
                OpenMode.ForRead) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(SnapshotKey))
                return new List<CorridorRegionSnapshot>();
            Xrecord x = tr.GetObject(dictionary.GetAt(SnapshotKey), OpenMode.ForRead) as Xrecord;
            return x == null || x.Data == null ? new List<CorridorRegionSnapshot>() :
                x.Data.AsArray().Select(v => CorridorRegionSnapshot.Parse(Convert.ToString(v.Value, CultureInfo.InvariantCulture)))
                    .Where(v => v != null).ToList();
        }

        private static void Save(Transaction tr, Database database, List<CorridorRegionSnapshot> states)
        {
            DBDictionary dictionary = tr.GetObject(database.NamedObjectsDictionaryId,
                OpenMode.ForWrite) as DBDictionary;
            Xrecord x = new Xrecord();
            dictionary.SetAt(SnapshotKey, x);
            tr.AddNewlyCreatedDBObject(x, true);
            x.Data = new ResultBuffer(states.Select(v =>
                new TypedValue((int)DxfCode.Text, v.Encode())).ToArray());
        }
    }
}
