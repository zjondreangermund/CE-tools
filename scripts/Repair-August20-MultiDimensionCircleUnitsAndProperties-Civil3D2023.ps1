[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$multiPath = Join-Path $src 'MultiDimensionCommands.cs'
$helperPath = Join-Path $src 'August20SurfaceAndDimensionHelpers.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($required in @($multiPath,$helperPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 20 Multiple Dimensions finalizer prerequisite missing: $required"
    }
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
    if (-not $text.Contains($old)) { throw "August 20 Multiple Dimensions anchor not found: $label" }
    return $text.Replace($old,$new)
}

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 Multiple Dimensions method not found ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 Multiple Dimensions opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 Multiple Dimensions closing brace not found: $label" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close+1)
}

$multi = ReadText $multiPath

# The field-stability finalizer has already added Arrow 1/2, arrow size and text
# height controls. This finalizer is deliberately later: it adds standalone Circle
# support, measurement-unit scaling, and dimension-level presentation overrides so
# AutoCAD Properties reports the same DIMASZ/DIMTXT values selected in the popup.
$multi = $multi.Replace('Radius - polyline arc segments','Radius - arcs / circles')
$multi = $multi.Replace(
    'Arc length and radius dimensions use true polyline arc geometry.',
    'Arc length and radius dimensions use true polyline arc geometry; standalone circles receive a radial dimension with a visible centre mark.')

$dimStyleAnchor = @'
            settings.AddChoice(
                "DimStyle", "01 Dimension", "Dimension style", currentStyle,
'@
$unitsAndDimStyle = @'
            settings.AddChoice(
                "ValueUnits", "01 Dimension", "Dimension value units", "Metres",
                "Choose the displayed measurement units. Metres keeps the drawing measurement (x1); Millimetres multiplies linear/radius/arc-length values by 1000, so 30 m displays as 30000.",
                new[] { "Metres", "Millimetres" });
            settings.AddChoice(
                "DimStyle", "01 Dimension", "Dimension style", currentStyle,
'@
$multi = ReplaceRequired $multi $dimStyleAnchor $unitsAndDimStyle 'dimension value units popup control'

$leaderBlock = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));
'@
$leaderAndUnits = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));
            bool outputMillimetres = string.Equals(
                settings.Text("ValueUnits"),
                "Millimetres",
                StringComparison.OrdinalIgnoreCase);
            double measurementFactor = outputMillimetres ? 1000.0 : 1.0;
'@
$multi = ReplaceRequired $multi $leaderBlock $leaderAndUnits 'dimension unit factor'

$appearanceApply = @'
                    August20DimensionPresentation.Apply(
                        document.Database,
                        transaction,
                        styleId,
                        settings.Text("Arrow1"),
                        settings.Text("Arrow2"),
                        settings.Double("ArrowSize", currentArrowSize),
                        settings.Double("TextHeight", currentTextHeight));
'@
$appearanceApplyWithUnits = @'
                    August20DimensionPresentation.Apply(
                        document.Database,
                        transaction,
                        styleId,
                        settings.Text("Arrow1"),
                        settings.Text("Arrow2"),
                        settings.Double("ArrowSize", currentArrowSize),
                        settings.Double("TextHeight", currentTextHeight));
                    DimStyleTableRecord outputStyle = transaction.GetObject(
                        styleId,
                        OpenMode.ForWrite,
                        false) as DimStyleTableRecord;
                    if (outputStyle != null)
                    {
                        outputStyle.Dimlfac = measurementFactor;
                        if (outputMillimetres)
                            outputStyle.Dimdec = 0;
                    }
'@
$multi = ReplaceRequired $multi $appearanceApply $appearanceApplyWithUnits 'apply measurement factor to CE dimension style'

$featureLineAnchor = @'
                        CivilFeatureLine featureLine = entity as CivilFeatureLine;
'@
$circleThenFeatureLine = @'
                        Circle circle = entity as Circle;
                        if (circle != null)
                        {
                            sources++;
                            ProcessCircle(
                                document.Database,
                                transaction,
                                space,
                                circle,
                                mode,
                                styleId,
                                leader,
                                ref dimensions,
                                ref skippedGeometry,
                                ref failed);
                            continue;
                        }

                        CivilFeatureLine featureLine = entity as CivilFeatureLine;
'@
$multi = ReplaceRequired $multi $featureLineAnchor $circleThenFeatureLine 'standalone Circle selection handling'

