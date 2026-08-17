[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 17 required source was not found: $path" }
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function Write-Text([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), $utf8)
}
function Replace-MethodBody {
    param([string]$Text,[string]$Signature,[string]$Body)
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 17 method signature was not found: $Signature" }
    $open = $Text.IndexOf('{', $start)
    if ($open -lt 0) { throw "August 17 opening brace was not found for: $Signature" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '{') { $depth++ }
        elseif ($Text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "August 17 closing brace was not found for: $Signature" }
    return $Text.Substring(0,$open) + "{`r`n" + $Body.TrimEnd() + "`r`n        }" + $Text.Substring($close+1)
}
function Replace-FirstRegex {
    param([string]$Text,[string]$Pattern,[string]$Replacement,[string]$Failure)
    $rx = New-Object System.Text.RegularExpressions.Regex($Pattern)
    if (-not $rx.IsMatch($Text)) { throw $Failure }
    return $rx.Replace($Text, $Replacement, 1)
}

# ---------------------------------------------------------------------------
# PROJECT + SURVEY: one-page production centres.
# ---------------------------------------------------------------------------
$centresPath = Join-Path $src 'August14StructuredDisciplineProductionCentres.cs'
$centres = Read-Text $centresPath
$projectBody = @'
            Run("CE-PROJECT PRODUCTION",
                "Project setup, coordinate system, standards, styles, registers and coordinated delivery on one page.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 Project Production"),
                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 Project Production"),
                    A("CE-Save / Update Company Standard", "CE_PROJECTCOMPANYSTANDARDSAVE", "Store reusable company defaults without project-specific name/number/client/town/CRS values.", "01 Project Production"),
                    A("CE-Project Setup - Last Saved / Company Standard / Blank", "CE_PROJECTSETUPCHOICE2", "Open Last Saved project information, Company Standard defaults or a Standard Blank project form.", "01 Project Production"),
                    A("CE-Project Information", "CE_PROJECTINFO", "Review the current drawing's linked project information.", "01 Project Production"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select/import shared project Civil 3D styles.", "01 Project Production"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply discipline-specific style presets.", "01 Project Production"),
                    A("CE-Standards", "CE_STANDARDS", "Select and record project standards.", "01 Project Production"),
                    A("CE-Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings, project location and page setup environment.", "01 Project Production"),
                    A("CE-Drawing Register", "CE_DRAWINGREGISTERPROJECTSYNC", "Synchronize project information, drawing numbers, titles, revisions and issue information.", "01 Project Production"),
                    A("CE-Drawing / Client Books", "CE_BOOKTOOLS", "Create drawing books, client books and indexes using the registered title-block source.", "01 Project Production")
                });
'@
$surveyBody = @'
            Activate("Survey");
            Run("CE-SURVEY PRODUCTION",
                "Survey settings, surface preparation/design, setting-out and delivery on one page.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 SETTINGS"),
                    A("CE-Namibia LO / WGS84 Survey Conversion", "CE_NAMIBIALO", "Convert picked/drawing WGS84 and Namibia Schwarzeck LO survey coordinates.", "01 SETTINGS"),
                    A("CE-Project Style Centre - Points / Surfaces", "CE_PROJECTSTYLES", "Select/import point, point-label and surface styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Survey style preset.", "01 SETTINGS"),
                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Import/export Civil survey data.", "02 PREPARE"),
                    A("CE-Surface Production", "CE_SURVEYSURFACEPRODUCTION", "Create a surface from file/objects, choose style and create point-extent border.", "02 PREPARE"),
                    A("CE-Surface Tools", "CE_SURFTOOLS", "Review/create existing-ground surfaces.", "03 CREATE"),
                    A("CE-Surface Report - Popup / Linked Table / Excel", "CE_SURFACEREPORTPRODUCTION", "Review a Civil 3D surface in a popup, place a linked annotative table and/or export Excel.", "03 CREATE"),
                    A("CE-Surface Correction / Review", "CE_SURFCTOOLS", "Audit and create reversible corrected surfaces.", "04 DESIGN"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader setting-out.", "05 SETTING-OUT"),
                    A("CE-Grid Setting-Out - Multiple Polylines", "CE_GRIDSETTINGOUTMULTI", "Linked grid/vertex setting-out across multiple polylines with continuous numbering.", "05 SETTING-OUT"),
                    A("CE-Base / Comparison Surface", "CE_SURFACECOMPAREPRODUCTION", "Popup base/comparison selection with linked points, tables and Excel output.", "05 SETTING-OUT"),
                    A("CE-Refresh Surface Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked comparison tables.", "06 DELIVER"),
                    A("CE-Survey Linked / Annotative Refresh", "CE_SURVEYREFRESHSAFE", "Refresh linked points/tables, restore COGO label offsets and sync annotation scale.", "06 DELIVER"),
                    A("CE-Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review survey corrections and comparison output.", "06 DELIVER"),
                    A("CE-Export Table to CSV / Excel", "CE_TABLEEXPORTCSV", "Export selected CE table to Excel-compatible CSV.", "06 DELIVER"),
                    A("CE-Correct Table Column Spacing", "CE_TABLECOLUMNSPACE", "Apply consistent table column spacing.", "06 DELIVER")
                });
