[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Once {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
    if (-not $text.Contains($Old)) { throw "Could not integrate '$Description'. Marker not found in $Path" }
    [System.IO.File]::WriteAllText($Path, $text.Replace($Old,$New), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

function Replace-AllLiteral {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) {
        if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
        throw "Could not integrate '$Description'. Token not found in $Path"
    }
    [System.IO.File]::WriteAllText($Path, $text.Replace($Old,$New), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$plugin = Join-Path $src 'PluginEntry.cs'
$roadProduction = Join-Path $src 'RoadProductionCommentCommands.cs'
$roadLayout = Join-Path $src 'RoadLayoutProductionCommands.cs'
$assembly = Join-Path $src 'CeAssemblyCommands.cs'
$surface = Join-Path $src 'SurfaceSpikeHoleRepairCommands.cs'
$automatic = Join-Path $src 'AugustAutomaticRefreshManager.cs'
$behavior = Join-Path $src 'AugustBehaviorCompletionCommands.cs'
$defaults = Join-Path $src 'AugustRoadProfileDefaults.cs'

foreach ($required in @($plugin,$roadProduction,$roadLayout,$assembly,$surface,$automatic,$behavior,$defaults)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "August behavior source missing: $required" }
}

# Queue one universal refresh after every completed CE command. The universal
# manager remains the only writer and executes after Civil 3D returns to idle.
$oldAutoInit = @'
            AugustGlobalShortcutManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
$newAutoInit = @'
            AugustGlobalShortcutManager.Initialize();
            AugustAutomaticRefreshManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
Replace-Once -Path $plugin -Old $oldAutoInit -New $newAutoInit -Description 'queue automatic linked refresh after CE commands'

$oldAutoTerminate = @'
            AugustGlobalShortcutManager.Terminate();
            FloatingToolsCommands.Terminate();
'@
$newAutoTerminate = @'
            AugustAutomaticRefreshManager.Terminate();
            AugustGlobalShortcutManager.Terminate();
            FloatingToolsCommands.Terminate();
'@
Replace-Once -Path $plugin -Old $oldAutoTerminate -New $newAutoTerminate -Description 'terminate final automatic refresh watcher'

# Requested first/default road profile-view band set.
$oldBand = 'RoadValue(road, selection, "Profile View Band Set Style")'
$newBand = 'AugustRoadProfileDefaults.PreferredBandSet(RoadValue(road, selection, "Profile View Band Set Style"))'
Replace-AllLiteral -Path $roadProduction -Old $oldBand -New $newBand -Description 'default road profile band set to Road-Single-Band Set 1-Full Grid'

# Empty Civil assemblies can be difficult to see before subassemblies are added.
# Add a linked visible marker at the exact requested assembly insertion point.
$oldAssemblyMarker = @'
            }
            document.Editor.Regen();
            if (string.Equals(model.Text("OpenPalette"), "Yes", StringComparison.OrdinalIgnoreCase))
'@
$newAssemblyMarker = @'
            }
            AugustAssemblyVisibility.EnsureMarker(document, assemblyId, point.Value);
            document.Editor.Regen();
            if (string.Equals(model.Text("OpenPalette"), "Yes", StringComparison.OrdinalIgnoreCase))
'@
Replace-Once -Path $assembly -Old $oldAssemblyMarker -New $newAssemblyMarker -Description 'show visible marker for newly inserted road assembly'

# Let the bulk junction command itself offer Arc or Polyline output. Cross
# junctions already create all four quadrants inside one intersection loop before
# moving to the next detected junction; the conversion runs only after the group
# is fully generated so clockwise grouping/setting-out is retained.
$oldJunctionSettings = @'
            model.AddPositiveDouble("Radius", "02 Geometry", "Bellmouth radius", 10.0, "Return radius.");
            model.AddPositiveDouble("HalfWidth", "02 Geometry", "Default road half-width", 3.7, "Used when a centreline has no generated edge offset to infer its half-width.");
'@
$newJunctionSettings = @'
            model.AddPositiveDouble("Radius", "02 Geometry", "Bellmouth radius", 10.0, "Return radius.");
            model.AddChoice("Geometry", "02 Geometry", "Return geometry", "Arcs", "Keep native Arc returns or convert the completed T/cross-junction return groups to lightweight-polyline arc segments.", new[] { "Arcs", "Polylines" });
            model.AddPositiveDouble("HalfWidth", "02 Geometry", "Default road half-width", 3.7, "Used when a centreline has no generated edge offset to infer its half-width.");
'@
Replace-Once -Path $roadLayout -Old $oldJunctionSettings -New $newJunctionSettings -Description 'add Arc/Polyline option to bulk T/cross junctions'

$oldJunctionEnd = @'
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADJUNCTIONBULK complete. T-junctions={0}; cross-junctions={1}; return arcs={2}.", tCount, crossCount, arcs);
'@
$newJunctionEnd = @'
            if (string.Equals(model.Text("Geometry"), "Polylines", StringComparison.OrdinalIgnoreCase))
                AugustJunctionReturnRuntime.ConvertGenerated(document, null);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADJUNCTIONBULK complete. T-junctions={0}; cross-junctions={1}; return objects={2}; geometry={3}.", tCount, crossCount, arcs, model.Text("Geometry"));
'@
Replace-Once -Path $roadLayout -Old $oldJunctionEnd -New $newJunctionEnd -Description 'convert completed junction groups to requested polyline returns'

# Add an internal-holes-only surface repair mode. This creates no spike/low
# replacement candidates; only detected internal open-edge holes receive TIN fill
# points in the separate repair surface.
$oldSurfaceStart = @'
            settings.AddPositiveDouble(
                "SpikeTolerance",
'@
$newSurfaceStart = @'
            settings.AddChoice(
                "RepairMode",
                "Repair Criteria",
                "Repair mode",
                "Spikes / lows and internal holes",
                "Choose the complete repair, only internal holes, or only spikes/lows. Internal holes only adds TIN fill only to detected hole areas.",
                new[] { "Spikes / lows and internal holes", "Internal holes only", "Spikes / lows only" });
            settings.AddPositiveDouble(
                "SpikeTolerance",
'@
Replace-Once -Path $surface -Old $oldSurfaceStart -New $newSurfaceStart -Description 'add internal-holes-only surface repair option'

$oldFillHoles = @'
            bool fillHoles = !string.Equals(
                settings.Text("HoleHandling"),
                "Keep internal holes",
                StringComparison.OrdinalIgnoreCase);
'@
$newFillHoles = @'
            bool repairSpikes = !string.Equals(settings.Text("RepairMode"), "Internal holes only", StringComparison.OrdinalIgnoreCase);
            bool fillHoles = !string.Equals(settings.Text("RepairMode"), "Spikes / lows only", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(
                                 settings.Text("HoleHandling"),
                                 "Keep internal holes",
                                 StringComparison.OrdinalIgnoreCase);
'@
Replace-Once -Path $surface -Old $oldFillHoles -New $newFillHoles -Description 'separate spike and hole repair controls'

$oldFirstPlan = @'
                    sourceId,
                    spikeTolerance,
                    neighbourRadius,
'@
$newFirstPlan = @'
                    sourceId,
                    repairSpikes ? spikeTolerance : double.MaxValue / 4.0,
                    neighbourRadius,
'@
Replace-Once -Path $surface -Old $oldFirstPlan -New $newFirstPlan -Description 'disable spike replacements in internal-holes-only mode'

$oldRetryPlan = @'
                        sourceId,
                        spikeTolerance,
                        neighbourRadius * 4.0,
'@
$newRetryPlan = @'
                        sourceId,
                        repairSpikes ? spikeTolerance : double.MaxValue / 4.0,
                        neighbourRadius * 4.0,
'@
Replace-Once -Path $surface -Old $oldRetryPlan -New $newRetryPlan -Description 'keep adaptive retry hole-only when requested'

# Direct ribbon access to the final behaviour commands.
$oldAssemblyRibbon = @'
                        Cmd("Create CE Road Assembly", "CE_ASSEMBLYCREATE ", "Create a named Civil 3D road assembly at a selected insertion point."),
                        Cmd("Assembly Register", "CE_ASSEMBLYREPORT ", "Review every assembly, style and subassembly count."),
'@
$newAssemblyRibbon = @'
                        Cmd("Create CE Road Assembly", "CE_ASSEMBLYCREATE ", "Create a named Civil 3D road assembly at a selected insertion point and show its visible linked marker."),
                        Cmd("Refresh Assembly Markers", "CE_ASSEMBLYMARKERS ", "Create visible location markers for existing Civil 3D assemblies that have no obvious model-space graphics."),
                        Cmd("Assembly Register", "CE_ASSEMBLYREPORT ", "Review every assembly, style and subassembly count."),
'@
Replace-Once -Path $plugin -Old $oldAssemblyRibbon -New $newAssemblyRibbon -Description 'add assembly visibility command to ribbon'

$oldJunctionRibbon = @'
                        Cmd("Preliminary Road Layout Production", "CE_ROADLAYOUTTOOLS ", "Create road-reserve centrelines, road edges, shoulders, bulk T/cross junctions, road names, dimensions and junction setting-out."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
$newJunctionRibbon = @'
                        Cmd("Preliminary Road Layout Production", "CE_ROADLAYOUTTOOLS ", "Create road-reserve centrelines, road edges, shoulders, bulk T/cross junctions, road names, dimensions and junction setting-out."),
                        Cmd("Junction Return Arc / Polyline", "CE_JUNCTIONRETURNTYPE ", "Convert all/selected generated junction return arcs to linked polyline arc segments."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
Replace-Once -Path $plugin -Old $oldJunctionRibbon -New $newJunctionRibbon -Description 'add junction return geometry command to Road Production ribbon'

$oldStormSequence = @'
                        Cmd("Sequence Main and Branches", "CE_SWSEQ ", "Sequence the complete stormwater network."),
                        Cmd("Create / Refresh Alignments", "CE_SWALIGN ", "Create linked stormwater alignments."),
'@
$newStormSequence = @'
                        Cmd("Sequence + Auto Alignments", "CE_SWSEQPRODUCTION ", "Sequence stormwater and automatically continue into alignment production; optionally queue profiles."),
                        Cmd("Sequence Main and Branches", "CE_SWSEQ ", "Sequence only the complete stormwater network."),
                        Cmd("Create / Refresh Alignments", "CE_SWALIGN ", "Create linked stormwater alignments."),
'@
Replace-Once -Path $plugin -Old $oldStormSequence -New $newStormSequence -Description 'add automatic stormwater sequence-to-alignment workflow'

$oldWaterSequence = @'
                        Cmd("Sequence Mains and Branches", "CE_WATERSEQ ", "Sequence water routes and branches."),
                        Cmd("Create / Refresh Alignments", "CE_WATERALIGN ", "Create linked water alignments."),
'@
$newWaterSequence = @'
                        Cmd("Sequence + Auto Alignments", "CE_WATERSEQPRODUCTION ", "Sequence water and automatically continue into alignment production; optionally queue profiles."),
                        Cmd("Sequence Mains and Branches", "CE_WATERSEQ ", "Sequence only water routes and branches."),
                        Cmd("Create / Refresh Alignments", "CE_WATERALIGN ", "Create linked water alignments."),
'@
Replace-Once -Path $plugin -Old $oldWaterSequence -New $newWaterSequence -Description 'add automatic water sequence-to-alignment workflow'

# Final verification.
$pluginText = [System.IO.File]::ReadAllText($plugin)
foreach ($token in @('AugustAutomaticRefreshManager.Initialize();','CE_ASSEMBLYMARKERS','CE_JUNCTIONRETURNTYPE','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION')) {
    if (-not $pluginText.Contains($token)) { throw "August behavior verification failed: $token" }
}
$roadText = [System.IO.File]::ReadAllText($roadProduction)
if (-not $roadText.Contains('AugustRoadProfileDefaults.PreferredBandSet')) { throw 'Road profile default band set integration missing.' }
$surfaceText = [System.IO.File]::ReadAllText($surface)
if (-not $surfaceText.Contains('Internal holes only')) { throw 'Surface internal-holes-only mode missing.' }

Write-Host 'Final August behavior fixes are integrated for Civil 3D 2023 compilation.' -ForegroundColor Cyan
