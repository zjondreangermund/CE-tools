[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Once {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
    if (-not $text.Contains($Old)) { throw "Could not integrate '$Description'. Marker not found in $Path" }
    [System.IO.File]::WriteAllText($Path,$text.Replace($Old,$New),[System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$plugin = Join-Path $root 'src\CE.Tools.Civil3D\PluginEntry.cs'
foreach ($required in @(
    $plugin,
    (Join-Path $root 'src\CE.Tools.Civil3D\SewerSequenceAutoProductionCommands.cs'),
    (Join-Path $root 'src\CE.Tools.Civil3D\MidblockSewerLayoutCommands.cs'),
    (Join-Path $root 'src\CE.Tools.Civil3D\ProfileStyleAutoImportRuntime.cs'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Final utility workflow source missing: $required" }
}

$oldSewer = @'
                        Cmd("Sewer Production Tools", "CE_SEWTOOLS ", "Open the complete sewer production menu."),
                        Cmd("Sequence Network + Production Options", "CE_SEWSEQWORKFLOW ", "Sequence a complete network or selected path."),
'@
$newSewer = @'
                        Cmd("Sewer Production Tools", "CE_SEWTOOLS ", "Open the complete sewer production menu."),
                        Cmd("Sequence + Auto Alignments", "CE_SEWSEQPRODUCTION ", "Sequence the sewer network and automatically continue into linked alignment production; profiles are optional."),
                        Cmd("Sequence Network + Production Options", "CE_SEWSEQWORKFLOW ", "Sequence a complete network or selected path."),
'@
Replace-Once -Path $plugin -Old $oldSewer -New $newSewer -Description 'add Sewer sequence-to-alignment workflow'

$oldUtility = @'
                        Cmd("Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE ", "Create Sewer/SW/Water/Bulk Water routes from connected CE road-reserve centrelines."),
                        Cmd("Multiple Pipe / Structure Tools", "CE_NETWORKMULTI ", "Create, connect and schedule multiple network objects."),
'@
$newUtility = @'
                        Cmd("Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE ", "Create Sewer/SW/Water/Bulk Water routes from connected CE road-reserve centrelines."),
                        Cmd("Midblock Sewer Centre + Offsets", "CE_MIDBLOCKSEWERLAYOUT ", "Create the Midblock sewer centre route plus both visible parallel offset guides for all/selected blocks."),
                        Cmd("Multiple Pipe / Structure Tools", "CE_NETWORKMULTI ", "Create, connect and schedule multiple network objects."),
'@
Replace-Once -Path $plugin -Old $oldUtility -New $newUtility -Description 'add direct Midblock sewer centre-plus-offset workflow'

$oldProfile = @'
                        Cmd("Batch Profile Views", "CE_PROFILEVIEWBATCHTOOLS ", "Apply profile-view styles, band sets, automatic fit and rebuild options."),
                        Cmd("Safe Profile / Band Batch", "CE_PROFILEBATCHSAFE ", "Run profile style/band repair stages independently to isolate incompatible profile views."),
'@
$newProfile = @'
                        Cmd("Batch Profile Views", "CE_PROFILEVIEWBATCHTOOLS ", "Apply profile-view styles, band sets, automatic fit and rebuild options."),
                        Cmd("Auto-Import Missing Profile/Band Styles", "CE_PROFILESTYLEAUTOIMPORT ", "Import the supplied CE project style sources only when the drawing lacks the expected profile/band library."),
                        Cmd("Safe Profile / Band Batch", "CE_PROFILEBATCHSAFE ", "Run profile style/band repair stages independently to isolate incompatible profile views."),
'@
Replace-Once -Path $plugin -Old $oldProfile -New $newProfile -Description 'add automatic profile/band style import command'

$text = [System.IO.File]::ReadAllText($plugin)
foreach ($command in @('CE_SEWSEQPRODUCTION','CE_MIDBLOCKSEWERLAYOUT','CE_PROFILESTYLEAUTOIMPORT')) {
    if (-not $text.Contains($command)) { throw "Final utility workflow ribbon verification failed: $command" }
}
Write-Host 'Final Sewer/Midblock/profile-style workflow ribbon integration passed.' -ForegroundColor Cyan
