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
    if (-not $text.Contains($old)) { throw "August 18 Survey stability anchor not found: $label" }
    return $text.Replace($old,$new)
}
function ReplaceMethod([string]$text,[string]$marker,[string]$replacement,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker not found: $label" }
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

# 1. Dedicated dynamic Grid Setting-Out front door.
$gridEnginePath = Required 'August18DynamicGridSettingOutCommands.cs'
$gridEngine = ReadText $gridEnginePath
foreach ($marker in @(
    '"CE_GRIDSETTINGOUTDYNAMIC"',
    '"Perimeter", "Full grid"',
    'Select MULTIPLE closed grid/site polylines',
    'CE_DYNAMIC_GRID_TABLE',
    'internal static int RefreshAll(Document document)',
    'point.RawDescription = record.Name;')) {
    if (-not $gridEngine.Contains($marker)) { throw "Dynamic Grid engine marker missing: $marker" }
}

$finalPath = Required 'FinalAllCommentsCompletionCommands.cs'
$final = ReadText $finalPath
$gridOld = @'
            document.Editor.WriteMessage(
                "\nCE_GRIDSETTINGOUT: select one or more polylines/feature lines for linked dynamic setting-out.");
            document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);
'@ -replace "`n","`r`n"
$gridNew = @'
            document.Editor.WriteMessage(
                "\nCE_GRIDSETTINGOUT: select one or more closed polylines, then choose Perimeter or Full grid.");
            document.SendStringToExecute("CE_GRIDSETTINGOUTDYNAMIC ", true, false, true);
'@ -replace "`n","`r`n"
$final = ReplaceRequired $final $gridOld $gridNew 'Grid Setting-Out dedicated dynamic front door'
WriteText $finalPath $final

# 2. One settled Universal refresh owner. Normal refresh updates dependency data;
# it does not re-run label overlap/style/table presentation solvers.
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

$refreshOld = @'
        internal static UniversalRefreshResult RefreshNow(Document document)
        {
            return RefreshNow(document, false);
        }
'@ -replace "`n","`r`n"
$refreshNew = @'
        internal static UniversalRefreshResult RefreshNow(Document document)
        {
            return RefreshNow(document, true);
        }

        internal static UniversalRefreshResult RefreshBackground(Document document)
        {
            return RefreshNow(document, true);
        }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $refreshOld $refreshNew 'undo-suppressed Universal refresh entry'

# These operations can change visual positions or force table redraws. Keep them
# available through their dedicated/manual commands, but never run them as part
# of ordinary automatic source-geometry refresh.
foreach ($call in @(
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);')) {
    $universal = $universal.Replace($call,'/* point style reapply intentionally excluded from automatic refresh */')
}
foreach ($call in @(
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);')) {
    $universal = $universal.Replace($call,'/* annotation overlap movement intentionally excluded from automatic refresh */')
}
$universal = $universal.Replace(
    'CeTablePresentationManager.CenterCeTables(document);',
    '/* table presentation movement intentionally excluded from automatic refresh */')

$coordOld = @'
                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }
                catch { result.Warnings++; }
'@ -replace "`n","`r`n"
$coordNew = @'
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
$universal = ReplaceRequired $universal $coordOld $coordNew 'dynamic Grid and Survey linked tables in Universal refresh'

$predicateOld = @'
        private static bool IsSiteGridCommand(string command)
        {
            return string.Equals(command, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase);
        }
'@ -replace "`n","`r`n"
$predicateNew = @'
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
$universal = ReplaceRequired $universal $predicateOld $predicateNew 'self-refreshing Survey command predicate'
$universal = $universal.Replace('if (IsSiteGridCommand(command))','if (IsSelfRefreshingSurveyCommand(command))')

$changedOld = @'
            DBObject value = e.DBObject;
            if (value is Entity || value is Xrecord || value is DBDictionary ||
                value is CogoPoint || value is Pipe || value is Structure ||
                value is Autodesk.Civil.DatabaseServices.Network)
                Queue();
'@ -replace "`n","`r`n"
$changedNew = @'
            DBObject value = e.DBObject;
            if (IsRefreshDependency(value)) Queue();
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $changedOld $changedNew 'dependency-only ObjectModified queue'

$changedMarker = @'
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

            string name = value.GetType().Name ?? string.Empty;
            return name.IndexOf("FeatureLine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Alignment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs e)
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $changedMarker $dependencyHelper 'refresh dependency filter helper'

$idleOld = '            RefreshNow(active, true);'
$idleNew = @'
            UniversalRefreshResult result = RefreshNow(active, true);
            if (result.LinkedEngineRuns > 0 || result.VertexTables > 0 ||
                result.JunctionLabels > 0 || result.MetadataAttributes > 0)
            {
                try { active.Editor.Regen(); } catch { }
            }
'@ -replace "`n","`r`n"
$universal = ReplaceRequired $universal $idleOld $idleNew 'single final automatic Regen'
WriteText $universalPath $universal

# 3. Survey Linked/Annotative Refresh becomes one Universal background pass only.
$surveyFieldPath = Required 'August14SurveyFieldReviewCommands.cs'
$surveyField = ReadText $surveyFieldPath
$surveyMarker = '        [CommandMethod("CE_TOOLS", "CE_SURVEYREFRESHSAFE", CommandFlags.Modal | CommandFlags.Redraw)]'
$surveyReplacement = @'
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
                document.Editor.WriteMessage("\nCE_SURVEYREFRESHSAFE stopped. {0}", exception.Message);
            }
        }
