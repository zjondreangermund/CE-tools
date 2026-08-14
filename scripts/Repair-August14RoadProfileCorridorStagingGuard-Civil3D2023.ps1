[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$roadCorridor = Join-Path $src 'RoadCorridorCompletionCommands.cs'
if (-not (Test-Path -LiteralPath $roadCorridor -PathType Leaf)) {
    throw "Road profile/corridor staging source missing: $roadCorridor"
}

function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

$text = ReadText $roadCorridor

# Do not depend on one historic whole-line spelling here. Earlier staging passes
# may legitimately add/remove commands or normalize whitespace before the August
# 13 road repair runs. Match the queue by its first CE command and replace it
# with the final production sequence.
$profileTarget = 'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL CE_ROADPROFILEVIEWFINAL ", true, false, true);'
$profilePattern = 'document\.SendStringToExecute\(\s*"CE_ROADPROFILES[^"]*"\s*,\s*true\s*,\s*false\s*,\s*true\s*\);'
$profileRegex = [regex]::new($profilePattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($profileRegex.IsMatch($text)) {
    $text = $profileRegex.Replace($text,$profileTarget,1)
}
elseif (-not $text.Contains('CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL CE_ROADPROFILEVIEWFINAL')) {
    throw 'CE_ROADPROFILEFULL queue could not be located for the final Road profile sequence.'
}

$corridorTarget = 'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX ", true, false, true);'
$corridorPattern = 'document\.SendStringToExecute\(\s*"CE_ROADCORRIDORS[^"]*"\s*,\s*true\s*,\s*false\s*,\s*true\s*\);'
$corridorRegex = [regex]::new($corridorPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($corridorRegex.IsMatch($text)) {
    $text = $corridorRegex.Replace($text,$corridorTarget,1)
}
elseif (-not $text.Contains('CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX')) {
    throw 'CE_ROADCORRIDORFULL queue could not be located for the final Road corridor sequence.'
}

# Normalize the live corridor surface defaults by pattern as well. This keeps the
# field repair idempotent if an earlier pass changes descriptions or fallback
# code lists while preserving the same settings keys.
$topTextTarget = 'model.AddText("TopCodes", "01 Corridor Surfaces", "Top link codes", "Top", "CE-TOP uses the Top link code. The finalizer also sets Top Links overhang correction and builds the surface.");'
$topTextRegex = [regex]::new('model\.AddText\(\s*"TopCodes"\s*,\s*"01 Corridor Surfaces"\s*,\s*"Top link codes"\s*,\s*"[^"]*"\s*,\s*"[^"]*"\s*\);',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($topTextRegex.IsMatch($text)) { $text = $topTextRegex.Replace($text,$topTextTarget,1) }

$bottomTextTarget = 'model.AddText("BottomCodes", "01 Corridor Surfaces", "Bottom link codes", "Datum", "CE-BOTTOM uses the Datum link code. The finalizer also sets Bottom Links overhang correction and builds the surface.");'
$bottomTextRegex = [regex]::new('model\.AddText\(\s*"BottomCodes"\s*,\s*"01 Corridor Surfaces"\s*,\s*"Bottom link codes"\s*,\s*"[^"]*"\s*,\s*"[^"]*"\s*\);',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($bottomTextRegex.IsMatch($text)) { $text = $bottomTextRegex.Replace($text,$bottomTextTarget,1) }

$topAssignTarget = 'TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top" }),' 
$topAssignRegex = [regex]::new('TopCodes\s*=\s*SplitCodes\(\s*model\.Text\(\s*"TopCodes"\s*\)\s*,\s*new\[\]\s*\{[^}]*\}\s*\)\s*,',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($topAssignRegex.IsMatch($text)) { $text = $topAssignRegex.Replace($text,$topAssignTarget,1) }

$bottomAssignTarget = 'BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum" }),' 
$bottomAssignRegex = [regex]::new('BottomCodes\s*=\s*SplitCodes\(\s*model\.Text\(\s*"BottomCodes"\s*\)\s*,\s*new\[\]\s*\{[^}]*\}\s*\)\s*,',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($bottomAssignRegex.IsMatch($text)) { $text = $bottomAssignRegex.Replace($text,$bottomAssignTarget,1) }

$text = $text.Replace('internal string Name { get; private set; }','public string Name { get; private set; }')
WriteText $roadCorridor $text

$verify = ReadText $roadCorridor
foreach ($marker in @(
    'CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL CE_ROADPROFILEVIEWFINAL',
    'CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX',
    '"Top link codes", "Top"',
    '"Bottom link codes", "Datum"',
    'public string Name { get; private set; }')) {
    if (-not $verify.Contains($marker)) {
        throw "Road staging guard could not establish required integration marker: $marker"
    }
}

Write-Host 'Road profile/corridor staging queues were normalized by command pattern before the August 13 field repair.' -ForegroundColor Green
Write-Host 'CE_ROADPROFILEFULL now includes design profile, final vertical curves and final profile-view styling.' -ForegroundColor Green
Write-Host 'CE_ROADCORRIDORFULL now includes corridor completion and the final CE-TOP/CE-BOTTOM output pass.' -ForegroundColor Green
