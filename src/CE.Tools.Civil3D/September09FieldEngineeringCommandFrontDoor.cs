using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September09FieldEngineeringCommandFrontDoor))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Unique command registrations for the September 09 field-engineering pack.
    /// Existing CE_GRIDDIFFERENCE, CE_MULTIFILLET, CE_SURFACESLOPEARROWS and
    /// CE_SEWALIGN registrations are intentionally left with their established
    /// command owners and are rewired by the final staging pass.
    /// </summary>
    public sealed class September09FieldEngineeringCommandFrontDoor
    {
        [CommandMethod("CE_TOOLS", "CE_ROADRESERVECENTRELINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadReserveCentreLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            // The September 11 wrapper keeps the established reserve-detection engine,
            // then joins its new open centre segments and removes straight redundant
            // vertices without touching bend or T/X junction vertices.
            September11FieldCompletionRuntime.RoadReserveCentrePolylines(document);
        }

        [CommandMethod("CE_TOOLS", "CE_CONSTRUCTIONFILLET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConstructionFillet()
        {
            new September04FieldGeometryCompletionCommands().MultiFilletCommand();
        }

        [CommandMethod("CE_TOOLS", "CE_SLOPEANNOTATIONS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SlopeAnnotations()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September09FieldEngineeringRuntime.SlopeAnnotations(document);
        }
    }
}