'@
$centres = Replace-MethodBody -Text $centres -Signature 'public void ProjectProduction()' -Body $projectBody
$centres = Replace-MethodBody -Text $centres -Signature 'public void SurveyProduction()' -Body $surveyBody

# Platform: insert relative to the live CE_FLCREATE owner, independent of label text.
if (-not $centres.Contains('"CE_PLATFORMFEATURELINESLOPE"')) {
    $platformInsert = '                    A("CE-Platform Feature Lines at Fixed / Minimum Slope", "CE_PLATFORMFEATURELINESLOPE", "Create individual feature lines from multiple platform polylines at a fixed or minimum slope toward/away from a selected reference feature line.", "02 PREPARE"),'
    $pattern = '(?m)^(\s*A\([^\r\n]*"CE_FLCREATE"[^\r\n]*\),)\r?$'
    $centres = Replace-FirstRegex -Text $centres -Pattern $pattern -Replacement ('$1' + "`r`n" + $platformInsert) -Failure 'Platform live CE_FLCREATE action could not be located structurally.'
}
Write-Text $centresPath $centres

# Road: insert relative to the live CE_ROADCORRIDORFULL owner. In current main
# this is permanent source, so this branch normally becomes a no-op.
$roadPath = Join-Path $src 'August13RoadProductionCentres.cs'
$road = Read-Text $roadPath
if (-not $road.Contains('"CE_CORRIDORFEATURELINES"')) {
    $roadInsert = '    A("CE-Corridor Feature Lines", "CE_CORRIDORFEATURELINES", "Create individual feature lines from selected corridor centreline/edge/kerb/sidewalk/shoulder/toe codes or all corridor feature lines.", "03 Corridors"),'
    $pattern = '(?m)^(\s*A\([^\r\n]*"CE_ROADCORRIDORFULL"[^\r\n]*\),)\r?$'
    $road = Replace-FirstRegex -Text $road -Pattern $pattern -Replacement ('$1' + "`r`n" + $roadInsert) -Failure 'Road live CE_ROADCORRIDORFULL action could not be located structurally.'
}
Write-Text $roadPath $road

# ---------------------------------------------------------------------------
# PROJECT TOWN -> NAMIBIA LO: connect the live CE_NAMIBIALO command directly
# to the Project Town/Coordinate-System source. Match the method structurally,
# not one historical whitespace layout.
# ---------------------------------------------------------------------------
$namibiaPath = Join-Path $src 'NamibiaCoordinateRuntimeCommands.cs'
$namibia = Read-Text $namibiaPath
if (-not $namibia.Contains('August17ProjectRuntime.PreferredLoCentralMeridian(document)')) {
    $pattern = '(?ms)(Document\s+document\s*=\s*AcApplication\.DocumentManager\.MdiActiveDocument;\s*if\s*\(document\s*==\s*null\)\s*return;\s*)int\s+inferred\s*;\s*NamibiaCoordinateRuntime\.TryInferLoZone\(out\s+inferred\)\s*;\s*if\s*\(inferred\s*<=\s*0\)\s*inferred\s*=\s*17\s*;'
    $replacement = '$1int inferred = August17ProjectRuntime.PreferredLoCentralMeridian(document);'
    $namibia = Replace-FirstRegex -Text $namibia -Pattern $pattern -Replacement $replacement -Failure 'Namibia LO command owner was found, but its initial-zone block could not be upgraded structurally.'
}
Write-Text $namibiaPath $namibia

