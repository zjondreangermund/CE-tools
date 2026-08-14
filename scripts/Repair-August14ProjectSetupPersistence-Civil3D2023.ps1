[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Project Setup persistence source missing: $path"
    }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,$utf8) }

# The normal CE-PRODUCTION CENTRE is still the front door used in field testing.
# Route its Project Setup row through CE_PROJECTSETUPCHOICE so the user always
# gets Last Saved versus Standard (Blank) before the form opens.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldProduction = '                Action("SETTINGS - Project Setup", "CE_PROJECTSETUP", "Project/client/stage/revision/designed-drawn-checked-approved information.", "01 SETTINGS"),'
$newProduction = '                Action("SETTINGS - CE-Project Setup", "CE_PROJECTSETUPCHOICE", "Open Last Saved Project Information or use Standard (Blank) Project Information before editing.", "01 SETTINGS"),'
if ($text.Contains($oldProduction)) {
    $text = $text.Replace($oldProduction,$newProduction)
    WriteText $production $text
    Write-Host 'Project Production now opens the Last Saved / Standard Blank Project Setup choice.' -ForegroundColor Green
}
elseif ($text.Contains('"CE_PROJECTSETUPCHOICE"')) {
    Write-Host 'Project Production already routes to CE_PROJECTSETUPCHOICE.' -ForegroundColor DarkGreen
}
else {
    throw 'Could not find the Project Production Project Setup action to upgrade.'
}

# The standalone CE_PROJECT menu should behave the same way as Project Production.
$project = Required 'ProjectSetupCommands.cs'
$text = ReadText $project
$oldMenu = '                    new DisciplineWorkflowAction("Set up project", "CE_PROJECTSETUP", "Enter project, client, location, standards, template and units.", "01 Project"),'
$newMenu = '                    new DisciplineWorkflowAction("Set up project - Last Saved or Blank", "CE_PROJECTSETUPCHOICE", "Open Last Saved Project Information or use Standard (Blank) Project Information.", "01 Project"),'
if ($text.Contains($oldMenu)) {
    $text = $text.Replace($oldMenu,$newMenu)
    WriteText $project $text
    Write-Host 'CE_PROJECT menu now routes Project Setup through the reusable choice workflow.' -ForegroundColor Green
}
elseif ($text.Contains('new DisciplineWorkflowAction("Set up project - Last Saved or Blank", "CE_PROJECTSETUPCHOICE"')) {
    Write-Host 'CE_PROJECT menu already uses the reusable Project Setup choice.' -ForegroundColor DarkGreen
}
else {
    throw 'Could not find the CE_PROJECT Set up project action to upgrade.'
}

# Verify the cross-drawing store and upgraded command source are present in the
# staged tree before the compiler is allowed to continue.
$store = Required 'ProjectLastSavedInfoStore.cs'
$choice = Required 'August14ProjectSetupCommands.cs'
$storeText = ReadText $store
$choiceText = ReadText $choice
if (-not $storeText.Contains('LastProjectInformation.dat')) {
    throw 'The CE Tools cross-drawing Last Saved Project Information store is missing.'
}
if (-not $choiceText.Contains('Open Last Saved Project Information') -or
    -not $choiceText.Contains('Use Standard (Blank) Project Information') -or
    -not $choiceText.Contains('ProjectLastSavedInfoStore.TryWrite')) {
    throw 'The upgraded Project Setup Last Saved / Standard Blank workflow is incomplete.'
}

$productionVerify = ReadText $production
if ($productionVerify.Contains($oldProduction) -or
    -not $productionVerify.Contains('"CE_PROJECTSETUPCHOICE"')) {
    throw 'The normal Project Production centre still routes to the old Project Setup command.'
}

Write-Host 'August 14 Project Setup persistence and entry-point repair passed.' -ForegroundColor Cyan
