[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$dq = [char]34
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim($dq)).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ('September 09 field-engineering source missing: {0}' -f $path)
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace '\r?\n', "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace '\r?\n',"`r`n"),$utf8)
}
function MethodBounds([string]$text,[string]$marker) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw ('Method marker missing: {0}' -f $marker) }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw ('Opening brace missing: {0}' -f $marker) }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw ('Closing brace missing: {0}' -f $marker) }
    return [pscustomobject]@{ Start=$start; Open=$open; Close=$close }
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $b = MethodBounds $text $marker
    $normalized = ($body -replace '\r?\n',"`r`n").Trim("`r","`n")
    return $text.Substring(0,$b.Open+1) + "`r`n" + $normalized + "`r`n        " + $text.Substring($b.Close)
}

$runtimePath = Required 'September09FieldEngineeringRuntime.cs'
$frontDoorPath = Required 'September05FieldGeometryCommandFrontDoor.cs'
$completionPath = Required 'September04FieldGeometryCompletionCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$aug27Path = Required 'August27DynamicSlopeGridHatchCommands.cs'
$sewerAlignmentPath = Required 'SewerBranchAlignmentCommands.cs'
$menuPath = Required 'August24FieldCompletionCommands.cs'

# PolylineOptions is a Civil 3D value type. Keep the tracked helper compatible with
# Civil 3D 2023 even when the raw source came from a previous edit before this pass.
$runtime = ReadText $runtimePath
$runtime = $runtime.Replace(
    '            if (polylineOptions == null || polylineOptions.PlineId.IsNull)',
    '            if (polylineOptions.PlineId.IsNull)')
WriteText $runtimePath $runtime

# Make the DIFFERENCE column part of the actual Table transaction. The September 05
# document-level scan happened after table creation/refresh and could miss the newly
# written linked table in field drawings. This injection runs immediately after every
# PopulateTable call while the same writable Table is still open.
$grid = ReadText $gridPath
$populate = '                PopulateTable(document.Database, table, records, link);'
$perTable = '                September09FieldEngineeringRuntime.EnsureGridDifferenceColumn(table);'
if (-not $grid.Contains($perTable)) {
    $count = ([regex]::Matches($grid,[regex]::Escape($populate))).Count
    if ($count -lt 2) { throw ('Expected create+refresh PopulateTable calls; found {0}.' -f $count) }
    $grid = $grid.Replace($populate, $populate + "`r`n" + $perTable)
}
WriteText $gridPath $grid

# Route the existing unique CE_GRIDDIFFERENCE command to the robust table scan.
$frontDoor = ReadText $frontDoorPath
$frontDoor = ReplaceMethodBody $frontDoor '        public void GridDifference()' @'
            September09FieldEngineeringRuntime.GridDifferenceActive();
'@
WriteText $frontDoorPath $frontDoor

# Centre construction keeps the existing zero-fillet and true-XLINE modes, and adds
# a finite fillet-ready mode for road-reserve construction geometry. The actual
# multiple specified-radius fillet is CE_CONSTRUCTIONFILLET / CE_MULTIFILLET.
$completion = ReadText $completionPath
$oldChoices = '                new[] { "Zero-fillet finite centre lines", "Construction XLINE entities" });'
$newChoices = '                new[] { "Zero-fillet finite centre lines", "Fillet-ready finite centre lines", "Construction XLINE entities" });'
if ($completion.Contains($oldChoices)) { $completion = $completion.Replace($oldChoices,$newChoices) }
$oldMode = @'
            bool xlineMode = string.Equals(_lastCentreMode, "Construction XLINE entities", StringComparison.OrdinalIgnoreCase);
            if (!xlineMode) ZeroFilletCentreEndpoints(centres, _lastCentreJoinDistance);
'@ -replace '\r?\n',"`r`n"
$newMode = @'
            bool xlineMode = string.Equals(_lastCentreMode, "Construction XLINE entities", StringComparison.OrdinalIgnoreCase);
            bool filletReadyMode = string.Equals(_lastCentreMode, "Fillet-ready finite centre lines", StringComparison.OrdinalIgnoreCase);
            if (!xlineMode && !filletReadyMode) ZeroFilletCentreEndpoints(centres, _lastCentreJoinDistance);
'@ -replace '\r?\n',"`r`n"
if ($completion.Contains($oldMode.Trim())) {
    $completion = $completion.Replace($oldMode.Trim(),$newMode.Trim())
}
$completion = $completion.Replace(
    'Zero-fillet finite centre lines meet exactly at the closest crossing. XLINE mode creates true AutoCAD construction-line entities.',
    'Zero-fillet meets at closest crossings; Fillet-ready keeps finite ends for specified-radius multi-fillet; XLINE creates true AutoCAD construction-line entities.')
