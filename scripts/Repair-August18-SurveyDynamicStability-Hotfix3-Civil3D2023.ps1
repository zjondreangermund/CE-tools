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
        throw "August 18 Survey stability source missing: $path"
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
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        throw "August 18 Survey stability anchor not found: $label"
    }
    return $text.Replace($old,$new)
}
function ReplaceMethod([string]$text,[string]$marker,[string]$replacement,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 18 Survey stability method marker not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace not found: $label" }
    return $text.Substring(0,$start) + $replacement + $text.Substring($close+1)
}

# -----------------------------------------------------------------------------
# 1. GRID SETTING-OUT: keep its own Perimeter / Full-grid popup and route the
#    historical public command to the new linked multi-polyline grid engine.
#    Hotfix2 temporarily routed this command to Vertex Setting-Out; replace only
#    that temporary bridge so the legacy static implementation remains unreachable.
# -----------------------------------------------------------------------------
$gridEnginePath = Required 'August18DynamicGridSettingOutCommands.cs'
$gridEngine = ReadText $gridEnginePath
foreach ($marker in @(
    '"CE_GRIDSETTINGOUTDYNAMIC"',
    '"Perimeter", "Full grid"',
    'Select MULTIPLE closed grid/site polylines',
    'CE_DYNAMIC_GRID_TABLE',
    'internal static int RefreshAll(Document document)',
    'point.RawDescription = record.Name;')) {
    if (-not $gridEngine.Contains($marker)) {
        throw "Dynamic Grid Setting-Out engine marker missing: $marker"
    }
}

$finalPath = Required 'FinalAllCommentsCompletionCommands.cs'
$final = ReadText $finalPath
$final = ReplaceRequired $final `
    '            document.Editor.WriteMessage(`r`n                "\nCE_GRIDSETTINGOUT: select one or more polylines/feature lines for linked dynamic setting-out.");`r`n            document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);' `
    '            document.Editor.WriteMessage(`r`n                "\nCE_GRIDSETTINGOUT: select one or more closed polylines, then choose Perimeter or Full grid.");`r`n            document.SendStringToExecute("CE_GRIDSETTINGOUTDYNAMIC ", true, false, true);' `
    'Grid Setting-Out dedicated dynamic front door'
WriteText $finalPath $final

# -----------------------------------------------------------------------------
# 2. UNIVERSAL REFRESH: one refresh owner, one settled pass, no presentation
#    solvers during normal dependency updates and no broad Table/MText/Xrecord
#    feedback. Geometry/source edits still queue automatically.
# -----------------------------------------------------------------------------
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath
$universal = $universal.Replace(
    '        internal static double DelaySeconds { get; set; } = 0.15;',
    '        internal static double DelaySeconds { get; set; } = 0.35;')
$universal = $universal.Replace(
    '            UniversalDynamicRefreshManager.DelaySeconds = Math.Max(model.Double("Delay", 0.15), 0.10);',
    '            UniversalDynamicRefreshManager.DelaySeconds = Math.Max(model.Double("Delay", 0.35), 0.25);')
$universal = $universal.Replace(
    '            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.10) return;',
    '            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;')

$refreshEntryOld = @'
        internal static UniversalRefreshResult RefreshNow(Document document)
        {
            return RefreshNow(document, false);
        }
'@ -replace "`n","`r`n"
$refreshEntryNew = @'
        internal static UniversalRefreshResult RefreshNow(Document document)
        {
            // Refresh commands are dependency bookkeeping, not drawing edits.
            return RefreshNow(document, true);
        }

        internal static UniversalRefreshResult RefreshBackground(Document document)
        {
            return RefreshNow(document, true);
        }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $refreshEntryOld $refreshEntryNew 'undo-suppressed universal refresh entry'

# Presentation/style solvers are deliberate manual actions. Re-running them after
# every geometry edit moves COGO labels and causes visible table/annotation churn.
$universal = $universal.Replace(
    '                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, false); }`r`n                catch { result.Warnings++; }`r`n',
    '')
