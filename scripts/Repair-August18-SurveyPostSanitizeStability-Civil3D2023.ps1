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
        throw "Post-sanitize Survey stability source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function Keep-FirstExactBlock([string]$text,[string]$block) {
    if ([string]::IsNullOrEmpty($block)) { return $text }
    $first = $text.IndexOf($block,[StringComparison]::Ordinal)
    if ($first -lt 0) { return $text }
    $next = $text.IndexOf($block,$first + $block.Length,[StringComparison]::Ordinal)
    while ($next -ge 0) {
        $text = $text.Remove($next,$block.Length)
        $next = $text.IndexOf($block,$first + $block.Length,[StringComparison]::Ordinal)
    }
    return $text
}
function Insert-AfterFirst([string]$text,[string]$marker,[string]$addition,[string]$label) {
    if ($text.Contains($addition.Trim())) { return $text }
    $index = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Post-sanitize insertion anchor not found: $label" }
    $end = $index + $marker.Length
    return $text.Substring(0,$end) + $addition + $text.Substring($end)
}
function Insert-BeforeFirst([string]$text,[string]$marker,[string]$addition,[string]$label) {
    if ($text.Contains($addition.Trim())) { return $text }
    $index = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Post-sanitize insertion anchor not found: $label" }
    return $text.Substring(0,$index) + $addition + $text.Substring($index)
}

# -----------------------------------------------------------------------------
# 1. Universal Dynamic is dependency refresh only. Late August 14 recovery passes
#    are allowed to restore their historical hooks for compatibility, but none of
#    the label-restoration / presentation hooks may survive into the final build.
# -----------------------------------------------------------------------------
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath

foreach ($statement in @(
    'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);',
    'August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);',
    'AnnotationScaleSyncManager.ApplyCurrentScale(document);')) {
    $pattern = '(?m)^\s*try\s*\{\s*' + [regex]::Escape($statement) + '\s*\}\s*\r?\n\s*catch\s*\{\s*result\.Warnings\+\+;\s*\}\s*\r?\n?'
    $universal = [regex]::Replace($universal,$pattern,'')
}

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

foreach ($statement in @(
    'DynamicCoordinateLinkStore.Refresh(document);',
    'SurfaceComparisonLinkStore.RefreshAll(document);',
    'MultiSurfaceComparisonTableStore.RefreshAll(document);',
    'LinkedSurfaceReportTableStore.RefreshAll(document);')) {
    $block = '                try { ' + $statement + ' }' + "`r`n" +
             '                catch { result.Warnings++; }' + "`r`n"
    $universal = Keep-FirstExactBlock $universal $block
}
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 2. COGO project-style synchronization may style CE setting-out points, but it
#    must never restore an old stored label offset for Vertex or Dynamic Grid COGO.
# -----------------------------------------------------------------------------
$cogoPath = Required 'CogoPointProjectStyleCommands.cs'
$cogo = ReadText $cogoPath

$skipMarker = '                        TrySetLabelVisible(point);'
$skipAddition = @'
                        // CE dynamic setting-out points own their label attachment.
                        // Apply the selected styles, but never restore a historical
                        // CE_COGO_LABEL_OFFSET that can pull the label away from
                        // the generated COGO marker after a Vertex/Grid refresh.
                        if (IsCeSettingOutPoint(point, transaction))
                            continue;
'@ -replace "`n","`r`n"
if (-not $cogo.Contains('if (IsCeSettingOutPoint(point, transaction))')) {
    $cogo = Insert-AfterFirst $cogo $skipMarker ("`r`n" + $skipAddition.TrimEnd()) 'COGO setting-out offset exclusion'
}

