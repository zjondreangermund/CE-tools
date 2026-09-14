using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.September14FeatureLineLinkExistingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Links existing Civil 3D feature lines into the established CE_FLREL stepped-offset
    /// refresh model. Linking writes relationship metadata only; selected geometry is not
    /// erased, replaced or rebuilt by this command.
    /// </summary>
    public sealed class September14FeatureLineLinkExistingCommands
    {
        private const string RecordKey = "CE_FLREL";
        private const double Tolerance = 1e-7;

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLRELLINKEXISTING",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExisting()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            var sourceOptions = new PromptEntityOptions("\nSelect SOURCE feature line: ");
            sourceOptions.SetRejectMessage("\nSelect an editable Civil 3D feature line.");
            sourceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult sourceResult = editor.GetEntity(sourceOptions);
            if (sourceResult.Status != PromptStatus.OK) return;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect existing feature lines to link to the source: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var candidates = new List<LinkCandidate>();
            int skipped = 0;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = transaction.GetObject(
                        sourceResult.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                    EnsureEditable(source, transaction, "source");

                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                        {
                            if (id.IsNull || id == sourceResult.ObjectId)
                            {
                                skipped++;
                                continue;
                            }

                            CivilFeatureLine child = transaction.GetObject(
                                id, OpenMode.ForRead, false) as CivilFeatureLine;
                            if (child == null || child.IsReferenceObject ||
                                IsLayerLocked(transaction, child.LayerId) ||
                                HasRelation(child, transaction))
                            {
                                skipped++;
                                continue;
                            }

                            double horizontal;
                            double vertical;
                            string reason;
                            if (!TryMeasureRelation(
                                    source,
                                    child,
                                    plan,
                                    out horizontal,
                                    out vertical,
                                    out reason))
                            {
                                skipped++;
                                editor.WriteMessage(
                                    "\nFeature line '{0}' was skipped. {1}",
                                    string.IsNullOrWhiteSpace(child.Name)
                                        ? child.Handle.ToString()
                                        : child.Name,
                                    reason);
                                continue;
                            }

                            candidates.Add(new LinkCandidate(
                                id,
                                child.Name,
                                horizontal,
                                vertical));
                        }
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLRELLINKEXISTING cancelled before linking. No changes were committed. " +
                    exception.Message);
                return;
            }

            if (candidates.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_FLRELLINKEXISTING: no suitable unlinked feature lines were selected.");
                return;
            }

            candidates = candidates
                .OrderBy(item => Math.Abs(item.HorizontalOffset))
                .ThenBy(item => item.HorizontalOffset)
                .ToList();

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = transaction.GetObject(
                        sourceResult.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                    EnsureEditable(source, transaction, "source");
                    string sourceHandle = source.Handle.ToString();

                    for (int index = 0; index < candidates.Count; index++)
                    {
                        LinkCandidate candidate = candidates[index];
                        CivilFeatureLine child = transaction.GetObject(
                            candidate.ObjectId, OpenMode.ForWrite, false) as CivilFeatureLine;
                        EnsureEditable(child, transaction, "child");
                        if (HasRelation(child, transaction))
                            throw new InvalidOperationException(
                                "A selected feature line became linked while the command was running.");

                        WriteRelation(
                            child,
                            sourceHandle,
                            candidate.HorizontalOffset,
                            candidate.VerticalOffset,
                            index + 1,
                            transaction);
                    }

                    transaction.Commit();
                }

                document.Editor.Regen();
                editor.WriteMessage(
                    "\nCE_FLRELLINKEXISTING complete. Linked existing feature lines={0}; skipped={1}. Geometry was kept unchanged. Use CE_FLRELUPDATEMULTI or Dynamic Refresh when the source changes.",
                    candidates.Count,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLRELLINKEXISTING cancelled. No relationship writes were committed. " +
                    exception.Message);
            }
        }

        private static bool TryMeasureRelation(
            CivilFeatureLine source,
            CivilFeatureLine child,
            Polyline sourcePlan,
            out double horizontalOffset,
            out double verticalOffset,
            out string reason)
        {
            horizontalOffset = 0.0;
            verticalOffset = 0.0;
            reason = string.Empty;

            Point3dCollection points = child.GetPoints(FeatureLinePointType.PIPoint);
            if (points == null || points.Count == 0)
                points = child.GetPoints(FeatureLinePointType.AllPoints);
            if (points == null || points.Count == 0)
            {
                reason = "No feature-line points were available.";
                return false;
            }

            var horizontalSamples = new List<double>();
            var verticalSamples = new List<double>();
            foreach (Point3d point in points)
            {
                Point3d planPoint = new Point3d(point.X, point.Y, 0.0);
                Point3d closestPlan = sourcePlan.GetClosestPointTo(planPoint, false);
                Vector3d tangent = sourcePlan.GetFirstDerivative(closestPlan);
                double tangentLength = Math.Sqrt(
                    (tangent.X * tangent.X) + (tangent.Y * tangent.Y));
                if (tangentLength <= Tolerance) continue;

                double dx = point.X - closestPlan.X;
                double dy = point.Y - closestPlan.Y;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                double cross = (tangent.X * dy) - (tangent.Y * dx);
                double signedDistance = Math.Abs(cross) <= Tolerance
                    ? distance
                    : Math.Sign(cross) * distance;

                Point3d sourcePoint = source.GetClosestPointTo(
                    planPoint,
                    Vector3d.ZAxis,
                    false);

                horizontalSamples.Add(signedDistance);
                verticalSamples.Add(point.Z - sourcePoint.Z);
            }

            if (horizontalSamples.Count == 0 || verticalSamples.Count == 0)
            {
                reason = "A stable source offset could not be measured.";
                return false;
            }

            horizontalOffset = horizontalSamples.Average();
            verticalOffset = verticalSamples.Average();

            double horizontalSpread = horizontalSamples.Max(value => Math.Abs(value - horizontalOffset));
            double verticalSpread = verticalSamples.Max(value => Math.Abs(value - verticalOffset));
            double horizontalTolerance = Math.Max(0.05, Math.Abs(horizontalOffset) * 0.10);
            double verticalTolerance = 0.05;

            if (horizontalSpread > horizontalTolerance || verticalSpread > verticalTolerance)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "Geometry is not a consistent source offset (horizontal spread {0:0.###}, vertical spread {1:0.###}).",
                    horizontalSpread,
                    verticalSpread);
                return false;
            }

            return true;
        }

        private static Polyline BuildPlanPolyline(CivilFeatureLine source)
        {
            Point3dCollection piPoints = source.GetPoints(FeatureLinePointType.PIPoint);
            if (piPoints == null || piPoints.Count < 2)
                throw new InvalidOperationException("The source requires at least two PI points.");

            List<Point3d> points = piPoints.Cast<Point3d>().ToList();
            bool closed = source.Closed;
            if (closed && points.Count > 2 &&
                PlanDistance(points[0], points[points.Count - 1]) <= Tolerance)
                points.RemoveAt(points.Count - 1);

            var polyline = new Polyline(points.Count)
            {
                Normal = Vector3d.ZAxis,
                Elevation = 0.0,
                Closed = closed
            };

            int segmentCount = closed ? points.Count : points.Count - 1;
            for (int index = 0; index < points.Count; index++)
            {
                double bulge = index < segmentCount ? source.GetBulge(index) : 0.0;
                polyline.AddVertexAt(
                    index,
                    new Point2d(points[index].X, points[index].Y),
                    bulge,
                    0.0,
                    0.0);
            }
            return polyline;
        }

        private static void WriteRelation(
            CivilFeatureLine child,
            string sourceHandle,
            double horizontalOffset,
            double verticalOffset,
            int sequence,
            Transaction transaction)
        {
            if (child.ExtensionDictionary.IsNull) child.CreateExtensionDictionary();
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                child.ExtensionDictionary, OpenMode.ForWrite, false);
            Xrecord record = new Xrecord();
            dictionary.SetAt(RecordKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Real, horizontalOffset),
                new TypedValue((int)DxfCode.Real, verticalOffset),
                new TypedValue((int)DxfCode.Int32, sequence));
        }

        private static bool HasRelation(
            CivilFeatureLine featureLine,
            Transaction transaction)
        {
            if (featureLine == null || featureLine.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                featureLine.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            return dictionary != null && dictionary.Contains(RecordKey);
        }

        private static void EnsureEditable(
            CivilFeatureLine featureLine,
            Transaction transaction,
            string role)
        {
            if (featureLine == null || featureLine.IsReferenceObject ||
                IsLayerLocked(transaction, featureLine.LayerId))
                throw new InvalidOperationException(
                    "The " + role + " feature line must be editable and on an unlocked layer.");
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            LayerTableRecord layer = transaction.GetObject(
                layerId, OpenMode.ForRead, false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private sealed class LinkCandidate
        {
            public LinkCandidate(
                ObjectId objectId,
                string name,
                double horizontalOffset,
                double verticalOffset)
            {
                ObjectId = objectId;
                Name = name;
                HorizontalOffset = horizontalOffset;
                VerticalOffset = verticalOffset;
            }

            public ObjectId ObjectId { get; }
            public string Name { get; }
            public double HorizontalOffset { get; }
            public double VerticalOffset { get; }
        }
    }
}
