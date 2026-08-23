using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PolylineNetworkPreparationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Public command entry point for the field-safe crossing/T-junction engine.
    /// The previous implementation performed all replacements in one transaction
    /// and relied only on true 3D IntersectWith results. The safe engine analyses
    /// plan intersections first and replaces/verifies one source at a time.
    /// </summary>
    public sealed class PolylineNetworkPreparationCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_PLBREAKJUNCTIONS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BreakAtAllCrossingsAndJunctions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            August21SafePolylineBreakEngine.Run(document);
        }
    }
}
