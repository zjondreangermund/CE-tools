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

[assembly: CommandClass(typeof(CETools.Civil3D.MidblockSewerLayoutCommands))]

namespace CETools.Civil3D
{
    public sealed class MidblockSewerLayoutCommands
    {
        private const string LinkKey = "CE_MIDBLOCK_SEWER_LAYOUT";

        [CommandMethod("CE_TOOLS", "CE_MIDBLOCKSEWERLAYOUT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateMidblockLayout()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Midblock Sewer Layout",
                "Create an open midblock sewer centre route through each block plus visible parallel side-offset guides. This is the explicit Option 2 Midblock layout used before native sewer network creation.");
            model.AddChoice("Scope", "01 Blocks", "Cadastral blocks", "Selected", "Use selected closed lightweight polylines or all closed lightweight polylines in current model space.", new[] { "Selected", "All" });
            model.AddPositiveDouble("EndInset", "02 Centre Route", "End inset", 1.5, "Shorten the midblock centre route from each block end by this distance.");
            model.AddPositiveDouble("SideOffset", "03 Offset Guides", "Visible side offset from centre route", 1.5, "Create two parallel offset guide lines this distance from the midblock sewer centre route.");
            model.AddChoice("Direction", "02 Centre Route", "Route direction", "Automatic longest axis", "Use the longest block axis automatically or force horizontal/vertical centre routing.", new[] { "Automatic longest axis", "Horizontal", "Vertical" });
            model.AddChoice("Replace", "04 Output", "Existing CE midblock output", "Replace existing", "Replace previous CE midblock centre/offset output or retain it.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> blocks = ResolveBlocks(document, model.Text("Scope"));
            if (blocks.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_MIDBLOCKSEWERLAYOUT: no closed block/cadastral polylines were found.");
                return;
            }

            double endInset = Math.Max(0.0, model.Double("EndInset", 1.5));
            double sideOffset = Math.Max(0.001, model.Double("SideOffset", 1.5));
            int centres = 0;
            int offsets = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                ObjectId centreLayer = EnsureLayer(document.Database, transaction, "CE-SEWER-MIDBLOCK-CENTER");
                ObjectId offsetLayer = EnsureLayer(document.Database, transaction, "CE-SEWER-MIDBLOCK-OFFSET");
                if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                    EraseExisting(space, transaction);

                foreach (ObjectId blockId in blocks)
                {
                    Polyline block;
                    try { block = transaction.GetObject(blockId, OpenMode.ForRead, false) as Polyline; }
                    catch { skipped++; continue; }
                    if (block == null || !block.Closed || block.NumberOfVertices < 3) { skipped++; continue; }
                    Polyline centre = BuildCentreRoute(block, endInset, model.Text("Direction"));
                    if (centre == null || centre.Length <= 1e-7) { skipped++; continue; }
                    centre.SetDatabaseDefaults(document.Database);
                    centre.LayerId = centreLayer;
                    space.AppendEntity(centre);
                    transaction.AddNewlyCreatedDBObject(centre, true);
                    string group = Guid.NewGuid().ToString("N");
                    WriteLink(centre, transaction, block.Handle.ToString(), group, "CENTER", 0.0);
                    centres++;

                    foreach (double signed in new[] { -sideOffset, sideOffset })
                    {
                        DBObjectCollection curves;
                        try { curves = centre.GetOffsetCurves(signed); }
                        catch { continue; }
                        foreach (DBObject value in curves)
                        {
                            Curve curve = value as Curve;
                            if (curve == null) { value.Dispose(); continue; }
                            curve.SetDatabaseDefaults(document.Database);
                            curve.LayerId = offsetLayer;
                            space.AppendEntity(curve);
                            transaction.AddNewlyCreatedDBObject(curve, true);
                            WriteLink(curve, transaction, block.Handle.ToString(), group, "OFFSET", signed);
                            offsets++;
                        }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_MIDBLOCKSEWERLAYOUT complete. Midblock centre routes={0}; visible side offsets={1}; skipped={2}.", centres, offsets, skipped);
        }

        private static List<ObjectId> ResolveBlocks(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                {
                    selection = document.Editor.GetSelection(
                        new PromptSelectionOptions { MessageForAdding = "\nSelect closed cadastral/block polylines: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true },
                        new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
                }
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                return FilterClosed(document.Database, selection.Value.GetObjectIds());
            }

            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return ids;
                foreach (ObjectId id in space)
                {
                    try
                    {
                        Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (polyline != null && polyline.Closed && !IsGenerated(polyline, transaction)) ids.Add(id);
                    }
                    catch { }
                }
            }
            return ids;
        }

        private static List<ObjectId> FilterClosed(Database database, IEnumerable<ObjectId> input)
        {
            var ids = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in input)
                {
                    try
                    {
                        Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (polyline != null && polyline.Closed) ids.Add(id);
                    }
                    catch { }
                }
            }
            return ids;
        }

        private static Polyline BuildCentreRoute(Polyline source, double endInset, string direction)
        {
            Extents3d extents;
            try { extents = source.GeometricExtents; }
            catch { return null; }
            double width = extents.MaxPoint.X - extents.MinPoint.X;
            double height = extents.MaxPoint.Y - extents.MinPoint.Y;
            bool horizontal = string.Equals(direction, "Horizontal", StringComparison.OrdinalIgnoreCase) ||
                (!string.Equals(direction, "Vertical", StringComparison.OrdinalIgnoreCase) && width >= height);
            double available = horizontal ? width : height;
            double inset = Math.Min(Math.Max(0.0, endInset), Math.Max(0.0, available * 0.45));
            Point2d start;
            Point2d end;
            if (horizontal)
            {
                double y = (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5;
                start = new Point2d(extents.MinPoint.X + inset, y);
                end = new Point2d(extents.MaxPoint.X - inset, y);
            }
            else
            {
                double x = (extents.MinPoint.X + extents.MaxPoint.X) * 0.5;
                start = new Point2d(x, extents.MinPoint.Y + inset);
                end = new Point2d(x, extents.MaxPoint.Y - inset);
            }
            if (start.GetDistanceTo(end) <= 1e-7) return null;
            var route = new Polyline(2) { Closed = false, Elevation = source.Elevation, Normal = source.Normal };
            route.AddVertexAt(0, start, 0.0, 0.0, 0.0);
            route.AddVertexAt(1, end, 0.0, 0.0, 0.0);
            return route;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void WriteLink(Entity entity, Transaction transaction, string sourceHandle, string group, string kind, double offset)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            var record = new Xrecord
            {
                Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, sourceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, group ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, kind ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, offset))
            };
            dictionary.SetAt(LinkKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void EraseExisting(BlockTableRecord space, Transaction transaction)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.ExtensionDictionary.IsNull) continue;
                try
                {
                    DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkKey)) entity.Erase();
                }
                catch { }
            }
        }

        private static bool IsGenerated(Entity entity, Transaction transaction)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                return dictionary != null && dictionary.Contains(LinkKey);
            }
            catch { return false; }
        }
    }
}
