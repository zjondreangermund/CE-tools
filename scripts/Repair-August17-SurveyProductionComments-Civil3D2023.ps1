[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 17 Survey-comment source missing: $path" }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceMethodBody([string]$text,[string]$signature,[string]$body) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Survey-comment method signature not found: $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace not found: $signature" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { $close=$i; break } }
    }
    if ($close -lt 0) { throw "Closing brace not found: $signature" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close+1)
}

$commandsPath = Required 'August17SurveyProductionCommentCommands.cs'
$commandsText = ReadText $commandsPath
if (-not $commandsText.Contains('[CommandMethod("CE_TOOLS", "CE_SURVEYGRIDSETTINGOUT"')) { throw 'CE_SURVEYGRIDSETTINGOUT command is missing.' }
if (-not $commandsText.Contains('[CommandMethod(') -or -not $commandsText.Contains('"CE_SITEGRIDMULTI"')) { throw 'CE_SITEGRIDMULTI command is missing.' }
if (-not $commandsText.Contains('CE-Grid Setting-Out - Multiple / Selected Polylines')) { throw 'Grid Setting-Out selected/multiple option is missing.' }
if (-not $commandsText.Contains('CE-Site Grid - Selected / Multiple Polylines / Window Selection')) { throw 'Site Grid selected/multiple/window option is missing.' }

$backgroundPath = Required 'BackgroundPreparationCommands.cs'
$backgroundText = ReadText $backgroundPath
if (-not $backgroundText.Contains('[CommandMethod("CE_TOOLS", "CE_BACKGROUNDPREPTOOLS"')) { throw 'CE_BACKGROUNDPREPTOOLS command is missing.' }
if (-not $backgroundText.Contains('"CE Tools - Background Tools"')) { throw 'CE-Background Tools popup is missing.' }

$centresPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath

$surveyBody = @'
            Activate("Survey");
            Run("CE-SURVEY PRODUCTION",
                "Choose Survey Settings, Survey Surface Production or Survey Setting-Out / Delivery Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 SETTINGS"),
                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 SETTINGS"),
                    A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS", "Prepare imported/background DWGs: burst blocks, colour 250, audit/overkill/purge, freeze solid hatches/dimensions and correct scale to metres.", "02 PREPARE"),
                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Import/export Civil survey data.", "02 PREPARE"),
                    A("CE-Surface Production", "CE_SURVEYSURFACEPRODUCTION", "Create a surface from file/objects, choose style and create point-extent border.", "02 PREPARE"),
                    A("CE-Surface Tools", "CE_SURFTOOLS", "Review/create existing-ground surfaces.", "03 CREATE"),
                    A("CE-Surface Report - Popup / Linked Table / Excel", "CE_SURFACEREPORTPRODUCTION", "Review a Civil 3D surface in a popup, place a linked annotative table and/or export Excel.", "03 CREATE"),
                    A("CE-Surface Correction / Review", "CE_SURFCTOOLS", "Audit and create reversible corrected surfaces.", "04 DESIGN"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader setting-out.", "05 SETTING-OUT"),
                    A("CE-Grid Setting-Out", "CE_SURVEYGRIDSETTINGOUT", "Choose grid setting-out for selected/multiple polylines or Site Grid from selected/multiple/window-selected polylines.", "05 SETTING-OUT"),
                    A("CE-Base / Comparison Surface", "CE_SURFACECOMPAREPRODUCTION", "Popup base/comparison selection with linked points, tables and Excel output.", "05 SETTING-OUT"),
                    A("CE-Refresh Surface Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked comparison tables.", "06 DELIVER"),
                    A("CE-Survey Linked / Annotative Refresh", "CE_SURVEYREFRESHSAFE", "Refresh linked points/tables and sync annotation scale.", "06 DELIVER"),
                    A("CE-Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review survey corrections and comparison output.", "06 DELIVER"),
                    A("CE-Export Table to CSV / Excel", "CE_TABLEEXPORTCSV", "Export selected CE table to Excel-compatible CSV.", "06 DELIVER"),
                    A("CE-Correct Table Column Spacing", "CE_TABLECOLUMNSPACE", "Apply consistent table column spacing.", "06 DELIVER")
                });
'@

