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
        throw "August 19 Survey/Grid/Sewer source missing: $path"
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
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        throw "August 19 Survey/Grid/Sewer anchor not found: $label"
    }
    return $text.Replace($old,$new)
}
function ReplaceMethod([string]$text,[string]$marker,[string]$replacement,[string]$label) {
    $replacement = $replacement -replace "`r?`n","`r`n"
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 19 method marker not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 19 opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 19 closing brace not found: $label" }
    return $text.Substring(0,$start) + $replacement + $text.Substring($close + 1)
}

$sitePath = Required 'August12SurveySiteGridCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$batchPath = Required 'August11NetworkBatchCommands.cs'
$sewerPath = Required 'August13SewerMultiSourceNetworkCommands.cs'

$site = ReadText $sitePath
$grid = ReadText $gridPath
$batch = ReadText $batchPath
$sewer = ReadText $sewerPath

# This layer is intentionally allowed to run only after the final August 18 state.
$checks = @(
    @{ Name = 'Site Grid Swap X/Y'; Ok = $site.Contains('"SwapXY"') },
    @{ Name = 'Site Grid reverse signs'; Ok = $site.Contains('"ReverseSigns"') },
    @{ Name = 'Dynamic Grid Swap X/Y'; Ok = $grid.Contains('SwapXY') },
    @{ Name = 'Dynamic Grid reverse signs'; Ok = $grid.Contains('ReverseSigns') },
    @{ Name = 'Sewer batch command'; Ok = $batch.Contains('CE_NETWORKFROMPOLYLINESBATCH') },
    @{ Name = 'Direct sewer multi-source engine'; Ok = $sewer.Contains('CE_SEWERNETWORKMULTI') }
)
$missing = $checks | Where-Object { -not $_.Ok }
if ($missing) {
    throw ('August 19 Survey/Grid/Sewer repair refused to run before the complete August 18 staged state: ' + (($missing | ForEach-Object { $_.Name }) -join ', '))
}

# -----------------------------------------------------------------------------
# 1. Site Grid: make coordinate MText visibly readable at normal model extents.
#    Keep the selected paper height, but apply a practical lower bound based on
#    both grid spacing and site-frame size. Contents is assigned last so AutoCAD
#    does not reformat the MText after its final geometry properties are set.
# -----------------------------------------------------------------------------
$oldFloor = @'
            double siteGridTextFloor = Math.Max(
                Math.Min(settings.SpacingX, settings.SpacingY) * 0.08,
                0.001);
            modelTextHeight = Math.Max(modelTextHeight, siteGridTextFloor);
'@
$newFloor = @'
            double siteGridMinimumSpacing = Math.Max(
                0.001,
                Math.Min(settings.SpacingX, settings.SpacingY));
            double siteGridFrameSpan = Math.Max(
                0.001,
                Math.Min(
                    Math.Abs(bounds.MaxX - bounds.MinX),
                    Math.Abs(bounds.MaxY - bounds.MinY)));
            double siteGridTextFloor = Math.Max(
                Math.Max(siteGridMinimumSpacing * 0.40, siteGridFrameSpan * 0.008),
                0.01);
            modelTextHeight = Math.Max(modelTextHeight, siteGridTextFloor);
'@
$site = ReplaceRequired $site $oldFloor $newFloor 'Site Grid visible text-height floor'

$siteCreateLabel = @'
        private static MText CreateLabel(
            Database database,
            Polyline boundary,
            string contents,
            Point3d location,
            double textHeight,
            double rotation)
        {
            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.LayerId = boundary.LayerId;
            label.Location = location;
            label.TextHeight = Math.Max(textHeight, 0.01);
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Rotation = rotation;
            label.Width = 0.0;
            // Autodesk recommends assigning Contents after the other MText
            // geometry/format properties so the final text is not reformatted away.
            label.Contents = string.IsNullOrWhiteSpace(contents) ? " " : contents;
            return label;
        }
'@
$site = ReplaceMethod $site '        private static MText CreateLabel(' $siteCreateLabel 'Site Grid visible MText creation'
WriteText $sitePath $site

