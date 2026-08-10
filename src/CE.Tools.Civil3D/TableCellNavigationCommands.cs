using System;
using System.Collections.Generic;
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
            PromptEntityOptions options = new PromptEntityOptions("\nClick the data row/cell in a linked CE table: ");
            options.SetRejectMessage("\nSelect a CE Table object.");
            options.AddAllowedClass(typeof(Table), false);
            PromptEntityResult picked = document.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK) return;

            List<ObjectId> sources = LinkedTableSourceNavigator.Discover(document.Database, picked.ObjectId);
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_TABLECELLZOOM: this table has no discoverable live CE source handles.");
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
                        if (hit.Type == TableHitTestType.Cell)
                        {
                            row = hit.Row;
                            column = hit.Column;
                        }
                    }
                }
                catch { }
            }

            ObjectId[] target;
            int headerRows = Math.Max(0, rowCount - sources.Count);
            int sourceIndex = row - headerRows;
            if (row >= 0 && sourceIndex >= 0 && sourceIndex < sources.Count)
                target = new[] { sources[sourceIndex] };
            else
                target = sources.ToArray();

            document.Editor.SetImpliedSelection(target);
            ZoomTo(document, target);
            document.Editor.WriteMessage(
                "\nCE_TABLECELLZOOM: clicked row={0}, column={1}; selected/zoomed source objects={2}.",
                row,
                column,
                target.Length);
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
