using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PolylineNetworkPreparationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Public command entry point for the field-safe crossing/T-junction engine.
    /// August 25 uses distance-based plan intersections and an atomic replacement
    /// transaction so a source cannot be erased unless every expected span exists.
    /// </summary>
    public sealed class PolylineNetworkPreparationCommands
    {
        // Retained only as a staging-compatibility anchor for the preserved
        // August 20 runtime-stability finalizer. CE_PLBREAKJUNCTIONS never calls
        // this legacy helper; runtime execution stays on the August 25 engine.
        private const double Tolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLBREAKJUNCTIONS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BreakAtAllCrossingsAndJunctions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            August25CadSupplementaryBreakEngine.Run(document);
        }

        private static void ApplySplits(
            Database database,
            IDictionary<ObjectId, List<Point3d>> splitPoints,
            out int replaced,
            out int created)
        {
            // Compatibility-only body. The August 20 staging finalizer replaces
            // this method with its validated per-source transaction implementation.
            // Keeping it inert in tracked source prevents accidental legacy use.
            replaced = 0;
            created = 0;
        }
    }
}
