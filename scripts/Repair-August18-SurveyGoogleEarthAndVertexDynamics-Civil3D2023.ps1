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
    $anchor = '                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 SETTINGS"),'
    $addition = $anchor + "`r`n" +
        '                    A("CE-Plot Polyline Boundary in Google Earth", "CE_SURVEYGOOGLEEARTHBOUNDARY", "Convert one or more closed survey polylines to WGS84 KML and open the polygon boundaries in Google Earth.", "01 SETTINGS"),'
    if (-not $centres.Contains($anchor)) {
        throw 'Survey Production Namibia LO menu anchor was not found for Google Earth boundary insertion.'
    }
    $centres = $centres.Replace($anchor,$addition)
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

Write-Host 'Survey Production now includes closed-polyline Google Earth boundary export.' -ForegroundColor Green
Write-Host 'Vertex setting-out points now auto-follow moved source geometry after the edit command finishes.' -ForegroundColor Green
Write-Host 'Normal refresh preserves label offsets and no longer repeatedly solves overlaps.' -ForegroundColor Green
