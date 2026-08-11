using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11RoadNamingCurveCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.August11NetworkCloseCommands))]

namespace CETools.Civil3D
{
    public sealed class August11RoadNamingCurveCommands
    {
        private const string RoadNameLayer = "CE-ROAD-NAME";
        private const string NameLinkKey = "CE_ROAD_NAME_LINK";
        private const double Tol = 1e-8;

        [CommandMethod("CE_TOOLS", "CE_ROUTEHORIZONTALCURVES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void HorizontalCurves()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Horizontal Centreline Curves",
                "Apply tangent circular fillets to eligible line-line corners in multiple selected route/road polylines. Corners that cannot accommodate the specified radius are reduced conservatively rather than creating self-overlap.");
            model.AddPositiveDouble("Radius", "01 Geometry", "Horizontal curve radius", 10.0, "Requested centreline curve radius in drawing units.");
            model.AddChoice("Output", "02 Output", "Source handling", "Replace source polylines", "Replace selected source polylines or create curved copies on the same layer.", new[] { "Replace source polylines", "Create curved copies" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nSelect multiple route/road centreline polylines for horizontal curves: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double radius = Math.Max(0.001, model.Double("Radius", 10.0));
            bool replace = string.Equals(model.Text("Output"), "Replace source polylines", StringComparison.OrdinalIgnoreCase);
            int polylines = 0;
            int curves = 0;
            int reduced = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Polyline source;
                    try { source = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline; }
                    catch { continue; }
                    if (source == null || source.NumberOfVertices < 3) continue;
                    int localCurves;
                    int localReduced;
                    Polyline result = BuildFilletedPolyline(source, radius, out localCurves, out localReduced);
                    if (result == null || localCurves == 0)
                    {
                        if (result != null) result.Dispose();
                        continue;
                    }
                    result.SetDatabaseDefaults(document.Database);
                    result.LayerId = source.LayerId;
                    try { result.LinetypeId = source.LinetypeId; } catch { }
                    try { result.Color = source.Color; } catch { }
                    space.AppendEntity(result);
                    transaction.AddNewlyCreatedDBObject(result, true);
                    if (replace) source.Erase();
                    polylines++;
                    curves += localCurves;
                    reduced += localReduced;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROUTEHORIZONTALCURVES complete. Polylines processed={0}; tangent curves={1}; locally reduced radii={2}.", polylines, curves, reduced);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADNAMESYNC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadNameSync()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int changed = SyncRoadNames(document, true);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADNAMESYNC complete. Civil road objects renamed/linked={0}.", changed);
        }

        [CommandMethod("CE_TOOLS", "CE_UTILITYROUTEOFFSET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void UtilityRouteOffset()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Utility Route Offset from Erf / Road Reserve",
                "Create Stormwater, Sewer, Water or Bulk-Water preliminary route strings from selected erf boundaries, road-reserve edges or road centrelines.");
            model.AddChoice("Discipline", "01 Utility", "Discipline", "Sewer", "Output route discipline/layer.", new[] { "Stormwater", "Sewer", "Water", "Bulk Water" });
            model.AddChoice("Source", "01 Utility", "Selected source represents", "Road reserve edge", "Used in output metadata and workflow description.", new[] { "Erf boundary", "Road reserve edge", "Road centreline", "Other selected polyline" });
            model.AddPositiveDouble("Distance", "02 Offset", "Offset distance", 1.5, "Offset distance from selected source geometry.");
            model.AddChoice("Side", "02 Offset", "Offset side", "Both", "Create both sides or one geometric offset side.", new[] { "Both", "Positive", "Negative" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect multiple erf/road-reserve/road-centreline curves: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double distance = Math.Max(0.001, model.Double("Distance", 1.5));
            string discipline = model.Text("Discipline");
            string layerName = "CE-" + discipline.Replace(" ", "-").ToUpperInvariant() + "-ROUTE";
            string side = model.Text("Side");
            int created = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId layer = EnsureLayer(document.Database, transaction, layerName);
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve == null) continue;
                    IEnumerable<double> signs = string.Equals(side, "Positive", StringComparison.OrdinalIgnoreCase)
                        ? new[] { distance }
                        : string.Equals(side, "Negative", StringComparison.OrdinalIgnoreCase)
                            ? new[] { -distance }
                            : new[] { -distance, distance };
                    foreach (double signed in signs)
                    {
                        DBObjectCollection values;
                        try { values = curve.GetOffsetCurves(signed); }
                        catch { continue; }
                        foreach (DBObject value in values)
                        {
                            Entity output = value as Entity;
                            if (output == null) { value.Dispose(); continue; }
                            output.SetDatabaseDefaults(document.Database);
                            output.LayerId = layer;
                            space.AppendEntity(output);
                            transaction.AddNewlyCreatedDBObject(output, true);
                            WriteRouteLink(output, transaction, discipline, model.Text("Source"), signed);
                            created++;
                        }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_UTILITYROUTEOFFSET complete. {0} route strings created for {1}.", created, discipline);
        }

        internal static int SyncRoadNames(Document document, bool createLinks)
        {
            if (document == null) return 0;
            int changed = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                    if (space == null) return 0;
                    List<RoadLabel> labels = ReadRoadLabels(space, transaction);
                    if (labels.Count == 0) return 0;
                    var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId id in space)
                    {
                        Entity entity;
                        try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                        catch { continue; }
                        if (entity == null || entity.GetType().Name.IndexOf("Alignment", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Point3d centre;
                        try
                        {
                            Extents3d extents = entity.GeometricExtents;
                            centre = new Point3d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5, 0.0);
                        }
                        catch { continue; }
                        RoadLabel nearest = labels.OrderBy(label => PlanDistanceSquared(centre, label.Position)).First();
                        string oldName = ReadName(entity);
                        if (string.IsNullOrWhiteSpace(nearest.Name)) continue;
                        if (!string.Equals(oldName, nearest.Name, StringComparison.OrdinalIgnoreCase) && TryWriteName(entity, nearest.Name))
                        {
                            if (!string.IsNullOrWhiteSpace(oldName)) renames[oldName] = nearest.Name;
                            changed++;
                        }
                        if (createLinks) WriteNameLink(entity, transaction, nearest.SourceHandle, nearest.Name);
                    }

                    if (renames.Count > 0)
                    {
                        foreach (ObjectId id in space)
                        {
                            Entity entity;
                            try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                            catch { continue; }
                            if (entity == null) continue;
                            string type = entity.GetType().Name;
                            if (type.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) < 0 &&
                                type.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0 &&
                                type.IndexOf("Section", StringComparison.OrdinalIgnoreCase) < 0 &&
                                type.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string current = ReadName(entity);
                            if (string.IsNullOrWhiteSpace(current)) continue;
                            foreach (KeyValuePair<string, string> rename in renames)
                            {
                                if (current.IndexOf(rename.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string next = ReplaceIgnoreCase(current, rename.Key, rename.Value);
                                if (TryWriteName(entity, next)) changed++;
                                break;
                            }
                        }
                    }
                    transaction.Commit();
                }
            }
            catch { }
            return changed;
        }

        private static Polyline BuildFilletedPolyline(Polyline source, double requestedRadius, out int curves, out int reduced)
        {
            curves = 0;
            reduced = 0;
            if (source == null || source.NumberOfVertices < 3) return null;
            int n = source.NumberOfVertices;
            var result = new Polyline(n * 2 + 2) { Closed = source.Closed, Elevation = source.Elevation, Normal = source.Normal };
            int output = 0;
            Point2d first = source.GetPoint2dAt(0);
            result.AddVertexAt(output++, first, 0.0, 0.0, 0.0);
            int lastCorner = source.Closed ? n : n - 1;
            for (int i = 1; i < lastCorner; i++)
            {
                int prevIndex = (i - 1 + n) % n;
                int nextIndex = (i + 1) % n;
                if (Math.Abs(source.GetBulgeAt(prevIndex)) > Tol || Math.Abs(source.GetBulgeAt(i)) > Tol)
                {
                    result.AddVertexAt(output++, source.GetPoint2dAt(i), source.GetBulgeAt(i), 0.0, 0.0);
                    continue;
                }
                Point2d p0 = source.GetPoint2dAt(prevIndex);
                Point2d p1 = source.GetPoint2dAt(i);
                Point2d p2 = source.GetPoint2dAt(nextIndex);
                Vector2d rayBack = p0 - p1;
                Vector2d rayForward = p2 - p1;
                if (rayBack.Length <= Tol || rayForward.Length <= Tol)
                {
                    result.AddVertexAt(output++, p1, 0.0, 0.0, 0.0);
                    continue;
                }
                double dot = Math.Max(-1.0, Math.Min(1.0, rayBack.GetNormal().DotProduct(rayForward.GetNormal())));
                double interior = Math.Acos(dot);
                double turn = Math.PI - interior;
                if (turn <= 1e-5 || turn >= Math.PI - 1e-5)
                {
                    result.AddVertexAt(output++, p1, 0.0, 0.0, 0.0);
                    continue;
                }
                double tangent = requestedRadius / Math.Tan(interior * 0.5);
                double maximum = Math.Min(rayBack.Length, rayForward.Length) * 0.45;
                if (tangent > maximum)
                {
                    tangent = maximum;
                    reduced++;
                }
                if (tangent <= Tol)
                {
                    result.AddVertexAt(output++, p1, 0.0, 0.0, 0.0);
                    continue;
                }
                Point2d tangentIn = p1 + rayBack.GetNormal() * tangent;
                Point2d tangentOut = p1 + rayForward.GetNormal() * tangent;
                Vector2d incoming = p1 - p0;
                Vector2d outgoing = p2 - p1;
                double cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
                double bulge = Math.Tan(turn * 0.25) * (cross >= 0.0 ? 1.0 : -1.0);
                if (result.GetPoint2dAt(result.NumberOfVertices - 1).GetDistanceTo(tangentIn) > Tol)
                    result.AddVertexAt(output++, tangentIn, bulge, 0.0, 0.0);
                else result.SetBulgeAt(result.NumberOfVertices - 1, bulge);
                result.AddVertexAt(output++, tangentOut, 0.0, 0.0, 0.0);
                curves++;
            }
            if (!source.Closed)
            {
                Point2d last = source.GetPoint2dAt(n - 1);
                if (result.GetPoint2dAt(result.NumberOfVertices - 1).GetDistanceTo(last) > Tol)
                    result.AddVertexAt(output, last, 0.0, 0.0, 0.0);
            }
            return result;
        }

        private static List<RoadLabel> ReadRoadLabels(BlockTableRecord space, Transaction transaction)
        {
            var result = new List<RoadLabel>();
            foreach (ObjectId id in space)
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                string text = string.Empty;
                Point3d position = Point3d.Origin;
                DBText dbText = entity as DBText;
                MText mText = entity as MText;
                if (dbText != null) { text = dbText.TextString; position = dbText.Position; }
                else if (mText != null) { text = mText.Text; position = mText.Location; }
                else continue;
                if (!string.Equals(entity.Layer, RoadNameLayer, StringComparison.OrdinalIgnoreCase) &&
                    (text ?? string.Empty).IndexOf("ROAD", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string name = (text ?? string.Empty).Replace("\\P", " ").Trim();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(new RoadLabel(name, position, id.Handle.ToString()));
            }
            return result;
        }

        private static string ReadName(object target)
        {
            if (target == null) return string.Empty;
            try
            {
                PropertyInfo property = target.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                return property == null ? string.Empty : Convert.ToString(property.GetValue(target, null), CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        private static bool TryWriteName(object target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static void WriteNameLink(Entity entity, Transaction transaction, string labelHandle, string roadName)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(NameLinkKey)) record = transaction.GetObject(dictionary.GetAt(NameLinkKey), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(NameLinkKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, labelHandle ?? string.Empty), new TypedValue((int)DxfCode.Text, roadName ?? string.Empty));
        }

        private static void WriteRouteLink(Entity entity, Transaction transaction, string discipline, string sourceType, double offset)
        {
            const string key = "CE_UTILITY_ROUTE_OFFSET";
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            var record = new Xrecord
            {
                Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, discipline ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, sourceType ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, offset))
            };
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static double PlanDistanceSquared(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
        {
            int index = source.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return source;
            return source.Substring(0, index) + newValue + source.Substring(index + oldValue.Length);
        }

        private sealed class RoadLabel
        {
            internal RoadLabel(string name, Point3d position, string sourceHandle) { Name = name; Position = position; SourceHandle = sourceHandle; }
            internal string Name;
            internal Point3d Position;
            internal string SourceHandle;
        }
    }

    public sealed class August11NetworkCloseCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CLOSEPIPESONLY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ClosePipesOnly()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.Editor.WriteMessage("\nCE_CLOSEPIPESONLY: opening selected multi-part connection/closure workflow. This command never calls CE_BOQREFRESH.");
            document.SendStringToExecute("CE_NETWORKCONNECTSELECTED ", true, false, true);
        }
    }
}
