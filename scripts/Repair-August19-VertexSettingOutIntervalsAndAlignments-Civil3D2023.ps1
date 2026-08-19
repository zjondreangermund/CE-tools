[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 19 Vertex Setting-Out source missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText(
        $path,
        ($text -replace "`r?`n", "`r`n"),
        $utf8)
}

function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n", "`r`n"
    $new = $new -replace "`r?`n", "`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        throw "August 19 repair anchor not found: $label"
    }
    return $text.Replace($old,$new)
}

$vertexPath = Required 'VertexSettingOutCommands.cs'
$geometryPath = Required 'VertexSettingOutGeometry.cs'
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$finalGridPath = Required 'FinalAllCommentsCompletionCommands.cs'
$dynamicGridPath = Required 'August18DynamicGridSettingOutCommands.cs'

$vertex = ReadText $vertexPath
$geometry = ReadText $geometryPath
$universal = ReadText $universalPath
$finalGrid = ReadText $finalGridPath
$dynamicGrid = ReadText $dynamicGridPath

# August 19 is allowed to run only after the final August 18 staged state exists.
$august18Checks = @(
    @{ Name = 'August 18 coordinate display'; Ok = $vertex.Contains('"SwapXY", "04 Coordinate Display", "Swap X / Y values"') },
    @{ Name = 'August 18 background refresh entry point'; Ok = $universal.Contains('RefreshBackground(Document document)') },
    @{ Name = 'August 18 source-owner refresh policy'; Ok = $universal.Contains('IsLinkedSettingOutSource(DBObject value)') },
    @{ Name = 'August 18 dedicated dynamic grid route'; Ok = $finalGrid.Contains('CE_GRIDSETTINGOUTDYNAMIC') },
    @{ Name = 'August 18 grid coordinate sign control'; Ok = $dynamicGrid.Contains('ReverseSigns') }
)
$missingAugust18 = $august18Checks | Where-Object { -not $_.Ok }
if ($missingAugust18) {
    throw ('August 19 refused to run because the complete August 18 staged state was not detected: ' + (($missingAugust18 | ForEach-Object { $_.Name }) -join ', '))
}
Write-Host 'August 18 staged state verified. Applying August 19 only after all August 18 repairs.' -ForegroundColor Green

# -----------------------------------------------------------------------------
# 1. Existing Vertex Setting-Out popup and linked-table persistence.
# -----------------------------------------------------------------------------
$vertex = $vertex.Replace(
    'Creates and maintains setting-out points for multiple polylines and feature' + "`r`n" +
    '    /// lines.',
    'Creates and maintains setting-out points for multiple polylines, feature' + "`r`n" +
    '    /// lines and Civil 3D alignments.')
$vertex = $vertex.Replace(
    'Create dynamic COGO, MText or MLeader setting-out points from multiple polylines and feature lines.',
    'Create dynamic COGO, MText or MLeader setting-out points from multiple polylines, feature lines and alignments.')
$vertex = $vertex.Replace(
    '"\nSelect multiple polylines and/or Civil 3D feature lines: "',
    '"\nSelect MULTIPLE polylines, Civil 3D feature lines and/or Civil 3D alignments: "')

$oldGeneration = @'
            settings.AddChoice(
                "Generation", "01 Output", "Point generation", "Engineering setting-out points",
                "Choose the complete arc/tangent engineering rules or only the original polyline/feature-line vertices.",
                new[] { "Engineering setting-out points", "Polyline vertices only" });
'@
$newGeneration = @'
            settings.AddChoice(
                "Generation", "01 Output", "Point generation", "Engineering setting-out points",
                "Choose the complete engineering rules, source vertices only, or vertices plus points at a specified interval measured along each selected source.",
                new[] { "Engineering setting-out points", "Vertices only", "Vertices + specified intervals" });
            settings.AddPositiveDouble(
                "IntervalSpacing", "01 Output", "Specified point interval", 10.0,
                "Used by 'Vertices + specified intervals'. Points follow the true polyline/feature-line curve or Civil 3D alignment station geometry.");
'@
$vertex = ReplaceRequired $vertex $oldGeneration $newGeneration 'Vertex point-generation popup options'

$oldGenerationRead = @'
            string generationMode = settings.Text("Generation");
'@
$newGenerationRead = @'
            string generationMode = settings.Text("Generation");
            double intervalSpacing = settings.Double("IntervalSpacing", 10.0);
