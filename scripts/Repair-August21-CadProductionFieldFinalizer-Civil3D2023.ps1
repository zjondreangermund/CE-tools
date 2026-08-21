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
        throw "August 21 CAD/field source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function MethodRange([string]$text,[string]$marker,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 21 CAD/field method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 21 CAD/field method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 21 CAD/field opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 21 CAD/field closing brace not found: $label" }
    return @($start,$open,$close)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $range = MethodRange $text $marker $label
    $open = [int]$range[1]
    $close = [int]$range[2]
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}
function ReplaceInsideMethod(
    [string]$text,[string]$marker,[string]$old,[string]$new,[string]$label) {
    $range = MethodRange $text $marker $label
    $start = [int]$range[0]
    $close = [int]$range[2]
    $length = $close - $start + 1
    $method = $text.Substring($start,$length)
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($method.Contains($new)) { return $text }
    if (-not $method.Contains($old)) {
        throw "August 21 CAD/field method anchor missing: $label"
    }
    $method = $method.Replace($old,$new)
    return $text.Substring(0,$start) + $method + $text.Substring($start+$length)
}

$floatingPath = Required 'FloatingToolsWindow.cs'
$dimensionPath = Required 'MultiDimensionCommands.cs'
$gridPath = Required 'August12SurveySiteGridCommands.cs'
$breakPath = Required 'PolylineNetworkPreparationCommands.cs'
[void](Required 'August21CadProductionCommands.cs')
[void](Required 'August21SafePolylineBreakEngine.cs')

# -----------------------------------------------------------------------------
# 1. Add CE-CAD Production as the LAST workflow tab (top/right end of the workflow
# strip) and point it at the dedicated six-section CAD production page.
# -----------------------------------------------------------------------------
$floating = ReadText $floatingPath
if (-not $floating.Contains('"cadproduction", "CE-CAD Production"')) {
    $anchor = @'
                Step("Create property report", "CE_FLOODPROPERTYREPORT"),
                Step("Export flood animation", "CE_FLOODANIMATIONHTML"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));
        }

        private static WorkflowDefinition Build(
'@ -replace "`r?`n","`r`n"
    if (-not $floating.Contains($anchor)) {
        throw 'August 21 CE-CAD Production tab insertion anchor missing after Flood workflow.'
    }
    $replacement = @'
                Step("Create property report", "CE_FLOODPROPERTYREPORT"),
                Step("Export flood animation", "CE_FLOODANIMATIONHTML"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "cadproduction", "CE-CAD Production", "CE-CAD PRODUCTION",
                "General CAD production, annotation, background preparation, cleanup, XREF and hatch production from the supplied CAD Production workflow.",
                tools, new[] { "CAD", "DRAW", "ANNOT", "HATCH", "XREF", "TRIM", "EXTEND", "GRID", "VERTEX", "BOUNDARY", "COLOR", "COLOUR" },
                Step("Open CE-CAD Production page", "CE_CADPRODUCTION"),
                Step("General: Break crossings / T-junctions", "CE_PLBREAKJUNCTIONS"),
                Step("General: Multiple Boundary Trim / Extend", "CE_BOUNDARYEDITTOOLS"),
                Step("General: Vertex Setting-Out", "CE_VERTEXSETTINGOUT"),
                Step("General: Grid Setting-Out", "CE_GRIDSETTINGOUT"),
                Step("Annotation: Multiple Dimensions", "CE_MULTIDIM"),
                Step("Annotation: Annotation Settings", "CE_ANNOTSETTINGS"),
                Step("Background: Preparation Tools", "CE_BACKGROUNDPREPTOOLS"),
                Step("Cleanup: Cleanup Window", "CE_DRAWCLEAN"),
                Step("XREF: Project Tools", "CE_XREFPROJECTTOOLS"),
                Step("Hatch: Hatch Tools", "CE_HATCHTOOLS"));
        }

        private static WorkflowDefinition Build(
'@ -replace "`r?`n","`r`n"
    $floating = $floating.Replace($anchor,$replacement)
}
WriteText $floatingPath $floating

# -----------------------------------------------------------------------------
# 2. Open-polyline chain dimensions must use the SAME Metres/Millimetres setting
# as the normal CE-Multiple Dimensions path. AddDimension calls SetFromStyle(), so
# the per-object Dimlfac/Dimdec overrides are deliberately applied AFTER it.
# -----------------------------------------------------------------------------
$dimension = ReadText $dimensionPath
$dimension = $dimension.Replace(
    '                    requestedStyle);',
    '                    requestedStyle,' + "`r`n" + '                    settings.Text("ValueUnits"));')
$dimension = $dimension.Replace(
    '            string requestedStyle)' + "`r`n" + '        {',
    '            string requestedStyle,' + "`r`n" + '            string valueUnits)' + "`r`n" + '        {')
