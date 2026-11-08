using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterProductionAdditionalCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Water-specific plan route preparation, junction breaks, native gravity
    /// network connections, and engineering data. Does not alter sewer networks.
    /// </summary>
    public sealed class WaterProductionAdditionalCommands
    {
        private static bool IsWater(CivilNetwork network)
        {
            if (network == null) return false;
            string name = network.Name ?? string.Empty;
            return name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [CommandMethod("CE_TOOLS", "CE_WATERROUTERESERVE",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void CreateWaterRoadReserveRoutes()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Water Road Reserve Routes",
                "Generate independent Water route polylines from selected road-reserve/centreline polylines. Original road geometry is preserved. Inspect the route side before network generation.");
            settings.AddPositiveDouble("Offset", "01 Route", "Offset from selected source (drawing units)", 1.0,
                "Use a positive lateral offset. Confirm the drawing's unit system and correct side.");
            settings.AddChoice("Side", "01 Route", "Offset side", "Left", "Left/right relative to polyline drawing direction.",
                new[] { "Left", "Right" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double offset = settings.Double("Offset", 1.0);
            if (string.Equals(settings.Text("Side"), "Right", StringComparison.OrdinalIgnoreCase))
                offset = -offset;
            PromptSelectionResult picked = doc.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect road-reserve/centreline polylines to offset into Water routes: ",
                AllowDuplicates = false
            });
            if (picked.Status != PromptStatus.OK || picked.Value == null) return;
            int made = 0, skipped = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layers = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;
                const string layerName = "CE-WATER-ROUTES";
                if (layers.Has(layerName)) layerId = layers[layerName];
                else
                {
                    layers.UpgradeOpen();
                    var layer = new LayerTableRecord { Name = layerName };
                    layerId = layers.Add(layer);
                    tr.AddNewlyCreatedDBObject(layer, true);
                }
                var model = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);
                foreach (ObjectId id in picked.Value.GetObjectIds())
                {
                    var source = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (source == null || source.Closed) { skipped++; continue; }
                    try
                    {
                        DBObjectCollection generated = source.GetOffsetCurves(offset);
                        foreach (DBObject entityObject in generated)
                        {
                            var route = entityObject as Entity;
                            if (route == null) { entityObject.Dispose(); continue; }
                            route.LayerId = layerId;
                            model.AppendEntity(route);
                            tr.AddNewlyCreatedDBObject(route, true);
                            made++;
                        }
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water route polylines created: " + made +
                "\nSources skipped: " + skipped +
                "\n\nRoad reserve geometry was retained. Review left/right offset before generating Water pipes.",
                "CE Tools - Water Routes");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERROUTEBREAK",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void BreakWaterRoutesAtCrossings()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Break Water Routes at Crossings",
                "Split the selected Water route polylines where they intersect each other. Original polylines are replaced only after all pieces for that polyline are successfully created.");
            settings.AddPositiveDouble("Tolerance", "01 Geometry", "Intersection tolerance (drawing units)", 0.001,
                "Used to collapse nearly identical crossing points. Endpoints are never split.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double tol = settings.Double("Tolerance", 0.001);
            PromptSelectionResult picked = doc.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect Water route polylines to split at intersections: ",
                AllowDuplicates = false
            });
            if (picked.Status != PromptStatus.OK || picked.Value == null) return;
            int split = 0, pieces = 0, skipped = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lines = picked.Value.GetObjectIds()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(line => line != null && !line.Closed)
                    .ToList();
                var model = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);
                for (int i = 0; i < lines.Count; i++)
                {
                    Polyline source = lines[i];
                    var cuts = new Point3dCollection();
                    for (int j = 0; j < lines.Count; j++)
                    {
                        if (j == i) continue;
                        var crossing = new Point3dCollection();
                        try
                        {
                            source.IntersectWith(lines[j], Intersect.OnBothOperands,
                                crossing, IntPtr.Zero, IntPtr.Zero);
                        }
                        catch { continue; }
                        foreach (Point3d point in crossing)
                        {
                            if (point.DistanceTo(source.StartPoint) < tol ||
                                point.DistanceTo(source.EndPoint) < tol) continue;
                            if (!cuts.Cast<Point3d>().Any(other => other.DistanceTo(point) < tol))
                                cuts.Add(point);
                        }
                    }
                    if (cuts.Count == 0) continue;
                    try
                    {
                        DBObjectCollection parts = source.GetSplitCurves(cuts);
                        if (parts == null || parts.Count < 2) { skipped++; continue; }
                        foreach (DBObject item in parts)
                        {
                            var entity = item as Entity;
                            if (entity == null) { item.Dispose(); continue; }
                            entity.LayerId = source.LayerId;
                            model.AppendEntity(entity);
                            tr.AddNewlyCreatedDBObject(entity, true);
                            pieces++;
                        }
                        source.UpgradeOpen();
                        source.Erase();
                        split++;
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water route polylines split: " + split +
                "\nNew segments: " + pieces + "\nSkipped: " + skipped +
                "\n\nReview T-junction topology before Water network generation.",
                "CE Tools - Water Route Break");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERCONNECTPARTS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ConnectWaterParts()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Connect Water Network Parts",
                "Connect Water pipes to network structures at coincident endpoints. Review connections before saving: this does not move existing pipe geometry.");
            settings.AddPositiveDouble("Tolerance", "01 Connections",
                "Maximum endpoint separation (drawing units)", 0.05,
                "Use a small tolerance. Endpoints beyond this distance are left untouched.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double tolerance = settings.Double("Tolerance", 0.05);
            int pipes = 0, connected = 0, errors = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (!IsWater(network)) continue;
                    var structures = network.GetStructureIds().Cast<ObjectId>()
                        .Select(id => tr.GetObject(id, OpenMode.ForRead, false) as CivilStructure)
                        .Where(value => value != null && !value.IsReferenceObject).ToList();
                    foreach (ObjectId id in network.GetPipeIds())
                    {
                        var pipe = tr.GetObject(id, OpenMode.ForWrite, false) as CivilPipe;
                        if (pipe == null || pipe.IsReferenceObject) continue;
                        pipes++;
                        foreach (bool start in new[] { true, false })
                        {
                            Point3d endpoint = start ? pipe.StartPoint : pipe.EndPoint;
                            var match = structures
                                .Select(item => new { Item = item, Distance = item.Position.DistanceTo(endpoint) })
                                .OrderBy(item => item.Distance).FirstOrDefault();
                            if (match == null || match.Distance > tolerance) continue;
                            try
                            {
                                pipe.ConnectToStructure(
                                    start ? ConnectorPositionType.Start : ConnectorPositionType.End,
                                    match.Item.ObjectId, true);
                                connected++;
                            }
                            catch { errors++; }
                        }
                    }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water pipes inspected: " + pipes + "\nEndpoint connections applied: " + connected +
                "\nConnection failures: " + errors +
                "\n\nReview network connections and invert elevations.",
                "CE Tools - Connect Water Parts");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILESPLIT",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SplitWaterProfileViews()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.SendStringToExecute("_.CE_ROADPROFILEVIEWSPLIT\n", true, false, false);
        }

        [CommandMethod("CE_TOOLS", "CE_WATERDESIGNDATAEXPORT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportWaterDesignData()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var pipes = new List<IList<string>>
            {
                new List<string> { "NETWORK", "PIPE", "START X", "START Y", "START Z",
                    "END X", "END Y", "END Z", "LENGTH 3D (drawing units)", "INNER DIAMETER (drawing units)" }
            };
            var structures = new List<IList<string>>
            {
                new List<string> { "NETWORK", "STRUCTURE", "X", "Y", "Z" }
            };
            Func<double, string> fmt = value => value.ToString("0.###", CultureInfo.InvariantCulture);
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (!IsWater(network)) continue;
                    foreach (ObjectId id in network.GetPipeIds())
                    {
                        CivilPipe pipe = tr.GetObject(id, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        string diameter = "";
                        try
                        {
                            var property = pipe.GetType().GetProperty("InnerDiameterOrWidth");
                            if (property != null)
                                diameter = Convert.ToString(property.GetValue(pipe, null), CultureInfo.InvariantCulture);
                        }
                        catch { }
                        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
                        pipes.Add(new List<string> { network.Name, pipe.Name,
                            fmt(a.X), fmt(a.Y), fmt(a.Z),
                            fmt(b.X), fmt(b.Y), fmt(b.Z),
                            fmt((b-a).Length), diameter });
                    }
                    foreach (ObjectId id in network.GetStructureIds())
                    {
                        CivilStructure part = tr.GetObject(id, OpenMode.ForRead, false) as CivilStructure;
                        if (part == null) continue;
                        Point3d p = part.Position;
                        structures.Add(new List<string> { network.Name, part.Name,
                            fmt(p.X), fmt(p.Y), fmt(p.Z) });
                    }
                }
            }
            if (pipes.Count < 2 && structures.Count < 2)
            {
                System.Windows.Forms.MessageBox.Show("No Water network parts found.",
                    "CE Tools - Water Design Export");
                return;
            }
            using (var save = new System.Windows.Forms.SaveFileDialog())
            {
                save.Title = "Export Water Design Data";
                save.Filter = "Excel workbook (*.xlsx)|*.xlsx";
                save.FileName = "CE-Tools-Water-Design-Data.xlsx";
                if (save.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    SimpleXlsxWriter.Write(save.FileName, new List<XlsxSheet>
                    {
                        new XlsxSheet("Water Pipes", pipes),
                        new XlsxSheet("Water Structures", structures)
                    });
                    System.Diagnostics.Process.Start(save.FileName);
                }
                catch (System.Exception error)
                {
                    System.Windows.Forms.MessageBox.Show(error.Message,
                        "CE Tools - Water Design Export");
                }
            }
        }
    }
}
