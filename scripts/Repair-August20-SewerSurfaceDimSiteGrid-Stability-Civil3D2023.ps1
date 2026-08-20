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
        throw "August 20 field-stability source missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n", "`r`n"
    $new = $new -replace "`r?`n", "`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 20 anchor not found: $label" }
    return $text.Replace($old,$new)
}

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 method signature not found ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 closing brace not found: $label" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close + 1)
}

function InsertActionAfterCommandInMethod(
    [string]$text,
    [string]$methodSignature,
    [string]$existingCommand,
    [string]$newCommand,
    [string]$newAction,
    [string]$label) {

    $methodStart = $text.IndexOf($methodSignature,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) { throw "August 20 menu method not found: $label" }
    $open = $text.IndexOf('{',$methodStart)
    if ($open -lt 0) { throw "August 20 menu opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 menu closing brace not found: $label" }
    $method = $text.Substring($methodStart,$close-$methodStart+1)
    if ($method.Contains($newCommand)) { return $text }
    $markerIndex = $method.IndexOf($existingCommand,[StringComparison]::Ordinal)
    if ($markerIndex -lt 0) { throw "August 20 menu command anchor not found ($label): $existingCommand" }
    $lineEnd = $method.IndexOf("`n",$markerIndex,[StringComparison]::Ordinal)
    if ($lineEnd -lt 0) { throw "August 20 menu action line has no newline: $label" }
    $insertAt = $lineEnd + 1
    $method = $method.Substring(0,$insertAt) + $newAction + "`r`n" + $method.Substring($insertAt)
    return $text.Substring(0,$methodStart) + $method + $text.Substring($close+1)
}

$helperPath = Required 'August20SurfaceAndDimensionHelpers.cs'
$multiPath = Required 'MultiDimensionCommands.cs'
$structuredPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$cadastralPath = Required 'August19CadastralSewerRouteCommands.cs'
$midblockPath = Required 'August11MidblockSewerProductionCommands.cs'
$roadReservePath = Required 'August19RoadReserveSewerAndSafetyCommands.cs'
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'

$helper = ReadText $helperPath
foreach ($marker in @(
    'internal static class August20SurfaceChoice',
    'internal static class August20DimensionPresentation',
    '"Architectural tick"',
    'style.Dimblk1 = arrowId;',
    'style.Dimblk2 = arrowId;',
    'style.Dimasz =',
    'style.Dimtxt =')) {
    if (-not $helper.Contains($marker)) { throw "August 20 helper marker missing: $marker" }
}

# -----------------------------------------------------------------------------
# 1. CE_MULTIDIM: selected Arrow 1/2, arrow size and text height.
# -----------------------------------------------------------------------------
$multi = ReadText $multiPath
$readStyles = @'
            ReadDimensionStyles(document.Database, out dimensionStyles, out currentStyle);
'@
$readStylesWithPresentation = @'
            ReadDimensionStyles(document.Database, out dimensionStyles, out currentStyle);
            double currentArrowSize;
            double currentTextHeight;
            August20DimensionPresentation.ReadSizes(
                document.Database,
                currentStyle,
                out currentArrowSize,
                out currentTextHeight);
'@
$multi = ReplaceRequired $multi $readStyles $readStylesWithPresentation 'CE_MULTIDIM source-style size defaults'

$dimStyleBlock = @'
            settings.AddChoice(
                "DimStyle", "01 Dimension", "Dimension style", currentStyle,
                "Select a drawing dimension style. CE Tools creates/updates an annotative copy so the source style and existing dimensions remain unchanged.",
                dimensionStyles);
