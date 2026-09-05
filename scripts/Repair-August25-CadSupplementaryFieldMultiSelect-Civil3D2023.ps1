[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Path([string]$name) {
    $value = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "CAD Supplementary field source missing: $value" }
    return $value
}
function Read([string]$path) { [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteFile([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $marker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $marker" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $marker" }
    return $text.Substring(0,$open+1) + "`r`n" + ($body -replace "`r?`n","`r`n").Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}

# Geometry commands: force the field-tested multi-object runtime to be the final
# route after every historical repair script has finished mutating staged source.
$geometryPath = Path 'August24FieldGeometryCommands.cs'
$geometry = Read $geometryPath
$geometry = ReplaceMethodBody $geometry '        public void StretchMultipleFeatureLines()' @'
            Document document = Active();
            if (document == null) return;
            August25CadSupplementaryFieldRuntime.StretchFeatureLines(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void ConstructionOffsets()' @'
            Document document = Active();
            if (document == null) return;
            August25CadSupplementaryFieldRuntime.ConstructionOffsets(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void MiddleConstructionLines()' @'
            Document document = Active();
            if (document == null) return;
            August25CadSupplementaryFieldRuntime.MiddleConstructionLines(document);
'@
WriteFile $geometryPath $geometry

# Curve conversion: isolate every selected source in its own transaction. Keep mode
# never opens a source for write and Replace mode erases only after that source's
# output has been appended successfully. A failed source cannot roll back siblings.
$curvePath = Path 'CurveConversionCommands.cs'
$curve = Read $curvePath
$curve = ReplaceMethodBody $curve '        public void ConvertCurves()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Convert Curves to Polylines",
                "Convert multiple selected lines, arcs, circles, splines and polylines safely. Every source is processed independently; Keep originals never modifies the selected source objects.");
            model.AddChoice("Mode", "01 Conversion", "Conversion mode", "Auto-detect selected objects",
                "Auto-detect converts supported non-polyline curves to lightweight polylines.",
                new[] { "Auto-detect selected objects", "Lines to polylines", "Arcs to polylines", "Circles to polylines", "Splines to polylines", "3D polylines to polylines", "Polylines to 3D polylines" });
            model.AddDouble("Segment", "02 Approximation", "Maximum segment length", 1.0,
                "Curves are sampled so no generated chord is longer than this drawing-unit distance.");
            model.AddPositiveInteger("ArcVertices", "02 Approximation", "Minimum vertices on arcs", 12,
                "Every converted arc receives at least this many visible polyline vertices.");
            model.AddPositiveInteger("CircleVertices", "02 Approximation", "Minimum vertices on circles", 36,
                "Every converted circle receives at least this many visible polyline vertices.");
            model.AddChoice("Keep", "03 Source", "Source objects", "Keep originals",
                "Keep every source object, or erase only an individual source after its replacement was created successfully.",
                new[] { "Keep originals", "Replace originals" });
            model.AddChoice("Layer", "03 Source", "Output layer", "Use source layer",
                "Use each source layer or place output on the current layer.",
                new[] { "Use source layer", "Use current layer" });
            model.AddChoice("Elevation", "04 2D output", "2D elevation", "Use first source elevation",
                "Use the first source elevation or flatten 2D output to zero.",
                new[] { "Use first source elevation", "Flatten to zero" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectCurves(document.Editor);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            document.Editor.SetImpliedSelection(new ObjectId[0]);

            string mode = model.Text("Mode");
            bool keep = string.Equals(model.Text("Keep"), "Keep originals", StringComparison.OrdinalIgnoreCase);
            bool sourceLayer = string.Equals(model.Text("Layer"), "Use source layer", StringComparison.OrdinalIgnoreCase);
            bool flatten = string.Equals(model.Text("Elevation"), "Flatten to zero", StringComparison.OrdinalIgnoreCase);
            double maximumSegment = Math.Max(model.Double("Segment", 1.0), 0.001);
            int minimumArcVertices = Math.Max(model.Integer("ArcVertices", 12), 4);
            int minimumCircleVertices = Math.Max(model.Integer("CircleVertices", 36), 12);
            int converted = 0; int skipped = 0; int failed = 0;
            var rows = new List<IList<string>>();

            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (source == null || source.IsErased || !MatchesMode(source, mode)) { skipped++; continue; }
                        Entity output = CreateOutput(source, mode, maximumSegment, minimumArcVertices, minimumCircleVertices, flatten);
                        if (output == null) { skipped++; continue; }

                        BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                        if (space == null) throw new InvalidOperationException("The active drawing space is unavailable.");
                        string sourceType = source.GetType().Name;
                        string sourceHandle = source.Handle.ToString();
                        string sourceLayerName = source.Layer;
                        output.SetDatabaseDefaults(document.Database);
                        output.LayerId = sourceLayer ? source.LayerId : document.Database.Clayer;
                        try { output.Color = source.Color; } catch { output.ColorIndex = 256; }
                        try { output.LinetypeId = source.LinetypeId; } catch { }
                        try { output.LineWeight = source.LineWeight; } catch { }
                        try { output.Transparency = source.Transparency; } catch { }
                        ObjectId outputId = space.AppendEntity(output);
                        transaction.AddNewlyCreatedDBObject(output, true);

                        if (!keep)
                        {
                            source.UpgradeOpen();
                            source.Erase();
                        }
                        transaction.Commit();
                        converted++;
                        rows.Add(new List<string> { sourceType, output.GetType().Name, sourceHandle, outputId.Handle.ToString(), sourceLayerName });
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    document.Editor.WriteMessage("\nSelected source left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Curve Conversion Complete",
                string.Format(CultureInfo.CurrentCulture, "Converted={0}; skipped={1}; failed={2}; originals={3}.", converted, skipped, failed, keep ? "kept" : "replaced individually"),
                new List<string> { "Source Type", "Output Type", "Source Handle", "Output Handle", "Layer" }, rows,
                "CE TOOLS CURVE CONVERSION REGISTER");
            document.Editor.WriteMessage("\nCE_CURVECONVERT complete. Converted={0}; skipped={1}; failed={2}.", converted, skipped, failed);
'@
WriteFile $curvePath $curve

# Multiple Dimensions: Lines are first-class sources and all linear measurements are
# displayed in millimetres without changing the user's source DimStyle globally.
$dimensionPath = Path 'MultiDimensionCommands.cs'
$dimension = Read $dimensionPath
$dimension = $dimension.Replace(
    'Add multiple annotative dimensions to multiple AutoCAD polylines and Civil 3D feature lines. Arc length and radius dimensions use true polyline arc geometry.',
    'Add multiple annotative dimensions to AutoCAD lines/open or closed polylines and Civil 3D feature lines. Linear values are displayed in millimetres; arc length and radius dimensions use true polyline arc geometry.')
$dimension = $dimension.Replace(
    'Select multiple polylines and/or Civil 3D feature lines to dimension:',
    'Select multiple lines, polylines and/or Civil 3D feature lines to dimension:')
$lineBranchAnchor = @'
                        Polyline polyline = entity as Polyline;
'@
$lineBranch = @'
                        Line sourceLine = entity as Line;
                        if (sourceLine != null)
                        {
                            sources++;
                            ProcessLine(document.Database, transaction, space, sourceLine, mode, styleId, offset,
                                ref dimensions, ref skippedGeometry, ref failed);
                            continue;
                        }

                        Polyline polyline = entity as Polyline;
'@
if (-not $dimension.Contains('Line sourceLine = entity as Line;')) {
    if (-not $dimension.Contains($lineBranchAnchor)) { throw 'MultiDimension line-source insertion anchor missing.' }
    $dimension = $dimension.Replace($lineBranchAnchor,$lineBranch)
}
$processLine = @'
        private static void ProcessLine(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Line line,
            string mode,
            ObjectId styleId,
            double offset,
            ref int created,
            ref int skipped,
            ref int failed)
        {
            Point3d start = Plan(line.StartPoint);
            Point3d end = Plan(line.EndPoint);
            if (start.DistanceTo(end) <= GeometryTolerance) { skipped++; return; }
            Point3d centroid = new Point3d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5, 0.0);
            bool all = string.Equals(mode, "All applicable geometry", StringComparison.OrdinalIgnoreCase);
            try
            {
                Dimension dimension;
                if (string.Equals(mode, "Linear - horizontal", StringComparison.OrdinalIgnoreCase))
                    dimension = new RotatedDimension(0.0, start, end, HorizontalDimPoint(start, end, centroid, offset), "<>", styleId);
                else if (string.Equals(mode, "Linear - vertical", StringComparison.OrdinalIgnoreCase))
                    dimension = new RotatedDimension(Math.PI * 0.5, start, end, VerticalDimPoint(start, end, centroid, offset), "<>", styleId);
                else if (all || string.Equals(mode, "Aligned - straight segments", StringComparison.OrdinalIgnoreCase))
                    dimension = new AlignedDimension(start, end, OffsetLinePoint(start, end, centroid, offset), "<>", styleId);
                else { skipped++; return; }
                AddDimension(database, transaction, space, dimension, ref created);
            }
            catch { failed++; }
        }

'@
if (-not $dimension.Contains('        private static void ProcessLine(')) {
    $anchor = '        private static void ProcessPolyline('
    $at = $dimension.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'MultiDimension ProcessPolyline anchor missing.' }
    $dimension = $dimension.Substring(0,$at) + $processLine + $dimension.Substring($at)
}
$dimAnchor = '            try { dimension.SetFromStyle(); } catch { PaperAnnotationScale.SetAnnotative(dimension); }'
$dimReplacement = $dimAnchor + "`r`n            // Drawing geometry is in metres; display linear values as millimetres per dimension.`r`n            dimension.Dimlfac = 1000.0;"
if (-not $dimension.Contains('dimension.Dimlfac = 1000.0;')) {
    if (-not $dimension.Contains($dimAnchor)) { throw 'MultiDimension Dimlfac insertion anchor missing.' }
    $dimension = $dimension.Replace($dimAnchor,$dimReplacement)
}
WriteFile $dimensionPath $dimension

# Final guards: these are deliberately strict so an installer build fails before
# compilation rather than silently shipping an older unsafe CAD Supplementary route.
$geometry = Read $geometryPath
$curve = Read $curvePath
$dimension = Read $dimensionPath
if (-not $geometry.Contains('August25CadSupplementaryFieldRuntime.StretchFeatureLines(document);')) { throw 'Final FeatureLine stretch route missing.' }
if (-not $geometry.Contains('August25CadSupplementaryFieldRuntime.ConstructionOffsets(document);')) { throw 'Final construction offset route missing.' }
if (-not $geometry.Contains('August25CadSupplementaryFieldRuntime.MiddleConstructionLines(document);')) { throw 'Final middle construction route missing.' }
if (-not $curve.Contains('foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())') -or -not $curve.Contains('source.UpgradeOpen();')) { throw 'Final per-source conversion route missing.' }
if (-not $dimension.Contains('Line sourceLine = entity as Line;') -or -not $dimension.Contains('dimension.Dimlfac = 1000.0;')) { throw 'Final millimetre/Line dimension route missing.' }

Write-Host 'August 25 CAD Supplementary field multi-select finalization complete.' -ForegroundColor Green
Write-Host 'Convert, break, stretch, offset, centre construction and millimetre dimensions are on guarded field routes.' -ForegroundColor Green
