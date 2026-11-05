using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ScaleEachObjectCommands))]
namespace CETools.Civil3D
{
    public sealed class ScaleEachObjectCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SCALEEACH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ScaleEach()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Scale Each Selected Object",
                "Scale multiple selected entities individually about the bottom-centre or top-centre of each object's actual geometric extents. No shared base point is used.");
            settings.AddPositiveDouble("Factor", "01 Scaling", "Scale factor", 1.0,
                "Scale factor applied individually. 1.0 leaves the original size unchanged.");
            settings.AddChoice("Anchor", "01 Scaling", "Base point for each object", "Bottom centre",
                "Use each object's own bounding box, not a single shared point.",
                new[] { "Bottom centre", "Top centre" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            var options = new PromptSelectionOptions { MessageForAdding =
                "\nSelect multiple objects to scale individually (Ctrl/Shift/window): " };
            PromptSelectionResult picked = doc.Editor.GetSelection(options);
            if (picked.Status != PromptStatus.OK || picked.Value == null) return;
            double factor = settings.Double("Factor", 1);
            if (factor <= 0) return;
            int changed = 0, skipped = 0;
            using (DocumentLock locked = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in picked.Value.GetObjectIds())
                {
                    try
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (ent == null || ent.IsErased || ent is Autodesk.Civil.DatabaseServices.SectionView)
                        { skipped++; continue; }
                        Extents3d limits = ent.GeometricExtents;
                        Point3d anchor = new Point3d(
                            (limits.MinPoint.X + limits.MaxPoint.X) / 2.0,
                            settings.Text("Anchor") == "Top centre" ? limits.MaxPoint.Y : limits.MinPoint.Y,
                            (limits.MinPoint.Z + limits.MaxPoint.Z) / 2.0);
                        ent.TransformBy(Matrix3d.Scaling(factor, anchor));
                        changed++;
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_SCALEEACH completed. Scaled {0} objects, skipped {1} unsupported objects.",
                changed, skipped);
        }
    }
}
