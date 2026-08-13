[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August11 completion-2 source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

$production = Need 'August11ProductionCentreCommands.cs'
$roadProductionV2 = Need 'August13RoadProductionCentres.cs'
$roadHub = Need 'August11RoadCompletionCommands.cs'
$roadExtra = Need 'August11RoadNamingCurveCommands.cs'
$roadLayout = Need 'RoadLayoutProductionCommands.cs'
$plugin = Need 'PluginEntry.cs'
$universal = Need 'UniversalDynamicRefreshCommands.cs'

# Correct aliases in the guided Production Centre to commands that exist in the
# permanent CE Tools command set.
$text = ReadText $production
$aliases = [ordered]@{
    'CE_BOQSTORMWATER' = 'CE_BOQSTORM'
    'CE_REPORTSTORMWATER' = 'CE_REPORTSTORM'
    'CE_PARKGRADINGTOOLS' = 'CE_PARKGRADETOOLS'
    'CE_PARKQTYTOOLS' = 'CE_STANDARDQTYTOOLS'
    'CE_STANDARDS' = 'CE_DESIGNSTANDARDS'
    'CE_HYDROLOGYREVIEW' = 'CE_HYDROLOGYTOOLS'
    'CE_FLOODQUICK' = 'CE_CATCHMENTQUICK'
}
$aliasCount = 0
foreach ($pair in $aliases.GetEnumerator()) {
    if ($text.Contains($pair.Key)) {
        $text = $text.Replace($pair.Key,$pair.Value)
        $aliasCount++
    }
}
if ($aliasCount -gt 0) {
    WriteText $production $text
    Write-Host "Corrected Production Centre command aliases: $aliasCount" -ForegroundColor Green
}
else { Write-Host 'Production Centre command aliases are already normalized.' -ForegroundColor DarkGreen }

# Add the last road field-test actions to the road-completion hub.
$text = ReadText $roadHub
$anchor = '                    new DisciplineWorkflowAction("Route annotation presentation", "CE_ROUTEANNOTATIONSTYLE", "Paper text sizes, masks, dimension metre suffix and arrow size.", "04 Annotation"),'
if (-not $text.Contains('"CE_ROUTEHORIZONTALCURVES"')) {
    if (-not $text.Contains($anchor)) { throw 'Road-completion route-annotation action marker was not found.' }
    $insert = @'
                    new DisciplineWorkflowAction("Multiple horizontal centreline curves", "CE_ROUTEHORIZONTALCURVES", "Apply tangent circular curves with a specified radius to multiple selected road/route polylines.", "02 Geometry"),
                    new DisciplineWorkflowAction("Synchronize road names through Civil objects", "CE_ROADNAMESYNC", "Propagate ROAD-n names into alignments, profiles, corridors, sections and assemblies and store the name link.", "02 Geometry"),
                    new DisciplineWorkflowAction("Utility offsets from erf / road-reserve geometry", "CE_UTILITYROUTEOFFSET", "Create Stormwater/Sewer/Water/Bulk-Water route strings at selected offsets from erf, reserve-edge or road-centre geometry.", "02 Geometry"),
'@
    $text = $text.Replace($anchor,$insert.TrimEnd("`r","`n") + "`r`n" + $anchor)
    Write-Host 'Added horizontal curves, road-name sync and utility-route offsets to Road Completion.' -ForegroundColor Green
}
# Ordered junction selection must go directly to the general vertex engine; it
# accepts polylines as well as arcs. Do not send it back through the legacy
# arc-only CE_ROADJUNCTIONSETTINGOUT command.
if ($text.Contains('document.SendStringToExecute("CE_ROADJUNCTIONSETTINGOUT ", true, false, true);')) {
    $text = $text.Replace('document.SendStringToExecute("CE_ROADJUNCTIONSETTINGOUT ", true, false, true);','document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);')
    Write-Host 'Routed ordered junction setting-out directly to polyline/arc-capable Vertex Setting-Out.' -ForegroundColor Green
}
WriteText $roadHub $text

# The legacy road-junction command itself was hard-coded to JUNCTION_ARC and an
# Arc cast. Keep its public command name, but delegate to the ordered all-Curve
# workflow so old ribbon buttons also support polylines.
$text = ReadText $roadLayout
$junctionPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_ROADJUNCTIONSETTINGOUT".*?\)\]\s*        public void JunctionSettingOut\(\)\s*        \{.*?\n        \}\s*(?=\n        \[CommandMethod\("CE_TOOLS", "CE_ROADLAYOUTREFRESH")'
$junctionReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONSETTINGOUT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionSettingOut()
        {
            new August11RoadCompletionCommands().JunctionSettingOutFourQuadrants();
        }
'@
if (-not $text.Contains('new August11RoadCompletionCommands().JunctionSettingOutFourQuadrants();')) {
    $regex = [regex]::new($junctionPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($text)) { throw 'Legacy arc-only CE_ROADJUNCTIONSETTINGOUT method could not be located.' }
    $text = $regex.Replace($text,$junctionReplacement.TrimEnd("`r","`n"),1)
    WriteText $roadLayout $text
    Write-Host 'Replaced legacy arc-only CE_ROADJUNCTIONSETTINGOUT with all-Curve ordered setting-out.' -ForegroundColor Green
}

# Guided Road/Sewer/Bulk-Water centres should expose these explicit field tools.
# The August 13 Road Production V2 owns the Road actions now, so do not require
# the removed legacy Road Continuity / Junction Finish anchor.
$text = ReadText $production
if (-not $text.Contains('Action("Horizontal Centreline Curves", "CE_ROUTEHORIZONTALCURVES"')) {
    $anchor = '                Action("Road Continuity / Junction Finish", "CE_ROADAUG11TOOLS", "Join reserve centrelines, outside offsets, junction trim boundaries and route annotation.", "02 PREPARE"),'
    if ($text.Contains($anchor)) {
        $insert = @'
                Action("Horizontal Centreline Curves", "CE_ROUTEHORIZONTALCURVES", "Apply specified tangent curve radii to multiple road/route centreline polylines.", "02 PREPARE"),
                Action("Synchronize Road Names", "CE_ROADNAMESYNC", "Make ROAD-n naming consistent across alignments, profiles, corridors, sections and assemblies.", "02 PREPARE"),
'@
        $text = $text.Replace($anchor,$anchor + "`r`n" + $insert.TrimEnd("`r","`n"))
    }
    else {
        $roadV2Text = ReadText $roadProductionV2
        if (-not $roadV2Text.Contains('"CE_ROUTEHORIZONTALCURVES"') -or -not $roadV2Text.Contains('"CE_ROADNAMESYNC"')) {
            throw 'Road Production V2 is present but its horizontal-curve / road-name-sync field actions are missing.'
        }
        Write-Host 'Road Production V2 detected; legacy Road prepare marker is no longer required.' -ForegroundColor DarkGreen
    }
}
$text = $text.Replace('Action("PREPARE - Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create bulk-water planning routes at selected offsets.", "02 PREPARE"),','Action("PREPARE - Utility Route from Erf / Road Reserve", "CE_UTILITYROUTEOFFSET", "Create bulk-water planning routes at selected offsets from erf, reserve-edge or road-centre geometry.", "02 PREPARE"),')
# Shared SW/Water utility workflow uses explicit configurable offset command.
$text = $text.Replace('Action("PREPARE - Utility Route Planner", "CE_UTILITYFROMROADRESERVE", "Create a preliminary route from road-reserve geometry.", "02 PREPARE"),','Action("PREPARE - Utility Route Planner", "CE_UTILITYROUTEOFFSET", "Create a preliminary route from erf, reserve-edge or road-centre geometry at a selected offset.", "02 PREPARE"),')
WriteText $production $text

# The old Corridors ribbon action labelled Baselines and Regions was only a
# report popup. Point it at the production command; retain the report as its own
# neighbouring button.
$text = ReadText $plugin
$old = '                    Cmd("Baselines and Regions", "CE_CORBASEUI ", "Show baseline and region details in a pop-up and optionally place a table."),'
$new = @'
                    Cmd("Create / Rebuild Baselines and Regions", "CE_ROADCORRIDORCOMPLETE ", "Create or rebuild corridor baselines, regions, targets and corridor surfaces as one production step."),
                    Cmd("Baseline / Region Report", "CE_CORBASEUI ", "Show baseline and region details in a pop-up and optionally place a table."),
'@
if ($text.Contains($old)) {
    $text = $text.Replace($old,$new.TrimEnd("`r","`n"))
    Write-Host 'Corrected Corridors Baselines/Regions action to production command.' -ForegroundColor Green
}
elseif ($text.Contains('Create / Rebuild Baselines and Regions')) { Write-Host 'Corridor Baselines/Regions production mapping is already corrected.' -ForegroundColor DarkGreen }
else { throw 'Corridor Baselines and Regions ribbon marker was not found.' }

# Add field-test network/route tools to Utilities without removing the existing
# expert commands.
$networkAnchor = '                        Cmd("Create Network from Object", "CE_NETWORKFROMPOLYLINES ", "Choose discipline and launch the Civil 3D native network-from-object workflow."),'
if (-not $text.Contains('Cmd("Batch Networks from Multiple Sources", "CE_NETWORKFROMPOLYLINESBATCH ')) {
    if (-not $text.Contains($networkAnchor)) { throw 'Utilities Create Network ribbon marker not found.' }
    $networkInsert = @'
                        Cmd("Batch Networks from Multiple Sources", "CE_NETWORKFROMPOLYLINESBATCH ", "Select multiple source polylines/feature lines once and queue native network creation with duplicate-source protection."),
                        Cmd("Utility Route Offset from Erf / Reserve", "CE_UTILITYROUTEOFFSET ", "Create SW/Sewer/Water/Bulk-Water routes at selected offsets from erf, road-reserve or road-centre geometry."),
                        Cmd("Close / Connect Selected Pipes", "CE_CLOSEPIPESONLY ", "Close/connect selected pipe and structure parts without invoking BOQ refresh."),
'@
    $text = $text.Replace($networkAnchor,$networkInsert.TrimEnd("`r","`n") + "`r`n" + $networkAnchor)
    Write-Host 'Added multi-source network, utility offset and close-pipes-only tools to Utilities.' -ForegroundColor Green
}
WriteText $plugin $text

# If any historical ribbon/workflow card still maps a Close Pipe action to BOQ
# refresh, correct that mapping throughout staged C# sources. Only actions whose
# visible title contains Close Pipe(s) are rewritten; BOQ refresh elsewhere is
# deliberately untouched.
$fixedCloseMappings = 0
Get-ChildItem -LiteralPath $src -Filter '*.cs' -File | ForEach-Object {
    $path = $_.FullName
    $content = ReadText $path
    $pattern = '(?is)("[^"\r\n]*Close\s+Pipe(?:s|\s+Ends)?[^"\r\n]*"\s*,\s*")CE_BOQREFRESH\s*'
    $updated = [regex]::Replace($content,$pattern,'$1CE_CLOSEPIPESONLY ')
    if ($updated -ne $content) {
        WriteText $path $updated
        $fixedCloseMappings++
    }
}
if ($fixedCloseMappings -gt 0) { Write-Host "Corrected Close Pipes -> BOQ refresh mappings in $fixedCloseMappings staged source file(s)." -ForegroundColor Green }
else { Write-Host 'No remaining Close Pipes -> BOQ refresh mapping was found.' -ForegroundColor DarkGreen }

# Dynamically keep names consistent after relevant road/name geometry changes.
$text = ReadText $universal
if (-not $text.Contains('August11RoadNamingCurveCommands.SyncRoadNames(document, false);')) {
    $anchor = '                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }`r`n                catch { result.Warnings++; }'
    if (-not $text.Contains($anchor)) {
        # tolerate LF-only staged source
        $anchor = "                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }`n                catch { result.Warnings++; }"
    }
    if (-not $text.Contains($anchor)) { throw 'Universal road-junction refresh marker not found for road-name sync.' }
    $newline = if ($anchor.Contains("`r`n")) { "`r`n" } else { "`n" }
    $insert = '                try { August11RoadNamingCurveCommands.SyncRoadNames(document, false); }' + $newline + '                catch { result.Warnings++; }'
    $text = $text.Replace($anchor,$anchor + $newline + $insert)
    WriteText $universal $text
    Write-Host 'Integrated dynamic ROAD-n name synchronization into universal refresh.' -ForegroundColor Green
}

Write-Host 'August 11 field completion pass 2 is ready for validation.' -ForegroundColor Cyan