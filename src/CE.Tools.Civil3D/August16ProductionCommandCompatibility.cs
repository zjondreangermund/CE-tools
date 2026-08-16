using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August16ProductionCommandCompatibility))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Compatibility owners for historical Production Centre command names that
    /// survived after the underlying tools were renamed. Every bridge calls the
    /// current implementation directly; no placeholder/no-op commands live here.
    /// </summary>
    public sealed class August16ProductionCommandCompatibility
    {
        [CommandMethod("CE_TOOLS", "CE_BOQSTORMWATER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void StormwaterBoq()
        {
            new BillOfQuantitiesCommands().ExportStormwater();
        }

        [CommandMethod("CE_TOOLS", "CE_REPORTSTORMWATER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StormwaterReport()
        {
            new ProductionReportCommands().StormReport();
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWINGBOOKROAD", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadDrawingBook()
        {
            new ProductionReportCommands().CreateDrawingBook();
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODQUICK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FloodQuick()
        {
            // The current hydraulic review centre includes catchment/rational-flow
            // screening and the culvert review requested by the Flood workflow.
            new HydraulicReviewCommands().HydraulicTools();
        }

        [CommandMethod("CE_TOOLS", "CE_HYDROLOGYREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void HydrologyReview()
        {
            // Current terrain-hydrology centre: surface flow, catchment, hydrograph
            // comparison and cleanup.
            new SurfaceHydrologyCommands().HydrologyTools();
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADINGTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingGradingTools()
        {
            new ParkingDynamicGradingCommands().ParkingGradeTools();
        }

        [CommandMethod("CE_TOOLS", "CE_PARKQTYTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingQuantityTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Parking Quantities / Delivery",
                "Review current parking counts and linked option data, then create/export quantity information using live CE Tools commands.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Count parking bays",
                        "CE_PKCOUNT",
                        "Count current selected/drawing parking bays.",
                        "01 Quantity Review"),
                    new DisciplineWorkflowAction(
                        "Parking option information",
                        "CE_PARKOPTIONSINFO",
                        "Review the currently linked parking option and generated bay count.",
                        "01 Quantity Review"),
                    new DisciplineWorkflowAction(
                        "Optimised parking information",
                        "CE_PARKOPTINFO",
                        "Review linked optimiser data and current capacity.",
                        "01 Quantity Review"),
                    new DisciplineWorkflowAction(
                        "Linked BOQ tools",
                        "CE_BOQTOOLS",
                        "Create/refresh/export a drawing-linked BOQ when detailed parking/layerwork quantities are required.",
                        "02 BOQ / Export")
                });
        }
    }
}
