[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function SourcePath([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "September 10 source missing: $path" }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n", "`r`n"
    $new = $new -replace "`r?`n", "`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "September 10 anchor missing: $label" }
    return $text.Replace($old,$new)
}
function InsertBeforeOnce([string]$text,[string]$anchor,[string]$insert,[string]$marker,[string]$label) {
    if ($text.Contains($marker)) { return $text }
    $index = $text.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "September 10 insertion anchor missing: $label" }
    return $text.Insert($index,($insert -replace "`r?`n","`r`n"))
}
function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "September 10 method missing ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "September 10 opening brace missing: $label" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "September 10 closing brace missing: $label" }
    return $text.Substring(0,$open+1) + "`r`n" + ($body -replace "`r?`n","`r`n").Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}

# -----------------------------------------------------------------------------
# Sewer engineering audit: the selected surface now becomes the network reference
# surface and existing Civil 3D rules are applied before cover/slope/drop checks.
# Cover uses the pipe crown (outer radius); manhole drops use pipe inverts.
# -----------------------------------------------------------------------------
$auditPath = SourcePath 'August24FieldCompletionCommands.cs'
$audit = ReadText $auditPath
$audit = $audit.Replace(
    'Review full-network cover, pipe slopes and structure drops. The command reports observed ranges and violations without modifying the network.',
    'Review full-network cover, pipe slopes and structure drops. By default the selected surface is linked to every pipe/structure and existing Civil 3D part rules are applied before the audit.')

$audit = ReplaceRequired $audit @'
            settings.AddPositiveInteger("Samples", "01 Cover", "Cover samples per pipe", 10, "Number of equally spaced cover samples along each pipe.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
'@ @'
            settings.AddPositiveInteger("Samples", "01 Cover", "Cover samples per pipe", 10, "Number of equally spaced cover samples along each pipe.");
            settings.AddChoice("PrepareNetwork", "04 Rules", "Link selected surface + apply rules", "Yes",
                "Yes links the selected surface to every pipe and structure and applies each part's current Civil 3D rule set before auditing cover, slope and drops.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
'@ 'sewer audit surface/rules option'

$audit = ReplaceRequired $audit @'
            SurfaceChoice surfaceChoice = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            SewerAuditResult audit = AuditNetwork(document, networkId, surfaceChoice == null ? ObjectId.Null : surfaceChoice.ObjectId, settings);
'@ @'
            SurfaceChoice surfaceChoice = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            int linkedPipes = 0;
            int linkedStructures = 0;
            int ruleFailures = 0;
            string surfacePreparationError = string.Empty;
            bool prepareNetwork = string.Equals(settings.Text("PrepareNetwork"), "Yes", StringComparison.OrdinalIgnoreCase);
            if (prepareNetwork && surfaceChoice != null)
            {
                bool prepared = September09SewerSurfaceRulesRuntime.LinkExistingPartsToSurface(
                    document,
                    networkId,
                    surfaceChoice.ObjectId,
                    ObjectId.Null,
                    ObjectId.Null,
                    out linkedPipes,
                    out linkedStructures,
                    out ruleFailures,
                    out surfacePreparationError);
                if (!prepared)
                    document.Editor.WriteMessage("\nCE_SEWAUDITLIMITS surface/rule preparation warning: {0}", surfacePreparationError);
            }
            SewerAuditResult audit = AuditNetwork(document, networkId, surfaceChoice == null ? ObjectId.Null : surfaceChoice.ObjectId, settings);
'@ 'sewer audit pre-audit surface linking'

$audit = ReplaceRequired $audit @'
                    Pair("Structures checked", audit.Structures.ToString(CultureInfo.CurrentCulture)),
                    Pair("Cover range", RangeText(audit.MinCover, audit.MaxCover)),
'@ @'
                    Pair("Structures checked", audit.Structures.ToString(CultureInfo.CurrentCulture)),
                    Pair("Surface-linked pipes", linkedPipes.ToString(CultureInfo.CurrentCulture)),
                    Pair("Surface-linked structures", linkedStructures.ToString(CultureInfo.CurrentCulture)),
                    Pair("Rule application failures", ruleFailures.ToString(CultureInfo.CurrentCulture)),
                    Pair("Cover range", RangeText(audit.MinCover, audit.MaxCover)),
'@ 'sewer audit preparation report'

$audit = ReplaceRequired $audit @'
                    if (pipe.StartStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.StartStructureId, pipe.StartPoint.Z);
                    if (pipe.EndStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.EndStructureId, pipe.EndPoint.Z);
'@ @'
                    double invertRadius = ReadPipeInnerRadius(pipe);
                    if (pipe.StartStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.StartStructureId, pipe.StartPoint.Z - invertRadius);
                    if (pipe.EndStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.EndStructureId, pipe.EndPoint.Z - invertRadius);
'@ 'structure drops from pipe inverts'

$audit = $audit.Replace(
    'foreach (string name in new[] { "InnerDiameterOrWidth", "OuterDiameterOrWidth" })',
    'foreach (string name in new[] { "OuterDiameterOrWidth", "InnerDiameterOrWidth" })')

if (-not $audit.Contains('private static double ReadPipeInnerRadius(CivilPipe pipe)')) {
    $innerRadius = @'
        private static double ReadPipeInnerRadius(CivilPipe pipe)
        {
            foreach (string name in new[] { "InnerDiameterOrWidth", "OuterDiameterOrWidth" })
            {
                try
                {
                    PropertyInfo property = pipe.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (property == null || !property.CanRead) continue;
                    double diameter = Convert.ToDouble(property.GetValue(pipe, null), CultureInfo.InvariantCulture);
                    if (diameter > 0.0) return diameter * 0.5;
                }
                catch { }
            }
            return 0.0;
        }

'@
    $anchor = '        // -----------------------------------------------------------------' + "`r`n" + '        // Helpers - feature relationships'
    $at = $audit.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'September 10 ReadPipeInnerRadius insertion anchor missing.' }
    $audit = $audit.Insert($at,($innerRadius -replace "`r?`n","`r`n"))
}
WriteText $auditPath $audit

# -----------------------------------------------------------------------------
# Sewer branch sequence: side branches must number from their free/upstream end
# toward the already-owned junction/main branch. The shared junction keeps its
# original branch number, while MH2.1/P2.1 begins at the side-branch free end.
# -----------------------------------------------------------------------------
$sequencePath = SourcePath 'SewerSequenceCommands.cs'
$sequence = ReadText $sequencePath
if (-not $sequence.Contains('bool firstEndpointAlreadyAssigned = assignedStructures.Contains(selected.NodeIds[0]);')) {
    $orientation = @'
                if (result.Count > 0 && selected.NodeIds.Count > 1)
                {
                    bool firstEndpointAlreadyAssigned = assignedStructures.Contains(selected.NodeIds[0]);
                    bool lastEndpointAlreadyAssigned = assignedStructures.Contains(selected.NodeIds[selected.NodeIds.Count - 1]);
                    if (firstEndpointAlreadyAssigned && !lastEndpointAlreadyAssigned)
                    {
                        ReverseInPlace(selected.NodeIds);
                        ReverseInPlace(selected.EdgeIds);
                    }
                }

'@
    $anchor = '                var structuresToRename = new List<ObjectId>();'
    $at = $sequence.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'September 10 side-branch sequence insertion anchor missing.' }
    $sequence = $sequence.Insert($at,($orientation -replace "`r?`n","`r`n"))
}
WriteText $sequencePath $sequence

