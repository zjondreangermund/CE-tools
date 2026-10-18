using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceVisibilityCommands))]
namespace CETools.Civil3D
{
    /// <summary>Bulk object visibility; leaves styles, geometry and display settings intact.</summary>
    public sealed class SurfaceVisibilityCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURFACESON", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Show() { Set(true); }
        [CommandMethod("CE_TOOLS", "CE_SURFACESOFF", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Hide() { Set(false); }
        [CommandMethod("CE_TOOLS", "CE_SURFACEVISIBILITY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Popup()
        {
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Surface Visibility",
                "Control visibility of all Civil 3D surface objects without changing their styles or geometry.");
            settings.AddChoice("Visibility", "01 Surfaces", "Surface visibility",
                "Show all", "Changes the Civil 3D surface object's drawing visibility.",
                new[] { "Show all", "Hide all" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            Set(settings.Text("Visibility") == "Show all");
        }
        private static void Set(bool visible)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            int changed = 0, skipped = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    Surface surface = null;
                    try { surface = tr.GetObject(id, OpenMode.ForWrite, false) as Surface; }
                    catch { skipped++; continue; }
                    if (surface == null || surface.IsReferenceObject) { skipped++; continue; }
                    if (surface.Visible != visible) { surface.Visible = visible; changed++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE surface visibility: {0} changed; {1} reference/unavailable surfaces skipped.",
                changed, skipped);
        }
    }
}
