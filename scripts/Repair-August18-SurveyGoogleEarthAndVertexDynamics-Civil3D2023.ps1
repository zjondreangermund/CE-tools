[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$folder,[string]$name) {
    $path = Join-Path $folder $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 18 Survey/vertex source missing: $path"
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
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 18 repair anchor not found: $label" }
    return $text.Replace($old,$new)
}

# -----------------------------------------------------------------------------
# 1. Survey Production: expose closed-polyline -> Google Earth KML handoff.
#    This runs AFTER the final August 17 one-page Survey repair so older menu
#    injectors cannot remove the new command.
# -----------------------------------------------------------------------------
$googleEarthPath = Required $src 'August18SurveyGoogleEarthBoundaryCommands.cs'
$googleEarth = ReadText $googleEarthPath
if (-not $googleEarth.Contains('"CE_SURVEYGOOGLEEARTHBOUNDARY"')) {
    throw 'CE_SURVEYGOOGLEEARTHBOUNDARY command is missing from its source file.'
}
if (-not $googleEarth.Contains('NamibiaCoordinateRuntime.TryDrawingToWgs84')) {
    throw 'Google Earth boundary command is not wired to the Namibia/WGS84 conversion runtime.'
}

$centresPath = Required $src 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath
$menuCommand = 'CE_SURVEYGOOGLEEARTHBOUNDARY'
if (-not $centres.Contains($menuCommand)) {
    $googleEarthAction = '                    A("CE-Plot Polyline Boundary in Google Earth", "CE_SURVEYGOOGLEEARTHBOUNDARY", "Convert one or more closed survey polylines to WGS84 KML and open the polygon boundaries in Google Earth.", "01 SETTINGS"),'
    $anchors = @(
        '                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 SETTINGS"),',
        '                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 SETTINGS"),'
    )
    $inserted = $false
    foreach ($anchor in $anchors) {
        if ($centres.Contains($anchor)) {
            $centres = $centres.Replace($anchor, $anchor + "`r`n" + $googleEarthAction)
            $inserted = $true
            break
        }
    }
    if (-not $inserted) {
        throw 'Survey Production location/coordinate-system menu anchor was not found for Google Earth boundary insertion.'
    }
    WriteText $centresPath $centres
}

# -----------------------------------------------------------------------------
# 2. Universal dynamic refresh: source polylines must carry their linked vertex
#    setting-out outputs automatically after MOVE/STRETCH/grip editing completes.
#    Make the deferred refresh near-immediate, while never running automatic
#    overlap solving during ordinary refresh. Overlap correction stays manual.
# -----------------------------------------------------------------------------
$universalPath = Required $src 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath
$universal = ReplaceRequired $universal `
    '        internal static double DelaySeconds { get; set; } = 1.8;' `
    '        internal static double DelaySeconds { get; set; } = 0.15;' `
    'universal refresh delay'
$universal = ReplaceRequired $universal `
    '            UniversalDynamicRefreshManager.DelaySeconds = Math.Max(model.Double("Delay", 1.2), 0.5);' `
    '            UniversalDynamicRefreshManager.DelaySeconds = Math.Max(model.Double("Delay", 0.15), 0.10);' `
    'dynamic refresh settings minimum delay'
$universal = $universal.Replace(
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);')
$universal = $universal.Replace(
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);')
$universal = ReplaceRequired $universal `
    '            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;' `
    '            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.10) return;' `
    'universal repeat-refresh guard'
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 3. Vertex Setting-Out: refresh must be position-idempotent. Do not derive a new
#    label offset from the previous generated output before recreating it. That
#    old anchor can represent the pre-move source and causes cumulative drift.
# -----------------------------------------------------------------------------
$vertexPath = Required $src 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath
$vertex = $vertex.Replace(
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);')
$vertex = $vertex.Replace(
    "                CaptureCurrentAnnotationOffset(transaction, id, record);`r`n                mtext.Location = record.Point;",
    "                // Refresh is deterministic: retain the configured anchored position.`r`n                mtext.Location = record.Point;")
$oldRecreateCapture = @'
                    CaptureCurrentAnnotationOffset(
                        transaction,
                        existing,
                        record);
                    EraseIfPossible(transaction, existing);
'@ -replace "`n","`r`n"
$newRecreateCapture = @'
                    // Recreate against the recalculated source point. Do not
                    // accumulate a displacement from the previous source anchor.
                    EraseIfPossible(transaction, existing);
'@ -replace "`n","`r`n"
if ($vertex.Contains($oldRecreateCapture)) {
    $vertex = $vertex.Replace($oldRecreateCapture,$newRecreateCapture)
}
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 4. Site Grid runtime: stop the two background refresh managers from feeding
#    each other. Only actual CE site-grid parents/children are allowed to trigger
#    the site-grid rebuild. Automatic rebuilds are excluded from AutoCAD Undo,
#    and an explicit CE_SITEGRID command acknowledges the objects it just made so
#    they are not rebuilt again immediately on Idle.
# -----------------------------------------------------------------------------
$siteGridPath = Required $src 'August12SurveySiteGridCommands.cs'
$siteGrid = ReadText $siteGridPath

