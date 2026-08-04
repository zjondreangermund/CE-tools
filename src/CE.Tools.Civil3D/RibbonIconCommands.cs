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
                "Text-only is the Civil 3D 2023 safe default. Cached and Full modes add generated icons and automatically fall back to text if rendering fails.",
                new System.Collections.Generic.List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Text-only ribbon", "TextOnly", "Maximum Civil 3D 2023 compatibility; all panels and commands remain visible.", "01 Recommended"),
                    new DisciplineWorkflowAction("Cached generated icons", "Cached", "Generate each icon once per session.", "02 Icons"),
                    new DisciplineWorkflowAction("Full generated icons", "Full", "Generate the complete visual icon set.", "02 Icons")
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
                    "\nCE_RIBBONICONS set to {0}. Ribbon rebuilt={1}. TextOnly is the safe default for each Civil 3D session.",
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
