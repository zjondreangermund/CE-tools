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
    /// Final August 26 field runtime for destructive/multi-object CAD Supplementary
    /// geometry. Every selected source is isolated in its own transaction. Closing
    /// feature lines preserves plan curves and elevations; stretch uses the entity's
    /// real grip/stretch protocol; construction offsets are LINE entities joined at
    /// zero-radius corners rather than ordinary offset polylines.
    /// </summary>
    internal static class August26CadSupplementaryFieldRuntime
    {
        private const double Tol = 0.000001;

        private sealed class ConstructionSegment
        {
            internal Point3d Start;
            internal Point3d End;
            internal int Group;
        }

        internal static void CloseOpenMultiple(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = Select(editor,
                "\nSelect open lightweight polylines and/or Civil 3D feature lines to close by connecting each object's own endpoints: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int polylines = 0;
            int featureLines = 0;
            int alreadyClosed = 0;
            int skipped = 0;
            int failed = 0;

            foreach (ObjectId id in selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.IsErased || LayerLocked(transaction, entity.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        Polyline polyline = entity as Polyline;
                        if (polyline != null)
                        {
                            if (polyline.Closed) { alreadyClosed++; continue; }
                            if (polyline.NumberOfVertices < 2) { skipped++; continue; }
                            polyline.UpgradeOpen();
                            polyline.Closed = true;
                            try { polyline.RecordGraphicsModified(true); } catch { }
                            transaction.Commit();
                            polylines++;
                            continue;
                        }

                        CivilFeatureLine featureLine = entity as CivilFeatureLine;
                        if (featureLine == null || featureLine.IsReferenceObject)
                        {
                            skipped++;
                            continue;
                        }
                        if (featureLine.Closed) { alreadyClosed++; continue; }

                        if (!CreateClosedFeatureLineReplacement(document.Database, transaction, featureLine))
                            throw new InvalidOperationException("Civil 3D could not create the closed feature-line replacement.");

                        transaction.Commit();
                        featureLines++;
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nSelected object left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_CLOSEOPENMULTI complete. Polylines closed={0}; feature lines closed={1}; already closed={2}; skipped={3}; failed={4}.",
                polylines, featureLines, alreadyClosed, skipped, failed);
        }

        private static bool CreateClosedFeatureLineReplacement(
            Database database,
            Transaction transaction,
            CivilFeatureLine featureLine)
        {
            Point3dCollection piCollection = featureLine.GetPoints(FeatureLinePointType.PIPoint);
            if (piCollection == null || piCollection.Count < 2) return false;

            var piPoints = new List<Point3d>();
            foreach (Point3d point in piCollection) piPoints.Add(point);
            var elevationPoints = new List<Point3d>();
            Point3dCollection elevationCollection = featureLine.GetPoints(FeatureLinePointType.ElevationPoint);
            if (elevationCollection != null)
                foreach (Point3d point in elevationCollection) elevationPoints.Add(point);

            var bulges = new List<double>();
            for (int index = 0; index < piPoints.Count - 1; index++)
            {
                double bulge = 0.0;
                try { bulge = featureLine.GetBulge(index); } catch { }
                bulges.Add(bulge);
            }

            BlockTableRecord owner = transaction.GetObject(
                featureLine.OwnerId,
                OpenMode.ForWrite,
                false) as BlockTableRecord;
            if (owner == null) return false;

            var temporary = new Polyline(piPoints.Count);
            temporary.SetDatabaseDefaults(database);
            temporary.LayerId = featureLine.LayerId;
            temporary.Elevation = 0.0;
            for (int index = 0; index < piPoints.Count; index++)
            {
                double bulge = index < bulges.Count ? bulges[index] : 0.0;
                temporary.AddVertexAt(
                    index,
                    new Point2d(piPoints[index].X, piPoints[index].Y),
                    bulge,
                    0.0,
                    0.0);
            }
            temporary.Closed = true;
            owner.AppendEntity(temporary);
            transaction.AddNewlyCreatedDBObject(temporary, true);

            string originalName = featureLine.Name;
            string styleName = featureLine.StyleName;
            ObjectId siteId = featureLine.SiteId;
            ObjectId layerId = featureLine.LayerId;

            featureLine.UpgradeOpen();
            featureLine.Name = "CE_TMP_CLOSE_" + Guid.NewGuid().ToString("N");
            string targetName = string.IsNullOrWhiteSpace(originalName)
                ? "CE-CLOSED-FEATURELINE-" + featureLine.ObjectId.Handle.ToString()
                : originalName;

            ObjectId replacementId = siteId.IsNull
                ? CivilFeatureLine.Create(targetName, temporary.ObjectId)
                : CivilFeatureLine.Create(targetName, temporary.ObjectId, siteId);
            CivilFeatureLine replacement = transaction.GetObject(
                replacementId,
                OpenMode.ForWrite,
                false) as CivilFeatureLine;
            if (replacement == null || !replacement.Closed) return false;

            try { replacement.SetPropertiesFrom(featureLine); } catch { }
            replacement.LayerId = layerId;
            if (!string.IsNullOrWhiteSpace(styleName))
            {
                try { replacement.StyleName = styleName; } catch { }
            }

            Point3dCollection replacementPi = replacement.GetPoints(FeatureLinePointType.PIPoint);
            int piCount = replacementPi == null ? 0 : replacementPi.Count;
            for (int index = 0; index < Math.Min(piPoints.Count, piCount); index++)
            {
                try { replacement.SetPointElevation(index, piPoints[index].Z); } catch { }
            }

            foreach (Point3d point in elevationPoints)
            {
                if (PlanDistance(point, piPoints[0]) <= Tol ||
                    PlanDistance(point, piPoints[piPoints.Count - 1]) <= Tol)
                    continue;
                try { replacement.InsertElevationPoint(point); } catch { }
            }

            if (!temporary.IsErased) temporary.Erase();
            featureLine.Erase();
            try { replacement.RecordGraphicsModified(true); } catch { }
            return true;
        }

        internal static void StretchFeatureLines(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = Select(editor,
                "\nSelect Civil 3D feature lines to stretch: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            PromptPointResult firstCorner = editor.GetPoint("\nFirst corner of crossing stretch window: ");
            if (firstCorner.Status != PromptStatus.OK) return;
            PromptPointResult secondCorner = editor.GetCorner(new PromptCornerOptions(
                "\nOpposite corner of crossing stretch window: ", firstCorner.Value));
            if (secondCorner.Status != PromptStatus.OK) return;
            PromptPointResult basePoint = editor.GetPoint("\nSpecify stretch base point: ");
            if (basePoint.Status != PromptStatus.OK) return;
            PromptPointResult destination = editor.GetPoint(new PromptPointOptions("\nSpecify second point: ")
            {
                BasePoint = basePoint.Value,
                UseBasePoint = true
            });
            if (destination.Status != PromptStatus.OK) return;

            Point3d corner1 = firstCorner.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d corner2 = secondCorner.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d baseWorld = basePoint.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d destinationWorld = destination.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Vector3d displacement = destinationWorld - baseWorld;
            if (displacement.Length <= Tol) return;

            int changed = 0;
            int skipped = 0;
            int failed = 0;
            foreach (ObjectId id in selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct())
            {
                bool moved;
                try
                {
                    moved = TryStretchFeatureLine(document.Database, id, corner1, corner2, displacement, true);
                    if (!moved)
                        moved = TryStretchFeatureLine(document.Database, id, corner1, corner2, displacement, false);
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nFeature line left unchanged: {0}", exception.Message);
                    continue;
                }

                if (moved) changed++; else skipped++;
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_MULTISTRETCHFL complete. Feature lines stretched={0}; skipped/no grips in window={1}; failed={2}.",
                changed, skipped, failed);
        }

        private static bool TryStretchFeatureLine(
            Database database,
            ObjectId id,
            Point3d corner1,
            Point3d corner2,
            Vector3d displacement,
            bool useGripProtocol)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine featureLine = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine;
                if (featureLine == null || featureLine.IsReferenceObject ||
                    LayerLocked(transaction, featureLine.LayerId))
                    return false;

                Point3dCollection beforeCollection = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                var before = new List<Point3d>();
                if (beforeCollection != null)
                    foreach (Point3d point in beforeCollection) before.Add(point);

                var moveIndices = new IntegerCollection();
                if (useGripProtocol)
                {
                    var gripPoints = new Point3dCollection();
                    var snapModes = new IntegerCollection();
                    var geometryIds = new IntegerCollection();
                    featureLine.GetGripPoints(gripPoints, snapModes, geometryIds);
                    for (int index = 0; index < gripPoints.Count; index++)
                        if (InsidePlanWindow(gripPoints[index], corner1, corner2)) moveIndices.Add(index);
                    if (moveIndices.Count == 0) return false;
                    featureLine.MoveGripPointsAt(moveIndices, displacement);
                }
                else
                {
                    var stretchPoints = new Point3dCollection();
                    featureLine.GetStretchPoints(stretchPoints);
                    for (int index = 0; index < stretchPoints.Count; index++)
                        if (InsidePlanWindow(stretchPoints[index], corner1, corner2)) moveIndices.Add(index);
                    if (moveIndices.Count == 0) return false;
                    featureLine.MoveStretchPointsAt(moveIndices, displacement);
                }

                Point3dCollection afterCollection = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                var after = new List<Point3d>();
                if (afterCollection != null)
                    foreach (Point3d point in afterCollection) after.Add(point);
                if (!StretchResultIsLocal(before, after, corner1, corner2, displacement))
                    return false;

                try { featureLine.RecordGraphicsModified(true); } catch { }
                transaction.Commit();
                return true;
            }
        }

        private static bool StretchResultIsLocal(
            IList<Point3d> before,
            IList<Point3d> after,
            Point3d corner1,
            Point3d corner2,
            Vector3d displacement)
        {
            if (before == null || after == null || before.Count == 0 || before.Count != after.Count)
                return true;

            bool movedInside = false;
            double expected = Math.Sqrt(displacement.X * displacement.X + displacement.Y * displacement.Y);
            double tolerance = Math.Max(0.0001, expected * 0.001);
            for (int index = 0; index < before.Count; index++)
            {
                double moved = PlanDistance(before[index], after[index]);
                if (InsidePlanWindow(before[index], corner1, corner2))
                {
                    if (moved > tolerance) movedInside = true;
                }
                else if (moved > tolerance)
                {
                    // Protect against the Entity base implementation translating the
                    // whole FeatureLine when a Civil grip protocol is unavailable.
                    return false;
                }
            }
            return movedInside;
        }

        internal static void ConstructionOffsets(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Offset / Construction Offset",
                "Normal mode creates a normal offset. Construction-line mode offsets every straight section as an AutoCAD LINE, removes redundant collinear sections, and joins adjacent construction lines at a zero-radius corner.");
            model.AddPositiveDouble("Distance", "01 Offset", "Offset distance", 1.0,
                "Drawing-unit offset distance.");
            model.AddChoice("Mode", "01 Offset", "Offset type", "Normal offset",
                "Construction-line offset creates separate LINE construction entities, not a normal offset polyline.",
                new[] { "Normal offset", "Construction-line offset" });
            model.AddChoice("Side", "01 Offset", "Offset side", "Pick side",
                "Pick side is safest for mixed curve directions. Left/right uses source direction.",
                new[] { "Pick side", "Left", "Right" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, lightweight polylines and/or Civil 3D feature lines to offset: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            Point3d? sidePoint = null;
            if (string.Equals(model.Text("Side"), "Pick side", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult point = editor.GetPoint("\nPick the required offset side: ");
                if (point.Status != PromptStatus.OK) return;
                sidePoint = point.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            }

            double distance = Math.Max(model.Double("Distance", 1.0), Tol);
            bool construction = string.Equals(
                model.Text("Mode"), "Construction-line offset", StringComparison.OrdinalIgnoreCase);
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
                            bool closed;
                            List<ConstructionSegment> sections = ReadStraightConstructionSections(source, out closed);
                            MergeCollinearSections(sections);
                            if (sections.Count == 0) { skipped++; continue; }
                            double sign = ResolveConstructionSign(
                                sections, distance, model.Text("Side"), sidePoint);
                            List<ConstructionSegment> offsets = OffsetConstructionSections(
                                sections, sign * distance);
                            JoinZeroFillet(offsets, closed, distance);
                            foreach (ConstructionSegment segment in offsets)
                            {
                                if (PlanDistance(segment.Start, segment.End) <= Tol) continue;
                                var output = new Line(segment.Start, segment.End);
                                ApplySourceProperties(document.Database, source, output);
                                owner.AppendEntity(output);
                                transaction.AddNewlyCreatedDBObject(output, true);
                                created++;
                            }
                        }
                        else
                        {
                            double sign = ResolveNormalOffsetSign(source, distance, model.Text("Side"), sidePoint);
                            DBObjectCollection values = BuildNormalOffsets(document.Database, source, sign * distance);
                            if (values == null || values.Count == 0)
                            {
                                DisposeUnowned(values);
                                skipped++;
                                continue;
                            }
                            foreach (DBObject value in values)
                            {
                                Entity output = value as Entity;
                                if (output == null) { try { value.Dispose(); } catch { } continue; }
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
                construction ? "construction LINE entities / zero-fillet" : "normal offset");
        }

        internal static void MiddleConstructionLines(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Centre Construction Lines",
                "Create centre construction LINE entities between selected pairs. Straight sampled runs are simplified first, so no intermediate vertices remain on straight sections.");
            model.AddPositiveDouble("Maximum", "01 Geometry", "Maximum pair distance", 20.0,
                "Pairs whose sampled separation exceeds this value are skipped.");
            model.AddPositiveInteger("Samples", "01 Geometry", "Curve samples per pair", 24,
                "Samples follow bends and curves; collinear samples are removed before LINE entities are created.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, polylines or Civil 3D feature lines in pairs: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct().ToArray();
            if (ids.Length < 2) return;

            double maximum = model.Double("Maximum", 20.0);
            int samples = Math.Max(2, model.Integer("Samples", 24));
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
                        Curve firstCurve = first as Curve;
                        Curve secondCurve = second as Curve;
                        if (first == null || second == null || firstCurve == null || secondCurve == null)
                        {
                            skipped++;
                            continue;
                        }
                        if (LayerLocked(transaction, first.LayerId)) { skipped++; continue; }

                        bool reverseSecond = EndpointPairDistance(firstCurve, secondCurve, true) <
                                             EndpointPairDistance(firstCurve, secondCurve, false);
                        var midpoints = new List<Point3d>();
                        double worst = 0.0;
                        for (int sample = 0; sample <= samples; sample++)
                        {
                            double fraction = sample / (double)samples;
                            Point3d a = PointAtFraction(firstCurve, fraction);
                            Point3d b = PointAtFraction(secondCurve, reverseSecond ? 1.0 - fraction : fraction);
                            worst = Math.Max(worst, PlanDistance(a, b));
                            midpoints.Add(new Point3d(
                                (a.X + b.X) * 0.5,
                                (a.Y + b.Y) * 0.5,
                                (a.Z + b.Z) * 0.5));
                        }
                        if (worst > maximum) { skipped++; continue; }
                        midpoints = SimplifyStraightVertices(midpoints);
                        if (midpoints.Count < 2) { skipped++; continue; }

                        BlockTableRecord owner = transaction.GetObject(
                            document.Database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (owner == null) { skipped++; continue; }
                        for (int index = 0; index + 1 < midpoints.Count; index++)
                        {
                            if (PlanDistance(midpoints[index], midpoints[index + 1]) <= Tol) continue;
                            var output = new Line(midpoints[index], midpoints[index + 1]);
                            ApplySourceProperties(document.Database, first, output);
                            owner.AppendEntity(output);
                            transaction.AddNewlyCreatedDBObject(output, true);
                            created++;
                        }
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
                "\nCE_SURVEYMIDCONSTRUCTION complete. Construction LINE entities={0}; skipped/unpaired={1}.",
                created, skipped + (ids.Length % 2));
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

        private static List<ConstructionSegment> ReadStraightConstructionSections(Entity source, out bool closed)
        {
            closed = false;
            var result = new List<ConstructionSegment>();
            Line line = source as Line;
            if (line != null)
            {
                result.Add(new ConstructionSegment { Start = line.StartPoint, End = line.EndPoint, Group = 0 });
                return result;
            }

            Polyline polyline = source as Polyline;
            if (polyline != null)
            {
                closed = polyline.Closed;
                int count = polyline.NumberOfVertices;
                int segmentCount = closed ? count : count - 1;
                int group = 0;
                for (int index = 0; index < segmentCount; index++)
                {
                    bool straight = false;
                    try { straight = polyline.GetSegmentType(index) == SegmentType.Line; } catch { }
                    if (!straight) { group++; continue; }
                    int next = (index + 1) % count;
                    Point3d a = polyline.GetPoint3dAt(index);
                    Point3d b = polyline.GetPoint3dAt(next);
                    if (PlanDistance(a, b) <= Tol) continue;
                    result.Add(new ConstructionSegment { Start = a, End = b, Group = group });
                }
                return result;
            }

            CivilFeatureLine featureLine = source as CivilFeatureLine;
            if (featureLine != null && !featureLine.IsReferenceObject)
            {
                closed = featureLine.Closed;
                Point3dCollection piCollection = featureLine.GetPoints(FeatureLinePointType.PIPoint);
                if (piCollection == null || piCollection.Count < 2) return result;
                var points = new List<Point3d>();
                foreach (Point3d point in piCollection) points.Add(point);
                int segmentCount = closed ? points.Count : points.Count - 1;
                int group = 0;
                for (int index = 0; index < segmentCount; index++)
                {
                    double bulge = 0.0;
                    try { bulge = featureLine.GetBulge(index); } catch { }
                    if (Math.Abs(bulge) > Tol) { group++; continue; }
                    int next = (index + 1) % points.Count;
                    Point3d a = points[index];
                    Point3d b = points[next];
                    if (PlanDistance(a, b) <= Tol) continue;
                    result.Add(new ConstructionSegment { Start = a, End = b, Group = group });
                }
            }
            return result;
        }

        private static void MergeCollinearSections(List<ConstructionSegment> sections)
        {
            if (sections == null || sections.Count < 2) return;
            var merged = new List<ConstructionSegment>();
            foreach (ConstructionSegment section in sections)
            {
                if (merged.Count == 0)
                {
                    merged.Add(section);
                    continue;
                }
                ConstructionSegment previous = merged[merged.Count - 1];
                if (previous.Group == section.Group &&
                    PlanDistance(previous.End, section.Start) <= Tol &&
                    CollinearForward(previous.Start, previous.End, section.End))
                {
                    previous.End = section.End;
                }
                else
                {
                    merged.Add(section);
                }
            }
            sections.Clear();
            sections.AddRange(merged);
        }

        private static bool CollinearForward(Point3d a, Point3d b, Point3d c)
        {
            double abx = b.X - a.X;
            double aby = b.Y - a.Y;
            double bcx = c.X - b.X;
            double bcy = c.Y - b.Y;
            double cross = Math.Abs(abx * bcy - aby * bcx);
            double scale = Math.Max(1.0,
                Math.Sqrt(abx * abx + aby * aby) * Math.Sqrt(bcx * bcx + bcy * bcy));
            double dot = abx * bcx + aby * bcy;
            return cross <= Tol * scale && dot >= 0.0;
        }

        private static List<ConstructionSegment> OffsetConstructionSections(
            IList<ConstructionSegment> sections,
            double offset)
        {
            var result = new List<ConstructionSegment>();
            foreach (ConstructionSegment section in sections)
            {
                double dx = section.End.X - section.Start.X;
                double dy = section.End.Y - section.Start.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= Tol) continue;
                double nx = -dy / length * offset;
                double ny = dx / length * offset;
                result.Add(new ConstructionSegment
                {
                    Start = new Point3d(section.Start.X + nx, section.Start.Y + ny, section.Start.Z),
                    End = new Point3d(section.End.X + nx, section.End.Y + ny, section.End.Z),
                    Group = section.Group
                });
            }
            return result;
        }

        private static void JoinZeroFillet(
            IList<ConstructionSegment> sections,
            bool closed,
            double offsetDistance)
        {
            if (sections == null || sections.Count < 2) return;
            for (int index = 0; index + 1 < sections.Count; index++)
            {
                if (sections[index].Group != sections[index + 1].Group) continue;
                JoinPairAtIntersection(sections[index], sections[index + 1], offsetDistance);
            }
            if (closed && sections.Count > 2 &&
                sections[0].Group == sections[sections.Count - 1].Group)
                JoinPairAtIntersection(sections[sections.Count - 1], sections[0], offsetDistance);
        }

        private static void JoinPairAtIntersection(
            ConstructionSegment first,
            ConstructionSegment second,
            double offsetDistance)
        {
            Point3d intersection;
            if (!TryInfiniteIntersection(first.Start, first.End, second.Start, second.End, out intersection))
                return;
            double limit = Math.Max(
                PlanDistance(first.Start, first.End),
                PlanDistance(second.Start, second.End)) * 10.0 + Math.Abs(offsetDistance) * 20.0 + 1.0;
            if (PlanDistance(first.End, intersection) > limit ||
                PlanDistance(second.Start, intersection) > limit)
                return;
            first.End = intersection;
            second.Start = intersection;
        }

        private static double ResolveConstructionSign(
            IList<ConstructionSegment> sections,
            double distance,
            string side,
            Point3d? picked)
        {
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) return -1.0;
            if (!picked.HasValue) return 1.0;
            List<ConstructionSegment> positive = OffsetConstructionSections(sections, distance);
            List<ConstructionSegment> negative = OffsetConstructionSections(sections, -distance);
            double pd = DistanceToSegments(positive, picked.Value);
            double nd = DistanceToSegments(negative, picked.Value);
            return pd <= nd ? 1.0 : -1.0;
        }

        private static double ResolveNormalOffsetSign(
            Entity source,
            double distance,
            string side,
            Point3d? picked)
        {
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) return -1.0;
            if (!picked.HasValue) return 1.0;

            Curve curve = source as Curve;
            if (curve != null)
            {
                double positive = NativeOffsetDistanceToPick(curve, distance, picked.Value);
                double negative = NativeOffsetDistanceToPick(curve, -distance, picked.Value);
                if (!double.IsInfinity(positive) || !double.IsInfinity(negative))
                    return positive <= negative ? 1.0 : -1.0;
            }

            bool closed;
            List<ConstructionSegment> sections = ReadStraightConstructionSections(source, out closed);
            return ResolveConstructionSign(sections, distance, "Pick side", picked);
        }

        private static DBObjectCollection BuildNormalOffsets(Database database, Entity source, double offset)
        {
            Curve curve = source as Curve;
            if (curve != null)
            {
                try { return curve.GetOffsetCurves(offset); } catch { }
            }

            CivilFeatureLine featureLine = source as CivilFeatureLine;
            if (featureLine == null || featureLine.IsReferenceObject) return null;
            Point3dCollection piCollection = featureLine.GetPoints(FeatureLinePointType.PIPoint);
            if (piCollection == null || piCollection.Count < 2) return null;
            var transient = new Polyline(piCollection.Count);
            try
            {
                transient.SetDatabaseDefaults(database);
                transient.Elevation = 0.0;
                for (int index = 0; index < piCollection.Count; index++)
                {
                    double bulge = 0.0;
                    if (index < piCollection.Count - 1 || featureLine.Closed)
                    {
                        try { bulge = featureLine.GetBulge(index); } catch { }
                    }
                    Point3d point = piCollection[index];
                    transient.AddVertexAt(index, new Point2d(point.X, point.Y), bulge, 0.0, 0.0);
                }
                transient.Closed = featureLine.Closed;
                return transient.GetOffsetCurves(offset);
            }
            finally
            {
                transient.Dispose();
            }
        }

        private static double NativeOffsetDistanceToPick(Curve curve, double offset, Point3d pick)
        {
            DBObjectCollection values = null;
            try
            {
                values = curve.GetOffsetCurves(offset);
                double best = double.PositiveInfinity;
                foreach (DBObject value in values)
                {
                    Curve offsetCurve = value as Curve;
                    if (offsetCurve == null) continue;
                    Point3d closest = offsetCurve.GetClosestPointTo(
                        new Point3d(pick.X, pick.Y, pick.Z), false);
                    best = Math.Min(best, PlanDistance(closest, pick));
                }
                return best;
            }
            catch { return double.PositiveInfinity; }
            finally { DisposeUnowned(values); }
        }

        private static void ApplySourceProperties(Database database, Entity source, Entity output)
        {
            output.SetDatabaseDefaults(database);
            try { output.SetPropertiesFrom(source); } catch { }
            output.LayerId = source.LayerId;
            try { output.Color = source.Color; } catch { output.ColorIndex = 256; }
            try { output.LinetypeId = source.LinetypeId; } catch { }
            try { output.LineWeight = source.LineWeight; } catch { }
            try { output.Transparency = source.Transparency; } catch { }
        }

        private static double DistanceToSegments(IList<ConstructionSegment> segments, Point3d point)
        {
            if (segments == null || segments.Count == 0) return double.PositiveInfinity;
            double best = double.PositiveInfinity;
            foreach (ConstructionSegment segment in segments)
                best = Math.Min(best, DistanceToSegment(point, segment.Start, segment.End));
            return best;
        }

        private static double DistanceToSegment(Point3d point, Point3d start, Point3d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length2 = dx * dx + dy * dy;
            if (length2 <= Tol) return PlanDistance(point, start);
            double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / length2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double x = start.X + t * dx;
            double y = start.Y + t * dy;
            double ex = point.X - x;
            double ey = point.Y - y;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static List<Point3d> SimplifyStraightVertices(IList<Point3d> source)
        {
            var points = new List<Point3d>();
            if (source == null) return points;
            foreach (Point3d point in source)
            {
                if (points.Count == 0 || PlanDistance(points[points.Count - 1], point) > Tol)
                    points.Add(point);
            }
            if (points.Count <= 2) return points;

            var result = new List<Point3d> { points[0] };
            for (int index = 1; index < points.Count - 1; index++)
            {
                Point3d previous = result[result.Count - 1];
                Point3d current = points[index];
                Point3d next = points[index + 1];
                if (!CollinearForward(previous, current, next)) result.Add(current);
            }
            result.Add(points[points.Count - 1]);
            return result;
        }

        private static bool TryInfiniteIntersection(
            Point3d a1,
            Point3d a2,
            Point3d b1,
            Point3d b2,
            out Point3d point)
        {
            point = Point3d.Origin;
            double rx = a2.X - a1.X;
            double ry = a2.Y - a1.Y;
            double sx = b2.X - b1.X;
            double sy = b2.Y - b1.Y;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= Tol) return false;
            double qx = b1.X - a1.X;
            double qy = b1.Y - a1.Y;
            double t = (qx * sy - qy * sx) / denominator;
            point = new Point3d(a1.X + t * rx, a1.Y + t * ry, (a1.Z + b1.Z) * 0.5);
            return true;
        }

        private static double EndpointPairDistance(Curve first, Curve second, bool reverseSecond)
        {
            try
            {
                Point3d bStart = reverseSecond ? second.EndPoint : second.StartPoint;
                Point3d bEnd = reverseSecond ? second.StartPoint : second.EndPoint;
                return PlanDistance(first.StartPoint, bStart) + PlanDistance(first.EndPoint, bEnd);
            }
            catch { return double.PositiveInfinity; }
        }

        private static Point3d PointAtFraction(Curve curve, double fraction)
        {
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                return curve.GetPointAtDist(start + (end - start) * fraction);
            }
            catch
            {
                double parameter = curve.StartParam + (curve.EndParam - curve.StartParam) * fraction;
                return curve.GetPointAtParameter(parameter);
            }
        }

        private static bool InsidePlanWindow(Point3d point, Point3d first, Point3d second)
        {
            double minX = Math.Min(first.X, second.X) - Tol;
            double maxX = Math.Max(first.X, second.X) + Tol;
            double minY = Math.Min(first.Y, second.Y) - Tol;
            double maxY = Math.Max(first.Y, second.Y) + Tol;
            return point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool LayerLocked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(
                    layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static void DisposeUnowned(DBObjectCollection values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }
    }
}