# PR #110 moved Site Grid display refresh away from Editor.Regen() so refreshes
# do not pollute AutoCAD Undo/Redo. Normalize an older staged source to the same
# display-flush form before applying the August 18 loop/Undo guards. This keeps
# the repair compatible with both the legacy REGEN source and the newer source.
$siteGrid = $siteGrid.Replace(
    '            document.Editor.Regen();',
    '            August21DisplayRefresh.Flush(document);')
$siteGrid = $siteGrid.Replace(
    '                    _document.Editor.Regen();',
    '                    August21DisplayRefresh.Flush(_document);')

$siteGridHelperAnchor = @'
        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
'@ -replace "`n","`r`n"
$siteGridHelperReplacement = @'
        internal static bool IsLinkedSiteGridObject(
            Database database,
            ObjectId id)
        {
            if (database == null || id.IsNull || id.IsErased)
                return false;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartOpenCloseTransaction())
                {
                    DBObject owner = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                    if (owner == null || owner.ExtensionDictionary.IsNull)
                        return false;
                    DBDictionary dictionary = transaction.GetObject(
                        owner.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    return dictionary != null &&
                        (dictionary.Contains(ParentKey) ||
                         dictionary.Contains(ChildKey));
                }
            }
            catch
            {
                return false;
            }
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $siteGridHelperAnchor $siteGridHelperReplacement 'site-grid linked-object filter helper'

$runtimeTerminateAnchor = @'
        private static void OnDocumentActivated(
            object sender,
            DocumentCollectionEventArgs args)
'@ -replace "`n","`r`n"
$runtimeTerminateReplacement = @'
        internal static void AcknowledgeCurrentState()
        {
            DirtyIds.Clear();
            _pending = false;
        }

        private static void OnDocumentActivated(
            object sender,
            DocumentCollectionEventArgs args)
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $runtimeTerminateAnchor $runtimeTerminateReplacement 'site-grid acknowledge-current-state helper'

$commitAckOld = @'
                transaction.Commit();
            }

            August21DisplayRefresh.Flush(document);
'@ -replace "`n","`r`n"
$commitAckNew = @'
                transaction.Commit();
            }

            August12SiteGridRuntimeManager.AcknowledgeCurrentState();
            August21DisplayRefresh.Flush(document);
'@ -replace "`n","`r`n"
if (-not $siteGrid.Contains($commitAckNew)) {
    if (-not $siteGrid.Contains($commitAckOld)) {
        throw 'August 18 repair anchor not found: site-grid explicit command acknowledgement'
    }
    $siteGrid = $siteGrid.Replace($commitAckOld,$commitAckNew)
}

$manualRefreshOld = @'
            int refreshed = RefreshAll(document, null);
            August21DisplayRefresh.Flush(document);
'@ -replace "`n","`r`n"
$manualRefreshNew = @'
            int refreshed = RefreshAll(document, null);
            August12SiteGridRuntimeManager.AcknowledgeCurrentState();
            August21DisplayRefresh.Flush(document);
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $manualRefreshOld $manualRefreshNew 'site-grid manual refresh acknowledgement'

$dirtyFilterOld = @'
            var dirty = new HashSet<ObjectId>(DirtyIds);
            DirtyIds.Clear();
            _pending = false;
            _busy = true;
'@ -replace "`n","`r`n"
$dirtyFilterNew = @'
            var dirty = new HashSet<ObjectId>(DirtyIds);
            DirtyIds.Clear();
            _pending = false;

            // The drawing contains many CE objects with extension dictionaries.
            // Only a true site-grid parent/child may wake this dedicated manager.
            dirty.RemoveWhere(id =>
                !August12SurveySiteGridCommands.IsLinkedSiteGridObject(
                    _document.Database,
                    id));
            if (dirty.Count == 0)
                return;

            _busy = true;
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $dirtyFilterOld $dirtyFilterNew 'site-grid dirty-object filtering'

$siteGridAutoRefreshOld = @'
            _busy = true;
            try
            {
                int refreshed =
                    August12SurveySiteGridCommands.RefreshAll(
                        _document,
                        dirty);
                if (refreshed > 0)
                    August21DisplayRefresh.Flush(_document);
            }
            catch
            {
                // Dynamic refresh must never interrupt the active Civil 3D session.
            }
            finally
            {
                _busy = false;
            }
'@ -replace "`n","`r`n"
$siteGridAutoRefreshNew = @'
            _busy = true;
            bool undoRecordingDisabled = false;
            try
            {
                // A background dependency update is bookkeeping, not a user edit.
                // Keep it out of AutoCAD's Undo dropdown.
                try
                {
                    _document.Database.DisableUndoRecording(true);
                    undoRecordingDisabled = true;
                }
                catch { }

                int refreshed =
                    August12SurveySiteGridCommands.RefreshAll(
                        _document,
                        dirty);
                if (refreshed > 0)
                    August21DisplayRefresh.Flush(_document);
            }
            catch
            {
                // Dynamic refresh must never interrupt the active Civil 3D session.
            }
            finally
            {
                if (undoRecordingDisabled)
                {
                    try { _document.Database.DisableUndoRecording(false); }
                    catch { }
                }
                _busy = false;
            }
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $siteGridAutoRefreshOld $siteGridAutoRefreshNew 'site-grid automatic refresh undo suppression'
WriteText $siteGridPath $siteGrid

