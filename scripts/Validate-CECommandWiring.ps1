[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
if (-not (Test-Path -LiteralPath $src -PathType Container)) { throw "CE wiring validation source folder missing: $src" }

function Text([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "CE wiring validation missing source: $path" }
    return [System.IO.File]::ReadAllText($path)
}
function Need([bool]$condition,[string]$message) { if (-not $condition) { throw "CE WIRING VALIDATION FAILED: $message" } }

$files = Get-ChildItem -LiteralPath $src -Filter '*.cs' -File
$owners = @{}
$refs = @{}
$declarationPattern = 'CommandMethod\s*\(\s*(?:"[^"]+"\s*,\s*)?"(?<cmd>CE_[A-Z0-9_]+)"'
$helperPattern = '\b(?:Cmd|Action|RoadAction|WorkflowAction)\s*\(\s*"[^"]*"\s*,\s*"(?<cmd>CE_[A-Z0-9_]+)\b'
$disciplinePattern = '(?:new\s+)?DisciplineWorkflowAction\s*\(\s*"[^"]*"\s*,\s*"(?<cmd>CE_[A-Z0-9_]+)\b'
$sendPattern = 'SendStringToExecute\s*\(\s*"(?<body>(?:[^"\\]|\\.)*)"'
$ceTokenPattern = '\bCE_[A-Z0-9_]+\b'

foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($text,$declarationPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $cmd = $match.Groups['cmd'].Value.ToUpperInvariant()
        if (-not $owners.ContainsKey($cmd)) { $owners[$cmd] = @() }
        $owners[$cmd] += $file.Name
    }
    foreach ($pattern in @($helperPattern,$disciplinePattern)) {
        foreach ($match in [regex]::Matches($text,$pattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $cmd = $match.Groups['cmd'].Value.ToUpperInvariant()
            if (-not $refs.ContainsKey($cmd)) { $refs[$cmd] = @() }
            $refs[$cmd] += $file.Name
        }
    }
    foreach ($match in [regex]::Matches($text,$sendPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $tokens = @([regex]::Matches($match.Groups['body'].Value,$ceTokenPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase) | ForEach-Object { $_.Value.ToUpperInvariant() })
        foreach ($cmd in $tokens) {
            if (-not $refs.ContainsKey($cmd)) { $refs[$cmd] = @() }
            $refs[$cmd] += $file.Name
        }
        Need ($tokens.Count -le 1) ("unsafe multi-CE-command SendStringToExecute chain in {0}: {1}" -f $file.Name,($tokens -join ' -> '))
    }
}

foreach ($cmd in $owners.Keys) {
    Need ($owners[$cmd].Count -eq 1) ("duplicate CommandMethod owner for {0}: {1}" -f $cmd,($owners[$cmd] -join ', '))
}
foreach ($cmd in $refs.Keys) {
    if ($cmd -eq 'CE_TOOLS') { continue }
    Need ($owners.ContainsKey($cmd)) ("referenced CE command has no CommandMethod owner: {0} (from {1})" -f $cmd,(($refs[$cmd] | Select-Object -Unique) -join ', '))
}

$production = Text 'August11ProductionCentreCommands.cs'
$presets = Text 'August11DisciplineStylePresetCommands.cs'
$styleCentre = Text 'ProjectStyleCenterCommands.cs'
Need ($presets.Contains('ActivateForProduction') -and $presets.Contains('var clean = new ProjectStyleSelection')) 'discipline style default isolation missing'
foreach ($discipline in @('Platforms','Roads','Stormwater','Sewer','Water','Bulk Water','Parking','Flood')) {
    Need ($production.Contains('ActivateForProduction(Active() == null ? null : Active().Database, "' + $discipline + '")')) ("safe Production Centre style activation missing for $discipline")
    Need ($styleCentre.Contains('"' + $discipline + '"')) ("Project Style Centre choice missing for $discipline")
}
Need ($styleCentre.Contains('August11DisciplineStylePresetManager.SavePreset(document.Database, selection);')) 'Project Style Centre does not snapshot discipline presets'

$survey = Text 'August11SurveyRuntimeCommands.cs'
Need ($survey.Contains('if (space == null || !space.IsLayout) continue;')) 'multi-surface linked table refresh is not all-layout aware'
$roadNames = Text 'August11RoadNamingCurveCommands.cs'
foreach ($token in @('ReadRoadProductionSource(entity)','ReadRoadLayoutParent(entity, transaction)','string.Equals(label.SourceHandle, productionSource, StringComparison.OrdinalIgnoreCase)')) { Need ($roadNames.Contains($token)) ('ROAD-n metadata-first sync missing ' + $token) }
$midblock = Text 'August11MidblockSewerProductionCommands.cs'
Need ($midblock.Contains('double centreSpanX = parcels.Max(item => item.Center.X)')) 'Midblock automatic orientation is not row-spread based'
Need (-not $midblock.Contains('double totalWidth = parcels.Sum(item => item.Width);')) 'old Midblock orientation heuristic remains'
$bellmouth = Text 'August11BellmouthTrimCommands.cs'
foreach ($token in @('TryReadStoredJunctionGroup','RoadLayoutRecordKey','exact.TryGetValue(storedGroup')) { Need ($bellmouth.Contains($token)) ('bellmouth exact group wiring missing ' + $token) }
$network = Text 'August11NetworkBatchCommands.cs'
foreach ($token in @('NetworkSourceMarker.Mark(_document, _current, _discipline)','internal static void Mark(Document document, ObjectId id, string discipline)','internal static int Clear(Document document, IEnumerable<ObjectId> ids)')) { Need ($network.Contains($token)) ('network exact-document marker wiring missing ' + $token) }
$sequence = Text 'CeSequentialCommandRunner.cs'
foreach ($token in @('CommandEnded += OnCommandEnded','CommandCancelled += OnCommandCancelled','CommandFailed += OnCommandFailed','AcApplication.Idle += OnIdle')) { Need ($sequence.Contains($token)) ('safe sequential command runner missing ' + $token) }
$roadCorridor = Text 'RoadCorridorCompletionCommands.cs'
Need ($roadCorridor.Contains('new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" }')) 'complete road-profile command sequence missing'
Need ($roadCorridor.Contains('new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE" }')) 'complete corridor command sequence missing'
$platform = Text 'PlatformProductionCommands.cs'
Need (-not $platform.Contains('else featureLine.SetPointElevation(index, elevation);')) 'unsafe Platform AllPoints numeric-index setter remains'
Need (-not $platform.Contains('child.SetPointElevation(index, sourcePoint.Z + dz);')) 'unsafe Platform stepped-offset numeric-index setter remains'

Write-Host ('CE command/behavior wiring validation passed. Unique commands=' + $owners.Count + '; referenced commands=' + $refs.Count + '; Civil3D source files=' + $files.Count + '.') -ForegroundColor Green
