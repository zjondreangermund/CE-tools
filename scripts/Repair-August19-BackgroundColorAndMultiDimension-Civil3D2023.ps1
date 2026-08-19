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
        throw "Required August 19 Background/Multi-Dimension source missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method signature not found: $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace not found: $signature" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace not found: $signature" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close+1)
}

# -----------------------------------------------------------------------------
# Background Colour 250 crash protection
# -----------------------------------------------------------------------------
# The old command opened every model-space object ForWrite, including Aecc/Civil
# custom objects and XREF references. A background cleanup command must not mutate
# those object types. Open ForRead first, inspect safely, then UpgradeOpen only the
# ordinary AutoCAD entities that are actually going to change.
$backgroundPath = Required 'BackgroundPreparationCommands.cs'
$background = ReadText $backgroundPath
$colourBody = @'
            Document document = Active();
            if (document == null) return;

            int changed = 0;
            int already250 = 0;
            int skippedLocked = 0;
            int skippedProtected = 0;
            int failed = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null || entity.IsErased) { skippedProtected++; continue; }

                            LayerTableRecord layer = transaction.GetObject(
                                entity.LayerId,
                                OpenMode.ForRead,
                                false) as LayerTableRecord;
                            if (layer != null && layer.IsLocked)
                            {
                                skippedLocked++;
                                continue;
                            }

                            BlockReference block = entity as BlockReference;
                            if (block != null && IsXref(block, transaction))
                            {
                                skippedProtected++;
                                continue;
                            }

                            string managedType = entity.GetType().FullName ?? string.Empty;
                            string dxfName = string.Empty;
                            try
                            {
                                RXClass rx = entity.GetRXClass();
                                dxfName = rx == null ? string.Empty : (rx.DxfName ?? string.Empty);
                            }
                            catch { }

                            // Civil 3D/Aecc and proxy/custom objects are deliberately excluded.
                            // Background Colour 250 is a DWG cleanup utility, not a Civil-object
                            // style editor. This prevents Aecc object write reactors from being
                            // triggered merely to change display colour.
                            if (managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase) ||
                                dxfName.StartsWith("AECC", StringComparison.OrdinalIgnoreCase) ||
                                dxfName.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                skippedProtected++;
                                continue;
                            }

                            if (entity.ColorIndex == 250)
                            {
                                already250++;
                                continue;
                            }

                            entity.UpgradeOpen();
                            entity.ColorIndex = 250;
                            changed++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_BGCOLOR250 complete. Changed={0}; already 250={1}; locked-layer skips={2}; protected Civil/XREF/proxy skips={3}; failed={4}.",
                changed,
                already250,
                skippedLocked,
                skippedProtected,
                failed);
'@
$background = ReplaceMethodBody $background 'public void BackgroundColour250()' $colourBody
WriteText $backgroundPath $background

