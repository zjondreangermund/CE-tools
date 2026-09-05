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
        throw ('September 05 field-geometry source missing: {0}' -f $path)
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
function EnsureBeforeTokenInMethod([string]$text,[string]$methodMarker,[string]$token,[string]$statement,[string]$presence) {
    $b = MethodBounds $text $methodMarker
    $method = $text.Substring($b.Open+1,$b.Close-$b.Open-1)
    if ($method.Contains($presence)) { return $text }
    $relative = $method.IndexOf($token,[StringComparison]::Ordinal)
    if ($relative -lt 0) { throw ('Insertion token missing in {0}: {1}' -f $methodMarker,$token) }
    $absolute = $b.Open + 1 + $relative
    $lineStart = $text.LastIndexOf("`n",$absolute)
    if ($lineStart -lt 0) { $lineStart = $b.Open + 1 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart+$indentLength -lt $text.Length -and ($text[$lineStart+$indentLength] -eq ' ' -or $text[$lineStart+$indentLength] -eq "`t")) { $indentLength++ }
    $indent = $text.Substring($lineStart,$indentLength)
    return $text.Insert($lineStart,$indent + $statement + "`r`n")
}
function EnsureAfterLineContaining([string]$text,[string]$token,[string]$line,[string]$presence) {
    if ($text.Contains($presence)) { return $text }
    $index = $text.IndexOf($token,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw ('Menu insertion anchor missing: {0}' -f $token) }
    $lineEnd = $text.IndexOf("`n",$index)
    if ($lineEnd -lt 0) { throw ('Menu insertion line end missing: {0}' -f $token) }
    $lineStart = $text.LastIndexOf("`n",$index)
    if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart+$indentLength -lt $text.Length -and ($text[$lineStart+$indentLength] -eq ' ' -or $text[$lineStart+$indentLength] -eq "`t")) { $indentLength++ }
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

# Remove direct attributes from the helper class. August27 remains the single
# CE_CONNECTENDPOINTS registration; the September05 front door registers only the
# two new commands.
$completion = ReadText $completionPath
$attrMulti = '        [CommandMethod(' + $dq + 'CE_MULTIFILLET' + $dq + ', CommandFlags.Modal | CommandFlags.UsePickSet)]' + "`r`n"
$attrConnect = '        [CommandMethod(' + $dq + 'CE_CONNECTENDPOINTS' + $dq + ', CommandFlags.Modal | CommandFlags.UsePickSet)]' + "`r`n"
$attrDifference = '        [CommandMethod(' + $dq + 'CE_GRIDDIFFERENCE' + $dq + ', CommandFlags.Modal)]' + "`r`n"
foreach ($attribute in @($attrMulti,$attrConnect,$attrDifference)) { $completion = $completion.Replace($attribute,'') }
$completion = $completion.Replace('        private static void ConnectEndpoints(Document document)','        internal static void ConnectEndpoints(Document document)')
WriteText $completionPath $completion

# T/X crossings and T endpoints: native GetSplitCurves for polylines; keep source.
$break = ReadText $breakPath
$break = ReplaceMethodBody $break '        internal static void Run(Document document)' @'
            September04FieldGeometryCompletionCommands.BreakAtJunctions(document);
'@
WriteText $breakPath $break

# Construction offset remains true XLINE. Centre construction defaults to finite
# zero-fillet closest crossings and still offers true XLINE output.
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

# Keep one CE_CONNECTENDPOINTS CommandMethod and replace its implementation.
$aug27 = ReadText $aug27Path
$aug27 = ReplaceMethodBody $aug27 '        public void ConnectSelectedEndpoints()' @'
            Document document = Active();
            if (document == null) return;
            September04FieldGeometryCompletionCommands.ConnectEndpoints(document);
'@
WriteText $aug27Path $aug27

# Difference = Design Level - NG Level after creation and after each dynamic refresh.
$gridCall = 'September04FieldGeometryCompletionCommands.EnsureGridDifferenceColumns(document);'
$grid = ReadText $gridPath
$grid = EnsureBeforeTokenInMethod $grid '        public void CreateDynamicGrid()' 'document.Editor.SetImpliedSelection(new[] { tableId });' ('                ' + $gridCall) $gridCall
$grid = EnsureBeforeTokenInMethod $grid '        internal static int RefreshAll(Document document)' 'return refreshed;' ('            ' + $gridCall) $gridCall
WriteText $gridPath $grid

