using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September14RoadCentreStrictCommands))]

namespace CETools.Civil3D
{
    public sealed class September14RoadCentreStrictCommands
    {
        private const double DirectionTolerance = 1e-9;

        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADCENTRECLEANSTRICT",
            CommandFlags.Modal |
            CommandFlags.UsePickSet |
            CommandFlags.Redraw)]
        public void CleanRoadCentresStrict()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;

            if (document == null)
                return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Strict Road-Centre Cleanup",
                "Remove only redundant intermediate vertices. Endpoints, " +
                "arc BC/EC transitions and genuine bends remain unchanged. " +
                "Open and closed polylines can be processed separately or together.");

            settings.AddChoice(
                "PolylineType",
                "01 Selection",
                "Polyline type",
                "Open polylines",
                "Choose which lightweight polylines the cleanup command processes.",
                new[]
                {
                    "Open polylines",
                    "Closed polylines",
                    "Open and closed polylines"
                });

            settings.AddPositiveDouble(
                "MinimumDeflection",
                "02 Cleanup",
                "Minimum deflection angle (degrees)",
                1.0,
                "A line-line vertex is removed when its deflection is at or below " +
                "this angle. Larger bends are retained.");

            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            string polylineType = settings.Text("PolylineType");

            bool includeOpen =
                string.Equals(polylineType, "Open polylines",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(polylineType, "Open and closed polylines",
                    StringComparison.OrdinalIgnoreCase);

            bool includeClosed =
                string.Equals(polylineType, "Closed polylines",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(polylineType, "Open and closed polylines",
                    StringComparison.OrdinalIgnoreCase);

            double minimumDeflection =
                Math.Max(0.0, settings.Double("MinimumDeflection", 1.0));

            Editor editor = document.Editor;

            PromptSelectionResult selection = editor.SelectImplied();

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            includeOpen && includeClosed
                                ? "\nSelect open and closed road-centre polylines to clean: "
                                : includeClosed
                                    ? "\nSelect CLOSED road-centre polylines to clean: "
                                    : "\nSelect OPEN road-centre polylines to keep only start/end, BC/EC and genuine bends: ",

                        MessageForRemoval =
                            "\nRemove road-centre polyline from cleanup: ",

                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[]
                    {
                        new TypedValue(
                            (int)DxfCode.Start,
                            "LWPOLYLINE")
                    }));
            }

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return;

            int cleaned = 0;
            int unchanged = 0;
            int skippedType = 0;
            int failed = 0;
            int removedVertices = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId id in
                    selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        using (Transaction transaction =
                            document.Database.TransactionManager.StartTransaction())
                        {
                            Polyline polyline =
                                transaction.GetObject(
                                    id,
                                    OpenMode.ForRead,
                                    false) as Polyline;

                            if (polyline == null)
                            {
                                failed++;
                                continue;
                            }

                            if ((polyline.Closed && !includeClosed) ||
                                (!polyline.Closed && !includeOpen))
                            {
                                skippedType++;
                                continue;
                            }

                            if (polyline.NumberOfVertices <=
                                (polyline.Closed ? 3 : 2))
                            {
                                unchanged++;
                                continue;
                            }

                            List<int> remove =
                                FindRedundantVertices(
                                    polyline,
                                    minimumDeflection);

                            // A closed polyline must retain at least
                            // three vertices.
                            if (polyline.Closed &&
                                polyline.NumberOfVertices - remove.Count < 3)
                            {
                                remove = remove
                                    .Take(polyline.NumberOfVertices - 3)
                                    .ToList();
                            }

                            if (remove.Count == 0)
                            {
                                unchanged++;
                                continue;
                            }

                            polyline.UpgradeOpen();

                            // Remove in descending order so indices
                            // remain valid.
                            for (int index = remove.Count - 1;
                                 index >= 0;
                                 index--)
                            {
                                polyline.RemoveVertexAt(remove[index]);
                            }

                            removedVertices += remove.Count;
                            cleaned++;

                            transaction.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        failed++;

                        editor.WriteMessage(
                            "\nCE_ROADCENTRECLEANSTRICT skipped {0}: {1}",
                            id,
                            ex.Message);
                    }
                }
            }

            editor.WriteMessage(
                "\nCE_ROADCENTRECLEANSTRICT complete. " +
                "Cleaned={0}; vertices removed={1}; unchanged={2}; " +
                "skipped by open/closed option={3}; failed={4}; " +
                "minimum deflection={5:0.###} degrees. " +
                "Endpoints (open), arc BC/EC transitions and genuine bends are retained.",
                cleaned,
                removedVertices,
                unchanged,
                skippedType,
                failed,
                minimumDeflection);
        }

        internal static List<int> FindRedundantVertices(
            Polyline polyline)
        {
            return FindRedundantVertices(polyline, 0.0);
        }

        internal static List<int> FindRedundantVertices(
            Polyline polyline,
            double minimumDeflectionDegrees)
        {
            var remove = new List<int>();

            if (polyline == null ||
                polyline.NumberOfVertices <=
                (polyline.Closed ? 3 : 2))
                return remove;

            int vertexCount = polyline.NumberOfVertices;

            int firstCandidate = polyline.Closed ? 0 : 1;
            int lastCandidate =
                polyline.Closed ? vertexCount - 1 : vertexCount - 2;

            for (int vertexIndex = firstCandidate;
                 vertexIndex <= lastCandidate;
                 vertexIndex++)
            {
                int previousSegment =
                    (vertexIndex - 1 + vertexCount) % vertexCount;

                int nextSegment = vertexIndex;

                SegmentType? previousType =
                    SafeSegmentType(polyline, previousSegment);

                SegmentType? nextType =
                    SafeSegmentType(polyline, nextSegment);

                if (!previousType.HasValue ||
                    !nextType.HasValue)
                    continue;

                // Preserve all arc transition vertices.
                if (previousType.Value == SegmentType.Arc ||
                    nextType.Value == SegmentType.Arc)
                    continue;

                if (previousType.Value != SegmentType.Line ||
                    nextType.Value != SegmentType.Line)
                    continue;

                int beforeIndex =
                    (vertexIndex - 1 + vertexCount) % vertexCount;

                int afterIndex =
                    (vertexIndex + 1) % vertexCount;

                Point2d before =
                    polyline.GetPoint2dAt(beforeIndex);

                Point2d current =
                    polyline.GetPoint2dAt(vertexIndex);

                Point2d after =
                    polyline.GetPoint2dAt(afterIndex);

                if (IsStraightThrough(
                    before,
                    current,
                    after,
                    minimumDeflectionDegrees))
                {
                    remove.Add(vertexIndex);
                }
            }

            return remove;
        }

        private static SegmentType? SafeSegmentType(
            Polyline polyline,
            int segmentIndex)
        {
            try
            {
                return polyline.GetSegmentType(segmentIndex);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsStraightThrough(
            Point2d before,
            Point2d current,
            Point2d after,
            double minimumDeflectionDegrees)
        {
            Vector2d incoming = current - before;
            Vector2d outgoing = after - current;

            double incomingLength = incoming.Length;
            double outgoingLength = outgoing.Length;

            if (incomingLength <= 1e-12 ||
                outgoingLength <= 1e-12)
                return false;

            double dot =
                incoming.X * outgoing.X +
                incoming.Y * outgoing.Y;

            double scale =
                incomingLength * outgoingLength;

            if (dot <= 0.0)
                return false;

            double cosine =
                Math.Max(-1.0,
                    Math.Min(1.0, dot / scale));

            double deflection =
                Math.Acos(cosine) * 180.0 / Math.PI;

            return deflection <=
                Math.Max(0.0, minimumDeflectionDegrees) + 1e-10;
        }
    }
}