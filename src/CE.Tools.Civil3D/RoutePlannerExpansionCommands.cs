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

[assembly: CommandClass(typeof(CETools.Civil3D.RoutePlannerExpansionCommands))]

namespace CETools.Civil3D
{
    public sealed class RoutePlannerExpansionCommands
    {
        private const string RouteLink = "CE_ROUTE_FROM_ROAD_RESERVE";

        [CommandMethod("CE_TOOLS", "CE_ROUTEPLANNER", CommandFlags.Modal)]
        public void RoutePlanner()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Roads / Utility Route Planner",
                "Generate preliminary CAD routes first, then continue into native Civil 3D alignments, networks, profiles, schedules, BOQs and production drawings.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Generate road-reserve centrelines from cadastral layout", "CE_ROADRESERVECENTERLINES", "Create connected preliminary road centrelines between opposing erf/cadastral reserve boundaries, including different reserve widths.", "01 Road Reserve"),
                    new DisciplineWorkflowAction("Create Roads layout", "CE_ROADLAYOUTTOOLS", "Create road edges, shoulders, junctions, road names, dimensions and junction setting-out from the preliminary road centre geometry.", "01 Road Reserve"),
                    new DisciplineWorkflowAction("Create utility route from road-reserve centrelines", "CE_UTILITYFROMROADRESERVE", "Create Sewer/SW/Water/Bulk Water preliminary routes on or offset from existing CE road-reserve centrelines.", "02 Utility Option 1"),
                    new DisciplineWorkflowAction("Create Midblock sewer route + visible offsets", "CE_MIDBLOCKSEWERLAYOUT", "Create the open midblock sewer centre route plus both visible side-offset guides through all/selected cadastral blocks.", "03 Utility Option 2"),
                    new DisciplineWorkflowAction("Break routes at crossings / T-junctions", "CE_PLBREAKJUNCTIONS", "Prepare selected route polylines at true crossings and junctions.", "04 Preparation"),
                    new DisciplineWorkflowAction("Create Civil 3D networks from routes", "CE_NETWORKCREATEHUB", "Create Sewer/SW/Water/Bulk Water native Civil 3D networks from accepted preliminary route geometry.", "05 Civil 3D Production"),
                    new DisciplineWorkflowAction("Sewer production", "CE_SEWTOOLS", "Sequence, label, align, profile and schedule the sewer network.", "05 Civil 3D Production"),
                    new DisciplineWorkflowAction("Stormwater production", "CE_SWTOOLS", "Sequence, label, align, profile and schedule the stormwater network.", "05 Civil 3D Production"),
                    new DisciplineWorkflowAction("Water production", "CE_WATERTOOLS", "Sequence, label, align, profile and schedule the water network.", "05 Civil 3D Production"),
                    new DisciplineWorkflowAction("Bulk-water production", "CE_BULKWATERTOOLS", "Continue accepted routes into the bulk-water production workflow.", "05 Civil 3D Production"),
                    new DisciplineWorkflowAction("Network schedules / BOQs", "CE_NETWORKSCHEDULETOOLS", "Create linked native-network schedules and downstream BOQ output.", "06 Quantities")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_UTILITYFROMROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void UtilityFromRoadReserve()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Utility Route from Road Reserve",
                "Create connected preliminary utility routes from CE road-reserve centrelines. A zero offset follows the road centre; a signed side offset places the service route inside the reserve.");
            model.AddChoice("Discipline", "01 Route", "Discipline", "Sewer", "Select the downstream utility discipline.", new[] { "Sewer", "Stormwater", "Water", "Bulk Water" });
            model.AddChoice("Scope", "01 Route", "Road centrelines", "All", "Use all CE road-reserve centrelines or only selected road centrelines.", new[] { "All", "Selected" });
            model.AddText("Offset", "02 Geometry", "Signed lateral offset from road centreline", "0.000", "0 follows the road centre. Positive/negative values choose opposite sides using AutoCAD offset orientation.");
            model.AddChoice("Replace", "03 Output", "Existing generated routes", "Replace same discipline", "Replace prior CE routes for this discipline or keep them.", new[] { "Replace same discipline", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double offset;
            if (!TryNumber(model.Text("Offset"), out offset))
            {
                document.Editor.WriteMessage("\nCE_UTILITYFROMROADRESERVE cancelled. Offset must be numeric.");
                return;
            }
            List<ObjectId> sourceIds = ResolveRoadCentrelines(document, model.Text("Scope"));
            if (sourceIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_UTILITYFROMROADRESERVE: no CE road-reserve centrelines were found. Run CE_ROADRESERVECENTERLINES first.");
                return;
            }
            string discipline = model.Text("Discipline");
            string layerName = DisciplineLayer(discipline);
            int created = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                if (space == null) return;
                if (string.Equals(model.Text("Replace"), "Replace same discipline", StringComparison.OrdinalIgnoreCase))
                    EraseGenerated(space, transaction, discipline);
                foreach (ObjectId sourceId in sourceIds)
                {
                    Polyline source;
                    try { source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Polyline; } catch { skipped++; continue; }
                    if (source == null || source.Length <= 1e-7) { skipped++; continue; }
                    if (Math.Abs(offset) <= 1e-9)
                    {
                        Polyline clone = ClonePolyline(source);
                        clone.SetDatabaseDefaults(document.Database);
                        clone.LayerId = layerId;
                        space.AppendEntity(clone);
                        transaction.AddNewlyCreatedDBObject(clone, true);
                        WriteLink(clone, transaction, source, discipline, offset);
                        created++;
                        continue;
                    }
                    DBObjectCollection offsets;
                    try { offsets = source.GetOffsetCurves(offset); }
                    catch { skipped++; continue; }
                    bool local = false;
                    foreach (DBObject value in offsets)
                    {
                        Polyline route = value as Polyline;
                        if (route == null) { value.Dispose(); continue; }
                        route.SetDatabaseDefaults(document.Database);
                        route.LayerId = layerId;
                        space.AppendEntity(route);
                        transaction.AddNewlyCreatedDBObject(route, true);
                        WriteLink(route, transaction, source, discipline, offset);
                        created++;
                        local = true;
                    }
                    if (!local) skipped++;
                }
                transaction.Commit();
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_UTILITYFROMROADRESERVE complete. Discipline={0}; routes={1}; skipped={2}; layer={3}.", discipline, created, skipped, layerName);
        }

        private static List<ObjectId> ResolveRoadCentrelines(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                    selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect CE road-reserve centreline polylines: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                return selection.Value.GetObjectIds().Where(id => IsRoadCentre(document.Database, id)).ToList();
            }
            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return ids;
                foreach (ObjectId id in space)
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                    if (polyline != null && string.Equals(polyline.Layer, "CE-ROAD-CENTERLINE", StringComparison.OrdinalIgnoreCase)) ids.Add(id);
                }
            }
            return ids;
        }

        private static bool IsRoadCentre(Database database, ObjectId id)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                try
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    return polyline != null && string.Equals(polyline.Layer, "CE-ROAD-CENTERLINE", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        private static Polyline ClonePolyline(Polyline source)
        {
            var result = new Polyline(source.NumberOfVertices) { Closed = source.Closed, Elevation = source.Elevation, Normal = source.Normal };
            for (int index = 0; index < source.NumberOfVertices; index++)
                result.AddVertexAt(index, source.GetPoint2dAt(index), source.GetBulgeAt(index), source.GetStartWidthAt(index), source.GetEndWidthAt(index));
            return result;
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

        private static string DisciplineLayer(string discipline)
        {
            if (string.Equals(discipline, "Stormwater", StringComparison.OrdinalIgnoreCase)) return "CE-SW-ROUTE";
            if (string.Equals(discipline, "Water", StringComparison.OrdinalIgnoreCase)) return "CE-WATER-ROUTE";
            if (string.Equals(discipline, "Bulk Water", StringComparison.OrdinalIgnoreCase)) return "CE-BULK-WATER-ROUTE";
            return "CE-SEWER-ROUTE";
        }

        private static void WriteLink(Polyline route, Transaction transaction, Polyline source, string discipline, double offset)
        {
            if (route.ExtensionDictionary.IsNull) route.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(route.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RouteLink)) record = transaction.GetObject(dictionary.GetAt(RouteLink), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RouteLink, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, source.Handle.ToString()),
                new TypedValue((int)DxfCode.Text, discipline ?? "Sewer"),
                new TypedValue((int)DxfCode.Real, offset));
        }

        private static void EraseGenerated(BlockTableRecord space, Transaction transaction, string discipline)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Polyline route;
                try { route = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline; } catch { continue; }
                if (route == null || route.ExtensionDictionary.IsNull) continue;
                try
                {
                    DBDictionary dictionary = transaction.GetObject(route.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary == null || !dictionary.Contains(RouteLink)) continue;
                    Xrecord record = transaction.GetObject(dictionary.GetAt(RouteLink), OpenMode.ForRead, false) as Xrecord;
                    TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
                    if (values != null && values.Length > 1 && string.Equals(Convert.ToString(values[1].Value, CultureInfo.InvariantCulture), discipline, StringComparison.OrdinalIgnoreCase)) route.Erase();
                }
                catch { }
            }
        }

        private static bool TryNumber(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }
    }
}