$universal = $universal.Replace(
    '                try { RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false); }`r`n                catch { result.Warnings++; }`r`n',
    '')
$universal = $universal.Replace(
    '                try { CeTablePresentationManager.CenterCeTables(document); }`r`n                catch { result.Warnings++; }`r`n',
    '')

$coordinateRefreshAnchor = @'
                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }
                catch { result.Warnings++; }
'@ -replace "`n","`r`n"
$coordinateRefreshExpanded = @'
                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.VertexTables += August18DynamicGridSettingOutCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { SurfaceComparisonLinkStore.RefreshAll(document); }
                catch { result.Warnings++; }
                try { MultiSurfaceComparisonTableStore.RefreshAll(document); }
                catch { result.Warnings++; }
                try { LinkedSurfaceReportTableStore.RefreshAll(document); }
                catch { result.Warnings++; }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $coordinateRefreshAnchor $coordinateRefreshExpanded 'dynamic grid and Survey linked-table integration'

$sitePredicateOld = @'
        private static bool IsSiteGridCommand(string command)
        {
            return string.Equals(command, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase);
        }
'@ -replace "`n","`r`n"
$sitePredicateNew = @'
        private static bool IsSelfRefreshingSurveyCommand(string command)
        {
            return string.Equals(command, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_GRIDSETTINGOUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_GRIDSETTINGOUTDYNAMIC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_GRIDSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_VERTEXSETTINGOUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_VERTEXSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SURVEYREFRESHSAFE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase);
        }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $sitePredicateOld $sitePredicateNew 'self-refreshing Survey command predicate'
$universal = $universal.Replace('if (IsSiteGridCommand(command))','if (IsSelfRefreshingSurveyCommand(command))')

$objectChangedOld = @'
            DBObject value = e.DBObject;
            if (value is Entity || value is Xrecord || value is DBDictionary ||
                value is CogoPoint || value is Pipe || value is Structure ||
                value is Autodesk.Civil.DatabaseServices.Network)
                Queue();
'@ -replace "`n","`r`n"
$objectChangedNew = @'
            DBObject value = e.DBObject;
            if (IsRefreshDependency(value)) Queue();
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $objectChangedOld $objectChangedNew 'dependency-only ObjectModified queue'

$objectChangedMethodAnchor = @'
        private static void OnObjectChanged(object sender, ObjectEventArgs e)
'@ -replace "`n","`r`n"
$dependencyHelper = @'
        private static bool IsRefreshDependency(DBObject value)
        {
            if (value == null || value.IsErased) return false;
            if (value is Polyline || value is Polyline2d || value is Polyline3d ||
                value is Line || value is Arc || value is CogoPoint ||
                value is Pipe || value is Structure ||
                value is Autodesk.Civil.DatabaseServices.Network)
                return true;

            // Avoid a compile-time dependency on every Civil 3D derived class
            // while still recognizing the engineering sources that can drive
            // linked outputs. Tables, MText, MLeaders, Dimensions, Xrecords and
            // dictionaries are deliberately excluded: they are CE outputs and
            // must not feed another refresh back into the queue.
            string name = value.GetType().Name ?? string.Empty;
            return name.IndexOf("FeatureLine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Alignment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs e)
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $objectChangedMethodAnchor $dependencyHelper 'refresh dependency filter helper'

$idleRefreshOld = '            RefreshNow(active, true);'
$idleRefreshNew = @'
            UniversalRefreshResult result = RefreshNow(active, true);
            if (result.LinkedEngineRuns > 0 || result.VertexTables > 0 ||
                result.JunctionLabels > 0 || result.MetadataAttributes > 0)
            {
                try { active.Editor.Regen(); } catch { }
            }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $idleRefreshOld $idleRefreshNew 'single post-refresh regen'
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 3. SURVEY LINKED/ANNOTATIVE REFRESH: one coordinated pass only. The old method
#    refreshed many stores independently, then Universal refreshed them again,
#    then the post-command manager queued a third pass. It also historically
#    restored stale COGO label offsets. Replace the entire public command body.
# -----------------------------------------------------------------------------
$surveyFieldPath = Required 'August14SurveyFieldReviewCommands.cs'
$surveyField = ReadText $surveyFieldPath
$surveyRefreshMarker = '        [CommandMethod("CE_TOOLS", "CE_SURVEYREFRESHSAFE", CommandFlags.Modal | CommandFlags.Redraw)]'
$surveyRefreshReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_SURVEYREFRESHSAFE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurveyRefreshSafe()
        {
            Document document = Active();
            if (document == null) return;
            try
            {
                UniversalRefreshResult result =
                    UniversalDynamicRefreshManager.RefreshBackground(document);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_SURVEYREFRESHSAFE complete. One coordinated linked refresh was applied without restoring/moving COGO label offsets. Vertex/grid tables={0}; warnings={1}.",
                    result.VertexTables,
                    result.Warnings);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURVEYREFRESHSAFE stopped. {0}",
                    exception.Message);
            }
        }
