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
        throw "September 05 field-geometry source missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function MethodBounds([string]$text,[string]$marker) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $marker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $marker" }
    $depth = 0
    $close = -1
    for ($index = $open; $index -lt $text.Length; $index++) {
        if ($text[$index] -eq '{') { $depth++ }
        elseif ($text[$index] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $index; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $marker" }
    return [pscustomobject]@{ Start=$start; Open=$open; Close=$close }
}

function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $bounds = MethodBounds $text $marker
    $normalized = ($body -replace "`r?`n","`r`n").Trim("`r","`n")
    return $text.Substring(0,$bounds.Open+1) + "`r`n" + $normalized + "`r`n        " + $text.Substring($bounds.Close)
}

function EnsureBeforeTokenInMethod(
    [string]$text,
    [string]$methodMarker,
    [string]$token,
    [string]$statement,
    [string]$presence) {

    $bounds = MethodBounds $text $methodMarker
    $methodText = $text.Substring($bounds.Open+1,$bounds.Close-$bounds.Open-1)
    if ($methodText.Contains($presence)) { return $text }
    $relative = $methodText.IndexOf($token,[StringComparison]::Ordinal)
    if ($relative -lt 0) { throw "Insertion token missing in $methodMarker : $token" }
    $absolute = $bounds.Open + 1 + $relative
    $lineStart = $text.LastIndexOf("`n",$absolute)
    if ($lineStart -lt 0) { $lineStart = $bounds.Open + 1 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart + $indentLength -lt $text.Length -and
           ($text[$lineStart+$indentLength] -eq ' ' -or $text[$lineStart+$indentLength] -eq "`t")) {
        $indentLength++
    }
    $indent = $text.Substring($lineStart,$indentLength)
    return $text.Insert($lineStart,$indent + $statement + "`r`n")
}

function EnsureAfterLineContaining(
    [string]$text,
    [string]$token,
    [string]$line,
    [string]$presence) {

    if ($text.Contains($presence)) { return $text }
    $index = $text.IndexOf($token,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Menu insertion anchor missing: $token" }
    $lineEnd = $text.IndexOf("`n",$index)
    if ($lineEnd -lt 0) { throw "Menu insertion line end missing: $token" }
    $lineStart = $text.LastIndexOf("`n",$index)
    if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart + $indentLength -lt $text.Length -and
           ($text[$lineStart+$indentLength] -eq ' ' -or $text[$lineStart+$indentLength] -eq "`t")) {
        $indentLength++
    }
    $indent = $text.Substring($lineStart,$indentLength)
    return $text.Insert($lineEnd+1,$indent + $line + "`r`n")
}

$completionPath = Required 'September04FieldGeometryCompletionCommands.cs'
$frontDoorPath = Required 'September05FieldGeometryCommandFrontDoor.cs'
$breakPath = Required 'August25CadSupplementaryBreakEngine.cs'
$geometryPath = Required 'August24FieldGeometryCommands.cs'
$aug27Path = Required 'August27DynamicSlopeGridHatchCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$menuPath = Required 'August24FieldCompletionCommands.cs'

# Keep the September 04 completion class as a runtime/helper class. The repository
# already has a registered CE_CONNECTENDPOINTS command in the August 27 command
# class, so duplicate CommandMethod attributes would make command loading
# nondeterministic. CE_MULTIFILLET and CE_GRIDDIFFERENCE are registered by the
# small September05 front-door class instead.
$completion = ReadText $completionPath
foreach ($attribute in @(
    '        [CommandMethod("CE_MULTIFILLET", CommandFlags.Modal | CommandFlags.UsePickSet)]' + "`r`n",
    '        [CommandMethod("CE_CONNECTENDPOINTS", CommandFlags.Modal | CommandFlags.UsePickSet)]' + "`r`n",
    '        [CommandMethod("CE_GRIDDIFFERENCE", CommandFlags.Modal)]' + "`r`n")) {
    $completion = $completion.Replace($attribute,'')
}
$completion = $completion.Replace(
    '        private static void ConnectEndpoints(Document document)',
    '        internal static void ConnectEndpoints(Document document)')
WriteText $completionPath $completion

# Restore the break behaviour the field user described: detect T/X in plan, use
# AutoCAD native GetSplitCurves for polylines, keep the selected source database
# object/handle as the first span, and never erase the source route.
$break = ReadText $breakPath
$break = ReplaceMethodBody $break '        internal static void Run(Document document)' @'
            September04FieldGeometryCompletionCommands.BreakAtJunctions(document);
'@
WriteText $breakPath $break