WriteText $completionPath $completion

# Surface slope annotation must be a real Civil 3D SurfaceSlopeLabel rather than
# custom AutoCAD Leader/MText graphics. Native labels remain dynamically attached to
# the selected surface and use Civil 3D surface-slope label styles.
$aug27 = ReadText $aug27Path
$aug27 = ReplaceMethodBody $aug27 '        internal static void SurfaceSlopeArrows(Document document)' @'
            September09FieldEngineeringRuntime.SurfaceSlopeLabels(document);
'@
WriteText $aug27Path $aug27

# Sewer alignment field error: preserve the temporary source polyline through the
# first creation attempt, try the Civil 3D string-name overload first, then fall back
# to resolved ObjectIds. The helper erases the temporary polyline only after success.
$sewer = ReadText $sewerAlignmentPath
$sewer = $sewer.Replace('                            EraseExistingEntities = true,','                            EraseExistingEntities = false,')
$oldCreate = @'
                        ObjectId alignmentId = CivilAlignment.Create(
                            civilDocument,
                            polylineOptions,
                            alignmentName,
                            ObjectId.Null,
                            layerId,
                            alignmentStyleId,
                            labelSetStyleId);
'@ -replace '\r?\n',"`r`n"
$newCreate = @'
                        ObjectId alignmentId = September09FieldEngineeringRuntime.CreateSewerAlignmentSafe(
                            database,
                            civilDocument,
                            transaction,
                            polylineOptions,
                            alignmentName,
                            layerId,
                            alignmentStyleId,
                            alignmentStyleName,
                            labelSetStyleId,
                            labelSetStyleName);
'@ -replace '\r?\n',"`r`n"
if ($sewer.Contains($oldCreate.Trim())) {
    $sewer = $sewer.Replace($oldCreate.Trim(),$newCreate.Trim())
}
WriteText $sewerAlignmentPath $sewer

# Field menu additions. Insert into both CAD and Road supplementary centres while
# keeping the existing generic CE_MULTIFILLET command and established slope commands.
$menu = ReadText $menuPath
$cadCentre = '                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines for selected curve pairs.", "01 Geometry"),'
$roadCentre = '                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines within a specified maximum separation.", "02 Construction"),'
$cadRoadLine = '                    A("CE-Road Reserve Centre Polylines", "CE_ROADRESERVECENTRELINES", "Create finite red centre polylines midway between multiple selected cadastral/erf road-reserve boundaries.", "01 Geometry"),'
$cadFilletLine = '                    A("CE-Fillet Construction / Road Centre Lines", "CE_CONSTRUCTIONFILLET", "Fillet multiple open construction LINE/LWPOLYLINE endpoints to a specified remembered radius.", "01 Geometry"),'
$roadRoadLine = '                    A("CE-Road Reserve Centre Polylines", "CE_ROADRESERVECENTRELINES", "Detect facing cadastral/erf reserve boundaries and create finite red centre polylines without changing source parcels.", "02 Construction"),'
$roadFilletLine = '                    A("CE-Fillet Construction / Road Centre Lines", "CE_CONSTRUCTIONFILLET", "Fillet multiple finite construction/road-centre lines to a specified remembered radius.", "02 Construction"),'
if (-not $menu.Contains('"CE_ROADRESERVECENTRELINES"')) {
    if (-not $menu.Contains($cadCentre)) { throw 'CAD centre-construction menu anchor missing.' }
    if (-not $menu.Contains($roadCentre)) { throw 'Road centre-construction menu anchor missing.' }
    $menu = $menu.Replace($cadCentre,$cadCentre + "`r`n" + $cadRoadLine + "`r`n" + $cadFilletLine)
    $menu = $menu.Replace($roadCentre,$roadCentre + "`r`n" + $roadRoadLine + "`r`n" + $roadFilletLine)
}

$menu = $menu.Replace(
    'A("CE-Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Create sampled downhill arrows and slope labels on a selected surface.", "02 Annotation"),',
    'A("CE-Surface Slope Labels - Native Civil 3D", "CE_SURFACESLOPEARROWS", "Create native dynamic Civil 3D SurfaceSlopeLabel elements attached to the selected surface.", "02 Annotation"),')
