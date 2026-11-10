using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates only the missing connector/extension geometry between selected
    /// straight legs from LINE/LWPOLYLINE entities. Original source entities are
    /// never edited, erased, split, trimmed, exploded or reconstructed. Existing
    /// polylines remain exactly as selected; new entities are only the extensions
    /// needed for perpendicular crossings, T-junctions, corners and collinear gaps.
    /// </summary>
    internal static class OrthogonalPolylineNetworkCommands
    {
        private const double Epsilon = 1e-8;
        private static double _lastMaximumDistance = 20.0;
        private static double _lastAngleTolerance = 2.0;
        private static string _lastOutputLayer = "CE-ORTHOGONAL-NETWORK";
        private static bool _lastAllowCrossLayer = false;

        private sealed class SourceSegment
        {
            internal ObjectId SourceId;
            internal string SourceLayer;
            internal Point2d A;
            internal Point2d B;
            internal double Elevation;
        }

        private sealed class OutputEdge
        {
            internal Point2d A;
            internal Point2d B;
            internal double Elevation;
            internal ObjectId SourceId;

            internal OutputEdge(Point2d a, Point2d b, double elevation, ObjectId sourceId)
            {
                A = a;
                B = b;
                Elevation = elevation;
                SourceId = sourceId;
            }
        }

        internal static void Create(Document document)
        {
            if (document == null || document.Database == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult implied = editor.SelectImplied();
            editor.SetImpliedSelection(new ObjectId[0]);

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Create Orthogonal Polyline Network",
                "Keep every selected original LINE/polyline exactly unchanged. Create ONLY new connector segments needed to extend endpoints to perpendicular mid-span crossings, T-junctions, perpendicular corners and compatible collinear gaps. No original entity is split, trimmed, exploded, replaced or erased.");

            settings.AddPositiveDouble(
                "MaximumDistance",
                "01 Geometry",
                "Maximum extension / connection distance",
                _lastMaximumDistance,
                "Maximum distance a new connector may extend from an original endpoint to a perpendicular crossing, T-junction, corner or collinear continuation.");

            settings.AddPositiveDouble(
                "AngleTolerance",
                "01 Geometry",
                "Perpendicular / parallel angle tolerance (degrees)",
                _lastAngleTolerance,
                "Maximum angular deviation used to recognise perpendicular crossings and collinear continuations. Recommended: 1 to 3 degrees.");

            settings.AddText(
                "OutputLayer",
                "02 Output",
                "Output network layer",
                _lastOutputLayer,
                "Generated connected network is created on this layer. Source entities remain unchanged.");

            settings.AddChoice(
                "CrossLayer",
                "01 Geometry",
                "Connections between different source layers",
                _lastAllowCrossLayer ? "Allow selected layers" : "Same layer only",
                "Same layer only prevents accidental connections between separate utility disciplines. Enable only when the selected layers belong to one network.",
                new[] { "Same layer only", "Allow selected layers" });

            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double maximumDistance = Math.Max(0.001,
                settings.Double("MaximumDistance", _lastMaximumDistance));
            double angleTolerance = Math.Max(0.1,
                Math.Min(15.0, settings.Double("AngleTolerance", _lastAngleTolerance)));
            string outputLayer = string.IsNullOrWhiteSpace(settings.Text("OutputLayer"))
                ? "CE-ORTHOGONAL-NETWORK"
                : settings.Text("OutputLayer").Trim();
            bool allowCrossLayer = string.Equals(
                settings.Text("CrossLayer"), "Allow selected layers",
                StringComparison.OrdinalIgnoreCase);

            _lastMaximumDistance = maximumDistance;
            _lastAngleTolerance = angleTolerance;
            _lastOutputLayer = outputLayer;
            _lastAllowCrossLayer = allowCrossLayer;

            ObjectId[] selectedIds;
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                selectedIds = implied.Value.GetObjectIds();
            }
            else
            {
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,LINE")
                });

                PromptSelectionResult selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple straight Lines / lightweight Polylines (Window/Crossing; Enter to finish): ",
                        MessageForRemoval = "\nRemove objects from selection: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    }, filter);

                if (selection.Status != PromptStatus.OK || selection.Value == null)
                {
                    editor.WriteMessage("\nCE_CREATEORTHOGONALNETWORK cancelled. No source geometry was changed.");
                    return;
                }

                selectedIds = selection.Value.GetObjectIds();
            }

            selectedIds = selectedIds
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToArray();

            var segments = new List<SourceSegment>();
            int skippedObjects = 0;
            int skippedArcSegments = 0;
            int skippedZeroLength = 0;

            using (Transaction readTransaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds)
                {
                    Entity entity = null;
                    try { entity = readTransaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }

                    if (entity == null)
                    {
                        skippedObjects++;
                        continue;
                    }

                    Polyline polyline = entity as Polyline;
                    Line line = entity as Line;

                    if (polyline != null)
                    {
                        if (polyline.Normal.DotProduct(Vector3d.ZAxis) < 0.999999)
                        {
                            skippedObjects++;
                            continue;
                        }

                        int vertexCount = polyline.NumberOfVertices;
                        if (vertexCount < 2)
                        {
                            skippedObjects++;
                            continue;
                        }

                        // Process every straight leg of open OR closed lightweight
                        // polylines. A multi-vertex source is decomposed into its
                        // constituent legs while retaining the source ObjectId so
                        // the pairwise solver does not create false self-crossings.
                        int segmentCount = polyline.Closed ? vertexCount : vertexCount - 1;
                        int acceptedForPolyline = 0;
                        for (int vertex = 0; vertex < segmentCount; vertex++)
                        {
                            int next = (vertex + 1) % vertexCount;
                            if (Math.Abs(polyline.GetBulgeAt(vertex)) > 1e-10)
                            {
                                skippedArcSegments++;
                                continue;
                            }

                            Point2d a = polyline.GetPoint2dAt(vertex);
                            Point2d b = polyline.GetPoint2dAt(next);
                            if (a.GetDistanceTo(b) <= Epsilon)
                            {
                                skippedZeroLength++;
                                continue;
                            }

                            segments.Add(new SourceSegment
                            {
                                SourceId = id,
                                SourceLayer = entity.Layer,
                                A = a,
                                B = b,
                                Elevation = polyline.Elevation
                            });
                            acceptedForPolyline++;
                        }

                        if (acceptedForPolyline == 0)
                            skippedObjects++;
                    }
                    else if (line != null)
                    {
                        if (Math.Abs(line.StartPoint.Z - line.EndPoint.Z) > 1e-6)
                        {
                            skippedObjects++;
                            continue;
                        }

                        Point2d a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                        Point2d b = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                        if (a.GetDistanceTo(b) <= Epsilon)
                        {
                            skippedZeroLength++;
                            continue;
                        }

                        segments.Add(new SourceSegment
                        {
                            SourceId = id,
                            SourceLayer = entity.Layer,
                            A = a,
                            B = b,
                            Elevation = line.StartPoint.Z
                        });
                    }
                    else
                    {
                        skippedObjects++;
                    }
                }

                readTransaction.Commit();
            }

            if (segments.Count < 2)
            {
                editor.WriteMessage(
                    "\nCE_CREATEORTHOGONALNETWORK: fewer than two usable straight legs were selected. Arc legs and non-XY objects are not processed.");
                return;
            }

            // Do not force one global direction pair onto the whole selection.
            // A drawing may contain several independently rotated orthogonal
            // groups. Preserve every source leg and solve pairs geometrically.
            // This command creates connector-only entities; it never rebuilds
            // the original source polylines from split segments.
            double angleLimit = angleTolerance * Math.PI / 180.0;

            double perpendicularSinTolerance = Math.Sin(angleLimit);
            double parallelSinTolerance = perpendicularSinTolerance;
            double lateralTolerance = Math.Max(0.001, Math.Min(0.05, maximumDistance * 0.001));

            int perpendicularIntersections = 0;
            int collinearConnectors = 0;
            var connectorEdges = new List<OutputEdge>();

            for (int i = 0; i < segments.Count; i++)
            {
                SourceSegment first = segments[i];
                Vector2d r = new Vector2d(
                    first.B.X - first.A.X,
                    first.B.Y - first.A.Y);
                double firstLength = r.Length;
                if (firstLength <= Epsilon) continue;
                Vector2d firstUnit = new Vector2d(
                    r.X / firstLength,
                    r.Y / firstLength);

                for (int j = i + 1; j < segments.Count; j++)
                {
                    SourceSegment second = segments[j];
                    if (first.SourceId == second.SourceId) continue;
                    if (!allowCrossLayer &&
                        !string.Equals(first.SourceLayer, second.SourceLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Math.Abs(first.Elevation - second.Elevation) > 1e-6) continue;

                    Vector2d s = new Vector2d(
                        second.B.X - second.A.X,
                        second.B.Y - second.A.Y);
                    double secondLength = s.Length;
                    if (secondLength <= Epsilon) continue;
                    Vector2d secondUnit = new Vector2d(
                        s.X / secondLength,
                        s.Y / secondLength);

                    double absCross = Math.Abs(Cross(firstUnit, secondUnit));
                    double absDot = Math.Abs(firstUnit.DotProduct(secondUnit));

                    // Perpendicular pair: intersect the infinite support lines,
                    // but accept only if each required extension is within range.
                    if (absDot <= perpendicularSinTolerance)
                    {
                        double denominator = Cross(r, s);
                        if (Math.Abs(denominator) <= Epsilon) continue;

                        Vector2d delta = new Vector2d(
                            second.A.X - first.A.X,
                            second.A.Y - first.A.Y);
                        double t = Cross(delta, s) / denominator;
                        double u = Cross(delta, r) / denominator;

                        double firstOutside = DistanceOutsideUnitInterval(t) * firstLength;
                        double secondOutside = DistanceOutsideUnitInterval(u) * secondLength;

                        if (firstOutside <= maximumDistance + Epsilon &&
                            secondOutside <= maximumDistance + Epsilon)
                        {
                            Point2d intersection = new Point2d(
                                first.A.X + r.X * t,
                                first.A.Y + r.Y * t);

                            // Never rebuild or split the selected source legs.
                            // Add a connector only for the portion that lies
                            // beyond an original endpoint. If the intersection
                            // is already on a source segment, that segment is
                            // left completely untouched.
                            bool addedExtension = false;

                            if (t < 0.0 || t > 1.0)
                            {
                                Point2d firstEndpoint = t < 0.0 ? first.A : first.B;
                                if (firstEndpoint.GetDistanceTo(intersection) > Epsilon)
                                {
                                    connectorEdges.Add(new OutputEdge(
                                        firstEndpoint, intersection, first.Elevation, first.SourceId));
                                    addedExtension = true;
                                }
                            }

                            if (u < 0.0 || u > 1.0)
                            {
                                Point2d secondEndpoint = u < 0.0 ? second.A : second.B;
                                if (secondEndpoint.GetDistanceTo(intersection) > Epsilon)
                                {
                                    connectorEdges.Add(new OutputEdge(
                                        secondEndpoint, intersection, second.Elevation, second.SourceId));
                                    addedExtension = true;
                                }
                            }

                            if (addedExtension)
                                perpendicularIntersections++;
                        }

                        continue;
                    }

                    // Parallel / collinear continuation: connect only segments
                    // on effectively the same support line, not adjacent roads.
                    if (absCross <= parallelSinTolerance)
                    {
                        Vector2d offsetA = new Vector2d(
                            second.A.X - first.A.X,
                            second.A.Y - first.A.Y);
                        Vector2d offsetB = new Vector2d(
                            second.B.X - first.A.X,
                            second.B.Y - first.A.Y);
                        double lateralA = Math.Abs(Cross(firstUnit, offsetA));
                        double lateralB = Math.Abs(Cross(firstUnit, offsetB));
                        if (lateralA > lateralTolerance || lateralB > lateralTolerance) continue;

                        // Use a common origin for stable interval projection.
                        double b0 = offsetA.DotProduct(firstUnit);
                        double b1 = offsetB.DotProduct(firstUnit);
                        double firstMin = Math.Min(0.0, firstLength);
                        double firstMax = Math.Max(0.0, firstLength);
                        double secondMin = Math.Min(b0, b1);
                        double secondMax = Math.Max(b0, b1);

                        Point2d connectorA;
                        Point2d connectorB;
                        double gap;

                        if (firstMax < secondMin)
                        {
                            gap = secondMin - firstMax;
                            connectorA = first.B;
                            connectorB = Math.Abs(b0 - secondMin) <= Math.Abs(b1 - secondMin)
                                ? second.A : second.B;
                        }
                        else if (secondMax < firstMin)
                        {
                            gap = firstMin - secondMax;
                            connectorA = Math.Abs(b0 - secondMax) <= Math.Abs(b1 - secondMax)
                                ? second.A : second.B;
                            connectorB = first.A;
                        }
                        else
                        {
                            continue;
                        }

                        if (gap > Epsilon && gap <= maximumDistance + Epsilon)
                        {
                            connectorEdges.Add(new OutputEdge(
                                connectorA, connectorB, first.Elevation, first.SourceId));
                            collinearConnectors++;
                        }
                    }
                }
            }

            // Output ONLY portions of connectors that are not already covered
            // by any selected original segment or previously accepted connector.
            // This removes complete and partial duplicates before entities are
            // created, while keeping all selected source geometry untouched.
            var uniqueOutputEdges = new List<OutputEdge>();
            int duplicateOrOverlappingPartsSkipped = 0;
            foreach (OutputEdge candidate in connectorEdges)
            {
                List<OutputEdge> uncovered = GetUncoveredConnectorParts(
                    candidate, segments, uniqueOutputEdges, lateralTolerance);
                if (uncovered.Count == 0)
                {
                    duplicateOrOverlappingPartsSkipped++;
                    continue;
                }

                if (uncovered.Count != 1 ||
                    uncovered[0].A.GetDistanceTo(candidate.A) > lateralTolerance ||
                    uncovered[0].B.GetDistanceTo(candidate.B) > lateralTolerance)
                    duplicateOrOverlappingPartsSkipped++;

                foreach (OutputEdge part in uncovered)
                {
                    string key = EdgeKey(part.A, part.B, part.Elevation);
                    if (!uniqueOutputEdges.Any(existing =>
                        string.Equals(EdgeKey(existing.A, existing.B, existing.Elevation),
                            key, StringComparison.Ordinal)))
                        uniqueOutputEdges.Add(part);
                    else
                        duplicateOrOverlappingPartsSkipped++;
                }
            }

            if (uniqueOutputEdges.Count == 0)
            {
                editor.WriteMessage("\nCE_CREATEORTHOGONALNETWORK: no output geometry could be generated.");
                return;
            }

            editor.WriteMessage(
                "\nCE_CREATEORTHOGONALNETWORK preview: source straight legs checked={0}; perpendicular endpoint extensions for crossings / T-junctions / corners={1}; collinear gap connectors={2}; NEW connector segments only={3}; skipped objects={4}; skipped arc legs={5}; skipped zero-length legs={6}; maximum extension distance={7:0.###}; perpendicular tolerance={8:0.###} degrees; original selected entities will remain unchanged; cross-layer={9}.",
                segments.Count, perpendicularIntersections, collinearConnectors,
                uniqueOutputEdges.Count, skippedObjects, skippedArcSegments,
                skippedZeroLength, maximumDistance, angleTolerance,
                allowCrossLayer ? "Yes" : "No");

            var confirm = new PromptKeywordOptions("\nCreate ONLY the new connector segments? [Yes/No] <Yes>: ");
            confirm.Keywords.Add("Yes");
            confirm.Keywords.Add("No");
            confirm.AllowNone = true;
            PromptResult confirmation = editor.GetKeywords(confirm);
            if (confirmation.Status == PromptStatus.Cancel) return;
            if (confirmation.Status == PromptStatus.OK &&
                string.Equals(confirmation.StringResult, "No", StringComparison.OrdinalIgnoreCase))
                return;

            int created = 0;
            var createdConnectorIds = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)transaction.GetObject(
                    document.Database.LayerTableId, OpenMode.ForRead);
                ObjectId outputLayerId;
                if (layerTable.Has(outputLayer))
                {
                    outputLayerId = layerTable[outputLayer];
                }
                else
                {
                    layerTable.UpgradeOpen();
                    var layerRecord = new LayerTableRecord { Name = outputLayer };
                    try
                    {
                        Entity firstSource = transaction.GetObject(
                            uniqueOutputEdges[0].SourceId, OpenMode.ForRead, false) as Entity;
                        if (firstSource != null)
                        {
                            LayerTableRecord sourceLayer = transaction.GetObject(
                                firstSource.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                            if (sourceLayer != null)
                            {
                                layerRecord.Color = sourceLayer.Color;
                                layerRecord.LineWeight = sourceLayer.LineWeight;
                            }
                        }
                    }
                    catch { }
                    outputLayerId = layerTable.Add(layerRecord);
                    transaction.AddNewlyCreatedDBObject(layerRecord, true);
                }

                BlockTableRecord owner = (BlockTableRecord)transaction.GetObject(
                    document.Database.CurrentSpaceId, OpenMode.ForWrite);

                foreach (OutputEdge edge in uniqueOutputEdges)
                {
                    if (edge.A.GetDistanceTo(edge.B) <= Epsilon) continue;

                    var output = new Polyline(2);
                    output.SetDatabaseDefaults(document.Database);
                    Entity source = null;
                    try { source = transaction.GetObject(edge.SourceId, OpenMode.ForRead, false) as Entity; }
                    catch { }

                    if (source != null)
                    {
                        output.SetPropertiesFrom(source);
                    }

                    output.LayerId = outputLayerId;
                    output.Elevation = edge.Elevation;
                    output.Normal = Vector3d.ZAxis;
                    output.AddVertexAt(0, edge.A, 0.0, 0.0, 0.0);
                    output.AddVertexAt(1, edge.B, 0.0, 0.0, 0.0);

                    owner.AppendEntity(output);
                    transaction.AddNewlyCreatedDBObject(output, true);
                    createdConnectorIds.Add(output.ObjectId);
                    created++;
                }

                transaction.Commit();
            }

            editor.WriteMessage(
                "\nCE_CREATEORTHOGONALNETWORK complete. Created {0} NEW connector segments on layer '{1}'. Skipped {2} duplicate/overlapping connector portions. Original selected polylines and lines were not modified, split, trimmed, exploded, replaced or erased.",
                created, outputLayer, duplicateOrOverlappingPartsSkipped);

            // Run native OVERKILL after creation, but preselect ONLY the newly
            // created connector entities. Original selected source entities are
            // deliberately excluded from the implied selection and cannot be
            // removed or simplified by this cleanup pass.
            if (createdConnectorIds.Count > 0)
            {
                editor.SetImpliedSelection(createdConnectorIds.ToArray());
                document.SendStringToExecute("_.OVERKILL\n", true, false, false);
                editor.WriteMessage(
                    "\nOVERKILL has been started with only the {0} newly created connector entities selected. Confirm its settings/dialog to remove any remaining duplicate or overlapping connector geometry. Original source polylines are not selected for OVERKILL.",
                    createdConnectorIds.Count);
            }
        }


        /// <summary>
        /// Removes portions of a proposed connector that are already covered
        /// by selected source legs or connectors accepted earlier. This is a
        /// geometric overlap test (including partial overlap), not just an
        /// exact endpoint-key comparison.
        /// </summary>
        private static List<OutputEdge> GetUncoveredConnectorParts(
            OutputEdge candidate,
            IList<SourceSegment> sourceSegments,
            IList<OutputEdge> acceptedConnectors,
            double lateralTolerance)
        {
            var covered = new List<Tuple<double, double>>();
            AddCoveredIntervals(candidate, sourceSegments, covered, lateralTolerance);
            AddCoveredIntervals(candidate, acceptedConnectors, covered, lateralTolerance);

            if (covered.Count == 0)
                return new List<OutputEdge> { candidate };

            covered.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var merged = new List<Tuple<double, double>>();
            foreach (Tuple<double, double> interval in covered)
            {
                double start = Math.Max(0.0, Math.Min(1.0, interval.Item1));
                double end = Math.Max(0.0, Math.Min(1.0, interval.Item2));
                if (end - start <= Epsilon) continue;

                if (merged.Count == 0 || start > merged[merged.Count - 1].Item2 + Epsilon)
                    merged.Add(Tuple.Create(start, end));
                else
                {
                    Tuple<double, double> previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = Tuple.Create(
                        previous.Item1, Math.Max(previous.Item2, end));
                }
            }

            Vector2d direction = new Vector2d(
                candidate.B.X - candidate.A.X,
                candidate.B.Y - candidate.A.Y);
            double length = direction.Length;
            if (length <= Epsilon) return new List<OutputEdge>();

            var result = new List<OutputEdge>();
            double cursor = 0.0;
            foreach (Tuple<double, double> interval in merged)
            {
                if (interval.Item1 > cursor + Epsilon)
                {
                    Point2d partA = PointAt(candidate.A, direction, cursor);
                    Point2d partB = PointAt(candidate.A, direction, interval.Item1);
                    if (partA.GetDistanceTo(partB) > lateralTolerance)
                        result.Add(new OutputEdge(partA, partB,
                            candidate.Elevation, candidate.SourceId));
                }
                cursor = Math.Max(cursor, interval.Item2);
            }

            if (cursor < 1.0 - Epsilon)
            {
                Point2d partA = PointAt(candidate.A, direction, cursor);
                Point2d partB = candidate.B;
                if (partA.GetDistanceTo(partB) > lateralTolerance)
                    result.Add(new OutputEdge(partA, partB,
                        candidate.Elevation, candidate.SourceId));
            }

            return result;
        }

        private static Point2d PointAt(Point2d origin, Vector2d direction, double parameter)
        {
            return new Point2d(
                origin.X + direction.X * parameter,
                origin.Y + direction.Y * parameter);
        }

        private static void AddCoveredIntervals(
            OutputEdge candidate,
            IEnumerable<SourceSegment> covers,
            List<Tuple<double, double>> intervals,
            double lateralTolerance)
        {
            Vector2d direction = new Vector2d(
                candidate.B.X - candidate.A.X,
                candidate.B.Y - candidate.A.Y);
            double length = direction.Length;
            if (length <= Epsilon) return;
            Vector2d unit = new Vector2d(direction.X / length, direction.Y / length);

            foreach (SourceSegment cover in covers)
            {
                if (Math.Abs(candidate.Elevation - cover.Elevation) > 1e-6) continue;
                Vector2d coverDirection = new Vector2d(
                    cover.B.X - cover.A.X, cover.B.Y - cover.A.Y);
                double coverLength = coverDirection.Length;
                if (coverLength <= Epsilon) continue;
                Vector2d coverUnit = new Vector2d(
                    coverDirection.X / coverLength, coverDirection.Y / coverLength);

                // Only collinear geometry can cover a connector.
                if (Math.Abs(Cross(unit, coverUnit)) > 1e-7) continue;

                Vector2d offsetA = new Vector2d(
                    cover.A.X - candidate.A.X, cover.A.Y - candidate.A.Y);
                Vector2d offsetB = new Vector2d(
                    cover.B.X - candidate.A.X, cover.B.Y - candidate.A.Y);
                if (Math.Abs(Cross(unit, offsetA)) > lateralTolerance ||
                    Math.Abs(Cross(unit, offsetB)) > lateralTolerance)
                    continue;

                double tA = offsetA.DotProduct(unit) / length;
                double tB = offsetB.DotProduct(unit) / length;
                double start = Math.Max(0.0, Math.Min(tA, tB));
                double end = Math.Min(1.0, Math.Max(tA, tB));
                if (end - start > Epsilon)
                    intervals.Add(Tuple.Create(start, end));
            }
        }

        private static void AddCoveredIntervals(
            OutputEdge candidate,
            IEnumerable<OutputEdge> covers,
            List<Tuple<double, double>> intervals,
            double lateralTolerance)
        {
            Vector2d direction = new Vector2d(
                candidate.B.X - candidate.A.X,
                candidate.B.Y - candidate.A.Y);
            double length = direction.Length;
            if (length <= Epsilon) return;
            Vector2d unit = new Vector2d(direction.X / length, direction.Y / length);

            foreach (OutputEdge cover in covers)
            {
                if (Math.Abs(candidate.Elevation - cover.Elevation) > 1e-6) continue;
                Vector2d coverDirection = new Vector2d(
                    cover.B.X - cover.A.X, cover.B.Y - cover.A.Y);
                double coverLength = coverDirection.Length;
                if (coverLength <= Epsilon) continue;
                Vector2d coverUnit = new Vector2d(
                    coverDirection.X / coverLength, coverDirection.Y / coverLength);
                if (Math.Abs(Cross(unit, coverUnit)) > 1e-7) continue;

                Vector2d offsetA = new Vector2d(
                    cover.A.X - candidate.A.X, cover.A.Y - candidate.A.Y);
                Vector2d offsetB = new Vector2d(
                    cover.B.X - candidate.A.X, cover.B.Y - candidate.A.Y);
                if (Math.Abs(Cross(unit, offsetA)) > lateralTolerance ||
                    Math.Abs(Cross(unit, offsetB)) > lateralTolerance)
                    continue;

                double tA = offsetA.DotProduct(unit) / length;
                double tB = offsetB.DotProduct(unit) / length;
                double start = Math.Max(0.0, Math.Min(tA, tB));
                double end = Math.Min(1.0, Math.Max(tA, tB));
                if (end - start > Epsilon)
                    intervals.Add(Tuple.Create(start, end));
            }
        }

        private static double UndirectedAngleDifference(double first, double second)
        {
            double difference = Math.Abs(first - second) % Math.PI;
            if (difference > Math.PI / 2.0)
                difference = Math.PI - difference;
            return difference;
        }

        private static double DistanceOutsideUnitInterval(double parameter)
        {
            if (parameter < 0.0) return -parameter;
            if (parameter > 1.0) return parameter - 1.0;
            return 0.0;
        }

        private static double Cross(Vector2d first, Vector2d second)
        {
            return first.X * second.Y - first.Y * second.X;
        }

        private static string EdgeKey(Point2d a, Point2d b, double elevation)
        {
            string first = PointKey(a, elevation);
            string second = PointKey(b, elevation);
            return string.CompareOrdinal(first, second) <= 0
                ? first + "|" + second
                : second + "|" + first;
        }

        private static string PointKey(Point2d point, double elevation)
        {
            return Math.Round(point.X, 6).ToString("0.000000", CultureInfo.InvariantCulture) + "," +
                   Math.Round(point.Y, 6).ToString("0.000000", CultureInfo.InvariantCulture) + "," +
                   Math.Round(elevation, 6).ToString("0.000000", CultureInfo.InvariantCulture);
        }
    }
}
