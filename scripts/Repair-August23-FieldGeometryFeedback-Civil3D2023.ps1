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
        throw "August 23 field-geometry prerequisite missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function MethodRange([string]$text,[string]$marker,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 23 field-geometry method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 23 field-geometry method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 23 field-geometry opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 23 field-geometry closing brace not found: $label" }
    return @($start,$open,$close)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $range = MethodRange $text $marker $label
    $open = [int]$range[1]
    $close = [int]$range[2]
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}

$geometryPath = Required 'August20GeometryFirstSewerCommands.cs'
$breakPath = Required 'August21SafePolylineBreakEngine.cs'

# -----------------------------------------------------------------------------
# 1. Road-reserve centreline pairing.
# The old algorithm globally marked an exterior edge as "used" after its first
# match. A long cadastral edge could therefore create one centreline only even
# when several non-overlapping road-reserve gaps existed along that same edge.
# Build every valid candidate first, prefer the nearest facing gap, and reserve
# only the accepted overlap interval on each edge. One edge may now participate
# in several DISJOINT road-reserve centreline segments without duplicate/parallel
# pairings over the same span.
# -----------------------------------------------------------------------------
$geometry = ReadText $geometryPath
$pairBody = @'
            var candidates = new List<EdgePair>();
            if (exterior == null || exterior.Count < 2) return candidates;

            for (int firstIndex = 0; firstIndex < exterior.Count; firstIndex++)
            {
                EdgeLite first = exterior[firstIndex];
                for (int secondIndex = firstIndex + 1; secondIndex < exterior.Count; secondIndex++)
                {
                    EdgeLite second = exterior[secondIndex];
                    if (second.Id == first.Id || second.ParcelIndex == first.ParcelIndex) continue;
                    if (first.Horizontal != second.Horizontal || first.Vertical != second.Vertical) continue;

                    double gap;
                    double lo;
                    double hi;
                    if (!OverlapAndGap(first, second, out gap, out lo, out hi)) continue;
                    if (gap < minWidth - Tol || gap > maxWidth + Tol) continue;

                    double overlap = hi - lo;
                    double required = Math.Min(first.Length, second.Length) * minOverlapPercent / 100.0;
                    if (overlap + Tol < required) continue;
                    if (!FacesGap(first, second)) continue;

                    candidates.Add(new EdgePair(first, second, gap, lo, hi));
                }
            }

            var pairs = new List<EdgePair>();
            var occupied = new Dictionary<int, List<double[]>>();
            foreach (EdgePair candidate in candidates
                .OrderBy(pair => pair.Width)
                .ThenByDescending(pair => pair.OverlapHi - pair.OverlapLo))
            {
                List<double[]> firstIntervals;
                if (!occupied.TryGetValue(candidate.First.Id, out firstIntervals))
                {
                    firstIntervals = new List<double[]>();
                    occupied[candidate.First.Id] = firstIntervals;
                }

                List<double[]> secondIntervals;
                if (!occupied.TryGetValue(candidate.Second.Id, out secondIntervals))
                {
                    secondIntervals = new List<double[]>();
                    occupied[candidate.Second.Id] = secondIntervals;
                }

                bool firstConflict = firstIntervals.Any(interval =>
                    Math.Min(interval[1], candidate.OverlapHi) -
                    Math.Max(interval[0], candidate.OverlapLo) > KeyTolerance);
                bool secondConflict = secondIntervals.Any(interval =>
                    Math.Min(interval[1], candidate.OverlapHi) -
                    Math.Max(interval[0], candidate.OverlapLo) > KeyTolerance);
                if (firstConflict || secondConflict) continue;

                pairs.Add(candidate);
                firstIntervals.Add(new[] { candidate.OverlapLo, candidate.OverlapHi });
                secondIntervals.Add(new[] { candidate.OverlapLo, candidate.OverlapHi });
            }
            return pairs;
