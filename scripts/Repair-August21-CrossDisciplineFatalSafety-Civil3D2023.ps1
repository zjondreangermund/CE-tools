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
        throw "August 21 fatal-safety prerequisite missing: $path"
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
    if ($start -lt 0) { throw "August 21 fatal-safety method not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 21 fatal-safety method ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 21 fatal-safety opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "August 21 fatal-safety closing brace not found: $label" }
    return @($start,$open,$close)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $range = MethodRange $text $marker $label
    $open = [int]$range[1]
    $close = [int]$range[2]
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}

$featurePath = Required 'FeatureLineConstructionCommands.cs'
$platformSlopePath = Required 'August17ProductionFeatureLineCommands.cs'
$sequencePath = Required 'SewerSequenceCommands.cs'
$dynamicPath = Required 'SewerNetworkDynamicSequenceManager.cs'
$geometryPath = Required 'August20GeometryFirstSewerCommands.cs'
$roadCentrePath = Required 'August13RoadProductionCentres.cs'
$sewerCentrePath = Required 'August14StructuredDisciplineProductionCentres.cs'
[void](Required 'August21CrossDisciplineFatalSafety.cs')

# 1. CE_FLCREATE: no selected/user source is passed directly to FeatureLine.Create.
$feature = ReadText $featurePath
$featureBody = @'
            August21SafeFeatureLineCreation.RunCreateFromObjects(document);
'@
$feature = ReplaceMethodBody $feature `
    '        private static void CreateFromObjects(Document document)' `
    $featureBody `
    'CE_FLCREATE protected-clone delegation'
WriteText $featurePath $feature

# 2. Platform slope feature lines: sanitized committed Polyline3d per source.
$platform = ReadText $platformSlopePath
$platformBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            August21SafeFeatureLineCreation.RunPlatformFeatureLinesAtSlope(document);
'@
$platform = ReplaceMethodBody $platform `
    '        public void PlatformFeatureLinesAtSlope()' `
    $platformBody `
    'Platform feature-line slope protected temporary delegation'
WriteText $platformSlopePath $platform

# 3. CE_SEWSEQ labels must never re-enter Civil label creation synchronously from
# the sequence/rename transaction. Normalize the preserved compatibility repair.
$deferred = Join-Path $root 'scripts\Repair-August21-SewerSequenceDeferredLabelsCompatibility-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $deferred -PathType Leaf)) {
    throw "August 21 Sewer Sequence deferred-label repair missing: $deferred"
}
& $deferred -RepoRoot $root
$global:LASTEXITCODE = 0

# 4. Dynamic sewer resequencing: ObjectModified/ObjectErased and Application.Idle
# are notification-only. Never call the Civil network rewrite stack directly from
# Idle; queue the normal modal command when AutoCAD is command-idle instead.
$dynamic = ReadText $dynamicPath
$onIdle = @'
            try
            {
                Document document = AcApplication.DocumentManager.MdiActiveDocument;
                try { AttachDatabase(document == null ? null : document.Database); }
                catch { return; }
                if (!Enabled || !_pending || _busy || document == null) return;
                if (UniversalDynamicRefreshManager.Enabled)
                {
                    try { UniversalDynamicRefreshManager.Queue(); } catch { }
                    _pending = false;
                    return;
                }
                if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 1200.0) return;
                if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 900.0) return;

                string commandNames = string.Empty;
                try
                {
                    commandNames = Convert.ToString(
                        AcApplication.GetSystemVariable("CMDNAMES"),
                        CultureInfo.InvariantCulture);
                }
                catch { return; }
                if (!string.IsNullOrWhiteSpace(commandNames)) return;

                _busy = true;
                try
                {
                    _pending = false;
                    _lastRunUtc = DateTime.UtcNow;
                    document.SendStringToExecute(
                        "CE_SEWAUTOSEQALL ",
                        true,
                        false,
                        false);
                }
                catch
                {
                    _pending = true;
                }
                finally
                {
                    _busy = false;
                }
            }
            catch
            {
                _busy = false;
                _pending = true;
            }
'@
$dynamic = ReplaceMethodBody $dynamic `
    '        private static void OnIdle(object sender, EventArgs eventArgs)' `
    $onIdle `
    'Dynamic sewer Idle notification-only guard'

$attach = @'
            try
            {
                if (ReferenceEquals(_database, database)) return;
                DetachDatabase();
                _database = database;
                if (_database == null) return;
                _database.ObjectModified += OnObjectChanged;
                _database.ObjectAppended += OnObjectChanged;
                _database.ObjectErased += OnObjectErased;
            }
            catch
            {
                _database = null;
            }
'@
$dynamic = ReplaceMethodBody $dynamic `
    '        private static void AttachDatabase(Database database)' `
    $attach `
    'Dynamic sewer database attach exception shield'

$detach = @'
            Database database = _database;
            _database = null;
            if (database == null) return;
            try { database.ObjectModified -= OnObjectChanged; } catch { }
            try { database.ObjectAppended -= OnObjectChanged; } catch { }
            try { database.ObjectErased -= OnObjectErased; } catch { }
