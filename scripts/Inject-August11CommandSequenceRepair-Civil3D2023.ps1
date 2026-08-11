[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August11 sequence-repair source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

$runner = Need 'CeSequentialCommandRunner.cs'
$runnerText = ReadText $runner
if (-not $runnerText.Contains('internal static class CeSequentialCommandRunner') -or
    -not $runnerText.Contains('CommandEnded += OnCommandEnded') -or
    -not $runnerText.Contains('CommandCancelled += OnCommandCancelled') -or
    -not $runnerText.Contains('AcApplication.Idle += OnIdle')) {
    throw 'CE sequential command runner is incomplete.'
}

# CE_ROADPROFILEFULL previously sent all three command names in one input string.
# CE_ROADPROFILES is interactive, so later names could be consumed by its prompts.
$roadCorridor = Need 'RoadCorridorCompletionCommands.cs'
$text = ReadText $roadCorridor
$oldProfile = '            document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);'
$newProfile = @'
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" },
                "CE Tools - Complete road profile");
'@
if ($text.Contains($oldProfile)) {
    $text = $text.Replace($oldProfile,$newProfile.TrimEnd("`r","`n"))
    Write-Host 'Sequenced CE_ROADPROFILEFULL one command at a time.' -ForegroundColor Green
}
elseif (-not $text.Contains('"CE Tools - Complete road profile"')) {
    throw 'CE_ROADPROFILEFULL interactive command chain marker was not found.'
}

$oldCorridor = '            document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE ", true, false, true);'
$newCorridor = @'
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE" },
                "CE Tools - Complete road corridors");
'@
if ($text.Contains($oldCorridor)) {
    $text = $text.Replace($oldCorridor,$newCorridor.TrimEnd("`r","`n"))
    Write-Host 'Sequenced CE_ROADCORRIDORFULL one command at a time.' -ForegroundColor Green
}
elseif (-not $text.Contains('"CE Tools - Complete road corridors"')) {
    throw 'CE_ROADCORRIDORFULL interactive command chain marker was not found.'
}
WriteText $roadCorridor $text

# Best-fit alias gets the same safe sequence instead of a single multi-command
# SendString so its surface/point/style prompts cannot consume later command names.
$vertical = Need 'August11RoadVerticalCurveCommands.cs'
$text = ReadText $vertical
$oldBestFit = '            document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);'
$newBestFit = @'
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" },
                "CE Tools - Best-fit final road profile");
'@
if ($text.Contains($oldBestFit)) {
    $text = $text.Replace($oldBestFit,$newBestFit.TrimEnd("`r","`n"))
    Write-Host 'Sequenced CE_ROADPROFILEBESTFIT one command at a time.' -ForegroundColor Green
}
elseif (-not $text.Contains('"CE Tools - Best-fit final road profile"')) {
    throw 'CE_ROADPROFILEBESTFIT interactive command chain marker was not found.'
}
WriteText $vertical $text

Write-Host 'August 11 interactive command sequence repair passed.' -ForegroundColor Cyan