# -----------------------------------------------------------------------------
# 2. Dynamic Grid Setting-Out: expose Base/NG and Comparison/Design surfaces,
#    persist them with the linked table, and resample them on every linked refresh.
#    The selected surfaces affect table level columns only; true COGO X/Y stays put.
# -----------------------------------------------------------------------------
if (-not $grid.Contains('"BaseSurface"')) {
    $surfaceNamesAnchor = @'
            List<ObjectId> sourceIds = SelectBoundaries(document);
            if (sourceIds.Count == 0) return;

'@
    $surfaceNamesBlock = @'
            List<ObjectId> sourceIds = SelectBoundaries(document);
            if (sourceIds.Count == 0) return;

            List<string> gridSurfaceNames = ReadGridSurfaceNames(
                document.Database,
                civil);
            var gridSurfaceChoices = new List<string> { "<None>" };
            gridSurfaceChoices.AddRange(gridSurfaceNames);

'@
    $grid = ReplaceRequired $grid $surfaceNamesAnchor $surfaceNamesBlock 'Dynamic Grid surface-name list'

    $modeBlock = @'
            settings.AddChoice(
                "Mode", "01 Grid", "Point layout", "Perimeter",
                "Perimeter creates boundary/grid-edge setting-out points. Full grid fills the selected boundary extents and clips candidates to the closed polyline.",
                new[] { "Perimeter", "Full grid" });
'@
    $modeWithSurfaces = @'
            settings.AddChoice(
                "Mode", "01 Grid", "Point layout", "Perimeter",
                "Perimeter creates boundary/grid-edge setting-out points. Full grid fills the selected boundary extents and clips candidates to the closed polyline.",
                new[] { "Perimeter", "Full grid" });
            settings.AddChoice(
                "BaseSurface", "02 Surfaces", "Base / NG surface", "<None>",
                "Optional existing-ground/base surface. Its elevation is sampled at every grid setting-out point and written to the linked table.",
                gridSurfaceChoices);
            settings.AddChoice(
                "ComparisonSurface", "02 Surfaces", "Comparison / Design surface", "<None>",
                "Optional comparison/design surface. Difference is Comparison - Base and refreshes when linked source geometry changes.",
                gridSurfaceChoices);
'@
    $grid = ReplaceRequired $grid $modeBlock $modeWithSurfaces 'Dynamic Grid Base/Comparison popup controls'

    $grid = $grid.Replace('"Prefix", "02 Numbering"','"Prefix", "03 Numbering"')
    $grid = $grid.Replace('"Start", "02 Numbering"','"Start", "03 Numbering"')
    $grid = $grid.Replace('"SwapXY", "03 Coordinate Display"','"SwapXY", "04 Coordinate Display"')
    $grid = $grid.Replace('"ReverseSigns", "03 Coordinate Display"','"ReverseSigns", "04 Coordinate Display"')
    $grid = $grid.Replace('"PaperHeight", "04 Annotation"','"PaperHeight", "05 Annotation"')

    $linkAnchor = '                StartNumber = settings.Integer("Start", 1),'
    $linkAddition = $linkAnchor + "`r`n" +
        '                BaseSurfaceName = settings.Text("BaseSurface"),' + "`r`n" +
        '                ComparisonSurfaceName = settings.Text("ComparisonSurface"),'
    $grid = ReplaceRequired $grid $linkAnchor $linkAddition 'Dynamic Grid surface link values'
}

# Pass the current transaction/CivilDocument into table population so surface
# sampling happens in the same safe transaction as the linked table refresh.
$grid = $grid.Replace(
    'PopulateTable(document.Database, table, records, link);',
    'PopulateTable(document.Database, transaction, civil, table, records, link);')

$gridPopulate = @'
        private static void PopulateTable(
            Database database,
            Transaction transaction,
            CivilDocument civil,
            Table table,
            IList<GridRecord> records,
            DynamicGridLink link)
        {
            double textHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                link.PaperHeight > 0.0 ? link.PaperHeight : 2.0);
            table.SetSize(records.Count + 2, 9);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 9.0, 0.001));
            table.Columns[1].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[5].Width = Math.Max(textHeight * 12.0, 0.001);
            table.Columns[6].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[7].Width = Math.Max(textHeight * 12.0, 0.001);
            table.Cells[0, 0].TextString =
                "CE GRID SETTING-OUT - " + link.Mode.ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 8));
            string[] headings =
            {
                "POINT", "SOURCE", "X", "Y", "Z",
                "BASE LEVEL", "COMPARISON LEVEL", "DIFFERENCE", "MODE"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];

            Autodesk.Civil.DatabaseServices.Surface baseSurface = FindGridSurface(
                transaction,
                civil,
                link.BaseSurfaceName);
            Autodesk.Civil.DatabaseServices.Surface comparisonSurface = FindGridSurface(
                transaction,
                civil,
                link.ComparisonSurfaceName);

            for (int index = 0; index < records.Count; index++)
            {
                GridRecord record = records[index];
                int row = index + 2;
                double displayX = link.SwapXY ? record.Point.Y : record.Point.X;
                double displayY = link.SwapXY ? record.Point.X : record.Point.Y;
                if (link.ReverseSigns)
                {
                    displayX = -displayX;
                    displayY = -displayY;
                }

                double baseLevel;
                bool hasBase = TryGridSurfaceLevel(baseSurface, record.Point, out baseLevel);
                double comparisonLevel;
                bool hasComparison = TryGridSurfaceLevel(
                    comparisonSurface,
                    record.Point,
                    out comparisonLevel);

                table.Cells[row, 0].TextString = record.Name;
                table.Cells[row, 1].TextString = record.Source;
                table.Cells[row, 2].TextString = displayX.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = displayY.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = hasBase
                    ? baseLevel.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 6].TextString = hasComparison
                    ? comparisonLevel.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 7].TextString = hasBase && hasComparison
                    ? (comparisonLevel - baseLevel).ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = link.Mode;
            }

            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }
'@
$grid = ReplaceMethod $grid '        private static void PopulateTable(' $gridPopulate 'Dynamic Grid surface-aware linked table'

if (-not $grid.Contains('private static List<string> ReadGridSurfaceNames(')) {
    $surfaceHelpers = @'
        private static List<string> ReadGridSurfaceNames(
            Database database,
            CivilDocument civil)
        {
            var names = new List<string>();
            if (database == null || civil == null) return names;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    Autodesk.Civil.DatabaseServices.Surface surface;
                    try
                    {
                        surface = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Autodesk.Civil.DatabaseServices.Surface;
                    }
                    catch
                    {
                        continue;
                    }
                    if (surface != null && !string.IsNullOrWhiteSpace(surface.Name))
                        names.Add(surface.Name);
                }
            }
            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Autodesk.Civil.DatabaseServices.Surface FindGridSurface(
            Transaction transaction,
            CivilDocument civil,
            string name)
        {
            if (transaction == null || civil == null ||
                string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "<None>", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (ObjectId id in civil.GetSurfaceIds())
            {
                Autodesk.Civil.DatabaseServices.Surface surface;
                try
                {
                    surface = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Autodesk.Civil.DatabaseServices.Surface;
                }
                catch
                {
                    continue;
                }
                if (surface != null && string.Equals(
                    surface.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return surface;
            }
            return null;
        }

        private static bool TryGridSurfaceLevel(
            Autodesk.Civil.DatabaseServices.Surface surface,
            Point3d point,
            out double elevation)
        {
            elevation = 0.0;
            if (surface == null) return false;
            try
            {
                elevation = surface.FindElevationAtXY(point.X, point.Y);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch
            {
                return false;
            }
        }

'@
    $selectMarker = '        private static List<ObjectId> SelectBoundaries(Document document)'
    $selectIndex = $grid.IndexOf($selectMarker,[StringComparison]::Ordinal)
    if ($selectIndex -lt 0) {
        throw 'August 19 could not locate Dynamic Grid SelectBoundaries for surface helpers.'
    }
    $grid = $grid.Insert($selectIndex,($surfaceHelpers -replace "`r?`n","`r`n"))
}

# Persist the two selected surface names with the linked table.
if (-not $grid.Contains('"BaseSurface=" + (link.BaseSurfaceName')) {
    $persistAnchor = @'
                new TypedValue((int)DxfCode.Text, "ReverseSigns=" + (link.ReverseSigns ? "1" : "0"))
'@
    $persistReplacement = @'
                new TypedValue((int)DxfCode.Text, "ReverseSigns=" + (link.ReverseSigns ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "BaseSurface=" + (link.BaseSurfaceName ?? string.Empty)),
                new TypedValue((int)DxfCode.Text, "ComparisonSurface=" + (link.ComparisonSurfaceName ?? string.Empty))
'@
    $grid = ReplaceRequired $grid $persistAnchor $persistReplacement 'Dynamic Grid surface persistence'
}

if (-not $grid.Contains('text.StartsWith("BaseSurface="')) {
    $readAnchor = @'
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
'@
    $readReplacement = @'
                else if (text.StartsWith("BaseSurface=", StringComparison.OrdinalIgnoreCase))
                    link.BaseSurfaceName = text.Substring(12);
                else if (text.StartsWith("ComparisonSurface=", StringComparison.OrdinalIgnoreCase))
                    link.ComparisonSurfaceName = text.Substring(18);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
'@
    $grid = ReplaceRequired $grid $readAnchor $readReplacement 'Dynamic Grid surface readback'
}

if (-not $grid.Contains('internal string BaseSurfaceName')) {
    $fieldAnchor = @'
            internal bool ReverseSigns;
'@
    $fieldReplacement = @'
            internal bool ReverseSigns;
            internal string BaseSurfaceName = "<None>";
            internal string ComparisonSurfaceName = "<None>";
'@
    $grid = ReplaceRequired $grid $fieldAnchor $fieldReplacement 'Dynamic Grid surface link fields'
}
WriteText $gridPath $grid

# -----------------------------------------------------------------------------
# 3. Sewer network from MULTIPLE polylines: when the older batch entry point is
#    used for Sewer, pass the already-selected complete set to CE_SEWERNETWORKMULTI.
#    The direct engine creates the network via the Civil 3D API, so the native
#    CreateNetworkFromObject single-object selection prompt is never opened.
# -----------------------------------------------------------------------------
$oldSewerSelection = @'
            Editor editor = document.Editor;
            editor.SetImpliedSelection(new ObjectId[0]);

            PromptSelectionResult selected = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect ALL sewer source lines/polylines/feature lines for ONE sewer network: ",
                    MessageForRemoval = "\nRemove sewer source objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
'@
$newSewerSelection = @'
            Editor editor = document.Editor;
            PromptSelectionResult selected = editor.SelectImplied();
            if (selected.Status != PromptStatus.OK ||
                selected.Value == null ||
                selected.Value.Count == 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                selected = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect ALL sewer source lines/polylines/feature lines for ONE sewer network: ",
                        MessageForRemoval = "\nRemove sewer source objects: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
'@
if (-not $sewer.Contains('PromptSelectionResult selected = editor.SelectImplied();')) {
    $sewer = ReplaceRequired $sewer $oldSewerSelection $newSewerSelection 'Direct sewer multi-source preselection'
}
WriteText $sewerPath $sewer

$batchAnchor = @'
            NetworkFromObjectBatchManager.Start(document, sources, discipline);
            document.Editor.WriteMessage(
                "\nCE_NETWORKFROMPOLYLINESBATCH started. Sources queued={0}; discipline={1}. Complete each Civil 3D native network dialog normally; CE Tools will advance through the complete selected source set automatically.",
                sources.Count,
                discipline);
'@
$batchReplacement = @'
            if (string.Equals(discipline, "Sewer", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.SetImpliedSelection(sources.ToArray());
                document.Editor.WriteMessage(
                    "\nCE_NETWORKFROMPOLYLINESBATCH: Sewer sources selected={0}. Passing the COMPLETE selected set to CE_SEWERNETWORKMULTI; there is no second one-by-one CreateNetworkFromObject selection prompt.",
                    sources.Count);
                document.SendStringToExecute("CE_SEWERNETWORKMULTI ", true, false, true);
                return;
            }

            NetworkFromObjectBatchManager.Start(document, sources, discipline);
            document.Editor.WriteMessage(
                "\nCE_NETWORKFROMPOLYLINESBATCH started. Sources queued={0}; discipline={1}. Complete each Civil 3D native network dialog normally; CE Tools will advance through the complete selected source set automatically.",
                sources.Count,
                discipline);
'@
if (-not $batch.Contains('Passing the COMPLETE selected set to CE_SEWERNETWORKMULTI')) {
    $batch = ReplaceRequired $batch $batchAnchor $batchReplacement 'Sewer batch route to true multi-source engine'
}
WriteText $batchPath $batch

# Final staged guards. Fail before MSBuild rather than silently compiling an old path.
$siteCheck = ReadText $sitePath
$gridCheck = ReadText $gridPath
$batchCheck = ReadText $batchPath
$sewerCheck = ReadText $sewerPath

foreach ($marker in @(
    'siteGridMinimumSpacing * 0.40',
    'siteGridFrameSpan * 0.008',
    'label.Width = 0.0;',
    'label.Contents = string.IsNullOrWhiteSpace(contents)')) {
    if (-not $siteCheck.Contains($marker)) {
        throw "August 19 Site Grid visibility marker missing: $marker"
    }
}
foreach ($marker in @(
    '"BaseSurface", "02 Surfaces"',
    '"ComparisonSurface", "02 Surfaces"',
    '"BASE LEVEL", "COMPARISON LEVEL", "DIFFERENCE"',
    'FindElevationAtXY(point.X, point.Y)',
    'BaseSurfaceName',
    'ComparisonSurfaceName')) {
    if (-not $gridCheck.Contains($marker)) {
        throw "August 19 Dynamic Grid surface marker missing: $marker"
    }
}
if (-not $batchCheck.Contains('Passing the COMPLETE selected set to CE_SEWERNETWORKMULTI')) {
    throw 'August 19 Sewer batch route did not receive the direct multi-source handoff.'
}
if (-not $sewerCheck.Contains('PromptSelectionResult selected = editor.SelectImplied();')) {
    throw 'August 19 direct sewer multi-source command does not consume the existing multiple preselection.'
}

Write-Host 'August 19 Site Grid visibility repair applied: coordinate MText now has a spacing/frame-aware readable height floor.' -ForegroundColor Green
Write-Host 'August 19 Grid Setting-Out now offers Base/NG and Comparison/Design surfaces and stores Base, Comparison and Difference levels in the linked table.' -ForegroundColor Green
Write-Host 'August 19 Sewer multi-polyline production now reuses the complete selection and bypasses the native one-object CreateNetworkFromObject prompt.' -ForegroundColor Green