'@
$geometry = ReplaceMethodBody $geometry `
    '        private static List<EdgePair> PairRoadReserveEdges(' `
    $pairBody `
    'Road Reserve disjoint centreline pairing'
WriteText $geometryPath $geometry

# -----------------------------------------------------------------------------
# 2. Break at crossings / T-junctions.
# IntersectWith can miss a T-junction when source elevations differ slightly or a
# drafted endpoint is only a few millimetres from the receiving polyline. Keep the
# exact intersection pass, then run a plan-XY vertex/endpoint proximity pass. The
# split point is always projected back onto the real source curve before replacement,
# so the verified create-first / erase-last safety boundary remains unchanged.
# -----------------------------------------------------------------------------
$break = ReadText $breakPath
if (-not $break.Contains('private const double JunctionSnapTolerance = 0.01;')) {
    $anchor = '        private const double Tolerance = 0.000001;'
    if (-not $break.Contains($anchor)) {
        throw 'August 23 field-geometry break tolerance anchor missing.'
    }
    $break = $break.Replace(
        $anchor,
        $anchor + "`r`n" + '        private const double JunctionSnapTolerance = 0.01;')
}

$analyseBody = @'
            var result = ids.ToDictionary(
                id => id,
                id => new SplitPlan { SourceId = id });
            var unique = new List<Point3d>();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                for (int firstIndex = 0; firstIndex < ids.Count; firstIndex++)
                {
                    Curve first = transaction.GetObject(
                        ids[firstIndex], OpenMode.ForRead, false) as Curve;
                    if (first == null || first.IsErased) continue;

                    for (int secondIndex = firstIndex + 1; secondIndex < ids.Count; secondIndex++)
                    {
                        Curve second = transaction.GetObject(
                            ids[secondIndex], OpenMode.ForRead, false) as Curve;
                        if (second == null || second.IsErased) continue;

                        var intersections = new Point3dCollection();
                        try
                        {
                            first.IntersectWith(
                                second,
                                Intersect.OnBothOperands,
                                intersections,
                                IntPtr.Zero,
                                IntPtr.Zero);
                        }
                        catch { }

                        foreach (Point3d intersection in intersections)
                        {
                            int beforeFirst = result[ids[firstIndex]].Points.Count;
                            int beforeSecond = result[ids[secondIndex]].Points.Count;
                            AddInternal(first, intersection, result[ids[firstIndex]].Points);
                            AddInternal(second, intersection, result[ids[secondIndex]].Points);
                            if (result[ids[firstIndex]].Points.Count > beforeFirst ||
                                result[ids[secondIndex]].Points.Count > beforeSecond)
                                AddUnique(unique, intersection);
                        }

                        AddPlanVertexTouches(
                            first,
                            second,
                            result[ids[firstIndex]].Points,
                            result[ids[secondIndex]].Points,
                            unique);
                        AddPlanVertexTouches(
                            second,
                            first,
                            result[ids[secondIndex]].Points,
                            result[ids[firstIndex]].Points,
                            unique);
                    }
                }
            }

            uniqueIntersections = unique.Count;
            return result;
'@
$break = ReplaceMethodBody $break `
    '        private static Dictionary<ObjectId, SplitPlan> Analyse(' `
    $analyseBody `
    'Polyline crossing/T-junction analysis'

if (-not $break.Contains('        private static void AddPlanVertexTouches(')) {
    $marker = '        private static void AddInternal('
    $insertAt = $break.IndexOf($marker,[StringComparison]::Ordinal)
    if ($insertAt -lt 0) {
        throw 'August 23 field-geometry AddInternal insertion marker missing.'
    }
    $helpers = @'
        private static void AddPlanVertexTouches(
            Curve source,
            Curve other,
            IList<Point3d> sourcePoints,
            IList<Point3d> otherPoints,
            IList<Point3d> unique)
        {
            if (source == null || other == null) return;
            var candidates = new List<Point3d>();

            Polyline lightweight = source as Polyline;
            if (lightweight != null)
            {
                for (int index = 0; index < lightweight.NumberOfVertices; index++)
                {
                    try { candidates.Add(lightweight.GetPoint3dAt(index)); }
                    catch { }
                }
            }
            else
            {
                try { candidates.Add(source.StartPoint); } catch { }
                try { candidates.Add(source.EndPoint); } catch { }
            }

            foreach (Point3d candidate in candidates)
            {
                try
                {
                    Point3d onOther = other.GetClosestPointTo(candidate, false);
                    double dx = candidate.X - onOther.X;
                    double dy = candidate.Y - onOther.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > JunctionSnapTolerance)
                        continue;

                    int beforeSource = sourcePoints.Count;
                    int beforeOther = otherPoints.Count;
                    AddInternal(source, candidate, sourcePoints);
                    AddInternal(other, onOther, otherPoints);
                    if (sourcePoints.Count > beforeSource || otherPoints.Count > beforeOther)
                    {
                        AddUnique(
                            unique,
                            new Point3d(
                                (candidate.X + onOther.X) * 0.5,
                                (candidate.Y + onOther.Y) * 0.5,
                                0.0));
                    }
                }
                catch { }
            }
        }

'@ -replace "`r?`n","`r`n"
    $break = $break.Insert($insertAt,$helpers)
}
WriteText $breakPath $break

# Final semantic guards.
$geometryCheck = ReadText $geometryPath
$breakCheck = ReadText $breakPath
foreach ($marker in @(
    'var occupied = new Dictionary<int, List<double[]>>();',
    '.OrderBy(pair => pair.Width)',
    'firstIntervals.Add(new[] { candidate.OverlapLo, candidate.OverlapHi });',
    'secondIntervals.Add(new[] { candidate.OverlapLo, candidate.OverlapHi });')) {
    if (-not $geometryCheck.Contains($marker)) {
        throw "August 23 Road Reserve centreline guard missing: $marker"
    }
}
if ($geometryCheck.Contains('var used = new HashSet<int>();')) {
    throw 'August 23 Road Reserve centreline repair failed: global one-use edge lock still exists.'
}
foreach ($marker in @(
    'private const double JunctionSnapTolerance = 0.01;',
    'AddPlanVertexTouches(',
    'Math.Sqrt(dx * dx + dy * dy) > JunctionSnapTolerance',
    'GetClosestPointTo(candidate, false)')) {
    if (-not $breakCheck.Contains($marker)) {
        throw "August 23 break-at-junction guard missing: $marker"
    }
}
foreach ($marker in @(
    'VerifyReplacement(',
    'Cleanup(database, newIds);',
    'source.Erase();')) {
    if (-not $breakCheck.Contains($marker)) {
        throw "August 23 break safety preservation guard missing: $marker"
    }
}

Write-Host 'August 23 field geometry feedback pass applied:' -ForegroundColor Green
Write-Host ' - Road Reserve centreline pairing now supports multiple disjoint reserve gaps per cadastral edge.' -ForegroundColor Green
Write-Host ' - Break at Crossings/T-junctions now detects near/plan-XY vertex touches while preserving verified create-first safety.' -ForegroundColor Green