# Construction offsets remain true AutoCAD XLINEs. Centre construction uses the
# new closest-crossing zero-fillet finite mode by default while retaining the
# explicit true-XLINE option requested in the previous field round.
$geometry = ReadText $geometryPath
$geometry = ReplaceMethodBody $geometry '        public void ConstructionOffsets()' @'
            Document document = Active();
            if (document == null) return;
            September04CadSupplementaryRuntime.ConstructionOffsets(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void MiddleConstructionLines()' @'
            Document document = Active();
            if (document == null) return;
            September04FieldGeometryCompletionCommands.MiddleConstructionLines(document);
'@
WriteText $geometryPath $geometry

# Route the already-registered CE_CONNECTENDPOINTS command to the new tolerance,
# source-preserving green-polyline implementation. This avoids a second command
# registration while replacing the older no-distance version.
$aug27 = ReadText $aug27Path
$aug27 = ReplaceMethodBody $aug27 '        public void ConnectSelectedEndpoints()' @'
            Document document = Active();
            if (document == null) return;
            September04FieldGeometryCompletionCommands.ConnectEndpoints(document);
'@
WriteText $aug27Path $aug27

# Make Design-NG part of normal Grid Setting-Out creation and every refresh. The
# helper finds the live NG LEVEL and DESIGN LEVEL columns, inserts DIFFERENCE after
# DESIGN when needed, and recalculates values to three decimals.
$grid = ReadText $gridPath
$grid = EnsureBeforeTokenInMethod `
    $grid `
    '        public void CreateDynamicGrid()' `
    'document.Editor.SetImpliedSelection(new[] { tableId });' `
    '                September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);' `
    'September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);'
$grid = EnsureBeforeTokenInMethod `
    $grid `
    '        internal static int RefreshAll(Document document)' `
    'return refreshed;' `
    '            September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);' `
    'September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);'
WriteText $gridPath $grid

# Surface the new field commands in CAD Supplementary. August 27 already inserts
# CE_CONNECTENDPOINTS in current staged builds; only add it here when an older stage
# did not. CE_GRIDDIFFERENCE is normally automatic and remains available by command.
$menu = ReadText $menuPath
$menu = EnsureAfterLineContaining `
    $menu `
    '"CE_PLBREAKJUNCTIONS"' `
    'A("CE-Fillet Multiple Open Lines / Polylines", "CE_MULTIFILLET", "Fillet multiple open LINE/LWPOLYLINE endpoints to a remembered radius; radius 0 gives an exact trim/extend crossing.", "01 Geometry"),' `
    '"CE_MULTIFILLET"'
if (-not $menu.Contains('"CE_CONNECTENDPOINTS"')) {
    $menu = EnsureAfterLineContaining `
        $menu `
        '"CE_MULTIFILLET"' `
        'A("CE-Connect Endpoints - New Green Polyline", "CE_CONNECTENDPOINTS", "Connect the selected endpoint side within a remembered distance tolerance without changing the red source objects.", "01 Geometry"),' `
        '"CE_CONNECTENDPOINTS"'
}
WriteText $menuPath $menu

# Final staged guards. These intentionally check behaviour, command registration,
# and the final route rather than only checking that a file exists.
$completion = ReadText $completionPath
$frontDoor = ReadText $frontDoorPath
$break = ReadText $breakPath
$geometry = ReadText $geometryPath
$aug27 = ReadText $aug27Path
$grid = ReadText $gridPath
$menu = ReadText $menuPath

foreach ($token in @(
    'source.GetSplitCurves(splitPoints)',
    'CollectCrossings(',
    'CollectEndpointTJunctions(',
    'ReplacePolylineGeometry(source, pieces[0]);',
    'ZeroFilletCentreEndpoints(',
    'ClosestSupportIntersection(',
    'designValue - ngValue',
    'ColorIndex = 3',
    'UseDefaultValue = true;',
    '_lastFilletRadius',
    '_lastEndpointDistance')) {
    if (-not $completion.Contains($token)) { throw "September 05 completion runtime guard missing: $token" }
}
if ($completion.Contains('[CommandMethod("CE_CONNECTENDPOINTS"') -or
    $completion.Contains('[CommandMethod("CE_MULTIFILLET"') -or
    $completion.Contains('[CommandMethod("CE_GRIDDIFFERENCE"')) {
    throw 'Completion runtime still contains direct command registration; duplicate command loading is possible.'
}
foreach ($token in @(
    '[CommandMethod("CE_TOOLS", "CE_MULTIFILLET"',
    '[CommandMethod("CE_TOOLS", "CE_GRIDDIFFERENCE"')) {
    if (-not $frontDoor.Contains($token)) { throw "September 05 command front-door guard missing: $token" }
}
if (-not $break.Contains('September04FieldGeometryCompletionCommands.BreakAtJunctions(document);')) {
    throw 'CE_PLBREAKJUNCTIONS is not routed to the final native keep-source runtime.'
}
foreach ($token in @(
    'September04CadSupplementaryRuntime.ConstructionOffsets(document);',
    'September04FieldGeometryCompletionCommands.MiddleConstructionLines(document);')) {
    if (-not $geometry.Contains($token)) { throw "Final construction route missing: $token" }
}
if (-not $aug27.Contains('September04FieldGeometryCompletionCommands.ConnectEndpoints(document);')) {
    throw 'CE_CONNECTENDPOINTS is not routed to the new green connector runtime.'
}
$gridDifferenceCount = ([regex]::Matches(
    $grid,
    [regex]::Escape('September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);'))).Count
if ($gridDifferenceCount -lt 2) {
    throw "Grid difference must run on creation and refresh; found $gridDifferenceCount route(s)."
}
foreach ($token in @('"CE_MULTIFILLET"','"CE_CONNECTENDPOINTS"')) {
    if (-not $menu.Contains($token)) { throw "CAD Supplementary menu guard missing: $token" }
}

Write-Host 'September 05 field-geometry completion finalization complete.' -ForegroundColor Green
Write-Host 'T/X native keep-source break, centre zero-fillet/XLINE modes, remembered multi-fillet, green endpoint connector and Design-NG grid difference are now the final staged routes.' -ForegroundColor Green
