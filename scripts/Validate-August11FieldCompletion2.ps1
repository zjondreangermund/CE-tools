[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$scripts = Join-Path $root 'scripts'
function Text([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "AUGUST11 FINAL VALIDATION FAILED: missing $path" }
    return [System.IO.File]::ReadAllText($path)
}
function Need([bool]$condition,[string]$message) { if (-not $condition) { throw "AUGUST11 FINAL VALIDATION FAILED: $message" } }

# Parse every August11 PowerShell file now. This catches quoting/bracket errors
# before the user's Civil 3D build reaches any C# compilation work.
Get-ChildItem -LiteralPath $scripts -Filter '*August11*.ps1' -File | ForEach-Object {
    $errors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$errors) | Out-Null
    if ($errors -and $errors.Count -gt 0) {
        $message = ($errors | ForEach-Object { $_.Message }) -join ' | '
        throw "AUGUST11 FINAL VALIDATION FAILED: PowerShell parse error in $($_.Name): $message"
    }
}
Write-Host 'All August 11 PowerShell injectors/repairs/validators parse successfully.' -ForegroundColor DarkGreen

$roadLayout = Text 'RoadLayoutProductionCommands.cs'
$bellmouth = Text 'August11BellmouthTrimCommands.cs'
$roadHub = Text 'August11RoadCompletionCommands.cs'
$production = Text 'August11ProductionCentreCommands.cs'
$survey = Text 'August11SurveyRuntimeCommands.cs'
$plugin = Text 'PluginEntry.cs'
$styleCentre = Text 'ProjectStyleCenterCommands.cs'
$stylePresets = Text 'August11DisciplineStylePresetCommands.cs'
$roadCorridor = Text 'RoadCorridorCompletionCommands.cs'
$vertical = Text 'August11RoadVerticalCurveCommands.cs'
$sequence = Text 'CeSequentialCommandRunner.cs'

# Bellmouth geometry and exact tangent-edge cleanup.
Need ($roadLayout.Contains('Vector2d firstOffset = ux.MultiplyBy')) 'endpoint-derived bellmouth tangent geometry is missing'
Need ($roadLayout.Contains('Vector2d secondOffset = ux.MultiplyBy')) 'second bellmouth tangent endpoint is missing'
Need (-not $roadLayout.Contains('double baseAngle = Math.Atan2(ux.Y, ux.X);')) 'old hard-coded bellmouth base-angle implementation remains'
Need ($bellmouth.Contains('CE_BELLMOUTHTRIMEDGES')) 'bellmouth tangent trim command missing'
Need ($bellmouth.Contains('GetSplitCurves')) 'bellmouth edge trim does not split at tangent stations'
Need ($bellmouth.Contains('arc.StartPoint') -and $bellmouth.Contains('arc.EndPoint')) 'bellmouth edge trim does not use actual arc tangent endpoints'
Need ($bellmouth.Contains('TryReadStoredJunctionGroup')) 'bellmouth trim does not use exact stored CE junction grouping'
Need ($roadHub.Contains('CE_BELLMOUTHTRIMEDGES')) 'Road Completion hub does not expose bellmouth tangent trim'
Need ($production.Contains('CE_BELLMOUTHTRIMEDGES')) 'Road Production Centre does not expose bellmouth tangent trim'

# Original COGO restore position must be captured at creation/first refresh, not
# only when the user later happens to run an overlap command.
Need ($survey.Contains('VertexSettingOutCommands.RefreshAll(_document)')) 'post-setting-out vertex refresh is missing'
Need ($survey.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(_document)')) 'initial COGO label offsets are not captured immediately after setting-out'
Need ($survey.Contains('if (space == null || !space.IsLayout) continue;')) 'linked multi-surface table refresh is not scanning all layout spaces'

# Main CE TOOLS ribbon must visibly expose the new home and production-critical tools.
Need ($plugin.Contains('Cmd("CE Tools Home", "CE_WELCOME ')) 'main CE TOOLS ribbon has no CE Tools Home / welcome entry'
Need ($plugin.Contains('Cmd("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS ')) 'main Project Styles menu has no discipline preset entry'
Need ($plugin.Contains('Cmd("Complete Final Road Profile", "CE_ROADPROFILEFULL ')) 'main Road Production menu lacks complete final road profile command'
Need ($plugin.Contains('Cmd("Add / Repair Vertical Curves", "CE_ROADVERTICALCURVES ')) 'main Road Production menu lacks vertical-curve command'
Need ($plugin.Contains('Cmd("Trim Edges to Bellmouths", "CE_BELLMOUTHTRIMEDGES ')) 'main Road Production menu lacks bellmouth tangent trim'

# Independent discipline style selection from the same Civil style catalogue.
Need ($styleCentre.Contains('August11DisciplineStylePresetManager.SavePreset(document.Database, selection);')) 'CE_PROJECTSTYLES does not snapshot each saved discipline preset'
Need ($stylePresets.Contains('PROJECT_STYLE_PRESET_')) 'discipline preset storage record missing'
Need ($stylePresets.Contains('ActivateForProduction')) 'discipline style manager does not isolate unsaved production disciplines'
Need ($production.Contains('August11DisciplineStylePresetManager.ActivateForProduction')) 'guided Production Centres do not safely activate/reset discipline presets'

# Final road profile is not complete until it has an editable PVI profile and
# true vertical curves. Interactive stages must be launched sequentially rather
# than placing later command names into the first command's input stream.
Need ($vertical.Contains('AddFreeSymmetricParabolaByPVIAndCurveLength')) 'true parabolic road vertical-curve API call missing'
Need ($sequence.Contains('internal static class CeSequentialCommandRunner')) 'safe sequential CE command runner is missing'
Need ($sequence.Contains('CommandEnded += OnCommandEnded')) 'sequential runner does not wait for CommandEnded'
Need ($sequence.Contains('CommandCancelled += OnCommandCancelled')) 'sequential runner does not stop safely on cancellation'
Need ($roadCorridor.Contains('CeSequentialCommandRunner.Start')) 'CE_ROADPROFILEFULL/CE_ROADCORRIDORFULL are not using safe command sequencing'
Need ($roadCorridor.Contains('new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" }')) 'CE_ROADPROFILEFULL sequence is incomplete'
Need ($roadCorridor.Contains('new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE" }')) 'CE_ROADCORRIDORFULL sequence is incomplete'
Need (-not $roadCorridor.Contains('SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE')) 'unsafe road-profile multi-command input string remains'
Need (-not $roadCorridor.Contains('SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE')) 'unsafe road-corridor multi-command input string remains'
Need ($roadCorridor.Contains('GetProperty("Visible"')) 'corridor completion does not attempt to restore visibility'
Need ($roadCorridor.Contains('RecordGraphicsModified')) 'corridor completion does not refresh corridor graphics'

Write-Host 'August 11 focused final field-completion validation passed.' -ForegroundColor Green