# The Site Grid has its own precise dependency manager. Do not also schedule the
# expensive universal refresh after CE_SITEGRID/REFRESH/REMOVE, including pending
# object events raised while the explicit command creates its children.
$universal = ReadText $universalPath
$universalSiteGridOld = @'
            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase) ||
'@ -replace "`n","`r`n"
$universalSiteGridNew = @'
            if (IsSiteGridCommand(command))
            {
                _pending = false;
                _lastChangeUtc = DateTime.UtcNow;
                return;
            }
            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase) ||
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $universalSiteGridOld $universalSiteGridNew 'universal site-grid command exclusion'

$undoHelperAnchor = @'
        private static bool IsUndoRedo(string command)
'@ -replace "`n","`r`n"
$undoHelperReplacement = @'
        private static bool IsSiteGridCommand(string command)
        {
            return string.Equals(command, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUndoRedo(string command)
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $undoHelperAnchor $undoHelperReplacement 'universal site-grid command predicate'
WriteText $universalPath $universal

$automaticPath = Required $src 'AugustAutomaticRefreshManager.cs'
$automatic = ReadText $automaticPath
$automaticOld = @'
            string name = ReadCommandName(args);
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
'@ -replace "`n","`r`n"
$automaticNew = @'
            string name = ReadCommandName(args);
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(name, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase))
                return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
'@ -replace "`n","`r`n"
$automatic = ReplaceRequired $automatic $automaticOld $automaticNew 'automatic manager site-grid exclusion'
WriteText $automaticPath $automatic

# -----------------------------------------------------------------------------
# Final guards. These are intentionally strict because this is the last staging
# repair before the Civil 3D 2023 compile.
# -----------------------------------------------------------------------------
$centres = ReadText $centresPath
if (-not $centres.Contains('CE_SURVEYGOOGLEEARTHBOUNDARY')) {
    throw 'Survey Production does not expose CE_SURVEYGOOGLEEARTHBOUNDARY.'
}
$universal = ReadText $universalPath
if (-not $universal.Contains('DelaySeconds { get; set; } = 0.15;')) {
    throw 'Fast automatic source-geometry refresh delay was not applied.'
}
if ($universal.Contains('CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);')) {
    throw 'Universal refresh still auto-solves COGO overlaps and can drift labels.'
}
if ($universal.Contains('RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);')) {
    throw 'Universal refresh still auto-solves linked annotation overlaps.'
}
if (-not $universal.Contains('private static bool IsSiteGridCommand(string command)')) {
    throw 'Universal refresh still lacks the dedicated Site Grid command exclusion.'
}
$vertex = ReadText $vertexPath
if ($vertex.Contains('RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);')) {
    throw 'Vertex Setting-Out still requests overlap movement during normal create/refresh.'
}
if ($vertex.Contains('CaptureCurrentAnnotationOffset(transaction, id, record);')) {
    throw 'MText vertex refresh still captures a stale pre-refresh annotation anchor.'
}
if ($vertex.Contains($oldRecreateCapture)) {
    throw 'MLeader vertex refresh still captures a stale pre-refresh annotation anchor.'
}
$siteGrid = ReadText $siteGridPath
foreach ($requiredSiteGrid in @(
    'internal static bool IsLinkedSiteGridObject(',
    'internal static void AcknowledgeCurrentState()',
    'dirty.RemoveWhere(id =>',
    '_document.Database.DisableUndoRecording(true);',
    '_document.Database.DisableUndoRecording(false);')) {
    if (-not $siteGrid.Contains($requiredSiteGrid)) {
        throw "Site Grid loop/Undo guard missing: $requiredSiteGrid"
    }
}
if ($siteGrid.Contains('document.Editor.Regen();') -or
    $siteGrid.Contains('_document.Editor.Regen();')) {
    throw 'Site Grid repair regressed to Editor.Regen instead of August21DisplayRefresh.Flush.'
}
if (([regex]::Matches(
        $siteGrid,
        [regex]::Escape('August21DisplayRefresh.Flush('))).Count -lt 3) {
    throw 'Site Grid display-flush guard is missing from create/manual/deferred refresh paths.'
}
$automatic = ReadText $automaticPath
if (-not $automatic.Contains('CE_SITEGRIDREFRESH')) {
    throw 'Automatic CE command refresh manager still schedules Site Grid globally.'
}

Write-Host 'Survey Production now includes closed-polyline Google Earth boundary export.' -ForegroundColor Green
Write-Host 'Vertex setting-out points now auto-follow moved source geometry after the edit command finishes.' -ForegroundColor Green
Write-Host 'Normal refresh preserves label offsets and no longer repeatedly solves overlaps.' -ForegroundColor Green
Write-Host 'Site Grid refresh is now dependency-filtered, excluded from background Undo, and no longer feeds the universal refresh loop.' -ForegroundColor Green
