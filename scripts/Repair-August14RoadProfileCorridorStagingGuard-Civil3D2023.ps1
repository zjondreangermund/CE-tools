[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

function FindCommandOwnerFile([string]$commandName) {
    $escaped = [regex]::Escape($commandName)
    foreach ($file in Get-ChildItem -LiteralPath $src -Filter '*.cs' -File) {
        $text = ReadText $file.FullName
        if ([regex]::IsMatch($text, '\[CommandMethod\s*\([^\]]*"' + $escaped + '"[^\]]*\)\s*\]', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            return $file.FullName
        }
    }
    return $null
}

function ReplaceCommandMethodBody([string]$commandName,[string]$newBody) {
    $path = FindCommandOwnerFile $commandName
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Command owner could not be located for $commandName."
    }

    $text = ReadText $path
    $escaped = [regex]::Escape($commandName)
    $attribute = [regex]::Match(
        $text,
        '\[CommandMethod\s*\([^\]]*"' + $escaped + '"[^\]]*\)\s*\]',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $attribute.Success) {
        throw "CommandMethod attribute could not be located for $commandName in $([System.IO.Path]::GetFileName($path))."
    }

    $openBrace = $text.IndexOf('{', $attribute.Index + $attribute.Length)
    if ($openBrace -lt 0) {
        throw "Method opening brace could not be located for $commandName."
    }

    $depth = 0
    $closeBrace = -1
    for ($index = $openBrace; $index -lt $text.Length; $index++) {
        $ch = $text[$index]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') {
            $depth--
            if ($depth -eq 0) {
                $closeBrace = $index
                break
            }
        }
    }
    if ($closeBrace -lt 0) {
        throw "Method closing brace could not be located for $commandName."
    }

    $indent = '        '
    $replacement = "{`r`n" + $newBody.TrimEnd("`r","`n") + "`r`n" + $indent + "}"
    $text = $text.Substring(0,$openBrace) + $replacement + $text.Substring($closeBrace + 1)
    WriteText $path $text
    return $path
}

$profileBody = @'
            Document document = ActiveDocument();
            if (document == null) return;
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVESFINAL", "CE_ROADPROFILEVIEWFINAL" },
                "CE road profile production");
'@

$corridorBody = @'
            Document document = ActiveDocument();
            if (document == null) return;
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE", "CE_ROADCORRIDOROUTPUTFIX" },
                "CE road corridor production");
'@

$profileOwner = ReplaceCommandMethodBody 'CE_ROADPROFILEFULL' $profileBody
$corridorOwner = ReplaceCommandMethodBody 'CE_ROADCORRIDORFULL' $corridorBody

# Normalize the live corridor defaults if the current owner source exposes them.
$roadCorridor = Join-Path $src 'RoadCorridorCompletionCommands.cs'
if (Test-Path -LiteralPath $roadCorridor -PathType Leaf) {
    $text = ReadText $roadCorridor

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
}

$profileVerify = ReadText $profileOwner
$corridorVerify = ReadText $corridorOwner
if (-not $profileVerify.Contains('CeSequentialCommandRunner.Start') -or
    -not $profileVerify.Contains('new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVESFINAL", "CE_ROADPROFILEVIEWFINAL" }')) {
    throw 'CE_ROADPROFILEFULL final sequential Road profile sequence was not established.'
}
if (-not $corridorVerify.Contains('CeSequentialCommandRunner.Start') -or
    -not $corridorVerify.Contains('new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE", "CE_ROADCORRIDOROUTPUTFIX" }')) {
    throw 'CE_ROADCORRIDORFULL final sequential Road corridor sequence was not established.'
}
if ($profileVerify.Contains('SendStringToExecute("CE_ROADPROFILES') -or
    $corridorVerify.Contains('SendStringToExecute("CE_ROADCORRIDORS')) {
    throw 'Unsafe multi-command Road SendStringToExecute queue remains after staging repair.'
}

$finalizer = FindCommandOwnerFile 'CE_ROADVERTICALCURVESFINAL'
$viewFinalizer = FindCommandOwnerFile 'CE_ROADPROFILEVIEWFINAL'
$corridorFinalizer = FindCommandOwnerFile 'CE_ROADCORRIDOROUTPUTFIX'
foreach ($pair in @(
    @{ Name='CE_ROADVERTICALCURVESFINAL'; Path=$finalizer },
    @{ Name='CE_ROADPROFILEVIEWFINAL'; Path=$viewFinalizer },
    @{ Name='CE_ROADCORRIDOROUTPUTFIX'; Path=$corridorFinalizer })) {
    if ([string]::IsNullOrWhiteSpace($pair.Path)) {
        throw "Required finalizer command owner is missing: $($pair.Name)"
    }
}

Write-Host ('CE_ROADPROFILEFULL owner repaired: ' + [System.IO.Path]::GetFileName($profileOwner)) -ForegroundColor Green
Write-Host ('CE_ROADCORRIDORFULL owner repaired: ' + [System.IO.Path]::GetFileName($corridorOwner)) -ForegroundColor Green
Write-Host 'Road profile/corridor staging is command-owner aware and uses the safe sequential command runner.' -ForegroundColor Green
Write-Host 'CE_ROADPROFILEFULL runs profiles, final design, final vertical curves and final profile-view styling one step at a time.' -ForegroundColor Green
Write-Host 'CE_ROADCORRIDORFULL runs corridor creation, completion and the final CE-TOP/CE-BOTTOM output pass one step at a time.' -ForegroundColor Green
