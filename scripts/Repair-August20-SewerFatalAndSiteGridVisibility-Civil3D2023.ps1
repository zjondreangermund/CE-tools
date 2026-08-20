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
        throw "August 20 fatal/site-grid hotfix source missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 hotfix method signature not found ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 hotfix opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 hotfix closing brace not found: $label" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close + 1)
}

function MoveSurfaceResolutionBeforeSelection(
    [string]$text,
    [string]$methodSignature,
    [string]$selectionMarker,
    [string]$label) {

    $methodStart = $text.IndexOf($methodSignature,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) { throw "August 20 hotfix command method not found: $label" }
    $open = $text.IndexOf('{',$methodStart)
    if ($open -lt 0) { throw "August 20 hotfix command opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 hotfix command closing brace not found: $label" }

    $method = $text.Substring($methodStart,$close-$methodStart+1)
    $selectionIndex = $method.IndexOf($selectionMarker,[StringComparison]::Ordinal)
    if ($selectionIndex -lt 0) { throw "August 20 hotfix selection marker not found ($label): $selectionMarker" }

    $surfaceStart = $method.IndexOf(
        '            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));',
        [StringComparison]::Ordinal)
    if ($surfaceStart -lt 0) { throw "August 20 hotfix popup Surface resolver not found: $label" }

    if ($surfaceStart -gt $selectionIndex) {
        $ifStart = $method.IndexOf('            if (',$surfaceStart,[StringComparison]::Ordinal)
        if ($ifStart -lt 0) { throw "August 20 hotfix Surface validation block not found: $label" }
        $braceOpen = $method.IndexOf('{',$ifStart)
        if ($braceOpen -lt 0) { throw "August 20 hotfix Surface validation opening brace not found: $label" }
        $braceDepth = 0
        $braceClose = -1
        for ($i=$braceOpen; $i -lt $method.Length; $i++) {
            if ($method[$i] -eq '{') { $braceDepth++ }
            elseif ($method[$i] -eq '}') {
                $braceDepth--
                if ($braceDepth -eq 0) { $braceClose = $i; break }
            }
        }
        if ($braceClose -lt 0) { throw "August 20 hotfix Surface validation closing brace not found: $label" }
        $blockEnd = $braceClose + 1
        while ($blockEnd -lt $method.Length -and ($method[$blockEnd] -eq "`r" -or $method[$blockEnd] -eq "`n")) { $blockEnd++ }
        $surfaceBlock = $method.Substring($surfaceStart,$blockEnd-$surfaceStart)
        $method = $method.Remove($surfaceStart,$blockEnd-$surfaceStart)
        $selectionIndex = $method.IndexOf($selectionMarker,[StringComparison]::Ordinal)
        if ($selectionIndex -lt 0) { throw "August 20 hotfix selection marker moved unexpectedly: $label" }
        $method = $method.Insert($selectionIndex,$surfaceBlock + "`r`n")
    }

    $finalSelection = $method.IndexOf($selectionMarker,[StringComparison]::Ordinal)
    $finalSurface = $method.IndexOf(
        '            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));',
        [StringComparison]::Ordinal)
    if ($finalSurface -lt 0 -or $finalSelection -lt 0 -or $finalSurface -gt $finalSelection) {
        throw "August 20 hotfix failed to resolve the selected Surface before polygon selection: $label"
    }

    return $text.Substring(0,$methodStart) + $method + $text.Substring($close+1)
}

$helperPath = Required 'August20SurfaceAndDimensionHelpers.cs'
$cadastralPath = Required 'August19CadastralSewerRouteCommands.cs'
$midblockPath = Required 'August11MidblockSewerProductionCommands.cs'
$roadReservePath = Required 'August19RoadReserveSewerAndSafetyCommands.cs'
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'