$helperMarker = '        internal static int ResolveOverlaps(Document document, ISet<ObjectId> restrictedPointIds = null)'
$cogoHelpers = @'
        internal static Vector3d ReadStableSettingOutLabelOffset(
            Database database,
            CivilCogoPoint point,
            Point3d anchor)
        {
            double small = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 1.5),
                0.001);
            Vector3d fallback = new Vector3d(small, small, 0.0);
            if (database == null || point == null) return fallback;
            try
            {
                Vector3d current = point.LabelLocation - anchor;
                double maximum = Math.Max(
                    PaperAnnotationScale.ModelDistance(database, 8.0),
                    small * 4.0);
                if (current.Length > small * 0.10 &&
                    current.Length <= maximum)
                    return new Vector3d(current.X, current.Y, 0.0);
            }
            catch { }
            return fallback;
        }

        internal static void SetSettingOutLabelLocation(
            CivilCogoPoint point,
            Point3d anchor,
            Vector3d offset)
        {
            if (point == null) return;
            try
            {
                point.LabelLocation = new Point3d(
                    anchor.X + offset.X,
                    anchor.Y + offset.Y,
                    anchor.Z);
            }
            catch { }
        }

        private static bool IsCeSettingOutPoint(
            CivilCogoPoint point,
            Transaction transaction)
        {
            if (point == null || transaction == null) return false;
            try
            {
                ResultBuffer vertex = point.GetXDataForApplication(
                    "CE_VERTEX_SETTINGOUT");
                if (vertex != null) return true;
            }
            catch { }
            try
            {
                if (point.ExtensionDictionary.IsNull) return false;
                DBDictionary dictionary = transaction.GetObject(
                    point.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                return dictionary != null &&
                    dictionary.Contains("CE_DYNAMIC_GRID_POINT");
            }
            catch
            {
                return false;
            }
        }

'@ -replace "`n","`r`n"
if (-not $cogo.Contains('ReadStableSettingOutLabelOffset(')) {
    $cogo = Insert-BeforeFirst $cogo $helperMarker $cogoHelpers 'stable setting-out COGO label helpers'
}
WriteText $cogoPath $cogo

# -----------------------------------------------------------------------------
# 3. Vertex Setting-Out owns its COGO label position. Do not call generic
#    annotation clamping or whole-drawing COGO style/offset restoration after
#    create/refresh. New/recreated COGO points get project styles once and keep a
#    small stable relative label offset; moved points carry that offset with them.
# -----------------------------------------------------------------------------
$vertexPath = Required 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath

$vertex = [regex]::Replace(
    $vertex,
    '(?m)^\s*// CE_VERTEXSETTINGOUT(?:REFRESH| RefreshAll)[^\r\n]*(?:label offset capture|absolute label restore)[^\r\n]*\r?\n',
    '')
$vertex = [regex]::Replace(
    $vertex,
    '(?m)^\s*August11SurveyRuntimeCommands\.CaptureCogoInitialOffsets\(document\);\s*\r?\n',
    '')
$vertex = [regex]::Replace(
    $vertex,
    '(?m)^\s*August11SurveyRuntimeCommands\.RestoreCogoLabels\(document, null\);\s*\r?\n',
    '')
$vertex = [regex]::Replace(
    $vertex,
    '(?m)^\s*RuntimeAnnotationLinkManager\.ClampLinkedAnnotations\(document,\s*(?:true|false)\);\s*\r?\n',
    '')
$vertex = [regex]::Replace(
    $vertex,
    '(?ms)^\s*try\s*\{\s*CogoPointProjectStyleCommands\.ApplySelectedStyles\(document,\s*(?:true|false)\);\s*\}\s*\r?\n\s*catch\s*\{\s*\}\s*\r?\n?',
    '')

$createRaw = '                point.RawDescription = record.PointName;'
$createStable = @'
                point.RawDescription = record.PointName;
                CogoPointProjectStyleCommands.ApplyPointStyles(
                    database,
                    civilDocument,
                    transaction,
                    point);
                Vector3d stableLabelOffset =
                    CogoPointProjectStyleCommands.ReadStableSettingOutLabelOffset(
                        database,
                        point,
                        record.Point);
                CogoPointProjectStyleCommands.SetSettingOutLabelLocation(
                    point,
                    record.Point,
                    stableLabelOffset);
'@ -replace "`n","`r`n"
if (-not $vertex.Contains('Vector3d stableLabelOffset =')) {
    if (-not $vertex.Contains($createRaw)) {
        throw 'Post-sanitize Vertex COGO create RawDescription anchor was not found.'
    }
    $vertex = $vertex.Replace($createRaw,$createStable.TrimEnd())
}

$updateAnchor = '                cogo.Easting = record.Point.X;'
$updateBefore = @'
                Point3d previousCogoAnchor = new Point3d(
                    cogo.Easting,
                    cogo.Northing,
                    cogo.Elevation);
                Vector3d carriedLabelOffset =
                    CogoPointProjectStyleCommands.ReadStableSettingOutLabelOffset(
                        cogo.Database,
                        cogo,
                        previousCogoAnchor);
'@ -replace "`n","`r`n"
if (-not $vertex.Contains('Point3d previousCogoAnchor = new Point3d(')) {
    $vertex = Insert-BeforeFirst $vertex $updateAnchor $updateBefore 'Vertex COGO label-offset capture'
}

$updateRaw = '                cogo.RawDescription = record.PointName;'
$updateAfter = @'

                CogoPointProjectStyleCommands.SetSettingOutLabelLocation(
                    cogo,
                    record.Point,
                    carriedLabelOffset);
'@ -replace "`n","`r`n"
if (-not $vertex.Contains('                    carriedLabelOffset);')) {
    $vertex = Insert-AfterFirst $vertex $updateRaw $updateAfter 'Vertex COGO label-offset carry'
}
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 4. Dynamic Grid COGO uses the same stable point/label attachment. Site Grid is
#    also available directly inside the Grid Setting-Out window.
# -----------------------------------------------------------------------------
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$grid = ReadText $gridPath

$oldSelection = @'
            List<ObjectId> sourceIds = SelectBoundaries(document);
            if (sourceIds.Count == 0) return;

'@ -replace "`n","`r`n"
if ($grid.Contains($oldSelection)) {
    $grid = $grid.Replace($oldSelection,'')
}

$workflowAnchor = '            settings.AddPositiveDouble('
$workflowChoice = @'
            settings.AddChoice(
                "Workflow", "01 Grid", "Grid workflow", "Boundary Grid Setting-Out",
                "Boundary Grid Setting-Out uses multiple closed polylines with Perimeter or Full grid. Site Grid opens the dedicated CE Site Grid workflow.",
                new[] { "Boundary Grid Setting-Out", "Site Grid" });
'@ -replace "`n","`r`n"
if (-not $grid.Contains('"Workflow", "01 Grid", "Grid workflow"')) {
    $grid = Insert-BeforeFirst $grid $workflowAnchor $workflowChoice 'Grid Setting-Out Site Grid workflow choice'
}

$editAnchor = '            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;'
$editAddition = @'

            if (string.Equals(
                    settings.Text("Workflow"),
                    "Site Grid",
                    StringComparison.OrdinalIgnoreCase))
            {
                document.SendStringToExecute(
                    "CE_SITEGRID ",
                    true,
                    false,
                    true);
                return;
            }

            List<ObjectId> sourceIds = SelectBoundaries(document);
            if (sourceIds.Count == 0) return;
'@ -replace "`n","`r`n"
if (-not $grid.Contains('settings.Text("Workflow")')) {
    $grid = Insert-AfterFirst $grid $editAnchor $editAddition 'Grid Setting-Out Site Grid dispatch'
}

$gridDescriptionOld = 'Create one linked setting-out group from multiple selected closed polylines. Choose Perimeter or Full grid; COGO points and the linked table follow source geometry and annotation-scale changes automatically.'
$gridDescriptionNew = 'Choose Boundary Grid Setting-Out or Site Grid. Boundary Grid supports multiple closed polylines with Perimeter or Full grid; linked COGO points and the table follow source geometry automatically.'
$grid = $grid.Replace($gridDescriptionOld,$gridDescriptionNew)

$gridCreateRaw = '            point.RawDescription = record.Name;'
$gridCreateAddition = @'

            CogoPointProjectStyleCommands.ApplyPointStyles(
                database,
                civil,
                transaction,
                point);
            Vector3d stableLabelOffset =
                CogoPointProjectStyleCommands.ReadStableSettingOutLabelOffset(
                    database,
                    point,
                    record.Point);
            CogoPointProjectStyleCommands.SetSettingOutLabelLocation(
                point,
                record.Point,
                stableLabelOffset);
'@ -replace "`n","`r`n"
if (-not $grid.Contains('CogoPointProjectStyleCommands.ApplyPointStyles(')) {
    $grid = Insert-AfterFirst $grid $gridCreateRaw $gridCreateAddition 'Dynamic Grid COGO safe style/label anchor'
}

$gridUpdateAnchor = '            point.Easting = record.Point.X;'
$gridUpdateBefore = @'
            Point3d previousCogoAnchor = new Point3d(
                point.Easting,
                point.Northing,
                point.Elevation);
            Vector3d carriedLabelOffset =
                CogoPointProjectStyleCommands.ReadStableSettingOutLabelOffset(
                    point.Database,
                    point,
                    previousCogoAnchor);
'@ -replace "`n","`r`n"
if (-not $grid.Contains('Point3d previousCogoAnchor = new Point3d(')) {
    $grid = Insert-BeforeFirst $grid $gridUpdateAnchor $gridUpdateBefore 'Dynamic Grid COGO label-offset capture'
}

$gridUpdateRaw = '            point.RawDescription = record.Name;'
$gridUpdateAfter = @'

            CogoPointProjectStyleCommands.SetSettingOutLabelLocation(
                point,
                record.Point,
                carriedLabelOffset);
'@ -replace "`n","`r`n"
if (-not $grid.Contains('                    carriedLabelOffset);')) {
    $firstRaw = $grid.IndexOf($gridUpdateRaw,[StringComparison]::Ordinal)
    if ($firstRaw -lt 0) { throw 'Dynamic Grid RawDescription anchor was not found.' }
    $secondRaw = $grid.IndexOf(
        $gridUpdateRaw,
        $firstRaw + $gridUpdateRaw.Length,
        [StringComparison]::Ordinal)
    if ($secondRaw -lt 0) { throw 'Dynamic Grid update RawDescription anchor was not found.' }
    $end = $secondRaw + $gridUpdateRaw.Length
    $grid = $grid.Substring(0,$end) + $gridUpdateAfter + $grid.Substring($end)
}
WriteText $gridPath $grid

# -----------------------------------------------------------------------------
# 5. Every visible Grid Setting-Out route reaches the dedicated Grid window. That
#    window now contains Boundary Perimeter/Full Grid plus Site Grid.
# -----------------------------------------------------------------------------
$centresPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath
$centres = $centres.Replace('"CE_GRIDSETTINGOUTMULTI"','"CE_GRIDSETTINGOUT"')
$centres = $centres.Replace(
    'Linked grid/perimeter setting-out.',
    'Boundary Perimeter / Full Grid and Site Grid setting-out.')
$centres = $centres.Replace(
    'Refresh linked points/tables, restore original COGO label offsets and sync annotation scale.',
    'Refresh linked points/tables in one coordinated pass without moving COGO label offsets.')
WriteText $centresPath $centres

$fieldPath = Required 'August14SurveyFieldReviewCommands.cs'
$field = ReadText $fieldPath
$field = $field.Replace(
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT",',
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUT",')
$field = $field.Replace(
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTCREATE",',
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUT",')
$field = $field.Replace(
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTDYNAMIC",',
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUT",')
$field = $field.Replace(
    'Move linked points back onto changed source vertices, refresh tables and restore annotation scale/COGO label offsets.',
    'Refresh linked points/tables in one coordinated pass without moving COGO label offsets.')
WriteText $fieldPath $field

# -----------------------------------------------------------------------------
# Final guards: exact regressions seen in Civil 3D field testing.
# -----------------------------------------------------------------------------
$universal = ReadText $universalPath
foreach ($forbidden in @(
    'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);',
    'August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);',
    'AnnotationScaleSyncManager.ApplyCurrentScale(document);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'CeTablePresentationManager.CenterCeTables(document);')) {
    if ($universal.Contains($forbidden)) {
        throw "Post-sanitize Universal refresh regression remains: $forbidden"
    }
}
foreach ($statement in @(
    'DynamicCoordinateLinkStore.Refresh(document);',
    'SurfaceComparisonLinkStore.RefreshAll(document);',
    'MultiSurfaceComparisonTableStore.RefreshAll(document);',
    'LinkedSurfaceReportTableStore.RefreshAll(document);')) {
    if ([regex]::Matches($universal,[regex]::Escape($statement)).Count -gt 1) {
        throw "Duplicate Universal refresh call remains after final cleanup: $statement"
    }
}

$vertex = ReadText $vertexPath
foreach ($forbidden in @(
    'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);',
    'August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);',
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);')) {
    if ($vertex.Contains($forbidden)) {
        throw "Post-sanitize Vertex/Grid label-movement regression remains: $forbidden"
    }
}
foreach ($required in @(
    'ReadStableSettingOutLabelOffset(',
    'SetSettingOutLabelLocation(',
    'Point3d previousCogoAnchor = new Point3d(')) {
    if (-not $vertex.Contains($required)) {
        throw "Vertex COGO stable-label marker missing: $required"
    }
}

$cogo = ReadText $cogoPath
foreach ($required in @(
    'if (IsCeSettingOutPoint(point, transaction))',
    'ReadStableSettingOutLabelOffset(',
    'SetSettingOutLabelLocation(',
    '"CE_DYNAMIC_GRID_POINT"')) {
    if (-not $cogo.Contains($required)) {
        throw "COGO setting-out label protection marker missing: $required"
    }
}

$grid = ReadText $gridPath
foreach ($required in @(
    '"Workflow", "01 Grid", "Grid workflow"',
    '"Boundary Grid Setting-Out", "Site Grid"',
    '"CE_SITEGRID "',
    'ReadStableSettingOutLabelOffset(',
    'SetSettingOutLabelLocation(')) {
    if (-not $grid.Contains($required)) {
        throw "Dynamic Grid final marker missing: $required"
    }
}

$centres = ReadText $centresPath
if ($centres.Contains('"CE_GRIDSETTINGOUTMULTI"')) {
    throw 'Survey Production still routes Grid Setting-Out through the legacy multi/Vertex menu.'
}
$field = ReadText $fieldPath
if ($field.Contains('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT",') -or
    $field.Contains('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTCREATE",') -or
    $field.Contains('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTDYNAMIC",')) {
    throw 'Survey field-review Grid Setting-Out bypasses the final Grid window.'
}
if (-not $field.Contains('"CE_GRIDSETTINGOUT"')) {
    throw 'Survey field-review Grid Setting-Out does not expose the final Grid window.'
}

Write-Host 'Post-sanitize Survey stability passed: automatic Vertex/Grid refresh no longer clamps or restores COGO label offsets.' -ForegroundColor Green
Write-Host 'CE setting-out COGO labels now carry one bounded relative offset with their COGO marker.' -ForegroundColor Green
Write-Host 'Universal linked tables use one dependency refresh pass without table-presentation feedback.' -ForegroundColor Green
Write-Host 'Grid Setting-Out now includes Boundary Perimeter / Full Grid plus Site Grid.' -ForegroundColor Green
