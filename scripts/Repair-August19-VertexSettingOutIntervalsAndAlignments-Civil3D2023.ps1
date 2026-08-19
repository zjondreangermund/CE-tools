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
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 19 repair anchor not found: $label" }
    return $text.Replace($old,$new)
}
function ReplaceMethod([string]$text,[string]$marker,[string]$replacement,[string]$label) {
    $replacement = $replacement -replace "`r?`n","`r`n"
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 19 method marker not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 19 opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 19 closing brace not found: $label" }
    return $text.Substring(0,$start) + $replacement + $text.Substring($close + 1)
}
function InsertBefore([string]$text,[string]$marker,[string]$addition,[string]$label) {
    if ($text.Contains($addition.Trim())) { return $text }
    $index = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "August 19 insertion anchor not found: $label" }
    return $text.Substring(0,$index) + ($addition -replace "`r?`n","`r`n") + $text.Substring($index)
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

# August 19 is deliberately allowed to run only after the complete August 18
# staging + final precompile architecture has already been applied.
$august18Checks = @(
    @{ Name = 'August 18 coordinate display'; Ok = $vertex.Contains('"SwapXY", "04 Coordinate Display", "Swap X / Y values"') },
    @{ Name = 'August 18 background refresh entry point'; Ok = $universal.Contains('RefreshBackground(Document document)') },
    @{ Name = 'August 18 self-refresh command guard'; Ok = $universal.Contains('IsSelfRefreshingSurveyCommand(string command)') },
    @{ Name = 'August 18 dedicated dynamic grid route'; Ok = $finalGrid.Contains('CE_GRIDSETTINGOUTDYNAMIC') },
    @{ Name = 'August 18 grid coordinate sign control'; Ok = $dynamicGrid.Contains('ReverseSigns') }
)
$missingAugust18 = $august18Checks | Where-Object { -not $_.Ok }
if ($missingAugust18) {
    throw ('August 19 refused to run because the complete August 18 staged state was not detected: ' + (($missingAugust18 | ForEach-Object { $_.Name }) -join ', '))
}
Write-Host 'August 18 staged state verified. Applying August 19 only after all August 18 repairs.' -ForegroundColor Green

# -----------------------------------------------------------------------------
# Vertex Setting-Out UI / persistence / refresh.
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
                "Choose the complete engineering rules, source vertices/geometry points only, or vertices plus points at a specified interval measured along each source.",
                new[] { "Engineering setting-out points", "Vertices only", "Vertices + specified intervals" });
            settings.AddPositiveDouble(
                "IntervalSpacing", "01 Output", "Specified point interval", 10.0,
                "Used by 'Vertices + specified intervals'. Points are measured from the start of each selected polyline, feature line or alignment and follow true curved alignment/arc geometry.");
'@
$vertex = ReplaceRequired $vertex $oldGeneration $newGeneration 'Vertex point-generation popup options'

$vertex = ReplaceRequired $vertex `
    '            string generationMode = settings.Text("Generation");' `
    ("            string generationMode = settings.Text(\"Generation\");`r`n" +
     "            double intervalSpacing = settings.Double(\"IntervalSpacing\", 10.0);") `
    'Vertex interval setting read'

$oldInitialRead = @'
                sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out geometryRejected);
'@
$newInitialRead = @'
                sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    generationMode,
                    intervalSpacing,
                    out geometryRejected);
'@
$vertex = ReplaceRequired $vertex $oldInitialRead $newInitialRead 'Initial multi-source geometry read with interval mode'
$vertex = $vertex.Replace("            ApplyGenerationMode(sources, generationMode);`r`n",'')

$vertex = ReplaceRequired $vertex `
    '                GenerationMode = generationMode,' `
    ("                GenerationMode = generationMode,`r`n" +
     "                IntervalSpacing = intervalSpacing,") `
    'Persist interval spacing on new/continued link'

$reviewAnchor = '                new KeyValuePair<string, string>("Point output", outputType),'
$reviewAddition = @'
                new KeyValuePair<string, string>("Point output", outputType),
                new KeyValuePair<string, string>("Point generation", generationMode),
                new KeyValuePair<string, string>("Specified interval", string.Equals(generationMode, "Vertices + specified intervals", StringComparison.OrdinalIgnoreCase) ? intervalSpacing.ToString("0.###", CultureInfo.CurrentCulture) : "Not used"),
