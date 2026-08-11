[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August11 audit-repair source missing: $path" }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

# -----------------------------------------------------------------------------
# 1. Multi-surface coordinate tables: refresh every model/paper layout space,
#    not only whichever tab happens to be current when universal refresh runs.
# -----------------------------------------------------------------------------
$survey = Need 'August11SurveyRuntimeCommands.cs'
$text = ReadText $survey
if (-not $text.Contains('if (space == null || !space.IsLayout) continue;')) {
    $pattern = '(?s)        internal static int RefreshMultiSurfaceTables\(Document document\)\s*        \{.*?\n        \}\s*(?=\n        private static Table BuildMultiSurfaceTable)'
    $replacement = @'
        internal static int RefreshMultiSurfaceTables(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                    if (blockTable == null) return 0;
                    foreach (ObjectId spaceId in blockTable)
                    {
                        BlockTableRecord space;
                        try { space = transaction.GetObject(spaceId, OpenMode.ForRead, false) as BlockTableRecord; }
                        catch { continue; }
                        if (space == null || !space.IsLayout) continue;
                        foreach (ObjectId id in space)
                        {
                            Table table;
                            try { table = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                            catch { continue; }
                            if (table == null) continue;
                            List<ObjectId> points;
                            List<ObjectId> surfaces;
                            if (!TryReadMultiSurfaceLink(table, transaction, out points, out surfaces)) continue;
                            UpdateMultiSurfaceTable(document.Database, transaction, table, points, surfaces);
                            refreshed++;
                        }
                    }
                    transaction.Commit();
                }
            }
            catch { }
            return refreshed;
        }
'@
    $regex = [regex]::new($pattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($text)) { throw 'Could not isolate RefreshMultiSurfaceTables for all-layout refresh repair.' }
    $text = $regex.Replace($text,$replacement.TrimEnd("`r","`n"),1)
    WriteText $survey $text
    Write-Host 'Repaired multi-surface table refresh across all model/paper layout spaces.' -ForegroundColor Green
}
else { Write-Host 'Multi-surface table all-layout refresh is already repaired.' -ForegroundColor DarkGreen }

# -----------------------------------------------------------------------------
# 2. ROAD-n name synchronization: match the CE road-production source handle to
#    the road-name label's CE_ROAD_LAYOUT parent handle first. Nearest-label
#    matching remains only as a compatibility fallback for old untagged drawings.
# -----------------------------------------------------------------------------
$roadNames = Need 'August11RoadNamingCurveCommands.cs'
$text = ReadText $roadNames
$oldMatch = @'
                        RoadLabel nearest = labels.OrderBy(label => PlanDistanceSquared(centre, label.Position)).First();
                        string oldName = ReadName(entity);
                        if (string.IsNullOrWhiteSpace(nearest.Name)) continue;
                        if (!string.Equals(oldName, nearest.Name, StringComparison.OrdinalIgnoreCase) && TryWriteName(entity, nearest.Name))
                        {
                            if (!string.IsNullOrWhiteSpace(oldName)) renames[oldName] = nearest.Name;
                            changed++;
                        }
                        if (createLinks) WriteNameLink(entity, transaction, nearest.SourceHandle, nearest.Name);
'@
$newMatch = @'
                        string productionSource = ReadRoadProductionSource(entity);
                        RoadLabel matched = string.IsNullOrWhiteSpace(productionSource)
                            ? null
                            : labels.FirstOrDefault(label =>
                                string.Equals(label.SourceHandle, productionSource, StringComparison.OrdinalIgnoreCase));
                        if (matched == null)
                            matched = labels.OrderBy(label => PlanDistanceSquared(centre, label.Position)).First();
                        string oldName = ReadName(entity);
                        if (string.IsNullOrWhiteSpace(matched.Name)) continue;
                        if (!string.Equals(oldName, matched.Name, StringComparison.OrdinalIgnoreCase) && TryWriteName(entity, matched.Name))
                        {
                            if (!string.IsNullOrWhiteSpace(oldName)) renames[oldName] = matched.Name;
                            changed++;
                        }
                        if (createLinks) WriteNameLink(entity, transaction, matched.SourceHandle, matched.Name);
'@
if (-not $text.Contains('string productionSource = ReadRoadProductionSource(entity);')) {
    $oldValue = $oldMatch.TrimEnd("`r","`n")
    if (-not $text.Contains($oldValue)) { throw 'Road-name nearest-label match block was not found.' }
    $text = $text.Replace($oldValue,$newMatch.TrimEnd("`r","`n"))
}

