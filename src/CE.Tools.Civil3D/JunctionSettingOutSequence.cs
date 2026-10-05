using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CETools.Core;

namespace CETools.Civil3D
{
    internal static class JunctionSettingOutSequence
    {
        internal static Point3d Centre(VertexSettingSource source)
        {
            var points = source.Records.Where(r => r.Kind != "ARC CENTER").Select(r => r.Point).ToList();
            return new Point3d((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
                (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2, 0);
        }

        internal static List<List<VertexSettingSource>> Groups(IEnumerable<VertexSettingSource> sources, double distance)
        {
            var groups = new List<List<VertexSettingSource>>();
            foreach (VertexSettingSource source in sources.OrderByDescending(s => Centre(s).Y).ThenBy(s => Centre(s).X))
            {
                var group = groups.FirstOrDefault(g => !string.IsNullOrEmpty(source.JunctionGroup)
                    ? g[0].JunctionGroup == source.JunctionGroup
                    : string.IsNullOrEmpty(g[0].JunctionGroup) && g.All(s => Centre(s).DistanceTo(Centre(source)) <= distance));
                if (group == null) { group = new List<VertexSettingSource>(); groups.Add(group); }
                group.Add(source);
            }
            foreach (var group in groups)
            {
                var points = group.Select(Centre).ToList();
                var centre = new Point3d((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
                    (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2, 0);
                foreach (var source in group) source.JunctionCenter = centre;
            }
            return groups.OrderByDescending(g => g[0].JunctionCenter.Y).ThenBy(g => g[0].JunctionCenter.X).ToList();
        }

        internal static void ReadMetadata(Database db, Transaction tr, IEnumerable<VertexSettingSource> sources)
        {
            foreach (var source in sources)
            {
                Entity entity = tr.GetObject(source.SourceId, OpenMode.ForRead, false) as Entity;
                TypedValue[] layout = Record(entity, tr, "CE_ROAD_LAYOUT");
                if (layout != null && layout.Length >= 7)
                {
                    source.RoadHandle = Convert.ToString(layout[1].Value);
                    if (Convert.ToString(layout[0].Value).StartsWith("JUNCTION", StringComparison.OrdinalIgnoreCase))
                        source.JunctionGroup = Convert.ToString(layout[5].Value);
                }

                // Newer batch junctions store their exact owning/main road handle
                // and junction group directly on the generated return. This avoids
                // assigning ROAD/RD numbers from selection order or from a nearby
                // crossing road when setting-out is re-run.
                ResultBuffer batchBuffer = null;
                try
                {
                    batchBuffer = entity == null
                        ? null
                        : entity.GetXDataForApplication("CE_ROAD_JUNCTION");
                }
                catch { }
                if (batchBuffer != null)
                {
                    try
                    {
                        foreach (TypedValue value in batchBuffer.AsArray())
                        {
                            if (value.TypeCode !=
                                (int)DxfCode.ExtendedDataAsciiString)
                                continue;
                            string text =
                                Convert.ToString(
                                    value.Value,
                                    CultureInfo.InvariantCulture) ??
                                string.Empty;
                            if (text.StartsWith(
                                    "MAIN=",
                                    StringComparison.OrdinalIgnoreCase))
                                source.RoadHandle =
                                    text.Substring(5);
                            else if (text.StartsWith(
                                    "GROUP=",
                                    StringComparison.OrdinalIgnoreCase))
                                source.JunctionGroup =
                                    text.Substring(6);
                        }
                    }
                    finally
                    {
                        batchBuffer.Dispose();
                    }
                }

                source.RoadNumber = Number(entity, tr);
                if (source.RoadNumber == 0 && !string.IsNullOrEmpty(source.RoadHandle))
                    source.RoadNumber = Number(OpenHandle(db, tr, source.RoadHandle), tr);
            }
        }

        internal static bool Assign(Document document, IList<VertexSettingSource> sources, bool junctions,
            double groupingDistance, string identityMode, int specifiedNumber)
        {
            List<List<VertexSettingSource>> groups;
            var unresolved = new List<List<VertexSettingSource>>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                ReadMetadata(document.Database, tr, sources);
                groups = junctions ? Groups(sources, groupingDistance) : sources.Select(s => new List<VertexSettingSource> { s }).ToList();
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                var roads = space.Cast<ObjectId>().Where(id => !id.IsErased).Select(id => tr.GetObject(id, OpenMode.ForRead, false) as Entity)
                    .Where(e => e != null && (e is Alignment || e is Polyline) && Number(e, tr) > 0).ToList();
                foreach (var group in groups)
                {
                    int number = 0;
                    if (identityMode == "Use specified road number") number = specifiedNumber;
                    else if (identityMode != "Pick owning road")
                    {
                        var linked = group.Where(s => s.RoadNumber > 0).Select(s => s.RoadNumber).Distinct().ToList();
                        if (linked.Count == 1) number = linked[0];
                        else if (linked.Count == 0)
                        {
                            var ranked = roads.Select(r => new { Number = Number(r, tr), Distance = group.Average(s => Distance(r, Centre(s))) })
                                .Where(r => r.Distance <= groupingDistance).GroupBy(r => r.Number)
                                .Select(g => g.OrderBy(r => r.Distance).First()).OrderBy(r => r.Distance).ToList();
                            // At a shared cross-junction both roads may be equally close.
                            // Do not silently invent ownership from drawing/selection order.
                            if (ranked.Count > 0 && (ranked.Count == 1 || ranked[1].Distance - ranked[0].Distance > 0.05)) number = ranked[0].Number;
                        }
                    }
                    if (number == 0) unresolved.Add(group);
                    else foreach (var source in group) source.RoadNumber = number;
                }
            }
            foreach (var group in unresolved)
            {
                Point3d centre = Centre(group[0]);
                PromptEntityResult picked = document.Editor.GetEntity(string.Format(CultureInfo.InvariantCulture,
                    "\nSelect owning named road alignment/corridor/centreline for junction near ({0:0.##}, {1:0.##}): ", centre.X, centre.Y));
                if (picked.Status != PromptStatus.OK) return false;
                int number;
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                    number = Number(tr.GetObject(picked.ObjectId, OpenMode.ForRead, false) as Entity, tr);
                if (number <= 0)
                {
                    document.Editor.WriteMessage("\nThe selected object has no ROAD/RD number. Name the road or choose 'Use specified road number'. Nothing was changed.");
                    return false;
                }
                foreach (var source in group) source.RoadNumber = number;
            }
            return true;
        }

        internal static int Number(Entity entity, Transaction tr)
        {
            if (entity == null) return 0;
            string name = "";
            Alignment alignment = entity as Alignment;
            Corridor corridor = entity as Corridor;
            FeatureLine feature = entity as FeatureLine;
            if (alignment != null) name = alignment.Name;
            else if (corridor != null) name = corridor.Name;
            else if (feature != null) name = feature.Name;
            int number = RoadAnnotationPlan.RoadNumber(name);
            if (number > 0) return number;
            var layout = Record(entity, tr, "CE_ROAD_LAYOUT");
            if (layout != null && layout.Length >= 7) number = RoadAnnotationPlan.RoadNumber(Convert.ToString(layout[6].Value));
            var nameLink = Record(entity, tr, "CE_ROAD_NAME_LINK");
            if (number == 0 && nameLink != null && nameLink.Length >= 2) number = RoadAnnotationPlan.RoadNumber(Convert.ToString(nameLink[1].Value));
            if (number == 0 && entity is Polyline && layout != null && Convert.ToString(layout[0].Value) == "CENTER")
            {
                // Before this update CE_ROADNAMES stored the road name on its
                // linked label only. Read that exact parent link for old drawings.
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(entity.Database), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.IsErased) continue;
                    MText label = tr.GetObject(id, OpenMode.ForRead, false) as MText;
                    var labelLink = Record(label, tr, "CE_ROAD_LAYOUT");
                    if (labelLink == null || labelLink.Length < 7 || Convert.ToString(labelLink[0].Value) != "ROAD_NAME" ||
                        !string.Equals(Convert.ToString(labelLink[1].Value), entity.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                    number = RoadAnnotationPlan.RoadNumber(Convert.ToString(labelLink[6].Value));
                    if (number > 0) break;
                }
            }
            // A layer may hold several roads. Do not treat its digits as identity.
            return number;
        }

        private static double Distance(Entity road, Point3d point)
        {
            try
            {
                Alignment alignment = road as Alignment;
                if (alignment != null)
                {
                    double station = 0, offset = 0;
                    alignment.StationOffset(point.X, point.Y, ref station, ref offset);
                    return Math.Abs(offset);
                }
                Curve curve = road as Curve;
                if (curve != null) return curve.GetClosestPointTo(point, false).DistanceTo(point);
            }
            catch { }
            return double.MaxValue;
        }

        private static TypedValue[] Record(Entity entity, Transaction tr, string key)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return null;
            var dictionary = tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(key)) return null;
            var record = tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead) as Xrecord;
            return record == null || record.Data == null ? null : record.Data.AsArray();
        }

        private static Entity OpenHandle(Database db, Transaction tr, string handle)
        {
            long value;
            if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return null;
            try { return tr.GetObject(db.GetObjectId(false, new Handle(value), 0), OpenMode.ForRead, false) as Entity; }
            catch { return null; }
        }
    }
}