'@
$vertex = ReplaceRequired $vertex $reviewAnchor $reviewAddition.TrimEnd() 'Vertex preview generation/interval rows'

$oldRefreshRead = @'
                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out rejected);
'@
$newRefreshRead = @'
                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    link.GenerationMode,
                    link.IntervalSpacing,
                    out rejected);
'@
$vertex = ReplaceRequired $vertex $oldRefreshRead $newRefreshRead 'Linked refresh interval-aware geometry read'
$vertex = $vertex.Replace("                ApplyGenerationMode(sources, link.GenerationMode);`r`n",'')
$vertex = $vertex.Replace(
    'None of the linked source polylines or feature lines are available.',
    'None of the linked source polylines, feature lines or alignments are available.')

$writeGen = @'
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "GEN=" + (link.GenerationMode ?? string.Empty)));
'@
$writeInterval = @'
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "GEN=" + (link.GenerationMode ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "INTERVAL=" + link.IntervalSpacing.ToString("R", CultureInfo.InvariantCulture)));
'@
$vertex = ReplaceRequired $vertex $writeGen $writeInterval 'Vertex table link interval persistence'

$vertex = ReplaceRequired $vertex `
    '                GenerationMode = "Engineering setting-out points",' `
    ("                GenerationMode = \"Engineering setting-out points\",`r`n" +
     "                IntervalSpacing = 10.0,") `
    'Backward-compatible interval default'

$parseGen = @'
                if (value.StartsWith("GEN=", StringComparison.OrdinalIgnoreCase))
                    link.GenerationMode = value.Substring(4);
                else if (value.StartsWith("ELEV=", StringComparison.OrdinalIgnoreCase))
'@
$parseInterval = @'
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
$vertex = ReplaceRequired $vertex $parseGen $parseInterval 'Read saved Vertex interval spacing'

$vertex = ReplaceRequired $vertex `
    '            public string GenerationMode { get; set; }' `
    ("            public string GenerationMode { get; set; }`r`n" +
     "            public double IntervalSpacing { get; set; }") `
    'Vertex link interval field'

WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# Geometry extractor. Preserve every August 18 rule; add an overload so only
# August 19 callers request interval/alignment generation.
# -----------------------------------------------------------------------------
$geometry = ReplaceRequired $geometry `
    'using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;' `
    ("using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;`r`n" +
     "using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;`r`n" +
     "using CivilStation = Autodesk.Civil.DatabaseServices.Station;`r`n" +
     "using CivilStationTypes = Autodesk.Civil.DatabaseServices.StationTypes;") `
    'Civil 3D alignment aliases'

$readSources = @'
        public static IList<VertexSettingSource> ReadSources(
            Database database,
            Transaction transaction,
            IEnumerable<ObjectId> sourceIds,
            out int rejected)
        {
            return ReadSources(
                database,
                transaction,
                sourceIds,
                "Engineering setting-out points",
                10.0,
                out rejected);
        }

        public static IList<VertexSettingSource> ReadSources(
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
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch
                {
                    rejected++;
                    continue;
                }

                VertexSettingSource source = BuildSource(
                    entity,
                    transaction,
                    generationMode,
                    intervalSpacing);
                if (source == null || source.Records == null || source.Records.Count == 0)
                {
                    rejected++;
                    continue;
                }
                result.Add(source);
            }
            return result;
        }
'@
$geometry = ReplaceMethod $geometry '        public static IList<VertexSettingSource> ReadSources(' $readSources 'Interval-aware ReadSources overload'

$isSupported = @'
        public static bool IsSupported(Entity entity)
        {
            return entity is Polyline ||
                   entity is Polyline2d ||
                   entity is Polyline3d ||
                   entity is CivilFeatureLine ||
                   entity is CivilAlignment;
        }
'@
$geometry = ReplaceMethod $geometry '        public static bool IsSupported(Entity entity)' $isSupported 'Alignment selection support'

$buildSource = @'
        private static VertexSettingSource BuildSource(
            Entity entity,
            Transaction transaction,
            string generationMode,
            double intervalSpacing)
        {
            if (entity == null || !IsSupported(entity)) return null;

            CivilAlignment alignment = entity as CivilAlignment;
            if (alignment != null)
                return BuildAlignment(alignment, generationMode, intervalSpacing);

            var points = new List<Point3d>();
            var bulges = new List<double>();
            bool closed;
            VertexSettingSource source;

            Polyline lightweight = entity as Polyline;
            if (lightweight != null)
            {
                for (int index = 0; index < lightweight.NumberOfVertices; index++)
                    points.Add(lightweight.GetPoint3dAt(index));
                closed = lightweight.Closed;
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++)
                    bulges.Add(lightweight.GetBulgeAt(index));
                source = BuildFromVertices(entity, points, bulges, closed);
                return ApplyRequestedGeneration(source, points, bulges, closed, generationMode, intervalSpacing);
            }

            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex == null) continue;
                    points.Add(vertex.Position);
                    bulges.Add(vertex.Bulge);
                }
                closed = polyline2d.Closed;
                RemoveClosingDuplicate(points, bulges);
                TrimBulges(points, bulges, closed);
                source = BuildFromVertices(entity, points, bulges, closed);
                return ApplyRequestedGeneration(source, points, bulges, closed, generationMode, intervalSpacing);
            }

            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                closed = polyline3d.Closed;
                RemoveClosingDuplicate(points, null);
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++) bulges.Add(0.0);
                source = BuildFromVertices(entity, points, bulges, closed);
                return ApplyRequestedGeneration(source, points, bulges, closed, generationMode, intervalSpacing);
            }

            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection piPoints = featureLine.GetPoints(
                    FeatureLinePointType.PIPoint);
                foreach (Point3d point in piPoints) points.Add(point);
                closed = featureLine.Closed;
                RemoveClosingDuplicate(points, null);
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++)
                {
                    double bulge = 0.0;
                    try { bulge = featureLine.GetBulge(index); }
                    catch { bulge = 0.0; }
                    bulges.Add(bulge);
                }
                source = BuildFromVertices(entity, points, bulges, closed);
                return ApplyRequestedGeneration(source, points, bulges, closed, generationMode, intervalSpacing);
            }

            return null;
        }
'@
$geometry = ReplaceMethod $geometry '        private static VertexSettingSource BuildSource(' $buildSource 'Multi-source BuildSource with alignments'

$generationHelpers = @'
        private static VertexSettingSource ApplyRequestedGeneration(
            VertexSettingSource source,
            IList<Point3d> points,
            IList<double> bulges,
            bool closed,
            string generationMode,
            double intervalSpacing)
        {
            if (source == null) return null;
            bool verticesOnly = string.Equals(
                generationMode,
                "Vertices only",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    generationMode,
                    "Polyline vertices only",
                    StringComparison.OrdinalIgnoreCase);
            bool intervals = string.Equals(
                generationMode,
                "Vertices + specified intervals",
                StringComparison.OrdinalIgnoreCase);
            if (!verticesOnly && !intervals) return source;

            source.Records = source.Records
                .Where(record => string.Equals(
                    record.Kind,
                    "VERTEX",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            source.Dimensions = new List<VertexRadialDimension>();
            if (intervals)
                AddIntervalRecords(source, points, bulges, closed, intervalSpacing);
            return source;
        }

        private static void AddIntervalRecords(
            VertexSettingSource source,
            IList<Point3d> points,
            IList<double> bulges,
            bool closed,
            double spacing)
        {
            if (source == null || points == null || points.Count < 2 || !(spacing > Tolerance))
                return;
            int segmentCount = closed ? points.Count : points.Count - 1;
            if (segmentCount < 1) return;

            var measures = new List<SegmentMeasure>();
            double total = 0.0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                Point3d start = points[segment];
                Point3d end = points[(segment + 1) % points.Count];
                double bulge = segment < bulges.Count ? bulges[segment] : 0.0;
                ArcData arc = null;
                double length;
                if (Math.Abs(bulge) > Tolerance && TryArc(start, end, bulge, out arc))
                    length = arc.Length;
                else
                    length = PlanDistance(start, end);
                if (length <= Tolerance) continue;
                measures.Add(new SegmentMeasure
                {
                    SegmentIndex = segment,
                    Start = start,
                    End = end,
                    Arc = arc,
                    StartDistance = total,
                    Length = length
                });
                total += length;
            }
            if (total <= spacing + Tolerance) return;

            int intervalIndex = 1;
            for (double distance = spacing;
                 distance < total - Tolerance && intervalIndex < 100000;
                 distance += spacing, intervalIndex++)
            {
                SegmentMeasure measure = measures.LastOrDefault(item =>
                    distance >= item.StartDistance - Tolerance &&
                    distance <= item.StartDistance + item.Length + Tolerance);
                if (measure == null) continue;
                double local = Math.Max(0.0, Math.Min(
                    measure.Length,
                    distance - measure.StartDistance));
                double fraction = measure.Length <= Tolerance ? 0.0 : local / measure.Length;
                Point3d point = measure.Arc == null
                    ? Interpolate(measure.Start, measure.End, fraction)
                    : PointOnArc(measure.Arc, measure.Start, measure.End, fraction);
                if (source.Records.Any(record => record.Point.DistanceTo(point) <= 1e-6))
                    continue;
                source.Records.Add(Record(
                    source.Handle,
                    source.Name,
                    "I" + intervalIndex.ToString(CultureInfo.InvariantCulture),
                    "INTERVAL",
                    measure.SegmentIndex + 1,
                    point,
                    measure.Length,
                    measure.Arc == null ? (double?)null : measure.Arc.Radius));
            }
        }

        private static Point3d PointOnArc(
            ArcData arc,
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

        private static VertexSettingSource BuildAlignment(
            CivilAlignment alignment,
            string generationMode,
            double intervalSpacing)
        {
            if (alignment == null) return null;
            string handle = alignment.Handle.ToString();
            string sourceName = string.IsNullOrWhiteSpace(alignment.Name)
                ? handle
                : alignment.Name;
            double startStation = alignment.StartingStation;
            double endStation = alignment.EndingStation;
            if (!(endStation > startStation + Tolerance)) return null;

            var records = new List<VertexSettingRecord>();
            var geometryStations = new List<CivilStation>();
            try
            {
                geometryStations.AddRange(alignment.GetStationSet(
                    CivilStationTypes.GeometryPoint | CivilStationTypes.PIPoint));
            }
            catch { }

            var stations = geometryStations
                .Where(item => item != null &&
                    item.RawStation >= startStation - Tolerance &&
                    item.RawStation <= endStation + Tolerance)
                .OrderBy(item => item.RawStation)
                .GroupBy(item => Math.Round(item.RawStation, 8))
                .Select(group => group.First())
                .ToList();

            int vertexIndex = 0;
            foreach (CivilStation station in stations)
            {
                Point2d location = station.Location;
                records.Add(Record(
                    handle,
                    sourceName,
                    "AV" + vertexIndex.ToString(CultureInfo.InvariantCulture),
                    "VERTEX",
                    vertexIndex + 1,
                    new Point3d(location.X, location.Y, 0.0),
                    Math.Max(0.0, station.RawStation - startStation),
                    null));
                vertexIndex++;
            }

            AddAlignmentEndpointIfMissing(
                records, alignment, handle, sourceName, startStation, startStation, true);
            AddAlignmentEndpointIfMissing(
                records, alignment, handle, sourceName, startStation, endStation, false);

            bool intervals = string.Equals(
                generationMode,
                "Vertices + specified intervals",
                StringComparison.OrdinalIgnoreCase);
            if (intervals && intervalSpacing > Tolerance)
            {
                int intervalIndex = 1;
                for (double station = startStation + intervalSpacing;
                     station < endStation - Tolerance && intervalIndex < 100000;
                     station += intervalSpacing, intervalIndex++)
                {
                    Point3d point;
                    if (!TryAlignmentPoint(alignment, station, out point)) continue;
                    if (records.Any(record => record.Point.DistanceTo(point) <= 1e-6))
                        continue;
                    records.Add(Record(
                        handle,
                        sourceName,
                        "AI" + intervalIndex.ToString(CultureInfo.InvariantCulture),
                        "INTERVAL",
                        intervalIndex,
                        point,
                        intervalSpacing,
                        null));
                }
            }

            return new VertexSettingSource
            {
                SourceId = alignment.ObjectId,
                Handle = handle,
                Name = sourceName,
                Records = records,
                Dimensions = new List<VertexRadialDimension>()
            };
        }

        private static void AddAlignmentEndpointIfMissing(
            ICollection<VertexSettingRecord> records,
            CivilAlignment alignment,
            string handle,
            string sourceName,
            double startStation,
            double station,
            bool start)
        {
            Point3d point;
            if (!TryAlignmentPoint(alignment, station, out point)) return;
            if (records.Any(record => record.Point.DistanceTo(point) <= 1e-6)) return;
            records.Add(Record(
                handle,
                sourceName,
                start ? "AV_START" : "AV_END",
                "VERTEX",
                start ? 1 : Math.Max(1, records.Count + 1),
                point,
                Math.Max(0.0, station - startStation),
                null));
        }

        private static bool TryAlignmentPoint(
            CivilAlignment alignment,
            double station,
            out Point3d point)
        {
            point = Point3d.Origin;
            if (alignment == null) return false;
            try
            {
                double easting = 0.0;
                double northing = 0.0;
                alignment.PointLocation(
                    station,
                    0.0,
                    ref easting,
                    ref northing);
                point = new Point3d(easting, northing, 0.0);
                return true;
            }
            catch
            {
                return false;
            }
        }

'@
$geometry = InsertBefore $geometry '        private static VertexSettingSource BuildFromVertices(' $generationHelpers 'August 19 interval/alignment geometry helpers'

$arcAssignmentOld = @'
            arc = new ArcData
            {
                Center = center,
                MidPoint = midPoint,
                Radius = radius,
                Length = Math.Abs(includedAngle) * radius
            };
'@
$arcAssignmentNew = @'
            arc = new ArcData
            {
                Center = center,
                MidPoint = midPoint,
                Radius = radius,
                Length = Math.Abs(includedAngle) * radius,
                StartAngle = startAngle,
                IncludedAngle = includedAngle
            };
'@
$geometry = ReplaceRequired $geometry $arcAssignmentOld $arcAssignmentNew 'Arc stationing data for true interval points'

$arcClassOld = @'
        private sealed class ArcData
        {
            public Point3d Center { get; set; }
            public Point3d MidPoint { get; set; }
            public double Radius { get; set; }
            public double Length { get; set; }
        }
'@
$arcClassNew = @'
        private sealed class SegmentMeasure
        {
            public int SegmentIndex { get; set; }
            public Point3d Start { get; set; }
            public Point3d End { get; set; }
            public ArcData Arc { get; set; }
            public double StartDistance { get; set; }
            public double Length { get; set; }
        }

        private sealed class ArcData
        {
            public Point3d Center { get; set; }
            public Point3d MidPoint { get; set; }
            public double Radius { get; set; }
            public double Length { get; set; }
            public double StartAngle { get; set; }
            public double IncludedAngle { get; set; }
        }
'@
$geometry = ReplaceRequired $geometry $arcClassOld $arcClassNew 'Interval segment and arc data classes'

WriteText $geometryPath $geometry

# Final guards. These run on the same staged source that will be handed to Roslyn.
$vertexFinal = ReadText $vertexPath
$geometryFinal = ReadText $geometryPath
foreach ($check in @(
    @{ Name='August 18 Swap X/Y remains intact'; Ok=$vertexFinal.Contains('"SwapXY", "04 Coordinate Display", "Swap X / Y values"') },
    @{ Name='August 19 specified interval popup'; Ok=$vertexFinal.Contains('"Vertices + specified intervals"') },
    @{ Name='August 19 linked interval persistence'; Ok=$vertexFinal.Contains('"INTERVAL=" + link.IntervalSpacing') },
    @{ Name='August 19 alignment support'; Ok=$geometryFinal.Contains('entity is CivilAlignment') },
    @{ Name='August 19 alignment station sampling'; Ok=$geometryFinal.Contains('alignment.PointLocation(') },
    @{ Name='August 19 true arc interval sampling'; Ok=$geometryFinal.Contains('PointOnArc(') }
)) {
    if (-not $check.Ok) { throw "August 19 final guard failed: $($check.Name)" }
}

Write-Host 'August 19 Vertex Setting-Out repair complete.' -ForegroundColor Green
Write-Host '  - Existing August 18 staged behavior was verified before any August 19 mutation.' -ForegroundColor Green
Write-Host '  - Multiple polylines, feature lines and Civil 3D alignments are accepted.' -ForegroundColor Green
Write-Host '  - Point generation now offers Engineering, Vertices only, or Vertices + specified intervals.' -ForegroundColor Green
Write-Host '  - Interval points follow true polyline/feature-line arcs and true Civil 3D alignment station geometry.' -ForegroundColor Green
Write-Host '  - Linked tables persist interval spacing and regenerate interval points after source edits.' -ForegroundColor Green