$deliveryBody = @'
            Run("CE-SURVEY SETTING-OUT / DELIVERY PRODUCTION",
                "Create linked setting-out, choose Grid or Site Grid workflows, compare surfaces and deliver tables/exports.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader setting-out.", "05 COMPLETE"),
                    A("CE-Grid Setting-Out", "CE_SURVEYGRIDSETTINGOUT", "Choose grid setting-out for selected/multiple polylines or Site Grid from selected/multiple/window-selected polylines.", "05 COMPLETE"),
                    A("CE-Base / Comparison Surface Table", "CE_SURFACECOMPARETABLE", "Create linked base/comparison surface tables.", "05 COMPLETE"),
                    A("CE-Refresh Surface Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked comparison tables.", "05 COMPLETE"),
                    A("CE-Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review survey corrections and comparison output.", "06 DELIVER"),
                    A("CE-Export Table to CSV / Excel", "CE_TABLEEXPORTCSV", "Export selected CE table to Excel-compatible CSV.", "06 DELIVER"),
                    A("CE-Correct Table Column Spacing", "CE_TABLECOLUMNSPACE", "Apply consistent table column spacing.", "06 DELIVER")
                });
'@

$centres = ReplaceMethodBody $centres 'public void SurveyProduction()' $surveyBody
$centres = ReplaceMethodBody $centres 'public void SurveyDelivery()' $deliveryBody
WriteText $centresPath $centres

# Validate only the final one-page SurveyProduction method. Separate Survey Settings,
# Surface and Delivery sub-centres remain in this class for compatibility and may
# legitimately contain style commands that are not on the requested one-page menu.
$centres = ReadText $centresPath
$surveyStart = $centres.IndexOf('public void SurveyProduction()', [StringComparison]::Ordinal)
$surveySettingsStart = $centres.IndexOf('public void SurveySettings()', $surveyStart, [StringComparison]::Ordinal)
if ($surveyStart -lt 0 -or $surveySettingsStart -le $surveyStart) { throw 'Survey Production method range could not be validated.' }
$surveySection = $centres.Substring($surveyStart,$surveySettingsStart-$surveyStart)

$expected = @(
    'CE_SURVEYLOCATION',
    'CE_NAMIBIALO',
    'CE_BACKGROUNDPREPTOOLS',
    'CE_LANDXMLTOOLS',
    'CE_SURVEYSURFACEPRODUCTION',
    'CE_SURFTOOLS',
    'CE_SURFACEREPORTPRODUCTION',
    'CE_SURFCTOOLS',
    'CE_VERTEXSETTINGOUT',
    'CE_SURVEYGRIDSETTINGOUT',
    'CE_SURFACECOMPAREPRODUCTION',
    'CE_COORDMULTISURFACEREFRESH',
    'CE_SURVEYREFRESHSAFE',
    'CE_SURVEYCOMPARETOOLS',
    'CE_TABLEEXPORTCSV',
    'CE_TABLECOLUMNSPACE'
)
$cursor = -1
foreach ($command in $expected) {
    $next = $surveySection.IndexOf($command, [StringComparison]::Ordinal)
    if ($next -lt 0) { throw "Survey Production is missing required command: $command" }
    if ($next -le $cursor) { throw "Survey Production order is incorrect at command: $command" }
    $cursor = $next
}

if ($surveySection.Contains('CE_BACKGROUNDTOOLS')) { throw 'Survey Production still exposes the old Background/XREF manager directly.' }
if ($surveySection.Contains('CE_DISCIPLINESTYLEPRESETS')) { throw 'Survey Production still contains Discipline Style Presets; the requested one-page order does not.' }
if ($surveySection.Contains('CE_PROJECTSTYLES')) { throw 'Survey Production still contains Project Style Centre; the requested one-page order does not.' }
if ($surveySection.Contains('CE_GRIDSETTINGOUTMULTI')) { throw 'Survey Production still bypasses the new Grid Setting-Out submenu.' }

Write-Host 'Survey Production now follows the exact 17-08-2026 requested order.' -ForegroundColor Green
Write-Host 'Survey Production PREPARE opens CE-Background Tools instead of the direct Background/XREF manager.' -ForegroundColor Green
Write-Host 'Grid Setting-Out now opens the two-choice Survey grid submenu.' -ForegroundColor Green
Write-Host 'Site Grid supports preselection, individual/multiple selection and Window/Crossing selection.' -ForegroundColor Green