$oldLabelAdd = '                if (!string.IsNullOrWhiteSpace(name)) result.Add(new RoadLabel(name, position, id.Handle.ToString()));'
$newLabelAdd = @'
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string sourceHandle = ReadRoadLayoutParent(entity, transaction);
                    if (string.IsNullOrWhiteSpace(sourceHandle)) sourceHandle = id.Handle.ToString();
                    result.Add(new RoadLabel(name, position, sourceHandle));
                }
'@
if (-not $text.Contains('string sourceHandle = ReadRoadLayoutParent(entity, transaction);')) {
    if (-not $text.Contains($oldLabelAdd)) { throw 'Road-label source-handle creation marker was not found.' }
    $text = $text.Replace($oldLabelAdd,$newLabelAdd.TrimEnd("`r","`n"))
}

if (-not $text.Contains('private static string ReadRoadProductionSource(DBObject entity)')) {
    $helperAnchor = '        private static string ReadName(object target)'
    if (-not $text.Contains($helperAnchor)) { throw 'Road-name helper insertion marker was not found.' }
    $helpers = @'
        private static string ReadRoadProductionSource(DBObject entity)
        {
            if (entity == null) return string.Empty;
            try
            {
                ResultBuffer data = entity.GetXDataForApplication("CE_ROAD_PRODUCTION");
                if (data == null) return string.Empty;
                foreach (TypedValue value in data)
                {
                    string text = value.Value as string;
                    if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                        return text.Substring(7).Trim();
                }
            }
            catch { }
            return string.Empty;
        }

        private static string ReadRoadLayoutParent(Entity entity, Transaction transaction)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return string.Empty;
            try
            {
                DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains("CE_ROAD_LAYOUT")) return string.Empty;
                Xrecord record = transaction.GetObject(dictionary.GetAt("CE_ROAD_LAYOUT"), OpenMode.ForRead, false) as Xrecord;
                TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
                return values != null && values.Length > 1
                    ? Convert.ToString(values[1].Value, CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

'@
    $text = $text.Replace($helperAnchor,$helpers + $helperAnchor)
}
WriteText $roadNames $text
Write-Host 'Repaired ROAD-n synchronization to use CE source handles before spatial fallback.' -ForegroundColor Green

# -----------------------------------------------------------------------------
# 3. Midblock route automatic direction: decide by parcel-centre/overall row
#    spread rather than summing individual lot widths/heights. Square lots in a
#    long row otherwise produce an ambiguous or wrong automatic orientation.
# -----------------------------------------------------------------------------
$midblock = Need 'August11MidblockSewerProductionCommands.cs'
$text = ReadText $midblock
$oldOrientation = @'
        private static bool ResolveHorizontal(IList<ParcelBox> parcels, string selection)
        {
            if (string.Equals(selection, "Horizontal", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(selection, "Vertical", StringComparison.OrdinalIgnoreCase)) return false;
            double totalWidth = parcels.Sum(item => item.Width);
            double totalHeight = parcels.Sum(item => item.Height);
            return totalWidth >= totalHeight;
        }
'@
$newOrientation = @'
        private static bool ResolveHorizontal(IList<ParcelBox> parcels, string selection)
        {
            if (string.Equals(selection, "Horizontal", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(selection, "Vertical", StringComparison.OrdinalIgnoreCase)) return false;
            if (parcels == null || parcels.Count == 0) return true;

            double centreSpanX = parcels.Max(item => item.Center.X) - parcels.Min(item => item.Center.X);
            double centreSpanY = parcels.Max(item => item.Center.Y) - parcels.Min(item => item.Center.Y);
            double overallWidth = parcels.Max(item => item.Extents.MaxPoint.X) - parcels.Min(item => item.Extents.MinPoint.X);
            double overallHeight = parcels.Max(item => item.Extents.MaxPoint.Y) - parcels.Min(item => item.Extents.MinPoint.Y);
            double tolerance = Math.Max(Math.Max(overallWidth, overallHeight) * 0.02, 0.01);
            if (Math.Abs(centreSpanX - centreSpanY) > tolerance) return centreSpanX >= centreSpanY;
            return overallWidth >= overallHeight;
        }
'@
if (-not $text.Contains('double centreSpanX = parcels.Max(item => item.Center.X)')) {
    $oldValue = $oldOrientation.TrimEnd("`r","`n")
    if (-not $text.Contains($oldValue)) { throw 'Midblock automatic-orientation method marker was not found.' }
    $text = $text.Replace($oldValue,$newOrientation.TrimEnd("`r","`n"))
    WriteText $midblock $text
    Write-Host 'Repaired Midblock automatic row orientation from parcel-centre/overall spread.' -ForegroundColor Green
}
else { Write-Host 'Midblock automatic orientation is already spread-based.' -ForegroundColor DarkGreen }

# -----------------------------------------------------------------------------
# 4. Network batch source markers: lock and write against the exact Document that
#    launched the queue. Using MdiActiveDocument inside an asynchronous command
#    end handler can silently mark the wrong/no drawing when users switch tabs.
# -----------------------------------------------------------------------------
$network = Need 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$text = $text.Replace('NetworkSourceMarker.Clear(document.Database, selected.Value.GetObjectIds())','NetworkSourceMarker.Clear(document, selected.Value.GetObjectIds())')
$text = $text.Replace('NetworkSourceMarker.Mark(_document.Database, _current, _discipline);','NetworkSourceMarker.Mark(_document, _current, _discipline);')

$oldMarkHeader = @'
        internal static void Mark(Database database, ObjectId id, string discipline)
        {
            if (database == null || id.IsNull || id.IsErased) return;
            try
            {
                using (DocumentLock documentLock = AcApplication.DocumentManager.MdiActiveDocument == null ? null : AcApplication.DocumentManager.MdiActiveDocument.LockDocument())
'@
$newMarkHeader = @'
        internal static void Mark(Document document, ObjectId id, string discipline)
        {
            if (document == null || id.IsNull || id.IsErased) return;
            Database database = document.Database;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
'@
if ($text.Contains($oldMarkHeader.TrimEnd("`r","`n"))) {
    $text = $text.Replace($oldMarkHeader.TrimEnd("`r","`n"),$newMarkHeader.TrimEnd("`r","`n"))
}
elseif (-not $text.Contains('internal static void Mark(Document document, ObjectId id, string discipline)')) {
    throw 'Network source-marker Mark method was not found for exact-document repair.'
}

$oldClearHeader = @'
        internal static int Clear(Database database, IEnumerable<ObjectId> ids)
        {
            if (database == null) return 0;
            int count = 0;
            using (DocumentLock documentLock = AcApplication.DocumentManager.MdiActiveDocument == null ? null : AcApplication.DocumentManager.MdiActiveDocument.LockDocument())
'@
$newClearHeader = @'
        internal static int Clear(Document document, IEnumerable<ObjectId> ids)
        {
            if (document == null) return 0;
            Database database = document.Database;
            int count = 0;
            using (DocumentLock documentLock = document.LockDocument())
'@
if ($text.Contains($oldClearHeader.TrimEnd("`r","`n"))) {
    $text = $text.Replace($oldClearHeader.TrimEnd("`r","`n"),$newClearHeader.TrimEnd("`r","`n"))
}
elseif (-not $text.Contains('internal static int Clear(Document document, IEnumerable<ObjectId> ids)')) {
    throw 'Network source-marker Clear method was not found for exact-document repair.'
}
WriteText $network $text
Write-Host 'Repaired network batch completion markers to use the exact launching Document.' -ForegroundColor Green

# -----------------------------------------------------------------------------
# 5. Assembly-facing commands must all use the same Civil 3D 2023/2024 resolver.
#    The visibility-marker command previously bypassed the robust fallback chain.
# -----------------------------------------------------------------------------
$behavior = Need 'AugustBehaviorCompletionCommands.cs'
$text = ReadText $behavior
if (-not $text.Contains('CivilAssemblyResolver.GetAssemblyIds(civil, document.Database)')) {
    $pattern = '(?s)        internal static int EnsureAllMarkers\(Document document, CivilDocument civil\)\s*        \{.*?\n        \}\s*(?=\n        private static bool MarkerExists)'
    $replacement = @'
        internal static int EnsureAllMarkers(Document document, CivilDocument civil)
        {
            if (document == null || civil == null) return 0;
            IList<ObjectId> ids = CivilAssemblyResolver.GetAssemblyIds(civil, document.Database);
            int count = 0;
            foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased))
            {
                Point3d point = Point3d.Origin;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBObject assembly;
                    try { assembly = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    point = ReadPoint(assembly, "Location", "InsertionPoint", "Origin");
                }
                EnsureMarker(document, id, point);
                count++;
            }
            return count;
        }
'@
    $regex = [regex]::new($pattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($text)) { throw 'Assembly visibility-marker discovery method could not be isolated.' }
    $text = $regex.Replace($text,$replacement.TrimEnd("`r","`n"),1)
    WriteText $behavior $text
    Write-Host 'Unified assembly visibility markers with CivilAssemblyResolver.' -ForegroundColor Green
}
else { Write-Host 'Assembly visibility markers already use CivilAssemblyResolver.' -ForegroundColor DarkGreen }

Write-Host 'August 11 behavioral audit repairs are staged.' -ForegroundColor Cyan