'@
$appearanceBlock = @'
            settings.AddChoice(
                "DimStyle", "01 Dimension", "Dimension style", currentStyle,
                "Select a drawing dimension style. CE Tools creates/updates an annotative copy so the source style and existing dimensions remain unchanged.",
                dimensionStyles);
            settings.AddChoice(
                "Arrow1", "02 Appearance", "Arrow 1 style", August20DimensionPresentation.FromSelectedStyle,
                "Choose the first dimension-line arrowhead. From selected style keeps Arrow 1 from the chosen drawing dimension style.",
                August20DimensionPresentation.ArrowChoices);
            settings.AddChoice(
                "Arrow2", "02 Appearance", "Arrow 2 style", August20DimensionPresentation.FromSelectedStyle,
                "Choose the second dimension-line arrowhead independently from Arrow 1.",
                August20DimensionPresentation.ArrowChoices);
            settings.AddPositiveDouble(
                "ArrowSize", "02 Appearance", "Arrow size", currentArrowSize,
                "Dimension-style arrow size. The CE output style remains annotative.");
            settings.AddPositiveDouble(
                "TextHeight", "02 Appearance", "Text height", currentTextHeight,
                "Dimension-style text height. The CE output style remains annotative.");
'@
$multi = ReplaceRequired $multi $dimStyleBlock $appearanceBlock 'CE_MULTIDIM arrow/text presentation controls'
$multi = $multi.Replace('"Offset", "02 Placement"','"Offset", "03 Placement"')
$multi = $multi.Replace('"ArcLeader", "02 Placement"','"ArcLeader", "03 Placement"')

$styleReady = @'
                    if (styleId.IsNull)
                    {
                        document.Editor.WriteMessage("\nCE_MULTIDIM stopped. The selected dimension style could not be prepared.");
                        return;
                    }
'@
$styleReadyWithPresentation = @'
                    if (styleId.IsNull)
                    {
                        document.Editor.WriteMessage("\nCE_MULTIDIM stopped. The selected dimension style could not be prepared.");
                        return;
                    }
                    August20DimensionPresentation.Apply(
                        document.Database,
                        transaction,
                        styleId,
                        settings.Text("Arrow1"),
                        settings.Text("Arrow2"),
                        settings.Double("ArrowSize", currentArrowSize),
                        settings.Double("TextHeight", currentTextHeight));
'@
$multi = ReplaceRequired $multi $styleReady $styleReadyWithPresentation 'CE_MULTIDIM apply annotative presentation overrides'
WriteText $multiPath $multi

# Make Multiple Dimensions visible both from Survey Production and the detailed
# Survey Setting-Out / Delivery centre.
$structured = ReadText $structuredPath
$surveyMainAction = '                    A("CE-Multiple Dimensions", "CE_MULTIDIM", "Annotative aligned/linear/angular/radius/arc-length dimensions for multiple polylines and feature lines.", "01 Survey Production"),'
$structured = InsertActionAfterCommandInMethod $structured 'public void SurveyProduction()' '"CE_SURVEYDELIVERYPRODUCTIONCENTRE"' '"CE_MULTIDIM"' $surveyMainAction 'Survey Production CE_MULTIDIM'
$surveyDeliveryAction = '                    A("CE-Multiple Dimensions", "CE_MULTIDIM", "Annotative aligned/linear/angular/radius/arc-length dimensions for multiple polylines and feature lines, with selectable arrow styles, arrow size, text height and dimension style.", "05 COMPLETE"),'
$structured = InsertActionAfterCommandInMethod $structured 'public void SurveyDelivery()' '"CE_GRIDSETTINGOUT"' '"CE_MULTIDIM"' $surveyDeliveryAction 'Survey Delivery CE_MULTIDIM'
WriteText $structuredPath $structured