$dynamicSequencePath = SourcePath 'SewerNetworkDynamicSequenceManager.cs'
$dynamicSequence = ReadText $dynamicSequencePath
$dynamicSequence = ReplaceRequired $dynamicSequence @'
                    if (branch == null || branch.Edges.Count == 0) continue;
                    OrientHighToLow(branch, topology);
                    result.Add(branch);
'@ @'
                    if (branch == null || branch.Edges.Count == 0) continue;
                    // WalkBranchSegment begins at the already-owned junction and walks
                    // outward. Reverse every side branch so .1 starts at its free end
                    // and numbering progresses toward the parent/main branch.
                    branch.Nodes.Reverse();
                    branch.Edges.Reverse();
                    result.Add(branch);
'@ 'dynamic sewer side-branch orientation'
WriteText $dynamicSequencePath $dynamicSequence

# -----------------------------------------------------------------------------
# Multiple Dimensions: add an Enabled/Disabled dynamic option. The final staged
# August 25 line/polyline/feature-line implementation remains the geometry engine;
# the new manager only persists links and asks it to regenerate when required.
# -----------------------------------------------------------------------------
$multiPath = SourcePath 'MultiDimensionCommands.cs'
$multi = ReadText $multiPath
if (-not $multi.Contains('"Dynamic", "03 Dynamic", "Dynamic update"')) {
    $dynamicSetting = @'
            settings.AddChoice(
                "Dynamic", "03 Dynamic", "Dynamic update", "Enabled",
                "Enabled keeps dimensions linked to selected lines, polylines and Civil 3D feature lines. Grip edits, MOVE/STRETCH and annotation-scale changes rebuild the dimensions automatically.",
                new[] { "Enabled", "Disabled" });
'@
    $anchor = '            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;'
    $at = $multi.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'September 10 Multiple Dimensions settings insertion anchor missing.' }
    $multi = $multi.Insert($at,($dynamicSetting -replace "`r?`n","`r`n"))
}