$menu = $menu.Replace(
    'A("CE-Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Sample a surface and show downhill slope arrows/values.", "02 Slopes"),',
    'A("CE-Surface Slope Labels - Native Civil 3D", "CE_SURFACESLOPEARROWS", "Create native dynamic Civil 3D SurfaceSlopeLabel elements attached to the selected surface.", "02 Slopes"),')
if (-not $menu.Contains('"CE_SLOPEANNOTATIONS"')) {
    $cadSurface = '                    A("CE-Surface Slope Labels - Native Civil 3D", "CE_SURFACESLOPEARROWS", "Create native dynamic Civil 3D SurfaceSlopeLabel elements attached to the selected surface.", "02 Annotation"),'
    $surveySurface = '                    A("CE-Surface Slope Labels - Native Civil 3D", "CE_SURFACESLOPEARROWS", "Create native dynamic Civil 3D SurfaceSlopeLabel elements attached to the selected surface.", "02 Slopes"),'
    $cadSlopeOptions = '                    A("CE-Slope Annotation Options", "CE_SLOPEANNOTATIONS", "Choose native surface slope labels, slope between two feature lines, or slope along feature lines.", "02 Annotation"),'
    $surveySlopeOptions = '                    A("CE-Slope Annotation Options", "CE_SLOPEANNOTATIONS", "Choose native surface slope labels, slope between two feature lines, or slope along feature lines.", "02 Slopes"),'
    if ($menu.Contains($cadSurface)) { $menu = $menu.Replace($cadSurface,$cadSurface + "`r`n" + $cadSlopeOptions) }
    if ($menu.Contains($surveySurface)) { $menu = $menu.Replace($surveySurface,$surveySurface + "`r`n" + $surveySlopeOptions) }
}
WriteText $menuPath $menu

# Strict final guards: this is the absolute final build boundary after Sep05.
$runtime = ReadText $runtimePath
$frontDoor = ReadText $frontDoorPath
$completion = ReadText $completionPath
$grid = ReadText $gridPath
$aug27 = ReadText $aug27Path
$sewer = ReadText $sewerAlignmentPath
$menu = ReadText $menuPath

foreach ($token in @(
    '"NG LEVEL"','"DESIGN LEVEL"','"DIFFERENCE"','(designValue - ngValue)',
    'CE-ROAD-CENTRELINE','Facing parcel sides only','SnapCentreJunctions(',
    'CivilSurfaceSlopeLabel.Create','Surface slope - native Civil 3D label',
    'Slope between two feature lines','Slope along feature lines',
    'CreateSewerAlignmentSafe(','EraseExistingEntities = false')) {
    if (-not $runtime.Contains($token) -and -not $sewer.Contains($token)) {
        throw ('September 09 runtime guard missing: {0}' -f $token)
    }
}

$perTableCount = ([regex]::Matches($grid,[regex]::Escape($perTable))).Count
if ($perTableCount -lt 2) { throw ('Grid DIFFERENCE must run inside create+refresh Table transactions; found {0}.' -f $perTableCount) }
if (-not $frontDoor.Contains('September09FieldEngineeringRuntime.GridDifferenceActive();')) { throw 'CE_GRIDDIFFERENCE final route is not September09 runtime.' }
foreach ($token in @('Fillet-ready finite centre lines','bool filletReadyMode','if (!xlineMode && !filletReadyMode)')) {
    if (-not $completion.Contains($token)) { throw ('Centre construction fillet-ready guard missing: {0}' -f $token) }
}
if (-not $aug27.Contains('September09FieldEngineeringRuntime.SurfaceSlopeLabels(document);')) { throw 'CE_SURFACESLOPEARROWS is not routed to native Civil 3D labels.' }
if (-not $sewer.Contains('September09FieldEngineeringRuntime.CreateSewerAlignmentSafe(')) { throw 'CE_SEWALIGN safe creation route missing.' }
foreach ($token in @('"CE_ROADRESERVECENTRELINES"','"CE_CONSTRUCTIONFILLET"','"CE_SLOPEANNOTATIONS"','Native Civil 3D')) {
    if (-not $menu.Contains($token)) { throw ('Field menu guard missing: {0}' -f $token) }
}
if ($runtime.Contains('polylineOptions == null')) { throw 'Civil PolylineOptions value-type null check still present.' }

Write-Host 'September 09 field-engineering finalization complete.' -ForegroundColor Green
Write-Host 'Grid Design-NG column is transactional/dynamic; road-reserve centres and construction fillet are available; surface slopes are native Civil 3D labels; sewer alignment creation has a named-style/ObjectId fallback.' -ForegroundColor Green
