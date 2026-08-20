[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 20 fatal/site-grid hotfix source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label,[string]$fallbackMethodName='') {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0 -and -not [string]::IsNullOrWhiteSpace($fallbackMethodName)) {
        $pattern = '(?m)^\s*private\s+static\s+[^\r\n{;]*\b' + [regex]::Escape($fallbackMethodName) + '\s*\('
        $matches = [regex]::Matches($text,$pattern)
        if ($matches.Count -eq 1) {
            $start = $matches[0].Index
            Write-Host ("August 20 hotfix using structural method fallback for {0}: {1}" -f $label,$fallbackMethodName) -ForegroundColor DarkCyan
        }
        elseif ($matches.Count -gt 1) {
            throw "August 20 hotfix method fallback is ambiguous ($label): $fallbackMethodName matches=$($matches.Count)"
        }
    }
    if ($start -lt 0) { throw "August 20 hotfix method signature not found ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 hotfix opening brace not found: $label" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { $close=$i; break } }
    }
    if ($close -lt 0) { throw "August 20 hotfix closing brace not found: $label" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close+1)
}

function MoveSurfaceResolutionBeforeSelection([string]$text,[string]$methodSignature,[string]$selectionMarker,[string]$label) {
    $methodStart = $text.IndexOf($methodSignature,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) { throw "August 20 hotfix command method not found: $label" }
    $open = $text.IndexOf('{',$methodStart)
    if ($open -lt 0) { throw "August 20 hotfix command opening brace not found: $label" }
    $depth=0; $close=-1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { $close=$i; break } }
    }
    if ($close -lt 0) { throw "August 20 hotfix command closing brace not found: $label" }
    $method = $text.Substring($methodStart,$close-$methodStart+1)
    $selectionIndex = $method.IndexOf($selectionMarker,[StringComparison]::Ordinal)
    if ($selectionIndex -lt 0) { throw "August 20 hotfix selection marker not found ($label): $selectionMarker" }
    $resolver = '            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));'
    $surfaceStart = $method.IndexOf($resolver,[StringComparison]::Ordinal)
    if ($surfaceStart -lt 0) { throw "August 20 hotfix popup Surface resolver not found: $label" }

    if ($surfaceStart -gt $selectionIndex) {
        $ifStart = $method.IndexOf('            if (',$surfaceStart,[StringComparison]::Ordinal)
        if ($ifStart -lt 0) { throw "August 20 hotfix Surface validation block not found: $label" }
        $braceOpen = $method.IndexOf('{',$ifStart)
        if ($braceOpen -lt 0) { throw "August 20 hotfix Surface validation opening brace not found: $label" }
        $braceDepth=0; $braceClose=-1
        for ($i=$braceOpen; $i -lt $method.Length; $i++) {
            if ($method[$i] -eq '{') { $braceDepth++ }
            elseif ($method[$i] -eq '}') { $braceDepth--; if ($braceDepth -eq 0) { $braceClose=$i; break } }
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

    $finalSurface = $method.IndexOf($resolver,[StringComparison]::Ordinal)
    $finalSelection = $method.IndexOf($selectionMarker,[StringComparison]::Ordinal)
    if ($finalSurface -lt 0 -or $finalSelection -lt 0 -or $finalSurface -gt $finalSelection) { throw "August 20 hotfix failed to resolve the selected Surface before polygon selection: $label" }
    return $text.Substring(0,$methodStart) + $method + $text.Substring($close+1)
}

$helperPath = Required 'August20SurfaceAndDimensionHelpers.cs'
$cadastralPath = Required 'August19CadastralSewerRouteCommands.cs'
$midblockPath = Required 'August11MidblockSewerProductionCommands.cs'
$roadReservePath = Required 'August19RoadReserveSewerAndSafetyCommands.cs'
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'

# Shared guarded elevation helper.
$helper = ReadText $helperPath
if (-not $helper.Contains('using Autodesk.AutoCAD.Geometry;')) {
    $usingAnchor='using Autodesk.AutoCAD.DatabaseServices;'
    if (-not $helper.Contains($usingAnchor)) { throw 'August 20 hotfix Geometry using anchor missing.' }
    $helper=$helper.Replace($usingAnchor,$usingAnchor+"`r`nusing Autodesk.AutoCAD.Geometry;")
}
if (-not $helper.Contains('internal static bool TryElevationSafe(')) {
    $anchor = "            return ObjectId.Null;`r`n        }`r`n    }`r`n`r`n    /// <summary>`r`n    /// Applies user-selected presentation overrides to the CE annotative copy of"
    if (-not $helper.Contains($anchor)) { throw 'August 20 hotfix could not locate the Surface helper class end.' }
    $replacement = @'
            return ObjectId.Null;
        }

        internal static bool TryElevationSafe(CivilSurface surface, Point2d point, out double elevation)
        {
            elevation = double.NaN;
            if (surface == null || double.IsNaN(point.X) || double.IsInfinity(point.X) || double.IsNaN(point.Y) || double.IsInfinity(point.Y))
                return false;
            try
            {
                Extents3d extents = surface.GeometricExtents;
                double span = Math.Max(Math.Abs(extents.MaxPoint.X - extents.MinPoint.X), Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y));
                double margin = Math.Max(0.01, span * 1e-8);
                if (point.X < extents.MinPoint.X - margin || point.X > extents.MaxPoint.X + margin || point.Y < extents.MinPoint.Y - margin || point.Y > extents.MaxPoint.Y + margin)
                    return false;
            }
            catch { }
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
    $helper=$helper.Replace($anchor,$replacement.TrimEnd("`r","`n"))
}
WriteText $helperPath $helper

