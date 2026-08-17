[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Namibia project-zone source missing: $path" }
    return $path
}
function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceMethodBody([string]$text,[string]$signature,[string]$body) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method signature not found: $signature" }
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

# The selected Project/Survey Town is authoritative when it has a known Namibia
# LO mapping. A stale previously saved Coordinate System must not keep the old LO.
$runtimePath = Required 'August17ProductionFeatureLineCommands.cs'
$runtime = ReadText $runtimePath
$preferredBody = @'
            if (document != null)
            {
                try
                {
                    IDictionary<string, string> project = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
                    string town;
                    if (project != null && project.TryGetValue("Town", out town) && !string.IsNullOrWhiteSpace(town))
                    {
                        int townZone = ParseLo(NamibiaCoordinateSystemCatalog.PreferredLoName(town));
                        if (townZone > 0) return townZone;
                    }
                    string coordinateSystem;
                    if (project != null && project.TryGetValue("Coordinate System", out coordinateSystem))
                    {
                        int coordinateZone = ParseLo(coordinateSystem);
                        if (coordinateZone > 0) return coordinateZone;
                    }
                }
                catch { }
            }

            int inferred;
            try { NamibiaCoordinateRuntime.TryInferLoZone(out inferred); }
            catch { inferred = 0; }
            return inferred > 0 ? inferred : 17;
'@
$runtime = ReplaceMethodBody $runtime 'internal static int PreferredLoCentralMeridian(Document document)' $preferredBody
WriteText $runtimePath $runtime

# CE_NAMIBIALO must use the linked Project Town/CRS rather than only the drawing's
# current coordinate system. More importantly, popup persistence is loaded before
# display, so force the authoritative Project zone AFTER those saved values load.
$namibiaPath = Required 'NamibiaCoordinateRuntimeCommands.cs'
$namibia = ReadText $namibiaPath
$oldInference = @'
            int inferred;
            NamibiaCoordinateRuntime.TryInferLoZone(out inferred);
            if (inferred <= 0) inferred = 17;
'@ -replace "`n","`r`n"
$newInference = @'
            int inferred = August17ProjectRuntime.PreferredLoCentralMeridian(document);
'@ -replace "`n","`r`n"
if ($namibia.Contains($oldInference)) {
    $namibia = $namibia.Replace($oldInference,$newInference)
}
elseif (-not $namibia.Contains('int inferred = August17ProjectRuntime.PreferredLoCentralMeridian(document);')) {
    throw 'CE_NAMIBIALO initial-zone block could not be linked to Project Town/CRS.'
}

if (-not $namibia.Contains('projectZoneField.Value = inferred.ToString(CultureInfo.InvariantCulture);')) {
    $oldEdit = '            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;'
    $newEdit = @'
            // Load normal popup defaults first, then restore the live Project Town/CRS
            // zone so a previously saved manual zone cannot override a Town change.
            CrossDrawingProductionSettingsStore.Load(model);
            ProductionSettingsPersistenceStore.Load(document.Database, model);
            ProductionSettingsField projectZoneField = model.Fields.FirstOrDefault(field =>
                string.Equals(field.Key, "Zone", StringComparison.OrdinalIgnoreCase));
            if (projectZoneField != null)
                projectZoneField.Value = inferred.ToString(CultureInfo.InvariantCulture);
            var settingsWindow = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(settingsWindow);
            if (!settingsWindow.Accepted) return;
            ProductionSettingsPersistenceStore.Save(document.Database, model);
            CrossDrawingProductionSettingsStore.Save(model);
'@ -replace "`n","`r`n"
    if (-not $namibia.Contains($oldEdit)) { throw 'CE_NAMIBIALO settings-window anchor was not found.' }
    $namibia = $namibia.Replace($oldEdit,$newEdit.TrimEnd())
}
WriteText $namibiaPath $namibia

# Regression guards for the exact field failure reported on 17 August.
$runtimeCheck = ReadText $runtimePath
$methodStart = $runtimeCheck.IndexOf('internal static int PreferredLoCentralMeridian(Document document)',[StringComparison]::Ordinal)
$methodEnd = $runtimeCheck.IndexOf('internal static bool TryInsertRegisteredClientBookTitleBlock',$methodStart,[StringComparison]::Ordinal)
if ($methodStart -lt 0 -or $methodEnd -le $methodStart) { throw 'Project LO resolver method range could not be validated.' }
$method = $runtimeCheck.Substring($methodStart,$methodEnd-$methodStart)
$townIndex = $method.IndexOf('TryGetValue("Town"',[StringComparison]::Ordinal)
$crsIndex = $method.IndexOf('TryGetValue("Coordinate System"',[StringComparison]::Ordinal)
if ($townIndex -lt 0 -or $crsIndex -lt 0 -or $townIndex -gt $crsIndex) { throw 'Project Town is not authoritative over a stale Coordinate System in the LO resolver.' }

$namibiaCheck = ReadText $namibiaPath
foreach ($marker in @(
    'int inferred = August17ProjectRuntime.PreferredLoCentralMeridian(document);',
    'CrossDrawingProductionSettingsStore.Load(model);',
    'ProductionSettingsPersistenceStore.Load(document.Database, model);',
    'projectZoneField.Value = inferred.ToString(CultureInfo.InvariantCulture);')) {
    if (-not $namibiaCheck.Contains($marker)) { throw "Namibia project-zone runtime marker missing: $marker" }
}
$catalog = ReadText (Required 'FinalAllCommentsCompletionCommands.cs')
if (-not $catalog.Contains('{ "Windhoek", 17 }')) { throw 'Windhoek -> LO17 town mapping is missing.' }

Write-Host 'Namibia LO project-zone fix passed: recognized Town overrides stale CRS and persisted popup Zone.' -ForegroundColor Green
Write-Host 'Windhoek now resolves to LO17 when CE_NAMIBIALO opens.' -ForegroundColor Green
