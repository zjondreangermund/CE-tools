using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CETools.Core;

namespace CETools.Civil3D
{
    /// <summary>
    /// Deterministic straight-route T/X break runtime. Junction finding is performed
    /// by the CAD-independent PlanJunctionPlanner and therefore does not depend on
    /// AutoCAD IntersectWith/GetClosestPointTo behaviour. Existing source entities
    /// are never erased: the original object becomes the first split span and only
    /// the additional spans are appended.
    /// </summary>
    internal static class September04VerifiedJunctionBreakRuntime
    {
        private const double PlanTolerance = 0.01;
        private const double LengthTolerance = 0.00001;

        private sealed class RouteSource
        {
            internal ObjectId Id;
            internal bool IsLine;
            internal PlanPolylinePath Path;
            internal double Length;
        }

        internal static void BreakPolylinesAtJunctions(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = Select(editor);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> selectedIds = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (selectedIds.Count < 2)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: select at least two line/polyline routes.");
                return;
            }

            List<RouteSource> routes = ReadStraightRoutes(document.Database, selectedIds);
            int ignored = Math.Max(0, selectedIds.Count - routes.Count);
            if (routes.Count < 2)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: only {0} usable straight LINE/LWPOLYLINE route(s) were found; ignored={1}.",
                    routes.Count,
                    ignored);
                return;
            }

            PlanJunctionPlan plan;
            try
            {
                plan = PlanJunctionPlanner.Build(
                    routes.Select(route => route.Path).ToList(),
                    PlanTolerance);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during deterministic plan analysis. No source was changed. {0}",
                    exception.Message);
                return;
            }

            int affected = plan.CutsByPath.Count(values => values.Count > 0);
            if (plan.Junctions.Count == 0 || affected == 0)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: no internal T-junctions/crossings found. Selected={0}; usable straight routes={1}; ignored={2}.",
                    selectedIds.Count,
                    routes.Count,
                    ignored);
                return;
            }

            int splitSources = 0;
            int addedSpans = 0;
            int unchanged = 0;
            for (int index = 0; index < routes.Count; index++)
            {
                IReadOnlyList<double> routeCuts = plan.CutsByPath[index];
                if (routeCuts == null || routeCuts.Count == 0) continue;

                RouteSource route = routes[index];
                int created;
                string failure;
                bool success = route.IsLine
                    ? SplitLineAndKeepSource(document.Database, route.Id, routeCuts, out created, out failure)
                    : SplitPolylineAndKeepSource(document.Database, route.Id, routeCuts, out created, out failure);

                if (success)
                {
                    splitSources++;
                    addedSpans += created;
                }
                else
                {
                    unchanged++;
                    editor.WriteMessage(
                        "\nRoute {0} kept unchanged: {1}",
                        route.Id.Handle,
                        string.IsNullOrWhiteSpace(failure) ? "split verification failed" : failure);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS TESTED T/X engine complete. Junctions={0}; sources split={1}; additional spans={2}; unchanged={3}; ignored={4}. Original source handles were kept; no selected source entity was erased.",
                plan.Junctions.Count,
                splitSources,
                addedSpans,
                unchanged,
                ignored);
        }

        private static PromptSelectionResult Select(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect LINE/LWPOLYLINE routes to break-and-keep at T-junctions/crossings: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,LINE")
                }));
        }

        private static List<RouteSource> ReadStraightRoutes(Database database, IEnumerable<ObjectId> ids)
        {
            var routes = new List<RouteSource>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null || entity.IsErased) continue;

                    Line line = entity as Line;
                    if (line != null)
                    {
                        double length = line.Length;
                        if (length <= LengthTolerance) continue;
                        routes.Add(new RouteSource
                        {
                            Id = id,
                            IsLine = true,
                            Length = length,
                            Path = new PlanPolylinePath(new[]
                            {
                                new PlanPoint(line.StartPoint.X, line.StartPoint.Y),
                                new PlanPoint(line.EndPoint.X, line.EndPoint.Y)
                            })
                        });
                        continue;
                    }

                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.Closed || polyline.NumberOfVertices < 2)
                        continue;
                    if (!AllSegmentsStraight(polyline)) continue;

                    var points = new List<PlanPoint>(polyline.NumberOfVertices);
                    for (int vertex = 0; vertex < polyline.NumberOfVertices; vertex++)
                    {
                        Point3d point = polyline.GetPoint3dAt(vertex);
                        points.Add(new PlanPoint(point.X, point.Y));
                    }

                    var path = new PlanPolylinePath(points);
                    double sourceLength = polyline.Length;
                    double tolerance = Math.Max(0.001, sourceLength * 0.0000001);
                    if (Math.Abs(path.Length - sourceLength) > tolerance)
                        continue; // non-world-plan geometry: do not risk a wrong station split.

                    routes.Add(new RouteSource
                    {
                        Id = id,
                        IsLine = false,
                        Length = sourceLength,
                        Path = path
                    });
                }
            }
            return routes;
        }

        private static bool AllSegmentsStraight(Polyline polyline)
        {
            if (polyline == null) return false;
            for (int segment = 0; segment + 1 < polyline.NumberOfVertices; segment++)
            {
                try
                {
                    if (polyline.GetSegmentType(segment) != SegmentType.Line) return false;
                }
                catch
                {
                    try { if (Math.Abs(polyline.GetBulgeAt(segment)) > 1e-10) return false; }
                    catch { return false; }
                }
            }
            return true;
        }

        private static bool SplitPolylineAndKeepSource(
            Database database,
            ObjectId sourceId,
            IReadOnlyList<double> cuts,
            out int addedSpans,
            out string failure)
        {
            addedSpans = 0;
            failure = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline source = transaction.GetObject(sourceId, OpenMode.ForWrite, false) as Polyline;
                if (source == null || source.IsErased || source.Closed || source.NumberOfVertices < 2)
                {
                    failure = "Source is no longer a usable open lightweight polyline.";
                    return false;
                }
                if (LayerLocked(transaction, source.LayerId))
                {
                    failure = "Source layer is locked.";
                    return false;
                }

                List<double> uniqueCuts = NormalizeCuts(cuts, source.Length);
                if (uniqueCuts.Count == 0)
                {
                    failure = "No internal cut stations remained after validation.";
                    return false;
                }

                DBObjectCollection raw = August25StraightPolylineSplitter.TryBuild(source, uniqueCuts);
                if (raw == null)
                {
                    failure = "Deterministic straight-polyline splitter could not build verified spans.";
                    return false;
                }

                var pieces = raw.Cast<DBObject>()
                    .OfType<Polyline>()
                    .Where(piece => piece.NumberOfVertices >= 2 && piece.Length > LengthTolerance)
                    .ToList();
                if (pieces.Count != uniqueCuts.Count + 1)
                {
                    DisposeTransientPieces(pieces);
                    failure = "Split span count did not match the requested T/X cut count.";
                    return false;
                }

                double total = pieces.Sum(piece => piece.Length);
                double verifyTolerance = Math.Max(0.001, source.Length * 0.0000001);
                if (Math.Abs(total - source.Length) > verifyTolerance)
                {
                    DisposeTransientPieces(pieces);
                    failure = "Split length verification failed; source stayed unchanged.";
                    return false;
                }

                BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null)
                {
                    DisposeTransientPieces(pieces);
                    failure = "Source owner space is unavailable.";
                    return false;
                }

                for (int index = 1; index < pieces.Count; index++)
                {
                    Polyline piece = pieces[index];
                    CopyEntityProperties(source, piece);
                    owner.AppendEntity(piece);
                    transaction.AddNewlyCreatedDBObject(piece, true);
                    addedSpans++;
                }

                Polyline firstPiece = pieces[0];
                ReplacePolylineGeometry(source, firstPiece);
                try { source.RecordGraphicsModified(true); } catch { }
                try { firstPiece.Dispose(); } catch { }
                transaction.Commit();
                return true;
            }
        }

        private static bool SplitLineAndKeepSource(
            Database database,
            ObjectId sourceId,
            IReadOnlyList<double> cuts,
            out int addedSpans,
            out string failure)
        {
            addedSpans = 0;
            failure = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Line source = transaction.GetObject(sourceId, OpenMode.ForWrite, false) as Line;
                if (source == null || source.IsErased || source.Length <= LengthTolerance)
                {
                    failure = "Source is no longer a usable line.";
                    return false;
                }
                if (LayerLocked(transaction, source.LayerId))
                {
                    failure = "Source layer is locked.";
                    return false;
                }

                double length = source.Length;
                List<double> uniqueCuts = NormalizeCuts(cuts, length);
                if (uniqueCuts.Count == 0)
                {
                    failure = "No internal cut stations remained after validation.";
                    return false;
                }

                Vector3d direction = (source.EndPoint - source.StartPoint).GetNormal();
                var boundaries = new List<double> { 0.0 };
                boundaries.AddRange(uniqueCuts);
                boundaries.Add(length);

                BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null)
                {
                    failure = "Source owner space is unavailable.";
                    return false;
                }

                Point3d originalStart = source.StartPoint;
                for (int index = 1; index + 1 < boundaries.Count; index++)
                {
                    Point3d start = originalStart + direction * boundaries[index];
                    Point3d end = originalStart + direction * boundaries[index + 1];
                    if (start.DistanceTo(end) <= LengthTolerance) continue;
                    var span = new Line(start, end);
                    CopyEntityProperties(source, span);
                    owner.AppendEntity(span);
                    transaction.AddNewlyCreatedDBObject(span, true);
                    addedSpans++;
                }

                source.StartPoint = originalStart;
                source.EndPoint = originalStart + direction * boundaries[1];
                try { source.RecordGraphicsModified(true); } catch { }
                transaction.Commit();
                return true;
            }
        }

        private static List<double> NormalizeCuts(IEnumerable<double> cuts, double length)
        {
            double endTolerance = Math.Max(LengthTolerance, Math.Min(PlanTolerance, length * 0.000001));
            var values = (cuts ?? Enumerable.Empty<double>())
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .Where(value => value > endTolerance && value < length - endTolerance)
                .OrderBy(value => value)
                .ToList();
            for (int index = values.Count - 1; index > 0; index--)
                if (Math.Abs(values[index] - values[index - 1]) <= endTolerance)
                    values.RemoveAt(index);
            return values;
        }

        private static void ReplacePolylineGeometry(Polyline target, Polyline sourcePiece)
        {
            if (target == null || sourcePiece == null || sourcePiece.NumberOfVertices < 2)
                throw new InvalidOperationException("Verified first split span is invalid.");

            target.Closed = false;
            int desired = sourcePiece.NumberOfVertices;
            while (target.NumberOfVertices > desired)
                target.RemoveVertexAt(target.NumberOfVertices - 1);
            while (target.NumberOfVertices < desired)
            {
                int index = target.NumberOfVertices;
                target.AddVertexAt(index, sourcePiece.GetPoint2dAt(index), 0.0, 0.0, 0.0);
            }
            for (int index = 0; index < desired; index++)
            {
                target.SetPointAt(index, sourcePiece.GetPoint2dAt(index));
                target.SetBulgeAt(index, sourcePiece.GetBulgeAt(index));
                target.SetStartWidthAt(index, sourcePiece.GetStartWidthAt(index));
                target.SetEndWidthAt(index, sourcePiece.GetEndWidthAt(index));
            }
            target.Elevation = sourcePiece.Elevation;
            target.Normal = sourcePiece.Normal;
            target.Closed = false;
        }

        private static void CopyEntityProperties(Entity source, Entity target)
        {
            if (source == null || target == null) return;
            try { target.SetPropertiesFrom(source); } catch { }
            try { target.LayerId = source.LayerId; } catch { }
        }

        private static bool LayerLocked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch
            {
                return true;
            }
        }

        private static void DisposeTransientPieces(IEnumerable<Polyline> pieces)
        {
            foreach (Polyline piece in pieces ?? Enumerable.Empty<Polyline>())
            {
                try { if (piece != null && piece.Database == null) piece.Dispose(); } catch { }
            }
        }
    }
}