'@
$dynamic = ReplaceMethodBody $dynamic `
    '        private static void DetachDatabase()' `
    $detach `
    'Dynamic sewer database detach exception shield'

$erased = @'
            try
            {
                if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
                DBObject value = eventArgs.DBObject;
                if (value is CivilPipe || value is CivilStructure || value is CivilNetwork)
                    Queue();
            }
            catch { }
'@
$dynamic = ReplaceMethodBody $dynamic `
    '        private static void OnObjectErased(' `
    $erased `
    'Dynamic sewer erased callback exception shield'

$changed = @'
            try
            {
                if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
                DBObject value = eventArgs.DBObject;
                if (value is CivilPipe || value is CivilStructure || value is CivilNetwork)
                    Queue();
            }
            catch { }
'@
$dynamic = ReplaceMethodBody $dynamic `
    '        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)' `
    $changed `
    'Dynamic sewer changed callback exception shield'

# Defer the expensive label/style/alignment/linked refresh chain from the actual
# resequence transaction. The universal refresh manager executes it from its own
# safe command/idle boundary instead of recursively inside Civil network writes.
$oldPost = @'
                foreach (ObjectId networkId in refreshedNetworks)
                {
                    SewerNetworkLabelCommands.EnsureLabels(
                        document,
                        new[] { networkId });
                }
                if (refreshedNetworks.Count > 0)
                {
                    SewerLabelStyleSyncCommands.ApplySelectedStyles(document);
                    RefreshGeneratedAlignments(
                        document,
                        civilDocument,
                        refreshedNetworks,
                        result);
                    try
                    {
                        LinkedRefreshEngine.Refresh(document, false);
                    }
                    catch
                    {
                        result.RefreshFailures++;
                    }
                }
'@ -replace "`r?`n","`r`n"
$newPost = @'
                if (refreshedNetworks.Count > 0)
                {
                    try
                    {
                        document.SendStringToExecute(
                            "CE_SEWLABELS ",
                            true,
                            false,
                            false);
                    }
                    catch { result.RefreshFailures++; }
                    try { UniversalDynamicRefreshManager.Queue(); }
                    catch { result.RefreshFailures++; }
                }
'@ -replace "`r?`n","`r`n"
if ($dynamic.Contains($oldPost)) {
    $dynamic = $dynamic.Replace($oldPost,$newPost)
}
elseif (-not $dynamic.Contains('document.SendStringToExecute(') -or
        -not $dynamic.Contains('"CE_SEWLABELS "')) {
    throw 'August 21 dynamic sewer post-resequence deferral anchor missing.'
}
WriteText $dynamicPath $dynamic

# 5. Geometry-first cadastral input validation. Reject bulged, zero-area and
# self-intersecting erf boundaries before road-reserve/midblock pairing. This keeps
# malformed cadastral geometry away from all later offset/pair calculations.
$geometry = ReadText $geometryPath
$pointAnchor = @'
                    Point2d point;
                    try { point = polyline.GetPoint2dAt(i); }
                    catch { valid = false; break; }
                    if (!Finite(point)) { valid = false; break; }
'@ -replace "`r?`n","`r`n"
$pointSafe = @'
                    Point2d point;
                    try
                    {
                        point = polyline.GetPoint2dAt(i);
                        double bulge = polyline.GetBulgeAt(i);
                        if (double.IsNaN(bulge) || double.IsInfinity(bulge) || Math.Abs(bulge) > Tol)
                        {
                            valid = false;
                            break;
                        }
                    }
                    catch { valid = false; break; }
                    if (!Finite(point)) { valid = false; break; }
'@ -replace "`r?`n","`r`n"
if ($geometry.Contains($pointAnchor)) {
    $geometry = $geometry.Replace($pointAnchor,$pointSafe)
}
elseif (-not $geometry.Contains('Math.Abs(bulge) > Tol')) {
    throw 'August 21 cadastral bulge safety anchor missing.'
}
$geometry = $geometry.Replace(
    '                if (!valid || points.Count < 3) continue;',
    '                if (!valid || points.Count < 3 || !SafeCadastralPolygon(points)) continue;')

if (-not $geometry.Contains('private static bool SafeCadastralPolygon(')) {
    $insertMarker = '        private static List<EdgeLite> BuildEdges(List<ParcelLite> parcels, double minimumLength)'
    $insertAt = $geometry.IndexOf($insertMarker,[StringComparison]::Ordinal)
    if ($insertAt -lt 0) { throw 'August 21 safe cadastral polygon insertion marker missing.' }
    $helpers = @'
        private static bool SafeCadastralPolygon(IList<Point2d> points)
        {
            if (points == null || points.Count < 3 || points.Any(point => !Finite(point)))
                return false;
            double twiceArea = 0.0;
            for (int index = 0; index < points.Count; index++)
            {
                Point2d a = points[index];
                Point2d b = points[(index + 1) % points.Count];
                if (Distance(a, b) <= Tol) return false;
                twiceArea += a.X * b.Y - b.X * a.Y;
            }
            if (Math.Abs(twiceArea) <= Tol) return false;

            for (int first = 0; first < points.Count; first++)
            {
                Point2d a1 = points[first];
                Point2d a2 = points[(first + 1) % points.Count];
                for (int second = first + 1; second < points.Count; second++)
                {
                    if (second == first || second == first + 1 ||
                        (first == 0 && second == points.Count - 1))
                        continue;
                    Point2d b1 = points[second];
                    Point2d b2 = points[(second + 1) % points.Count];
                    if (SegmentsCross(a1, a2, b1, b2)) return false;
                }
            }
            return true;
        }

        private static bool SegmentsCross(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double o1 = Cross(a, b, c);
            double o2 = Cross(a, b, d);
            double o3 = Cross(c, d, a);
            double o4 = Cross(c, d, b);
            return ((o1 > Tol && o2 < -Tol) || (o1 < -Tol && o2 > Tol)) &&
                   ((o3 > Tol && o4 < -Tol) || (o3 < -Tol && o4 > Tol));
        }

        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) -
                   (b.Y - a.Y) * (c.X - a.X);
        }

'@ -replace "`r?`n","`r`n"
    $geometry = $geometry.Insert($insertAt,$helpers)
}
WriteText $geometryPath $geometry

# 6. Re-run the idempotent menu normalizers so the visible Production pages cannot
# accidentally expose the older native cadastral implementations after a later
# staged source repair.
foreach ($compat in @(
    'Repair-August21-RoadCentrelineMenuCompatibility-Civil3D2023.ps1',
    'Repair-August21-SewerLayoutMenuCompatibility-Civil3D2023.ps1')) {
    $path = Join-Path $root ('scripts\' + $compat)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 21 fatal-safety menu normalizer missing: $path"
    }
    & $path -RepoRoot $root
    $global:LASTEXITCODE = 0
}

# Final exact guards.
$featureCheck = ReadText $featurePath
$platformCheck = ReadText $platformSlopePath
$sequenceCheck = ReadText $sequencePath
$dynamicCheck = ReadText $dynamicPath
$geometryCheck = ReadText $geometryPath
$roadMenu = ReadText $roadCentrePath
$sewerMenu = ReadText $sewerCentrePath

if (-not $featureCheck.Contains('August21SafeFeatureLineCreation.RunCreateFromObjects(document);')) {
    throw 'Fatal-safety guard: CE_FLCREATE is not delegated to protected-clone creation.'
}
if (-not $platformCheck.Contains('August21SafeFeatureLineCreation.RunPlatformFeatureLinesAtSlope(document);')) {
    throw 'Fatal-safety guard: Platform slope feature lines are not delegated to sanitized creation.'
}
if ($sequenceCheck.Contains('SewerNetworkLabelCommands.EnsureLabels(')) {
    throw 'Fatal-safety guard: synchronous label re-entry survived CE_SEWSEQ.'
}
$onIdleRange = MethodRange $dynamicCheck '        private static void OnIdle(object sender, EventArgs eventArgs)' 'final OnIdle guard'
$onIdleText = $dynamicCheck.Substring([int]$onIdleRange[0],[int]$onIdleRange[2]-[int]$onIdleRange[0]+1)
if ($onIdleText.Contains('ResequenceAll(')) {
    throw 'Fatal-safety guard: dynamic Sewer still rewrites Civil network directly from Application.Idle.'
}
if (-not $onIdleText.Contains('"CE_SEWAUTOSEQALL "')) {
    throw 'Fatal-safety guard: dynamic Sewer does not queue the modal resequence command.'
}
foreach ($marker in @('SafeCadastralPolygon(points)','Math.Abs(bulge) > Tol','CE_MIDBLOCKSEWERPRODUCTION','CE_SEWERROADRESERVE','CE_ROADRESERVECENTERLINESSAFE')) {
    if (-not $geometryCheck.Contains($marker)) {
        throw "Fatal-safety guard: geometry-first cadastral marker missing: $marker"
    }
}
if (-not [regex]::IsMatch($roadMenu,'A\(\s*"CE-Road Reserve Centrelines"\s*,\s*"CE_ROADCENTERLINEPOLY"')) {
    throw 'Fatal-safety guard: Road Production is not routed to geometry-first centreline polylines.'
}
foreach ($marker in @('"CE_SEWERLAYOUTMIDBLOCK"','"CE_SEWERLAYOUTROADRESERVE"')) {
    if (-not $sewerMenu.Contains($marker)) {
        throw "Fatal-safety guard: Sewer Layout safe route missing: $marker"
    }
}

Write-Host 'August 21 cross-discipline fatal-safety pass complete:' -ForegroundColor Green
Write-Host ' - CE_SEWSEQ labels are deferred and dynamic resequence no longer mutates Civil networks from Application.Idle.' -ForegroundColor Green
Write-Host ' - Road centrelines, Midblock sewer and Road Reserve sewer remain routed through geometry-first cadastral workflows.' -ForegroundColor Green
Write-Host ' - Cadastral polygons with arcs, zero area or self-intersections are rejected before pairing/offset work.' -ForegroundColor Green
Write-Host ' - CE_FLCREATE and Platform slope feature lines use isolated committed temporary geometry; user source objects are retained.' -ForegroundColor Green
