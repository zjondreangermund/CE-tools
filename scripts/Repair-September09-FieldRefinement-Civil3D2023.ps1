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
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('September 09 refinement source missing: {0}' -f $path) }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) -replace '\r?\n',"`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace '\r?\n',"`r`n"),$utf8) }
function MethodBounds([string]$text,[string]$marker) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw ('Method marker missing: {0}' -f $marker) }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw ('Opening brace missing: {0}' -f $marker) }
    $depth=0; $close=-1
    for($i=$open;$i -lt $text.Length;$i++) {
        if($text[$i] -eq '{'){$depth++}
        elseif($text[$i] -eq '}'){$depth--; if($depth -eq 0){$close=$i;break}}
    }
    if($close -lt 0){throw ('Closing brace missing: {0}' -f $marker)}
    return [pscustomobject]@{Open=$open;Close=$close}
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $b=MethodBounds $text $marker
    $body=($body -replace '\r?\n',"`r`n").Trim("`r","`n")
    return $text.Substring(0,$b.Open+1)+"`r`n"+$body+"`r`n        "+$text.Substring($b.Close)
}

$runtimePath = Required 'September09FieldRefinementRuntime.cs'
$sep05FrontPath = Required 'September05FieldGeometryCommandFrontDoor.cs'
$sep09FrontPath = Required 'September09FieldEngineeringCommandFrontDoor.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$breakPath = Required 'August25CadSupplementaryBreakEngine.cs'
$aug27Path = Required 'August27DynamicSlopeGridHatchCommands.cs'
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$menuPath = Required 'August24FieldCompletionCommands.cs'

# Raw-source compatibility: foreach iteration variables are immutable in C#.
# Keep the paired boundary endpoints in local variables instead of reassigning b.
$runtime = ReadText $runtimePath
$oldFlip = @'
                    if (dot < 0.0) { Point2d swap = b.A; b = new BoundarySegment { Source = b.Source, A = b.B, B = swap }; db = -db; }

                    Point2d midA = Mid(a.A, a.B);
                    Point2d midB = Mid(b.A, b.B);
'@ -replace '\r?\n',"`r`n"
$newFlip = @'
                    Point2d bStart = b.A;
                    Point2d bEnd = b.B;
                    if (dot < 0.0) { Point2d swap = bStart; bStart = bEnd; bEnd = swap; db = -db; }

                    Point2d midA = Mid(a.A, a.B);
                    Point2d midB = Mid(bStart, bEnd);
'@ -replace '\r?\n',"`r`n"
if ($runtime.Contains($oldFlip.Trim())) { $runtime = $runtime.Replace($oldFlip.Trim(),$newFlip.Trim()) }
$runtime = $runtime.Replace('double b0 = (b.A - a.A).DotProduct(da);','double b0 = (bStart - a.A).DotProduct(da);')
$runtime = $runtime.Replace('double b1 = (b.B - a.A).DotProduct(da);','double b1 = (bEnd - a.A).DotProduct(da);')
$runtime = $runtime.Replace('if (!PointOnSupportAtProjection(b.A, db, a.A, da, low, out bAt0) ||','if (!PointOnSupportAtProjection(bStart, db, a.A, da, low, out bAt0) ||')
$runtime = $runtime.Replace('!PointOnSupportAtProjection(b.A, db, a.A, da, high, out bAt1)) continue;','!PointOnSupportAtProjection(bStart, db, a.A, da, high, out bAt1)) continue;')
$runtime = $runtime.Replace('string selectedStyle = styleSettings.Text("Style");','string selectedStyle = lineStyles.Count > 0 ? styleSettings.Text("Style") : string.Empty;')
WriteText $runtimePath $runtime

# Grid DIFFERENCE: preserve Design-NG calculation, then copy DESIGN LEVEL text
# height/text style/alignment into every DIFFERENCE header/data cell.
$grid = ReadText $gridPath
$oldPerTable = 'September09FieldEngineeringRuntime.EnsureGridDifferenceColumn(table);'
$newPerTable = 'September09FieldRefinementRuntime.EnsureStyledGridDifferenceColumn(table);'
$grid = $grid.Replace($oldPerTable,$newPerTable)
WriteText $gridPath $grid

