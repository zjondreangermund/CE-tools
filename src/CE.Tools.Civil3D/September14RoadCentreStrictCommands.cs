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
    /// <summary>
    /// September 14 road-centre field correction.
    ///
    /// The strict cleanup intentionally ignores T/X junction vertices when they are
    /// merely intermediate points on an otherwise straight road.  A straight road
    /// therefore finishes with start/end only.  Arc transition vertices are always
    /// retained, so one horizontal arc is represented by start, BC, EC and end.
    /// Genuine line-line direction changes are also retained rather than silently
    /// changing the road geometry.
    /// </summary>
    public sealed class September14RoadCentreStrictCommands
    {
        private const double DirectionTolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_ROADCENTRECLEANSTRICT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CleanRoadCentresStrict()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect open road-centre polylines to keep only start/end, BC/EC and genuine bends: ",
                        MessageForRemoval = "\nRemove road-centre polyline from cleanup: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[]
                    {
                        new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                    }));
            }

            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                return;

            int cleaned = 0;
            int unchanged = 0;
            int skippedClosed = 0;
            int failed = 0;
            int removedVertices = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                            if (polyline == null)
                            {
                                failed++;
                                continue;
                            }
                            if (polyline.Closed)
                            {
                                skippedClosed++;
                                continue;
                            }
                            if (polyline.NumberOfVertices <= 2)
                            {
                                unchanged++;
                                continue;
                            }

                            List<int> remove = FindRedundantVertices(polyline);
                            if (remove.Count == 0)
                            {
                                unchanged++;
                                continue;
                            }

                            polyline.UpgradeOpen();
                            for (int index = remove.Count - 1; index >= 0; index--)
                                polyline.RemoveVertexAt(remove[index]);

                            removedVertices += remove.Count;
                            cleaned++;
                            transaction.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        editor.WriteMessage("\nCE_ROADCENTRECLEANSTRICT skipped {0}: {1}", id, ex.Message);
                    }
                }
            }

            editor.WriteMessage(
                "\nCE_ROADCENTRECLEANSTRICT complete. Cleaned={0}; vertices removed={1}; unchanged={2}; closed skipped={3}; failed={4}. Straight roads keep start/end only; arc roads keep BC/EC.",
                cleaned,
                removedVertices,
                unchanged,
                skippedClosed,
                failed);
        }

        internal static List<int> FindRedundantVertices(Polyline polyline)
        {
            var remove = new List<int>();
            if (polyline == null || polyline.Closed || polyline.NumberOfVertices <= 2)
                return remove;

            // Endpoints are never candidates.  Every vertex touching an arc is a
            // BC/EC transition and must remain.  Only a truly straight line-line
            // intermediate vertex is removed, regardless of a T/X road junction.
            for (int vertexIndex = 1; vertexIndex < polyline.NumberOfVertices - 1; vertexIndex++)
            {
                SegmentType previousType = SafeSegmentType(polyline, vertexIndex - 1);
                SegmentType nextType = SafeSegmentType(polyline, vertexIndex);

                if (previousType == SegmentType.Arc || nextType == SegmentType.Arc)
                    continue;

                if (previousType != SegmentType.Line || nextType != SegmentType.Line)
                    continue;

                Point2d before = polyline.GetPoint2dAt(vertexIndex - 1);
                Point2d current = polyline.GetPoint2dAt(vertexIndex);
                Point2d after = polyline.GetPoint2dAt(vertexIndex + 1);
                if (IsStraightThrough(before, current, after))
                    remove.Add(vertexIndex);
            }

            return remove;
        }

        private static SegmentType SafeSegmentType(Polyline polyline, int segmentIndex)
        {
            try { return polyline.GetSegmentType(segmentIndex); }
            catch { return SegmentType.Point; }
        }

        private static bool IsStraightThrough(Point2d before, Point2d current, Point2d after)
        {
            Vector2d incoming = current - before;
            Vector2d outgoing = after - current;
            double incomingLength = incoming.Length;
            double outgoingLength = outgoing.Length;
            if (incomingLength <= 1e-12 || outgoingLength <= 1e-12)
                return false;

            double cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
            double dot = incoming.X * outgoing.X + incoming.Y * outgoing.Y;
            double scale = incomingLength * outgoingLength;
            return dot > 0.0 && Math.Abs(cross) <= DirectionTolerance * scale;
        }
    }
}