if (-not $multi.Contains('DynamicMultiDimensionManager.BeginCommand(')) {
    $begin = @'
            DynamicMultiDimensionManager.BeginCommand(
                document,
                !string.Equals(settings.Text("Dynamic"), "Disabled", StringComparison.OrdinalIgnoreCase),
                mode,
                settings.Double("Offset", 8.0),
                settings.Double("ArcLeader", 6.0));

'@
    $anchor = '            int sources = 0;'
    $at = $multi.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'September 10 Multiple Dimensions BeginCommand anchor missing.' }
    $multi = $multi.Insert($at,($begin -replace "`r?`n","`r`n"))
}

$multi = ReplaceRequired $multi @'
                        if (entity == null) { skippedSources++; continue; }

                        Line sourceLine = entity as Line;
'@ @'
                        if (entity == null) { skippedSources++; continue; }
                        DynamicMultiDimensionManager.ClearCurrentSource();

                        Line sourceLine = entity as Line;
'@ 'Multiple Dimensions per-source capture reset'

$multi = ReplaceRequired $multi @'
                        if (sourceLine != null)
                        {
                            sources++;
                            ProcessLine(document.Database, transaction, space, sourceLine, mode, styleId, offset,
'@ @'
                        if (sourceLine != null)
                        {
                            sources++;
                            DynamicMultiDimensionManager.BeginSource(transaction, sourceLine, mode, styleId);
                            ProcessLine(document.Database, transaction, space, sourceLine, mode, styleId, offset,
'@ 'Line dynamic dimension capture'

$multi = ReplaceRequired $multi @'
                        if (polyline != null)
                        {
                            sources++;
                            ProcessPolyline(
'@ @'
                        if (polyline != null)
                        {
                            sources++;
                            DynamicMultiDimensionManager.BeginSource(transaction, polyline, mode, styleId);
                            ProcessPolyline(
'@ 'Polyline dynamic dimension capture'

$multi = ReplaceRequired $multi @'
                        if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
                        {
                            sources++;
                            ProcessFeatureLine(
'@ @'
                        if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
                        {
                            sources++;
                            DynamicMultiDimensionManager.BeginSource(transaction, featureLine, mode, styleId);
                            ProcessFeatureLine(
'@ 'FeatureLine dynamic dimension capture'

if (-not $multi.Contains('internal static int RebuildDynamicSource(')) {
$rebuildMethod = @'
        internal static int RebuildDynamicSource(
            Document document,
            ObjectId sourceId,
            string mode,
            string styleName,
            double offsetPaper,
            double leaderPaper)
        {
            if (document == null || sourceId.IsNull) return -1;
            int created = 0;
            int skipped = 0;
            int failed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity;
                if (source == null) return -1;
                DimStyleTable styles = transaction.GetObject(
                    document.Database.DimStyleTableId,
                    OpenMode.ForRead,
                    false) as DimStyleTable;
                if (styles == null || string.IsNullOrWhiteSpace(styleName) || !styles.Has(styleName)) return -1;
                ObjectId styleId = styles[styleName];
                BlockTableRecord space = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return -1;

                double offset = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(offsetPaper, 0.0));
                double leader = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(leaderPaper, 0.0));
                DynamicMultiDimensionManager.BeginRebuildSource(
                    transaction, source, mode, styleId, offsetPaper, leaderPaper);

                Line line = source as Line;
                if (line != null)
                    ProcessLine(document.Database, transaction, space, line, mode, styleId, offset,
                        ref created, ref skipped, ref failed);
                else
                {
                    Polyline polyline = source as Polyline;
                    if (polyline != null)
                        ProcessPolyline(document.Database, transaction, space, polyline, mode, styleId, offset, leader,
                            ref created, ref skipped, ref failed);
                    else
                    {
                        CivilFeatureLine featureLine = source as CivilFeatureLine;
                        if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
                            ProcessFeatureLine(document.Database, transaction, space, featureLine, mode, styleId, offset,
                                ref created, ref skipped, ref failed);
                        else return -1;
                    }
                }
                transaction.Commit();
            }
            DynamicMultiDimensionManager.ClearCurrentSource();
            return created;
        }

'@
    $anchor = '        private static void ProcessLine('
    $at = $multi.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'September 10 Multiple Dimensions ProcessLine insertion anchor missing.' }
    $multi = $multi.Insert($at,($rebuildMethod -replace "`r?`n","`r`n"))
}

# AddDimension is rewritten by several historical staged passes. Patch it
# semantically rather than depending on one exact body shape.
$methodMarker = '        private static void AddDimension('
$methodStart = $multi.IndexOf($methodMarker,[StringComparison]::Ordinal)
if ($methodStart -lt 0) { throw 'September 10 Multiple Dimensions AddDimension marker missing.' }
$methodOpen = $multi.IndexOf('{',$methodStart)
$depth = 0; $methodClose = -1
for ($i=$methodOpen; $i -lt $multi.Length; $i++) {
    if ($multi[$i] -eq '{') { $depth++ }
    elseif ($multi[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $methodClose=$i; break }
    }
}
if ($methodClose -lt 0) { throw 'September 10 Multiple Dimensions AddDimension closing brace missing.' }
$method = $multi.Substring($methodStart,$methodClose-$methodStart+1)
$createdAt = $method.LastIndexOf('created++;',[StringComparison]::Ordinal)
if ($createdAt -lt 0) { throw 'September 10 Multiple Dimensions AddDimension created++ anchor missing.' }
$lineStart = $method.LastIndexOf("`n",$createdAt)
$indentStart = if ($lineStart -lt 0) { 0 } else { $lineStart + 1 }
$indent = $method.Substring($indentStart,$createdAt-$indentStart)
$insertion = ''
if (-not $method.Contains('PaperAnnotationScale.SetAnnotative(dimension);'))
    { $insertion += $indent + 'PaperAnnotationScale.SetAnnotative(dimension);' + "`r`n" }
if (-not $method.Contains('DynamicMultiDimensionManager.CaptureOutput(transaction, dimension);'))
    { $insertion += $indent + 'DynamicMultiDimensionManager.CaptureOutput(transaction, dimension);' + "`r`n" }
if ($insertion.Length -gt 0) {
    $method = $method.Insert($indentStart,$insertion)
    $multi = $multi.Substring(0,$methodStart) + $method + $multi.Substring($methodClose+1)
}
WriteText $multiPath $multi

# Universal refresh directly calls the dynamic dimension manager under the same
# background undo suppression used by all other linked CE outputs.
$universalPath = SourcePath 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath
$universal = ReplaceRequired $universal @'
                try { result.SegmentLabelSources += DynamicSegmentLabelManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }
'@ @'
                try { result.SegmentLabelSources += DynamicSegmentLabelManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { DynamicMultiDimensionManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }
'@ 'universal dynamic Multiple Dimensions refresh hook'
WriteText $universalPath $universal

# Annotation-scale sync is itself an Idle/background transaction. Suppress undo
# recording there too; the explicit CE_ANNOSCALESYNC command remains undoable.
$annoPath = SourcePath 'AnnotationScaleSyncCommands.cs'
$anno = ReadText $annoPath
if (-not $anno.Contains('bool undoRecordingDisabled = false;')) {
$annoBody = @'
            if (_busy ||
                (DateTime.UtcNow - _lastPollUtc).TotalMilliseconds < 500.0)
                return;
            _lastPollUtc = DateTime.UtcNow;

            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            string currentScale = ReadCurrentScaleName();
            string previousScale;
            if (LastScaleByDatabase.TryGetValue(
                    document.Database,
                    out previousScale) &&
                string.Equals(
                    previousScale,
                    currentScale,
                    StringComparison.OrdinalIgnoreCase))
                return;

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
                document.Editor.Regen();
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
'@
    $anno = ReplaceMethodBody $anno '        private static void OnIdle(object sender, EventArgs eventArgs)' $annoBody 'annotation-scale idle undo suppression'
}
WriteText $annoPath $anno

# Final guards: fail the build rather than silently shipping an older behavior.
$checks = @{
    $auditPath = @('"PrepareNetwork"','September09SewerSurfaceRulesRuntime.LinkExistingPartsToSurface(','ReadPipeInnerRadius(pipe)','"OuterDiameterOrWidth", "InnerDiameterOrWidth"');
    $sequencePath = @('firstEndpointAlreadyAssigned','ReverseInPlace(selected.NodeIds);');
    $dynamicSequencePath = @('branch.Nodes.Reverse();','branch.Edges.Reverse();');
    $multiPath = @('"Dynamic", "03 Dynamic", "Dynamic update"','DynamicMultiDimensionManager.BeginCommand(','DynamicMultiDimensionManager.BeginSource(transaction, sourceLine','internal static int RebuildDynamicSource(','DynamicMultiDimensionManager.CaptureOutput(transaction, dimension);','PaperAnnotationScale.SetAnnotative(dimension);');
    $universalPath = @('DynamicMultiDimensionManager.RefreshAll(document);');
    $annoPath = @('bool undoRecordingDisabled = false;','document.Database.DisableUndoRecording(true);','document.Database.DisableUndoRecording(false);')
}
foreach ($entry in $checks.GetEnumerator()) {
    $text = ReadText $entry.Key
    foreach ($marker in $entry.Value) {
        if (-not $text.Contains($marker)) { throw "September 10 final marker missing in $($entry.Key): $marker" }
    }
}

Write-Host 'September 10 Civil 3D field corrections applied.' -ForegroundColor Green
Write-Host 'Sewer audit now links the selected surface/applies rules, side branches sequence toward the main, and Multiple Dimensions can refresh dynamically without background Undo pollution.' -ForegroundColor Green