# Cadastral: resolve Surface before polygon selection and use guarded samples.
$cadastral=ReadText $cadastralPath
$cadastral=MoveSurfaceResolutionBeforeSelection $cadastral 'public void CreateSewerFromCadastral()' '            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' 'Cadastral Sewer'
$cadastralBody=@'
            return August20SurfaceChoice.TryElevationSafe(surface, point, out elevation);
'@
if (-not $cadastral.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) {
    $cadastral=ReplaceMethodBody $cadastral 'private static bool TryElevation(Surface surface, Point2d point, out double elevation)' $cadastralBody 'Cadastral safe Surface sampling' 'TryElevation'
}
else {
    Write-Host 'August 20 Cadastral Surface sampling is already guarded.' -ForegroundColor DarkGreen
}
WriteText $cadastralPath $cadastral

# Road Reserve: same common crash boundary.
$road=ReadText $roadReservePath
$road=MoveSurfaceResolutionBeforeSelection $road 'public void SewerRoadReserve()' '            List<ObjectId> ids = ResolveParcels(document, model.Text("Scope"));' 'Road Reserve Sewer'
$roadBody=@'
            return August20SurfaceChoice.TryElevationSafe(surface, point, out elevation);
'@
if (-not $road.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) {
    $road=ReplaceMethodBody $road 'private static bool TryElevation(Surface surface, Point2d point, out double elevation)' $roadBody 'Road Reserve safe Surface sampling' 'TryElevation'
}
else {
    Write-Host 'August 20 Road Reserve Surface sampling is already guarded.' -ForegroundColor DarkGreen
}
WriteText $roadReservePath $road

# Midblock: resolve Surface first and route every low-side sample through the same guard.
$mid=ReadText $midblockPath
$mid=MoveSurfaceResolutionBeforeSelection $mid 'public void CreateProductionRoutes()' '            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));' 'Midblock Sewer'
$midBody=@'
            var values = new List<double>();
            foreach (Point2d point in points)
            {
                double value;
                if (August20SurfaceChoice.TryElevationSafe(surface, point, out value))
                    values.Add(value);
            }
            return values.Count == 0 ? double.NaN : values.Average();
'@
if (-not $mid.Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out value)')) {
    $mid=ReplaceMethodBody $mid 'private static double AverageSurface(Surface surface, IEnumerable<Point2d> points)' $midBody 'Midblock safe Surface sampling' 'AverageSurface'
}
else {
    Write-Host 'August 20 Midblock Surface sampling is already guarded.' -ForegroundColor DarkGreen
}
WriteText $midblockPath $mid

# Site Grid: successful compilation is refused unless the visible field markers survive all earlier repairs.
$site=ReadText $siteGridPath
foreach ($marker in @('line.Color = boundary.Color;','line.LineWeight = boundary.LineWeight;','label.Color = boundary.Color;','point.Color = boundary.Color;','var pointMarker = new Circle(','"PM",','pointMarker.Color = boundary.Color;')) {
    if (-not $site.Contains($marker)) { throw "August 20 Site Grid field visibility marker missing: $marker" }
}
if (-not $site.Contains('pointMarker.LineWeight = LineWeight.LineWeight050;')) {
    $old='                            pointMarker.LineWeight = boundary.LineWeight;'
    if (-not $site.Contains($old)) { throw 'August 20 Site Grid point-marker lineweight anchor missing.' }
    $site=$site.Replace($old,'                            pointMarker.LineWeight = LineWeight.LineWeight050;')
}
WriteText $siteGridPath $site

# Final order/sampling guards.
foreach ($item in @(
    @{Name='Cadastral';Path=$cadastralPath;Method='public void CreateSewerFromCadastral()';Selection='List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));'},
    @{Name='Road Reserve';Path=$roadReservePath;Method='public void SewerRoadReserve()';Selection='List<ObjectId> ids = ResolveParcels(document, model.Text("Scope"));'},
    @{Name='Midblock';Path=$midblockPath;Method='public void CreateProductionRoutes()';Selection='List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));'})) {
    $check=ReadText $item.Path
    $methodStart=$check.IndexOf($item.Method,[StringComparison]::Ordinal)
    $surfaceIndex=$check.IndexOf('ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));',$methodStart,[StringComparison]::Ordinal)
    $selectionIndex=$check.IndexOf($item.Selection,$methodStart,[StringComparison]::Ordinal)
    if ($surfaceIndex -lt 0 -or $selectionIndex -lt 0 -or $surfaceIndex -gt $selectionIndex) { throw "August 20 $($item.Name) fatal-safety guard failed: Surface resolution is not before polygon selection." }
}
if (-not (ReadText $cadastralPath).Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) { throw 'August 20 Cadastral safe elevation guard missing.' }
if (-not (ReadText $roadReservePath).Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out elevation)')) { throw 'August 20 Road Reserve safe elevation guard missing.' }
if (-not (ReadText $midblockPath).Contains('August20SurfaceChoice.TryElevationSafe(surface, point, out value)')) { throw 'August 20 Midblock safe elevation guard missing.' }

Write-Host 'August 20 sewer fatal-safety hotfix passed: selected Civil Surfaces are resolved before polygon selection.' -ForegroundColor Green
Write-Host 'Cadastral, Midblock and Road Reserve surface samples now reject out-of-surface XY points before FindElevationAtXY.' -ForegroundColor Green
Write-Host 'Site Grid field visibility guard passed: grid geometry inherits the frame presentation and every DBPoint has a visible linked circle marker.' -ForegroundColor Green