# ---------------------------------------------------------------------------
# CLIENT BOOK: use Drawing Register Title Block Source first. Match the internal
# BuildTitleBlock fallback structurally so formatting changes do not stop staging.
# ---------------------------------------------------------------------------
$bookPath = Join-Path $src 'ClientBookCommands.cs'
$book = Read-Text $bookPath
if (-not $book.Contains('August17ProjectRuntime.TryInsertRegisteredClientBookTitleBlock')) {
    $newBlock = @'
                string registeredTitleBlockDiagnostic;
                bool usedRegisteredTitleBlock = August17ProjectRuntime.TryInsertRegisteredClientBookTitleBlock(
                    database,
                    transaction,
                    paperSpace,
                    generated,
                    page.Paper,
                    page.LayoutName,
                    page.PageNumber,
                    page.Title,
                    stage,
                    revision,
                    out registeredTitleBlockDiagnostic);
                if (!usedRegisteredTitleBlock)
                {
                    Table titleBlock = BuildTitleBlock(
                        database,
                        new Point3d(margin, margin + titleBlockHeight, 0.0),
                        page,
                        stage,
                        revision,
                        snapshot,
                        bodyText);
                    AddGenerated(transaction, paperSpace, titleBlock, generated);
                    titleBlock.GenerateLayout();
                }
'@
    $pattern = '(?ms)^\s*Table\s+titleBlock\s*=\s*BuildTitleBlock\(\s*database,\s*new\s+Point3d\(margin,\s*margin\s*\+\s*titleBlockHeight,\s*0\.0\),\s*page,\s*stage,\s*revision,\s*snapshot,\s*bodyText\s*\);\s*AddGenerated\(transaction,\s*paperSpace,\s*titleBlock,\s*generated\);\s*titleBlock\.GenerateLayout\(\);'
    $book = Replace-FirstRegex -Text $book -Pattern $pattern -Replacement ($newBlock -replace "`n","`r`n") -Failure 'Client Book fallback title-block block could not be located structurally.'
}
Write-Text $bookPath $book

# Final wiring guard validates the final live architecture, not historical text.
$featurePath = Join-Path $src 'August17ProductionFeatureLineCommands.cs'
$feature = Read-Text $featurePath
foreach ($marker in @(
    '"CE_CORRIDORFEATURELINES"',
    'ExportAsGradingFeatureLine(ObjectId.Null, dynamic)',
    '"CE_PLATFORMFEATURELINESLOPE"',
    'CivilFeatureLine.Create(string.Empty, temporaryId)',
    'PreferredLoCentralMeridian',
    'TryInsertRegisteredClientBookTitleBlock')) {
    if (-not $feature.Contains($marker)) { throw "August 17 source marker missing: $marker" }
}
$centres = Read-Text $centresPath
foreach ($marker in @('"CE_NAMIBIALO"','"CE_PROJECTCOMPANYSTANDARDSAVE"','"CE_PROJECTSETUPCHOICE2"','"CE_DRAWINGREGISTERPROJECTSYNC"','"CE_PLATFORMFEATURELINESLOPE"')) {
    if (-not $centres.Contains($marker)) { throw "August 17 Project/Survey/Platform menu marker missing: $marker" }
}
$road = Read-Text $roadPath
if (-not $road.Contains('"CE_CORRIDORFEATURELINES"')) { throw 'August 17 Road menu does not expose corridor feature-line extraction.' }
$namibia = Read-Text $namibiaPath
if (-not $namibia.Contains('August17ProjectRuntime.PreferredLoCentralMeridian(document)')) { throw 'August 17 Namibia LO popup is not linked to Project Town/CRS.' }
$book = Read-Text $bookPath
if (-not $book.Contains('August17ProjectRuntime.TryInsertRegisteredClientBookTitleBlock')) { throw 'August 17 Client Book does not use the registered Title Block Source.' }

Write-Host 'August 17 Project and Survey Production are one-page centres.' -ForegroundColor Green
Write-Host 'Project Production uses Namibia LO/WGS84; Project Town/CRS drives the LO central meridian.' -ForegroundColor Green
Write-Host 'Client Books use the Drawing Register Title Block Source with the CE internal title block only as fallback.' -ForegroundColor Green
Write-Host 'Road Production exposes selective Corridor Feature Lines; Platform Production exposes fixed/minimum slope feature lines.' -ForegroundColor Green
