[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 11 completion source missing: $path" }
    return $path
}

function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path, [string]$text) { [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false)) }

function Replace-LiteralOnce {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = ReadText $Path
    if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
    if (-not $text.Contains($Old)) { throw "Could not integrate '$Description'. Marker not found in $Path" }
    WriteText $Path ($text.Replace($Old,$New))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

function Replace-RegexRequired {
    param([string]$Path,[string]$Pattern,[string]$Replacement,[string]$Description,[string]$AlreadyMarker='')
    $text = ReadText $Path
    if (-not [string]::IsNullOrWhiteSpace($AlreadyMarker) -and $text.Contains($AlreadyMarker)) {
        Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen
        return
    }
    $regex = [regex]::new($Pattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($text)) { throw "Could not integrate '$Description'. Regex marker not found in $Path" }
    $newText = $regex.Replace($text,$Replacement,1)
    WriteText $Path $newText
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$plugin = Required 'PluginEntry.cs'
$network = Required 'FinalWorkflowGapCommands.cs'
$routePlanner = Required 'RoutePlannerExpansionCommands.cs'
$closure = Required 'August10CommentClosureCommands.cs'
$cogo = Required 'CogoPointProjectStyleCommands.cs'
$universal = Required 'UniversalDynamicRefreshCommands.cs'
$projectCoordination = Required 'ProjectCoordinationCommands.cs'
Required 'August11ProductionCentreCommands.cs' | Out-Null
Required 'August11NetworkBatchCommands.cs' | Out-Null
Required 'August11MidblockSewerProductionCommands.cs' | Out-Null
Required 'August11RoadCompletionCommands.cs' | Out-Null
Required 'August11SurveyRuntimeCommands.cs' | Out-Null
Required 'TableCellNavigationCommands.cs' | Out-Null

# Start/stop the immediate survey/table runtime with CE Tools.
$pluginText = ReadText $plugin
if (-not $pluginText.Contains('August11SurveyRuntimeManager.Initialize();')) {
    $marker = '            AugustGlobalShortcutManager.Initialize();'
    if (-not $pluginText.Contains($marker)) { $marker = '            UniversalDynamicRefreshManager.Initialize();' }
    if (-not $pluginText.Contains($marker)) { throw 'Could not find CE runtime initialization marker in PluginEntry.cs.' }
    $pluginText = $pluginText.Replace($marker, $marker + "`r`n            August11SurveyRuntimeManager.Initialize();")
    Write-Host 'Integrated August 11 immediate survey/table runtime manager.' -ForegroundColor Green
}
if (-not $pluginText.Contains('August11SurveyRuntimeManager.Terminate();')) {
    $marker = '            AugustGlobalShortcutManager.Terminate();'
    if (-not $pluginText.Contains($marker)) { $marker = '            UniversalDynamicRefreshManager.Terminate();' }
    if (-not $pluginText.Contains($marker)) { throw 'Could not find CE runtime termination marker in PluginEntry.cs.' }
    $pluginText = $pluginText.Replace($marker, '            August11SurveyRuntimeManager.Terminate();' + "`r`n" + $marker)
    Write-Host 'Integrated August 11 runtime termination.' -ForegroundColor Green
}

# Build the dedicated CE PRODUCTION tab whenever the main CE Tools tab is built.
if (-not $pluginText.Contains('ProductionWorkflowRibbonBuilder.EnsureCreated()')) {
    $oldRibbon = '_ribbonCreated = RibbonBuilder.EnsureCreated();'
    if (-not $pluginText.Contains($oldRibbon)) { throw 'Main CE ribbon creation marker was not found.' }
    $pluginText = $pluginText.Replace($oldRibbon, '_ribbonCreated = RibbonBuilder.EnsureCreated() && ProductionWorkflowRibbonBuilder.EnsureCreated();')
    Write-Host 'Integrated dedicated CE PRODUCTION ribbon tab.' -ForegroundColor Green
}

# Replace the old automatic full Workflow Centre popup with the simpler welcome
# screen. Full commands remain one click away in Engineering Intelligence Centre.
if (-not $pluginText.Contains('SendStringToExecute("CE_WELCOME "')) {
    $oldWelcome = '                    FloatingToolsCommands.OpenAtFirstStartup();'
    if ($pluginText.Contains($oldWelcome)) {
        $newWelcome = @'
                    Autodesk.AutoCAD.ApplicationServices.Document activeDocument = AcApplication.DocumentManager.MdiActiveDocument;
                    if (activeDocument != null)
                        activeDocument.SendStringToExecute("CE_WELCOME ", true, false, true);
'@
        $pluginText = $pluginText.Replace($oldWelcome,$newWelcome.TrimEnd("`r","`n"))
        Write-Host 'Integrated CE Tools welcome screen at first usable startup.' -ForegroundColor Green
    }
}
WriteText $plugin $pluginText

# Make the legacy Network From Polyline entry use the true multi-source batch.
$networkPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_NETWORKFROMPOLYLINES".*?\)\]\s*        public void NetworkFromObject\(\)\s*        \{.*?\n        \}\s*(?=\n        \[CommandMethod\("CE_TOOLS", "CE_NETWORKCONNECT")'
$networkReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_NETWORKFROMPOLYLINES", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void NetworkFromObject()
        {
            new August11NetworkBatchCommands().CreateNetworksBatch();
        }
'@
Replace-RegexRequired -Path $network -Pattern $networkPattern -Replacement $networkReplacement.TrimEnd("`r","`n") -Description 'route legacy Network From Polyline to multi-source batch' -AlreadyMarker 'new August11NetworkBatchCommands().CreateNetworksBatch();'

# Legacy connect entry now accepts the complete selected part set.
$connectPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_NETWORKCONNECT".*?\)\]\s*        public void ConnectParts\(\)\s*        \{.*?\n        \}\s*(?=\n        \[CommandMethod\("CE_TOOLS", "CE_NETWORKCREATEHUB")'
$connectReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_NETWORKCONNECT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ConnectParts()
        {
            new August11NetworkBatchCommands().ConnectSelectedParts();
        }
'@
Replace-RegexRequired -Path $network -Pattern $connectPattern -Replacement $connectReplacement.TrimEnd("`r","`n") -Description 'route legacy network connect to selected multi-part workflow' -AlreadyMarker 'new August11NetworkBatchCommands().ConnectSelectedParts();'

# Route Planner Option 2 must use the continuous production layout rather than
# the older one-short-line-per-block geometry.
$routeText = ReadText $routePlanner
if ($routeText.Contains('CE_MIDBLOCKSEWERLAYOUT')) {
    $routeText = $routeText.Replace('CE_MIDBLOCKSEWERLAYOUT','CE_MIDBLOCKSEWERPRODUCTION')
    WriteText $routePlanner $routeText
    Write-Host 'Integrated continuous Midblock Sewer Production into Route Planner.' -ForegroundColor Green
}
elseif ($routeText.Contains('CE_MIDBLOCKSEWERPRODUCTION')) { Write-Host 'Route Planner already uses continuous Midblock Sewer Production.' -ForegroundColor DarkGreen }
else { throw 'Route Planner Midblock command marker was not found.' }

# Project Information immediately inherits the town/coordinate system assigned
# through CE_SURVEYLOCATION.
$coordText = ReadText $projectCoordination
if (-not $coordText.Contains('August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);')) {
    $old = '                civilDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode = code;'
    if (-not $coordText.Contains($old)) { throw 'Survey location coordinate-system assignment marker not found.' }
    $coordText = $coordText.Replace($old,$old + "`r`n                August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);")
    WriteText $projectCoordination $coordText
    Write-Host 'Integrated Survey Location -> Project Information coordinate-system link.' -ForegroundColor Green
}

# Capture initial COGO label offsets before any CE overlap movement.
$cogoText = ReadText $cogo
if (-not $cogoText.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) {
    $old = '            CogoPointStyleResult result = ApplySelectedStyles(document, false);'
    if (-not $cogoText.Contains($old)) { throw 'COGO overlap style marker not found.' }
    $cogoText = $cogoText.Replace($old,'            August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);' + "`r`n" + $old)
    WriteText $cogo $cogoText
    Write-Host 'Integrated COGO initial-position capture before overlap resolution.' -ForegroundColor Green
}

# Generic smart-overlap capture and generic Restore command include COGO labels.
$closureText = ReadText $closure
if (-not $closureText.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) {
    $old = '                CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);'
    if (-not $closureText.Contains($old)) { throw 'Smart overlap COGO marker not found.' }
    $closureText = $closureText.Replace($old,'                August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);' + "`r`n" + $old)
    Write-Host 'Integrated initial COGO capture into Smart Annotation Overlap.' -ForegroundColor Green
}
if (-not $closureText.Contains('restored += August11SurveyRuntimeCommands.RestoreCogoLabels(document, selected);')) {
    $old = '            int restored = SmartAnnotationRuntime.Restore(document, selected);'
    if (-not $closureText.Contains($old)) { throw 'Annotation restore marker not found.' }
    $closureText = $closureText.Replace($old,$old + "`r`n            restored += August11SurveyRuntimeCommands.RestoreCogoLabels(document, selected);")
    Write-Host 'Integrated COGO initial-position restore into CE_ANNOTATIONRESTORE.' -ForegroundColor Green
}

# Legacy table-source zoom delegates to the robust cell/source chooser.
$tableZoomPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_TABLESOURCEZOOM".*?\)\]\s*        public void TableSourceZoom\(\)\s*        \{.*?\n        \}\s*(?=\n        \[CommandMethod\("CE_TOOLS", "CE_FLANNOTREFRESH")'
$tableZoomReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_TABLESOURCEZOOM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void TableSourceZoom()
        {
            new TableCellNavigationCommands().TableCellZoom();
        }
'@
$regex = [regex]::new($tableZoomPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $closureText.Contains('new TableCellNavigationCommands().TableCellZoom();')) {
    if (-not $regex.IsMatch($closureText)) { throw 'Legacy table source zoom method could not be located.' }
    $closureText = $regex.Replace($closureText,$tableZoomReplacement.TrimEnd("`r","`n"),1)
    Write-Host 'Integrated robust popup table-source navigation into legacy CE_TABLESOURCEZOOM.' -ForegroundColor Green
}
WriteText $closure $closureText

# The universal refresh also updates linked multi-surface coordinate tables.
# IMPORTANT: insert only after the complete SurveyCoordinateWorkflow try/catch
# pair. Inserting between try and catch creates CS1524 (Expected catch or finally).
$universalText = ReadText $universal
if (-not $universalText.Contains('August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(document);')) {
    $surveyTry = '                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }'
    $surveyCatch = '                catch { result.Warnings++; }'
    $oldCrLf = $surveyTry + "`r`n" + $surveyCatch
    $oldLf = $surveyTry + "`n" + $surveyCatch
    $lineBreak = "`r`n"
    $oldBlock = $oldCrLf
    if (-not $universalText.Contains($oldBlock)) {
        $oldBlock = $oldLf
        $lineBreak = "`n"
    }
    if (-not $universalText.Contains($oldBlock)) {
        throw 'Universal survey refresh try/catch pair was not found.'
    }
    $added = '                try { August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(document); }' + $lineBreak +
             '                catch { result.Warnings++; }'
    $newBlock = $oldBlock + $lineBreak + $added
    $universalText = $universalText.Replace($oldBlock,$newBlock)
    WriteText $universal $universalText
    Write-Host 'Integrated linked multi-surface coordinate tables after the complete universal survey refresh try/catch.' -ForegroundColor Green
}

Write-Host 'August 11 field completion integration is ready for Civil 3D 2023 validation.' -ForegroundColor Cyan