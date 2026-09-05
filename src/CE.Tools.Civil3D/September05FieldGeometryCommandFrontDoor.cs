using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(CETools.Civil3D.September05FieldGeometryCommandFrontDoor))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Registered command front doors for the September 05 field-geometry runtime.
    /// The implementation class is deliberately kept as a runtime/helper class so
    /// CE_CONNECTENDPOINTS remains registered only once by the August 27 command
    /// class while CE_MULTIFILLET and CE_GRIDDIFFERENCE are exposed here.
    /// </summary>
    public sealed class September05FieldGeometryCommandFrontDoor
    {
        [CommandMethod("CE_TOOLS", "CE_MULTIFILLET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MultiFillet()
        {
            new September04FieldGeometryCompletionCommands().MultiFilletCommand();
        }

        [CommandMethod("CE_TOOLS", "CE_GRIDDIFFERENCE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void GridDifference()
        {
            new September04FieldGeometryCompletionCommands().GridDifferenceCommand();
        }
    }
}
