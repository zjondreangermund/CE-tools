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
        throw "Source-only refresh repair source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceMethod([string]$text,[string]$signature,[string]$replacement,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Source-only refresh method marker not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Source-only refresh opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "Source-only refresh closing brace not found: $label" }
    return $text.Substring(0,$start) + ($replacement -replace "`r?`n","`r`n") + $text.Substring($close + 1)
}
function InsertBefore([string]$text,[string]$marker,[string]$addition,[string]$label) {
    if ($text.Contains($addition.Trim())) { return $text }
    $index = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Source-only refresh insertion marker not found: $label" }
    return $text.Substring(0,$index) + ($addition -replace "`r?`n","`r`n") + $text.Substring($index)
}

# -----------------------------------------------------------------------------
# 1. Universal Dynamic: only a modified linked Vertex/Grid SOURCE polyline or
#    feature line is allowed to set the automatic pending flag. Output COGO,
#    MText, MLeader, Table, Xrecord and other CE mutations can never requeue it.
# -----------------------------------------------------------------------------
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath

if (-not $universal.Contains('private static bool _sourcePending;')) {
    $field = '        private static bool _pending;'
    if (-not $universal.Contains($field)) { throw 'Universal pending field anchor was not found.' }
    $universal = $universal.Replace($field,$field + "`r`n" + '        private static bool _sourcePending;')
}

$attach = @'
        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _database = document == null ? null : document.Database;
            _pending = false;
            _sourcePending = false;
            if (_document == null || _database == null) return;

            // Source-owner-only policy: only modifications are observed. Creating,
            // erasing, opening or activating a drawing must never wake CE refresh.
            _database.ObjectModified += OnObjectChanged;
            _document.CommandWillStart += OnCommandWillStart;
            _document.CommandEnded += OnCommandEnded;
            _document.CommandCancelled += OnCommandEnded;
            _document.CommandFailed += OnCommandEnded;
        }
'@
$universal = ReplaceMethod $universal '        private static void Attach(Document document)' $attach 'Universal Attach'

$detach = @'
        private static void Detach()
        {
            if (_database != null)
                _database.ObjectModified -= OnObjectChanged;
            if (_document != null)
            {
                _document.CommandWillStart -= OnCommandWillStart;
                _document.CommandEnded -= OnCommandEnded;
                _document.CommandCancelled -= OnCommandEnded;
                _document.CommandFailed -= OnCommandEnded;
            }
            _database = null;
            _document = null;
            _undoRedoActive = false;
            _pending = false;
            _sourcePending = false;
        }
'@
$universal = ReplaceMethod $universal '        private static void Detach()' $detach 'Universal Detach'

$willStart = @'
        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (!IsUndoRedo(command)) return;
            _undoRedoActive = true;
            _pending = false;
            _sourcePending = false;
        }
'@
$universal = ReplaceMethod $universal '        private static void OnCommandWillStart(object sender, CommandEventArgs e)' $willStart 'Universal OnCommandWillStart'

$ended = @'
        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (!IsUndoRedo(command)) return;

            // Command completion itself never queues a refresh. ObjectModified on
            // a linked source owns the pending flag. Undo/redo simply clears it.
            _undoRedoActive = false;
            _pending = false;
            _sourcePending = false;
            _lastChangeUtc = DateTime.UtcNow;
        }
'@
$universal = ReplaceMethod $universal '        private static void OnCommandEnded(object sender, CommandEventArgs e)' $ended 'Universal OnCommandEnded'