# -----------------------------------------------------------------------------
# 1. Shared Civil Surface guard.  Never ask Civil 3D for an elevation outside the
#    surface entity extents.  A managed try/catch cannot reliably contain every
#    native geometry failure, so reject bad XY samples before FindElevationAtXY.
# -----------------------------------------------------------------------------
$helper = ReadText $helperPath
if (-not $helper.Contains('using Autodesk.AutoCAD.Geometry;')) {
    $usingAnchor = 'using Autodesk.AutoCAD.DatabaseServices;'
    if (-not $helper.Contains($usingAnchor)) { throw 'August 20 hotfix Geometry using anchor missing.' }
    $helper = $helper.Replace($usingAnchor,$usingAnchor + "`r`nusing Autodesk.AutoCAD.Geometry;")
}
if (-not $helper.Contains('internal static bool TryElevationSafe(')) {
    $classEndAnchor = @'
            return ObjectId.Null;
        }
    }

    /// <summary>
    /// Applies user-selected presentation overrides to the CE annotative copy of
'@ -replace "`n","`r`n"
    if (-not $helper.Contains($classEndAnchor)) {
        throw 'August 20 hotfix could not locate the Surface helper class end.'
    }
    $safeElevation = @'
            return ObjectId.Null;
        }

        internal static bool TryElevationSafe(
            CivilSurface surface,
            Point2d point,
            out double elevation)
        {
            elevation = double.NaN;
            if (surface == null ||
                double.IsNaN(point.X) || double.IsInfinity(point.X) ||
                double.IsNaN(point.Y) || double.IsInfinity(point.Y))
                return false;

            try
            {
                Extents3d extents = surface.GeometricExtents;
                double span = Math.Max(
                    Math.Abs(extents.MaxPoint.X - extents.MinPoint.X),
                    Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y));
                double margin = Math.Max(0.01, span * 1e-8);
                if (point.X < extents.MinPoint.X - margin ||
                    point.X > extents.MaxPoint.X + margin ||
                    point.Y < extents.MinPoint.Y - margin ||
                    point.Y > extents.MaxPoint.Y + margin)
                    return false;
            }
            catch
            {
                // Some surface subclasses do not expose useful extents.  In that
                // case retain the guarded API call below rather than failing the
                // complete planning workflow.
            }

            try
            {
                elevation = surface.FindElevationAtXY(point.X, point.Y);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch
            {
                elevation = double.NaN;
                return false;
            }
        }
    }

    /// <summary>
    /// Applies user-selected presentation overrides to the CE annotative copy of
'@ -replace "`n","`r`n"
    $helper = $helper.Replace($classEndAnchor,$safeElevation)
}
WriteText $helperPath $helper

# -----------------------------------------------------------------------------
# 2. Resolve the popup-selected Surface BEFORE Editor.GetSelection/SelectImplied.
#    This removes the one new Civil API enumeration that all three planners were
#    performing immediately after the user completed polygon selection.
# -----------------------------------------------------------------------------
$cadastral = ReadText $cadastralPath
$cadastral = MoveSurfaceResolutionBeforeSelection \
    $cadastral \
    'public void CreateSewerFromCadastral()' \
    '            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' \
    'Cadastral Sewer'
$cadastralElevation = @'
            return August20SurfaceChoice.TryElevationSafe(surface, point, out elevation);
'@
$cadastral = ReplaceMethodBody \
    $cadastral \
    'private static bool TryElevation(Surface surface, Point2d point, out double elevation)' \
    $cadastralElevation \
    'Cadastral safe Surface sampling'
WriteText $cadastralPath $cadastral

$road = ReadText $roadReservePath
$road = MoveSurfaceResolutionBeforeSelection \
    $road \
    'public void SewerRoadReserve()' \
    '            List<ObjectId> ids = ResolveParcels(document, model.Text("Scope"));' \
    'Road Reserve Sewer'
$roadElevation = @'
            return August20SurfaceChoice.TryElevationSafe(surface, point, out elevation);
'@
$road = ReplaceMethodBody \
    $road \
    'private static bool TryElevation(Surface surface, Point2d point, out double elevation)' \
    $roadElevation \
    'Road Reserve safe Surface sampling'
WriteText $roadReservePath $road

$mid = ReadText $midblockPath
$mid = MoveSurfaceResolutionBeforeSelection \
    $mid \
    'public void CreateProductionRoutes()' \
    '            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' \
    'Midblock Sewer'
