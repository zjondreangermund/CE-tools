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
        throw "Setting-out coordinate display source missing: $path"
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
    if (-not $text.Contains($old)) { throw "Setting-out coordinate display anchor not found: $label" }
    return $text.Replace($old,$new)
}
function ReplaceMethod([string]$text,[string]$marker,[string]$replacement,[string]$label) {
    $replacement = $replacement -replace "`r?`n","`r`n"
    if ($text.Contains($replacement.Trim())) { return $text }
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Setting-out coordinate method marker not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Setting-out coordinate opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "Setting-out coordinate closing brace not found: $label" }
    return $text.Substring(0,$start) + $replacement + $text.Substring($close + 1)
}

# -----------------------------------------------------------------------------
# 1. Vertex Setting-Out: use one consistent DISPLAY transform. Geometry and COGO
#    Easting/Northing remain true drawing coordinates. Existing links using the
#    historical "Y then X" setting are treated as a numeric X/Y swap.
# -----------------------------------------------------------------------------
$vertexPath = Required 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath

$oldVertexChoices = @'
            settings.AddChoice(
                "CoordinateOrder", "04 Coordinate Display", "Coordinate order", "X then Y",
                "Swap only the displayed X/Y letters/headings. Numeric coordinate values and true drawing coordinates remain unchanged.",
                new[] { "X then Y", "Y then X" });
            settings.AddChoice(
                "XSign", "04 Coordinate Display", "Displayed X sign", "Keep X sign",
                "Keep or reverse the displayed X sign without changing the COGO point or source geometry.",
                new[] { "Keep X sign", "Reverse X sign" });
            settings.AddChoice(
                "YSign", "04 Coordinate Display", "Displayed Y sign", "Keep Y sign",
                "Keep or reverse the displayed Y sign without changing the COGO point or source geometry.",
                new[] { "Keep Y sign", "Reverse Y sign" });
'@
$newVertexChoices = @'
            settings.AddChoice(
                "SwapXY", "04 Coordinate Display", "Swap X / Y values", "No",
                "Yes makes displayed X use the true Y value and displayed Y use the true X value. Geometry and COGO coordinates do not move.",
                new[] { "No", "Yes" });
            settings.AddChoice(
                "ReverseSigns", "04 Coordinate Display", "Reverse coordinate signs", "No",
                "Yes reverses the signs of both displayed X and displayed Y after any X/Y swap. Geometry and COGO coordinates remain unchanged.",
                new[] { "No", "Yes" });
'@
$vertex = ReplaceRequired $vertex $oldVertexChoices $newVertexChoices 'Vertex popup Swap X/Y + Reverse signs'

$oldVertexRead = @'
            string coordinateOrder = settings.Text("CoordinateOrder");
            string xSign = settings.Text("XSign");
            string ySign = settings.Text("YSign");
