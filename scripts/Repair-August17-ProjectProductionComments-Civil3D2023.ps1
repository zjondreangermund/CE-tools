[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 17 project-comment source missing: $path" }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceMethodBody([string]$text,[string]$signature,[string]$body) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Project-comment method signature not found: $signature" }
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

# Final one-page Project/Survey menus.
$centresPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$centres = ReadText $centresPath
$projectBody = @'
            Run("CE-PROJECT PRODUCTION",
                "Project setup, standards, styles, registers and coordinated delivery on one page.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Save / Update Company Standard", "CE_PROJECTCOMPANYSTANDARDSAVE", "Store reusable company defaults without project-specific name/number/client/town/CRS values.", "01 Project Production"),
                    A("CE-Project Setup - Last Saved / Company Standard / Blank", "CE_PROJECTSETUPCHOICE2", "Open Last Saved project information, Company Standard defaults or a Standard Blank project form.", "01 Project Production"),
                    A("CE-Project Information", "CE_PROJECTINFO", "Review the current drawing's linked project information.", "01 Project Production"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply discipline-specific style presets.", "01 Project Production"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select/import shared project Civil 3D styles.", "01 Project Production"),
                    A("CE-Standards", "CE_STANDARDS", "Select and record project standards.", "01 Project Production"),
                    A("CE-Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings and page setup environment.", "01 Project Production"),
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
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Survey style preset.", "01 SETTINGS"),
                    A("CE-Project Style Centre - Points / Surfaces", "CE_PROJECTSTYLES", "Select/import point, point-label and surface styles.", "01 SETTINGS"),
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
$centres = ReplaceMethodBody $centres 'public void ProjectProduction()' $projectBody
$centres = ReplaceMethodBody $centres 'public void SurveyProduction()' $surveyBody
WriteText $centresPath $centres

# Critical front-door repair: CE_PROJECTPRODUCTIONCENTRE and CE_SURVEYPRODUCTIONCENTRE
# are owned by August11ProductionCentreCommands. Route those public commands to the
# final structured one-page centres instead of opening their older hard-coded menus.
$frontPath = Required 'August11ProductionCentreCommands.cs'
$front = ReadText $frontPath
$projectFrontBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_PROJECTPRODUCTIONSTRUCTURED ", true, false, true);
'@
$surveyFrontBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_SURVEYPRODUCTIONSTRUCTURED ", true, false, true);
'@
$front = ReplaceMethodBody $front 'public void ProjectProduction()' $projectFrontBody
$front = ReplaceMethodBody $front 'public void SurveyProduction()' $surveyFrontBody
WriteText $frontPath $front

# Namibia popup always initializes from current shared Project CRS/Town.
$namibiaPath = Required 'NamibiaCoordinateRuntimeCommands.cs'
$namibia = ReadText $namibiaPath
if (-not $namibia.Contains('August17ProjectRuntime.PreferredLoCentralMeridian(document)')) {
    $pattern = '(?ms)int\s+inferred\s*;\s*NamibiaCoordinateRuntime\.TryInferLoZone\(out\s+inferred\)\s*;\s*if\s*\(inferred\s*<=\s*0\)\s*inferred\s*=\s*17\s*;'
    $rx = [regex]::new($pattern)
    if (-not $rx.IsMatch($namibia)) { throw 'Namibia LO initial-zone block could not be located.' }
    $namibia = $rx.Replace($namibia,'int inferred = August17ProjectRuntime.PreferredLoCentralMeridian(document);',1)
}
WriteText $namibiaPath $namibia

