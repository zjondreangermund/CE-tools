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

[assembly: CommandClass(typeof(CETools.Civil3D.CorridorEndExtensionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Temporary corridor edge extension. Persist one baseline/region station
    /// snapshot separately from the region ON/OFF snapshot. Do not rebuild or
    /// alter NeedsProcessing, which is used for junction exclusions.
    /// </summary>
    public sealed class CorridorEndExtensionCommands
    {
        private const string Snapshot = "CE_CORRIDOR_END_EXTENSION_V1";
        private sealed class State
        {
            public string Handle;
            public int Baseline;
            public int Index;
            public double Start;
            public double End;
            public double ExtendedStart;
            public double ExtendedEnd;
            public string Encode()
            {
                return string.Join("|", Handle, Baseline.ToString(CultureInfo.InvariantCulture),
                    Index.ToString(CultureInfo.InvariantCulture),
                    Start.ToString("R", CultureInfo.InvariantCulture),
                    End.ToString("R", CultureInfo.InvariantCulture),
                    ExtendedStart.ToString("R", CultureInfo.InvariantCulture),
                    ExtendedEnd.ToString("R", CultureInfo.InvariantCulture));
            }
            public static State Parse(string text)
            {
                string[] p = text.Split('|');
                if (p.Length != 7) return null;
                int b, i; double s, e, x, y;
                if (!int.TryParse(p[1], out b) || !int.TryParse(p[2], out i) ||
                    !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s) ||
                    !double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out e) ||
                    !double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                    !double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return null;
                return new State { Handle = p[0], Baseline = b, Index = i,
                    Start = s, End = e, ExtendedStart = x, ExtendedEnd = y };
            }
        }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORENDSEXTEND",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Extend() { Execute(false); }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORENDSRESTORE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Restore() { Execute(true); }

        private static void Execute(bool restore)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var scope = new ProductionSettingsDialogModel(
                restore ? "CE Tools - Restore Corridor Ends" : "CE Tools - Extend Corridor Ends",
                "Store the original outer region stations before extending to baseline limits. Does not enable excluded junction regions or rebuild the corridor.");
            scope.AddChoice("Discipline", "01 Scope", "Corridor type", "Road and sewer",
                "Choose which corridors to process based on their corridor names.",
                new[] { "Road", "Sewer", "Road and sewer" });
            scope.AddChoice("Selection", "01 Scope", "Corridors", "Selected corridors",
                "Select multiple corridors in the drawing or use all matching corridors.",
                new[] { "Selected corridors", "All matching corridors" });
            if (!DisciplineWorkflowDialogs.EditSettings(scope)) return;
            HashSet<ObjectId> wanted = null;
            if (scope.Text("Selection") == "Selected corridors")
            {
                PromptSelectionResult selection = doc.Editor.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nSelect the road/sewer corridors at the T-junctions: ",
                    AllowDuplicates = false
                });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                wanted = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                DBDictionary nod = tr.GetObject(doc.Database.NamedObjectsDictionaryId,
                    restore ? OpenMode.ForRead : OpenMode.ForWrite) as DBDictionary;
                if (nod == null) return;
                bool exists = nod.Contains(Snapshot);
                if (!restore && exists)
                {
                    doc.Editor.WriteMessage("\nA corridor-end snapshot exists. Restore it before starting another extension.");
                    return;
                }
                if (restore && !exists)
                {
                    doc.Editor.WriteMessage("\nNo saved corridor-end stations found.");
                    return;
                }
                List<State> original = restore
                    ? Read(tr, nod) : new List<State>();
                List<State> changes = new List<State>();
                var changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int modified = 0, skipped = 0;
                BlockTable bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord model = bt == null ? null :
                    tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (model == null) return;
                foreach (ObjectId id in model)
                {
                    if (wanted != null && !wanted.Contains(id)) continue;
                    Corridor corridor = tr.GetObject(id, OpenMode.ForWrite, false) as Corridor;
                    if (corridor == null || corridor.IsReferenceObject) continue;
                    bool sewer = (corridor.Name ?? "").IndexOf("SEW", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (scope.Text("Discipline") == "Sewer" && !sewer ||
                        scope.Text("Discipline") == "Road" && sewer) continue;
                    for (int b = 0; b < corridor.Baselines.Count; b++)
                    {
                        Baseline baseLine = corridor.Baselines[b];
                        if (baseLine.BaselineRegions.Count == 0) continue;
                        // Only the two outside regions are changed, leaving
                        // interior T-junction and split region limits untouched.
                        int[] edges = baseLine.BaselineRegions.Count == 1 ?
                            new[] { 0 } : new[] { 0, baseLine.BaselineRegions.Count - 1 };
                        foreach (int n in edges)
                        {
                            BaselineRegion region = baseLine.BaselineRegions[n];
                            if (region == null) continue;
                            string handle = id.Handle.ToString();
                            if (!restore)
                            {
                                State record = new State {
                                    Handle = handle, Baseline = b, Index = n,
                                    Start = region.StartStation, End = region.EndStation,
                                    ExtendedStart = n == 0 ? baseLine.StartStation : region.StartStation,
                                    ExtendedEnd = n == baseLine.BaselineRegions.Count - 1
                                        ? baseLine.EndStation : region.EndStation
                                };
                                if (record.ExtendedStart > record.Start + 0.001 ||
                                    record.ExtendedEnd < record.End - 0.001) { skipped++; continue; }
                                changes.Add(record);
                                if (Math.Abs(record.Start - record.ExtendedStart) > 0.001)
                                { region.StartStation = record.ExtendedStart; modified++; }
                                if (Math.Abs(record.End - record.ExtendedEnd) > 0.001)
                                { region.EndStation = record.ExtendedEnd; modified++; }
                            }
                            else
                            {
                                State record = original.FirstOrDefault(v =>
                                    v.Handle == handle && v.Baseline == b && v.Index == n);
                                if (record == null) continue;
                                // Never overwrite a region subsequently edited
                                // by another command: require exact extended limits.
                                if (Math.Abs(region.StartStation - record.ExtendedStart) > 0.001 ||
                                    Math.Abs(region.EndStation - record.ExtendedEnd) > 0.001)
                                { skipped++; continue; }
                                region.StartStation = record.Start;
                                region.EndStation = record.End;
                                modified++;
                                changedIds.Add(record.Encode());
                            }
                        }
                    }
                }
                if (!restore)
                {
                    if (changes.Count == 0) { doc.Editor.WriteMessage("\nNo regions eligible for extension."); return; }
                    var record = new Xrecord { Data = new ResultBuffer(changes.Select(v =>
                        new TypedValue((int)DxfCode.Text, v.Encode())).ToArray()) };
                    nod.SetAt(Snapshot, record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }
                else if (changedIds.Count == original.Count)
                {
                    nod.UpgradeOpen();
                    ObjectId oldId = nod.GetAt(Snapshot);
                    nod.Remove(Snapshot);
                    tr.GetObject(oldId, OpenMode.ForWrite).Erase();
                }
                tr.Commit();
                doc.Editor.WriteMessage("\nCorridor endpoint operation complete. Station endpoints changed={0}; skipped={1}.",
                    modified, skipped);
            }
            doc.Editor.Regen();
        }

        private static List<State> Read(Transaction tr, DBDictionary dictionary)
        {
            Xrecord record = tr.GetObject(dictionary.GetAt(Snapshot), OpenMode.ForRead) as Xrecord;
            if (record == null || record.Data == null) return new List<State>();
            return record.Data.AsArray().Select(v =>
                State.Parse(Convert.ToString(v.Value, CultureInfo.InvariantCulture)))
                .Where(v => v != null).ToList();
        }
    }
}
