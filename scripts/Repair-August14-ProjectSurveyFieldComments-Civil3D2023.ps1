[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required source was not found: $path" }
    return [System.IO.File]::ReadAllText($path)
}
function Write-Text([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), $utf8)
}
function Replace-MethodBody {
    param([string]$Text,[string]$Signature,[string]$Body)
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method signature was not found: $Signature" }
    $open = $Text.IndexOf('{', $start)
    if ($open -lt 0) { throw "Opening brace was not found for: $Signature" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '{') { $depth++ }
        elseif ($Text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace was not found for: $Signature" }
    return $Text.Substring(0,$open) + "{`r`n" + $Body.TrimEnd() + "`r`n        }" + $Text.Substring($close+1)
}

# ---------------------------------------------------------------------------
# PROJECT: Project Setup is authoritative for register/title-block metadata.
# ---------------------------------------------------------------------------
$registerPath = Join-Path $src 'ProductionDrawingRegisterCommands.cs'
$register = Read-Text $registerPath

$headerPattern = 'internal static readonly string\[\] HeaderFields\s*=\s*\{.*?\};'
$headerReplacement = @'
internal static readonly string[] HeaderFields =
        {
            "Project Name",
            "Project Number",
            "Client",
            "Company",
            "Country",
            "Town",
            "Coordinate System",
            "Standards",
            "Drawing Template",
            "Units",
            "Project Stage",
            "Revision",
            "Issue Date",
            "Drawing Number Prefix",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By",
            "Title Block Source"
        };
'@
$updated = [regex]::Replace($register, $headerPattern, $headerReplacement, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($updated -eq $register -and -not $register.Contains('"Coordinate System"')) {
    throw 'Drawing-register HeaderFields could not be upgraded.'
}
$register = $updated

$projectDefaultsBody = @'
            foreach (string field in HeaderFields)
            {
                if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value;
                if (project != null && project.TryGetValue(field, out value))
                    Headers[field] = value ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(Header("Issue Date")))
                Headers["Issue Date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(Header("Drawing Number Prefix")))
                Headers["Drawing Number Prefix"] = "CE";
            if (string.IsNullOrWhiteSpace(Header("Title Block Source")))
            {
                string bundled = ProductionTitleBlockManager.FindBundledSource();
                if (!string.IsNullOrWhiteSpace(bundled)) Headers["Title Block Source"] = bundled;
            }
'@
$register = Replace-MethodBody -Text $register -Signature 'internal void ApplyProjectDefaults(IDictionary<string, string> project)' -Body $projectDefaultsBody

$rowDefaultsBody = @'
            string prefix = Header("Drawing Number Prefix");
            string stage = Header("Project Stage");
            string revision = Header("Revision");
            string issueDate = Header("Issue Date");
            int next = 1;
            foreach (ProductionDrawingRegisterRow row in Rows)
            {
                if (string.IsNullOrWhiteSpace(row.DrawingNumber))
                    row.DrawingNumber = (string.IsNullOrWhiteSpace(prefix) ? "CE" : prefix) + "-" + next.ToString("000", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(row.Title)) row.Title = row.Layout;
                if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = "Project drawing";
                if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = "As shown";
                row.Stage = stage ?? string.Empty;
                row.Revision = revision ?? string.Empty;
                row.IssueDate = issueDate ?? string.Empty;
                next++;
            }
'@
$register = Replace-MethodBody -Text $register -Signature 'internal void ApplyRowDefaults()' -Body $rowDefaultsBody

$backWrite = @'
            ProjectSetupCommands.MergeSharedProjectMetadata(
                document.Database,
                result.Headers);
'@
$register = $register.Replace(($backWrite -replace "`n","`r`n"), '')
$register = $register.Replace($backWrite, '')

if (-not $register.Contains('{ "COUNTRY", data.Header("Country") }')) {
    $anchor = '                { "COMPANY", data.Header("Company") },'
    if (-not $register.Contains($anchor)) { throw 'Title-block Company attribute anchor was not found.' }
    $extra = @'
                { "COMPANY", data.Header("Company") },
                { "COUNTRY", data.Header("Country") },
                { "TOWN", data.Header("Town") },
                { "CITY", data.Header("Town") },
                { "COORDINATESYSTEM", data.Header("Coordinate System") },
                { "CRS", data.Header("Coordinate System") },
                { "STANDARDS", data.Header("Standards") },
                { "STANDARD", data.Header("Standards") },
                { "DRAWINGTEMPLATE", data.Header("Drawing Template") },
                { "TEMPLATE", data.Header("Drawing Template") },
                { "UNITS", data.Header("Units") },
'@
    $register = $register.Replace($anchor, ($extra.TrimEnd() -replace "`n","`r`n"))
}
Write-Text $registerPath $register
Write-Host 'Project Info now overrides shared drawing-register metadata; Town/CRS/standards/template/units flow to supported title blocks.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# PROJECT/SURVEY: route the progressive centres to the new review commands.
# ---------------------------------------------------------------------------
$centresPath = Join-Path $src 'August14StructuredDisciplineProductionCentres.cs'
$centres = Read-Text $centresPath
$centres = $centres.Replace('CE-Project Setup - Last Saved or Blank", "CE_PROJECTSETUPCHOICE"', 'CE-Project Setup - Last Saved / Company Standard / Blank", "CE_PROJECTSETUPCHOICE2"')
$centres = $centres.Replace('"CE_DRAWINGREGISTEREDIT"', '"CE_DRAWINGREGISTERPROJECTSYNC"')
if (-not $centres.Contains('CE_PROJECTCOMPANYSTANDARDSAVE')) {
    $anchor = '                    A("CE-Project Setup - Last Saved / Company Standard / Blank", "CE_PROJECTSETUPCHOICE2", "Open Last Saved project information or a Standard Blank project form.", "01 SETTINGS"),'
    if ($centres.Contains($anchor)) {
        $centres = $centres.Replace($anchor, $anchor + "`r`n" + '                    A("CE-Save / Update Company Standard", "CE_PROJECTCOMPANYSTANDARDSAVE", "Store reusable company defaults without project-specific name/number/client/town/CRS values.", "01 SETTINGS"),')
    }
}
if (-not $centres.Contains('CE_NAMIBIALO')) {
    $anchor = '                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 SETTINGS"),'
    if (-not $centres.Contains($anchor)) { throw 'Survey Location centre anchor was not found.' }
    $centres = $centres.Replace($anchor, $anchor + "`r`n" + '                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 SETTINGS"),')
}
$centres = $centres.Replace('"CE_SURFACECOMPARETABLE"', '"CE_SURFACECOMPAREPRODUCTION"')
$centres = $centres.Replace('"CE_GRIDSETTINGOUT"', '"CE_GRIDSETTINGOUTMULTI"')
if (-not $centres.Contains('CE_SURFACEREPORTPRODUCTION')) {
    $anchor = '                    A("CE-Surface Tools", "CE_SURFTOOLS", "Review/create existing-ground surfaces.", "03 CREATE"),'
    if ($centres.Contains($anchor))
    {
        $centres = $centres.Replace($anchor, $anchor + "`r`n" + '                    A("CE-Surface Report - Popup / Linked Table / Excel", "CE_SURFACEREPORTPRODUCTION", "Review a Civil 3D surface in a popup, place a linked annotative table and/or export Excel.", "03 CREATE"),')
    }
}
if (-not $centres.Contains('CE_SURVEYREFRESHSAFE')) {
    $anchor = '                    A("CE-Refresh Surface Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked comparison tables.", "05 COMPLETE"),'
    if ($centres.Contains($anchor))
    {
        $centres = $centres.Replace($anchor, $anchor + "`r`n" + '                    A("CE-Survey Linked / Annotative Refresh", "CE_SURVEYREFRESHSAFE", "Refresh linked points/tables, restore original COGO label offsets and sync annotation scale.", "05 COMPLETE"),' + "`r`n" + '                    A("CE-Restore Annotative Scale Sync", "CE_ANNOSCALESYNC", "Apply the current annotation scale to supported survey labels, leaders and tables.", "05 COMPLETE"),')
    }
}
Write-Text $centresPath $centres
Write-Host 'Project and Survey progressive Production Centres now expose the August 14 field-review commands.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# SURVEY: prevent cumulative COGO label drift and refresh linked source points.
# ---------------------------------------------------------------------------
$dynamicPath = Join-Path $src 'UniversalDynamicRefreshCommands.cs'
$dynamic = Read-Text $dynamicPath
if (-not $dynamic.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) {
    $anchor = '                try' + "`r`n" + '                {' + "`r`n" + '                    LinkedRefreshEngine.Refresh(document, false);'
    if (-not $dynamic.Contains($anchor)) { throw 'Universal Dynamic linked-engine anchor was not found.' }
    $insert = '                try { August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document); }' + "`r`n" + '                catch { result.Warnings++; }' + "`r`n"
    $dynamic = $dynamic.Replace($anchor, $insert + $anchor)
}
if (-not $dynamic.Contains('MultiSurfaceComparisonTableStore.RefreshAll(document);')) {
    $anchor = '                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }' + "`r`n" + '                catch { result.Warnings++; }'
    if (-not $dynamic.Contains($anchor)) { throw 'Universal Dynamic survey-coordinate anchor was not found.' }
    $extra = @'
                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { DynamicCoordinateLinkStore.Refresh(document); }
                catch { result.Warnings++; }
                try { SurfaceComparisonLinkStore.RefreshAll(document); }
                catch { result.Warnings++; }
                try { MultiSurfaceComparisonTableStore.RefreshAll(document); }
                catch { result.Warnings++; }
                try { LinkedSurfaceReportTableStore.RefreshAll(document); }
                catch { result.Warnings++; }
'@
    $dynamic = $dynamic.Replace($anchor, ($extra.TrimEnd() -replace "`n","`r`n"))
}
if (-not $dynamic.Contains('August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);')) {
    $anchor = '                try { CeTablePresentationManager.CenterCeTables(document); }' + "`r`n" + '                catch { result.Warnings++; }'
    if (-not $dynamic.Contains($anchor)) { throw 'Universal Dynamic table-centering anchor was not found.' }
    $extra = @'
                try { August11SurveyRuntimeCommands.RestoreCogoLabels(document, null); }
                catch { result.Warnings++; }
                try { AnnotationScaleSyncManager.ApplyCurrentScale(document); }
                catch { result.Warnings++; }
                try { CeTablePresentationManager.CenterCeTables(document); }
                catch { result.Warnings++; }
'@
    $dynamic = $dynamic.Replace($anchor, ($extra.TrimEnd() -replace "`n","`r`n"))
}
Write-Text $dynamicPath $dynamic
Write-Host 'Universal Dynamic now refreshes source-linked survey points and re-applies the stored COGO label offset instead of accumulating movement.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# SURVEY SITE GRID: annotative labels, larger inside clearance, corner clearance.
# ---------------------------------------------------------------------------
$gridPath = Join-Path $src 'August12SurveySiteGridCommands.cs'
$grid = Read-Text $gridPath
$grid = $grid.Replace('double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);', 'double insideOffset = Math.Max(modelTextHeight * 2.75, 0.001);' + "`r`n" + '            double cornerClearance = Math.Max(modelTextHeight * 4.5, 0.001);')
if (-not $grid.Contains('double xLabel = xValues[xIndex];')) {
    $anchor = '                string prefix = settings.ReverseXY ? "Y: " : "X: ";' + "`r`n" + '                MText label = CreateLabel('
    $replacement = '                string prefix = settings.ReverseXY ? "Y: " : "X: ";' + "`r`n" + '                double xLabel = xValues[xIndex];' + "`r`n" + '                if (xIndex == 0) xLabel += cornerClearance;' + "`r`n" + '                else if (xIndex == xValues.Count - 1) xLabel -= cornerClearance;' + "`r`n" + '                MText label = CreateLabel('
    if (-not $grid.Contains($anchor)) { throw 'Site-grid X-label anchor was not found.' }
    $grid = $grid.Replace($anchor, $replacement)
    $grid = $grid.Replace('                        xValues[xIndex],' + "`r`n" + '                        bounds.MinY + insideOffset,', '                        xLabel,' + "`r`n" + '                        bounds.MinY + insideOffset,')
}
if (-not $grid.Contains('double yLabel = yValues[yIndex];')) {
    $anchor = '                string prefix = settings.ReverseXY ? "X: " : "Y: ";' + "`r`n" + '                MText label = CreateLabel('
    $replacement = '                string prefix = settings.ReverseXY ? "X: " : "Y: ";' + "`r`n" + '                double yLabel = yValues[yIndex];' + "`r`n" + '                if (yIndex == 0) yLabel += cornerClearance;' + "`r`n" + '                else if (yIndex == yValues.Count - 1) yLabel -= cornerClearance;' + "`r`n" + '                MText label = CreateLabel('
    if (-not $grid.Contains($anchor)) { throw 'Site-grid Y-label anchor was not found.' }
    $grid = $grid.Replace($anchor, $replacement)
    $grid = $grid.Replace('                        yValues[yIndex],' + "`r`n" + '                        bounds.Elevation),', '                        yLabel,' + "`r`n" + '                        bounds.Elevation),')
}
if (-not $grid.Contains('PaperAnnotationScale.SetAnnotative(label);')) {
    $anchor = '            label.Rotation = rotation;' + "`r`n" + '            return label;'
    if (-not $grid.Contains($anchor)) { throw 'Site-grid CreateLabel return anchor was not found.' }
    $grid = $grid.Replace($anchor, '            label.Rotation = rotation;' + "`r`n" + '            PaperAnnotationScale.SetAnnotative(label);' + "`r`n" + '            return label;')
}
Write-Text $gridPath $grid
Write-Host 'Survey Site Grid labels now use larger inside/corner clearance and annotative scaling.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# SURVEY CORRECTION: after user places the changes table, plot corrected points.
# ---------------------------------------------------------------------------
$reportPath = Join-Path $src 'GridReportPresenter.cs'
$report = Read-Text $reportPath
if (-not $report.Contains('August14SurveyFieldReviewCommands.PlotSurveyCorrectionRows')) {
    $anchor = '                editor.WriteMessage("\nCE Tools report table created.");'
    if (-not $report.Contains($anchor)) { throw 'Grid report successful-table anchor was not found.' }
    $insert = @'
                if (string.Equals(tableTitle, "CE TOOLS SURVEY CORRECTION CHANGES", StringComparison.OrdinalIgnoreCase))
                    August14SurveyFieldReviewCommands.PlotSurveyCorrectionRows(document, rows);

                editor.WriteMessage("\nCE Tools report table created.");
'@
    $report = $report.Replace($anchor, ($insert.TrimEnd() -replace "`n","`r`n"))
}
Write-Text $reportPath $report
Write-Host 'Survey correction change-table placement now auto-plots the corrected points at their corrected elevations.' -ForegroundColor Green

# Final verification markers.
$checks = @(
    @{ Path=$registerPath; Marker='"Coordinate System"' },
    @{ Path=$registerPath; Marker='{ "TOWN", data.Header("Town") }' },
    @{ Path=$centresPath; Marker='CE_PROJECTSETUPCHOICE2' },
    @{ Path=$centresPath; Marker='CE_NAMIBIALO' },
    @{ Path=$centresPath; Marker='CE_SURFACECOMPAREPRODUCTION' },
    @{ Path=$centresPath; Marker='CE_SURFACEREPORTPRODUCTION' },
    @{ Path=$centresPath; Marker='CE_GRIDSETTINGOUTMULTI' },
    @{ Path=$dynamicPath; Marker='RestoreCogoLabels(document, null)' },
    @{ Path=$gridPath; Marker='cornerClearance' },
    @{ Path=$reportPath; Marker='PlotSurveyCorrectionRows' }
)
foreach ($check in $checks) {
    $value = Read-Text $check.Path
    if (-not $value.Contains([string]$check.Marker)) { throw "Project/Survey final repair verification failed: $($check.Marker)" }
}

Write-Host 'August 14 Project + Survey field-comment integration passed.' -ForegroundColor Cyan