# -----------------------------------------------------------------------------
# 2. Surface selection belongs in the popup for all three Sewer planners.
# -----------------------------------------------------------------------------
$cadastral = ReadText $cadastralPath
$cadastralStart = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
'@
$cadastralStartNew = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            List<string> ceSurfaceChoices = August20SurfaceChoice.ReadSurfaceNames(document);
            if (ceSurfaceChoices.Count == 0) ceSurfaceChoices.Add(August20SurfaceChoice.None);
            string ceDefaultSurface = ceSurfaceChoices[0];

            var model = new ProductionSettingsDialogModel(
'@
$cadastral = ReplaceRequired $cadastral $cadastralStart $cadastralStartNew 'Cadastral surface choices before popup'
$cadastralPreference = @'
            model.AddChoice("Preference", "02 Routing", "Automatic route preference", "Shortest practical route",
'@
$cadastralSurfaceChoice = @'
            model.AddChoice("Surface", "01 Cadastral", "Existing-ground / analysis surface", ceDefaultSurface,
                "Select the Civil 3D surface used for site-slope, low-point and gravity-route analysis. The surface is selected here instead of after the popup closes.",
                ceSurfaceChoices);
            model.AddChoice("Preference", "02 Routing", "Automatic route preference", "Shortest practical route",
'@
$cadastral = ReplaceRequired $cadastral $cadastralPreference $cadastralSurfaceChoice 'Cadastral surface popup control'
$cadastralPrompt = @'
            ObjectId surfaceId = PromptSurface(document);
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL cancelled. Select a Civil 3D surface so CE Tools can analyse slopes and the site low point.");
                return;
            }
'@
$cadastralResolve = @'
            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL stopped. Select a Civil 3D surface in the popup so CE Tools can analyse slopes and the site low point.");
                return;
            }
'@
$cadastral = ReplaceRequired $cadastral $cadastralPrompt $cadastralResolve 'Cadastral remove post-popup surface prompt'

$cadastralErase = @'
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.IsErased) continue;
                if (!(string.Equals(entity.Layer, MidblockLayer, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(entity.Layer, RoadReserveLayer, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(entity.Layer, ManholeLayer, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(entity.Layer, AnalysisLayer, StringComparison.OrdinalIgnoreCase)))
                    continue;
                string managedType = entity.GetType().FullName ?? string.Empty;
                if (managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
                catch { }
            }
'@
$cadastral = ReplaceMethodBody $cadastral 'private static void EraseExisting(BlockTableRecord space, Transaction transaction)' $cadastralErase 'Cadastral safe generated-output erase'
WriteText $cadastralPath $cadastral

$road = ReadText $roadReservePath
$roadStart = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
'@
$roadStartNew = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            List<string> ceSurfaceChoices = August20SurfaceChoice.ReadSurfaceNames(document);
            if (ceSurfaceChoices.Count == 0) ceSurfaceChoices.Add(August20SurfaceChoice.None);
            string ceDefaultSurface = ceSurfaceChoices[0];

            var model = new ProductionSettingsDialogModel(
'@
$road = ReplaceRequired $road $roadStart $roadStartNew 'Road Reserve surface choices before popup'
$roadErfOffset = @'
            model.AddPositiveDouble("ErfOffset", "02 Road Reserve", "Offset from erf boundary into road reserve", 1.5,
'@
$roadSurfaceChoice = @'
            model.AddChoice("Surface", "01 Cadastral", "Existing-ground / analysis surface", ceDefaultSurface,
                "Select the Civil 3D surface used for road-reserve flow direction and the site low point. The surface is selected here instead of after the popup closes.",
                ceSurfaceChoices);
            model.AddPositiveDouble("ErfOffset", "02 Road Reserve", "Offset from erf boundary into road reserve", 1.5,
'@
$road = ReplaceRequired $road $roadErfOffset $roadSurfaceChoice 'Road Reserve surface popup control'
$roadPrompt = @'
            ObjectId surfaceId = PromptSurface(document,
                "\nSelect Civil 3D surface for Road Reserve sewer slope / site-low-point analysis: ");
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE cancelled. A Civil 3D surface is required for flow direction and the site low point.");
                return;
            }
'@
$roadResolve = @'
            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE stopped. Select a Civil 3D surface in the popup for flow direction and the site low point.");
                return;
            }
'@
$road = ReplaceRequired $road $roadPrompt $roadResolve 'Road Reserve remove post-popup surface prompt'
$roadErase = @'
            var names = new HashSet<string>(layers ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.IsErased || !names.Contains(entity.Layer)) continue;
                string managedType = entity.GetType().FullName ?? string.Empty;
                if (managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
                catch { }
            }
'@
$road = ReplaceMethodBody $road 'private static void EraseByLayers(BlockTableRecord space, Transaction transaction, params string[] layers)' $roadErase 'Road Reserve safe generated-output erase'
WriteText $roadReservePath $road

$mid = ReadText $midblockPath
$midStart = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
'@
$midStartNew = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            List<string> ceSurfaceChoices = August20SurfaceChoice.ReadSurfaceNames(document);
            if (ceSurfaceChoices.Count == 0) ceSurfaceChoices.Add(August20SurfaceChoice.None);
            string ceDefaultSurface = ceSurfaceChoices[0];
            var model = new ProductionSettingsDialogModel(
'@
$mid = ReplaceRequired $mid $midStart $midStartNew 'Midblock surface choices before popup'
$midInset = @'
            model.AddPositiveDouble("RouteInset", "02 Route", "Route inset from erf side", 1.5, "Offset the route this distance inside the selected outer erf side.");
'@
$midSurfaceChoice = @'
            model.AddChoice("Surface", "02 Route", "Existing-ground / analysis surface", ceDefaultSurface,
                "Select the Civil 3D surface used when Automatic low side from surface is selected. The surface is chosen in this popup, not from a later command-line prompt.",
                ceSurfaceChoices);
            model.AddPositiveDouble("RouteInset", "02 Route", "Route inset from erf side", 1.5, "Offset the route this distance inside the selected outer erf side.");
'@
$mid = ReplaceRequired $mid $midInset $midSurfaceChoice 'Midblock surface popup control'
$midPrompt = @'
            ObjectId surfaceId = ObjectId.Null;
            if (string.Equals(model.Text("Side"), "Automatic low side from surface", StringComparison.OrdinalIgnoreCase))
                surfaceId = PromptSurface(document);
'@
$midResolve = @'
            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));
            if (string.Equals(model.Text("Side"), "Automatic low side from surface", StringComparison.OrdinalIgnoreCase) && surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_MIDBLOCKSEWERPRODUCTION stopped. Select a Civil 3D surface in the popup, or choose an explicit Bottom/Left or Top/Right route side.");
                return;
            }
'@
$mid = ReplaceRequired $mid $midPrompt $midResolve 'Midblock remove post-popup surface prompt'
$midErase = @'
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.IsErased || entity.ExtensionDictionary.IsNull) continue;
                string kind;
                if (!TryReadKind(entity, transaction, out kind)) continue;
                string managedType = entity.GetType().FullName ?? string.Empty;
                if (managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
                catch { }
            }
'@
$mid = ReplaceMethodBody $mid 'private static void EraseExisting(BlockTableRecord space, Transaction transaction)' $midErase 'Midblock safe generated-output erase'
WriteText $midblockPath $mid

# -----------------------------------------------------------------------------
# 3. Site Grid: keep DBPoint controls, add visible linked circle markers, and let
#    dragging a marker move/rebuild the complete linked grid.
# -----------------------------------------------------------------------------
$site = ReadText $siteGridPath
$pointsStart = @'
            if (settings.CreatePoints)
            {
                for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
'@
$pointsStartVisible = @'
            if (settings.CreatePoints)
            {
                double siteGridPointSpacing = Math.Max(
                    0.001,
                    Math.Min(settings.SpacingX, settings.SpacingY));
                double siteGridPointRadius = Math.Max(
                    0.001,
                    Math.Min(
                        siteGridPointSpacing * 0.12,
                        Math.Max(
                            siteGridPointSpacing * 0.035,
                            PaperAnnotationScale.ModelDistance(database, 0.75))));
                for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
'@
$site = ReplaceRequired $site $pointsStart $pointsStartVisible 'Site Grid visible point-marker radius'
$pointLink = @'
                        WriteChildLink(
                            point,
                            transaction,
                            parentHandle,
                            "P",
                            xIndex,
                            yIndex);
                        created++;
'@
$pointLinkVisible = @'
                        WriteChildLink(
                            point,
                            transaction,
                            parentHandle,
                            "P",
                            xIndex,
                            yIndex);
                        created++;

                        var pointMarker = new Circle(
                            point.Position,
                            Vector3d.ZAxis,
                            siteGridPointRadius);
                        pointMarker.SetDatabaseDefaults(database);
                        pointMarker.LayerId = boundary.LayerId;
                        try
                        {
                            pointMarker.Color = boundary.Color;
                            pointMarker.LineWeight = boundary.LineWeight;
                        }
                        catch { }
                        Append(modelSpace, transaction, pointMarker);
                        WriteChildLink(
                            pointMarker,
                            transaction,
                            parentHandle,
                            "PM",
                            xIndex,
                            yIndex);
                        created++;
'@
$site = ReplaceRequired $site $pointLink $pointLinkVisible 'Site Grid visible linked point markers'
$pointTranslation = @'
                    actual = point.Position;
                }
                else if (string.Equals(link.Role, "V", StringComparison.OrdinalIgnoreCase))
'@
$markerTranslation = @'
                    actual = point.Position;
                }
                else if (string.Equals(link.Role, "PM", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.XIndex < 0 || link.XIndex >= xValues.Count ||
                        link.YIndex < 0 || link.YIndex >= yValues.Count)
                        continue;
                    Circle marker = entity as Circle;
                    if (marker == null) continue;
                    expected = new Point3d(
                        xValues[link.XIndex],
                        yValues[link.YIndex],
                        bounds.Elevation);
                    actual = marker.Center;
                }
                else if (string.Equals(link.Role, "V", StringComparison.OrdinalIgnoreCase))
'@
$site = ReplaceRequired $site $pointTranslation $markerTranslation 'Site Grid point-marker move linkage'
WriteText $siteGridPath $site

# -----------------------------------------------------------------------------
# 4. Final guards: reject the fatal-prone erase pattern and post-popup surfaces.
# -----------------------------------------------------------------------------
$multiCheck = ReadText $multiPath
foreach ($marker in @(
    '"Arrow1"',
    '"Arrow2"',
    '"ArrowSize"',
    '"TextHeight"',
    'August20DimensionPresentation.Apply(')) {
    if (-not $multiCheck.Contains($marker)) { throw "CE_MULTIDIM August 20 marker missing: $marker" }
}
$structuredCheck = ReadText $structuredPath
if ([regex]::Matches($structuredCheck,'"CE_MULTIDIM"').Count -lt 2) {
    throw 'CE_MULTIDIM is not visible in both Survey Production and Survey Delivery.'
}
$cadastralCheck = ReadText $cadastralPath
$roadCheck = ReadText $roadReservePath
$midCheck = ReadText $midblockPath
foreach ($pair in @(
    @{ Name='Cadastral'; Text=$cadastralCheck; Surface='model.Text("Surface")' },
    @{ Name='Road Reserve'; Text=$roadCheck; Surface='model.Text("Surface")' },
    @{ Name='Midblock'; Text=$midCheck; Surface='model.Text("Surface")' })) {
    if (-not $pair.Text.Contains($pair.Surface)) {
        throw "$($pair.Name) Sewer popup does not resolve its selected Surface."
    }
}
if ($cadastralCheck.Contains('try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;')) {
    throw 'Cadastral generated-output cleanup still opens model-space entities ForWrite before filtering.'
}
if ($roadCheck.Contains('try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;')) {
    throw 'Road Reserve generated-output cleanup still opens model-space entities ForWrite before filtering.'
}
if ($midCheck.Contains('try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;')) {
    throw 'Midblock generated-output cleanup still opens model-space entities ForWrite before filtering.'
}
$siteCheck = ReadText $siteGridPath
foreach ($marker in @('siteGridPointRadius','"PM"','Circle marker = entity as Circle;')) {
    if (-not $siteCheck.Contains($marker)) { throw "Site Grid visible-point marker missing: $marker" }
}

Write-Host 'August 20 field-stability repair passed.' -ForegroundColor Cyan
Write-Host 'CE_MULTIDIM now exposes Arrow 1, Arrow 2, arrow size, text height and selected annotative dimension style.' -ForegroundColor Green
Write-Host 'Multiple Dimensions is visible from Survey Production and Survey Setting-Out / Delivery.' -ForegroundColor Green
Write-Host 'Cadastral, Midblock and Road Reserve Sewer now select required Civil 3D surfaces inside their popups.' -ForegroundColor Green
Write-Host 'Sewer Replace Existing cleanup no longer opens every model-space/Aecc entity ForWrite before filtering.' -ForegroundColor Green
Write-Host 'Site Grid keeps linked DBPoints and adds visible linked circle markers at every grid intersection.' -ForegroundColor Green