$processFeatureLineSignature = '        private static void ProcessFeatureLine('
$processFeatureLineIndex = $multi.IndexOf($processFeatureLineSignature,[StringComparison]::Ordinal)
if ($processFeatureLineIndex -lt 0) {
    throw 'August 20 Multiple Dimensions could not locate ProcessFeatureLine insertion point.'
}
if (-not $multi.Contains('private static void ProcessCircle(')) {
$processCircle = @'
        private static void ProcessCircle(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Circle circle,
            string mode,
            ObjectId styleId,
            double leader,
            ref int created,
            ref int skipped,
            ref int failed)
        {
            bool applicable = string.Equals(
                    mode,
                    "All applicable geometry",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mode,
                    "Radius - arcs / circles",
                    StringComparison.OrdinalIgnoreCase);
            if (!applicable)
            {
                skipped++;
                return;
            }
            if (circle == null || circle.Radius <= GeometryTolerance)
            {
                skipped++;
                return;
            }

            try
            {
                Point3d center = Plan(circle.Center);
                Vector3d direction = new Vector3d(1.0, 1.0, 0.0).GetNormal();
                Point3d chordPoint = center + direction * circle.Radius;
                var dimension = new RadialDimension(
                    center,
                    chordPoint,
                    Math.Max(leader, GeometryTolerance),
                    "<>",
                    styleId);
                AddDimension(database, transaction, space, dimension, ref created);

                // Positive DIMCEN creates a centre mark. Use the selected arrow-size
                // paper value as a stable visual centre-mark size, and apply it only
                // to the standalone-circle radial dimension.
                dimension.Dimcen = Math.Max(dimension.Dimasz, 0.001);
            }
            catch
            {
                failed++;
            }
        }

'@ -replace "`r?`n","`r`n"
    $multi = $multi.Substring(0,$processFeatureLineIndex) + $processCircle + $multi.Substring($processFeatureLineIndex)
}

$addDimensionBody = @'
            dimension.SetDatabaseDefaults(database);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            try { dimension.SetFromStyle(); }
            catch { }
            PaperAnnotationScale.SetAnnotative(dimension);

            // Store explicit per-dimension overrides after SetFromStyle. AutoCAD's
            // Properties palette then reports exactly the Arrow size and Text height
            // chosen in the CE popup instead of an annotation-scale-derived effective
            // value. DIMLFAC/precision are copied too so unit output is deterministic.
            try
            {
                DimStyleTableRecord style = transaction.GetObject(
                    dimension.DimensionStyle,
                    OpenMode.ForRead,
                    false) as DimStyleTableRecord;
                if (style != null)
                {
                    dimension.Dimasz = style.Dimasz;
                    dimension.Dimtxt = style.Dimtxt;
                    dimension.Dimlfac = style.Dimlfac;
                    dimension.Dimdec = style.Dimdec;
                }
            }
            catch { }
            created++;
'@
$multi = ReplaceMethodBody $multi '        private static void AddDimension(' $addDimensionBody 'dimension-level size/unit overrides'

WriteText $multiPath $multi

# Validate that the shared presentation helper keeps paper values as raw DIMASZ and
# DIMTXT values. The explicit Dimension overrides above depend on this invariant.
$helper = ReadText $helperPath
foreach ($marker in @(
    'style.Dimasz = Math.Max(arrowSize, 0.001);',
    'style.Dimtxt = Math.Max(textHeight, 0.001);')) {
    if (-not $helper.Contains($marker)) {
        throw "August 20 Multiple Dimensions raw presentation marker missing: $marker"
    }
}

$check = ReadText $multiPath
foreach ($marker in @(
    '"ValueUnits"',
    'new[] { "Metres", "Millimetres" }',
    'measurementFactor = outputMillimetres ? 1000.0 : 1.0;',
    'outputStyle.Dimlfac = measurementFactor;',
    'outputStyle.Dimdec = 0;',
    'Circle circle = entity as Circle;',
    'private static void ProcessCircle(',
    '"Radius - arcs / circles"',
    'dimension.Dimcen = Math.Max(dimension.Dimasz, 0.001);',
    'dimension.Dimasz = style.Dimasz;',
    'dimension.Dimtxt = style.Dimtxt;',
    'dimension.Dimlfac = style.Dimlfac;',
    'dimension.Dimdec = style.Dimdec;')) {
    if (-not $check.Contains($marker)) {
        throw "August 20 Multiple Dimensions final marker missing: $marker"
    }
}
if ($check.Contains('Radius - polyline arc segments')) {
    throw 'August 20 Multiple Dimensions still exposes the old polyline-only radius label.'
}

Write-Host 'CE-Multiple Dimensions final repair passed: standalone circles now receive radial dimensions with centre marks.' -ForegroundColor Green
Write-Host 'CE-Multiple Dimensions value units now support Metres (x1) and Millimetres (x1000; zero-decimal output).' -ForegroundColor Green
Write-Host 'CE-Multiple Dimensions now writes DIMASZ/DIMTXT/DIMLFAC directly to each dimension so Properties matches the popup.' -ForegroundColor Green
