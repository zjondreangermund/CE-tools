[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August11 completion-3 source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

$styleCentre = Need 'ProjectStyleCenterCommands.cs'
$stylePresets = Need 'August11DisciplineStylePresetCommands.cs'
$production = Need 'August11ProductionCentreCommands.cs'
$roadCorridor = Need 'RoadCorridorCompletionCommands.cs'
$vertical = Need 'August11RoadVerticalCurveCommands.cs'

# Every Project Style Centre save now snapshots that discipline separately.
$text = ReadText $styleCentre
$saveMarker = '                WriteSelection(document.Database, selection);'
if (-not $text.Contains('August11DisciplineStylePresetManager.SavePreset(document.Database, selection);')) {
    if (-not $text.Contains($saveMarker)) { throw 'Project Style Centre save marker not found.' }
    $text = $text.Replace($saveMarker,$saveMarker + "`r`n                August11DisciplineStylePresetManager.SavePreset(document.Database, selection);")
    WriteText $styleCentre $text
    Write-Host 'Integrated automatic per-discipline style-preset snapshot on CE_PROJECTSTYLES save.' -ForegroundColor Green
}

# Activating a Production Centre also activates its discipline's stored preset.
$text = ReadText $production
$activations = [ordered]@{
    'RunCentre("PLATFORM PRODUCTION"' = 'Platforms'
    'RunCentre("ROAD PRODUCTION"' = 'Roads'
    'RunCentre("STORMWATER PRODUCTION"' = 'Stormwater'
    'RunCentre("SEWER PRODUCTION"' = 'Sewer'
    'RunCentre("WATER PRODUCTION"' = 'Water'
    'RunCentre("BULK WATER PRODUCTION"' = 'Bulk Water'
    'RunCentre("PARKING AREA PRODUCTION"' = 'Parking'
    'RunCentre("FLOOD PRODUCTION"' = 'Flood'
}
foreach ($pair in $activations.GetEnumerator()) {
    $marker = $pair.Key
    $discipline = $pair.Value
    $activation = 'August11DisciplineStylePresetManager.Activate(Active() == null ? null : Active().Database, "' + $discipline + '");'
    if ($text.Contains($activation)) { continue }
    $index = $text.IndexOf($marker,[System.StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Production Centre marker not found for discipline $discipline" }
    $lineStart = $text.LastIndexOf("`n",$index)
    if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }
    $indent = $text.Substring($lineStart,$index-$lineStart)
    $text = $text.Insert($lineStart,$indent + $activation + "`r`n")
    Write-Host "Integrated style-preset activation for $discipline production." -ForegroundColor Green
}

# Expose preset review/copy next to the central style picker.
$settingsAnchor = '                Action("Project Style Centre", "CE_PROJECTSTYLES", "Select shared discipline Civil 3D styles.", "01 SETTINGS"),'
if (-not $text.Contains('Action("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS"')) {
    if (-not $text.Contains($settingsAnchor)) { throw 'Project Production style-settings marker not found.' }
    $presetAction = '                Action("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Review, copy or activate independent Roads/SW/Sewer/Water/Platform style selections from the shared style library.", "01 SETTINGS"),'
    $text = $text.Replace($settingsAnchor,$settingsAnchor + "`r`n" + $presetAction)
}
WriteText $production $text

# Full road profile now ends with an actual PVI-based vertical-curve pass.
$text = ReadText $roadCorridor
$oldProfileFull = 'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE ", true, false, true);'
$newProfileFull = 'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);'
if ($text.Contains($oldProfileFull)) {
    $text = $text.Replace($oldProfileFull,$newProfileFull)
    Write-Host 'Integrated automatic PVI parabolic vertical curves into CE_ROADPROFILEFULL.' -ForegroundColor Green
}
elif ($text.Contains($newProfileFull)) { Write-Host 'CE_ROADPROFILEFULL already includes vertical curves.' -ForegroundColor DarkGreen }
else { throw 'CE_ROADPROFILEFULL command sequence marker not found.' }

# Existing corridors can exist in Prospector while their display remains hidden.
# Use reflection so this stays compatible with Civil 3D 2023 managed API builds.
$visibilityMarker = '                    if (corridor == null || corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0) continue;'
if (-not $text.Contains('PropertyInfo visibleProperty = corridor.GetType().GetProperty("Visible"')) {
    if (-not $text.Contains($visibilityMarker)) { throw 'Road corridor object-validation marker not found.' }
    $visibilityInsert = @'
                    if (corridor == null || corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        PropertyInfo visibleProperty = corridor.GetType().GetProperty("Visible", BindingFlags.Public | BindingFlags.Instance);
                        if (visibleProperty != null && visibleProperty.CanWrite && visibleProperty.PropertyType == typeof(bool))
                            visibleProperty.SetValue(corridor, true, null);
                        MethodInfo graphics = corridor.GetType().GetMethod("RecordGraphicsModified", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                        if (graphics != null) graphics.Invoke(corridor, new object[] { true });
                    }
                    catch { }
'@
    $text = $text.Replace($visibilityMarker,$visibilityInsert.TrimEnd("`r","`n"))
    Write-Host 'Integrated corridor visible/graphics refresh repair.' -ForegroundColor Green
}
WriteText $roadCorridor $text

Write-Host 'August 11 field completion pass 3 is ready for validation.' -ForegroundColor Cyan
