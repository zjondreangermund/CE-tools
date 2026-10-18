using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.TJunctionOppositeClosureCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch opposite-side T-mouth closure. The user identifies both kerb-edge
    /// endpoints at each T because proximity alone can connect the wrong road.
    /// Creates only line segments; source geometry is never trimmed or erased.
    /// </summary>
    public sealed class TJunctionOppositeClosureCommands
    {
        [CommandMethod("CE_TOOLS", "CE_TJUNCTIONCLOSEOPPOSITE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CloseTEnds()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Close Opposite Side of T-Junctions",
                "Close one or many selected T-junction openings with a line between the two kerb endpoints, as shown by the magenta line. Pick two endpoints per junction. Source kerbs and bellmouths remain unchanged.");
            settings.AddText("Layer", "01 Closure", "Closure layer", "CE-JUNCTION",
                "Existing or new output layer for the opposite-side T-junction closure.");
            settings.AddPositiveDouble("MaxWidth", "01 Closure", "Maximum allowed closure length", 40.0,
                "Safety check to prevent joining distant or unrelated roads.");
            settings.AddChoice("RunNames", "02 Finish", "Run Road Names after closing",
                "No", "Open the existing CE_ROADNAMES settings popup when closure work finishes.",
                new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            string layer = settings.Text("Layer");
            double maxWidth = settings.Double("MaxWidth", 40.0);
            int made = 0, duplicates = 0;
            while (true)
            {
                var start = new PromptPointOptions(
                    "\nPick FIRST T-junction opposite-side kerb endpoint [Enter to finish]: ");
                start.AllowNone = true;
                PromptPointResult first = document.Editor.GetPoint(start);
                if (first.Status == PromptStatus.None || first.Status == PromptStatus.Cancel) break;
                if (first.Status != PromptStatus.OK) break;
                var end = new PromptPointOptions(
                    "\nPick SECOND kerb endpoint across the same T-junction: ") {
                    BasePoint = first.Value, UseBasePoint = true
                };
                PromptPointResult second = document.Editor.GetPoint(end);
                if (second.Status != PromptStatus.OK) break;
                Point3d a = first.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                Point3d b = second.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                double distance = a.DistanceTo(b);
                if (distance < 0.001 || distance > maxWidth)
                {
                    document.Editor.WriteMessage("\nSkipped: closure length outside safety limits.");
                    continue;
                }
                using (document.LockDocument())
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    var bt = tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var layers = tr.GetObject(document.Database.LayerTableId, OpenMode.ForRead) as LayerTable;
                    if (bt == null || layers == null) return;
                    if (!layers.Has(layer))
                    {
                        layers.UpgradeOpen();
                        var record = new LayerTableRecord { Name = layer };
                        layers.Add(record);
                        tr.AddNewlyCreatedDBObject(record, true);
                    }
                    var space = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
                    if (space == null) return;
                    bool exists = false;
                    foreach (ObjectId id in space)
                    {
                        Line line = tr.GetObject(id, OpenMode.ForRead, false) as Line;
                        if (line == null || !string.Equals(line.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (line.StartPoint.DistanceTo(a) < 0.005 && line.EndPoint.DistanceTo(b) < 0.005 ||
                            line.StartPoint.DistanceTo(b) < 0.005 && line.EndPoint.DistanceTo(a) < 0.005)
                        { exists = true; break; }
                    }
                    if (exists) { duplicates++; continue; }
                    var closure = new Line(a, b);
                    closure.SetDatabaseDefaults(document.Database);
                    closure.Layer = layer;
                    space.AppendEntity(closure);
                    tr.AddNewlyCreatedDBObject(closure, true);
                    tr.Commit();
                    made++;
                }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_TJUNCTIONCLOSEOPPOSITE: closures created={0}, existing duplicates avoided={1}.",
                made, duplicates);
            if (string.Equals(settings.Text("RunNames"), "Yes", StringComparison.OrdinalIgnoreCase))
                document.SendStringToExecute("CE_ROADNAMES ", true, false, true);
        }
    }
}
