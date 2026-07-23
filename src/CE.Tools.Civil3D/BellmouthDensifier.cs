using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch densifies lightweight polylines at equal chainages while retaining
    /// their line and true-arc geometry.
    /// </summary>
    public sealed class BellmouthDensifierCommand
    {
        private const string MaximumKeyword = "Maximum";
        private const string NumberKeyword = "Number";
        private const int MaximumRequestedSegments = 100000;

        private static double _lastMaximumSpacing = 0.50;
        private static int _lastSegmentCount = 10;

        [CommandMethod(
            "CE_TOOLS",
            "CE_BMVERT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            Database database = document.Database;

            PromptSelectionResult selection = PromptForPolylines(editor);
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            PromptKeywordOptions methodOptions = new PromptKeywordOptions(
                "\nDensification method [Maximum/Number] <Maximum>: ")
            {
                AllowNone = true
            };
            methodOptions.Keywords.Add(MaximumKeyword);
            methodOptions.Keywords.Add(NumberKeyword);

            PromptResult methodResult = editor.GetKeywords(methodOptions);
            if (methodResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            string method = methodResult.Status == PromptStatus.None
                ? MaximumKeyword
                : methodResult.StringResult;

            double maximumSpacing = 0.0;
            int segmentCount = 0;

            if (string.Equals(method, NumberKeyword, StringComparison.OrdinalIgnoreCase))
            {
                PromptIntegerOptions countOptions = new PromptIntegerOptions(
                    $"\nNumber of equal chainage intervals <{_lastSegmentCount}>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = _lastSegmentCount,
                    LowerLimit = 2,
                    UpperLimit = MaximumRequestedSegments,
                    UseDefaultValue = true
                };

                PromptIntegerResult countResult = editor.GetInteger(countOptions);
                if (countResult.Status != PromptStatus.OK)
                {
                    return;
                }

                segmentCount = countResult.Value;
                _lastSegmentCount = segmentCount;
            }
            else
            {
                PromptDoubleOptions spacingOptions = new PromptDoubleOptions(
                    $"\nMaximum equal segment length in drawing units <{_lastMaximumSpacing:0.###}>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = _lastMaximumSpacing,
                    UseDefaultValue = true
                };

                PromptDoubleResult spacingResult = editor.GetDouble(spacingOptions);
                if (spacingResult.Status != PromptStatus.OK)
                {
                    return;
                }

                maximumSpacing = spacingResult.Value;
                _lastMaximumSpacing = maximumSpacing;
            }

            ObjectId[] objectIds = selection.Value.GetObjectIds();
            var service = new PolylineDensifier();

            int processed = 0;
            int failed = 0;
            int unchanged = 0;
            int insertedVertices = 0;
            int skippedExistingVertices = 0;

            for (int index = 0; index < objectIds.Length; index++)
            {
                ObjectId objectId = objectIds[index];

                try
                {
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        var polyline = transaction.GetObject(
                            objectId,
                            OpenMode.ForWrite,
                            false) as Polyline;

                        if (polyline == null || polyline.IsErased)
                        {
                            unchanged++;
                            continue;
                        }

                        DensifyResult result = string.Equals(
                            method,
                            NumberKeyword,
                            StringComparison.OrdinalIgnoreCase)
                            ? service.DensifyBySegmentCount(polyline, segmentCount)
                            : service.DensifyByMaximumSpacing(polyline, maximumSpacing);

                        transaction.Commit();

                        processed++;
                        insertedVertices += result.InsertedVertices;
                        skippedExistingVertices += result.SkippedExistingVertices;

                        if (result.InsertedVertices == 0)
                        {
                            unchanged++;
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage(
                        $"\nCE_BMVERT skipped object {objectId.Handle}: {exception.Message}");
                }

                if ((index + 1) % 25 == 0)
                {
                    editor.WriteMessage(
                        $"\nCE_BMVERT processed {index + 1} of {objectIds.Length} objects...");
                }
            }

            editor.WriteMessage(
                "\nCE_BMVERT complete." +
                $" Selected: {objectIds.Length};" +
                $" processed: {processed};" +
                $" vertices inserted: {insertedVertices};" +
                $" existing chainage vertices retained: {skippedExistingVertices};" +
                $" unchanged: {unchanged};" +
                $" failed: {failed}.");
        }

        private static PromptSelectionResult PromptForPolylines(Editor editor)
        {
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect bellmouth/kerb-return 2D polylines: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };

            var filter = new SelectionFilter(
                new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                });

            return editor.GetSelection(options, filter);
        }
    }

    internal sealed class DensifyResult
    {
        public DensifyResult(
            double originalLength,
            int plannedSegmentCount,
            double equalSpacing,
            int insertedVertices,
            int skippedExistingVertices)
        {
            OriginalLength = originalLength;
            PlannedSegmentCount = plannedSegmentCount;
            EqualSpacing = equalSpacing;
            InsertedVertices = insertedVertices;
            SkippedExistingVertices = skippedExistingVertices;
        }

        public double OriginalLength { get; }
        public int PlannedSegmentCount { get; }
        public double EqualSpacing { get; }
        public int InsertedVertices { get; }
        public int SkippedExistingVertices { get; }
    }

    /// <summary>
    /// Inserts vertices directly into an existing lightweight polyline. Entity
    /// identity, properties, closed state, lines, arcs, elevation and normal are
    /// retained.
    /// </summary>
    internal sealed class PolylineDensifier
    {
        private const double AbsoluteLengthTolerance = 1e-8;
        private const double RelativeLengthTolerance = 1e-10;
        private const int MaximumGeneratedSegments = 100000;

        public DensifyResult DensifyByMaximumSpacing(Polyline polyline, double maximumSpacing)
        {
            if (polyline == null)
            {
                throw new ArgumentNullException(nameof(polyline));
            }

            DensifyPlan plan = DensifyPlanner.ByMaximumSpacing(polyline.Length, maximumSpacing);
            ValidatePlanSize(plan);
            return ApplyPlan(polyline, plan);
        }

        public DensifyResult DensifyBySegmentCount(Polyline polyline, int segmentCount)
        {
            if (polyline == null)
            {
                throw new ArgumentNullException(nameof(polyline));
            }

            DensifyPlan plan = DensifyPlanner.BySegmentCount(polyline.Length, segmentCount);
            ValidatePlanSize(plan);
            return ApplyPlan(polyline, plan);
        }

        private static void ValidatePlanSize(DensifyPlan plan)
        {
            if (plan.SegmentCount > MaximumGeneratedSegments)
            {
                throw new InvalidOperationException(
                    $"The requested settings create {plan.SegmentCount:N0} intervals. " +
                    $"The current safety limit is {MaximumGeneratedSegments:N0} per polyline.");
            }
        }

        private static DensifyResult ApplyPlan(Polyline polyline, DensifyPlan plan)
        {
            if (polyline.NumberOfVertices < 2 || plan.Stations.Count == 0)
            {
                return new DensifyResult(polyline.Length, plan.SegmentCount, plan.EqualSpacing, 0, 0);
            }

            double tolerance = Math.Max(
                AbsoluteLengthTolerance,
                plan.TotalLength * RelativeLengthTolerance);

            int inserted = 0;
            int skipped = 0;

            polyline.MaximizeMemory();

            // Process descending chainages. Every edit preserves the curve, and
            // each later query uses the current vertex indices rather than stale
            // indices from before an insertion.
            for (int index = plan.Stations.Count - 1; index >= 0; index--)
            {
                double station = plan.Stations[index];
                SplitLocation location;

                if (!TryGetSplitLocation(polyline, station, tolerance, out location))
                {
                    skipped++;
                    continue;
                }

                InsertVertex(polyline, location);
                inserted++;
            }

            polyline.MinimizeMemory();

            return new DensifyResult(
                plan.TotalLength,
                plan.SegmentCount,
                plan.EqualSpacing,
                inserted,
                skipped);
        }

        private static bool TryGetSplitLocation(
            Polyline polyline,
            double station,
            double tolerance,
            out SplitLocation location)
        {
            location = default(SplitLocation);

            int vertexCount = polyline.NumberOfVertices;
            int maximumSegmentIndex = polyline.Closed ? vertexCount - 1 : vertexCount - 2;
            if (maximumSegmentIndex < 0)
            {
                return false;
            }

            double currentLength = polyline.Length;
            if (station <= tolerance || station >= currentLength - tolerance)
            {
                return false;
            }

            double parameter = polyline.GetParameterAtDistance(station);
            int segmentIndex = (int)Math.Floor(parameter);
            segmentIndex = Math.Max(0, Math.Min(maximumSegmentIndex, segmentIndex));

            double segmentStartDistance = polyline.GetDistanceAtParameter(segmentIndex);
            double segmentEndDistance = polyline.GetDistanceAtParameter(segmentIndex + 1);
            double segmentLength = segmentEndDistance - segmentStartDistance;

            if (segmentLength <= tolerance)
            {
                return false;
            }

            if (Math.Abs(station - segmentStartDistance) <= tolerance ||
                Math.Abs(segmentEndDistance - station) <= tolerance)
            {
                return false;
            }

            double fraction = (station - segmentStartDistance) / segmentLength;
            if (fraction <= 0.0 || fraction >= 1.0)
            {
                return false;
            }

            Point3d worldPoint = polyline.GetPointAtDist(station);
            Point3d entityPoint = worldPoint.TransformBy(polyline.Ecs.Inverse());

            location = new SplitLocation(
                segmentIndex,
                fraction,
                new Point2d(entityPoint.X, entityPoint.Y));

            return true;
        }

        private static void InsertVertex(Polyline polyline, SplitLocation location)
        {
            int segmentIndex = location.SegmentIndex;
            double originalBulge = polyline.GetBulgeAt(segmentIndex);
            BulgeSplit bulges = BulgeMath.Split(originalBulge, location.Fraction);

            double originalStartWidth = polyline.GetStartWidthAt(segmentIndex);
            double originalEndWidth = polyline.GetEndWidthAt(segmentIndex);
            double splitWidth = BulgeMath.Interpolate(
                originalStartWidth,
                originalEndWidth,
                location.Fraction);

            // AddVertexAt requires the new point in the polyline ECS/OCS. The
            // bulge stored at a vertex belongs to its outgoing segment.
            polyline.AddVertexAt(
                segmentIndex + 1,
                location.Point,
                bulges.SecondBulge,
                splitWidth,
                originalEndWidth);

            polyline.SetBulgeAt(segmentIndex, bulges.FirstBulge);
            polyline.SetEndWidthAt(segmentIndex, splitWidth);
        }

        private struct SplitLocation
        {
            public SplitLocation(int segmentIndex, double fraction, Point2d point)
            {
                SegmentIndex = segmentIndex;
                Fraction = fraction;
                Point = point;
            }

            public int SegmentIndex { get; }
            public double Fraction { get; }
            public Point2d Point { get; }
        }
    }
}
