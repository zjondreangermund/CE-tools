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
        private sealed class RegionState
        {
            public string CorridorHandle;
            public int Baseline;
            public int Region;
            public double Start;
            public double End;
            public bool Enabled;
            public string Encode()
            {
                return string.Join("|", CorridorHandle,
                    Baseline.ToString(CultureInfo.InvariantCulture),
                    Region.ToString(CultureInfo.InvariantCulture),
                    Start.ToString("R", CultureInfo.InvariantCulture),
                    End.ToString("R", CultureInfo.InvariantCulture),
                    Enabled ? "1" : "0");
            }
            public static RegionState Parse(string line)
            {
                string[] p = (line ?? "").Split('|');
                if (p.Length != 6) return null;
                int b, r; double s, e;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b) ||
                    !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ||
                    !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s) ||
                    !double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out e)) return null;
                return new RegionState { CorridorHandle = p[0], Baseline = b, Region = r, Start = s, End = e, Enabled = p[5] == "1" };
            }
        }

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
                List<RegionState> saved = Load(tr, doc.Database);
                if (restore && saved.Count == 0)
                {
                    doc.Editor.WriteMessage("\nNo region snapshot exists. Enable all once to save the original states.");
                    return;
                }
                if (!restore && saved.Count > 0)
                {
                    doc.Editor.WriteMessage("\nA saved snapshot already exists. Restore it before saving another state; original flags were not overwritten.");
                    return;
                }
                var newSnapshot = new List<RegionState>();
                int switched = 0, unchanged = 0, missing = 0;
                foreach (ObjectId id in all)
                {
                    Corridor corridor = tr.GetObject(id, OpenMode.ForWrite, false) as Corridor;
                    if (corridor == null || corridor.IsReferenceObject) continue;
                    string handle = id.Handle.ToString();
                    for (int b = 0; b < corridor.Baselines.Count; b++)
                    {
                        Baseline baseline = corridor.Baselines[b];
                        for (int n = 0; n < baseline.BaselineRegions.Count; n++)
                        {
                            BaselineRegion region = baseline.BaselineRegions[n];
                            if (region == null) continue;
                            if (!restore)
                            {
                                newSnapshot.Add(new RegionState {
                                    CorridorHandle = handle, Baseline = b, Region = n,
                                    Start = region.StartStation, End = region.EndStation,
                                    Enabled = region.NeedsProcessing
                                });
                                if (!region.NeedsProcessing) { region.NeedsProcessing = true; switched++; }
                                else unchanged++;
                            }
                            else
                            {
                                RegionState old = saved.FirstOrDefault(s =>
                                    string.Equals(s.CorridorHandle, handle, StringComparison.OrdinalIgnoreCase) &&
                                    s.Baseline == b && s.Region == n &&
                                    Math.Abs(s.Start - region.StartStation) < 0.001 &&
                                    Math.Abs(s.End - region.EndStation) < 0.001);
                                if (old == null) { missing++; continue; }
                                if (region.NeedsProcessing != old.Enabled)
                                { region.NeedsProcessing = old.Enabled; switched++; }
                                else unchanged++;
                            }
                        }
                    }
                }
                if (restore && missing == 0)
                    ClearSnapshot(tr, doc.Database);
                if (!restore)
                {
                    if (newSnapshot.Count == 0)
                    {
                        doc.Editor.WriteMessage("\nNo writable corridor regions found; nothing changed.");
                        return;
                    }
                    Save(tr, doc.Database, newSnapshot);
                }
                tr.Commit();
                doc.Editor.WriteMessage(
                    restore
                        ? "\nCE_CORRIDORREGIONSRESTORE: restored={0}, unchanged={1}, unmatched regions={2}. Snapshot retained."
                        : "\nCE_CORRIDORREGIONSALLON: enabled={0}, already on={1}; original states saved in drawing.",
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

        private static List<RegionState> Load(Transaction tr, Database database)
        {
            DBDictionary dictionary = tr.GetObject(database.NamedObjectsDictionaryId,
                OpenMode.ForRead) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(SnapshotKey))
                return new List<RegionState>();
            Xrecord x = tr.GetObject(dictionary.GetAt(SnapshotKey), OpenMode.ForRead) as Xrecord;
            return x == null || x.Data == null ? new List<RegionState>() :
                x.Data.AsArray().Select(v => RegionState.Parse(Convert.ToString(v.Value, CultureInfo.InvariantCulture)))
                    .Where(v => v != null).ToList();
        }

        private static void Save(Transaction tr, Database database, List<RegionState> states)
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
