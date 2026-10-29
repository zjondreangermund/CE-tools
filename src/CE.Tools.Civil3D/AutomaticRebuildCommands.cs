using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AutomaticRebuildCommands))]
namespace CETools.Civil3D
{
    public sealed class AutomaticRebuildCommands
    {
        [CommandMethod("CE_TOOLS", "CE_AUTOMATICREBUILD", CommandFlags.Modal)]
        public void Configure()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            var civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Automatic Rebuild",
                "Set the native Rebuild - Automatic flags for all editable corridors and surfaces independently.");
            model.AddChoice("Corridors", "01 Corridors", "All corridors", "Leave unchanged", "Automatic rebuild after design edits.", new[] { "Leave unchanged", "ON", "OFF" });
            model.AddChoice("Surfaces", "02 Surfaces", "All surfaces", "Leave unchanged", "Automatic surface rebuild at the end of a command.", new[] { "Leave unchanged", "ON", "OFF" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            int corridors = 0, surfaces = 0, skipped = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (model.Text("Corridors") != "Leave unchanged")
                foreach (ObjectId id in civil.CorridorCollection)
                {
                    var corridor = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                    if (corridor == null || corridor.IsReferenceObject) { skipped++; continue; }
                    corridor.UpgradeOpen(); corridor.RebuildAutomatic = model.Text("Corridors") == "ON"; corridors++;
                }
                if (model.Text("Surfaces") != "Leave unchanged")
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (surface == null || surface.IsReferenceObject) { skipped++; continue; }
                    surface.UpgradeOpen(); surface.AutoRebuild = model.Text("Surfaces") == "ON"; surfaces++;
                }
                tr.Commit();
            }
            doc.Editor.WriteMessage("\nAutomatic rebuild set: corridors={0}; surfaces={1}; protected/skipped={2}.", corridors, surfaces, skipped);
        }
    }
}