# -----------------------------------------------------------------------------
# Survey Production menu wiring for CE_MULTIDIM
# -----------------------------------------------------------------------------
$menuPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$menu = ReadText $menuPath
$surveyStart = $menu.IndexOf('public void SurveyDelivery()', [StringComparison]::Ordinal)
if ($surveyStart -lt 0) { throw 'SurveyDelivery() method was not found.' }
$surveyOpen = $menu.IndexOf('{',$surveyStart)
if ($surveyOpen -lt 0) { throw 'SurveyDelivery() opening brace was not found.' }
$depth = 0
$surveyClose = -1
for ($i=$surveyOpen; $i -lt $menu.Length; $i++) {
    if ($menu[$i] -eq '{') { $depth++ }
    elseif ($menu[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $surveyClose=$i; break }
    }
}
if ($surveyClose -lt 0) { throw 'SurveyDelivery() closing brace was not found.' }
$survey = $menu.Substring($surveyStart,$surveyClose-$surveyStart+1)
$multiAction = '                    A("CE-Multiple Dimensions", "CE_MULTIDIM", "Annotative aligned/linear/angular/radius/arc-length dimensions for multiple polylines and feature lines, using a selected dimension style.", "05 COMPLETE"),'
if (-not $survey.Contains('"CE_MULTIDIM"')) {
    # Earlier August 18/19 staging is allowed to rename or reword Survey actions.
    # Anchor to the command token, not one historical full action line. Prefer the
    # current Grid command, then Site Grid, then Vertex Setting-Out. If none are
    # present, insert immediately before the Surface Comparison action.
    $anchorMarker = $null
    foreach ($candidate in @('"CE_GRIDSETTINGOUT"','"CE_SITEGRID"','"CE_VERTEXSETTINGOUT"')) {
        if ($survey.Contains($candidate)) {
            $anchorMarker = $candidate
            break
        }
    }

    if ($anchorMarker) {
        $markerIndex = $survey.IndexOf($anchorMarker,[StringComparison]::Ordinal)
        $lineEnd = $survey.IndexOf("`n",$markerIndex,[StringComparison]::Ordinal)
        if ($lineEnd -lt 0) {
            throw "Survey Delivery action line containing $anchorMarker has no terminating newline."
        }
        $insertAt = $lineEnd + 1
        $survey = $survey.Substring(0,$insertAt) + $multiAction + "`r`n" + $survey.Substring($insertAt)
    }
    else {
        $fallbackMarker = '"CE_SURFACECOMPARETABLE"'
        $fallbackIndex = $survey.IndexOf($fallbackMarker,[StringComparison]::Ordinal)
        if ($fallbackIndex -lt 0) {
            throw 'Survey Delivery has no current Grid, Site Grid, Vertex Setting-Out or Surface Comparison action for CE-Multiple Dimensions insertion.'
        }
        $lineStart = $survey.LastIndexOf("`n",$fallbackIndex)
        $insertAt = if ($lineStart -lt 0) { 0 } else { $lineStart + 1 }
        $survey = $survey.Substring(0,$insertAt) + $multiAction + "`r`n" + $survey.Substring($insertAt)
    }

    if ([regex]::Matches($survey,'"CE_MULTIDIM"').Count -ne 1) {
        throw 'CE-Multiple Dimensions insertion did not produce exactly one Survey Delivery action.'
    }
    $menu = $menu.Substring(0,$surveyStart) + $survey + $menu.Substring($surveyClose+1)
    WriteText $menuPath $menu
}

# -----------------------------------------------------------------------------
# Regression guards
# -----------------------------------------------------------------------------
$backgroundCheck = ReadText $backgroundPath
$methodStart = $backgroundCheck.IndexOf('public void BackgroundColour250()', [StringComparison]::Ordinal)
$methodEnd = $backgroundCheck.IndexOf('[CommandMethod("CE_TOOLS", "CE_BGCLEAN"', $methodStart, [StringComparison]::Ordinal)
if ($methodStart -lt 0 -or $methodEnd -le $methodStart) {
    throw 'CE_BGCOLOR250 method range could not be validated.'
}
$method = $backgroundCheck.Substring($methodStart,$methodEnd-$methodStart)
foreach ($marker in @(
    'OpenMode.ForRead',
    'layer.IsLocked',
    'IsXref(block, transaction)',
    'managedType.StartsWith("Autodesk.Civil."',
    'dxfName.StartsWith("AECC"',
    'entity.UpgradeOpen();',
    'entity.ColorIndex = 250;')) {
    if (-not $method.Contains($marker)) {
        throw "CE_BGCOLOR250 safety marker missing: $marker"
    }
}
if ($method.Contains('transaction.GetObject(id, OpenMode.ForWrite, false) as Entity')) {
    throw 'CE_BGCOLOR250 still opens every model-space entity ForWrite.'
}

$menuCheck = ReadText $menuPath
if (-not $menuCheck.Contains('A("CE-Multiple Dimensions", "CE_MULTIDIM"')) {
    throw 'Survey Delivery does not expose CE-Multiple Dimensions.'
}
$multiPath = Required 'MultiDimensionCommands.cs'
$multi = ReadText $multiPath
foreach ($marker in @(
    '"CE_MULTIDIM"',
    'AlignedDimension',
    'RotatedDimension',
    'LineAngularDimension2',
    'RadialDimension',
    'ArcDimension',
    'EnsureAnnotativeDimensionStyle',
    'PaperAnnotationScale.SetAnnotative',
    'dimension.SetFromStyle()',
    'FeatureLinePointType.AllPoints')) {
    if (-not $multi.Contains($marker)) {
        throw "CE_MULTIDIM implementation marker missing: $marker"
    }
}

Write-Host 'Background Colour 250 safety fix passed: Civil/XREF/proxy objects are protected and entities are upgraded only when changed.' -ForegroundColor Green
Write-Host 'CE-Multiple Dimensions is wired into Survey Setting-Out / Delivery with selected-style annotative output.' -ForegroundColor Green
