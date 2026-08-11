using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.TableCellNavigationCommands))]

namespace CETools.Civil3D
{
    public sealed class TableCellNavigationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_TABLECELLZOOM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void TableCellZoom()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nClick a linked CE table (preferably the data row/cell you want to inspect): ");
            options.SetRejectMessage("\nSelect a CE Table object.");
            options.AddAllowedClass(typeof(Table), false);
            PromptEntityResult picked = document.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK) return;

            List<ObjectId> sources = LinkedTableSourceNavigator.Discover(document.Database, picked.ObjectId)
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            sources = FilterLiveEntities(document.Database, sources);
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_TABLECELLZOOM: this table has no discoverable live CE source entities.");
                return;
            }

            int row = -1;
            int column = -1;
            int rowCount = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(picked.ObjectId, OpenMode.ForRead, false) as Table;
                if (table == null) return;
                rowCount = table.Rows.Count;
                try
                {
                    using (ViewTableRecord view = document.Editor.GetCurrentView())
                    {
                        TableHitTestInfo hit = table.HitTest(picked.PickedPoint, view.ViewDirection);
                        if (hit != null && hit.Type == TableHitTestType.Cell)
                        {
                            row = hit.Row;
                            column = hit.Column;
                        }
                    }
                }
                catch
                {
                    // Civil 3D 2023 table hit-testing is not reliable for every
                    // transformed/annotative table. The popup below is the safe fallback.
                }
            }

            int headerRows = Math.Max(0, rowCount - sources.Count);
            int sourceIndex = row - headerRows;
            ObjectId preferred = sourceIndex >= 0 && sourceIndex < sources.Count ? sources[sourceIndex] : ObjectId.Null;
            ObjectId[] target = ChooseTarget(document, sources, preferred, row, column);
            if (target.Length == 0) return;

            try { document.Editor.SetImpliedSelection(target); }
            catch { }
            ZoomTo(document, target);
            document.Editor.WriteMessage(
                "\nCE_TABLECELLZOOM complete. Clicked row={0}, column={1}; selected/zoomed source objects={2}.",
                row,
                column,
                target.Length);
        }

        private static ObjectId[] ChooseTarget(Document document, IList<ObjectId> sources, ObjectId preferred, int row, int column)
        {
            if (sources == null || sources.Count == 0) return new ObjectId[0];
            if (sources.Count == 1) return new[] { sources[0] };

            var labels = new List<string> { "All linked source objects" };
            var map = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                for (int index = 0; index < sources.Count; index++)
                {
                    ObjectId id = sources[index];
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;
                    string label = Describe(entity, index + 1);
                    while (map.ContainsKey(label)) label += " · " + id.Handle.ToString();
                    labels.Add(label);
                    map[label] = id;
                }
            }
            if (map.Count == 0) return sources.ToArray();

            string defaultLabel = labels[1];
            if (!preferred.IsNull)
            {
                KeyValuePair<string, ObjectId> match = map.FirstOrDefault(pair => pair.Value == preferred);
                if (!string.IsNullOrWhiteSpace(match.Key)) defaultLabel = match.Key;
            }
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Table Source Navigation",
                "Choose the pipe, structure, feature line, alignment, profile or other live design element represented by this linked table. The clicked row is used as the default when Civil 3D can identify it.");
            model.AddChoice("Target", "01 Source", "Linked design element", defaultLabel, "Choose one source object or all linked objects.", labels);
            model.AddText("Clicked", "02 Information", "Clicked table cell", row >= 0 ? "Row " + row.ToString(CultureInfo.InvariantCulture) + ", Column " + column.ToString(CultureInfo.InvariantCulture) : "Cell not resolved - choose source below", "Civil 3D 2023 may not resolve the clicked cell for every transformed table; the source list remains available.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return new ObjectId[0];
            string selected = model.Text("Target");
            if (string.Equals(selected, "All linked source objects", StringComparison.OrdinalIgnoreCase)) return sources.ToArray();
            ObjectId idValue;
            return map.TryGetValue(selected, out idValue) ? new[] { idValue } : new ObjectId[0];
        }

        private static string Describe(Entity entity, int index)
        {
            string type = entity.GetType().Name;
            string layer = string.IsNullOrWhiteSpace(entity.Layer) ? "<no layer>" : entity.Layer;
            string name = ReadStringProperty(entity, "Name");
            string prefix = index.ToString(CultureInfo.InvariantCulture) + ". " + type;
            if (!string.IsNullOrWhiteSpace(name)) prefix += " · " + name;
            return prefix + " · Layer " + layer + " · Handle " + entity.Handle.ToString();
        }

        private static string ReadStringProperty(object target, string propertyName)
        {
            if (target == null) return string.Empty;
            try
            {
                System.Reflection.PropertyInfo property = target.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                return property == null ? string.Empty : Convert.ToString(property.GetValue(target, null), CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        private static List<ObjectId> FilterLiveEntities(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity != null) result.Add(id);
                }
            }
            return result;
        }

        private static void ZoomTo(Document document, IEnumerable<ObjectId> ids)
        {
            Extents3d? combined = null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased))
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;
                    try
                    {
                        Extents3d extents = entity.GeometricExtents;
                        if (!combined.HasValue) combined = extents;
                        else
                        {
                            Extents3d value = combined.Value;
                            value.AddExtents(extents);
                            combined = value;
                        }
                    }
                    catch { }
                }
            }
            if (!combined.HasValue) return;
            Extents3d bounds = combined.Value;
            Point3d centre = new Point3d(
                (bounds.MinPoint.X + bounds.MaxPoint.X) * 0.5,
                (bounds.MinPoint.Y + bounds.MaxPoint.Y) * 0.5,
                (bounds.MinPoint.Z + bounds.MaxPoint.Z) * 0.5);
            using (ViewTableRecord view = document.Editor.GetCurrentView())
            {
                double objectWidth = Math.Max(bounds.MaxPoint.X - bounds.MinPoint.X, 1.0) * 1.35;
                double objectHeight = Math.Max(bounds.MaxPoint.Y - bounds.MinPoint.Y, 1.0) * 1.35;
                double aspect = Math.Max(view.Width / Math.Max(view.Height, 1e-9), 1e-6);
                view.CenterPoint = new Point2d(centre.X, centre.Y);
                view.Width = Math.Max(objectWidth, objectHeight * aspect);
                view.Height = Math.Max(objectHeight, objectWidth / aspect);
                document.Editor.SetCurrentView(view);
            }
        }
    }
}
