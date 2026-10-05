using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace CETools.Civil3D
{
    internal static class JunctionSurfaceVertices
    {
        internal static IList<CivilChoice> PickSurfaces(Document document, CivilDocument civil)
        {
            List<CivilChoice> choices = FieldCompletionBatchUi.ReadSurfaceChoices(document, civil);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                choices = choices.Where(item =>
                    {
                        var surface = transaction.GetObject(item.Id, OpenMode.ForRead, false) as CivilTinSurface;
                        return surface != null && !surface.IsReferenceObject;
                    })
                    .OrderByDescending(item => item.Name.StartsWith("TOP-RD-", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (choices.Count == 0)
            {
                document.Editor.WriteMessage("\nNo editable TIN road TOP surfaces were found. Create the road surfaces first.");
                return null;
            }
            return FieldCompletionBatchUi.PickMultiple("CE Tools - Select Road TOP Surfaces",
                "Select every road TOP surface that must receive the junction vertices. The first selected surface covering a point supplies its elevation; that control elevation is used on all selected surfaces covering the point.", choices);
        }

        internal static List<CivilSurface> OpenSurfaces(Transaction transaction, IEnumerable<CivilChoice> choices)
        {
            // These surfaces are elevation references only. The feature-line
            // correction workflow must never add vertices/breaklines or rebuild a
            // selected TOP surface.
            return choices.Select(item => transaction.GetObject(item.Id, OpenMode.ForRead, false) as CivilSurface)
                .Where(surface => surface != null).ToList();
        }

        // Only a straight cross-road connector qualifies. Curved bellmouths keep
        // their arc geometry; running this again must not keep subdividing them.
        internal static bool EnsureMidpoint(CivilFeatureLine line)
        {
            Point3dCollection points = line.GetPoints(FeatureLinePointType.PIPoint);
            if (line.Closed || points.Count != 2 || Math.Abs(line.GetBulge(0)) > 1e-9) return false;
            Point3d first = points[0];
            Point3d last = points[1];
            if (first.DistanceTo(last) < 0.002) return false;
            Point3d midpoint = first + (last - first) * 0.5;
            // A previous grade-break point may already occupy the same XY.
            foreach (Point3d point in line.GetPoints(FeatureLinePointType.AllPoints))
                if (Math.Abs(point.X - midpoint.X) < 0.001 && Math.Abs(point.Y - midpoint.Y) < 0.001) return false;
            line.InsertPIPoint(midpoint);
            return true;
        }

        internal static int Apply(CivilFeatureLine line, IList<CivilSurface> surfaces, out int unresolved)
        {
            if (line == null || surfaces == null || surfaces.Count == 0)
            {
                unresolved = 0;
                return 0;
            }

            // Surface-reference mode: update ONLY the selected feature line.
            // Civil 3D recalculates segment grades/slopes from the corrected
            // vertex elevations. No surface definition, vertex, breakline or
            // rebuild is touched by this helper.
            bool changed = September18RoadJunctionCompletionCommands.TryAssignFeatureLineElevations(
                line, surfaces, out unresolved);
            return changed ? line.GetPoints(FeatureLinePointType.AllPoints).Count : 0;
        }
    }
}
