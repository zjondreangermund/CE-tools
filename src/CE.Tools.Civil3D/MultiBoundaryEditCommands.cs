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

[assembly: CommandClass(typeof(CETools.Civil3D.MultiBoundaryEditCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Multi-boundary drafting edits for CE Drawing Tools. Trim operations work on
    /// AutoCAD Curve objects and never alter the selected closed boundary polylines.
    /// Delete variants additionally remove wholly unwanted curves that never cross a
    /// boundary. Extend operations safely support Lines and open lightweight
    /// Polylines, extending eligible endpoints to the nearest selected boundary.
    /// </summary>
    public sealed class MultiBoundaryEditCommands
    {
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_BOUNDARYEDITTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Multiple Boundary Trim / Extend",
                "Trim, trim-and-delete or extend many drawing curves against multiple closed polyline boundaries.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Trim outside multiple boundaries", "CE_TRIMOUTSIDEMULTI", "Keep curve portions inside the selected boundaries; crossing outside portions are trimmed.", "01 Trim"),
                    new DisciplineWorkflowAction("Trim inside multiple boundaries", "CE_TRIMINSIDEMULTI", "Keep curve portions outside the selected boundaries; crossing inside portions are trimmed.", "01 Trim"),
                    new DisciplineWorkflowAction("Trim + delete outside", "CE_TRIMDELETEOUTSIDEMULTI", "Trim crossing outside portions and also delete curves wholly outside all selected boundaries.", "02 Trim and delete"),
                    new DisciplineWorkflowAction("Trim + delete inside", "CE_TRIMDELETEINSIDEMULTI", "Trim crossing inside portions and also delete curves wholly inside a selected boundary.", "02 Trim and delete"),
                    new DisciplineWorkflowAction("Extend outside objects to boundaries", "CE_EXTENDOUTSIDEMULTI", "Extend eligible endpoints currently outside the boundaries to the nearest selected boundary.", "03 Extend"),
                    new DisciplineWorkflowAction("Extend inside objects to boundaries", "CE_EXTENDINSIDEMULTI", "Extend eligible endpoints currently inside a boundary to the nearest selected boundary.", "03 Extend")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_TRIMOUTSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimOutside()
        {
            RunTrim(true, false, "CE_TRIMOUTSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_TRIMINSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimInside()
        {
            RunTrim(false, false, "CE_TRIMINSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_TRIMDELETEOUTSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimDeleteOutside()
        {
            RunTrim(true, true, "CE_TRIMDELETEOUTSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_TRIMDELETEINSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimDeleteInside()
        {
            RunTrim(false, true, "CE_TRIMDELETEINSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_EXTENDOUTSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExtendOutside()
        {
            RunExtend(false, "CE_EXTENDOUTSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_EXTENDINSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExtendInside()
        {
            RunExtend(true, "CE_EXTENDINSIDEMULTI");
        }

        private static void RunTrim(bool keepInside, bool deleteWhollyUnwanted, string commandName)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ObjectId> boundaryIds = SelectBoundaries(document);
            if (boundaryIds.Count == 0) return;
            string scope = AskScope(document, commandName + " - Target Objects");
            if (string.IsNullOrWhiteSpace(scope)) return;
            List<ObjectId> targets = ResolveTargets(document, scope, boundaryIds, false);
            if (targets.Count == 0)
            {
                document.Editor.WriteMessage("\n{0}: no supported curve targets were selected/found.", commandName);
                return;
            }

            int processed = 0;
            int split = 0;
            int deleted = 0;
            int keptPieces = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                List<Polyline> boundaries = boundaryIds
                    .Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(polyline => polyline != null && polyline.Closed)
                    .ToList();
                HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);

                foreach (ObjectId targetId in targets.Distinct().ToList())
                {
                    if (boundarySet.Contains(targetId) || targetId.IsNull || targetId.IsErased) continue;
                    Curve curve;
                    try { curve = transaction.GetObject(targetId, OpenMode.ForWrite, false) as Curve; }
                    catch { skipped++; continue; }
                    if (curve == null) { skipped++; continue; }
                    processed++;

                    List<Point3d> intersections = CollectIntersections(curve, boundaries);
                    if (intersections.Count == 0)
                    {
                        if (!deleteWhollyUnwanted) continue;
                        Point3d probe;
                        try { probe = CurveMidPoint(curve); }
                        catch { skipped++; continue; }
                        bool inside = InsideAny(boundaries, probe);
                        bool keep = keepInside ? inside : !inside;
                        if (!keep)
                        {
                            curve.Erase();
                            deleted++;
                        }
                        continue;
                    }

                    Point3dCollection splitPoints = SortAlongCurve(curve, intersections);
                    if (splitPoints.Count == 0) continue;
                    DBObjectCollection pieces;
                    try { pieces = curve.GetSplitCurves(splitPoints); }
                    catch { skipped++; continue; }
                    int localKept = 0;
                    foreach (DBObject value in pieces)
                    {
                        Curve piece = value as Curve;
                        if (piece == null)
                        {
                            value.Dispose();
                            continue;
                        }
                        Point3d midpoint;
                        try { midpoint = CurveMidPoint(piece); }
                        catch { piece.Dispose(); continue; }
                        bool inside = InsideAny(boundaries, midpoint);
                        bool keep = keepInside ? inside : !inside;
                        if (!keep)
                        {
                            piece.Dispose();
                            continue;
                        }
                        space.AppendEntity(piece);
                        transaction.AddNewlyCreatedDBObject(piece, true);
                        localKept++;
                        keptPieces++;
                    }
                    curve.Erase();
                    split++;
                    if (localKept == 0) deleted++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\n{0} complete. Targets={1}; split originals={2}; kept pieces={3}; deleted originals/whole curves={4}; skipped={5}.",
                commandName,
                processed,
                split,
                keptPieces,
                deleted,
                skipped);
        }

        private static void RunExtend(bool endpointMustBeInside, string commandName)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ObjectId> boundaryIds = SelectBoundaries(document);
            if (boundaryIds.Count == 0) return;
            string scope = AskScope(document, commandName + " - Target Objects");
            if (string.IsNullOrWhiteSpace(scope)) return;
            List<ObjectId> targets = ResolveTargets(document, scope, boundaryIds, true);
            if (targets.Count == 0)
            {
                document.Editor.WriteMessage("\n{0}: no supported Line/open Polyline targets were selected/found.", commandName);
                return;
            }

            int objectsChanged = 0;
            int endpointsChanged = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<Polyline> boundaries = boundaryIds
                    .Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(polyline => polyline != null && polyline.Closed)
                    .ToList();
                HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);
                foreach (ObjectId id in targets.Distinct())
                {
                    if (boundarySet.Contains(id) || id.IsNull || id.IsErased) continue;
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForWrite, false) as Curve; }
                    catch { skipped++; continue; }
                    if (curve == null || curve.Closed) { skipped++; continue; }
                    Line line = curve as Line;
                    Polyline polyline = curve as Polyline;
                    if (line == null && (polyline == null || polyline.NumberOfVertices < 2))
                    {
                        skipped++;
                        continue;
                    }

                    bool changed = false;
                    Point3d start = curve.StartPoint;
                    Point3d end = curve.EndPoint;
                    bool startRegion = InsideAny(boundaries, start);
                    bool endRegion = InsideAny(boundaries, end);
                    if (startRegion == endpointMustBeInside)
                    {
                        Vector3d direction = ExtensionDirection(curve, true);
                        Point3d target;
                        if (TryNearestBoundaryIntersection(curve, start, direction, boundaries, out target))
                        {
                            if (line != null) line.StartPoint = target;
                            else polyline.SetPointAt(0, new Point2d(target.X, target.Y));
                            changed = true;
                            endpointsChanged++;
                        }
                    }
                    if (endRegion == endpointMustBeInside)
                    {
                        Vector3d direction = ExtensionDirection(curve, false);
                        Point3d target;
                        if (TryNearestBoundaryIntersection(curve, end, direction, boundaries, out target))
                        {
                            if (line != null) line.EndPoint = target;
                            else polyline.SetPointAt(polyline.NumberOfVertices - 1, new Point2d(target.X, target.Y));
                            changed = true;
                            endpointsChanged++;
                        }
                    }
                    if (changed) objectsChanged++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n{0} complete. Objects extended={1}; endpoints extended={2}; unsupported/skipped={3}.", commandName, objectsChanged, endpointsChanged, skipped);
        }

        private static List<ObjectId> SelectBoundaries(Document document)
        {
            PromptSelectionResult implied = document.Editor.SelectImplied();
            PromptSelectionResult selection;
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                selection = implied;
                document.Editor.SetImpliedSelection(new ObjectId[0]);
            }
            else
            {
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple CLOSED polyline boundary objects: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    filter);
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3) result.Add(id);
                }
            }
            if (result.Count == 0)
                document.Editor.WriteMessage("\nCE boundary edit cancelled. No closed lightweight-polyline boundaries were selected.");
            return result;
        }

        private static string AskScope(Document document, string title)
        {
            var model = new ProductionSettingsDialogModel(
                title,
                "Choose whether the boundary operation is applied to selected target objects only or every supported target in the current space. Boundary objects themselves are always protected.");
            model.AddChoice("Scope", "Target objects", "Scope", "Selected", "Process selected targets or all supported curves in the current space.", new[] { "Selected", "All" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return string.Empty;
            return model.Text("Scope");
        }

        private static List<ObjectId> ResolveTargets(Document document, string scope, IList<ObjectId> boundaryIds, bool extend)
        {
            HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = extend
                            ? "\nSelect Lines/open Polylines to extend: "
                            : "\nSelect curve objects to trim: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    return selection.Value.GetObjectIds().Where(id =>
                    {
                        if (boundarySet.Contains(id)) return false;
                        Curve curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                        if (curve == null) return false;
                        if (!extend) return true;
                        return curve is Line || (curve is Polyline && !curve.Closed);
                    }).ToList();
                }
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return new List<ObjectId>();
                return space.Cast<ObjectId>().Where(id =>
                {
                    if (boundarySet.Contains(id)) return false;
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { return false; }
                    if (curve == null) return false;
                    if (!extend) return true;
                    return curve is Line || (curve is Polyline && !curve.Closed);
                }).ToList();
            }
        }

        private static List<Point3d> CollectIntersections(Curve curve, IEnumerable<Polyline> boundaries)
        {
            var result = new List<Point3d>();
            foreach (Polyline boundary in boundaries)
            {
                Point3dCollection values = new Point3dCollection();
                try { curve.IntersectWith(boundary, Intersect.OnBothOperands, values, IntPtr.Zero, IntPtr.Zero); }
                catch { continue; }
                foreach (Point3d point in values)
                    if (!result.Any(existing => PlanDistance(existing, point) <= 1e-6)) result.Add(point);
            }
            return result;
        }

        private static Point3dCollection SortAlongCurve(Curve curve, IEnumerable<Point3d> points)
        {
            var values = new List<KeyValuePair<double, Point3d>>();
            foreach (Point3d point in points)
            {
                try
                {
                    Point3d exact = curve.GetClosestPointTo(point, false);
                    double distance = curve.GetDistAtPoint(exact);
                    if (distance <= 1e-6) continue;
                    double total = curve.GetDistanceAtParameter(curve.EndParam);
                    if (distance >= total - 1e-6) continue;
                    if (!values.Any(item => Math.Abs(item.Key - distance) <= 1e-6)) values.Add(new KeyValuePair<double, Point3d>(distance, exact));
                }
                catch { }
            }
            Point3dCollection result = new Point3dCollection();
            foreach (KeyValuePair<double, Point3d> item in values.OrderBy(item => item.Key)) result.Add(item.Value);
            return result;
        }

        private static Point3d CurveMidPoint(Curve curve)
        {
            double total = curve.GetDistanceAtParameter(curve.EndParam);
            if (total <= Tol) return curve.StartPoint;
            return curve.GetPointAtDist(total * 0.5);
        }

        private static bool InsideAny(IEnumerable<Polyline> boundaries, Point3d point)
        {
            return boundaries.Any(boundary => PointInside(boundary, point));
        }

        private static bool PointInside(Polyline polygon, Point3d point)
        {
            if (polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3) return false;
            Point3d closest;
            try
            {
                closest = polygon.GetClosestPointTo(point, false);
                if (PlanDistance(closest, point) <= 1e-7) return true;
            }
            catch { }
            bool inside = false;
            for (int i = 0, j = polygon.NumberOfVertices - 1; i < polygon.NumberOfVertices; j = i++)
            {
                Point2d a = polygon.GetPoint2dAt(i);
                Point2d b = polygon.GetPoint2dAt(j);
                bool intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) /
                    (Math.Abs(b.Y - a.Y) <= 1e-20 ? 1e-20 : b.Y - a.Y) + a.X;
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static Vector3d ExtensionDirection(Curve curve, bool atStart)
        {
            Line line = curve as Line;
            if (line != null)
            {
                Vector3d direction = line.EndPoint - line.StartPoint;
                if (direction.Length <= Tol) return Vector3d.XAxis;
                direction = direction.GetNormal();
                return atStart ? -direction : direction;
            }
            Polyline polyline = curve as Polyline;
            if (polyline != null && polyline.NumberOfVertices >= 2)
            {
                Point3d first;
                Point3d second;
                if (atStart)
                {
                    first = polyline.GetPoint3dAt(0);
                    second = polyline.GetPoint3dAt(1);
                    Vector3d direction = first - second;
                    return direction.Length <= Tol ? -Vector3d.XAxis : direction.GetNormal();
                }
                first = polyline.GetPoint3dAt(polyline.NumberOfVertices - 2);
                second = polyline.GetPoint3dAt(polyline.NumberOfVertices - 1);
                Vector3d endDirection = second - first;
                return endDirection.Length <= Tol ? Vector3d.XAxis : endDirection.GetNormal();
            }
            return atStart ? -Vector3d.XAxis : Vector3d.XAxis;
        }

        private static bool TryNearestBoundaryIntersection(Curve curve, Point3d endpoint, Vector3d extensionDirection, IEnumerable<Polyline> boundaries, out Point3d target)
        {
            target = Point3d.Origin;
            double best = double.MaxValue;
            bool found = false;
            foreach (Polyline boundary in boundaries)
            {
                Point3dCollection values = new Point3dCollection();
                try { curve.IntersectWith(boundary, Intersect.ExtendThis, values, IntPtr.Zero, IntPtr.Zero); }
                catch { continue; }
                foreach (Point3d point in values)
                {
                    Vector3d vector = point - endpoint;
                    double distance = vector.Length;
                    if (distance <= 1e-6) continue;
                    if (vector.DotProduct(extensionDirection) <= 1e-6) continue;
                    if (distance >= best) continue;
                    best = distance;
                    target = point;
                    found = true;
                }
            }
            return found;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