'@
$newVertexRead = @'
            string coordinateOrder = string.Equals(
                settings.Text("SwapXY"),
                "Yes",
                StringComparison.OrdinalIgnoreCase)
                ? "Swap X/Y"
                : "X then Y";
            bool reverseCoordinateSigns = string.Equals(
                settings.Text("ReverseSigns"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            string xSign = reverseCoordinateSigns ? "Reverse X sign" : "Keep X sign";
            string ySign = reverseCoordinateSigns ? "Reverse Y sign" : "Keep Y sign";
'@
$vertex = ReplaceRequired $vertex $oldVertexRead $newVertexRead 'Vertex popup values -> persisted legacy link fields'

$vertexDisplayX = @'
        private static double DisplayX(
            Point3d point,
            VertexSettingLink link)
        {
            bool swap = link != null &&
                (string.Equals(
                    link.CoordinateOrder,
                    "Swap X/Y",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    link.CoordinateOrder,
                    "Y then X",
                    StringComparison.OrdinalIgnoreCase));
            double value = swap ? point.Y : point.X;
            return link != null && string.Equals(
                link.XSign,
                "Reverse X sign",
                StringComparison.OrdinalIgnoreCase)
                ? -value
                : value;
        }
'@
$vertex = ReplaceMethod $vertex '        private static double DisplayX(' $vertexDisplayX 'Vertex DisplayX numeric transform'

$vertexDisplayY = @'
        private static double DisplayY(
            Point3d point,
            VertexSettingLink link)
        {
            bool swap = link != null &&
                (string.Equals(
                    link.CoordinateOrder,
                    "Swap X/Y",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    link.CoordinateOrder,
                    "Y then X",
                    StringComparison.OrdinalIgnoreCase));
            double value = swap ? point.X : point.Y;
            return link != null && string.Equals(
                link.YSign,
                "Reverse Y sign",
                StringComparison.OrdinalIgnoreCase)
                ? -value
                : value;
        }
'@
$vertex = ReplaceMethod $vertex '        private static double DisplayY(' $vertexDisplayY 'Vertex DisplayY numeric transform'

$vertexLabelText = @'
        private static string LabelText(
            VertexSettingRecord record,
            VertexSettingLink link)
        {
            double displayX = DisplayX(record.Point, link);
            double displayY = DisplayY(record.Point, link);
            return string.Join(
                "\\P",
                record.PointName,
                "X=" + displayX.ToString("N3", CultureInfo.CurrentCulture),
                "Y=" + displayY.ToString("N3", CultureInfo.CurrentCulture),
                "Z=" + record.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }
'@
$vertex = ReplaceMethod $vertex '        private static string LabelText(' $vertexLabelText 'Vertex MText/MLeader display labels'

$oldVertexHeadings = @'
            bool yFirst = string.Equals(
                link.CoordinateOrder,
                "Y then X",
                StringComparison.OrdinalIgnoreCase);
            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT",
                yFirst ? "Y" : "X",
                yFirst ? "X" : "Y",
                "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"
            };
'@
$newVertexHeadings = @'
            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT",
                "X", "Y",
                "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"
            };
'@
$vertex = ReplaceRequired $vertex $oldVertexHeadings $newVertexHeadings 'Vertex table fixed display headings'
$vertex = $vertex.Replace(
    '                // Keep the numeric coordinate columns fixed and swap only their' + "`r`n" +
    '                // displayed X/Y headings when requested. Drawing coordinates never change.' + "`r`n",
    '                // DisplayX/DisplayY apply the saved display-only X/Y swap and sign transform.' + "`r`n")
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 2. Dynamic Grid Setting-Out: add the same two popup controls, persist them in
#    the linked table, and transform table X/Y values only. COGO stays at true XY.
# -----------------------------------------------------------------------------
$dynamicPath = Required 'August18DynamicGridSettingOutCommands.cs'
$dynamic = ReadText $dynamicPath

$dynamicChoiceAnchor = @'
            settings.AddPositiveInteger(
                "Start", "02 Numbering", "Starting number", 1,
                "First logical grid point number.");
            settings.AddPaperHeight(
                "PaperHeight", "03 Annotation", "Table paper text height", 2.0,
'@
$dynamicChoiceReplacement = @'
            settings.AddPositiveInteger(
                "Start", "02 Numbering", "Starting number", 1,
                "First logical grid point number.");
            settings.AddChoice(
                "SwapXY", "03 Coordinate Display", "Swap X / Y values", "No",
                "Yes makes displayed X use true Y and displayed Y use true X. COGO points and source geometry remain unchanged.",
                new[] { "No", "Yes" });
            settings.AddChoice(
                "ReverseSigns", "03 Coordinate Display", "Reverse coordinate signs", "No",
                "Yes reverses both displayed coordinate signs after any X/Y swap. True Civil 3D coordinates remain unchanged.",
                new[] { "No", "Yes" });
            settings.AddPaperHeight(
                "PaperHeight", "04 Annotation", "Table paper text height", 2.0,
'@
$dynamic = ReplaceRequired $dynamic $dynamicChoiceAnchor $dynamicChoiceReplacement 'Dynamic Grid coordinate popup controls'

$dynamicLinkAnchor = @'
                StartNumber = settings.Integer("Start", 1),
                PaperHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
'@
$dynamicLinkReplacement = @'
                StartNumber = settings.Integer("Start", 1),
                SwapXY = string.Equals(settings.Text("SwapXY"), "Yes", StringComparison.OrdinalIgnoreCase),
                ReverseSigns = string.Equals(settings.Text("ReverseSigns"), "Yes", StringComparison.OrdinalIgnoreCase),
                PaperHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
'@
$dynamic = ReplaceRequired $dynamic $dynamicLinkAnchor $dynamicLinkReplacement 'Dynamic Grid save popup transform'

$dynamicPopulate = @'
        private static void PopulateTable(
            Database database,
            Table table,
            IList<GridRecord> records,
            DynamicGridLink link)
        {
            double textHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                link.PaperHeight > 0.0 ? link.PaperHeight : 2.0);
            table.SetSize(records.Count + 2, 6);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 9.0, 0.001));
            table.Columns[1].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Cells[0, 0].TextString =
                "CE GRID SETTING-OUT - " + link.Mode.ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            string[] headings = { "POINT", "SOURCE", "X", "Y", "Z", "MODE" };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];

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
                table.Cells[row, 0].TextString = record.Name;
                table.Cells[row, 1].TextString = record.Source;
                table.Cells[row, 2].TextString = displayX.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = displayY.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = link.Mode;
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
$dynamic = ReplaceMethod $dynamic '        private static void PopulateTable(' $dynamicPopulate 'Dynamic Grid transformed table values'