'@
$vertex = ReplaceRequired $vertex $oldGenerationRead $newGenerationRead 'Vertex interval setting read'

$oldInitialRead = @'
                sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out geometryRejected);
'@
$newInitialRead = @'
                sources = August19VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    generationMode,
                    intervalSpacing,
                    out geometryRejected);
'@
$vertex = ReplaceRequired $vertex $oldInitialRead $newInitialRead 'Initial August 19 geometry read'
$vertex = $vertex.Replace("            ApplyGenerationMode(sources, generationMode);`r`n", string.Empty)

$oldGenerationLink = @'
                GenerationMode = generationMode,
'@
$newGenerationLink = @'
                GenerationMode = generationMode,
                IntervalSpacing = intervalSpacing,
'@
$vertex = ReplaceRequired $vertex $oldGenerationLink $newGenerationLink 'Persist interval on linked setting-out group'

$reviewAnchor = @'
                new KeyValuePair<string, string>("Point output", outputType),
'@
$reviewAddition = @'
                new KeyValuePair<string, string>("Point output", outputType),
                new KeyValuePair<string, string>("Point generation", generationMode),
                new KeyValuePair<string, string>("Specified interval", string.Equals(generationMode, "Vertices + specified intervals", StringComparison.OrdinalIgnoreCase) ? intervalSpacing.ToString("0.###", CultureInfo.CurrentCulture) : "Not used"),
'@
$vertex = ReplaceRequired $vertex $reviewAnchor $reviewAddition 'Vertex preview interval row'

$oldRefreshRead = @'
                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out rejected);
'@
$newRefreshRead = @'
                IList<VertexSettingSource> sources = August19VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    link.GenerationMode,
                    link.IntervalSpacing,
                    out rejected);
'@
$vertex = ReplaceRequired $vertex $oldRefreshRead $newRefreshRead 'Linked August 19 interval refresh'
$vertex = $vertex.Replace("                ApplyGenerationMode(sources, link.GenerationMode);`r`n", string.Empty)
$vertex = $vertex.Replace(
    'None of the linked source polylines or feature lines are available.',
    'None of the linked source polylines, feature lines or alignments are available.')

$oldWriteGeneration = @'
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "GEN=" + (link.GenerationMode ?? string.Empty)));
'@
$newWriteGeneration = @'
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "GEN=" + (link.GenerationMode ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "INTERVAL=" + link.IntervalSpacing.ToString("R", CultureInfo.InvariantCulture)));
'@
$vertex = ReplaceRequired $vertex $oldWriteGeneration $newWriteGeneration 'Persist interval in table XData'

$oldDefaultGeneration = @'
                GenerationMode = "Engineering setting-out points",
'@
$newDefaultGeneration = @'
                GenerationMode = "Engineering setting-out points",
                IntervalSpacing = 10.0,
'@
$vertex = ReplaceRequired $vertex $oldDefaultGeneration $newDefaultGeneration 'Backward-compatible interval default'

$oldReadGeneration = @'
                if (value.StartsWith("GEN=", StringComparison.OrdinalIgnoreCase))
                    link.GenerationMode = value.Substring(4);
                else if (value.StartsWith("ELEV=", StringComparison.OrdinalIgnoreCase))
'@
$newReadGeneration = @'
                if (value.StartsWith("GEN=", StringComparison.OrdinalIgnoreCase))
                    link.GenerationMode = value.Substring(4);
                else if (value.StartsWith("INTERVAL=", StringComparison.OrdinalIgnoreCase))
                {
                    double spacing;
                    if (double.TryParse(value.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out spacing) && spacing > 0.0)
                        link.IntervalSpacing = spacing;
                }
                else if (value.StartsWith("ELEV=", StringComparison.OrdinalIgnoreCase))
'@
$vertex = ReplaceRequired $vertex $oldReadGeneration $newReadGeneration 'Read saved interval spacing'

$oldLinkProperty = @'
            public string GenerationMode { get; set; }
'@
$newLinkProperty = @'
            public string GenerationMode { get; set; }
            public double IntervalSpacing { get; set; }
'@
$vertex = ReplaceRequired $vertex $oldLinkProperty $newLinkProperty 'Vertex link interval property'

WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 2. Let the existing selection validator accept Civil 3D alignments.
#    The August 18 geometry extractor itself is otherwise left intact.
# -----------------------------------------------------------------------------
$oldSupportedTail = @'
                   entity is Polyline3d ||
                   entity is CivilFeatureLine;
