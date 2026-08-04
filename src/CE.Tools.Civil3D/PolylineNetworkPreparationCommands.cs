using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PolylineNetworkPreparationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Prepares selected plan polylines for pipe-network conversion by replacing
    /// each source with independent curve segments at every true crossing and
    /// T-junction. The complete edit is one AutoCAD undo group/transaction.
    /// </summary>
    public sealed class PolylineNetworkPreparationCommands
    {
        private const double Tolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLBREAKJUNCTIONS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BreakAtAllCrossingsAndJunctions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            string path = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Break Polylines at Crossings and Junctions",
                "Select the preparation path. Source polylines are only replaced after the preview is accepted, and the complete operation can be undone once.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Select network path polylines",
                        "Select",
                        "Pick all sewer, stormwater, water or bulk-water route polylines in their intended path/selection order.",
                        "01 Path"),
                    new DisciplineWorkflowAction(
                        "Cancel without changes",
                        "Cancel",
                        "Close this preparation workflow without editing the drawing.",
                        "02 Cancel")
                });
            if (!string.Equals(path, "Select", StringComparison.OrdinalIgnoreCase)) return;

            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect all 2D/3D polylines to break at crossings and junctions: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE")
                }));
            if (selection.Status != PromptStatus.OK) return;

            List<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: select at least two intersecting polylines.");
                return;
            }

            Dictionary<ObjectId, List<Point3d>> splitPoints;
            int intersections;
            try
            {
                splitPoints = FindSplitPoints(
                    document.Database,
                    ids,
                    out intersections);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during intersection analysis. {0}",
                    exception.Message);
                return;
            }

            int affected = splitPoints.Count(item => item.Value.Count > 0);
            if (intersections == 0 || affected == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: no internal crossings or T-junctions were found. Endpoint-to-endpoint connections were retained.");
                return;
            }

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Polyline Junction Preview",
                "Accept to replace each affected source polyline with separate segments at every crossing/T-junction. Layers and curve properties are retained.",
                new List<KeyValuePair<string, string>>
                {
                    Pair("Selected polylines", ids.Count),
                    Pair("Unique crossing/junction locations", intersections),
                    Pair("Polylines to replace", affected)
                },
                "Break Polylines"))
                return;

            int replaced;
            int created;
            try
            {
                ApplySplits(document.Database, splitPoints, out replaced, out created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS failed. No partial edit was committed. {0}",
                    exception.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Source polylines replaced={0}; connected segments created={1}; junctions={2}.",
                replaced,
                created,
                intersections);
        }

        private static Dictionary<ObjectId, List<Point3d>> FindSplitPoints(
            Database database,
            IList<ObjectId> ids,
            out int uniqueIntersections)
        {
            var result = ids.ToDictionary(id => id, id => new List<Point3d>());
            var all = new List<Point3d>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                for (int firstIndex = 0; firstIndex < ids.Count; firstIndex++)
                {
                    Curve first = transaction.GetObject(
                        ids[firstIndex], OpenMode.ForRead, false) as Curve;
                    if (first == null) continue;
                    for (int secondIndex = firstIndex + 1;
                         secondIndex < ids.Count;
                         secondIndex++)
                    {
                        Curve second = transaction.GetObject(
                            ids[secondIndex], OpenMode.ForRead, false) as Curve;
                        if (second == null) continue;
                        var points = new Point3dCollection();
                        first.IntersectWith(
                            second,
                            Intersect.OnBothOperands,
                            points,
                            IntPtr.Zero,
                            IntPtr.Zero);
                        foreach (Point3d point in points)
                        {
                            AddInternalSplitPoint(first, point, result[ids[firstIndex]]);
                            AddInternalSplitPoint(second, point, result[ids[secondIndex]]);
                            AddUnique(all, point);
                        }
                    }
                }
            }
            uniqueIntersections = all.Count;
            return result;
        }

        private static void AddInternalSplitPoint(
            Curve curve,
            Point3d candidate,
            IList<Point3d> points)
        {
            Point3d point = curve.GetClosestPointTo(candidate, false);
            double distance = curve.GetDistAtPoint(point);
            double length = curve.GetDistanceAtParameter(curve.EndParam);
            if (distance <= Tolerance || length - distance <= Tolerance) return;
            AddUnique(points, point);
        }

        private static void AddUnique(IList<Point3d> points, Point3d point)
        {
            if (points.Any(existing => existing.DistanceTo(point) <= Tolerance)) return;
            points.Add(point);
        }

        private static void ApplySplits(
            Database database,
            IDictionary<ObjectId, List<Point3d>> splitPoints,
            out int replaced,
            out int created)
        {
            replaced = 0;
            created = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null)
                    throw new InvalidOperationException("The current drawing space is unavailable.");
                foreach (KeyValuePair<ObjectId, List<Point3d>> item in splitPoints)
                {
                    if (item.Value.Count == 0) continue;
                    Curve source = transaction.GetObject(
                        item.Key,
                        OpenMode.ForWrite,
                        false) as Curve;
                    if (source == null) continue;
                    Point3dCollection points = new Point3dCollection(
                        item.Value
                            .OrderBy(point => source.GetDistAtPoint(point))
                            .ToArray());
                    DBObjectCollection segments = source.GetSplitCurves(points);
                    if (segments == null || segments.Count < 2)
                        throw new InvalidOperationException(
                            "A selected polyline could not be split at its calculated junctions.");
                    foreach (DBObject value in segments)
                    {
                        Entity segment = value as Entity;
                        if (segment == null)
                        {
                            value.Dispose();
                            continue;
                        }
                        space.AppendEntity(segment);
                        transaction.AddNewlyCreatedDBObject(segment, true);
                        created++;
                    }
                    source.Erase();
                    replaced++;
                }
                transaction.Commit();
            }
        }

        private static KeyValuePair<string, string> Pair(string name, int value)
        {
            return new KeyValuePair<string, string>(
                name,
                value.ToString(CultureInfo.CurrentCulture));
        }
    }
}
