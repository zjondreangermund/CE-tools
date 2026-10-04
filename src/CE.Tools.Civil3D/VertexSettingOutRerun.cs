using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace CETools.Civil3D
{
    public sealed partial class VertexSettingOutCommands
    {
        private static Dictionary<int, int> ReadRoadStarts(Database db, string prefix, IEnumerable<string> replacing)
        {
            var excluded = new HashSet<string>(replacing ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var starts = new Dictionary<int, int>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = GetModelSpace(db, tr, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.IsErased) continue;
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    string type, group, key;
                    if (entity == null || !TryReadEntityLink(entity, out type, out group, out key) ||
                        type != "OUTPUT" || excluded.Contains(key.Split('|')[0])) continue;
                    string text = "";
                    var cogo = entity as Autodesk.Civil.DatabaseServices.CogoPoint;
                    var mtext = entity as MText;
                    var leader = entity as MLeader;
                    if (cogo != null) text = cogo.RawDescription;
                    else if (mtext != null) text = mtext.Contents;
                    else if (leader != null && leader.MText != null) text = leader.MText.Contents;
                    Match match = Regex.Match(text ?? "", Regex.Escape(prefix) + @"(\d+)\.(\d+)(?!\d)");
                    int road, point;
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out road) ||
                        !int.TryParse(match.Groups[2].Value, out point) || point == int.MaxValue) continue;
                    int next;
                    starts.TryGetValue(road, out next);
                    starts[road] = Math.Max(next, point + 1);
                }
            }
            return starts;
        }

        private static void RestoreRoadNumbers(Database db, Transaction tr, IList<VertexSettingSource> sources, VertexSettingLink link)
        {
            JunctionSettingOutSequence.ReadMetadata(db, tr, sources);
            int legacy = link.RoadStartNumber;
            foreach (var source in OrderSources(sources, link.SequenceMode, link.StartRecordKey))
            {
                int road;
                if (link.RoadNumbers.TryGetValue(source.Handle, out road)) source.RoadNumber = road;
                // Existing pre-upgrade tables have no road map. Preserve their old
                // sequence until the user explicitly re-runs with road assignment.
                else if (source.RoadNumber <= 0 && link.RoadNumbers.Count == 0) source.RoadNumber = legacy;
                legacy++;
            }
            if (link.SequenceMode == "Clockwise per junction from top left")
                JunctionSettingOutSequence.Groups(sources, link.GroupingDistance);
        }

        private static void ReplaceSelectedGroups(Database db, CivilDocument civil, Transaction tr,
            BlockTableRecord space, VertexSettingLink replacement, double textHeight)
        {
            var selected = new HashSet<string>(replacement.SourceHandles, StringComparer.OrdinalIgnoreCase);
            var oldTables = new List<Tuple<Table, VertexSettingLink>>();
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                if (id.IsErased) continue;
                Table table = tr.GetObject(id, OpenMode.ForRead, false) as Table;
                if (table == null || table.GetXDataForApplication(AppName) == null) continue;
                VertexSettingLink old = ReadTableLink(table);
                if (old.SourceHandles.Any(selected.Contains)) oldTables.Add(Tuple.Create(table, old));
            }

            foreach (var item in oldTables)
            {
                Table table = item.Item1;
                VertexSettingLink old = item.Item2;
                var remaining = old.SourceHandles.Where(h => !selected.Contains(h)).ToList();
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    if (id.IsErased) continue;
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    string type, group, key;
                    if (entity == null || !TryReadEntityLink(entity, out type, out group, out key) || group != old.GroupId) continue;
                    if (remaining.Count == 0 || ((type == "OUTPUT" || type == "DIM") && selected.Contains(key.Split('|')[0])))
                    {
                        entity.UpgradeOpen();
                        entity.Erase(); // A failure aborts the whole replacement transaction.
                    }
                }
                if (remaining.Count == 0) continue;

                // A shared table is retained with its unselected sources. Refresh its
                // rows and linked outputs inside the same transaction as the replacement.
                old.SourceHandles = remaining;
                foreach (string handle in selected) old.RoadNumbers.Remove(handle);
                var ids = remaining.Select(h => ResolveHandle(db, h)).Where(id => !id.IsNull && !id.IsErased).ToList();
                int rejected;
                var sources = VertexSettingOutGeometry.ReadSources(db, tr, ids, out rejected);
                ApplyGenerationMode(sources, old.GenerationMode);
                ApplyElevationReference(db, sources, old.ElevationMode, ResolveHandle(db, old.ElevationSourceHandle));
                ApplyLevelReferences(db, sources, ResolveHandle(db, old.NgSurfaceHandle), ResolveHandle(db, old.DesignSurfaceHandle));
                RestoreRoadNumbers(db, tr, sources, old);
                var records = FlattenAndName(sources, old.Prefix, old.StartNumber, old.NumberingMode,
                    old.RoadStartNumber, old.SequenceMode, old.StartRecordKey, old.RoadSeeds);
                Dictionary<string, ObjectId> outputs, dimensions;
                InventoryGroup(space, tr, old.GroupId, out outputs, out dimensions);
                foreach (var record in records)
                {
                    ObjectId output;
                    if (outputs.TryGetValue(record.Key, out output) && UpdateOutput(tr, output, old, record, textHeight)) continue;
                    CaptureCurrentAnnotationOffset(tr, output, record);
                    if (!output.IsNull && !output.IsErased) tr.GetObject(output, OpenMode.ForWrite, false).Erase();
                    CreateOutput(db, civil, tr, space, old, record, textHeight);
                }
                table.UpgradeOpen();
                WriteTableLink(table, tr, old);
                PopulateTable(table, records, textHeight, old);
            }
        }
    }
}