'@ -replace "`n","`r`n"
$surveyField = ReplaceMethod $surveyField $surveyRefreshMarker $surveyRefreshReplacement 'CE_SURVEYREFRESHSAFE single-owner refresh'
WriteText $surveyFieldPath $surveyField

# -----------------------------------------------------------------------------
# 4. CANNOSCALE watcher: update annotation contexts silently and queue exactly one
#    linked refresh. No immediate Regen here; the queued universal pass owns the
#    single redraw and is already outside AutoCAD Undo recording.
# -----------------------------------------------------------------------------
$annotationPath = Required 'AnnotationScaleSyncCommands.cs'
$annotation = ReadText $annotationPath
$scaleBlockOld = @'
            _busy = true;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    ApplyCurrentScale(document);
                }
                LastScaleByDatabase[document.Database] = currentScale;
                // A scale change is a dependency change even when no drawing
                // entity fired ObjectModified. Recalculate all CE-linked outputs.
                UniversalDynamicRefreshManager.Queue();
                document.Editor.Regen();
            }
            catch
            {
                // Retry on the next idle cycle; scale changes can briefly occur
                // while Civil 3D owns the document or is rebuilding labels.
            }
            finally
            {
                _busy = false;
            }
'@ -replace "`n","`r`n"
$scaleBlockNew = @'
            _busy = true;
            bool undoRecordingDisabled = false;
            try
            {
                // Annotation-context maintenance is background bookkeeping.
                try
                {
                    document.Database.DisableUndoRecording(true);
                    undoRecordingDisabled = true;
                }
                catch { }
                using (DocumentLock documentLock = document.LockDocument())
                {
                    ApplyCurrentScale(document);
                }
                LastScaleByDatabase[document.Database] = currentScale;
                UniversalDynamicRefreshManager.Queue();
            }
            catch
            {
                // Retry on the next idle cycle; scale changes can briefly occur
                // while Civil 3D owns the document or is rebuilding labels.
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
'@ -replace "`n","`r`n"
$annotation = ReplaceRequired $annotation $scaleBlockOld $scaleBlockNew 'single-redraw annotation scale watcher'
WriteText $annotationPath $annotation

# -----------------------------------------------------------------------------
# 5. Post-command refresh manager: self-refreshing Survey commands have already
#    updated their outputs. Do not queue Universal + Platform a second time.
# -----------------------------------------------------------------------------
$automaticPath = Required 'AugustAutomaticRefreshManager.cs'
$automatic = ReadText $automaticPath
$autoOld = @'
            if (string.Equals(name, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase))
                return;
'@ -replace "`n","`r`n"
$autoNew = @'
            if (string.Equals(name, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_GRIDSETTINGOUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_GRIDSETTINGOUTDYNAMIC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_GRIDSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_VERTEXSETTINGOUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_VERTEXSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SURVEYREFRESHSAFE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase))
                return;
'@ -replace "`n","`r`n"
$automatic = ReplaceRequired $automatic $autoOld $autoNew 'post-command duplicate refresh exclusion'
WriteText $automaticPath $automatic

# -----------------------------------------------------------------------------
# 6. Survey Production Background entry: the final one-page menu must open the
#    requested CE-Background Tools preparation window, not the old XREF manager.
#    The dedicated background-menu repair runs immediately before this script;
#    retain a final guard here so later edits cannot regress it silently.
# -----------------------------------------------------------------------------
$centresPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath
$surveyStart = $centres.IndexOf('public void SurveyProduction()', [StringComparison]::Ordinal)
if ($surveyStart -lt 0) { throw 'SurveyProduction() missing during final stability validation.' }
$surveySlice = $centres.Substring($surveyStart,[Math]::Min(12000,$centres.Length-$surveyStart))
if (-not $surveySlice.Contains('"CE_BACKGROUNDPREPTOOLS"')) {
    throw 'Survey Production does not open CE-Background Tools under PREPARE.'
}
if ($surveySlice.Contains('"CE_BACKGROUNDTOOLS"')) {
    throw 'Survey Production still exposes CE-Background/XREF Utilities directly.'
}

# -----------------------------------------------------------------------------
# FINAL REGRESSION GUARDS
# -----------------------------------------------------------------------------
$final = ReadText $finalPath
if (-not $final.Contains('document.SendStringToExecute("CE_GRIDSETTINGOUTDYNAMIC ", true, false, true);')) {
    throw 'CE_GRIDSETTINGOUT is not routed to the dedicated dynamic Grid Setting-Out engine.'
}
if ($final.Contains('document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);')) {
    $gridStart = $final.IndexOf('public sealed class GridSettingOutCommands',[StringComparison]::Ordinal)
    if ($gridStart -ge 0) {
        $gridSlice = $final.Substring($gridStart,[Math]::Min(6000,$final.Length-$gridStart))
        if ($gridSlice.Contains('document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);')) {
            throw 'Grid Setting-Out still routes to Vertex Setting-Out.'
        }
    }
}
$universal = ReadText $universalPath
foreach ($required in @(
    'RefreshBackground(Document document)',
    'August18DynamicGridSettingOutCommands.RefreshAll(document)',
    'IsRefreshDependency(DBObject value)',
    'IsSelfRefreshingSurveyCommand(string command)',
    'DelaySeconds { get; set; } = 0.35;')) {
    if (-not $universal.Contains($required)) {
        throw "Universal Survey stability marker missing: $required"
    }
}
foreach ($forbidden in @(
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);',
    'CeTablePresentationManager.CenterCeTables(document);',
    'value is Entity || value is Xrecord || value is DBDictionary')) {
    if ($universal.Contains($forbidden)) {
        throw "Universal refresh still contains feedback/presentation mutation: $forbidden"
    }
}
$surveyField = ReadText $surveyFieldPath
$refreshStart = $surveyField.IndexOf($surveyRefreshMarker,[StringComparison]::Ordinal)
$refreshSlice = $surveyField.Substring($refreshStart,[Math]::Min(1800,$surveyField.Length-$refreshStart))
if (-not $refreshSlice.Contains('UniversalDynamicRefreshManager.RefreshBackground(document)')) {
    throw 'Survey linked/annotative refresh is not using the one coordinated background refresh.'
}
foreach ($forbidden in @(
    'RestoreCogoLabels(document',
    'CeTablePresentationManager.CenterCeTables(document)',
    'DynamicCoordinateLinkStore.Refresh(document)')) {
    if ($refreshSlice.Contains($forbidden)) {
        throw "Survey refresh still contains duplicate/label-moving operation: $forbidden"
    }
}

Write-Host 'Grid Setting-Out now keeps its own multi-polyline Perimeter / Full-grid workflow.' -ForegroundColor Green
Write-Host 'Grid and Vertex source edits are handled by one settled Universal refresh pass.' -ForegroundColor Green
Write-Host 'Survey linked/annotative refresh no longer restores or auto-solves COGO label offsets.' -ForegroundColor Green
Write-Host 'Automatic scale/link maintenance is excluded from Undo and uses one final redraw.' -ForegroundColor Green
Write-Host 'Survey Production PREPARE opens CE-Background Tools, with XREF utilities nested inside it.' -ForegroundColor Green