# Project Style Centre loads saved discipline preset before first window if only defaults are active.
$stylePath = Required 'ProjectStyleCenterCommands.cs'
$style = ReadText $stylePath
if (-not $style.Contains('CE_PROJECTSTYLES initial discipline preset activation')) {
    $old = @'
                ProjectStyleSelection existing = ReadSelection(document.Database);
                var window = new ProjectStyleCenterWindow(
'@ -replace "`n","`r`n"
    $new = @'
                ProjectStyleSelection existing = ReadSelection(document.Database);
                // CE_PROJECTSTYLES initial discipline preset activation
                bool onlyDefaults = existing == null || !existing.Exists || existing.Values.Count == 0 ||
                    existing.Values.Values.All(value => string.IsNullOrWhiteSpace(value) || value.StartsWith("<Use drawing default>", StringComparison.OrdinalIgnoreCase));
                if (onlyDefaults)
                {
                    string initialDiscipline = existing != null && !string.IsNullOrWhiteSpace(existing.Discipline)
                        ? existing.Discipline
                        : "Roads";
                    August11DisciplineStylePresetManager.ActivateForProduction(document.Database, initialDiscipline);
                    existing = ReadSelection(document.Database);
                }
                var window = new ProjectStyleCenterWindow(
'@ -replace "`n","`r`n"
    if (-not $style.Contains($old)) { throw 'Project Style Centre initial-selection anchor was not found.' }
    $style = $style.Replace($old,$new)
}
WriteText $stylePath $style

# Drawing/Client Books accept selected office title-block DWGs including named non-attributed blocks.
$titlePath = Required 'ProductionDrawingRegisterCommands.cs'
$title = ReadText $titlePath
$oldCondition = '                    if (score > bestScore && attributes > 0)'
$newCondition = @'
                    bool namedTitleBlock =
                        name.IndexOf(paperName ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("TB", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (score > bestScore && (attributes > 0 || namedTitleBlock))
'@ -replace "`n","`r`n"
if ($title.Contains($oldCondition)) {
    $title = $title.Replace($oldCondition,$newCondition.TrimEnd())
}
elseif (-not $title.Contains('bool namedTitleBlock =')) {
    throw 'Title-block candidate-selection condition was not found.'
}
$title = $title.Replace('" attributed block definition was found in the selected DWG."','" title-block definition was found in the selected DWG."')
WriteText $titlePath $title

$clientBookPath = Required 'ClientBookCommands.cs'
$clientBook = ReadText $clientBookPath
if (-not $clientBook.Contains('August17ProjectRuntime.TryInsertRegisteredClientBookTitleBlock')) {
    $oldClientTitleBlock = @'
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
'@ -replace "`n","`r`n"
    $newClientTitleBlock = @'
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
'@ -replace "`n","`r`n"
    if (-not $clientBook.Contains($oldClientTitleBlock)) {
        throw 'Client Book internal title-block call-site could not be located for registered-source wiring.'
    }
    $clientBook = $clientBook.Replace($oldClientTitleBlock,$newClientTitleBlock)
}
WriteText $clientBookPath $clientBook

# Final guards validate BOTH the final page and the public front-door commands.
$projectStart = $centres.IndexOf('public void ProjectProduction()', [StringComparison]::Ordinal)
$surveyStart = $centres.IndexOf('public void SurveyProduction()', [StringComparison]::Ordinal)
if ($projectStart -lt 0 -or $surveyStart -le $projectStart) { throw 'Project/Survey one-page method ranges could not be validated.' }
$projectSection = $centres.Substring($projectStart,$surveyStart-$projectStart)
if ($projectSection.Contains('CE_SURVEYLOCATION') -or $projectSection.Contains('CE_NAMIBIALO')) { throw 'Project Production still contains Survey Location or Namibia LO/WGS84.' }
if ($projectSection.IndexOf('CE_DISCIPLINESTYLEPRESETS') -gt $projectSection.IndexOf('CE_PROJECTSTYLES')) { throw 'Project Production style preset order is incorrect.' }
$surveySection = $centres.Substring($surveyStart,[Math]::Min(7000,$centres.Length-$surveyStart))
if (-not $surveySection.Contains('CE_SURVEYLOCATION') -or -not $surveySection.Contains('CE_NAMIBIALO')) { throw 'Survey Production no longer owns Survey Location / Namibia LO.' }
if ($surveySection.IndexOf('CE_DISCIPLINESTYLEPRESETS') -gt $surveySection.IndexOf('CE_PROJECTSTYLES')) { throw 'Survey Production style preset order is incorrect.' }

$front = ReadText $frontPath
if (-not $front.Contains('document.SendStringToExecute("CE_PROJECTPRODUCTIONSTRUCTURED ", true, false, true);')) { throw 'CE_PROJECTPRODUCTIONCENTRE is not routed to the final structured Project page.' }
if (-not $front.Contains('document.SendStringToExecute("CE_SURVEYPRODUCTIONSTRUCTURED ", true, false, true);')) { throw 'CE_SURVEYPRODUCTIONCENTRE is not routed to the final structured Survey page.' }
if (-not (ReadText $namibiaPath).Contains('August17ProjectRuntime.PreferredLoCentralMeridian(document)')) { throw 'Town/CRS -> LO central-meridian wiring is missing.' }
if (-not (ReadText $stylePath).Contains('CE_PROJECTSTYLES initial discipline preset activation')) { throw 'Project Style Centre initial preset activation is missing.' }
if (-not (ReadText $titlePath).Contains('bool namedTitleBlock =')) { throw 'Drawing/Client Book normal title-block support is missing.' }
$drawingBook = ReadText (Required 'ProductionReportCommands.cs')
if (-not $drawingBook.Contains('drawingRegister.Header("Title Block Source")') -or -not $drawingBook.Contains('ProductionTitleBlockManager.TryInsert(')) { throw 'Drawing Book is not wired to Title Block Source.' }
$clientBook = ReadText $clientBookPath
if (-not $clientBook.Contains('August17ProjectRuntime.TryInsertRegisteredClientBookTitleBlock')) { throw 'Client Book is not wired to Title Block Source.' }

Write-Host 'CE_PROJECTPRODUCTIONCENTRE now routes to the corrected one-page Project Production page.' -ForegroundColor Green
Write-Host 'CE_SURVEYPRODUCTIONCENTRE now routes to the corrected one-page Survey Production page.' -ForegroundColor Green
Write-Host 'Project Production excludes Survey Location and Namibia LO/WGS84; Survey Production owns both.' -ForegroundColor Green
Write-Host 'Discipline Style Presets appears above Project Style Centre.' -ForegroundColor Green
Write-Host 'Namibia LO central meridian initializes from current Project Town/CRS.' -ForegroundColor Green
Write-Host 'Drawing Book and Client Book use the registered Title Block Source including named non-attributed title blocks.' -ForegroundColor Green
