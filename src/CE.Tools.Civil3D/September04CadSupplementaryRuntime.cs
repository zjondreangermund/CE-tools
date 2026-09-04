using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    /// <summary>
    /// September 04 field correction for CAD/Survey supplementary geometry.
    /// Construction offsets and centre construction output are true AutoCAD XLINE
    /// entities. Junction breaking keeps the selected lightweight polyline object
    /// (and therefore its handle/links) as the first split span and creates the
    /// remaining spans as lightweight polylines; the source is never erased.
    /// </summary>
    internal static class September04CadSupplementaryRuntime
    {
        private const double Tol = 0.000001;
        private const double JunctionTol = 0.00005;

        private sealed class StraightSegment
        {
            internal Point3d Start;
            internal Point3d End;
        }

        internal static void ConstructionOffsets(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Offset / Construction Offset",
                "Normal mode creates a normal offset. Construction mode creates true AutoCAD construction-line (XLINE) entities for each straight source section.");
            model.AddPositiveDouble("Distance", "01 Offset", "Offset distance", 1.0,
                "Drawing-unit offset distance.");
            model.AddChoice("Mode", "01 Offset", "Offset type", "Construction XLINE entities",
                "Construction mode creates infinite AutoCAD XLINE entities, not finite LINE or polyline geometry.",
                new[] { "Construction XLINE entities", "Normal offset" });
            model.AddChoice("Side", "01 Offset", "Offset side", "Pick side",
                "Pick side uses the picked point relative to each source direction. Left/right follows source direction.",
                new[] { "Pick side", "Left", "Right" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, lightweight polylines and/or Civil 3D feature lines to offset: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            Point3d? pickedSide = null;
            if (string.Equals(model.Text("Side"), "Pick side", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult point = editor.GetPoint("\nPick the required offset side: ");
                if (point.Status != PromptStatus.OK) return;
                pickedSide = point.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            }

            double distance = Math.Max(model.Double("Distance", 1.0), Tol);
            bool construction = string.Equals(
                model.Text("Mode"), "Construction XLINE entities", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            int skipped = 0;
            int failed = 0;

            foreach (ObjectId id in selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (source == null || source.IsErased || LayerLocked(transaction, source.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        BlockTableRecord owner = transaction.GetObject(
                            source.OwnerId.IsNull ? document.Database.CurrentSpaceId : source.OwnerId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (owner == null) { skipped++; continue; }

                        if (construction)
                        {
                            List<StraightSegment> sections = ReadStraightSegments(source);
                            if (sections.Count == 0) { skipped++; continue; }
                            double sign = ResolveSideSign(sections[0], model.Text("Side"), pickedSide);
                            var emitted = new List<Xline>();
                            foreach (StraightSegment section in sections)
                            {
                                Xline xline = CreateOffsetXline(document.Database, source, section, sign * distance);
                                if (xline == null) continue;
                                if (emitted.Any(existing => SameInfiniteLine(existing, xline)))
                                {
                                    xline.Dispose();
                                    continue;
                                }
                                owner.AppendEntity(xline);
                                transaction.AddNewlyCreatedDBObject(xline, true);
                                emitted.Add(xline);
                                created++;
                            }
                            if (emitted.Count == 0) skipped++;
                        }
                        else
                        {
                            List<StraightSegment> sections = ReadStraightSegments(source);
                            if (sections.Count == 0) { skipped++; continue; }
                            double sign = ResolveSideSign(sections[0], model.Text("Side"), pickedSide);
                            DBObjectCollection offsets = BuildNormalOffsets(source, sign * distance);
                            if (offsets == null || offsets.Count == 0)
                            {
                                DisposeUnowned(offsets);
                                skipped++;
                                continue;
                            }
                            foreach (DBObject value in offsets)
                            {
                                Entity output = value as Entity;
                                if (output == null)
                                {
                                    try { value.Dispose(); } catch { }
                                    continue;
                                }
                                ApplySourceProperties(document.Database, source, output);
                                owner.AppendEntity(output);
                                transaction.AddNewlyCreatedDBObject(output, true);
                                created++;
                            }
                        }
                        transaction.Commit();
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nOffset source left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_SURVEYCONSTRUCTIONOFFSET complete. Created={0}; skipped={1}; failed={2}; mode={3}.",
                created, skipped, failed,
                construction ? "true XLINE construction entities" : "normal offset");
        }

        internal static void MiddleConstructionLines(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Centre Construction Lines",
                "Create true AutoCAD XLINE construction entities midway between corresponding straight sections of each selected pair.");
            model.AddPositiveDouble("Maximum", "01 Geometry", "Maximum pair distance", 20.0,
                "A corresponding section pair is skipped when either end separation exceeds this value.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, polylines or Civil 3D feature lines in pairs for centre XLINEs: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct().ToArray();
            if (ids.Length < 2)
            {
                editor.WriteMessage("\nCE_SURVEYMIDCONSTRUCTION requires at least two source objects.");
                return;
            }

            double maximum = Math.Max(model.Double("Maximum", 20.0), Tol);
            int created = 0;
            int skipped = 0;

            for (int pair = 0; pair + 1 < ids.Length; pair += 2)
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity first = transaction.GetObject(ids[pair], OpenMode.ForRead, false) as Entity;
                        Entity second = transaction.GetObject(ids[pair + 1], OpenMode.ForRead, false) as Entity;
                        if (first == null || second == null ||
                            LayerLocked(transaction, first.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        List<StraightSegment> firstSections = ReadStraightSegments(first);
                        List<StraightSegment> secondSections = ReadStraightSegments(second);
                        int sectionCount = Math.Min(firstSections.Count, secondSections.Count);
                        if (sectionCount == 0) { skipped++; continue; }

                        BlockTableRecord owner = transaction.GetObject(
                            document.Database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (owner == null) { skipped++; continue; }

                        int pairCreated = 0;
                        var emitted = new List<Xline>();
                        for (int index = 0; index < sectionCount; index++)
                        {
                            StraightSegment a = firstSections[index];
                            StraightSegment b = AlignSecond(a, secondSections[index]);
                            double startSeparation = PlanDistance(a.Start, b.Start);
                            double endSeparation = PlanDistance(a.End, b.End);
                            if (Math.Max(startSeparation, endSeparation) > maximum)
                            {
                                skipped++;
                                continue;
                            }

                            Point3d middleStart = MidPoint(a.Start, b.Start);
                            Point3d middleEnd = MidPoint(a.End, b.End);
                            Vector3d direction = PlanVector(middleStart, middleEnd);
                            if (direction.Length <= Tol) { skipped++; continue; }

                            var xline = new Xline();
                            ApplySourceProperties(document.Database, first, xline);
                            xline.BasePoint = middleStart;
                            xline.UnitDir = direction.GetNormal();
                            if (emitted.Any(existing => SameInfiniteLine(existing, xline)))
                            {
                                xline.Dispose();
                                continue;
                            }
                            owner.AppendEntity(xline);
                            transaction.AddNewlyCreatedDBObject(xline, true);
                            emitted.Add(xline);
                            pairCreated++;
                            created++;
                        }
                        if (pairCreated == 0) skipped++;
                        transaction.Commit();
                    }
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage("\nCentre construction pair left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_SURVEYMIDCONSTRUCTION complete. True XLINE centre construction entities={0}; skipped/unpaired={1}.",
                created, skipped + (ids.Length % 2));
        }

        internal static void BreakPolylinesAtJunctions(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect lightweight polylines to break and KEEP at crossings/T-junctions: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                }));
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: select at least two lightweight polylines.");
                return;
            }

            var cuts = ids.ToDictionary(id => id, id => new List<double>());
            int junctions = 0;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var polylines = new Dictionary<ObjectId, Polyline>();
                    foreach (ObjectId id in ids)
                    {
                        Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (polyline != null && !polyline.Closed && polyline.NumberOfVertices >= 2)
                            polylines[id] = polyline;
                    }

                    ObjectId[] validIds = polylines.Keys.ToArray();
                    for (int firstIndex = 0; firstIndex < validIds.Length; firstIndex++)
                    {
                        for (int secondIndex = firstIndex + 1; secondIndex < validIds.Length; secondIndex++)
                        {
                            Polyline first = polylines[validIds[firstIndex]];
                            Polyline second = polylines[validIds[secondIndex]];
                            var pairLocations = new List<Point2d>();
                            CollectNativeIntersections(first, second, pairLocations);
                            CollectPlanStraightIntersections(first, second, pairLocations);
                            CollectEndpointTJunctions(first, second, pairLocations);

                            foreach (Point2d location in UniquePoints(pairLocations))
                            {
                                bool cutFirst = AddPlanCut(first, location, cuts[first.ObjectId]);
                                bool cutSecond = AddPlanCut(second, location, cuts[second.ObjectId]);
                                if (cutFirst || cutSecond) junctions++;
                            }
                        }
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during junction analysis. No selected polyline was changed. {0}",
                    exception.Message);
                return;
            }

            foreach (List<double> values in cuts.Values)
            {
                values.Sort();
                RemoveNearDuplicates(values);
            }

            List<ObjectId> affected = cuts.Where(pair => pair.Value.Count > 0)
                .Select(pair => pair.Key)
                .ToList();
            if (affected.Count == 0)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: no internal X crossings or T-junctions were found.");
                return;
            }

            int splitSources = 0;
            int newPieces = 0;
            int failed = 0;
            foreach (ObjectId id in affected)
            {
                try
                {
                    int created;
                    if (SplitPolylineKeepOriginal(document.Database, id, cuts[id], out created))
                    {
                        splitSources++;
                        newPieces += created;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nPolyline {0} was kept unchanged: {1}", id.Handle, exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Junction locations={0}; polylines split={1}; additional polyline pieces={2}; failed/unchanged={3}. Original source handles were kept; no source polyline was erased.",
                junctions, splitSources, newPieces, failed);
        }

        private static bool SplitPolylineKeepOriginal(
            Database database,
            ObjectId sourceId,
            IList<double> distances,
            out int newPieces)
        {
            newPieces = 0;
            if (distances == null || distances.Count == 0) return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline source = transaction.GetObject(sourceId, OpenMode.ForWrite, false) as Polyline;
                if (source == null || source.IsErased || source.Closed ||
                    source.NumberOfVertices < 2 || LayerLocked(transaction, source.LayerId))
                    return false;

                double length = source.Length;
                var splitPoints = new Point3dCollection();
                foreach (double distance in distances.Where(value => value > JunctionTol && value < length - JunctionTol).OrderBy(value => value))
                {
                    Point3d point = source.GetPointAtDist(distance);
                    if (!ContainsPlanPoint(splitPoints, point)) splitPoints.Add(point);
                }
                if (splitPoints.Count == 0) return false;

                DBObjectCollection values = null;
                try
                {
                    values = source.GetSplitCurves(splitPoints);
                    var pieces = new List<Polyline>();
                    if (values != null)
                    {
                        foreach (DBObject value in values)
                        {
                            Polyline piece = value as Polyline;
                            if (piece != null && piece.NumberOfVertices >= 2 && piece.Length > JunctionTol)
                                pieces.Add(piece);
                        }
                    }
                    if (pieces.Count < 2)
                        throw new InvalidOperationException("AutoCAD did not return at least two valid polyline split spans.");

                    pieces = pieces.OrderBy(piece => DistanceFromSourceStart(source, piece.StartPoint)).ToList();
                    BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null) throw new InvalidOperationException("Polyline owner space is unavailable.");

                    for (int index = 1; index < pieces.Count; index++)
                    {
                        Polyline piece = pieces[index];
                        ApplySourceProperties(database, source, piece);
                        owner.AppendEntity(piece);
                        transaction.AddNewlyCreatedDBObject(piece, true);
                        newPieces++;
                    }

                    // Keep the original database object and handle as the first piece.
                    // Do not erase/replace the source object.
                    ReplacePolylineGeometry(source, pieces[0]);
                    try { source.RecordGraphicsModified(true); } catch { }
                    transaction.Commit();
                    return true;
                }
                finally
                {
                    DisposeUnowned(values);
                }
            }
        }

        private static void ReplacePolylineGeometry(Polyline target, Polyline replacement)
        {
            if (target == null || replacement == null || replacement.NumberOfVertices < 2)
                throw new InvalidOperationException("Invalid first split span.");

            while (target.NumberOfVertices > 0)
                target.RemoveVertexAt(target.NumberOfVertices - 1);

            target.Normal = replacement.Normal;
            target.Elevation = replacement.Elevation;
            target.Thickness = replacement.Thickness;
            target.Closed = false;
            for (int index = 0; index < replacement.NumberOfVertices; index++)
            {
                target.AddVertexAt(
                    index,
                    replacement.GetPoint2dAt(index),
                    replacement.GetBulgeAt(index),
                    replacement.GetStartWidthAt(index),
                    replacement.GetEndWidthAt(index));
            }
        }

        private static double DistanceFromSourceStart(Polyline source, Point3d point)
        {
            try
            {
                Point3d closest = source.GetClosestPointTo(
                    new Point3d(point.X, point.Y, source.Elevation), false);
                return source.GetDistAtPoint(closest);
            }
            catch { return double.MaxValue; }
        }

        private static void CollectNativeIntersections(Polyline first, Polyline second, IList<Point2d> locations)
        {
            var points = new Point3dCollection();
            try
            {
                first.IntersectWith(second, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                foreach (Point3d point in points)
                    locations.Add(new Point2d(point.X, point.Y));
            }
            catch { }
        }

        private static void CollectPlanStraightIntersections(Polyline first, Polyline second, IList<Point2d> locations)
        {
            int firstCount = first.Closed ? first.NumberOfVertices : first.NumberOfVertices - 1;
            int secondCount = second.Closed ? second.NumberOfVertices : second.NumberOfVertices - 1;
            for (int firstSegment = 0; firstSegment < firstCount; firstSegment++)
            {
                if (!IsStraight(first, firstSegment)) continue;
                int firstNext = (firstSegment + 1) % first.NumberOfVertices;
                Point2d a = first.GetPoint2dAt(firstSegment);
                Point2d b = first.GetPoint2dAt(firstNext);
                for (int secondSegment = 0; secondSegment < secondCount; secondSegment++)
                {
                    if (!IsStraight(second, secondSegment)) continue;
                    int secondNext = (secondSegment + 1) % second.NumberOfVertices;
                    Point2d c = second.GetPoint2dAt(secondSegment);
                    Point2d d = second.GetPoint2dAt(secondNext);
                    Point2d intersection;
                    if (TryPlanSegmentIntersection(a, b, c, d, out intersection))
                        locations.Add(intersection);
                }
            }
        }

        private static void CollectEndpointTJunctions(Polyline first, Polyline second, IList<Point2d> locations)
        {
            AddEndpointIfOnOther(first.StartPoint, second, locations);
            AddEndpointIfOnOther(first.EndPoint, second, locations);
            AddEndpointIfOnOther(second.StartPoint, first, locations);
            AddEndpointIfOnOther(second.EndPoint, first, locations);
        }

        private static void AddEndpointIfOnOther(Point3d endpoint, Polyline host, IList<Point2d> locations)
        {
            var point = new Point2d(endpoint.X, endpoint.Y);
            try
            {
                Point3d closest = host.GetClosestPointTo(
                    new Point3d(endpoint.X, endpoint.Y, host.Elevation), false);
                if (PlanDistance(closest, endpoint) <= JunctionTol)
                    locations.Add(point);
            }
            catch { }
        }

        private static bool AddPlanCut(Polyline source, Point2d location, IList<double> distances)
        {
            try
            {
                Point3d probe = new Point3d(location.X, location.Y, source.Elevation);
                Point3d closest = source.GetClosestPointTo(probe, false);
                if (PlanDistance(closest, probe) > JunctionTol) return false;
                double distance = source.GetDistAtPoint(closest);
                double length = source.Length;
                if (distance <= JunctionTol || distance >= length - JunctionTol) return false;
                if (distances.Any(value => Math.Abs(value - distance) <= JunctionTol)) return false;
                distances.Add(distance);
                return true;
            }
            catch { return false; }
        }

        private static IEnumerable<Point2d> UniquePoints(IEnumerable<Point2d> values)
        {
            var result = new List<Point2d>();
            foreach (Point2d point in values)
            {
                if (result.Any(existing => PlanDistance(existing, point) <= JunctionTol)) continue;
                result.Add(point);
            }
            return result;
        }

        private static bool TryPlanSegmentIntersection(
            Point2d a, Point2d b, Point2d c, Point2d d, out Point2d intersection)
        {
            intersection = Point2d.Origin;
            double rx = b.X - a.X;
            double ry = b.Y - a.Y;
            double sx = d.X - c.X;
            double sy = d.Y - c.Y;
            double denominator = Cross(rx, ry, sx, sy);
            if (Math.Abs(denominator) <= Tol) return false;
            double qpx = c.X - a.X;
            double qpy = c.Y - a.Y;
            double t = Cross(qpx, qpy, sx, sy) / denominator;
            double u = Cross(qpx, qpy, rx, ry) / denominator;
            if (t < -JunctionTol || t > 1.0 + JunctionTol ||
                u < -JunctionTol || u > 1.0 + JunctionTol) return false;
            intersection = new Point2d(a.X + t * rx, a.Y + t * ry);
            return true;
        }

        private static bool IsStraight(Polyline polyline, int index)
        {
            try { return polyline.GetSegmentType(index) == SegmentType.Line; }
            catch { return Math.Abs(polyline.GetBulgeAt(index)) <= Tol; }
        }

        private static List<StraightSegment> ReadStraightSegments(Entity source)
        {
            var result = new List<StraightSegment>();
            Line line = source as Line;
            if (line != null)
            {
                if (PlanDistance(line.StartPoint, line.EndPoint) > Tol)
                    result.Add(new StraightSegment { Start = line.StartPoint, End = line.EndPoint });
                return result;
            }

            Polyline polyline = source as Polyline;
            if (polyline != null)
            {
                int count = polyline.NumberOfVertices;
                int segmentCount = polyline.Closed ? count : count - 1;
                for (int index = 0; index < segmentCount; index++)
                {
                    if (!IsStraight(polyline, index)) continue;
                    int next = (index + 1) % count;
                    Point3d start = polyline.GetPoint3dAt(index);
                    Point3d end = polyline.GetPoint3dAt(next);
                    if (PlanDistance(start, end) > Tol)
                        result.Add(new StraightSegment { Start = start, End = end });
                }
                return result;
            }

            CivilFeatureLine featureLine = source as CivilFeatureLine;
            if (featureLine != null && !featureLine.IsReferenceObject)
            {
                Point3dCollection values = featureLine.GetPoints(FeatureLinePointType.PIPoint);
                if (values == null || values.Count < 2) return result;
                int segmentCount = featureLine.Closed ? values.Count : values.Count - 1;
                for (int index = 0; index < segmentCount; index++)
                {
                    double bulge = 0.0;
                    try { bulge = featureLine.GetBulge(index); } catch { }
                    if (Math.Abs(bulge) > Tol) continue;
                    int next = (index + 1) % values.Count;
                    Point3d start = values[index];
                    Point3d end = values[next];
                    if (PlanDistance(start, end) > Tol)
                        result.Add(new StraightSegment { Start = start, End = end });
                }
            }
            return result;
        }

        private static Xline CreateOffsetXline(
            Database database, Entity source, StraightSegment segment, double offset)
        {
            Vector3d direction = PlanVector(segment.Start, segment.End);
            if (direction.Length <= Tol) return null;
            direction = direction.GetNormal();
            Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0);
            Point3d basePoint = segment.Start + normal * offset;
            var xline = new Xline();
            ApplySourceProperties(database, source, xline);
            xline.BasePoint = basePoint;
            xline.UnitDir = direction;
            return xline;
        }

        private static bool SameInfiniteLine(Xline first, Xline second)
        {
            if (first == null || second == null) return false;
            Vector3d a = first.UnitDir;
            Vector3d b = second.UnitDir;
            double parallel = Math.Abs(a.X * b.Y - a.Y * b.X);
            if (parallel > Tol) return false;
            Vector3d delta = second.BasePoint - first.BasePoint;
            double offset = Math.Abs(a.X * delta.Y - a.Y * delta.X);
            return offset <= JunctionTol;
        }

        private static StraightSegment AlignSecond(StraightSegment first, StraightSegment second)
        {
            double direct = PlanDistance(first.Start, second.Start) + PlanDistance(first.End, second.End);
            double reverse = PlanDistance(first.Start, second.End) + PlanDistance(first.End, second.Start);
            if (reverse < direct)
                return new StraightSegment { Start = second.End, End = second.Start };
            return new StraightSegment { Start = second.Start, End = second.End };
        }

        private static double ResolveSideSign(StraightSegment segment, string side, Point3d? picked)
        {
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) return -1.0;
            if (!picked.HasValue) return 1.0;
            Vector3d direction = PlanVector(segment.Start, segment.End);
            if (direction.Length <= Tol) return 1.0;
            Point3d middle = MidPoint(segment.Start, segment.End);
            Vector3d toPick = picked.Value - middle;
            double cross = direction.X * toPick.Y - direction.Y * toPick.X;
            return cross >= 0.0 ? 1.0 : -1.0;
        }

        private static DBObjectCollection BuildNormalOffsets(Entity source, double offset)
        {
            Curve curve = source as Curve;
            if (curve != null)
            {
                try { return curve.GetOffsetCurves(offset); } catch { return null; }
            }

            CivilFeatureLine featureLine = source as CivilFeatureLine;
            if (featureLine == null || featureLine.IsReferenceObject) return null;
            using (Polyline temporary = BuildPlanPolyline(featureLine))
            {
                if (temporary == null) return null;
                try { return temporary.GetOffsetCurves(offset); } catch { return null; }
            }
        }

        private static Polyline BuildPlanPolyline(CivilFeatureLine featureLine)
        {
            Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.PIPoint);
            if (points == null || points.Count < 2) return null;
            var polyline = new Polyline(points.Count);
            for (int index = 0; index < points.Count; index++)
            {
                double bulge = 0.0;
                try { bulge = featureLine.GetBulge(index); } catch { }
                polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), bulge, 0.0, 0.0);
            }
            polyline.Closed = featureLine.Closed;
            return polyline;
        }

        private static void ApplySourceProperties(Database database, Entity source, Entity output)
        {
            output.SetDatabaseDefaults(database);
            try { output.SetPropertiesFrom(source); } catch { }
            try { output.LayerId = source.LayerId; } catch { }
        }

        private static PromptSelectionResult Select(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static bool LayerLocked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static bool ContainsPlanPoint(Point3dCollection points, Point3d candidate)
        {
            foreach (Point3d point in points)
                if (PlanDistance(point, candidate) <= JunctionTol) return true;
            return false;
        }

        private static void RemoveNearDuplicates(IList<double> values)
        {
            for (int index = values.Count - 1; index > 0; index--)
                if (Math.Abs(values[index] - values[index - 1]) <= JunctionTol)
                    values.RemoveAt(index);
        }

        private static void DisposeUnowned(DBObjectCollection values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }

        private static Point3d MidPoint(Point3d first, Point3d second)
        {
            return new Point3d(
                (first.X + second.X) * 0.5,
                (first.Y + second.Y) * 0.5,
                (first.Z + second.Z) * 0.5);
        }

        private static Vector3d PlanVector(Point3d start, Point3d end)
        {
            return new Vector3d(end.X - start.X, end.Y - start.Y, 0.0);
        }

        private static double Cross(double ax, double ay, double bx, double by)
        {
            return ax * by - ay * bx;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double PlanDistance(Point2d first, Point2d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
