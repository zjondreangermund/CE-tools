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
function Remove-AllExact([string]$text,[string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return $text }
    while ($text.Contains($value)) { $text = $text.Replace($value,'') }
    return $text
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

# The August 14 hook repair and Hotfix3 can both expose the same linked-table
# refresh call. Keep exactly one copy so a single source edit produces one pass.
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
# 2. Vertex/Grid linked refresh must never restore a stale absolute COGO label
#    offset. The dedicated manual COGO restore command remains available elsewhere.
# -----------------------------------------------------------------------------
$vertexPath = Required 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath
$vertex = [regex]::Replace($vertex,'(?m)^\s*// CE_VERTEXSETTINGOUT(?:REFRESH| RefreshAll)[^\r\n]*(?:label offset capture|absolute label restore)[^\r\n]*\r?\n','')
$vertex = [regex]::Replace($vertex,'(?m)^\s*August11SurveyRuntimeCommands\.CaptureCogoInitialOffsets\(document\);\s*\r?\n','')
$vertex = [regex]::Replace($vertex,'(?m)^\s*August11SurveyRuntimeCommands\.RestoreCogoLabels\(document, null\);\s*\r?\n','')
$vertex = $vertex.Replace(
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);',
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, false);')
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 3. Every visible Grid Setting-Out route must reach the dedicated dynamic Grid
#    engine (multiple closed polylines + Perimeter / Full grid), never Vertex UI.
# -----------------------------------------------------------------------------
$centresPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath
$centres = $centres.Replace('"CE_GRIDSETTINGOUTMULTI"','"CE_GRIDSETTINGOUT"')
$centres = $centres.Replace(
    'Refresh linked points/tables, restore original COGO label offsets and sync annotation scale.',
    'Refresh linked points/tables in one coordinated pass without moving COGO label offsets.')
WriteText $centresPath $centres

$fieldPath = Required 'August14SurveyFieldReviewCommands.cs'
$field = ReadText $fieldPath
$field = $field.Replace(
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT",',
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTDYNAMIC",')
$field = $field.Replace(
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTCREATE",',
    'new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTDYNAMIC",')
$field = $field.Replace(
    'Move linked points back onto changed source vertices, refresh tables and restore annotation scale/COGO label offsets.',
    'Refresh linked points/tables in one coordinated pass without moving COGO label offsets.')
WriteText $fieldPath $field

# -----------------------------------------------------------------------------
# Final guards: these are the exact regressions that caused the field failures.
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
    'RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);')) {
    if ($vertex.Contains($forbidden)) {
        throw "Post-sanitize Vertex/Grid refresh regression remains: $forbidden"
    }
}
$centres = ReadText $centresPath
if ($centres.Contains('"CE_GRIDSETTINGOUTMULTI"')) {
    throw 'Survey Production still routes Grid Setting-Out through the legacy multi/Vertex menu.'
}
$field = ReadText $fieldPath
if ($field.Contains('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT",') -or
    $field.Contains('new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_GRIDSETTINGOUTCREATE",')) {
    throw 'Survey field-review Grid Setting-Out still routes to Vertex Setting-Out.'
}
if (-not $field.Contains('"CE_GRIDSETTINGOUTDYNAMIC"')) {
    throw 'Survey field-review Grid Setting-Out does not expose the dedicated dynamic Grid engine.'
}

Write-Host 'Post-sanitize Survey stability passed: no stale COGO offset restore remains in automatic refresh.' -ForegroundColor Green
Write-Host 'Universal linked tables now use one refresh call per dependency instead of duplicate passes.' -ForegroundColor Green
Write-Host 'All visible Grid Setting-Out routes now use the MULTIPLE-polyline Perimeter / Full-grid engine.' -ForegroundColor Green
