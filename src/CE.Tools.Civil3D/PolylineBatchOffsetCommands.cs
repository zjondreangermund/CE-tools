using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PolylineBatchOffsetCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Batch offset ordinary 2D polylines. Preserves source geometry and never
    /// silently erases or alters the selected roads / cadastral boundary lines.
    /// </summary>
    public sealed class PolylineBatchOffsetCommands
    {
        [CommandMethod("CE_TOOLS", "CE_POLYLINEOFFSET", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void OffsetPolylines()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var config = new ProductionSettingsDialogModel(
                "CE Tools - Offset Multiple Polylines",
                "Offset all drawing 2D polylines or selected polylines. Originals stay unchanged. For open polylines, left/right is relative to vertex direction; closed polygons depend on their winding.");
            config.AddChoice("Scope", "01 Selection", "Source polylines", "Multiple selected",
                "Select several polyline sources, or process every model-space 2D polyline.",
                new[] { "Multiple selected", "All model-space polylines" });
            config.AddPositiveDouble("Distance", "02 Offset", "Offset distance (m)", 1.5,
                "Actual offset distance in drawing units.");
            config.AddChoice("Side", "02 Offset", "Offset side", "Both",
                "For open polylines: left/right of vertex sequence. Closed-polyline offset direction depends on winding.",
                new[] { "Left", "Right", "Both" });
            config.AddChoice("Layer", "03 Output", "New object layer", "Same as source",
                "Keep source polyline layer, or move all new offset lines to CE-POLYLINE-OFFSETS.",
                new[] { "Same as source", "CE-POLYLINE-OFFSETS" });
            if (!DisciplineWorkflowDialogs.EditSettings(config)) return;
            var ids = new List<ObjectId>();
            if (config.Text("Scope") == "Multiple selected")
            {
                var selection = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple 2D polylines to offset: "
                };
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                });
                PromptSelectionResult chosen = doc.Editor.GetSelection(selection, filter);
                if (chosen.Status != PromptStatus.OK || chosen.Value == null) return;
                ids.AddRange(chosen.Value.GetObjectIds());
            }

            int created = 0;
            int skipped = 0;
            try
            {
                using (DocumentLock documentLock = doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    Database database = doc.Database;
                    BlockTable blocks = tr.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    BlockTableRecord space = tr.GetObject(
                        blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
                    if (config.Text("Scope") == "All model-space polylines")
                    {
                        foreach (ObjectId id in space)
                        {
                            if (id.ObjectClass != null &&
                                id.ObjectClass.DxfName == "LWPOLYLINE") ids.Add(id);
                        }
                    }
                    ObjectId outputLayer = ObjectId.Null;
                    if (config.Text("Layer") == "CE-POLYLINE-OFFSETS")
                    {
                        LayerTable layers = tr.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
                        if (layers.Has("CE-POLYLINE-OFFSETS")) outputLayer = layers["CE-POLYLINE-OFFSETS"];
                        else
                        {
                            layers.UpgradeOpen();
                            var record = new LayerTableRecord { Name = "CE-POLYLINE-OFFSETS" };
                            outputLayer = layers.Add(record);
                            tr.AddNewlyCreatedDBObject(record, true);
                        }
                    }
                    double distance = config.Double("Distance", 1.5);
                    double[] signs = config.Text("Side") == "Both" ? new[] { 1.0, -1.0 } :
                        config.Text("Side") == "Left" ? new[] { 1.0 } : new[] { -1.0 };
                    if (ids.Count > 20000)
                        throw new InvalidOperationException("Too many polylines. Select a smaller drawing area.");
                    foreach (ObjectId id in ids.Distinct())
                    {
                        Polyline source = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (source == null || source.IsErased || source.NumberOfVertices < 2) { skipped++; continue; }
                        foreach (double sign in signs)
                        {
                            DBObjectCollection offsets;
                            try { offsets = source.GetOffsetCurves(sign * distance); }
                            catch { skipped++; continue; }
                            foreach (DBObject item in offsets)
                            {
                                Entity output = item as Entity;
                                if (output == null) { item.Dispose(); continue; }
                                if (outputLayer.IsNull) output.LayerId = source.LayerId;
                                else output.LayerId = outputLayer;
                                space.AppendEntity(output);
                                tr.AddNewlyCreatedDBObject(output, true);
                                created++;
                            }
                        }
                    }
                    tr.Commit();
                }
                doc.Editor.Regen();
                doc.Editor.WriteMessage("\nCE_POLYLINEOFFSET created {0} offsets; skipped {1} failed inputs/directions.", created, skipped);
            }
            catch (System.Exception error)
            {
                doc.Editor.WriteMessage("\nCE_POLYLINEOFFSET aborted without committing: {0}", error.Message);
            }
        }
    }
}
