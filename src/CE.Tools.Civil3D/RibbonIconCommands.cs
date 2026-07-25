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
            var options = new PromptKeywordOptions(
                "\nCE Tools ribbon icons [TextOnly/Cached/Full] <" + RibbonVisuals.Mode + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("TextOnly");
            options.Keywords.Add("Cached");
            options.Keywords.Add("Full");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : RibbonVisuals.Mode.ToString();
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
                    "\nCE_RIBBONICONS set to {0}. Ribbon rebuilt={1}. Cached is the default for each Civil 3D session.",
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
