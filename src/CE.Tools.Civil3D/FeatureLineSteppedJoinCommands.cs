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

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineSteppedJoinCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Heals a collection of open stepped feature-line pieces into one feature
    /// line. Small gaps are bridged and every original piece endpoint remains a
    /// vertex in the resulting 3D source polyline/feature line.
    /// </summary>
    public sealed class FeatureLineSteppedJoinCommands
    {
        private const double CoincidentTolerance = 1e-7;

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSTEPJOIN",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JoinSteppedFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Join(document);
        }

        private static void Join(Document document)
        {
            Editor editor = document.Editor;
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect stepped feature-line pieces to heal into one feature line: "
            };
            PromptSelectionResult selection = editor.GetSelection(options);
            if (selection.Status != PromptStatus.OK) return;

            List<ObjectId> selectedIds = selection.Value
                .GetObjectIds()
                .Where(id => !id.IsNull)
                .Distinct()
                .ToList();
            if (selectedIds.Count < 2)
            {
                editor.WriteMessage("\nCE_FLSTEPJOIN requires at least two feature-line pieces.");
                return;
            }

            string defaultName = "CE-STEPPED-FL";
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine first = transaction.GetObject(
                        selectedIds[0], OpenMode.ForRead, false) as CivilFeatureLine;
                    if (first != null && !string.IsNullOrWhiteSpace(first.Name))
                        defaultName = first.Name + "-HEALED";
                }
            }
            catch
            {
                // The full validation pass below reports any inaccessible source.
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Heal Stepped Feature Lines",
                "CE Tools orders the selected pieces by their nearest endpoints, bridges only gaps within the selected tolerance, and preserves every piece endpoint as a vertex in the new feature line. Source pieces are kept.");
            settings.AddPositiveDouble(
                "GapTolerance", "01 Gap healing", "Maximum gap tolerance", 0.25,
                "Maximum plan gap, in drawing units, that CE Tools may bridge between adjacent selected pieces.");
            settings.AddText(
                "Name", "02 Output", "New feature-line name", defaultName,
                "A unique suffix is added automatically when this name already exists.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double gapTolerance = settings.Double("GapTolerance", 0.25);
            string requestedName = string.IsNullOrWhiteSpace(settings.Text("Name"))
                ? defaultName
                : settings.Text("Name");

            try
            {
                int pieceCount;
                int vertexCount;
                double largestGap;
                string outputName;
                CreateJoinedFeatureLine(
                    document,
                    selectedIds,
                    gapTolerance,
                    requestedName,
                    out pieceCount,
                    out vertexCount,
                    out largestGap,
                    out outputName);

                editor.WriteMessage(
                    "\nCE_FLSTEPJOIN complete. Created '{0}' from {1} pieces with {2} vertices; largest bridged gap={3:N3}. Source pieces were kept.",
                    outputName,
                    pieceCount,
                    vertexCount,
                    largestGap);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLSTEPJOIN cancelled. No changes were committed. " + exception.Message);
            }
        }

        private static void CreateJoinedFeatureLine(
            Document document,
            IList<ObjectId> selectedIds,
            double gapTolerance,
            string requestedName,
            out int pieceCount,
            out int vertexCount,
            out double largestGap,
            out string outputName)
        {
            pieceCount = 0;
            vertexCount = 0;
            largestGap = 0.0;
            outputName = string.Empty;

            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var pieces = new List<FeaturePiece>();
                ObjectId layerId = ObjectId.Null;
                ObjectId siteId = ObjectId.Null;
                string styleName = string.Empty;

                foreach (ObjectId id in selectedIds)
                {
                    CivilFeatureLine featureLine = transaction.GetObject(
                        id, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (featureLine == null)
                        throw new InvalidOperationException(
                            "The selection contains an object that is not a Civil 3D feature line.");
                    if (featureLine.IsReferenceObject)
                        throw new InvalidOperationException(
                            "Referenced feature lines cannot be healed into a local feature line.");
                    if (featureLine.Closed)
                        throw new InvalidOperationException(
                            "Select open stepped feature-line pieces. Closed feature lines are not supported by this healing workflow.");

                    Point3dCollection sourcePoints = featureLine.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (sourcePoints == null || sourcePoints.Count < 2)
                        throw new InvalidOperationException(
                            "Every selected feature-line piece requires at least two points.");

                    pieces.Add(new FeaturePiece(
                        id,
                        sourcePoints.Cast<Point3d>().ToList()));
                    if (layerId.IsNull)
                    {
                        layerId = featureLine.LayerId;
                        siteId = featureLine.SiteId;
                        styleName = featureLine.StyleName;
                    }
                }

                List<FeaturePiece> ordered = OrderPieces(pieces, gapTolerance, out largestGap);
                List<Point3d> joinedPoints = FlattenPieces(ordered);
                if (joinedPoints.Count < 2)
                    throw new InvalidOperationException("The selected feature lines did not produce a usable path.");

                BlockTable blockTable = (BlockTable)transaction.GetObject(
                    database.BlockTableId, OpenMode.ForRead, false);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                    blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite, false);
                HashSet<string> names = ReadFeatureLineNames(modelSpace, transaction);
                outputName = UniqueName(requestedName, names);

                var points = new Point3dCollection();
                foreach (Point3d point in joinedPoints) points.Add(point);
                var sourcePolyline = new Polyline3d(
                    Poly3dType.SimplePoly,
                    points,
                    false);
                sourcePolyline.SetDatabaseDefaults(database);
                if (!layerId.IsNull) sourcePolyline.LayerId = layerId;
                modelSpace.AppendEntity(sourcePolyline);
                transaction.AddNewlyCreatedDBObject(sourcePolyline, true);

                ObjectId featureLineId = siteId.IsNull
                    ? CivilFeatureLine.Create(outputName, sourcePolyline.ObjectId)
                    : CivilFeatureLine.Create(outputName, sourcePolyline.ObjectId, siteId);
                CivilFeatureLine result = transaction.GetObject(
                    featureLineId, OpenMode.ForWrite, false) as CivilFeatureLine;
                if (result == null)
                    throw new InvalidOperationException(
                        "Civil 3D did not return the healed feature line.");
                if (!layerId.IsNull) result.LayerId = layerId;
                if (!string.IsNullOrWhiteSpace(styleName)) result.StyleName = styleName;
                if (!sourcePolyline.IsErased) sourcePolyline.Erase();

                pieceCount = ordered.Count;
                vertexCount = joinedPoints.Count;
                transaction.Commit();
            }
        }

        private static List<FeaturePiece> OrderPieces(
            IList<FeaturePiece> source,
            double gapTolerance,
            out double largestGap)
        {
            var remaining = source.Skip(1).ToList();
            var ordered = new List<FeaturePiece> { source[0] };
            largestGap = 0.0;

            while (remaining.Count > 0)
            {
                Point3d head = ordered[0].Points[0];
                Point3d tail = ordered[ordered.Count - 1].Points[
                    ordered[ordered.Count - 1].Points.Count - 1];
                Attachment best = null;

                foreach (FeaturePiece candidate in remaining)
                {
                    Point3d start = candidate.Points[0];
                    Point3d end = candidate.Points[candidate.Points.Count - 1];
                    Consider(ref best, candidate, false, false, PlanDistance(tail, start));
                    Consider(ref best, candidate, true, false, PlanDistance(tail, end));
                    Consider(ref best, candidate, false, true, PlanDistance(head, end));
                    Consider(ref best, candidate, true, true, PlanDistance(head, start));
                }

                if (best == null || best.Distance > gapTolerance)
                {
                    double distance = best == null ? double.PositiveInfinity : best.Distance;
                    throw new InvalidOperationException(
                        "The nearest remaining endpoint gap is " +
                        (double.IsInfinity(distance)
                            ? "unavailable"
                            : distance.ToString("0.###", CultureInfo.CurrentCulture)) +
                        ", which exceeds the maximum gap tolerance of " +
                        gapTolerance.ToString("0.###", CultureInfo.CurrentCulture) + ".");
                }

                FeaturePiece piece = best.Reverse
                    ? best.Piece.Reversed()
                    : best.Piece;
                if (best.Prepend) ordered.Insert(0, piece);
                else ordered.Add(piece);
                remaining.Remove(best.Piece);
                largestGap = Math.Max(largestGap, best.Distance);
            }
            return ordered;
        }

        private static void Consider(
            ref Attachment best,
            FeaturePiece piece,
            bool reverse,
            bool prepend,
            double distance)
        {
            if (best == null || distance < best.Distance)
                best = new Attachment(piece, reverse, prepend, distance);
        }

        private static List<Point3d> FlattenPieces(IList<FeaturePiece> pieces)
        {
            var points = new List<Point3d>();
            foreach (FeaturePiece piece in pieces)
            {
                for (int index = 0; index < piece.Points.Count; index++)
                {
                    Point3d point = piece.Points[index];
                    if (points.Count > 0 && index == 0 &&
                        PlanDistance(points[points.Count - 1], point) <= CoincidentTolerance)
                        continue;
                    points.Add(point);
                }
            }
            return points;
        }

        private static HashSet<string> ReadFeatureLineNames(
            BlockTableRecord modelSpace,
            Transaction transaction)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in modelSpace)
            {
                CivilFeatureLine featureLine = transaction.GetObject(
                    id, OpenMode.ForRead, false) as CivilFeatureLine;
                if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name))
                    names.Add(featureLine.Name);
            }
            return names;
        }

        private static string UniqueName(string requested, ISet<string> names)
        {
            string baseName = string.IsNullOrWhiteSpace(requested)
                ? "CE-STEPPED-FL"
                : requested.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (!names.Add(candidate))
            {
                candidate = baseName + " (" +
                    suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private sealed class FeaturePiece
        {
            public FeaturePiece(ObjectId objectId, List<Point3d> points)
            {
                ObjectId = objectId;
                Points = points;
            }

            public ObjectId ObjectId { get; private set; }
            public List<Point3d> Points { get; private set; }

            public FeaturePiece Reversed()
            {
                var points = new List<Point3d>(Points);
                points.Reverse();
                return new FeaturePiece(ObjectId, points);
            }
        }

        private sealed class Attachment
        {
            public Attachment(
                FeaturePiece piece,
                bool reverse,
                bool prepend,
                double distance)
            {
                Piece = piece;
                Reverse = reverse;
                Prepend = prepend;
                Distance = distance;
            }

            public FeaturePiece Piece { get; private set; }
            public bool Reverse { get; private set; }
            public bool Prepend { get; private set; }
            public double Distance { get; private set; }
        }
    }
}
