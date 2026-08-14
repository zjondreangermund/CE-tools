[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required source was not found: $path" }
    return [System.IO.File]::ReadAllText($path)
}
function Write-Text([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), $utf8)
}
function Replace-MethodBody {
    param([string]$Text,[string]$Signature,[string]$Body)
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method signature was not found: $Signature" }
    $open = $Text.IndexOf('{', $start)
    if ($open -lt 0) { throw "Opening brace was not found for: $Signature" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '{') { $depth++ }
        elseif ($Text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace was not found for: $Signature" }
    return $Text.Substring(0,$open) + "{`r`n" + $Body.TrimEnd() + "`r`n        }" + $Text.Substring($close+1)
}

# ---------------------------------------------------------------------------
# PROJECT: Survey Location must update the same Project Info source immediately.
# ---------------------------------------------------------------------------
$coordPath = Join-Path $src 'ProjectCoordinationCommands.cs'
$coord = Read-Text $coordPath
if (-not $coord.Contains('August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);')) {
    $anchor = 'civilDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode = code;'
    if (-not $coord.Contains($anchor)) { throw 'Survey Location coordinate-system assignment anchor was not found.' }
    $coord = $coord.Replace($anchor, $anchor + "`r`n" + '                August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);')
}
Write-Text $coordPath $coord
Write-Host 'Survey Location now writes Town and Coordinate System back to the shared Project Info source.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# SURVEY SURFACE UI: old command names route to the popup production workflows.
# ---------------------------------------------------------------------------
$surfaceCommandsPath = Join-Path $src 'SurfaceCommands.cs'
$surfaceCommands = Read-Text $surfaceCommandsPath
$surfaceReportBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            new August14SurveyFieldReviewCommands().SurfaceReportProduction();
'@
$surfaceCommands = Replace-MethodBody -Text $surfaceCommands -Signature 'public void SurfaceReport()' -Body $surfaceReportBody
Write-Text $surfaceCommandsPath $surfaceCommands

$surveySurfacePath = Join-Path $src 'August14SurveySurfaceCommands.cs'
$surveySurface = Read-Text $surveySurfacePath
$compareBody = @'
            Document document = Active();
            if (document == null) return;
            new August14SurveyFieldReviewCommands().SurfaceComparisonProduction();
'@
$surveySurface = Replace-MethodBody -Text $surveySurface -Signature 'public void SurfaceCompareTable()' -Body $compareBody

# Store the original CE input-point XY set on the surface. Later border rebuilds
# use this source set instead of a contour/TIN display extent.
if (-not $surveySurface.Contains('SourcePointRecordName')) {
    $classAnchor = 'public sealed class August14SurveySurfaceCommands' + "`r`n" + '    {'
    if (-not $surveySurface.Contains($classAnchor)) { throw 'Survey Surface class anchor was not found.' }
    $surveySurface = $surveySurface.Replace($classAnchor, $classAnchor + "`r`n" + '        private const string SourcePointRecordName = "CE_SURFACE_SOURCE_POINTS_V1";')
}
if (-not $surveySurface.Contains('WriteSourcePointRecord(surface, transaction, points);')) {
    $anchor = '                    surface.AddVertices(points);'
    if (-not $surveySurface.Contains($anchor)) { throw 'Surface AddVertices anchor was not found.' }
    $surveySurface = $surveySurface.Replace($anchor, $anchor + "`r`n" + '                    WriteSourcePointRecord(surface, transaction, points);')
}

$oldPoints = @'
                    var points = new List<Point2d>();
                    foreach (TinSurfaceVertex vertex in surface.Vertices)
                    {
                        if (vertex == null || !vertex.IsValid) continue;
                        Point3d point = vertex.Location;
                        points.Add(new Point2d(point.X, point.Y));
                    }
'@
$newPoints = @'
                    List<Point2d> points = ReadSourcePointRecord(surface, transaction);
                    if (points.Count < 3)
                    {
                        points = new List<Point2d>();
                        foreach (TinSurfaceVertex vertex in surface.Vertices)
                        {
                            if (vertex == null || !vertex.IsValid) continue;
                            Point3d point = vertex.Location;
                            points.Add(new Point2d(point.X, point.Y));
                        }
                    }
'@
if ($surveySurface.Contains(($oldPoints -replace "`n","`r`n"))) {
    $surveySurface = $surveySurface.Replace(($oldPoints -replace "`n","`r`n"), ($newPoints -replace "`n","`r`n"))
}
elseif ($surveySurface.Contains($oldPoints)) {
    $surveySurface = $surveySurface.Replace($oldPoints, $newPoints)
}
elseif (-not $surveySurface.Contains('ReadSourcePointRecord(surface, transaction)')) {
    throw 'Automatic surface-border source-point block could not be upgraded.'
}

if (-not $surveySurface.Contains('private static void WriteSourcePointRecord(')) {
    $helperAnchor = '        private static ObjectId CreateBorderPolyline('
    $index = $surveySurface.IndexOf($helperAnchor, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'Surface border helper insertion anchor was not found.' }
    $helpers = @'
        private static void WriteSourcePointRecord(
            TinSurface surface,
            Transaction transaction,
            IEnumerable<Point3d> points)
        {
            if (surface == null || transaction == null || points == null) return;
            if (surface.ExtensionDictionary.IsNull) surface.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(surface.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(SourcePointRecordName))
                record = transaction.GetObject(dictionary.GetAt(SourcePointRecordName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(SourcePointRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record == null) return;
            var values = new List<TypedValue>();
            foreach (Point3d point in points)
                values.Add(new TypedValue((int)DxfCode.Text,
                    point.X.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    point.Y.ToString("R", CultureInfo.InvariantCulture)));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static List<Point2d> ReadSourcePointRecord(
            TinSurface surface,
            Transaction transaction)
        {
            var result = new List<Point2d>();
            if (surface == null || transaction == null || surface.ExtensionDictionary.IsNull) return result;
            try
            {
                DBDictionary dictionary = transaction.GetObject(surface.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(SourcePointRecordName)) return result;
                Xrecord record = transaction.GetObject(dictionary.GetAt(SourcePointRecordName), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return result;
                foreach (TypedValue item in record.Data)
                {
                    string text = item.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    string[] parts = text.Split('|');
                    double x;
                    double y;
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        result.Add(new Point2d(x, y));
                }
            }
            catch { }
            return result;
        }

'@
    $surveySurface = $surveySurface.Insert($index, ($helpers -replace "`r?`n","`r`n"))
}
Write-Text $surveySurfacePath $surveySurface
Write-Host 'Surface report/comparison commands now use popups; CE surfaces retain original source-point XY for border rebuilds.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# GRID / VERTEX SETTING-OUT: dedicated front door, true displayed XY swap, and
# absolute COGO label restoration on both manual and automatic refresh.
# ---------------------------------------------------------------------------
$vertexPath = Join-Path $src 'VertexSettingOutCommands.cs'
$vertex = Read-Text $vertexPath
if (-not $vertex.Contains('private static bool _gridSettingOutMode;')) {
    $anchor = '        private const string SchemaVersion = "2";'
    if (-not $vertex.Contains($anchor)) { throw 'Vertex setting-out schema anchor was not found.' }
    $vertex = $vertex.Replace($anchor, $anchor + "`r`n" + '        private static bool _gridSettingOutMode;')
}
if (-not $vertex.Contains('"CE_GRIDSETTINGOUTCREATE"')) {
    $anchor = '        [CommandMethod(' + "`r`n" + '            "CE_TOOLS",' + "`r`n" + '            "CE_VERTEXSETTINGOUT",'
    $index = $vertex.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'CE_VERTEXSETTINGOUT command declaration anchor was not found.' }
    $method = @'
        [CommandMethod("CE_TOOLS", "CE_GRIDSETTINGOUTCREATE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateGridSettingOut()
        {
            _gridSettingOutMode = true;
            try { Create(); }
            finally { _gridSettingOutMode = false; }
        }

'@
    $vertex = $vertex.Insert($index, ($method -replace "`r?`n","`r`n"))
}

$oldTitle = '                "CE Tools - Vertex Setting-Out Settings",'
$newTitle = '                _gridSettingOutMode ? "CE Tools - Grid Setting-Out Settings" : "CE Tools - Vertex Setting-Out Settings",'
if ($vertex.Contains($oldTitle)) { $vertex = $vertex.Replace($oldTitle, $newTitle) }
$oldNote = '                "All vertices are included. Arcs longer than 10 m receive a midpoint; every arc receives a centre point and radius dimension. Tangents longer than 20 m receive a midpoint, and tangents longer than 40 m receive three equally spaced points.");'
$newNote = '                _gridSettingOutMode ? "Select multiple polylines/feature lines. Use one continuous numbering sequence and a linked annotative table; source geometry remains linked for automatic refresh." : "All vertices are included. Arcs longer than 10 m receive a midpoint; every arc receives a centre point and radius dimension. Tangents longer than 20 m receive a midpoint, and tangents longer than 40 m receive three equally spaced points.");'
if ($vertex.Contains($oldNote)) { $vertex = $vertex.Replace($oldNote, $newNote) }

# The display order must swap values as well as headings. Geometry is untouched.
$oldTableCoordinates = @'
                // Keep the numeric coordinate columns fixed and swap only their
                // displayed X/Y headings when requested. Drawing coordinates never change.
                table.Cells[row, 4].TextString = displayX
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = displayY
                    .ToString("N3", CultureInfo.CurrentCulture);
'@
$newTableCoordinates = @'
                double firstCoordinate = yFirst ? displayY : displayX;
                double secondCoordinate = yFirst ? displayX : displayY;
                table.Cells[row, 4].TextString = firstCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = secondCoordinate
                    .ToString("N3", CultureInfo.CurrentCulture);
'@
if ($vertex.Contains(($oldTableCoordinates -replace "`n","`r`n"))) {
    $vertex = $vertex.Replace(($oldTableCoordinates -replace "`n","`r`n"), ($newTableCoordinates -replace "`n","`r`n"))
}
elseif (-not $vertex.Contains('double firstCoordinate = yFirst ? displayY : displayX;')) {
    throw 'Vertex table coordinate-display block could not be upgraded.'
}

$oldLabel = @'
            string first = (yFirst ? "Y=" : "X=") +
                displayX.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                displayY.ToString("N3", CultureInfo.CurrentCulture);
'@
$newLabel = @'
            double firstCoordinate = yFirst ? displayY : displayX;
            double secondCoordinate = yFirst ? displayX : displayY;
            string first = (yFirst ? "Y=" : "X=") +
                firstCoordinate.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                secondCoordinate.ToString("N3", CultureInfo.CurrentCulture);
'@
if ($vertex.Contains(($oldLabel -replace "`n","`r`n"))) {
    $vertex = $vertex.Replace(($oldLabel -replace "`n","`r`n"), ($newLabel -replace "`n","`r`n"))
}
elseif (-not $vertex.Contains('double firstCoordinate = yFirst ? displayY : displayX;')) {
    throw 'Vertex annotation coordinate-display block could not be upgraded.'
}

# Manual refresh: capture once before work and restore absolutely after styles.
if (-not $vertex.Contains('CE_VERTEXSETTINGOUTREFRESH label offset capture')) {
    $anchor = '            try' + "`r`n" + '            {' + "`r`n" + '                int points;' + "`r`n" + '                int dimensions;' + "`r`n" + '                RefreshTable(document, selected.ObjectId, out points, out dimensions);'
    if ($vertex.Contains($anchor)) {
        $replacement = '            try' + "`r`n" + '            {' + "`r`n" + '                // CE_VERTEXSETTINGOUTREFRESH label offset capture' + "`r`n" + '                August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);' + "`r`n" + '                int points;' + "`r`n" + '                int dimensions;' + "`r`n" + '                RefreshTable(document, selected.ObjectId, out points, out dimensions);'
        $vertex = $vertex.Replace($anchor, $replacement)
    }
}
$refreshClamp = '                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);' + "`r`n" + '                document.Editor.Regen();'
if ($vertex.Contains($refreshClamp) -and -not $vertex.Contains('CE_VERTEXSETTINGOUTREFRESH absolute label restore')) {
    $replacement = '                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);' + "`r`n" + '                // CE_VERTEXSETTINGOUTREFRESH absolute label restore' + "`r`n" + '                August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);' + "`r`n" + '                document.Editor.Regen();'
    # Replace the last occurrence, which belongs to RefreshSelected, without
    # disturbing the create/continue branches if earlier copies exist.
    $last = $vertex.LastIndexOf($refreshClamp, [StringComparison]::Ordinal)
    if ($last -ge 0) { $vertex = $vertex.Substring(0,$last) + $replacement + $vertex.Substring($last + $refreshClamp.Length) }
}

# Automatic refresh of every linked table gets the same capture/restore envelope.
$allStart = '        internal static int RefreshAll(Document document)' + "`r`n" + '        {' + "`r`n" + '            if (document == null) return 0;'
if ($vertex.Contains($allStart) -and -not $vertex.Contains('CE_VERTEXSETTINGOUT RefreshAll label capture')) {
    $vertex = $vertex.Replace($allStart, $allStart + "`r`n" + '            // CE_VERTEXSETTINGOUT RefreshAll label capture' + "`r`n" + '            August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')
}
$allEnd = '            return refreshed;' + "`r`n" + '        }'
$allStartIndex = $vertex.IndexOf('internal static int RefreshAll(Document document)', [StringComparison]::Ordinal)
if ($allStartIndex -ge 0 -and -not $vertex.Contains('CE_VERTEXSETTINGOUT RefreshAll absolute label restore')) {
    $returnIndex = $vertex.IndexOf($allEnd, $allStartIndex, [StringComparison]::Ordinal)
    if ($returnIndex -ge 0) {
        $replacement = '            // CE_VERTEXSETTINGOUT RefreshAll absolute label restore' + "`r`n" + '            August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);' + "`r`n" + $allEnd
        $vertex = $vertex.Substring(0,$returnIndex) + $replacement + $vertex.Substring($returnIndex + $allEnd.Length)
    }
}
Write-Text $vertexPath $vertex
Write-Host 'Grid/Vertex setting-out now has a dedicated front door, true displayed XY swap and absolute COGO label restore.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# SURFACE COMPARISON TABLE: sample the current moved linked DBPoint XY, not the
# originally typed XY, and never derive text size from the title cell.
# ---------------------------------------------------------------------------
$fieldPath = Join-Path $src 'August14SurveyFieldReviewCommands.cs'
$field = Read-Text $fieldPath
$field = $field.Replace('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT",', 'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTCREATE",')

$oldLoop = @'
                        foreach (SurfaceComparisonPoint item in stored)
                        {
                            try { refreshed.Add(new SurfaceComparisonPoint(item.Name, item.X, item.Y, baseSurface.FindElevationAtXY(item.X, item.Y), comparisonSurface.FindElevationAtXY(item.X, item.Y))); }
                            catch { }
                        }
'@
$newLoop = @'
                        foreach (SurfaceComparisonPoint item in stored)
                        {
                            try
                            {
                                double sampleX = item.X;
                                double sampleY = item.Y;
                                Point3d liveAnchor;
                                if (SurfaceComparisonLinkStore.TryResolveLiveAnchor(document.Database, baseId, comparisonId, item.X, item.Y, out liveAnchor))
                                {
                                    sampleX = liveAnchor.X;
                                    sampleY = liveAnchor.Y;
                                }
                                refreshed.Add(new SurfaceComparisonPoint(item.Name, sampleX, sampleY, baseSurface.FindElevationAtXY(sampleX, sampleY), comparisonSurface.FindElevationAtXY(sampleX, sampleY)));
                            }
                            catch { }
                        }
'@
if ($field.Contains(($oldLoop -replace "`n","`r`n"))) {
    $field = $field.Replace(($oldLoop -replace "`n","`r`n"), ($newLoop -replace "`n","`r`n"))
}
elseif (-not $field.Contains('SurfaceComparisonLinkStore.TryResolveLiveAnchor')) {
    throw 'Multi-surface comparison refresh loop could not be upgraded to live point anchors.'
}
$field = $field.Replace('table.Rows.Count > 0 && table.Columns.Count > 0 ? Math.Max(table.Cells[0, 0].TextHeight ?? 2.5, 0.001) : 2.5;', 'table.Rows.Count > 1 && table.Columns.Count > 0 ? Math.Max(table.Cells[1, 0].TextHeight ?? 2.5, 0.001) : 2.5;')
Write-Text $fieldPath $field
Write-Host 'Surface comparison tables now follow moved linked points and use stable non-title text sizing.' -ForegroundColor Green

# Final markers.
$checks = @(
    @{ Path=$coordPath; Marker='SyncProjectLocation(document, town, code)' },
    @{ Path=$surfaceCommandsPath; Marker='new August14SurveyFieldReviewCommands().SurfaceReportProduction()' },
    @{ Path=$surveySurfacePath; Marker='new August14SurveyFieldReviewCommands().SurfaceComparisonProduction()' },
    @{ Path=$surveySurfacePath; Marker='WriteSourcePointRecord(surface, transaction, points)' },
    @{ Path=$vertexPath; Marker='CE_GRIDSETTINGOUTCREATE' },
    @{ Path=$vertexPath; Marker='firstCoordinate = yFirst ? displayY : displayX' },
    @{ Path=$fieldPath; Marker='SurfaceComparisonLinkStore.TryResolveLiveAnchor' }
)
foreach ($check in $checks) {
    $value = Read-Text $check.Path
    if (-not $value.Contains([string]$check.Marker)) { throw "Project/Survey runtime field-test repair verification failed: $($check.Marker)" }
}

Write-Host 'August 14 Project + Survey runtime field-test integration passed.' -ForegroundColor Cyan