$sep05 = ReadText $sep05FrontPath
$sep05 = ReplaceMethodBody $sep05 '        public void MultiFillet()' @'
            Autodesk.AutoCAD.ApplicationServices.Document document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September09FieldRefinementRuntime.MultiFillet(document);
'@
$sep05 = ReplaceMethodBody $sep05 '        public void GridDifference()' @'
            Autodesk.AutoCAD.ApplicationServices.Document document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int changed = September09FieldRefinementRuntime.EnsureStyledGridDifferenceColumns(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_GRIDDIFFERENCE complete. Styled Design-NG table(s) updated={0}.", changed);
'@
WriteText $sep05FrontPath $sep05

$sep09 = ReadText $sep09FrontPath
$sep09 = ReplaceMethodBody $sep09 '        public void RoadReserveCentreLines()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September09FieldRefinementRuntime.RoadReserveCentrePolylines(document);
'@
$sep09 = ReplaceMethodBody $sep09 '        public void ConstructionFillet()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September09FieldRefinementRuntime.MultiFillet(document);
'@
$sep09 = ReplaceMethodBody $sep09 '        public void SlopeAnnotations()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September09FieldRefinementRuntime.SlopeAnnotations(document);
'@
WriteText $sep09FrontPath $sep09

# Existing break command name now means CUT ALL selected routes at every X/T and keep
# all successful LINE/LWPOLYLINE spans as separate database objects.
$break = ReadText $breakPath
$break = ReplaceMethodBody $break '        internal static void Run(Document document)' @'
            September09FieldRefinementRuntime.CutAllJunctions(document);
'@
WriteText $breakPath $break

# Existing Surface Slope command now opens the popup surface chooser and creates
# native Civil 3D SurfaceSlopeLabel entities.
$aug27 = ReadText $aug27Path
$aug27 = ReplaceMethodBody $aug27 '        internal static void SurfaceSlopeArrows(Document document)' @'
            September09FieldRefinementRuntime.SurfaceSlopeLabelsPopup(document);
'@
WriteText $aug27Path $aug27

# Preserve AutoCAD REDO after UNDO. Some ObjectModified/ObjectErased events arrive
# after CommandEnded; suppress CE automatic refresh briefly so no new undo item is
# inserted between UNDO and REDO. Any new normal command clears the suppression.
$universal = ReadText $universalPath
if (-not $universal.Contains('_undoRedoSuppressUntilUtc')) {
    $universal = $universal.Replace(
        '        private static bool _undoRedoActive;',
        '        private static bool _undoRedoActive;' + "`r`n" + '        private static DateTime _undoRedoSuppressUntilUtc = DateTime.MinValue;')
}
$universal = $universal.Replace(
    '            if (!IsUndoRedo(command)) return;' + "`r`n" + '            _undoRedoActive = true;',
    '            if (!IsUndoRedo(command))' + "`r`n" + '            {' + "`r`n" + '                _undoRedoSuppressUntilUtc = DateTime.MinValue;' + "`r`n" + '                return;' + "`r`n" + '            }' + "`r`n" + '            _undoRedoActive = true;')
$undoEndAnchor = '                _lastChangeUtc = DateTime.UtcNow;' + "`r`n" + '                return;'
$undoEndReplacement = '                _lastChangeUtc = DateTime.UtcNow;' + "`r`n" + '                _undoRedoSuppressUntilUtc = DateTime.UtcNow.AddSeconds(2.5);' + "`r`n" + '                return;'
if ($universal.Contains($undoEndAnchor) -and -not $universal.Contains('DateTime.UtcNow.AddSeconds(2.5)')) { $universal = $universal.Replace($undoEndAnchor,$undoEndReplacement) }
$universal = $universal.Replace(
    '            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;',
    '            if (_busy || _undoRedoActive || DateTime.UtcNow < _undoRedoSuppressUntilUtc || e == null || e.DBObject == null) return;')
$idleAnchor = '            if (!Enabled || !_pending || _busy || _undoRedoActive || active == null) return;'
$idleReplacement = $idleAnchor + "`r`n" + '            if (DateTime.UtcNow < _undoRedoSuppressUntilUtc) { _pending = false; return; }'
if ($universal.Contains($idleAnchor) -and -not $universal.Contains('DateTime.UtcNow < _undoRedoSuppressUntilUtc) { _pending = false; return; }')) { $universal = $universal.Replace($idleAnchor,$idleReplacement) }
WriteText $universalPath $universal

# Update menu wording to match field behavior.
$menu = ReadText $menuPath
$menu = $menu.Replace('Create finite red centre polylines midway between multiple selected cadastral/erf road-reserve boundaries.','Create joined/filleted centre polylines from closed cadastral or open/normal road-reserve boundaries; T/X junctions share exact endpoints.')
$menu = $menu.Replace('Detect facing cadastral/erf reserve boundaries and create finite red centre polylines without changing source parcels.','Create joined/filleted centres from closed cadastral or open/normal road-reserve boundaries without changing source geometry.')
$menu = $menu.Replace('Create native dynamic Civil 3D SurfaceSlopeLabel elements attached to the selected surface.','Choose the surface in a popup and create native dynamic Civil 3D SurfaceSlopeLabel elements.')
$menu = $menu.Replace('Choose native surface slope labels, slope between two feature lines, or slope along feature lines.','Choose native surface labels, one Civil 3D crossfall label between two feature lines, dynamic crossfalls, or slope along feature lines.')
WriteText $menuPath $menu

# Absolute-final guards.
$runtime = ReadText $runtimePath
$sep05 = ReadText $sep05FrontPath
$sep09 = ReadText $sep09FrontPath
$grid = ReadText $gridPath
$break = ReadText $breakPath
$aug27 = ReadText $aug27Path
$universal = ReadText $universalPath
foreach($token in @(
    'TextHeight = table.Cells[row, designColumn].TextHeight',
    'Open/normal road boundaries','SplitAtAllIntersections(','TraceNetworkChains(','BuildFilletedPolyline(',
    'Support-line crossings, not arbitrary nearest endpoints',
    'ALL-SEGMENTS complete','SplitPolylineAll(',
    'SurfaceSlopeLabelsPopup(','ReadSurfaceChoices(',
    'Single slope between two feature lines - Civil 3D','CivilGeneralSegmentLabel.Create','CivilFeatureLine.Create',
    'DateTime.UtcNow.AddSeconds(2.5)')) {
    if (-not $runtime.Contains($token) -and -not $universal.Contains($token)) { throw ('September 09 refinement guard missing: {0}' -f $token) }
}
if (-not $grid.Contains('September09FieldRefinementRuntime.EnsureStyledGridDifferenceColumn(table);')) { throw 'Styled DIFFERENCE is not inside the grid create/refresh Table transaction.' }
if (-not $sep05.Contains('September09FieldRefinementRuntime.MultiFillet(document);')) { throw 'CE_MULTIFILLET final route is wrong.' }
if (-not $sep09.Contains('September09FieldRefinementRuntime.RoadReserveCentrePolylines(document);')) { throw 'Road reserve centreline final route is wrong.' }
if (-not $sep09.Contains('September09FieldRefinementRuntime.SlopeAnnotations(document);')) { throw 'Slope options final route is wrong.' }
if (-not $break.Contains('September09FieldRefinementRuntime.CutAllJunctions(document);')) { throw 'CE_PLBREAKJUNCTIONS final all-segments route is wrong.' }
if (-not $aug27.Contains('September09FieldRefinementRuntime.SurfaceSlopeLabelsPopup(document);')) { throw 'Surface slope popup/native final route is wrong.' }
if (-not $universal.Contains('_undoRedoSuppressUntilUtc')) { throw 'UNDO/REDO post-command suppression is missing.' }

Write-Host 'September 09 field-refinement finalization complete.' -ForegroundColor Green
Write-Host 'Grid difference formatting, joined/filleted road centres with open-boundary support, support-intersection multi-fillet, all-segment X/T cuts, popup surface labels, native Civil single crossfall label and REDO preservation are final staged routes.' -ForegroundColor Green
