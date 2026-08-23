using System;
using System.Collections.Generic;
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
    /// Preserve-first multi-boundary drafting edits. Every target is processed in
    /// its own transaction. Crossing curves are split and fully classified before
    /// the original can be erased; if classification or replacement fails, that
    /// source is left untouched. Plan intersection fallbacks support ordinary Line
    /// and straight lightweight-Polyline geometry even when target/boundary Z values
    /// differ.
    /// </summary>
    public sealed class MultiBoundaryEditCommands
    {
        private const double Tol = 1e-7;
        private const double PlanTol = 1e-6;

        private sealed class PlanSegment
        {
            public Point3d Start;
            public Point3d End;
        }

        [CommandMethod("CE_TOOLS", "CE_BOUNDARYEDITTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Multiple Boundary Trim / Extend",
                "Preserve-first trim, trim-and-delete, extend and extend-and-delete operations against multiple closed polyline boundaries.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Trim outside multiple boundaries", "CE_TRIMOUTSIDEMULTI", "Keep crossing portions inside the selected boundaries and trim only the crossing outside portions. Wholly outside objects are retained.", "01 Trim"),
                    new DisciplineWorkflowAction("Trim inside multiple boundaries", "CE_TRIMINSIDEMULTI", "Keep crossing portions outside the selected boundaries and trim only the crossing inside portions. Wholly inside objects are retained.", "01 Trim"),
                    new DisciplineWorkflowAction("Trim + delete outside", "CE_TRIMDELETEOUTSIDEMULTI", "Keep every inside portion, trim crossing outside portions and delete curves proven wholly outside all selected boundaries.", "02 Trim and delete"),
                    new DisciplineWorkflowAction("Trim + delete inside", "CE_TRIMDELETEINSIDEMULTI", "Keep every outside portion, trim crossing inside portions and delete curves proven wholly inside a selected boundary.", "02 Trim and delete"),
                    new DisciplineWorkflowAction("Extend outside objects to boundaries", "CE_EXTENDOUTSIDEMULTI", "Extend eligible endpoints currently outside the boundaries to the nearest selected boundary; never delete an object.", "03 Extend"),
                    new DisciplineWorkflowAction("Extend inside objects to boundaries", "CE_EXTENDINSIDEMULTI", "Extend eligible endpoints currently inside a boundary to the nearest selected boundary; never delete an object.", "03 Extend"),
                    new DisciplineWorkflowAction("Extend + delete outside", "CE_EXTENDDELETEOUTSIDEMULTI", "Extend outside endpoints where possible; delete only a curve proven wholly outside when no valid boundary extension is available.", "04 Extend and delete"),
                    new DisciplineWorkflowAction("Extend + delete inside", "CE_EXTENDDELETEINSIDEMULTI", "Extend inside endpoints where possible; delete only a curve proven wholly inside when no valid boundary extension is available.", "04 Extend and delete")
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
            RunExtend(false, false, "CE_EXTENDOUTSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_EXTENDINSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExtendInside()
        {
            RunExtend(true, false, "CE_EXTENDINSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_EXTENDDELETEOUTSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExtendDeleteOutside()
        {
            RunExtend(false, true, "CE_EXTENDDELETEOUTSIDEMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_EXTENDDELETEINSIDEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExtendDeleteInside()
        {
            RunExtend(true, true, "CE_EXTENDDELETEINSIDEMULTI");
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
            int preserved = 0;
            HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId targetId in targets.Distinct().ToList())
                {
                    if (boundarySet.Contains(targetId) || targetId.IsNull || targetId.IsErased) continue;
                    bool committed = false;
                    try
                    {
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            BlockTableRecord space = transaction.GetObject(
                                document.Database.CurrentSpaceId,
                                OpenMode.ForWrite,
                                false) as BlockTableRecord;
                            List<Polyline> boundaries = OpenBoundaries(transaction, boundaryIds);
                            Curve curve = transaction.GetObject(
                                targetId,
                                OpenMode.ForWrite,
                                false) as Curve;
                            if (space == null || curve == null || curve.IsErased)
                            {
                                preserved++;
                                continue;
                            }
                            processed++;

                            List<Point3d> intersections = CollectIntersections(curve, boundaries);
                            if (intersections.Count == 0)
                            {
                                if (deleteWhollyUnwanted)
                                {
                                    bool inside;
                                    if (TryRegionConsensus(curve, boundaries, out inside))
                                    {
                                        bool keep = keepInside ? inside : !inside;
                                        if (!keep)
                                        {
                                            curve.Erase();
                                            deleted++;
                                        }
                                    }
                                    else
                                    {
                                        preserved++;
                                    }
                                }
                                transaction.Commit();
                                committed = true;
                                continue;
                            }

                            Point3dCollection splitPoints = SortAlongCurve(curve, intersections);
                            if (splitPoints.Count == 0)
                            {
                                preserved++;
                                continue;
                            }

                            DBObjectCollection pieces = null;
                            try { pieces = curve.GetSplitCurves(splitPoints); }
                            catch { }
                            if (pieces == null || pieces.Count < 2)
                            {
                                DisposePieces(pieces);
                                preserved++;
                                continue;
                            }

                            var keep = new List<Entity>();
                            var discard = new List<DBObject>();
                            bool classificationFailed = false;
                            foreach (DBObject value in pieces)
                            {
                                Curve piece = value as Curve;
                                if (piece == null)
                                {
                                    classificationFailed = true;
                                    discard.Add(value);
                                    continue;
                                }
                                bool inside;
                                if (!TryRegionConsensus(piece, boundaries, out inside))
                                {
                                    classificationFailed = true;
                                    discard.Add(value);
                                    continue;
                                }
                                bool wanted = keepInside ? inside : !inside;
                                if (wanted)
                                {
                                    Entity entity = piece as Entity;
                                    if (entity == null)
                                    {
                                        classificationFailed = true;
                                        discard.Add(value);
                                    }
                                    else
                                    {
                                        keep.Add(entity);
                                    }
                                }
                                else
                                {
                                    discard.Add(value);
                                }
                            }

                            if (classificationFailed)
                            {
                                DisposeDetached(keep.Cast<DBObject>().Concat(discard));
                                preserved++;
                                continue;
                            }

                            foreach (DBObject value in discard)
                                try { value.Dispose(); } catch { }

                            foreach (Entity entity in keep)
                            {
                                try { entity.SetPropertiesFrom(curve); } catch { }
                                space.AppendEntity(entity);
                                transaction.AddNewlyCreatedDBObject(entity, true);
                                try { entity.RecordGraphicsModified(true); } catch { }
                            }

                            // Source removal happens last, inside the same transaction.
                            // If any append/classification failed above, this line is never
                            // reached and AutoCAD leaves the original untouched.
                            curve.Erase();
                            split++;
                            keptPieces += keep.Count;
                            if (keep.Count == 0) deleted++;
                            transaction.Commit();
                            committed = true;
                        }
                    }
                    catch
                    {
                        preserved++;
                    }

                    if (!committed)
                    {
                        // The per-target transaction was aborted. No partial source
                        // replacement from this target survives.
                    }
                }
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\n{0} complete. Targets={1}; split originals={2}; kept pieces={3}; deleted originals/whole curves={4}; safely preserved/skipped={5}.",
                commandName,
                processed,
                split,
                keptPieces,
                deleted,
                preserved);
        }

        private static void RunExtend(bool endpointMustBeInside, bool deleteWhollyUnwanted, string commandName)
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
            int deleted = 0;
            int preserved = 0;
            HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId id in targets.Distinct())
                {
                    if (boundarySet.Contains(id) || id.IsNull || id.IsErased) continue;
                    try
                    {
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            List<Polyline> boundaries = OpenBoundaries(transaction, boundaryIds);
                            Curve curve = transaction.GetObject(id, OpenMode.ForWrite, false) as Curve;
                            if (curve == null || curve.Closed)
                            {
                                preserved++;
                                continue;
                            }
                            Line line = curve as Line;
                            Polyline polyline = curve as Polyline;
                            if (line == null && (polyline == null || polyline.NumberOfVertices < 2))
                            {
                                preserved++;
                                continue;
                            }

                            Point3d originalStart = curve.StartPoint;
                            Point3d originalEnd = curve.EndPoint;
                            bool startRegion = InsideAny(boundaries, originalStart);
                            bool endRegion = InsideAny(boundaries, originalEnd);
                            bool changed = false;
                            int changedEndpointsForObject = 0;

                            if (startRegion == endpointMustBeInside)
                            {
                                Vector3d direction = ExtensionDirection(curve, true);
                                Point3d target;
                                if (TryNearestBoundaryIntersection(
                                        curve,
                                        originalStart,
                                        direction,
                                        boundaries,
                                        out target))
                                {
                                    if (line != null) line.StartPoint = target;
                                    else polyline.SetPointAt(0, new Point2d(target.X, target.Y));
                                    changed = true;
                                    changedEndpointsForObject++;
                                }
                            }

                            if (endRegion == endpointMustBeInside)
                            {
                                Point3d currentEnd = curve.EndPoint;
                                Vector3d direction = ExtensionDirection(curve, false);
                                Point3d target;
                                if (TryNearestBoundaryIntersection(
                                        curve,
                                        currentEnd,
                                        direction,
                                        boundaries,
                                        out target))
                                {
                                    if (line != null) line.EndPoint = target;
                                    else polyline.SetPointAt(
                                        polyline.NumberOfVertices - 1,
                                        new Point2d(target.X, target.Y));
                                    changed = true;
                                    changedEndpointsForObject++;
                                }
                            }

                            if (changed)
                            {
                                try { curve.RecordGraphicsModified(true); } catch { }
                                objectsChanged++;
                                endpointsChanged += changedEndpointsForObject;
                                transaction.Commit();
                                continue;
                            }

                            if (deleteWhollyUnwanted &&
                                startRegion == endpointMustBeInside &&
                                endRegion == endpointMustBeInside &&
                                CollectIntersections(curve, boundaries).Count == 0)
                            {
                                bool inside;
                                if (TryRegionConsensus(curve, boundaries, out inside) &&
                                    inside == endpointMustBeInside)
                                {
                                    curve.Erase();
                                    deleted++;
                                    transaction.Commit();
                                    continue;
                                }
                            }

                            preserved++;
                            transaction.Commit();
                        }
                    }
                    catch
                    {
                        preserved++;
                    }
                }
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\n{0} complete. Objects extended={1}; endpoints extended={2}; safely deleted={3}; preserved/skipped={4}.",
                commandName,
                objectsChanged,
                endpointsChanged,
                deleted,
                preserved);
        }

        private static List<ObjectId> SelectBoundaries(Document document)
        {
            // Always select boundaries explicitly. PICKFIRST belongs to the user's
            // target workflow and must never silently become a protected boundary.
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect CLOSED polyline trimming boundary objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                filter);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3)
                        result.Add(id);
                }
            }
            if (result.Count == 0)
                document.Editor.WriteMessage("\nCE boundary edit cancelled. No closed lightweight-polyline boundaries were selected.");
            return result;
        }

        private static List<Polyline> OpenBoundaries(Transaction transaction, IEnumerable<ObjectId> ids)
        {
            var result = new List<Polyline>();
            foreach (ObjectId id in ids)
            {
                try
                {
                    Polyline boundary = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (boundary != null && boundary.Closed && boundary.NumberOfVertices >= 3)
                        result.Add(boundary);
                }
                catch { }
            }
            return result;
        }

        private static string AskScope(Document document, string title)
        {
            var model = new ProductionSettingsDialogModel(
                title,
                "Choose whether the boundary operation is applied to selected target objects only or every supported target in the current space. Boundary objects themselves are always protected.");
            model.AddChoice(
                "Scope",
                "Target objects",
                "Scope",
                "Selected",
                "Process selected targets or all supported curves in the current space.",
                new[] { "Selected", "All" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return string.Empty;
            return model.Text("Scope");
        }

        private static List<ObjectId> ResolveTargets(
            Document document,
            string scope,
            IList<ObjectId> boundaryIds,
            bool extend)
        {
            HashSet<ObjectId> boundarySet = new HashSet<ObjectId>(boundaryIds);
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                PromptSelectionResult selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = extend
                            ? "\nSelect Lines/open Polylines to extend: "
                            : "\nSelect curve objects to trim: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selection.Status != PromptStatus.OK || selection.Value == null)
                    return new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    return selection.Value.GetObjectIds().Where(id =>
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

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
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

        private static List<Point3d> CollectIntersections(
            Curve curve,
            IEnumerable<Polyline> boundaries)
        {
            var result = new List<Point3d>();
            foreach (Polyline boundary in boundaries)
            {
                Point3dCollection native = new Point3dCollection();
                try
                {
                    curve.IntersectWith(
                        boundary,
                        Intersect.OnBothOperands,
                        native,
                        IntPtr.Zero,
                        IntPtr.Zero);
                }
                catch { }
                foreach (Point3d point in native)
                    AddUniquePlan(result, point);

                AddPlanIntersections(curve, boundary, result);
            }
            return result;
        }

        private static void AddPlanIntersections(
            Curve curve,
            Polyline boundary,
            IList<Point3d> result)
        {
            List<PlanSegment> targetSegments = ReadStraightSegments(curve);
            List<PlanSegment> boundarySegments = ReadStraightSegments(boundary);
            foreach (PlanSegment targetSegment in targetSegments)
            {
                foreach (PlanSegment boundarySegment in boundarySegments)
                {
                    Point3d targetPoint;
                    if (TrySegmentIntersection(
                            targetSegment,
                            boundarySegment,
                            out targetPoint))
                        AddUniquePlan(result, targetPoint);
                }
            }
        }

        private static List<PlanSegment> ReadStraightSegments(Curve curve)
        {
            var result = new List<PlanSegment>();
            Line line = curve as Line;
            if (line != null)
            {
                if (PlanDistance(line.StartPoint, line.EndPoint) > Tol)
                    result.Add(new PlanSegment { Start = line.StartPoint, End = line.EndPoint });
                return result;
            }

            Polyline polyline = curve as Polyline;
            if (polyline == null || polyline.NumberOfVertices < 2) return result;
            int count = polyline.NumberOfVertices;
            int segmentCount = polyline.Closed ? count : count - 1;
            for (int index = 0; index < segmentCount; index++)
            {
                double bulge;
                try { bulge = polyline.GetBulgeAt(index); }
                catch { continue; }
                if (Math.Abs(bulge) > Tol) continue;
                Point3d start = polyline.GetPoint3dAt(index);
                Point3d end = polyline.GetPoint3dAt((index + 1) % count);
                if (PlanDistance(start, end) <= Tol) continue;
                result.Add(new PlanSegment { Start = start, End = end });
            }
            return result;
        }

        private static bool TrySegmentIntersection(
            PlanSegment first,
            PlanSegment second,
            out Point3d firstPoint)
        {
            firstPoint = Point3d.Origin;
            double rx = first.End.X - first.Start.X;
            double ry = first.End.Y - first.Start.Y;
            double sx = second.End.X - second.Start.X;
            double sy = second.End.Y - second.Start.Y;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= Tol) return false;
            double qx = second.Start.X - first.Start.X;
            double qy = second.Start.Y - first.Start.Y;
            double t = (qx * sy - qy * sx) / denominator;
            double u = (qx * ry - qy * rx) / denominator;
            if (t < -PlanTol || t > 1.0 + PlanTol ||
                u < -PlanTol || u > 1.0 + PlanTol)
                return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            firstPoint = new Point3d(
                first.Start.X + (first.End.X - first.Start.X) * t,
                first.Start.Y + (first.End.Y - first.Start.Y) * t,
                first.Start.Z + (first.End.Z - first.Start.Z) * t);
            return true;
        }

        private static void AddUniquePlan(IList<Point3d> points, Point3d point)
        {
            if (points.Any(existing => PlanDistance(existing, point) <= PlanTol)) return;
            points.Add(point);
        }

        private static Point3dCollection SortAlongCurve(
            Curve curve,
            IEnumerable<Point3d> points)
        {
            var values = new List<KeyValuePair<double, Point3d>>();
            double total = CurveLength(curve);
            if (total <= Tol) return new Point3dCollection();
            foreach (Point3d point in points)
            {
                try
                {
                    Point3d exact = curve.GetClosestPointTo(point, false);
                    double distance = curve.GetDistAtPoint(exact);
                    if (distance <= PlanTol || distance >= total - PlanTol) continue;
                    if (!values.Any(item => Math.Abs(item.Key - distance) <= PlanTol))
                        values.Add(new KeyValuePair<double, Point3d>(distance, exact));
                }
                catch { }
            }
            Point3dCollection result = new Point3dCollection();
            foreach (KeyValuePair<double, Point3d> item in values.OrderBy(item => item.Key))
                result.Add(item.Value);
            return result;
        }

        private static double CurveLength(Curve curve)
        {
            if (curve == null) return 0.0;
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                return Math.Abs(end - start);
            }
            catch { return 0.0; }
        }

        private static Point3d CurvePointAtFraction(Curve curve, double fraction)
        {
            double total = CurveLength(curve);
            if (total <= Tol) return curve.StartPoint;
            double distance = total * Math.Max(0.0, Math.Min(1.0, fraction));
            return curve.GetPointAtDist(distance);
        }

        private static bool TryRegionConsensus(
            Curve curve,
            IEnumerable<Polyline> boundaries,
            out bool inside)
        {
            inside = false;
            double[] fractions = { 0.15, 0.35, 0.5, 0.65, 0.85 };
            bool? state = null;
            try
            {
                foreach (double fraction in fractions)
                {
                    bool current = InsideAny(boundaries, CurvePointAtFraction(curve, fraction));
                    if (!state.HasValue) state = current;
                    else if (state.Value != current) return false;
                }
            }
            catch
            {
                return false;
            }
            if (!state.HasValue) return false;
            inside = state.Value;
            return true;
        }

        private static bool InsideAny(IEnumerable<Polyline> boundaries, Point3d point)
        {
            return boundaries.Any(boundary => PointInside(boundary, point));
        }

        private static bool PointInside(Polyline polygon, Point3d point)
        {
            if (polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3)
                return false;
            try
            {
                Point3d closest = polygon.GetClosestPointTo(point, false);
                if (PlanDistance(closest, point) <= PlanTol) return true;
            }
            catch { }

            bool inside = false;
            for (int i = 0, j = polygon.NumberOfVertices - 1;
                 i < polygon.NumberOfVertices;
                 j = i++)
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
                if (atStart)
                {
                    Vector3d direction = polyline.GetPoint3dAt(0) - polyline.GetPoint3dAt(1);
                    return direction.Length <= Tol ? -Vector3d.XAxis : direction.GetNormal();
                }
                Vector3d endDirection =
                    polyline.GetPoint3dAt(polyline.NumberOfVertices - 1) -
                    polyline.GetPoint3dAt(polyline.NumberOfVertices - 2);
                return endDirection.Length <= Tol ? Vector3d.XAxis : endDirection.GetNormal();
            }
            return atStart ? -Vector3d.XAxis : Vector3d.XAxis;
        }

        private static bool TryNearestBoundaryIntersection(
            Curve curve,
            Point3d endpoint,
            Vector3d extensionDirection,
            IEnumerable<Polyline> boundaries,
            out Point3d target)
        {
            target = Point3d.Origin;
            double best = double.MaxValue;
            bool found = false;
            foreach (Polyline boundary in boundaries)
            {
                Point3dCollection native = new Point3dCollection();
                try
                {
                    curve.IntersectWith(
                        boundary,
                        Intersect.ExtendThis,
                        native,
                        IntPtr.Zero,
                        IntPtr.Zero);
                }
                catch { }
                foreach (Point3d point in native)
                {
                    Vector3d vector = point - endpoint;
                    double distance = vector.Length;
                    if (distance <= PlanTol) continue;
                    if (vector.DotProduct(extensionDirection) <= PlanTol) continue;
                    if (distance >= best) continue;
                    best = distance;
                    target = point;
                    found = true;
                }

                foreach (PlanSegment segment in ReadStraightSegments(boundary))
                {
                    Point3d point;
                    double distance;
                    if (!TryRaySegmentIntersection(
                            endpoint,
                            extensionDirection,
                            segment,
                            out point,
                            out distance))
                        continue;
                    if (distance >= best) continue;
                    best = distance;
                    target = point;
                    found = true;
                }
            }
            return found;
        }

        private static bool TryRaySegmentIntersection(
            Point3d origin,
            Vector3d direction,
            PlanSegment segment,
            out Point3d point,
            out double distance)
        {
            point = Point3d.Origin;
            distance = double.MaxValue;
            double rx = direction.X;
            double ry = direction.Y;
            double sx = segment.End.X - segment.Start.X;
            double sy = segment.End.Y - segment.Start.Y;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= Tol) return false;
            double qx = segment.Start.X - origin.X;
            double qy = segment.Start.Y - origin.Y;
            double t = (qx * sy - qy * sx) / denominator;
            double u = (qx * ry - qy * rx) / denominator;
            if (t <= PlanTol || u < -PlanTol || u > 1.0 + PlanTol) return false;
            point = origin + direction * t;
            distance = t;
            return true;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static void DisposePieces(DBObjectCollection pieces)
        {
            if (pieces == null) return;
            foreach (DBObject value in pieces)
                try { value.Dispose(); } catch { }
        }

        private static void DisposeDetached(IEnumerable<DBObject> values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
                try { value.Dispose(); } catch { }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
