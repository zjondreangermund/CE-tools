[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Text([string]$Path) {
    return [System.IO.File]::ReadAllText($Path)
}

function Write-Text([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Replace-Once([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $count = ([regex]::Matches($Text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "$Label expected exactly one anchor but found $count."
    }
    return $Text.Replace($Old, $New)
}

function Replace-CSharpMethod([string]$Text, [string]$Signature, [string]$Replacement, [string]$Label) {
    $signatureIndex = $Text.IndexOf($Signature, [System.StringComparison]::Ordinal)
    if ($signatureIndex -lt 0) {
        throw "$Label signature was not found: $Signature"
    }
    $second = $Text.IndexOf($Signature, $signatureIndex + $Signature.Length, [System.StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "$Label signature is ambiguous; more than one match was found."
    }
    $braceStart = $Text.IndexOf('{', $signatureIndex + $Signature.Length)
    if ($braceStart -lt 0) {
        throw "$Label opening brace was not found."
    }
    $depth = 0
    $methodEnd = -1
    for ($i = $braceStart; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) {
                $methodEnd = $i
                break
            }
        }
    }
    if ($methodEnd -lt 0) {
        throw "$Label closing brace was not found."
    }
    return $Text.Substring(0, $signatureIndex) + $Replacement + $Text.Substring($methodEnd + 1)
}

$midblockPath = Join-Path $RepoRoot 'src\CE.Tools.Civil3D\August11MidblockSewerProductionCommands.cs'
$roadPath = Join-Path $RepoRoot 'src\CE.Tools.Civil3D\August19RoadReserveSewerAndSafetyCommands.cs'
$geometryPath = Join-Path $RepoRoot 'src\CE.Tools.Civil3D\August20GeometryFirstSewerCommands.cs'
$siteGridPath = Join-Path $RepoRoot 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'

foreach ($path in @($midblockPath, $roadPath, $geometryPath, $siteGridPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 20 geometry-first prerequisite missing: $path"
    }
}

Write-Host 'Applying geometry-first Sewer/Road Centreline command bridges...' -ForegroundColor Cyan

$midblock = Read-Text $midblockPath
$midblock = Replace-Once $midblock '"CE_MIDBLOCKSEWERPRODUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_MIDBLOCKSEWERPRODUCTIONLEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Midblock legacy command bridge'
Write-Text $midblockPath $midblock

$road = Read-Text $roadPath
$road = Replace-Once $road '"CE_SEWERROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_SEWERROADRESERVELEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Road Reserve sewer legacy command bridge'
$road = Replace-Once $road '"CE_ROADRESERVECENTERLINESSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_ROADRESERVECENTERLINESSAFELEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Road Reserve centreline legacy command bridge'
Write-Text $roadPath $road

$geometry = Read-Text $geometryPath
$geometry = Replace-Once $geometry '"CE_AUG20MIDBLOCKBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_MIDBLOCKSEWERPRODUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Geometry-first Midblock public bridge'
$geometry = Replace-Once $geometry '"CE_AUG20ROADRESERVEBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_SEWERROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Geometry-first Road Reserve sewer public bridge'
$geometry = Replace-Once $geometry '"CE_AUG20ROADCENTERBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' '"CE_ROADRESERVECENTERLINESSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' 'Geometry-first Road Reserve centreline public bridge'
Write-Text $geometryPath $geometry

$midblockCheck = Read-Text $midblockPath
$roadCheck = Read-Text $roadPath
$geometryCheck = Read-Text $geometryPath
foreach ($token in @('CE_AUG20MIDBLOCKBRIDGE','CE_AUG20ROADRESERVEBRIDGE','CE_AUG20ROADCENTERBRIDGE')) {
    if ($geometryCheck.Contains($token)) {
        throw "August 20 geometry-first bridge token survived staging: $token"
    }
}
foreach ($token in @('CE_MIDBLOCKSEWERPRODUCTIONLEGACY')) {
    if (-not $midblockCheck.Contains($token)) { throw "August 20 missing staged legacy token: $token" }
}
foreach ($token in @('CE_SEWERROADRESERVELEGACY','CE_ROADRESERVECENTERLINESSAFELEGACY')) {
    if (-not $roadCheck.Contains($token)) { throw "August 20 missing staged legacy token: $token" }
}
foreach ($token in @('CE_MIDBLOCKSEWERPRODUCTION','CE_SEWERROADRESERVE','CE_ROADRESERVECENTERLINESSAFE','CE_SEWERBUILDNETWORK','CE_CENTERLINETOALIGNMENT','CE_SEWERREFRESHLAYOUT')) {
    if (-not $geometryCheck.Contains($token)) { throw "August 20 geometry-first public/conversion command missing: $token" }
}

Write-Host 'Repairing Site Grid parent/child dynamic refresh targeting...' -ForegroundColor Cyan
$site = Read-Text $siteGridPath

$refreshSignature = '        internal static int RefreshAll(Document document, ISet<ObjectId> dirtyIds)'
$refreshMethod = @'
        internal static int RefreshAll(Document document, ISet<ObjectId> dirtyIds)
        {
            if (document == null) return 0;
            bool manualRefresh = dirtyIds == null;
            if (!manualRefresh && dirtyIds.Count == 0) return 0;

            int refreshed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = OpenModelSpace(
                    document.Database,
                    transaction,
                    OpenMode.ForRead);
                if (modelSpace == null) return 0;

                var dirtyParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!manualRefresh)
                {
                    foreach (ObjectId dirtyId in dirtyIds)
                    {
                        if (dirtyId.IsNull || dirtyId.IsErased) continue;
                        Entity dirtyEntity;
                        try
                        {
                            dirtyEntity = transaction.GetObject(
                                dirtyId,
                                OpenMode.ForRead,
                                false) as Entity;
                        }
                        catch
                        {
                            continue;
                        }
                        if (dirtyEntity == null || dirtyEntity.IsErased) continue;

                        Polyline dirtyBoundary = dirtyEntity as Polyline;
                        SiteGridSettings dirtySettings;
                        if (dirtyBoundary != null &&
                            TryReadParentLink(dirtyBoundary, transaction, out dirtySettings))
                        {
                            dirtyParents.Add(dirtyBoundary.Handle.ToString());
                            continue;
                        }

                        SiteGridChildLink dirtyLink;
                        if (TryReadChildLink(dirtyEntity, transaction, out dirtyLink) &&
                            !string.IsNullOrWhiteSpace(dirtyLink.ParentHandle))
                            dirtyParents.Add(dirtyLink.ParentHandle);
                    }
                    if (dirtyParents.Count == 0)
                        return 0;
                }

                List<ObjectId> ids = modelSpace.Cast<ObjectId>().ToList();
                foreach (ObjectId id in ids)
                {
                    Polyline boundary;
                    try
                    {
                        boundary = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Polyline;
                    }
                    catch
                    {
                        continue;
                    }
                    if (boundary == null || boundary.IsErased) continue;

                    SiteGridSettings settings;
                    if (!TryReadParentLink(boundary, transaction, out settings))
                        continue;

                    string parentHandle = boundary.Handle.ToString();
                    if (!manualRefresh && !dirtyParents.Contains(parentHandle))
                        continue;

                    if (!boundary.IsWriteEnabled)
                        boundary.UpgradeOpen();
                    RebuildOne(
                        document.Database,
                        transaction,
                        boundary,
                        settings,
                        dirtyIds);
                    refreshed++;
                }
                transaction.Commit();
            }
            return refreshed;
        }
'@
$site = Replace-CSharpMethod $site $refreshSignature $refreshMethod 'Site Grid RefreshAll'

$translationSignature = '        private static bool TryReadChildTranslation('
$translationMethod = @'
        private static bool TryReadChildTranslation(
            Database database,
            Transaction transaction,
            string parentHandle,
            GridBounds bounds,
            SiteGridSettings settings,
            ISet<ObjectId> dirtyIds,
            out Vector3d shift)
        {
            shift = new Vector3d(0.0, 0.0, 0.0);
            if (dirtyIds == null || dirtyIds.Count == 0)
                return false;

            List<double> xValues = BuildPositions(
                bounds.MinX,
                bounds.MaxX,
                settings.SpacingX);
            List<double> yValues = BuildPositions(
                bounds.MinY,
                bounds.MaxY,
                settings.SpacingY);

            foreach (ObjectId id in dirtyIds)
            {
                if (id.IsNull || id.IsErased) continue;
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
                    continue;
                }
                if (entity == null || entity.IsErased) continue;

                SiteGridChildLink link;
                if (!TryReadChildLink(entity, transaction, out link) ||
                    !string.Equals(
                        link.ParentHandle,
                        parentHandle,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                Point3d expected;
                Point3d actual;
                if (string.Equals(link.Role, "P", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(link.Role, "PM", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.XIndex < 0 || link.XIndex >= xValues.Count ||
                        link.YIndex < 0 || link.YIndex >= yValues.Count)
                        continue;
                    expected = new Point3d(
                        xValues[link.XIndex],
                        yValues[link.YIndex],
                        bounds.Elevation);
                    if (string.Equals(link.Role, "P", StringComparison.OrdinalIgnoreCase))
                    {
                        DBPoint point = entity as DBPoint;
                        if (point == null) continue;
                        actual = point.Position;
                    }
                    else
                    {
                        Circle marker = entity as Circle;
                        if (marker == null) continue;
                        actual = marker.Center;
                    }
                }
                else if (string.Equals(link.Role, "V", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.XIndex <= 0 || link.XIndex >= xValues.Count - 1)
                        continue;
                    Polyline line = entity as Polyline;
                    if (line == null || line.NumberOfVertices < 1) continue;
                    expected = new Point3d(
                        xValues[link.XIndex],
                        bounds.MinY,
                        bounds.Elevation);
                    actual = line.GetPoint3dAt(0);
                }
                else if (string.Equals(link.Role, "H", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.YIndex <= 0 || link.YIndex >= yValues.Count - 1)
                        continue;
                    Polyline line = entity as Polyline;
                    if (line == null || line.NumberOfVertices < 1) continue;
                    expected = new Point3d(
                        bounds.MinX,
                        yValues[link.YIndex],
                        bounds.Elevation);
                    actual = line.GetPoint3dAt(0);
                }
                else
                {
                    continue;
                }

                Vector3d candidate = actual - expected;
                if (candidate.Length > 1e-8)
                {
                    shift = new Vector3d(
                        candidate.X,
                        candidate.Y,
                        0.0);
                    return true;
                }
            }
            return false;
        }
'@
$site = Replace-CSharpMethod $site $translationSignature $translationMethod 'Site Grid child translation'

$modifiedSignature = '        private static void OnObjectModified('
$modifiedMethod = @'
        private static void OnObjectModified(
            object sender,
            ObjectEventArgs args)
        {
            if (_busy || _document == null || args == null || args.DBObject == null)
                return;

            DBObject value = args.DBObject;
            if (value.ObjectId.IsNull || value.IsErased)
                return;

            // Do not open/read extension dictionaries inside ObjectModified.  Track
            // only lightweight candidate entity ids; linkage is resolved safely on
            // Idle after the editing command has ended.
            if (!(value is Polyline) &&
                !(value is DBPoint) &&
                !(value is Circle) &&
                !(value is MText))
                return;

            DirtyIds.Add(value.ObjectId);
            _pending = true;
        }
'@
$site = Replace-CSharpMethod $site $modifiedSignature $modifiedMethod 'Site Grid ObjectModified watcher'

Write-Text $siteGridPath $site
$siteCheck = Read-Text $siteGridPath
foreach ($marker in @(
    'var dirtyParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);',
    'dirtyParents.Add(dirtyLink.ParentHandle);',
    'string.Equals(link.Role, "PM", StringComparison.OrdinalIgnoreCase)',
    'Circle marker = entity as Circle;',
    'if (!(value is Polyline) &&',
    '!(value is Circle) &&',
    'August12SurveySiteGridCommands.RefreshAll('
)) {
    if (-not $siteCheck.Contains($marker)) {
        throw "August 20 dynamic Site Grid marker missing after repair: $marker"
    }
}
if ($siteCheck.Contains('value.ObjectId.IsNull || value.IsErased || value.ExtensionDictionary.IsNull')) {
    throw 'August 20 Site Grid ObjectModified watcher still performs the unsafe/miss-prone ExtensionDictionary event-time gate.'
}
if (-not $siteCheck.Contains('WriteChildLink(') -or -not $siteCheck.Contains('"PM"')) {
    throw 'August 20 Site Grid visible point-marker link from the prior field repair is missing.'
}
if (-not $siteCheck.Contains('LineWeight.LineWeight050')) {
    throw 'August 20 Site Grid visible point-marker lineweight guard is missing.'
}

Write-Host 'August 20 geometry-first/dynamic repair passed:' -ForegroundColor Green
Write-Host ' - Midblock and Road Reserve public sewer commands now create AutoCAD planning polylines/circle manholes first.' -ForegroundColor Green
Write-Host ' - Road Reserve centreline public command now creates preview polylines before separate Alignment conversion.' -ForegroundColor Green
Write-Host ' - CE_SEWERBUILDNETWORK, CE_CENTERLINETOALIGNMENT and CE_SEWERREFRESHLAYOUT are available as separate conversion/refresh steps.' -ForegroundColor Green
Write-Host ' - Site Grid refresh now targets only the edited linked parent/child and visible point-marker circles participate in dynamic movement.' -ForegroundColor Green
