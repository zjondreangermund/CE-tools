using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.HatchOverlapFixCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Non-overlap repair for compatible SOLID hatch footprints. Every connected
    /// component is processed in its own transaction; failures roll back without
    /// changing the original hatches. Curved polyline loops keep their bulges.
    /// Islands, curved edge-defined loops, non-SOLID fills and mixed styles are skipped.
    /// </summary>
    public sealed class HatchOverlapFixCommands
    {
        private sealed class Candidate
        {
            internal ObjectId Id;
            internal Extents3d Bounds;
            internal string StyleKey;
        }

        [CommandMethod("CE_TOOLS", "CE_HATCHFIXOVERLAP",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void FixSelected() { Run(false); }

        [CommandMethod("CE_TOOLS", "CE_HATCHFIXOVERLAPALL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void FixAll() { Run(true); }

        private static void Run(bool all)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var candidates = new List<Candidate>();
            var selection = new HashSet<ObjectId>();
            if (!all)
            {
                PromptSelectionResult result = doc.Editor.SelectImplied();
                if (result.Status != PromptStatus.OK || result.Value == null ||
                    result.Value.Count == 0)
                    result = doc.Editor.GetSelection(
                        new PromptSelectionOptions {
                            MessageForAdding = "\nSelect overlapping SOLID hatches: ",
                            AllowDuplicates = false
                        }, new SelectionFilter(new[] {
                            new TypedValue((int)DxfCode.Start, "HATCH")
                        }));
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                selection = new HashSet<ObjectId>(result.Value.GetObjectIds());
            }

            int unsupported = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord model = bt == null ? null :
                    tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (model == null) return;
                foreach (ObjectId id in model)
                {
                    if (!all && !selection.Contains(id)) continue;
                    Hatch hatch = tr.GetObject(id, OpenMode.ForRead, false) as Hatch;
                    if (hatch == null) continue;
                    if (!Supported(hatch)) { unsupported++; continue; }
                    Extents3d ext;
                    try { ext = hatch.GeometricExtents; }
                    catch { unsupported++; continue; }
                    candidates.Add(new Candidate {
                        Id = id,
                        Bounds = ext,
                        StyleKey = StyleKey(hatch)
                    });
                }
            }
            // An overlap can only happen between footprints whose bounding
            // rectangles touch. Do not alter solitary or unrelated hatches.
            var groups = new List<List<Candidate>>();
            foreach (var styleGroup in candidates.GroupBy(c => c.StyleKey))
            {
                var remaining = styleGroup.ToList();
                while (remaining.Count > 0)
                {
                    var component = new List<Candidate> { remaining[0] };
                    remaining.RemoveAt(0);
                    bool added;
                    do {
                        added = false;
                        for (int i = remaining.Count - 1; i >= 0; i--)
                        {
                            if (!component.Any(c => Touch(c.Bounds, remaining[i].Bounds))) continue;
                            component.Add(remaining[i]);
                            remaining.RemoveAt(i);
                            added = true;
                        }
                    } while (added);
                    if (component.Count > 1) groups.Add(component);
                }
            }
            doc.Editor.WriteMessage(
                "\nCE_HATCHFIXOVERLAP: eligible SOLID hatches={0}, potential overlap groups={1}, unsupported skipped={2}.",
                candidates.Count, groups.Count, unsupported);
            if (groups.Count == 0) return;
            var opts = new PromptKeywordOptions(
                "\nUnion compatible fills, preserving the original 75% or other transparency? [Yes/No] <No>: ");
            opts.Keywords.Add("Yes");
            opts.Keywords.Add("No");
            opts.Keywords.Default = "No";
            opts.AllowNone = true;
            PromptResult answer = doc.Editor.GetKeywords(opts);
            if (answer.Status != PromptStatus.OK ||
                !string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
                return;

            int fixedGroups = 0, failedGroups = 0, replaced = 0;
            foreach (List<Candidate> component in groups)
            {
                try
                {
                    using (doc.LockDocument())
                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var hatches = component.Select(c => tr.GetObject(c.Id, OpenMode.ForRead, false) as Hatch)
                            .Where(h => h != null).ToList();
                        if (hatches.Count != component.Count)
                            throw new InvalidOperationException("A source hatch disappeared.");
                        ObjectId ownerId = hatches[0].OwnerId;
                        if (hatches.Any(h => h.OwnerId != ownerId))
                            throw new InvalidOperationException("Different owner spaces cannot be combined.");
                        BlockTableRecord owner = tr.GetObject(ownerId, OpenMode.ForWrite) as BlockTableRecord;
                        if (owner == null) throw new InvalidOperationException("Owner block not writable.");
                        var regions = new List<Region>();
                        foreach (Hatch h in hatches)
                        {
                            HatchLoop loop = h.GetLoopAt(0);
                            Polyline poly = ReadBoundary(loop);
                            if (poly == null)
                                throw new InvalidOperationException("Unsupported or unclosed hatch boundary; original left unchanged.");
                            var curves = new DBObjectCollection { poly };
                            try
                            {
                                DBObjectCollection made = Region.CreateFromCurves(curves);
                                if (made.Count != 1 || !(made[0] is Region))
                                {
                                    foreach (DBObject item in made) item.Dispose();
                                    throw new InvalidOperationException("Hatch boundary does not make one valid region.");
                                }
                                regions.Add((Region)made[0]);
                            }
                            finally { poly.Dispose(); }
                        }

                        try
                        {
                            double totalSourceArea = regions.Sum(region => region.Area);
                            Region merged = regions[0];
                            for (int i = 1; i < regions.Count; i++)
                                merged.BooleanOperation(BooleanOperationType.BoolUnite, regions[i]);
                            if (totalSourceArea - merged.Area < Math.Max(0.000001, totalSourceArea * 1e-9))
                                throw new InvalidOperationException("No actual shared hatch area; no change necessary.");
                            // Create nonassociative hatch from the unioned footprint.
                            // If the native hatch API refuses the region loop, the
                            // transaction rolls back and originals remain intact.
                            owner.AppendEntity(merged);
                            tr.AddNewlyCreatedDBObject(merged, true);
                            var source = hatches[0];
                            var result = new Hatch();
                            result.SetDatabaseDefaults(doc.Database);
                            result.LayerId = source.LayerId;
                            result.Color = source.Color;
                            result.Transparency = source.Transparency;
                            owner.AppendEntity(result);
                            tr.AddNewlyCreatedDBObject(result, true);
                            result.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                            result.Associative = false;
                            result.AppendLoop(HatchLoopTypes.Outermost,
                                new ObjectIdCollection { merged.ObjectId });
                            result.EvaluateHatch(true);
                            // Native hatch has its own boundary geometry once
                            // evaluated; retain the region as a nonplot boundary
                            // rather than risking deletion of a required source.
                            merged.Visible = false;
                            foreach (Hatch h in hatches) {
                                h.UpgradeOpen();
                                h.Erase();
                            }
                            tr.Commit();
                            fixedGroups++;
                            replaced += hatches.Count;
                        }
                        finally
                        {
                            foreach (Region reg in regions)
                                if (reg.ObjectId.IsNull) reg.Dispose();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    failedGroups++;
                    doc.Editor.WriteMessage("\n  Overlap group skipped unchanged: {0}", ex.Message);
                }
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage(
                "\nCE_HATCHFIXOVERLAP completed: repaired groups={0}, source hatches replaced={1}, groups safely skipped={2}. Check the junctions before saving.",
                fixedGroups, replaced, failedGroups);
        }

        private static bool Supported(Hatch h)
        {
            if (!h.IsSolidFill || h.NumberOfLoops != 1 || h.IsErased) return false;
            HatchLoop loop;
            try { loop = h.GetLoopAt(0); }
            catch { return false; }
            if (loop.IsPolyline)
                return loop.Polyline != null && loop.Polyline.Count >= 3;
            // Imported DXF/Civil 3D hatches frequently store their boundaries as
            // ordered line edges instead of polyline loops. Accept these only if
            // they form one closed polygon, with no spline/ellipse/island geometry.
            return IsClosedLineEdgeLoop(loop);
        }

        private static bool IsClosedLineEdgeLoop(HatchLoop loop)
        {
            if (loop == null || loop.IsPolyline || loop.Curves == null ||
                loop.Curves.Count < 3) return false;
            Point2d first = Point2d.Origin;
            Point2d previous = Point2d.Origin;
            int count = 0;
            const double tolerance = 0.001;
            foreach (Curve2d edge in loop.Curves)
            {
                LineSegment2d segment = edge as LineSegment2d;
                if (segment == null) return false;
                if (segment.StartPoint.GetDistanceTo(segment.EndPoint) < tolerance)
                    return false;
                if (count == 0) first = segment.StartPoint;
                else if (previous.GetDistanceTo(segment.StartPoint) > tolerance)
                    return false;
                previous = segment.EndPoint;
                count++;
            }
            return count >= 3 && previous.GetDistanceTo(first) <= tolerance;
        }

        private static Polyline ReadBoundary(HatchLoop loop)
        {
            if (loop == null) return null;
            var poly = new Polyline();
            if (loop.IsPolyline)
            {
                int i = 0;
                foreach (BulgeVertex vertex in loop.Polyline)
                    poly.AddVertexAt(i++, vertex.Vertex, vertex.Bulge, 0, 0);
                if (i < 3) { poly.Dispose(); return null; }
            }
            else
            {
                if (!IsClosedLineEdgeLoop(loop)) { poly.Dispose(); return null; }
                int i = 0;
                foreach (Curve2d edge in loop.Curves)
                {
                    LineSegment2d line = (LineSegment2d)edge;
                    poly.AddVertexAt(i++, line.StartPoint, 0.0, 0.0, 0.0);
                }
            }
            poly.Closed = true;
            return poly;
        }

        private static string StyleKey(Hatch h)
        {
            return h.OwnerId.Handle.ToString() + "|" + h.LayerId.Handle.ToString() +
                "|" + h.Color.ColorIndex.ToString() + "|" + h.Color.ColorMethod.ToString() + "|" + h.Transparency.Alpha.ToString() +
                "|" + h.PatternName;
        }

        private static bool Touch(Extents3d a, Extents3d b)
        {
            const double eps = 0.001;
            return a.MinPoint.X <= b.MaxPoint.X + eps &&
                   a.MaxPoint.X >= b.MinPoint.X - eps &&
                   a.MinPoint.Y <= b.MaxPoint.Y + eps &&
                   a.MaxPoint.Y >= b.MinPoint.Y - eps;
        }
    }
}