# CAD Supplementary menu additions.
$menu = ReadText $menuPath
$breakToken = $dq + 'CE_PLBREAKJUNCTIONS' + $dq
$multiToken = $dq + 'CE_MULTIFILLET' + $dq
$connectToken = $dq + 'CE_CONNECTENDPOINTS' + $dq
$multiLine = 'A(' + $dq + 'CE-Fillet Multiple Open Lines / Polylines' + $dq + ', ' + $multiToken + ', ' + $dq + 'Fillet multiple open LINE/LWPOLYLINE endpoints to a remembered radius; radius 0 gives an exact trim/extend crossing.' + $dq + ', ' + $dq + '01 Geometry' + $dq + '),'
$connectLine = 'A(' + $dq + 'CE-Connect Endpoints - New Green Polyline' + $dq + ', ' + $connectToken + ', ' + $dq + 'Connect the selected endpoint side within a remembered distance tolerance without changing the red source objects.' + $dq + ', ' + $dq + '01 Geometry' + $dq + '),'
$menu = EnsureAfterLineContaining $menu $breakToken $multiLine $multiToken
if (-not $menu.Contains($connectToken)) { $menu = EnsureAfterLineContaining $menu $multiToken $connectLine $connectToken }
WriteText $menuPath $menu

# Strict final staged guards.
$completion = ReadText $completionPath
$frontDoor = ReadText $frontDoorPath
$break = ReadText $breakPath
$geometry = ReadText $geometryPath
$aug27 = ReadText $aug27Path
$grid = ReadText $gridPath
$menu = ReadText $menuPath
foreach ($token in @('source.GetSplitCurves(splitPoints)','CollectCrossings(','CollectEndpointTJunctions(','ReplacePolylineGeometry(source, pieces[0]);','ZeroFilletCentreEndpoints(','ClosestSupportIntersection(','designValue - ngValue','ColorIndex = 3','UseDefaultValue = true;','_lastFilletRadius','_lastEndpointDistance')) {
    if (-not $completion.Contains($token)) { throw ('September 05 completion runtime guard missing: {0}' -f $token) }
}
$directConnect = '[CommandMethod(' + $dq + 'CE_CONNECTENDPOINTS' + $dq
$directMulti = '[CommandMethod(' + $dq + 'CE_MULTIFILLET' + $dq
$directDifference = '[CommandMethod(' + $dq + 'CE_GRIDDIFFERENCE' + $dq
if ($completion.Contains($directConnect) -or $completion.Contains($directMulti) -or $completion.Contains($directDifference)) { throw 'Completion runtime still contains direct command registration.' }
$frontMulti = '[CommandMethod(' + $dq + 'CE_TOOLS' + $dq + ', ' + $dq + 'CE_MULTIFILLET' + $dq
$frontDifference = '[CommandMethod(' + $dq + 'CE_TOOLS' + $dq + ', ' + $dq + 'CE_GRIDDIFFERENCE' + $dq
foreach ($token in @($frontMulti,$frontDifference)) { if (-not $frontDoor.Contains($token)) { throw ('Command front-door guard missing: {0}' -f $token) } }
if (-not $break.Contains('September04FieldGeometryCompletionCommands.BreakAtJunctions(document);')) { throw 'CE_PLBREAKJUNCTIONS final route is wrong.' }
foreach ($token in @('September04CadSupplementaryRuntime.ConstructionOffsets(document);','September04FieldGeometryCompletionCommands.MiddleConstructionLines(document);')) { if (-not $geometry.Contains($token)) { throw ('Final construction route missing: {0}' -f $token) } }
if (-not $aug27.Contains('September04FieldGeometryCompletionCommands.ConnectEndpoints(document);')) { throw 'CE_CONNECTENDPOINTS final route is wrong.' }
$gridDifferenceCount = ([regex]::Matches($grid,[regex]::Escape($gridCall))).Count
if ($gridDifferenceCount -lt 2) { throw ('Grid difference must run on creation and refresh; found {0} route(s).' -f $gridDifferenceCount) }
foreach ($token in @($multiToken,$connectToken)) { if (-not $menu.Contains($token)) { throw ('CAD Supplementary menu guard missing: {0}' -f $token) } }

Write-Host 'September 05 field-geometry completion finalization complete.' -ForegroundColor Green
Write-Host 'T/X native keep-source break, centre zero-fillet/XLINE modes, remembered multi-fillet, green endpoint connector and Design-NG grid difference are the final staged routes.' -ForegroundColor Green
