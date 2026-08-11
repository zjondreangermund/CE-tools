[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 11 compiler source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

# Run all field-completion, behavioral-audit and command-sequencing passes here
# so the existing one-click Stage-Build script gets the same audited source.
foreach ($completionName in @(
    'Inject-August11FieldCompletion2-Civil3D2023.ps1',
    'Inject-August11FieldCompletion3-Civil3D2023.ps1',
    'Inject-August11FieldCompletion4-Civil3D2023.ps1',
    'Inject-August11AuditRepairs-Civil3D2023.ps1',
    'Inject-August11CommandSequenceRepair-Civil3D2023.ps1')) {
    $completion = Join-Path $root ('scripts\' + $completionName)
    if (-not (Test-Path -LiteralPath $completion -PathType Leaf)) {
        throw "August 11 completion/audit pass was not found: $completion"
    }
    Unblock-File -LiteralPath $completion -ErrorAction SilentlyContinue
    & $completion -RepoRoot $root
    $global:LASTEXITCODE = 0
}

# CE_NETWORKMULTI already belongs to the permanent final-closure command hub.
# Keep that public command stable; expose this new source's optional hub under a
# different command so AutoCAD never receives duplicate CommandMethod names.
$network = Required 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$old = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
$new = '[CommandMethod("CE_TOOLS", "CE_NETWORKBATCHTOOLS", CommandFlags.Modal)]'
if ($text.Contains($old)) {
    $text = $text.Replace($old,$new)
    WriteText $network $text
    Write-Host 'Renamed August11 network hub to CE_NETWORKBATCHTOOLS; retained established CE_NETWORKMULTI owner.' -ForegroundColor Green
}
elseif ($text.Contains($new)) { Write-Host 'August11 network hub command name is already collision-safe.' -ForegroundColor DarkGreen }
else { throw 'August11 network hub CommandMethod marker was not found.' }

# Project metadata propagation only needs to queue the already-proven universal
# metadata refresh. Avoid a compile-time dependency on a private manager method.
$survey = Required 'August11SurveyRuntimeCommands.cs'
$text = ReadText $survey
$old = '                ProductionMetadataDynamicManager.Refresh(document);'
$new = '                UniversalDynamicRefreshManager.Queue();'
if ($text.Contains($old)) {
    $text = $text.Replace($old,$new)
    WriteText $survey $text
    Write-Host 'Routed project-location metadata refresh through UniversalDynamicRefreshManager.' -ForegroundColor Green
}
elseif ($text.Contains($new)) { Write-Host 'Project-location metadata refresh is already using universal refresh.' -ForegroundColor DarkGreen }
else { throw 'Project-location metadata refresh marker was not found.' }

# Match the proven CE Tools modal-window call pattern used elsewhere in the repo.
# Do not depend on a host-version-specific ShowModalWindow return signature.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldWelcome = @'
            bool? accepted = AcApplication.ShowModalWindow(window);
            if (accepted != true || string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
'@
$newWelcome = @'
            AcApplication.ShowModalWindow(window);
            if (string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
'@
$oldWelcomeValue = $oldWelcome.TrimEnd("`r","`n")
if ($text.Contains($oldWelcomeValue)) {
    $text = $text.Replace($oldWelcomeValue,$newWelcome.TrimEnd("`r","`n"))
    WriteText $production $text
    Write-Host 'Normalized CE welcome modal call for Civil 3D 2023.' -ForegroundColor Green
}
elseif ($text.Contains('AcApplication.ShowModalWindow(window);') -and -not $text.Contains('bool? accepted =')) {
    Write-Host 'CE welcome modal call is already host-compatible.' -ForegroundColor DarkGreen
}
else { throw 'CE welcome modal call marker was not found.' }

# TableHitTestInfo is a value type in the 2023 managed API. The permanent source
# is already fixed, but normalize any restored/stale copy before compilation.
$table = Required 'TableCellNavigationCommands.cs'
$text = ReadText $table
if ($text.Contains('hit != null && hit.Type == TableHitTestType.Cell')) {
    $text = $text.Replace('hit != null && hit.Type == TableHitTestType.Cell','hit.Type == TableHitTestType.Cell')
    WriteText $table $text
    Write-Host 'Normalized TableHitTestInfo value-type handling.' -ForegroundColor Green
}

# Verify the August11 batch source itself no longer DECLARES CE_NETWORKMULTI.
# Other production-centre source may legitimately reference the established hub.
$text = ReadText $network
$duplicateDeclaration = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
if ($text.Contains($duplicateDeclaration)) {
    throw 'August11 network batch source still declares colliding CE_NETWORKMULTI CommandMethod.'
}
if (-not $text.Contains('[CommandMethod("CE_TOOLS", "CE_NETWORKBATCHTOOLS", CommandFlags.Modal)]')) {
    throw 'August11 collision-safe CE_NETWORKBATCHTOOLS declaration is missing.'
}

# Focused field validator and generic PowerShell command/behavior validator are
# both required on the user's one-click build path before MSBuild.
foreach ($validatorName in @('Validate-August11FieldCompletion2.ps1','Validate-CECommandWiring.ps1')) {
    $validator = Join-Path $root ('scripts\' + $validatorName)
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "August 11 validator was not found: $validator"
    }
    Unblock-File -LiteralPath $validator -ErrorAction SilentlyContinue
    & $validator -RepoRoot $root
    $global:LASTEXITCODE = 0
}

Write-Host 'August 11 Civil 3D 2023 compiler compatibility and wiring guard passed.' -ForegroundColor Cyan