'@
$newSupportedTail = @'
                   entity is Polyline3d ||
                   entity is CivilFeatureLine ||
                   entity is Autodesk.Civil.DatabaseServices.Alignment;
'@
$geometry = ReplaceRequired $geometry $oldSupportedTail $newSupportedTail 'Alignment selection support'
WriteText $geometryPath $geometry

# -----------------------------------------------------------------------------
# 3. Extend the final August 18 source-owner refresh predicate so a linked Civil
#    3D alignment edit can trigger exactly one Vertex refresh as well.
# -----------------------------------------------------------------------------
$oldSourceKinds = @'
            bool featureLine = typeName.IndexOf(
                "FeatureLine",
                StringComparison.OrdinalIgnoreCase) >= 0;
            if (!polyline && !featureLine) return false;
'@
$newSourceKinds = @'
            bool featureLine = typeName.IndexOf(
                "FeatureLine",
                StringComparison.OrdinalIgnoreCase) >= 0;
            bool alignment = typeName.IndexOf(
                "Alignment",
                StringComparison.OrdinalIgnoreCase) >= 0;
            if (!polyline && !featureLine && !alignment) return false;
'@
$universal = ReplaceRequired $universal $oldSourceKinds $newSourceKinds 'Alignment source-owner refresh support'
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 4. Add an August 19-only geometry adapter to the TEMPORARY staged source.
#    Existing August 18 source files remain unchanged in the repository checkout.
# -----------------------------------------------------------------------------
$helperPath = Join-Path $src 'August19VertexSettingOutGeometry.cs'
$helper = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class August19VertexSettingOutGeometry
    {
        private const double Tolerance = 1e-8;

        internal static IList<VertexSettingSource> ReadSources(
            Database database,
            Transaction transaction,
            IEnumerable<ObjectId> sourceIds,
            string generationMode,
            double intervalSpacing,
            out int rejected)
        {
            var result = new List<VertexSettingSource>();
            rejected = 0;
            if (database == null || transaction == null || sourceIds == null)
                return result;

            foreach (ObjectId id in sourceIds.Where(item => !item.IsNull).Distinct())
            {
                Entity entity;
                try
                {
                    entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch
                {
                    rejected++;
                    continue;
                }
                if (entity == null)
                {
                    rejected++;
                    continue;
                }

                Alignment alignment = entity as Alignment;
                if (alignment != null)
                {
                    VertexSettingSource alignmentSource = BuildAlignment(
                        alignment,
                        generationMode,
                        intervalSpacing);
                    if (alignmentSource == null || alignmentSource.Records.Count == 0)
                        rejected++;
                    else
                        result.Add(alignmentSource);
                    continue;
                }

                int localRejected;
                IList<VertexSettingSource> baseSources = VertexSettingOutGeometry.ReadSources(
                    database,
                    transaction,
                    new[] { id },
                    out localRejected);
                rejected += localRejected;
                foreach (VertexSettingSource source in baseSources)
                {
                    ApplyGenerationMode(
                        transaction,
                        entity,
                        source,
                        generationMode,
                        intervalSpacing);
                    if (source.Records != null && source.Records.Count > 0)
                        result.Add(source);
                    else
                        rejected++;
                }
            }
            return result;
        }

        private static void ApplyGenerationMode(
            Transaction transaction,
            Entity entity,
            VertexSettingSource source,
            string mode,
            double intervalSpacing)
        {
            if (source == null || source.Records == null) return;

            bool verticesOnly = string.Equals(mode, "Vertices only", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Polyline vertices only", StringComparison.OrdinalIgnoreCase);
            bool intervals = string.Equals(mode, "Vertices + specified intervals", StringComparison.OrdinalIgnoreCase);
            if (!verticesOnly && !intervals) return;

            source.Records = source.Records
                .Where(item => string.Equals(item.Kind, "VERTEX", StringComparison.OrdinalIgnoreCase))
                .ToList();
            source.Dimensions = new List<VertexRadialDimension>();
            if (!intervals || intervalSpacing <= Tolerance) return;

            Curve curve = entity as Curve;
            if (curve != null)
            {
                AddCurveIntervals(source, curve, intervalSpacing);
                return;
            }

            FeatureLine featureLine = entity as FeatureLine;
            if (featureLine != null)
                AddFeatureLineIntervals(source, featureLine, intervalSpacing);
        }

        private static void AddCurveIntervals(
            VertexSettingSource source,
            Curve curve,
            double spacing)
        {
            double total;
            try
            {
                total = curve.GetDistanceAtParameter(curve.EndParam);
            }
            catch
            {
                return;
            }
            if (!(total > spacing + Tolerance)) return;

            int index = 1;
            for (double distance = spacing;
                 distance < total - Tolerance && index < 100000;
                 distance += spacing, index++)
            {
                Point3d point;
                try { point = curve.GetPointAtDist(distance); }
                catch { continue; }
                if (ContainsPoint(source.Records, point)) continue;
                source.Records.Add(NewRecord(
                    source,
                    "I" + index.ToString(CultureInfo.InvariantCulture),
                    index,
                    point,
                    distance));
            }

            source.Records = source.Records
                .OrderBy(item => CurveDistance(curve, item.Point))
                .ToList();
        }

        private static double CurveDistance(Curve curve, Point3d point)
        {
            try { return curve.GetDistAtPoint(point); }
            catch { return double.MaxValue; }
        }

        private static void AddFeatureLineIntervals(
            VertexSettingSource source,
            FeatureLine featureLine,
            double spacing)
        {
            Point3dCollection collection;
            try { collection = featureLine.GetPoints(FeatureLinePointType.PIPoint); }
            catch { return; }
            var points = collection.Cast<Point3d>().ToList();
            if (points.Count < 2) return;

            bool closed = false;
            try { closed = featureLine.Closed; } catch { }
            if (closed && points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= Tolerance)
                points.RemoveAt(points.Count - 1);

            int segmentCount = closed ? points.Count : points.Count - 1;
            var segments = new List<FeatureSegment>();
            double total = 0.0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                Point3d start = points[segment];
                Point3d end = points[(segment + 1) % points.Count];
                double bulge = 0.0;
                try { bulge = featureLine.GetBulge(segment); } catch { }

                ArcMeasure arc;
                double length;
                if (Math.Abs(bulge) > Tolerance && TryArc(start, end, bulge, out arc))
                    length = arc.Length;
                else
                {
                    arc = null;
                    length = PlanDistance(start, end);
                }
                if (length <= Tolerance) continue;
                segments.Add(new FeatureSegment
                {
                    Index = segment,
                    Start = start,
                    End = end,
                    Arc = arc,
                    StartDistance = total,
                    Length = length
                });
                total += length;
            }
            if (!(total > spacing + Tolerance)) return;

            int intervalIndex = 1;
            for (double distance = spacing;
                 distance < total - Tolerance && intervalIndex < 100000;
                 distance += spacing, intervalIndex++)
            {
                FeatureSegment segment = segments.LastOrDefault(item =>
                    distance >= item.StartDistance - Tolerance &&
                    distance <= item.StartDistance + item.Length + Tolerance);
                if (segment == null) continue;
                double local = Math.Max(0.0, Math.Min(
                    segment.Length,
                    distance - segment.StartDistance));
                double fraction = segment.Length <= Tolerance ? 0.0 : local / segment.Length;
                Point3d point = segment.Arc == null
                    ? Interpolate(segment.Start, segment.End, fraction)
                    : PointOnArc(segment.Arc, segment.Start, segment.End, fraction);
                if (ContainsPoint(source.Records, point)) continue;
                VertexSettingRecord record = NewRecord(
                    source,
                    "I" + intervalIndex.ToString(CultureInfo.InvariantCulture),
                    segment.Index + 1,
                    point,
                    distance);
                if (segment.Arc != null) record.Radius = segment.Arc.Radius;
                source.Records.Add(record);
            }

            source.Records = source.Records
                .OrderBy(item => FeatureDistance(featureLine, item.Point))
                .ToList();
        }

        private static double FeatureDistance(FeatureLine featureLine, Point3d point)
        {
            try { return featureLine.Get3dDistanceAtPoint(point); }
            catch { return double.MaxValue; }
        }

        private static VertexSettingSource BuildAlignment(
            Alignment alignment,
            string mode,
            double intervalSpacing)
        {
            if (alignment == null) return null;
            double startStation = alignment.StartingStation;
            double endStation = alignment.EndingStation;
            if (!(endStation > startStation + Tolerance)) return null;

            string handle = alignment.Handle.ToString();
            string name = string.IsNullOrWhiteSpace(alignment.Name)
                ? handle
                : alignment.Name;
            var records = new List<VertexSettingRecord>();

            try
            {
                Station[] stations = alignment.GetStationSet(
                    StationTypes.GeometryPoint | StationTypes.PIPoint);
                foreach (Station station in stations
                    .Where(item => item != null)
                    .OrderBy(item => item.RawStation))
                {
                    if (station.RawStation < startStation - Tolerance ||
                        station.RawStation > endStation + Tolerance)
                        continue;
                    Point2d location = station.Location;
                    Point3d point = new Point3d(location.X, location.Y, 0.0);
                    if (ContainsPoint(records, point)) continue;
                    records.Add(NewAlignmentRecord(
                        handle,
                        name,
                        "AV" + records.Count.ToString(CultureInfo.InvariantCulture),
                        "VERTEX",
                        point,
                        station.RawStation - startStation));
                }
            }
            catch { }

            AddAlignmentStation(records, alignment, handle, name, startStation, startStation, "AV_START", "VERTEX");
            AddAlignmentStation(records, alignment, handle, name, startStation, endStation, "AV_END", "VERTEX");

            bool intervals = string.Equals(mode, "Vertices + specified intervals", StringComparison.OrdinalIgnoreCase);
            if (intervals && intervalSpacing > Tolerance)
            {
                int intervalIndex = 1;
                for (double station = startStation + intervalSpacing;
                     station < endStation - Tolerance && intervalIndex < 100000;
                     station += intervalSpacing, intervalIndex++)
                {
                    Point3d point;
                    if (!TryAlignmentPoint(alignment, station, out point)) continue;
                    if (ContainsPoint(records, point)) continue;
                    records.Add(NewAlignmentRecord(
                        handle,
                        name,
                        "AI" + intervalIndex.ToString(CultureInfo.InvariantCulture),
                        "INTERVAL",
                        point,
                        station - startStation));
                }
            }

            records = records.OrderBy(item => item.SegmentLength).ToList();
            for (int index = 0; index < records.Count; index++)
                records[index].SegmentIndex = index + 1;

            return new VertexSettingSource
            {
                SourceId = alignment.ObjectId,
                Handle = handle,
                Name = name,
                Records = records,
                Dimensions = new List<VertexRadialDimension>()
            };
        }

        private static void AddAlignmentStation(
            ICollection<VertexSettingRecord> records,
            Alignment alignment,
            string handle,
            string name,
            double startStation,
            double station,
            string key,
            string kind)
        {
            Point3d point;
            if (!TryAlignmentPoint(alignment, station, out point)) return;
            if (ContainsPoint(records, point)) return;
            records.Add(NewAlignmentRecord(
                handle,
                name,
                key,
                kind,
                point,
                station - startStation));
        }

        private static bool TryAlignmentPoint(
            Alignment alignment,
            double station,
            out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                double easting = 0.0;
                double northing = 0.0;
                alignment.PointLocation(station, 0.0, ref easting, ref northing);
                point = new Point3d(easting, northing, 0.0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static VertexSettingRecord NewRecord(
            VertexSettingSource source,
            string localKey,
            int segmentIndex,
            Point3d point,
            double distance)
        {
            return new VertexSettingRecord
            {
                Key = source.Handle + "|" + localKey,
                Kind = "INTERVAL",
                SourceHandle = source.Handle,
                SourceName = source.Name,
                SegmentIndex = segmentIndex,
                Point = point,
                SegmentLength = distance,
                PointName = string.Empty
            };
        }

        private static VertexSettingRecord NewAlignmentRecord(
            string handle,
            string name,
            string localKey,
            string kind,
            Point3d point,
            double stationDistance)
        {
            return new VertexSettingRecord
            {
                Key = handle + "|" + localKey,
                Kind = kind,
                SourceHandle = handle,
                SourceName = name,
                SegmentIndex = 1,
                Point = point,
                SegmentLength = Math.Max(0.0, stationDistance),
                PointName = string.Empty
            };
        }

        private static bool ContainsPoint(
            IEnumerable<VertexSettingRecord> records,
            Point3d point)
        {
            return records != null && records.Any(item => item.Point.DistanceTo(point) <= 1e-6);
        }

        private static double PlanDistance(Point3d start, Point3d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point3d Interpolate(Point3d start, Point3d end, double fraction)
        {
            return new Point3d(
                start.X + (end.X - start.X) * fraction,
                start.Y + (end.Y - start.Y) * fraction,
                start.Z + (end.Z - start.Z) * fraction);
        }

        private static bool TryArc(
            Point3d start,
            Point3d end,
            double bulge,
            out ArcMeasure arc)
        {
            arc = null;
            double chord = PlanDistance(start, end);
            if (chord <= Tolerance) return false;
            double includedAngle = 4.0 * Math.Atan(bulge);
            double sine = Math.Sin(Math.Abs(includedAngle) * 0.5);
            double tangent = Math.Tan(includedAngle * 0.5);
            if (Math.Abs(sine) <= Tolerance || Math.Abs(tangent) <= Tolerance)
                return false;

            double radius = chord / (2.0 * sine);
            double midX = (start.X + end.X) * 0.5;
            double midY = (start.Y + end.Y) * 0.5;
            double normalX = -(end.Y - start.Y) / chord;
            double normalY = (end.X - start.X) / chord;
            double centerOffset = chord / (2.0 * tangent);
            double centerX = midX + normalX * centerOffset;
            double centerY = midY + normalY * centerOffset;
            double startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);

            arc = new ArcMeasure
            {
                Center = new Point3d(centerX, centerY, (start.Z + end.Z) * 0.5),
                Radius = radius,
                Length = Math.Abs(includedAngle) * radius,
                StartAngle = startAngle,
                IncludedAngle = includedAngle
            };
            return true;
        }

        private static Point3d PointOnArc(
            ArcMeasure arc,
            Point3d start,
            Point3d end,
            double fraction)
        {
            double angle = arc.StartAngle + arc.IncludedAngle * fraction;
            return new Point3d(
                arc.Center.X + arc.Radius * Math.Cos(angle),
                arc.Center.Y + arc.Radius * Math.Sin(angle),
                start.Z + (end.Z - start.Z) * fraction);
        }

        private sealed class FeatureSegment
        {
            internal int Index;
            internal Point3d Start;
            internal Point3d End;
            internal ArcMeasure Arc;
            internal double StartDistance;
            internal double Length;
        }

        private sealed class ArcMeasure
        {
            internal Point3d Center;
            internal double Radius;
            internal double Length;
            internal double StartAngle;
            internal double IncludedAngle;
        }
    }
}
'@
WriteText $helperPath $helper

# Final staged-source guards before Roslyn sees the August 19 layer.
$vertexFinal = ReadText $vertexPath
$geometryFinal = ReadText $geometryPath
$universalFinal = ReadText $universalPath
$helperFinal = ReadText $helperPath
foreach ($check in @(
    @{ Name = 'August 18 Swap X/Y remains intact'; Ok = $vertexFinal.Contains('"SwapXY", "04 Coordinate Display", "Swap X / Y values"') },
    @{ Name = 'Specified interval popup'; Ok = $vertexFinal.Contains('"Vertices + specified intervals"') },
    @{ Name = 'Linked interval persistence'; Ok = $vertexFinal.Contains('"INTERVAL=" + link.IntervalSpacing') },
    @{ Name = 'Alignment accepted by selection'; Ok = $geometryFinal.Contains('entity is Autodesk.Civil.DatabaseServices.Alignment') },
    @{ Name = 'Alignment source-owner refresh'; Ok = $universalFinal.Contains('bool alignment = typeName.IndexOf(') },
    @{ Name = 'Alignment station sampling'; Ok = $helperFinal.Contains('alignment.PointLocation(') },
    @{ Name = 'Alignment geometry stations'; Ok = $helperFinal.Contains('StationTypes.GeometryPoint | StationTypes.PIPoint') },
    @{ Name = 'True curve interval sampling'; Ok = $helperFinal.Contains('curve.GetPointAtDist(distance)') },
    @{ Name = 'Feature-line interval sampling'; Ok = $helperFinal.Contains('AddFeatureLineIntervals') }
)) {
    if (-not $check.Ok) {
        throw "August 19 final guard failed: $($check.Name)"
    }
}

Write-Host 'August 19 Vertex Setting-Out repair complete.' -ForegroundColor Green
Write-Host '  - The complete August 18 staged state was verified first.' -ForegroundColor Green
Write-Host '  - Existing August 18 source files were not edited in the repository checkout.' -ForegroundColor Green
Write-Host '  - Vertex Setting-Out accepts multiple polylines, feature lines and alignments.' -ForegroundColor Green
Write-Host '  - Point generation offers Engineering, Vertices only, or Vertices + specified intervals.' -ForegroundColor Green
Write-Host '  - Interval spacing persists with the linked table and is reused on refresh.' -ForegroundColor Green
Write-Host '  - Linked alignment edits participate in the same source-owner-only automatic refresh policy.' -ForegroundColor Green