$sourcePredicateAndChanged = @'
        private static bool IsLinkedSettingOutSource(DBObject value)
        {
            if (value == null || value.IsErased || value.ObjectId.IsNull || _database == null)
                return false;

            bool polyline = value is Polyline || value is Polyline2d || value is Polyline3d;
            string typeName = value.GetType().Name ?? string.Empty;
            bool featureLine = typeName.IndexOf(
                "FeatureLine",
                StringComparison.OrdinalIgnoreCase) >= 0;
            if (!polyline && !featureLine) return false;

            string sourceHandle;
            try { sourceHandle = value.ObjectId.Handle.ToString(); }
            catch { return false; }
            if (string.IsNullOrWhiteSpace(sourceHandle)) return false;

            try
            {
                using (Transaction transaction =
                    _database.TransactionManager.StartOpenCloseTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(
                        _database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    if (blockTable == null || !blockTable.Has(BlockTableRecord.ModelSpace))
                        return false;
                    BlockTableRecord model = transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model == null) return false;

                    foreach (ObjectId id in model)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; }
                        catch { continue; }
                        if (table == null || table.IsErased) continue;

                        // Vertex Setting-Out stores source handles in table XData as
                        // SRC=<handle>. Older links may contain the raw handle.
                        try
                        {
                            ResultBuffer vertex = table.GetXDataForApplication(
                                "CE_VERTEX_SETTINGOUT");
                            if (vertex != null)
                            {
                                foreach (TypedValue item in vertex)
                                {
                                    string text = Convert.ToString(
                                        item.Value,
                                        CultureInfo.InvariantCulture);
                                    if (string.Equals(
                                            text,
                                            "SRC=" + sourceHandle,
                                            StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(
                                            text,
                                            sourceHandle,
                                            StringComparison.OrdinalIgnoreCase))
                                        return true;
                                }
                            }
                        }
                        catch { }

                        // Dynamic Grid stores Source=<handle> values in its table
                        // extension record.
                        try
                        {
                            if (table.ExtensionDictionary.IsNull) continue;
                            DBDictionary dictionary = transaction.GetObject(
                                table.ExtensionDictionary,
                                OpenMode.ForRead,
                                false) as DBDictionary;
                            if (dictionary == null ||
                                !dictionary.Contains("CE_DYNAMIC_GRID_TABLE"))
                                continue;
                            Xrecord record = transaction.GetObject(
                                dictionary.GetAt("CE_DYNAMIC_GRID_TABLE"),
                                OpenMode.ForRead,
                                false) as Xrecord;
                            if (record == null || record.Data == null) continue;
                            foreach (TypedValue item in record.Data)
                            {
                                string text = Convert.ToString(
                                    item.Value,
                                    CultureInfo.InvariantCulture);
                                if (string.Equals(
                                        text,
                                        "Source=" + sourceHandle,
                                        StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                // A source edit must never interrupt the active Civil 3D command.
            }
            return false;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs e)
        {
            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;
            if (!IsLinkedSettingOutSource(e.DBObject)) return;

            _sourcePending = true;
            Queue();
        }
'@
$universal = ReplaceMethod $universal '        private static void OnObjectChanged(object sender, ObjectEventArgs e)' $sourcePredicateAndChanged 'Universal linked-source ObjectModified'

$erased = @'
        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            // Erasing outputs, tables, COGO points or unrelated geometry must not
            // start an automatic model refresh. Use the explicit refresh command
            // after intentional source deletion/relinking.
        }
'@
$universal = ReplaceMethod $universal '        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)' $erased 'Universal ObjectErased'

# Full/manual refresh clears any delayed source refresh that was waiting.
$manualPending = '                _pending = false;' + "`r`n" +
                 '                _lastRefreshUtc = DateTime.UtcNow;'
$manualPendingNew = '                _pending = false;' + "`r`n" +
                    '                _sourcePending = false;' + "`r`n" +
                    '                _lastRefreshUtc = DateTime.UtcNow;'
if ($universal.Contains($manualPending)) {
    $universal = $universal.Replace($manualPending,$manualPendingNew)
}

$autoRefreshAndIdle = @'
        private static int RefreshSettingOutDependencies(Document document)
        {
            if (document == null || _busy || _undoRedoActive || !_sourcePending)
                return 0;

            bool undoRecordingDisabled = false;
            int refreshed = 0;
            _busy = true;
            try
            {
                try
                {
                    document.Database.DisableUndoRecording(true);
                    undoRecordingDisabled = true;
                }
                catch { }

                // Automatic refresh deliberately owns only setting-out outputs.
                // It never runs the universal project/network/table/style pipeline.
                try { refreshed += VertexSettingOutCommands.RefreshAll(document); }
                catch { }
                try { refreshed += August18DynamicGridSettingOutCommands.RefreshAll(document); }
                catch { }

                _pending = false;
                _sourcePending = false;
                _lastRefreshUtc = DateTime.UtcNow;
                return refreshed;
            }
            finally
            {
                if (undoRecordingDisabled)
                {
                    try { document.Database.DisableUndoRecording(false); }
                    catch { }
                }
                _busy = false;
            }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!Enabled || !_pending || !_sourcePending || _busy ||
                _undoRedoActive || active == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalSeconds < DelaySeconds) return;
            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;

            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;

            int refreshed = RefreshSettingOutDependencies(active);
            if (refreshed > 0)
            {
                // Exactly one explicit redraw after the one deferred source update.
                try { active.Editor.Regen(); } catch { }
            }
        }
'@
$universal = ReplaceMethod $universal '        private static void OnIdle(object sender, EventArgs e)' $autoRefreshAndIdle 'Universal source-only OnIdle'
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 2. Legacy command-ended dispatcher must not queue Universal or Platform refresh
#    just because a CE command finished.
# -----------------------------------------------------------------------------
$automaticPath = Required 'AugustAutomaticRefreshManager.cs'
$automatic = ReadText $automaticPath
$automaticInit = @'
        internal static void Initialize()
        {
            // Automatic command-ended refresh is intentionally disabled. Linked
            // setting-out source geometry is watched by UniversalDynamicRefresh.
            _initialized = true;
        }
'@
$automatic = ReplaceMethod $automatic '        internal static void Initialize()' $automaticInit 'AugustAutomaticRefreshManager Initialize'
$automaticTerminate = @'
        internal static void Terminate()
        {
            _initialized = false;
        }
'@
$automatic = ReplaceMethod $automatic '        internal static void Terminate()' $automaticTerminate 'AugustAutomaticRefreshManager Terminate'
$automaticEnded = @'
        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            // No automatic queue on CE command completion.
        }
'@
$automatic = ReplaceMethod $automatic '        private static void OnCommandEnded(object sender, CommandEventArgs e)' $automaticEnded 'AugustAutomaticRefreshManager OnCommandEnded'
WriteText $automaticPath $automatic

# -----------------------------------------------------------------------------
# 3. Platform automatic Idle watcher is disabled. Explicit platform commands and
#    CE_PLATFORMREFRESH remain available, but platform tables/surfaces cannot wake
#    the Survey setting-out model in the background.
# -----------------------------------------------------------------------------
$platformPath = Required 'PlatformProductionCommands.cs'
$platform = ReadText $platformPath
$platformInit = @'
        internal static void EnsureInitialized()
        {
            // Background platform polling is disabled by the source-only refresh
            // policy. Platform commands still perform their explicit refreshes.
            _initialised = true;
        }
'@
$platform = ReplaceMethod $platform '        internal static void EnsureInitialized()' $platformInit 'PlatformDynamicRefreshManager EnsureInitialized'
$platformIdle = @'
        private static void Idle(object sender, EventArgs e)
        {
            // Disabled: no background platform regeneration.
        }
'@
$platform = ReplaceMethod $platform '        private static void Idle(object sender, EventArgs e)' $platformIdle 'PlatformDynamicRefreshManager Idle'
WriteText $platformPath $platform

# -----------------------------------------------------------------------------
# 4. Annotation-scale maintenance remains available manually, but it no longer
#    runs an Idle watcher or queues Universal refresh automatically.
# -----------------------------------------------------------------------------
$annotationPath = Required 'AnnotationScaleSyncCommands.cs'
$annotation = ReadText $annotationPath
$annotationInit = @'
        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            LastScaleByDatabase.Clear();
        }
'@
$annotation = ReplaceMethod $annotation '        internal static void Initialize()' $annotationInit 'AnnotationScaleSync Initialize'
$annotationTerminate = @'
        internal static void Terminate()
        {
            LastScaleByDatabase.Clear();
            _initialized = false;
            _busy = false;
        }
'@
$annotation = ReplaceMethod $annotation '        internal static void Terminate()' $annotationTerminate 'AnnotationScaleSync Terminate'
WriteText $annotationPath $annotation

# -----------------------------------------------------------------------------
# 5. Site Grid keeps one automatic watcher, but only its PARENT boundary polyline
#    may trigger it. Generated grid lines/points/text never dirty the runtime.
# -----------------------------------------------------------------------------
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'
$siteGrid = ReadText $siteGridPath
$siteModified = @'
        private static void OnObjectModified(
            object sender,
            ObjectEventArgs args)
        {
            if (_busy || _document == null || args == null || args.DBObject == null)
                return;

            Polyline boundary = args.DBObject as Polyline;
            if (boundary == null || boundary.ObjectId.IsNull ||
                boundary.IsErased || boundary.ExtensionDictionary.IsNull)
                return;

            bool isParent = false;
            try
            {
                using (Transaction transaction =
                    _document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    DBDictionary dictionary = transaction.GetObject(
                        boundary.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    isParent = dictionary != null && dictionary.Contains(
                        August12SurveySiteGridCommands.ParentKey);
                }
            }
            catch { }
            if (!isParent) return;

            DirtyIds.Add(boundary.ObjectId);
            _pending = true;
        }
'@
$siteGrid = ReplaceMethod $siteGrid '        private static void OnObjectModified(' $siteModified 'Site Grid parent-only ObjectModified'
$siteCommandEnded = @'
        private static void OnCommandEnded(
            object sender,
            CommandEventArgs args)
        {
            // ObjectModified on the parent boundary already owns the pending flag.
        }
'@
$siteGrid = ReplaceMethod $siteGrid '        private static void OnCommandEnded(' $siteCommandEnded 'Site Grid OnCommandEnded'

$siteGrid = $siteGrid.Replace(
    '"Move the frame, a generated grid line or a generated grid point and CE Tools refreshes the complete linked grid.");',
    '"Move or edit the linked frame polyline and CE Tools refreshes the complete grid once after the edit finishes.");')
$siteGrid = $siteGrid.Replace(
    '"When points are enabled, moving any generated grid point moves the complete linked grid.",',
    '"Generated grid points are output controls only; they do not trigger automatic regeneration.",')
$siteGrid = $siteGrid.Replace(
    '"\nCE_SITEGRID complete. Linked objects regenerated={0}. Move the frame/grid line/grid point to auto-refresh.",',
    '"\nCE_SITEGRID complete. Linked objects regenerated={0}. Only moving/editing the linked frame polyline auto-refreshes the grid.",')
WriteText $siteGridPath $siteGrid

# -----------------------------------------------------------------------------
# 6. Do not start legacy background model refreshers at plugin startup. Manual
#    commands remain registered. Universal source-owner watching remains active.
# -----------------------------------------------------------------------------
$pluginPath = Required 'PluginEntry.cs'
$plugin = ReadText $pluginPath
foreach ($manager in @(
    'ParkingOptionAutoRefreshManager.Initialize();',
    'DynamicSectionUpdateManager.Initialize();',
    'DynamicIntersectionUpdateManager.Initialize();',
    'AnnotationScaleSyncManager.Initialize();',
    'ParkingNumberAutoRefreshManager.Initialize();',
    'WaterSewerCostAutoRefreshManager.Initialize();',
    'LinkedTableAutoRefreshManager.Initialize();',
    'CogoPointProjectStyleManager.Initialize();',
    'SewerNetworkDynamicSequenceManager.Initialize();',
    'AugustAutomaticRefreshManager.Initialize();')) {
    $plugin = [regex]::Replace(
        $plugin,
        '(?m)^\s*' + [regex]::Escape($manager) + '\s*\r?\n',
        '')
}
WriteText $pluginPath $plugin

# -----------------------------------------------------------------------------
# Final guards. Fail the build rather than silently shipping another feedback
# loop or broad automatic regeneration path.
# -----------------------------------------------------------------------------
$universal = ReadText $universalPath
foreach ($forbidden in @(
    '_database.ObjectAppended += OnObjectChanged;',
    '_database.ObjectErased += OnObjectErased;',
    'if (IsRefreshDependency(value)) Queue();',
    'RefreshNow(active, true);',
    'command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)',
    'command.IndexOf("MOVE", StringComparison.OrdinalIgnoreCase) >= 0',
    'command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0')) {
    if ($universal.Contains($forbidden)) {
        throw "Broad automatic Universal refresh trigger remains: $forbidden"
    }
}
foreach ($required in @(
    'private static bool _sourcePending;',
    'IsLinkedSettingOutSource(DBObject value)',
    '"CE_VERTEX_SETTINGOUT"',
    '"CE_DYNAMIC_GRID_TABLE"',
    '"SRC=" + sourceHandle',
    '"Source=" + sourceHandle',
    'RefreshSettingOutDependencies(Document document)',
    'VertexSettingOutCommands.RefreshAll(document)',
    'August18DynamicGridSettingOutCommands.RefreshAll(document)',
    'if (!Enabled || !_pending || !_sourcePending')) {
    if (-not $universal.Contains($required)) {
        throw "Source-only Universal refresh marker missing: $required"
    }
}

$automatic = ReadText $automaticPath
if ($automatic.Contains('UniversalDynamicRefreshManager.Queue();') -or
    $automatic.Contains('PlatformDynamicRefreshManager.Queue();')) {
    throw 'Legacy August command-ended manager can still queue automatic refresh.'
}

$platform = ReadText $platformPath
$platformManagerStart = $platform.IndexOf('internal static class PlatformDynamicRefreshManager',[StringComparison]::Ordinal)
if ($platformManagerStart -lt 0) { throw 'PlatformDynamicRefreshManager was not found.' }
$platformManager = $platform.Substring($platformManagerStart)
if ($platformManager.Contains('AcApplication.Idle += Idle;') -or
    $platformManager.Contains('_database.ObjectModified += Changed;')) {
    throw 'Platform background refresh watcher is still active.'
}

$annotation = ReadText $annotationPath
if ($annotation.Contains('AcApplication.Idle += OnIdle;')) {
    throw 'Annotation-scale Idle watcher is still active.'
}

$siteGrid = ReadText $siteGridPath
if (-not $siteGrid.Contains('dictionary.Contains(' + "`r`n" + '                        August12SurveySiteGridCommands.ParentKey)')) {
    throw 'Site Grid runtime is not restricted to its parent boundary polyline.'
}

$plugin = ReadText $pluginPath
foreach ($forbiddenManager in @(
    'ParkingOptionAutoRefreshManager.Initialize();',
    'DynamicSectionUpdateManager.Initialize();',
    'DynamicIntersectionUpdateManager.Initialize();',
    'AnnotationScaleSyncManager.Initialize();',
    'ParkingNumberAutoRefreshManager.Initialize();',
    'WaterSewerCostAutoRefreshManager.Initialize();',
    'LinkedTableAutoRefreshManager.Initialize();',
    'CogoPointProjectStyleManager.Initialize();',
    'SewerNetworkDynamicSequenceManager.Initialize();')) {
    if ($plugin.Contains($forbiddenManager)) {
        throw "Legacy background refresh manager still starts with CE Tools: $forbiddenManager"
    }
}
if (-not $plugin.Contains('UniversalDynamicRefreshManager.Initialize();')) {
    throw 'Source-linked Universal watcher is not initialized at CE Tools startup.'
}

Write-Host 'Automatic refresh is now SOURCE-OWNER ONLY: only linked Vertex/Grid polylines or feature lines can queue it.' -ForegroundColor Green
Write-Host 'COGO, MText, MLeader, tables, Xrecords, drawing-open and CE-command completion cannot requeue automatic refresh.' -ForegroundColor Green
Write-Host 'Automatic setting-out refresh performs one deferred Vertex/Grid pass and at most one Regen per linked source edit.' -ForegroundColor Green
Write-Host 'Platform, annotation-scale and legacy project refresh Idle watchers no longer regenerate the Survey model in the background.' -ForegroundColor Green
Write-Host 'Site Grid now auto-refreshes only when its linked parent boundary polyline is edited.' -ForegroundColor Green