$dynamicWriteAnchor = @'
                new TypedValue((int)DxfCode.Text, "Start=" + link.StartNumber.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "PaperHeight=" + link.PaperHeight.ToString("R", CultureInfo.InvariantCulture))
'@
$dynamicWriteReplacement = @'
                new TypedValue((int)DxfCode.Text, "Start=" + link.StartNumber.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "PaperHeight=" + link.PaperHeight.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "SwapXY=" + (link.SwapXY ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "ReverseSigns=" + (link.ReverseSigns ? "1" : "0"))
'@
$dynamic = ReplaceRequired $dynamic $dynamicWriteAnchor $dynamicWriteReplacement 'Dynamic Grid persist display transform'

$dynamicReadAnchor = @'
                else if (text.StartsWith("PaperHeight=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out link.PaperHeight);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
'@
$dynamicReadReplacement = @'
                else if (text.StartsWith("PaperHeight=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out link.PaperHeight);
                else if (text.StartsWith("SwapXY=", StringComparison.OrdinalIgnoreCase))
                    link.SwapXY = string.Equals(text.Substring(7), "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text.Substring(7), "true", StringComparison.OrdinalIgnoreCase);
                else if (text.StartsWith("ReverseSigns=", StringComparison.OrdinalIgnoreCase))
                    link.ReverseSigns = string.Equals(text.Substring(13), "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text.Substring(13), "true", StringComparison.OrdinalIgnoreCase);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
'@
$dynamic = ReplaceRequired $dynamic $dynamicReadAnchor $dynamicReadReplacement 'Dynamic Grid read persisted transform'

$dynamicClassAnchor = @'
            internal int StartNumber = 1;
            internal double PaperHeight = 2.0;
'@
$dynamicClassReplacement = @'
            internal int StartNumber = 1;
            internal bool SwapXY;
            internal bool ReverseSigns;
            internal double PaperHeight = 2.0;
'@
$dynamic = ReplaceRequired $dynamic $dynamicClassAnchor $dynamicClassReplacement 'Dynamic Grid link transform fields'
WriteText $dynamicPath $dynamic

# -----------------------------------------------------------------------------
# 3. Site Grid: expose Swap X/Y + Reverse signs, persist them, and ensure border
#    coordinate text has a visible model-space floor based on grid spacing.
# -----------------------------------------------------------------------------
$sitePath = Required 'August12SurveySiteGridCommands.cs'
$site = ReadText $sitePath

$oldSiteChoice = @'
            model.AddChoice(
                "AxisOrder",
                "Grid",
                "Coordinate display",
                current.ReverseXY
                    ? "Reverse X / Y labels"
                    : "Normal X / Y labels",
                "Normal: vertical lines show X and horizontal lines show Y. Reverse swaps the X/Y label convention without changing geometry.",
                new[] { "Normal X / Y labels", "Reverse X / Y labels" });
'@
$newSiteChoice = @'
            model.AddChoice(
                "SwapXY",
                "Coordinate Display",
                "Swap X / Y values",
                current.ReverseXY ? "Yes" : "No",
                "Yes swaps the displayed coordinate axes only: vertical-grid true X values display as Y and horizontal-grid true Y values display as X.",
                new[] { "No", "Yes" });
            model.AddChoice(
                "ReverseSigns",
                "Coordinate Display",
                "Reverse coordinate signs",
                current.ReverseSigns ? "Yes" : "No",
                "Yes reverses both displayed coordinate signs after any X/Y swap. The site boundary and grid geometry remain unchanged.",
                new[] { "No", "Yes" });
'@
$site = ReplaceRequired $site $oldSiteChoice $newSiteChoice 'Site Grid coordinate popup controls'

$oldSiteSettings = @'
                ReverseXY = string.Equals(
                    model.Text("AxisOrder"),
                    "Reverse X / Y labels",
                    StringComparison.OrdinalIgnoreCase),
                PaperTextHeight = ParsePaperHeight(
'@
$newSiteSettings = @'
                ReverseXY = string.Equals(
                    model.Text("SwapXY"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                ReverseSigns = string.Equals(
                    model.Text("ReverseSigns"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                PaperTextHeight = ParsePaperHeight(
'@
$site = ReplaceRequired $site $oldSiteSettings $newSiteSettings 'Site Grid save coordinate transform'

$oldSiteHeight = @'
            double modelTextHeight = ModelTextHeight(
                database,
                settings.PaperTextHeight);
            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);
'@
$newSiteHeight = @'
            double modelTextHeight = ModelTextHeight(
                database,
                settings.PaperTextHeight);
            // Site-grid coordinate text must remain visible even when a drawing has
            // an unusual or missing annotation-scale conversion. Use a modest floor
            // tied to the selected grid spacing, never to the true coordinate values.
            double siteGridTextFloor = Math.Max(
                Math.Min(settings.SpacingX, settings.SpacingY) * 0.08,
                0.001);
            modelTextHeight = Math.Max(modelTextHeight, siteGridTextFloor);
            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);
'@
$site = ReplaceRequired $site $oldSiteHeight $newSiteHeight 'Site Grid visible text-height floor'

$oldSiteXLabel = @'
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),
'@
$newSiteXLabel = @'
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                double displayValue = settings.ReverseSigns
                    ? -xValues[xIndex]
                    : xValues[xIndex];
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),
'@
$site = ReplaceRequired $site $oldSiteXLabel $newSiteXLabel 'Site Grid vertical-axis transformed labels'

$oldSiteYLabel = @'
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),
'@
$newSiteYLabel = @'
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                double displayValue = settings.ReverseSigns
                    ? -yValues[yIndex]
                    : yValues[yIndex];
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),
'@
$site = ReplaceRequired $site $oldSiteYLabel $newSiteYLabel 'Site Grid horizontal-axis transformed labels'

$oldSiteWrite = @'
                    new TypedValue((int)DxfCode.Real, settings.PaperTextHeight),
                    new TypedValue((int)DxfCode.Int16, settings.CreatePoints ? 1 : 0)
'@
$newSiteWrite = @'
                    new TypedValue((int)DxfCode.Real, settings.PaperTextHeight),
                    new TypedValue((int)DxfCode.Int16, settings.CreatePoints ? 1 : 0),
                    new TypedValue((int)DxfCode.Int16, settings.ReverseSigns ? 1 : 0)
'@
$site = ReplaceRequired $site $oldSiteWrite $newSiteWrite 'Site Grid persist reverse signs'

$oldSiteRead = @'
                settings.CreatePoints = Convert.ToInt32(
                    values[5].Value,
                    CultureInfo.InvariantCulture) != 0;
                return settings.SpacingX > 0.0 &&
'@
$newSiteRead = @'
                settings.CreatePoints = Convert.ToInt32(
                    values[5].Value,
                    CultureInfo.InvariantCulture) != 0;
                settings.ReverseSigns = values.Length >= 7 && Convert.ToInt32(
                    values[6].Value,
                    CultureInfo.InvariantCulture) != 0;
                return settings.SpacingX > 0.0 &&
'@
$site = ReplaceRequired $site $oldSiteRead $newSiteRead 'Site Grid backward-compatible reverse sign read'

$oldSiteFields = @'
            internal bool ReverseXY;
            internal double PaperTextHeight;
'@
$newSiteFields = @'
            internal bool ReverseXY;
            internal bool ReverseSigns;
            internal double PaperTextHeight;
'@
$site = ReplaceRequired $site $oldSiteFields $newSiteFields 'Site Grid settings reverse signs field'

$oldSiteDefault = @'
                    ReverseXY = false,
                    PaperTextHeight = 2.0,
'@
$newSiteDefault = @'
                    ReverseXY = false,
                    ReverseSigns = false,
                    PaperTextHeight = 2.0,
'@
$site = ReplaceRequired $site $oldSiteDefault $newSiteDefault 'Site Grid reverse signs default'
WriteText $sitePath $site

# -----------------------------------------------------------------------------
# Final guards: fail before MSBuild if any setting-out display path did not receive
# the requested display-only transform. This prevents silently compiling headings-
# only swapping again.
# -----------------------------------------------------------------------------
$vertex = ReadText $vertexPath
foreach ($required in @(
    '"SwapXY", "04 Coordinate Display", "Swap X / Y values"',
    '"ReverseSigns", "04 Coordinate Display", "Reverse coordinate signs"',
    '? point.Y : point.X;',
    '? point.X : point.Y;',
    '"X=" + displayX.ToString',
    '"Y=" + displayY.ToString')) {
    if (-not $vertex.Contains($required)) {
        throw "Vertex setting-out display transform marker missing: $required"
    }
}
if ($vertex.Contains('Swap only the displayed X/Y letters/headings')) {
    throw 'Vertex Setting-Out still contains the obsolete headings-only X/Y swap.'
}

$dynamic = ReadText $dynamicPath
foreach ($required in @(
    '"SwapXY", "03 Coordinate Display", "Swap X / Y values"',
    '"ReverseSigns", "03 Coordinate Display", "Reverse coordinate signs"',
    'double displayX = link.SwapXY ? record.Point.Y : record.Point.X;',
    'double displayY = link.SwapXY ? record.Point.X : record.Point.Y;',
    '"SwapXY=" + (link.SwapXY ? "1" : "0")',
    '"ReverseSigns=" + (link.ReverseSigns ? "1" : "0")')) {
    if (-not $dynamic.Contains($required)) {
        throw "Dynamic Grid display transform marker missing: $required"
    }
}

$site = ReadText $sitePath
foreach ($required in @(
    '"SwapXY",',
    '"ReverseSigns",',
    'double siteGridTextFloor = Math.Max(',
    'double displayValue = settings.ReverseSigns',
    'new TypedValue((int)DxfCode.Int16, settings.ReverseSigns ? 1 : 0)',
    'internal bool ReverseSigns;')) {
    if (-not $site.Contains($required)) {
        throw "Site Grid display/text marker missing: $required"
    }
}
if ($site.Contains('"Reverse X / Y labels"')) {
    throw 'Site Grid still contains the obsolete label-letter-only X/Y option.'
}

Write-Host 'Vertex Setting-Out now swaps numeric display X/Y values and can reverse both displayed signs without moving geometry.' -ForegroundColor Green
Write-Host 'Dynamic Grid tables now persist the same Swap X/Y and Reverse signs display options.' -ForegroundColor Green
Write-Host 'Site Grid coordinate labels now use the same display transform and a visible text-height floor.' -ForegroundColor Green
Write-Host 'True Civil 3D source geometry and COGO Easting/Northing remain unchanged; only setting-out display values are transformed.' -ForegroundColor Green
