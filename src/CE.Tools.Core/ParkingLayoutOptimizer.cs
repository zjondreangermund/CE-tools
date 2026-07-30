using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    /// <summary>
    /// Host-independent parking layout screening. Geometry is analysed in a rotated
    /// local plane. Results remain concept alternatives and require verification
    /// against the governing parking, accessibility, fire, traffic and drainage rules.
    /// </summary>
    public static class ParkingLayoutOptimizer
    {
        private const double Tolerance = 1e-8;
        private const int MaximumCandidateSlots = 250000;

        public static IReadOnlyList<ParkingLayoutOption> Optimise(
            ParkingPolygon boundary,
            IEnumerable<ParkingPolygon> obstacles,
            ParkingPoint entrance,
            ParkingLayoutSettings settings)
        {
            if (boundary == null) throw new ArgumentNullException(nameof(boundary));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            boundary.Validate("boundary");
            settings.Validate();
            List<ParkingPolygon> obstacleList = (obstacles ?? Enumerable.Empty<ParkingPolygon>()).ToList();
            for (int index = 0; index < obstacleList.Count; index++)
                obstacleList[index].Validate("obstacle " + (index + 1));
            if (!boundary.Contains(entrance, true))
                throw new ArgumentException("The parking entrance point must lie inside or on the boundary.", nameof(entrance));

            double primary = FindPrimaryOrientation(boundary);
            var orientations = new List<double>();
            foreach (double offset in settings.OrientationOffsetsDegrees)
            {
                double value = NormalizeHalfTurn(primary + offset);
                if (!orientations.Any(existing => AngularDifference(existing, value) <= 0.001))
                    orientations.Add(value);
            }

            var options = new List<ParkingLayoutOption>();
            foreach (double orientation in orientations)
            {
                foreach (double angle in settings.ParkingAnglesDegrees)
                {
                    options.Add(BuildOption(
                        boundary,
                        obstacleList,
                        entrance,
                        settings,
                        orientation,
                        angle));
                }
            }
            return options
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.TotalBayCount)
                .ThenBy(item => item.MissingAccessibleBays)
                .ThenBy(item => item.ParkingAngleDegrees)
                .ThenBy(item => item.OrientationDegrees)
                .ToList();
        }

        private static ParkingLayoutOption BuildOption(
            ParkingPolygon boundaryWorld,
            IList<ParkingPolygon> obstaclesWorld,
            ParkingPoint entranceWorld,
            ParkingLayoutSettings settings,
            double orientationDegrees,
            double parkingAngleDegrees)
        {
            ParkingPoint origin = boundaryWorld.Centroid;
            double rotation = -DegreesToRadians(orientationDegrees);
            ParkingPolygon boundary = boundaryWorld.Transform(origin, rotation);
            List<ParkingPolygon> obstacles = obstaclesWorld
                .Select(item => item.Transform(origin, rotation))
                .ToList();
            ParkingPoint entrance = entranceWorld.Transform(origin, rotation);
            ParkingBounds bounds = boundary.Bounds;

            double angleRadians = DegreesToRadians(parkingAngleDegrees);
            double sine = Math.Max(Tolerance, Math.Abs(Math.Sin(angleRadians)));
            double cosine = Math.Abs(Math.Cos(angleRadians));
            double standardPitch = settings.StandardBayWidthMetres / sine;
            double projectedDepth = settings.BayDepthMetres * sine +
                settings.StandardBayWidthMetres * cosine;
            double moduleDepth = projectedDepth * 2.0 + settings.AisleWidthMetres;
            if (standardPitch <= Tolerance || projectedDepth <= Tolerance || moduleDepth <= Tolerance)
                return EmptyOption(orientationDegrees, parkingAngleDegrees, "Invalid projected module dimensions.");

            int modules = Math.Max(0, (int)Math.Floor(bounds.Height / moduleDepth));
            if (modules == 0)
                return EmptyOption(orientationDegrees, parkingAngleDegrees, "Boundary is too shallow for a double-loaded parking module.");

            var bays = new List<ParkingElement>();
            var aisles = new List<ParkingElement>();
            var islands = new List<ParkingElement>();
            var rejected = new ParkingRejectionSummary();
            int candidateSlots = 0;
            double unusedDepth = Math.Max(0.0, bounds.Height - modules * moduleDepth);
            double[] yOffsets = { 0.0, unusedDepth * 0.5, unusedDepth };
            ParkingCandidateSet best = null;

            foreach (double yOffset in yOffsets)
            {
                var candidate = new ParkingCandidateSet();
                for (int module = 0; module < modules; module++)
                {
                    double moduleBottom = bounds.MinY + yOffset + module * moduleDepth;
                    double lowerBaseline = moduleBottom + projectedDepth;
                    double upperBaseline = lowerBaseline + settings.AisleWidthMetres;
                    ParkingPolygon aisle = Rectangle(
                        bounds.MinX,
                        lowerBaseline,
                        bounds.MaxX,
                        upperBaseline);
                    if (!IsClear(aisle, boundary, obstacles))
                    {
                        candidate.Rejections.AisleBoundaryOrObstacle++;
                        continue;
                    }
                    candidate.Aisles.Add(new ParkingElement(
                        ParkingElementType.TrafficAisle,
                        aisle,
                        module,
                        -1,
                        "Aisle " + (module + 1)));
                    AddRow(
                        candidate,
                        boundary,
                        obstacles,
                        settings,
                        parkingAngleDegrees,
                        standardPitch,
                        lowerBaseline,
                        -1.0,
                        module * 2,
                        bounds,
                        ref candidateSlots);
                    AddRow(
                        candidate,
                        boundary,
                        obstacles,
                        settings,
                        parkingAngleDegrees,
                        standardPitch,
                        upperBaseline,
                        1.0,
                        module * 2 + 1,
                        bounds,
                        ref candidateSlots);
                    if (candidateSlots > MaximumCandidateSlots)
                        throw new InvalidOperationException(
                            "Parking optimisation exceeded the 250,000-slot safety limit. Increase bay dimensions, reduce alternatives or divide the site.");
                }
                if (best == null || candidate.StandardBays.Count > best.StandardBays.Count)
                    best = candidate;
            }

            if (best == null)
                return EmptyOption(orientationDegrees, parkingAngleDegrees, "No valid parking module was found.");

            bays.AddRange(best.StandardBays);
            aisles.AddRange(best.Aisles);
            rejected.Add(best.Rejections);

            ApplyIslands(bays, islands, settings);
            int accessibleCreated = ApplyAccessibleBays(
                bays,
                aisles,
                boundary,
                obstacles,
                entrance,
                settings,
                rejected);

            ParkingElement entranceConnection = BuildEntranceConnection(
                entrance,
                aisles,
                boundary,
                obstacles,
                settings);
            if (entranceConnection != null) aisles.Add(entranceConnection);
            else rejected.EntranceConnection++;

            int standardCount = bays.Count(item => item.Type == ParkingElementType.StandardBay);
            int accessibleCount = bays.Count(item => item.Type == ParkingElementType.AccessibleBay);
            int total = standardCount + accessibleCount;
            int targetShortfall = Math.Max(0, settings.TargetBayCount - total);
            int accessibleShortfall = Math.Max(0, settings.RequiredAccessibleBayCount - accessibleCount);
            double score = total * 1000.0 + accessibleCount * 250.0 -
                targetShortfall * 1500.0 - accessibleShortfall * 5000.0 -
                islands.Count * 5.0 - rejected.Total * 10.0;
            if (total >= settings.TargetBayCount) score += 10000.0;
            if (accessibleShortfall == 0) score += 5000.0;
            if (entranceConnection != null) score += 1000.0;

            double inverseRotation = DegreesToRadians(orientationDegrees);
            Func<ParkingElement, ParkingElement> toWorld = item =>
                item.Transform(origin, inverseRotation);
            return new ParkingLayoutOption(
                orientationDegrees,
                parkingAngleDegrees,
                score,
                standardCount,
                accessibleCount,
                settings.RequiredAccessibleBayCount,
                targetShortfall,
                accessibleShortfall,
                bays.Select(toWorld).ToList(),
                aisles.Select(toWorld).ToList(),
                islands.Select(toWorld).ToList(),
                rejected,
                entranceConnection != null,
                BuildNotes(total, settings, accessibleCreated, entranceConnection, rejected));
        }

        private static void AddRow(
            ParkingCandidateSet candidate,
            ParkingPolygon boundary,
            IList<ParkingPolygon> obstacles,
            ParkingLayoutSettings settings,
            double angleDegrees,
            double pitch,
            double baselineY,
            double sideSign,
            int row,
            ParkingBounds bounds,
            ref int candidateSlots)
        {
            double radians = DegreesToRadians(angleDegrees) * sideSign;
            ParkingPoint depthVector = new ParkingPoint(
                Math.Cos(radians) * settings.BayDepthMetres,
                Math.Sin(radians) * settings.BayDepthMetres);
            double skew = Math.Abs(depthVector.X);
            double start = bounds.MinX - skew;
            int sequence = 0;
            for (double x = start; x + pitch <= bounds.MaxX + skew + Tolerance; x += pitch)
            {
                candidateSlots++;
                ParkingPolygon bay = new ParkingPolygon(new[]
                {
                    new ParkingPoint(x, baselineY),
                    new ParkingPoint(x + pitch, baselineY),
                    new ParkingPoint(x + pitch + depthVector.X, baselineY + depthVector.Y),
                    new ParkingPoint(x + depthVector.X, baselineY + depthVector.Y)
                });
                if (!boundary.ContainsPolygon(bay))
                {
                    candidate.Rejections.OutsideBoundary++;
                    sequence++;
                    continue;
                }
                if (obstacles.Any(obstacle => obstacle.IntersectsOrContains(bay)))
                {
                    candidate.Rejections.ObstacleConflict++;
                    sequence++;
                    continue;
                }
                candidate.StandardBays.Add(new ParkingElement(
                    ParkingElementType.StandardBay,
                    bay,
                    row,
                    sequence,
                    "Bay R" + (row + 1) + "-" + (sequence + 1)));
                sequence++;
            }
        }

        private static void ApplyIslands(
            IList<ParkingElement> bays,
            ICollection<ParkingElement> islands,
            ParkingLayoutSettings settings)
        {
            if (settings.IslandIntervalBays <= 0 || settings.IslandWidthMetres <= Tolerance)
                return;
            foreach (IGrouping<int, ParkingElement> row in bays
                .Where(item => item.Type == ParkingElementType.StandardBay)
                .GroupBy(item => item.RowIndex)
                .ToList())
            {
                List<ParkingElement> ordered = row.OrderBy(item => item.Sequence).ToList();
                for (int index = settings.IslandIntervalBays; index < ordered.Count; index += settings.IslandIntervalBays + 1)
                {
                    ParkingElement removed = ordered[index];
                    if (!bays.Remove(removed)) continue;
                    ParkingPolygon island = ScaleAcrossWidth(
                        removed.Polygon,
                        Math.Min(1.0, settings.IslandWidthMetres /
                            Math.Max(Tolerance, EdgeLength(removed.Polygon.Vertices[0], removed.Polygon.Vertices[1]))));
                    islands.Add(new ParkingElement(
                        ParkingElementType.LandscapeIsland,
                        island,
                        removed.RowIndex,
                        removed.Sequence,
                        "Island R" + (removed.RowIndex + 1) + "-" + (removed.Sequence + 1)));
                }
            }
        }

        private static int ApplyAccessibleBays(
            IList<ParkingElement> bays,
            ICollection<ParkingElement> aisles,
            ParkingPolygon boundary,
            IList<ParkingPolygon> obstacles,
            ParkingPoint entrance,
            ParkingLayoutSettings settings,
            ParkingRejectionSummary rejected)
        {
            int created = 0;
            List<ParkingElement> candidates = bays
                .Where(item => item.Type == ParkingElementType.StandardBay)
                .OrderBy(item => item.Polygon.Centroid.DistanceTo(entrance))
                .ThenBy(item => item.RowIndex)
                .ThenBy(item => item.Sequence)
                .ToList();
            foreach (ParkingElement candidate in candidates)
            {
                if (created >= settings.RequiredAccessibleBayCount) break;
                if (!bays.Contains(candidate)) continue;
                ParkingPoint first = candidate.Polygon.Vertices[0];
                ParkingPoint second = candidate.Polygon.Vertices[1];
                double currentWidth = first.DistanceTo(second);
                double requiredWidth = settings.AccessibleBayWidthMetres +
                    settings.AccessAisleWidthMetres;
                double scale = requiredWidth / Math.Max(Tolerance, currentWidth);
                ParkingPolygon envelope = ScaleAcrossWidth(candidate.Polygon, scale);
                if (!boundary.ContainsPolygon(envelope) ||
                    obstacles.Any(item => item.IntersectsOrContains(envelope)))
                {
                    rejected.AccessibleEnvelope++;
                    continue;
                }
                if (bays.Any(item =>
                    item.Type == ParkingElementType.AccessibleBay &&
                    item.Polygon.IntersectsOrContains(envelope)))
                {
                    rejected.AccessibleEnvelope++;
                    continue;
                }
                List<ParkingElement> overlaps = bays
                    .Where(item =>
                        item != candidate &&
                        item.Type == ParkingElementType.StandardBay &&
                        item.Polygon.IntersectsOrContains(envelope))
                    .ToList();
                foreach (ParkingElement overlap in overlaps) bays.Remove(overlap);
                bays.Remove(candidate);

                double bayRatio = settings.AccessibleBayWidthMetres / requiredWidth;
                ParkingPolygon accessible = SliceAcrossWidth(envelope, 0.0, bayRatio);
                ParkingPolygon accessAisle = SliceAcrossWidth(envelope, bayRatio, 1.0);
                bays.Add(new ParkingElement(
                    ParkingElementType.AccessibleBay,
                    accessible,
                    candidate.RowIndex,
                    candidate.Sequence,
                    "Accessible " + (created + 1)));
                aisles.Add(new ParkingElement(
                    ParkingElementType.AccessAisle,
                    accessAisle,
                    candidate.RowIndex,
                    candidate.Sequence,
                    "Accessible aisle " + (created + 1)));
                created++;
            }
            return created;
        }

        private static ParkingElement BuildEntranceConnection(
            ParkingPoint entrance,
            IList<ParkingElement> aisles,
            ParkingPolygon boundary,
            IList<ParkingPolygon> obstacles,
            ParkingLayoutSettings settings)
        {
            ParkingElement nearest = aisles
                .Where(item => item.Type == ParkingElementType.TrafficAisle)
                .OrderBy(item => item.Polygon.Centroid.DistanceTo(entrance))
                .FirstOrDefault();
            if (nearest == null) return null;
            ParkingPoint target = nearest.Polygon.Centroid;
            ParkingPolygon connection = Corridor(
                entrance,
                target,
                Math.Min(settings.AisleWidthMetres, settings.EntranceWidthMetres));
            if (!boundary.ContainsPolygon(connection) ||
                obstacles.Any(item => item.IntersectsOrContains(connection)))
                return null;
            return new ParkingElement(
                ParkingElementType.EntranceConnection,
                connection,
                -1,
                -1,
                "Entrance connection");
        }

        private static bool IsClear(
            ParkingPolygon polygon,
            ParkingPolygon boundary,
            IEnumerable<ParkingPolygon> obstacles)
        {
            return boundary.ContainsPolygon(polygon) &&
                !obstacles.Any(item => item.IntersectsOrContains(polygon));
        }

        private static ParkingPolygon Rectangle(double minX, double minY, double maxX, double maxY)
        {
            return new ParkingPolygon(new[]
            {
                new ParkingPoint(minX, minY),
                new ParkingPoint(maxX, minY),
                new ParkingPoint(maxX, maxY),
                new ParkingPoint(minX, maxY)
            });
        }

        private static ParkingPolygon Corridor(ParkingPoint first, ParkingPoint second, double width)
        {
            ParkingPoint direction = second - first;
            double length = direction.Length;
            if (length <= Tolerance) return Rectangle(
                first.X - width * 0.5,
                first.Y - width * 0.5,
                first.X + width * 0.5,
                first.Y + width * 0.5);
            ParkingPoint normal = new ParkingPoint(-direction.Y / length, direction.X / length) * (width * 0.5);
            return new ParkingPolygon(new[]
            {
                first + normal,
                second + normal,
                second - normal,
                first - normal
            });
        }

        private static ParkingPolygon ScaleAcrossWidth(ParkingPolygon polygon, double scale)
        {
            ParkingPoint a = polygon.Vertices[0];
            ParkingPoint b = polygon.Vertices[1];
            ParkingPoint d = polygon.Vertices[3];
            ParkingPoint width = b - a;
            ParkingPoint centreStart = a + width * 0.5;
            ParkingPoint centreEnd = d + width * 0.5;
            ParkingPoint half = width * (scale * 0.5);
            return new ParkingPolygon(new[]
            {
                centreStart - half,
                centreStart + half,
                centreEnd + half,
                centreEnd - half
            });
        }

        private static ParkingPolygon SliceAcrossWidth(ParkingPolygon polygon, double startFraction, double endFraction)
        {
            ParkingPoint a = polygon.Vertices[0];
            ParkingPoint b = polygon.Vertices[1];
            ParkingPoint d = polygon.Vertices[3];
            ParkingPoint c = polygon.Vertices[2];
            ParkingPoint lowerStart = a + (b - a) * startFraction;
            ParkingPoint lowerEnd = a + (b - a) * endFraction;
            ParkingPoint upperStart = d + (c - d) * startFraction;
            ParkingPoint upperEnd = d + (c - d) * endFraction;
            return new ParkingPolygon(new[] { lowerStart, lowerEnd, upperEnd, upperStart });
        }

        private static double EdgeLength(ParkingPoint first, ParkingPoint second)
        {
            return first.DistanceTo(second);
        }

        private static double FindPrimaryOrientation(ParkingPolygon boundary)
        {
            double bestLength = -1.0;
            double best = 0.0;
            for (int index = 0; index < boundary.Vertices.Count; index++)
            {
                ParkingPoint first = boundary.Vertices[index];
                ParkingPoint second = boundary.Vertices[(index + 1) % boundary.Vertices.Count];
                double length = first.DistanceTo(second);
                if (length <= bestLength) continue;
                bestLength = length;
                best = Math.Atan2(second.Y - first.Y, second.X - first.X) * 180.0 / Math.PI;
            }
            return NormalizeHalfTurn(best);
        }

        private static string BuildNotes(
            int total,
            ParkingLayoutSettings settings,
            int accessibleCreated,
            ParkingElement connection,
            ParkingRejectionSummary rejected)
        {
            var notes = new List<string>();
            notes.Add(total >= settings.TargetBayCount
                ? "Target bay count achieved."
                : "Target shortfall=" + (settings.TargetBayCount - total));
            notes.Add(accessibleCreated >= settings.RequiredAccessibleBayCount
                ? "Accessible target achieved."
                : "Accessible shortfall=" + (settings.RequiredAccessibleBayCount - accessibleCreated));
            notes.Add(connection == null
                ? "No clear entrance-to-aisle connection was generated."
                : "Entrance connected to nearest traffic aisle.");
            if (rejected.Total > 0) notes.Add("Rejected candidate checks=" + rejected.Total);
            notes.Add("Concept screening only; verify all governing standards and swept paths.");
            return string.Join(" ", notes);
        }

        private static ParkingLayoutOption EmptyOption(double orientation, double angle, string reason)
        {
            return new ParkingLayoutOption(
                orientation,
                angle,
                -1000000.0,
                0,
                0,
                0,
                0,
                0,
                new List<ParkingElement>(),
                new List<ParkingElement>(),
                new List<ParkingElement>(),
                new ParkingRejectionSummary(),
                false,
                reason);
        }

        private static double DegreesToRadians(double value) { return value * Math.PI / 180.0; }
        private static double NormalizeHalfTurn(double value)
        {
            value %= 180.0;
            if (value < 0.0) value += 180.0;
            return value;
        }
        private static double AngularDifference(double first, double second)
        {
            double difference = Math.Abs(NormalizeHalfTurn(first) - NormalizeHalfTurn(second));
            return Math.Min(difference, 180.0 - difference);
        }
    }

    internal sealed class ParkingCandidateSet
    {
        public ParkingCandidateSet()
        {
            StandardBays = new List<ParkingElement>();
            Aisles = new List<ParkingElement>();
            Rejections = new ParkingRejectionSummary();
        }
        public List<ParkingElement> StandardBays { get; private set; }
        public List<ParkingElement> Aisles { get; private set; }
        public ParkingRejectionSummary Rejections { get; private set; }
    }

    public sealed class ParkingLayoutSettings
    {
        public ParkingLayoutSettings(
            int targetBayCount,
            int requiredAccessibleBayCount,
            double standardBayWidthMetres,
            double accessibleBayWidthMetres,
            double accessAisleWidthMetres,
            double bayDepthMetres,
            double aisleWidthMetres,
            double entranceWidthMetres,
            int islandIntervalBays,
            double islandWidthMetres,
            IEnumerable<double> parkingAnglesDegrees,
            IEnumerable<double> orientationOffsetsDegrees)
        {
            TargetBayCount = targetBayCount;
            RequiredAccessibleBayCount = requiredAccessibleBayCount;
            StandardBayWidthMetres = standardBayWidthMetres;
            AccessibleBayWidthMetres = accessibleBayWidthMetres;
            AccessAisleWidthMetres = accessAisleWidthMetres;
            BayDepthMetres = bayDepthMetres;
            AisleWidthMetres = aisleWidthMetres;
            EntranceWidthMetres = entranceWidthMetres;
            IslandIntervalBays = islandIntervalBays;
            IslandWidthMetres = islandWidthMetres;
            ParkingAnglesDegrees = (parkingAnglesDegrees ?? new double[0]).ToList();
            OrientationOffsetsDegrees = (orientationOffsetsDegrees ?? new double[0]).ToList();
        }
        public int TargetBayCount { get; private set; }
        public int RequiredAccessibleBayCount { get; private set; }
        public double StandardBayWidthMetres { get; private set; }
        public double AccessibleBayWidthMetres { get; private set; }
        public double AccessAisleWidthMetres { get; private set; }
        public double BayDepthMetres { get; private set; }
        public double AisleWidthMetres { get; private set; }
        public double EntranceWidthMetres { get; private set; }
        public int IslandIntervalBays { get; private set; }
        public double IslandWidthMetres { get; private set; }
        public IReadOnlyList<double> ParkingAnglesDegrees { get; private set; }
        public IReadOnlyList<double> OrientationOffsetsDegrees { get; private set; }

        public void Validate()
        {
            if (TargetBayCount <= 0) throw new ArgumentOutOfRangeException(nameof(TargetBayCount));
            if (RequiredAccessibleBayCount < 0) throw new ArgumentOutOfRangeException(nameof(RequiredAccessibleBayCount));
            Positive(StandardBayWidthMetres, nameof(StandardBayWidthMetres));
            Positive(AccessibleBayWidthMetres, nameof(AccessibleBayWidthMetres));
            Positive(AccessAisleWidthMetres, nameof(AccessAisleWidthMetres));
            Positive(BayDepthMetres, nameof(BayDepthMetres));
            Positive(AisleWidthMetres, nameof(AisleWidthMetres));
            Positive(EntranceWidthMetres, nameof(EntranceWidthMetres));
            if (IslandIntervalBays < 0) throw new ArgumentOutOfRangeException(nameof(IslandIntervalBays));
            if (IslandWidthMetres < 0.0 || double.IsNaN(IslandWidthMetres) || double.IsInfinity(IslandWidthMetres))
                throw new ArgumentOutOfRangeException(nameof(IslandWidthMetres));
            if (ParkingAnglesDegrees.Count == 0) throw new ArgumentException("At least one parking angle is required.");
            if (OrientationOffsetsDegrees.Count == 0) throw new ArgumentException("At least one orientation offset is required.");
            foreach (double angle in ParkingAnglesDegrees)
                if (angle < 30.0 || angle > 90.0 || double.IsNaN(angle) || double.IsInfinity(angle))
                    throw new ArgumentOutOfRangeException(nameof(ParkingAnglesDegrees));
        }
        private static void Positive(double value, string name)
        {
            if (value <= 0.0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name);
        }
    }

    public enum ParkingElementType
    {
        StandardBay,
        AccessibleBay,
        AccessAisle,
        TrafficAisle,
        LandscapeIsland,
        EntranceConnection
    }

    public sealed class ParkingElement
    {
        public ParkingElement(ParkingElementType type, ParkingPolygon polygon, int rowIndex, int sequence, string name)
        {
            Type = type;
            Polygon = polygon;
            RowIndex = rowIndex;
            Sequence = sequence;
            Name = name;
        }
        public ParkingElementType Type { get; private set; }
        public ParkingPolygon Polygon { get; private set; }
        public int RowIndex { get; private set; }
        public int Sequence { get; private set; }
        public string Name { get; private set; }
        public ParkingElement Transform(ParkingPoint origin, double radians)
        {
            return new ParkingElement(Type, Polygon.Transform(origin, radians), RowIndex, Sequence, Name);
        }
    }

    public sealed class ParkingLayoutOption
    {
        public ParkingLayoutOption(
            double orientationDegrees,
            double parkingAngleDegrees,
            double score,
            int standardBayCount,
            int accessibleBayCount,
            int requiredAccessibleBayCount,
            int targetShortfall,
            int missingAccessibleBays,
            IReadOnlyList<ParkingElement> bays,
            IReadOnlyList<ParkingElement> aisles,
            IReadOnlyList<ParkingElement> islands,
            ParkingRejectionSummary rejections,
            bool hasEntranceConnection,
            string notes)
        {
            OrientationDegrees = orientationDegrees;
            ParkingAngleDegrees = parkingAngleDegrees;
            Score = score;
            StandardBayCount = standardBayCount;
            AccessibleBayCount = accessibleBayCount;
            RequiredAccessibleBayCount = requiredAccessibleBayCount;
            TargetShortfall = targetShortfall;
            MissingAccessibleBays = missingAccessibleBays;
            Bays = bays;
            Aisles = aisles;
            Islands = islands;
            Rejections = rejections;
            HasEntranceConnection = hasEntranceConnection;
            Notes = notes;
        }
        public double OrientationDegrees { get; private set; }
        public double ParkingAngleDegrees { get; private set; }
        public double Score { get; private set; }
        public int StandardBayCount { get; private set; }
        public int AccessibleBayCount { get; private set; }
        public int RequiredAccessibleBayCount { get; private set; }
        public int TotalBayCount { get { return StandardBayCount + AccessibleBayCount; } }
        public int TargetShortfall { get; private set; }
        public int MissingAccessibleBays { get; private set; }
        public IReadOnlyList<ParkingElement> Bays { get; private set; }
        public IReadOnlyList<ParkingElement> Aisles { get; private set; }
        public IReadOnlyList<ParkingElement> Islands { get; private set; }
        public ParkingRejectionSummary Rejections { get; private set; }
        public bool HasEntranceConnection { get; private set; }
        public string Notes { get; private set; }
    }

    public sealed class ParkingRejectionSummary
    {
        public int OutsideBoundary { get; set; }
        public int ObstacleConflict { get; set; }
        public int AisleBoundaryOrObstacle { get; set; }
        public int AccessibleEnvelope { get; set; }
        public int EntranceConnection { get; set; }
        public int Total { get { return OutsideBoundary + ObstacleConflict + AisleBoundaryOrObstacle + AccessibleEnvelope + EntranceConnection; } }
        public void Add(ParkingRejectionSummary other)
        {
            if (other == null) return;
            OutsideBoundary += other.OutsideBoundary;
            ObstacleConflict += other.ObstacleConflict;
            AisleBoundaryOrObstacle += other.AisleBoundaryOrObstacle;
            AccessibleEnvelope += other.AccessibleEnvelope;
            EntranceConnection += other.EntranceConnection;
        }
    }

    public sealed class ParkingPoint
    {
        public ParkingPoint(double x, double y) { X = x; Y = y; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }
        public double DistanceTo(ParkingPoint other) { return (this - other).Length; }
        public ParkingPoint Transform(ParkingPoint origin, double radians)
        {
            double x = X - origin.X;
            double y = Y - origin.Y;
            double c = Math.Cos(radians);
            double s = Math.Sin(radians);
            return new ParkingPoint(origin.X + x * c - y * s, origin.Y + x * s + y * c);
        }
        public static ParkingPoint operator +(ParkingPoint a, ParkingPoint b) { return new ParkingPoint(a.X + b.X, a.Y + b.Y); }
        public static ParkingPoint operator -(ParkingPoint a, ParkingPoint b) { return new ParkingPoint(a.X - b.X, a.Y - b.Y); }
        public static ParkingPoint operator *(ParkingPoint a, double value) { return new ParkingPoint(a.X * value, a.Y * value); }
    }

    public sealed class ParkingBounds
    {
        public ParkingBounds(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double MinX { get; private set; }
        public double MinY { get; private set; }
        public double MaxX { get; private set; }
        public double MaxY { get; private set; }
        public double Width { get { return MaxX - MinX; } }
        public double Height { get { return MaxY - MinY; } }
    }

    public sealed class ParkingPolygon
    {
        private const double GeometryTolerance = 1e-8;
        public ParkingPolygon(IEnumerable<ParkingPoint> vertices)
        {
            Vertices = (vertices ?? Enumerable.Empty<ParkingPoint>()).ToList();
        }
        public IReadOnlyList<ParkingPoint> Vertices { get; private set; }
        public ParkingBounds Bounds
        {
            get
            {
                return new ParkingBounds(
                    Vertices.Min(item => item.X), Vertices.Min(item => item.Y),
                    Vertices.Max(item => item.X), Vertices.Max(item => item.Y));
            }
        }
        public double SignedArea
        {
            get
            {
                double value = 0.0;
                for (int i = 0; i < Vertices.Count; i++)
                {
                    ParkingPoint a = Vertices[i];
                    ParkingPoint b = Vertices[(i + 1) % Vertices.Count];
                    value += a.X * b.Y - b.X * a.Y;
                }
                return value * 0.5;
            }
        }
        public ParkingPoint Centroid
        {
            get
            {
                double area6 = SignedArea * 6.0;
                if (Math.Abs(area6) <= GeometryTolerance)
                    return new ParkingPoint(Vertices.Average(item => item.X), Vertices.Average(item => item.Y));
                double x = 0.0, y = 0.0;
                for (int i = 0; i < Vertices.Count; i++)
                {
                    ParkingPoint a = Vertices[i];
                    ParkingPoint b = Vertices[(i + 1) % Vertices.Count];
                    double cross = a.X * b.Y - b.X * a.Y;
                    x += (a.X + b.X) * cross;
                    y += (a.Y + b.Y) * cross;
                }
                return new ParkingPoint(x / area6, y / area6);
            }
        }
        public void Validate(string name)
        {
            if (Vertices.Count < 3) throw new ArgumentException(name + " requires at least three vertices.");
            if (Math.Abs(SignedArea) <= GeometryTolerance) throw new ArgumentException(name + " has zero area.");
            for (int i = 0; i < Vertices.Count; i++)
                for (int j = i + 1; j < Vertices.Count; j++)
                    if (!Adjacent(i, j, Vertices.Count) && SegmentsIntersect(
                        Vertices[i], Vertices[(i + 1) % Vertices.Count],
                        Vertices[j], Vertices[(j + 1) % Vertices.Count], false))
                        throw new ArgumentException(name + " is self-intersecting.");
        }
        public ParkingPolygon Transform(ParkingPoint origin, double radians)
        {
            return new ParkingPolygon(Vertices.Select(item => item.Transform(origin, radians)));
        }
        public bool Contains(ParkingPoint point, bool includeBoundary)
        {
            bool inside = false;
            int previous = Vertices.Count - 1;
            for (int current = 0; current < Vertices.Count; current++)
            {
                ParkingPoint a = Vertices[previous];
                ParkingPoint b = Vertices[current];
                if (PointOnSegment(a, b, point)) return includeBoundary;
                bool crosses = ((b.Y > point.Y) != (a.Y > point.Y)) &&
                    point.X < (a.X - b.X) * (point.Y - b.Y) /
                    ((a.Y - b.Y) + GeometryTolerance) + b.X;
                if (crosses) inside = !inside;
                previous = current;
            }
            return inside;
        }
        public bool ContainsPolygon(ParkingPolygon other)
        {
            if (other == null) return false;
            if (other.Vertices.Any(item => !Contains(item, true))) return false;
            for (int i = 0; i < other.Vertices.Count; i++)
                for (int j = 0; j < Vertices.Count; j++)
                    if (SegmentsIntersect(
                        other.Vertices[i], other.Vertices[(i + 1) % other.Vertices.Count],
                        Vertices[j], Vertices[(j + 1) % Vertices.Count], false))
                        return false;
            return Contains(other.Centroid, true);
        }
        public bool IntersectsOrContains(ParkingPolygon other)
        {
            if (other == null) return false;
            for (int i = 0; i < Vertices.Count; i++)
                for (int j = 0; j < other.Vertices.Count; j++)
                    if (SegmentsIntersect(
                        Vertices[i], Vertices[(i + 1) % Vertices.Count],
                        other.Vertices[j], other.Vertices[(j + 1) % other.Vertices.Count], true))
                        return true;
            return Contains(other.Centroid, true) || other.Contains(Centroid, true);
        }
        private static bool Adjacent(int first, int second, int count)
        {
            return first == second || Math.Abs(first - second) == 1 || Math.Abs(first - second) == count - 1;
        }
        private static bool PointOnSegment(ParkingPoint a, ParkingPoint b, ParkingPoint p)
        {
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (Math.Abs(cross) > GeometryTolerance) return false;
            double dot = (p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y);
            if (dot < -GeometryTolerance) return false;
            double length = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
            return dot <= length + GeometryTolerance;
        }
        private static bool SegmentsIntersect(ParkingPoint a, ParkingPoint b, ParkingPoint c, ParkingPoint d, bool includeTouch)
        {
            double o1 = Orientation(a, b, c), o2 = Orientation(a, b, d);
            double o3 = Orientation(c, d, a), o4 = Orientation(c, d, b);
            if (((o1 > GeometryTolerance && o2 < -GeometryTolerance) || (o1 < -GeometryTolerance && o2 > GeometryTolerance)) &&
                ((o3 > GeometryTolerance && o4 < -GeometryTolerance) || (o3 < -GeometryTolerance && o4 > GeometryTolerance))) return true;
            if (!includeTouch) return false;
            return (Math.Abs(o1) <= GeometryTolerance && PointOnSegment(a, b, c)) ||
                   (Math.Abs(o2) <= GeometryTolerance && PointOnSegment(a, b, d)) ||
                   (Math.Abs(o3) <= GeometryTolerance && PointOnSegment(c, d, a)) ||
                   (Math.Abs(o4) <= GeometryTolerance && PointOnSegment(c, d, b));
        }
        private static double Orientation(ParkingPoint a, ParkingPoint b, ParkingPoint c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }
    }
}
