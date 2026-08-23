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
    /// The previous implementation performed all replacements in one transaction
    /// and relied only on true 3D IntersectWith results. The safe engine analyses
    /// plan intersections first and replaces/verifies one source at a time.
    /// </summary>
    public sealed class PolylineNetworkPreparationCommands
    {
        // Retained only as a staging-compatibility anchor for the preserved
        // August 20 runtime-stability finalizer. CE_PLBREAKJUNCTIONS never calls
        // this legacy helper; runtime execution stays on August21SafePolylineBreakEngine.
        // The finalizer rewrites this body to its per-source transaction version
        // before its historical regression guard runs.
        private const double Tolerance = 0.000001;

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

        private static void ApplySplits(
            Database database,
            IDictionary<ObjectId, List<Point3d>> splitPoints,
            out int replaced,
            out int created)
        {
            // Compatibility-only body. The August 20 staging finalizer replaces
            // this method with its validated per-source transaction implementation.
            // Keeping it inert in tracked source prevents any accidental legacy use.
            replaced = 0;
            created = 0;
        }
    }
}
