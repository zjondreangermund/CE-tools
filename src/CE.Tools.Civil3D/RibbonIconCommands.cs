using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RibbonIconCommands))]

namespace CETools.Civil3D
{
    public sealed class RibbonIconCommands
    {
        [CommandMethod("CE_RIBBONICONS", CommandFlags.Modal)]
        public void ConfigureRibbonIcons()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            string choice = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Ribbon Display",
                "Full generated icons are the CE Tools default. Text-only remains available as a compatibility fallback.",
                new System.Collections.Generic.List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Full generated icons", "Full", "Generate the complete visual icon set; this is the default.", "01 Recommended"),
                    new DisciplineWorkflowAction("Cached generated icons", "Cached", "Generate each icon once per session.", "02 Compatibility"),
                    new DisciplineWorkflowAction("Text-only ribbon", "TextOnly", "Maximum compatibility if a Civil 3D display driver rejects generated icons.", "02 Compatibility")
                });
            if (string.IsNullOrWhiteSpace(choice)) return;
            RibbonIconMode mode = choice.Equals("TextOnly", StringComparison.OrdinalIgnoreCase)
                ? RibbonIconMode.TextOnly
                : choice.Equals("Full", StringComparison.OrdinalIgnoreCase)
                    ? RibbonIconMode.Full
                    : RibbonIconMode.Cached;
            RibbonVisuals.SetMode(mode);

            try
            {
                bool rebuilt = RibbonBuilder.EnsureCreated();
                TypicalDetailsRibbonExtension.EnsureCreated();
                editor.WriteMessage(
                    "\nCE_RIBBONICONS set to {0}. Ribbon rebuilt={1}. Full icons are the CE Tools session default.",
                    mode,
                    rebuilt);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_RIBBONICONS set to {0}, but the ribbon rebuild failed safely and text remains available. {1}: {2}",
                    mode,
                    exception.GetType().Name,
                    exception.Message);
            }
        }
    }
}