if ($dimension.Contains('private static void DimensionOpenPolylineChain(') -and
    -not $dimension.Contains('double chainMeasurementFactor = chainOutputMillimetres ? 1000.0 : 1.0;')) {
    $old = @'
            int selectedCount = selection.Value.Count;
            int acceptedCount = 0;
'@
    $new = @'
            bool chainOutputMillimetres = string.Equals(
                valueUnits,
                "Millimetres",
                StringComparison.OrdinalIgnoreCase);
            double chainMeasurementFactor = chainOutputMillimetres ? 1000.0 : 1.0;

            int selectedCount = selection.Value.Count;
            int acceptedCount = 0;
'@
    $dimension = ReplaceInsideMethod $dimension `
        '        private static void DimensionOpenPolylineChain(' `
        $old $new 'Open-polyline chain measurement factor'
}
if ($dimension.Contains('private static void DimensionOpenPolylineChain(') -and
    -not $dimension.Contains('dimension.Dimlfac = chainMeasurementFactor;')) {
    $old = @'
                        AddDimension(
                            document.Database,
                            transaction,
                            space,
                            dimension,
                            ref created);
'@
    $new = @'
                        AddDimension(
                            document.Database,
                            transaction,
                            space,
                            dimension,
                            ref created);
                        try
                        {
                            dimension.Dimlfac = chainMeasurementFactor;
                            if (chainOutputMillimetres) dimension.Dimdec = 0;
                            dimension.RecordGraphicsModified(true);
                        }
                        catch { }
'@
    $dimension = ReplaceInsideMethod $dimension `
        '        private static void DimensionOpenPolylineChain(' `
        $old $new 'Open-polyline chain DIMLFAC/DIMDEC override'
}
if (-not $dimension.Contains('dimension.Dimlfac = chainMeasurementFactor;')) {
    throw 'August 21 open-polyline chain millimetre repair was not installed.'
}
WriteText $dimensionPath $dimension

# -----------------------------------------------------------------------------
# 3. Site Grid display. Mark every new child graphics-dirty, explicitly queue the
# transaction manager for a graphics flush, REGEN the active editor, update the
# screen, and queue one deferred REGEN after the command/event transaction returns.
# This replaces the field symptom where AUDIT/OVERKILL/PURGE made the grid appear.
# -----------------------------------------------------------------------------
$grid = ReadText $gridPath
if (-not $grid.Contains('entity.RecordGraphicsModified(true);')) {
    $grid = $grid.Replace(
        '            transaction.AddNewlyCreatedDBObject(entity, true);',
        '            transaction.AddNewlyCreatedDBObject(entity, true);' + "`r`n" +
        '            try { entity.RecordGraphicsModified(true); } catch { }')
}
$grid = ReplaceInsideMethod $grid `
    '        public void CreateOrUpdateSiteGrid()' `
    '            document.Editor.Regen();' `
    '            August21DisplayRefresh.Flush(document);' `
    'Site Grid create immediate graphics flush'
$grid = ReplaceInsideMethod $grid `
    '        public void RefreshSiteGrids()' `
    '            document.Editor.Regen();' `
    '            August21DisplayRefresh.Flush(document);' `
    'Site Grid manual refresh immediate graphics flush'
if (-not $grid.Contains('August21DisplayRefresh.Flush(document);' + "`r`n" + '            return refreshed;')) {
    $grid = ReplaceInsideMethod $grid `
        '        internal static int RefreshAll(' `
        '            return refreshed;' `
        '            August21DisplayRefresh.Flush(document);' + "`r`n" + '            return refreshed;' `
        'Site Grid linked refresh immediate graphics flush'
}
WriteText $gridPath $grid

# -----------------------------------------------------------------------------
# 4. Break at Crossings/T-junctions. Delegate the public command to the verified
# per-source engine. It creates/commits replacements, verifies count/length in a
# second transaction, and only then erases that one source. Failed sources remain.
# -----------------------------------------------------------------------------
$break = ReadText $breakPath
$breakBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            August21SafePolylineBreakEngine.Run(document);
'@
$break = ReplaceMethodBody $break `
    '        public void BreakAtAllCrossingsAndJunctions()' `
    $breakBody `
    'CE_PLBREAKJUNCTIONS verified per-source delegation'
WriteText $breakPath $break

# Final semantic guards.
$guards = @{
    'CE-CAD Production workflow tab' = (ReadText $floatingPath).Contains('"cadproduction", "CE-CAD Production"')
    'CE-CAD Production command' = (ReadText (Join-Path $src 'August21CadProductionCommands.cs')).Contains('"CE_CADPRODUCTION"')
    'Open-polyline chain millimetres' = (ReadText $dimensionPath).Contains('dimension.Dimlfac = chainMeasurementFactor;')
    'Site Grid graphics flush' = (ReadText $gridPath).Contains('August21DisplayRefresh.Flush(document);')
    'Safe polyline break delegation' = (ReadText $breakPath).Contains('August21SafePolylineBreakEngine.Run(document);')
}
foreach ($entry in $guards.GetEnumerator()) {
    if (-not $entry.Value) { throw "August 21 CAD/field finalizer guard failed: $($entry.Key)" }
}

Write-Host 'August 21 CE-CAD Production and field finalizer passed:' -ForegroundColor Green
Write-Host ' - CE-CAD Production is the final/right workflow tab and opens the six-section CAD page.' -ForegroundColor Green
Write-Host ' - Open-polyline chain dimensions honour Millimetres with DIMLFAC x1000 and zero decimals.' -ForegroundColor Green
Write-Host ' - Site Grid queues graphics flush + screen update + deferred REGEN after create/refresh.' -ForegroundColor Green
Write-Host ' - Break at Crossings/T-junctions uses verified replacements before source erase.' -ForegroundColor Green

# Regression trigger: validate this exact final staged field batch before merge.