$midAverage = @'
            var values = new List<double>();
            foreach (Point2d point in points)
            {
                double value;
                if (August20SurfaceChoice.TryElevationSafe(surface, point, out value))
                    values.Add(value);
            }
            return values.Count == 0 ? double.NaN : values.Average();
'@
$mid = ReplaceMethodBody \
    $mid \
    'private static double AverageSurface(Surface surface, IEnumerable<Point2d> points)' \
    $midAverage \
    'Midblock safe Surface sampling'
WriteText $midblockPath $mid

# -----------------------------------------------------------------------------
# 3. Site Grid final field guard.  The successful build must contain visible
#    line/label presentation AND a real circle marker for every optional DBPoint.
#    DBPoint visibility alone depends on PDMODE/PDSIZE and is not field-reliable.
# -----------------------------------------------------------------------------
$site = ReadText $siteGridPath
foreach ($marker in @(
    'line.Color = boundary.Color;',
    'line.LineWeight = boundary.LineWeight;',
    'label.Color = boundary.Color;',
    'point.Color = boundary.Color;',
    'var pointMarker = new Circle(',
    '"PM",',
    'pointMarker.Color = boundary.Color;')) {
    if (-not $site.Contains($marker)) {
        throw "August 20 Site Grid field visibility marker missing: $marker"
    }
}

if (-not $site.Contains('pointMarker.LineWeight = LineWeight.LineWeight050;')) {
    $markerLine = '                            pointMarker.LineWeight = boundary.LineWeight;'
    if (-not $site.Contains($markerLine)) {
        throw 'August 20 Site Grid point-marker lineweight anchor missing.'
    }
    $site = $site.Replace(
        $markerLine,
        '                            pointMarker.LineWeight = LineWeight.LineWeight050;')
}
WriteText $siteGridPath $site

# -----------------------------------------------------------------------------
# 4. Hard final guards.  A build is rejected if any planner again resolves its
#    selected Civil Surface after polygon selection or bypasses guarded sampling.
# -----------------------------------------------------------------------------
foreach ($item in @(
    @{ Name='Cadastral'; Path=$cadastralPath; Method='public void CreateSewerFromCadastral()'; Selection='List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' },
    @{ Name='Road Reserve'; Path=$roadReservePath; Method='public void SewerRoadReserve()'; Selection='List<ObjectId> ids = ResolveParcels(document, model.Text("Scope"));' },
    @{ Name='Midblock'; Path=$midblockPath; Method='public void CreateProductionRoutes()'; Selection='List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' })) {
    $check = ReadText $item.Path
    $methodStart = $check.IndexOf($item.Method,[StringComparison]::Ordinal)
    $surfaceIndex = $check.IndexOf('ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));',$methodStart,[StringComparison]::Ordinal)
    $selectionIndex = $check.IndexOf($item.Selection,$methodStart,[StringComparison]::Ordinal)
    if ($surfaceIndex -lt 0 -or $selectionIndex -lt 0 -or $surfaceIndex -gt $selectionIndex) {
        throw "August 20 $($item.Name) fatal-safety guard failed: Surface resolution is not before polygon selection."
    }
}

$cadastralCheck = ReadText $cadastralPath
$roadCheck = ReadText $roadReservePath
$midCheck = ReadText $midblockPath
if (-not $cadastralCheck.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) {
    throw 'August 20 Cadastral safe elevation guard missing.'
}
if (-not $roadCheck.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) {
    throw 'August 20 Road Reserve safe elevation guard missing.'
}
if (-not $midCheck.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out value)')) {
    throw 'August 20 Midblock safe elevation guard missing.'
}

Write-Host 'August 20 sewer fatal-safety hotfix passed: selected Civil Surfaces are resolved before polygon selection.' -ForegroundColor Green
Write-Host 'Cadastral, Midblock and Road Reserve surface samples now reject out-of-surface XY points before FindElevationAtXY.' -ForegroundColor Green
Write-Host 'Site Grid field visibility guard passed: grid geometry inherits the frame presentation and every DBPoint has a visible linked circle marker.' -ForegroundColor Green
