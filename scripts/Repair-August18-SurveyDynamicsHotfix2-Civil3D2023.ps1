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
        throw "August 18 Survey dynamics hotfix source missing: $path"
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
    if (-not $text.Contains($old)) {
        throw "August 18 Survey dynamics hotfix anchor not found: $label"
    }
    return $text.Replace($old,$new)
}

# -----------------------------------------------------------------------------
# 1. The historical CE_GRIDSETTINGOUT command is a two-corner static COGO grid.
#    The visible Survey Production action promises selected/multiple polylines.
#    Route that historical front door to the current linked multi-source engine.
# -----------------------------------------------------------------------------
$finalPath = Required 'FinalAllCommentsCompletionCommands.cs'
$final = ReadText $finalPath
$gridOld = @'
        [CommandMethod("CE_TOOLS", "CE_GRIDSETTINGOUT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateGrid()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
'@ -replace "`n","`r`n"
$gridNew = @'
        [CommandMethod("CE_TOOLS", "CE_GRIDSETTINGOUT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateGrid()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            // CE_GRIDSETTINGOUT is now the linked multi-polyline/feature-line
            // front door. The Vertex Setting-Out engine supports pickset,
            // window and multiple selection with one continuous sequence.
            document.Editor.WriteMessage(
                "\nCE_GRIDSETTINGOUT: select one or more polylines/feature lines for linked dynamic setting-out.");
            document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);
            return;
'@ -replace "`n","`r`n"
$final = ReplaceRequired $final $gridOld $gridNew 'CE_GRIDSETTINGOUT multi-source front door'
WriteText $finalPath $final

# -----------------------------------------------------------------------------
# 2. Vertex Setting-Out COGO identity.
#    Civil 3D PointName is drawing-global and can open the modal "Duplicate Point
#    Name" dialog during background refresh/recreation. CE's dynamic identity is
#    already stored in RawDescription + the CE group/table link, so do not rewrite
#    the global PointName during automatic create/refresh. The visible CE labels
#    and tables keep P1/P2/etc through RawDescription and the linked record.
# -----------------------------------------------------------------------------
$vertexPath = Required 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath
$vertex = $vertex.Replace(
    '                try { point.PointName = record.PointName; } catch { }',
    '                // Keep Civil 3D PointName unmanaged to prevent duplicate-name prompts; CE name lives in RawDescription/link data.')
$vertex = $vertex.Replace(
    '                try { cogo.PointName = record.PointName; } catch { }',
    '                // Keep Civil 3D PointName unmanaged to prevent duplicate-name prompts; CE name lives in RawDescription/link data.')
WriteText $vertexPath $vertex

# Coordinate utilities use the same visible CE point-name convention. Keep the
# logical name in RawDescription instead of forcing Civil 3D's global PointName.
$coordinatePath = Required 'SurveyCoordinateWorkflowCommands.cs'
$coordinate = ReadText $coordinatePath
$coordinate = $coordinate.Replace(
    '                        point.PointName = pointNames[index];',
    '                        // RawDescription is the CE logical point name; do not force a duplicate Civil 3D PointName.')

# -----------------------------------------------------------------------------
# 3. Scale-aware linked coordinate tables.
#    Refresh must recompute the model height from the saved paper-mm setting and
#    CURRENT CANNOSCALE. Never preserve the old model-space cell height. Also
#    remove metre-hostile hard minima (5/18/20 drawing units).
# -----------------------------------------------------------------------------
$refreshHeightOld = @'
                double currentHeight = ReadCurrentTableTextHeight(
                    table,
                    database.Textsize);
                string currentTitle = ReadCurrentTableTitle(
                    table,
                    "LINKED COORDINATE REGISTER");
                PopulateTable(
                    table,
                    rows,
                    currentHeight,
                    currentTitle);
'@ -replace "`n","`r`n"
$refreshHeightNew = @'
                AnnotationOptions annotation = AnnotationSettingsStore.Read(database);
                double paperHeight = annotation == null
                    ? 2.0
                    : annotation.TextHeight;
                double currentHeight = ResolveScaleAwareTableHeight(
                    database,
                    paperHeight);
                string currentTitle = ReadCurrentTableTitle(
                    table,
                    "LINKED COORDINATE REGISTER");
                PopulateTable(
                    table,
                    rows,
                    currentHeight,
                    currentTitle);
'@ -replace "`n","`r`n"
$coordinate = ReplaceRequired $coordinate $refreshHeightOld $refreshHeightNew 'coordinate table scale-aware refresh height'
$coordinate = $coordinate.Replace(
    '            table.SetRowHeight(Math.Max(height * 2.4, 5.0));',
    '            table.SetRowHeight(Math.Max(height * 2.4, 0.001));')
$coordinate = $coordinate.Replace(
    '            table.SetColumnWidth(Math.Max(height * 7.0, 18.0));',
    '            table.SetColumnWidth(Math.Max(height * 7.0, 0.001));')
$coordinate = $coordinate.Replace(
    '            table.Columns[0].Width = Math.Max(height * 7.5, 20.0);',
    '            table.Columns[0].Width = Math.Max(height * 7.5, 0.001);')
WriteText $coordinatePath $coordinate

# -----------------------------------------------------------------------------
# 4. Survey Production Refresh must not restore stale COGO label offsets.
#    That command was re-applying the first stored MODEL-space offset after the
#    point and/or annotation scale changed, which is exactly why labels visibly
#    walked away from their COGO points after repeated refreshes.
# -----------------------------------------------------------------------------
$surveyFieldPath = Required 'August14SurveyFieldReviewCommands.cs'
$surveyField = ReadText $surveyFieldPath
$surveyField = $surveyField.Replace(
    '            try { August11SurveyRuntimeCommands.RestoreCogoLabels(document, null); } catch { }',
    '            // Do not restore stale model-space COGO label offsets during normal refresh. The dedicated manual restore command remains available.')
WriteText $surveyFieldPath $surveyField

# -----------------------------------------------------------------------------
# 5. Annotation-scale changes must trigger the linked-data refresh automatically.
#    AnnotationScaleSyncManager already detects CANNOSCALE changes reliably. Once
#    it adds the new annotation context, queue Universal Dynamic Refresh so vertex
#    tables, coordinate tables and linked point outputs recalculate themselves.
# -----------------------------------------------------------------------------
$annotationPath = Required 'AnnotationScaleSyncCommands.cs'
$annotation = ReadText $annotationPath
$scaleQueueOld = @'
                LastScaleByDatabase[document.Database] = currentScale;
                document.Editor.Regen();
'@ -replace "`n","`r`n"
$scaleQueueNew = @'
                LastScaleByDatabase[document.Database] = currentScale;
                // A scale change is a dependency change even when no drawing
                // entity fired ObjectModified. Recalculate all CE-linked outputs.
                UniversalDynamicRefreshManager.Queue();
                document.Editor.Regen();
'@ -replace "`n","`r`n"
$annotation = ReplaceRequired $annotation $scaleQueueOld $scaleQueueNew 'CANNOSCALE -> universal linked refresh queue'
WriteText $annotationPath $annotation

# -----------------------------------------------------------------------------
# 6. Site-grid coordinate labels are CE annotations too. Mark every generated
#    MText label annotative so AnnotationScaleSyncManager can add the active
#    scale context automatically without recreating the site-grid geometry.
# -----------------------------------------------------------------------------
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'
$siteGrid = ReadText $siteGridPath
$siteLabelOld = @'
            label.Contents = contents ?? string.Empty;
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Rotation = rotation;
            return label;
'@ -replace "`n","`r`n"
$siteLabelNew = @'
            label.Contents = contents ?? string.Empty;
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Rotation = rotation;
            PaperAnnotationScale.SetAnnotative(label);
            return label;
'@ -replace "`n","`r`n"
$siteGrid = ReplaceRequired $siteGrid $siteLabelOld $siteLabelNew 'annotative site-grid labels'
WriteText $siteGridPath $siteGrid

# -----------------------------------------------------------------------------
# Final strict guards.
# -----------------------------------------------------------------------------
$final = ReadText $finalPath
if (-not $final.Contains('document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);')) {
    throw 'CE_GRIDSETTINGOUT is not routed to the multi-source linked engine.'
}
$vertex = ReadText $vertexPath
if ($vertex.Contains('PointName = record.PointName')) {
    throw 'Vertex Setting-Out still writes duplicate-prone Civil 3D PointName values.'
}
$coordinate = ReadText $coordinatePath
if ($coordinate.Contains('point.PointName = pointNames[index];')) {
    throw 'Survey coordinate utilities still force duplicate-prone Civil 3D PointName values.'
}
if (-not $coordinate.Contains('double currentHeight = ResolveScaleAwareTableHeight(')) {
    throw 'Linked coordinate table refresh is not recalculating current scale-aware text height.'
}
foreach ($badMinimum in @(
    'Math.Max(height * 2.4, 5.0)',
    'Math.Max(height * 7.0, 18.0)',
    'Math.Max(height * 7.5, 20.0)')) {
    if ($coordinate.Contains($badMinimum)) {
        throw "Linked coordinate table still contains fixed drawing-unit minimum: $badMinimum"
    }
}
$surveyField = ReadText $surveyFieldPath
if ($surveyField.Contains('RestoreCogoLabels(document, null)')) {
    throw 'Survey Production Refresh still restores stale COGO label offsets.'
}
$annotation = ReadText $annotationPath
if (-not $annotation.Contains('UniversalDynamicRefreshManager.Queue();')) {
    throw 'Annotation-scale change does not queue linked CE refresh.'
}
$siteGrid = ReadText $siteGridPath
if (-not $siteGrid.Contains('PaperAnnotationScale.SetAnnotative(label);')) {
    throw 'Site-grid coordinate labels are not annotative.'
}

Write-Host 'Grid Setting-Out now routes directly to linked multi-polyline / feature-line Vertex Setting-Out.' -ForegroundColor Green
Write-Host 'Duplicate Civil 3D PointName prompts are removed from CE dynamic COGO create/refresh; CE names stay in RawDescription/link data.' -ForegroundColor Green
Write-Host 'Moved source geometry now drives linked COGO points/tables without stale COGO label-offset restoration.' -ForegroundColor Green
Write-Host 'Coordinate and vertex tables now refresh automatically after annotation-scale changes.' -ForegroundColor Green
Write-Host 'Site-grid coordinate labels are now annotative.' -ForegroundColor Green