'@ -replace "`n","`r`n"
$surveyField = ReplaceMethod $surveyField $surveyMarker $surveyReplacement 'CE_SURVEYREFRESHSAFE'
WriteText $surveyFieldPath $surveyField

# 4. CANNOSCALE changes: update annotation contexts silently, queue one Universal
# pass and let that pass own the only redraw.
$annotationPath = Required 'AnnotationScaleSyncCommands.cs'
$annotation = ReadText $annotationPath
$scaleOld = @'
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
$scaleNew = @'
            _busy = true;
            bool undoRecordingDisabled = false;
            try
            {
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
$annotation = ReplaceRequired $annotation $scaleOld $scaleNew 'silent annotation-scale maintenance'
WriteText $annotationPath $annotation

# 5. Do not queue a second Universal + Platform refresh after self-refreshing
# Survey commands have already completed their own linked update.
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
$automatic = ReplaceRequired $automatic $autoOld $autoNew 'duplicate post-command Survey refresh exclusion'
WriteText $automaticPath $automatic

# Final guards.
$centres = ReadText (Required 'August14StructuredDisciplineProductionCentres.cs')
if (-not $centres.Contains('"CE_BACKGROUNDPREPTOOLS"')) {
    throw 'Survey Production Background Tools final menu marker is missing.'
}
$final = ReadText $finalPath
$gridClassStart = $final.IndexOf('public sealed class GridSettingOutCommands',[StringComparison]::Ordinal)
if ($gridClassStart -lt 0) { throw 'GridSettingOutCommands class missing.' }
$gridSlice = $final.Substring($gridClassStart,[Math]::Min(5500,$final.Length-$gridClassStart))
if (-not $gridSlice.Contains('CE_GRIDSETTINGOUTDYNAMIC')) {
    throw 'CE_GRIDSETTINGOUT does not route to the dedicated dynamic Grid engine.'
}
if ($gridSlice.Contains('document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);')) {
    throw 'CE_GRIDSETTINGOUT still routes to Vertex Setting-Out.'
}
$universal = ReadText $universalPath
foreach ($required in @(
    'RefreshBackground(Document document)',
    'August18DynamicGridSettingOutCommands.RefreshAll(document)',
    'IsRefreshDependency(DBObject value)',
    'IsSelfRefreshingSurveyCommand(string command)',
    'DelaySeconds { get; set; } = 0.35;')) {
    if (-not $universal.Contains($required)) { throw "Universal stability marker missing: $required" }
}
foreach ($forbidden in @(
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'CeTablePresentationManager.CenterCeTables(document);',
    'value is Entity || value is Xrecord || value is DBDictionary')) {
    if ($universal.Contains($forbidden)) { throw "Automatic refresh still contains feedback/layout operation: $forbidden" }
}
$surveyField = ReadText $surveyFieldPath
$surveyStart = $surveyField.IndexOf($surveyMarker,[StringComparison]::Ordinal)
if ($surveyStart -lt 0) { throw 'Survey linked/annotative refresh command missing after replacement.' }
$surveySlice = $surveyField.Substring($surveyStart,[Math]::Min(1600,$surveyField.Length-$surveyStart))
if (-not $surveySlice.Contains('UniversalDynamicRefreshManager.RefreshBackground(document)')) {
    throw 'Survey linked/annotative refresh is not using one coordinated background refresh.'
}
foreach ($forbidden in @(
    'RestoreCogoLabels(document',
    'CeTablePresentationManager.CenterCeTables(document)',
    'DynamicCoordinateLinkStore.Refresh(document)')) {
    if ($surveySlice.Contains($forbidden)) { throw "Survey refresh still contains duplicate/label-moving operation: $forbidden" }
}

Write-Host 'Grid Setting-Out now has its own MULTIPLE-polyline Perimeter / Full-grid workflow.' -ForegroundColor Green
Write-Host 'Grid/Vertex linked updates now use one settled dependency refresh path instead of repeated feedback passes.' -ForegroundColor Green
Write-Host 'Survey Linked/Annotative Refresh no longer moves COGO labels or repeats table presentation operations.' -ForegroundColor Green
Write-Host 'Automatic annotation-scale maintenance is excluded from Undo and redraws only through the coordinated refresh.' -ForegroundColor Green
Write-Host 'Survey Production PREPARE opens CE-Background Tools; the old XREF utilities remain nested inside it.' -ForegroundColor Green
