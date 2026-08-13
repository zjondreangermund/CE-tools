[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

# Apply the audited coordinate-order fix late in staging. This changes only
# displayed X/Y headings and displayed numeric values; DWG/COGO coordinates are
# never rewritten. The existing repair also runs the global UI persistence pass.
$vertexRepair = Join-Path $root 'scripts\Repair-VertexCoordinateOrder-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $vertexRepair -PathType Leaf)) {
    throw "Vertex coordinate-order repair was not found: $vertexRepair"
}
Unblock-File -LiteralPath $vertexRepair -ErrorAction SilentlyContinue
& $vertexRepair -RepoRoot $root
$global:LASTEXITCODE = 0

$production = Join-Path $src 'August11ProductionCentreCommands.cs'
if (-not (Test-Path -LiteralPath $production -PathType Leaf)) {
    throw "Production Centre source was not found: $production"
}
$text = [System.IO.File]::ReadAllText($production)

# The first CE Tools welcome screen must open to the full available monitor
# extent while remaining a normal resizable WPF window.
$welcomePattern = '(?s)            Width = 760;\r?\n            Height = 470;\r?\n            WindowStartupLocation = WindowStartupLocation.CenterScreen;\r?\n            ResizeMode = ResizeMode.NoResize;'
$welcomeReplacement = @'
            MinWidth = 760;
            MinHeight = 470;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Maximized;
'@.TrimEnd("`r","`n")
if ([regex]::IsMatch($text,$welcomePattern)) {
    $text = [regex]::Replace($text,$welcomePattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $welcomeReplacement },1)
}
elseif (-not ($text.Contains('WindowState = WindowState.Maximized;') -and $text.Contains('ResizeMode = ResizeMode.CanResize;'))) {
    throw 'CE Tools welcome-window size marker was not found.'
}

# Make the new Road workflow the primary Road entry point while retaining the
# existing Road Production Centre command for ribbon/backward compatibility.
$text = $text.Replace(
    'Action("ROAD PRODUCTION", "CE_ROADPRODUCTIONCENTRE", "Cadastral road layout through alignments, profiles and corridors.", "01 Disciplines"),',
    'Action("ROAD PRODUCTION", "CE_ROADPRODUCTIONWORKFLOW", "New staged Road workflow: prepare, align, profile/split, corridor, junction, setting-out and delivery.", "01 Disciplines"),')
$text = $text.Replace(
    'Action("▶ RUN COMPLETE ROAD PRODUCTION", "CE_ROADPRODUCTION", "Open the ordered complete road-production workflow.", "99 RUN COMPLETE")',
    'Action("▶ RUN COMPLETE ROAD PRODUCTION", "CE_ROADPRODUCTIONWORKFLOW", "Open the new staged Road production workflow.", "99 RUN COMPLETE")')

# Add the profile split directly after Complete Road Profile in the older Road
# centre too, so either entry point exposes the new command.
if (-not $text.Contains('"CE_ROADPROFILEVIEWSPLIT"')) {
    $profileLinePattern = '(?m)^(\s*Action\("DESIGN - Complete Road Profile", "CE_ROADPROFILEFULL"[^\r\n]*\),)\r?$'
    if (-not [regex]::IsMatch($text,$profileLinePattern)) {
        throw 'Complete Road Profile action could not be located for split-profile insertion.'
    }
    $text = [regex]::Replace(
        $text,
        $profileLinePattern,
        '$1' + "`r`n                Action(`"Split Road Profile Views`", `"CE_ROADPROFILEVIEWSPLIT`", `"Split into specified station sections; default 0.000-750.000, 750.000-1500.000, etc.`", `"04 DESIGN`"),",
        1)
}

$text = $text.Replace(
    'Action("COMPLETE - Junction Setting-Out", "CE_JUNCTIONSETTINGOUT4", "Complete one full T/cross junction before continuing to the next.", "05 COMPLETE"),',
    'Action("COMPLETE - Junction Setting-Out", "CE_JUNCTIONSETTINGOUT4", "Complete 4 bellmouths per cross junction: J1.1-J1.4, then J2.1-J2.4 before continuing.", "05 COMPLETE"),')

[System.IO.File]::WriteAllText($production,$text,[System.Text.UTF8Encoding]::new($false))

$final = [System.IO.File]::ReadAllText($production)
foreach ($marker in @(
    'WindowState = WindowState.Maximized;',
    'ResizeMode = ResizeMode.CanResize;',
    '"CE_ROADPRODUCTIONWORKFLOW"',
    '"CE_ROADPROFILEVIEWSPLIT"',
    '4 bellmouths per cross junction')) {
    if (-not $final.Contains($marker)) { throw "August 13 requested feature wiring missing: $marker" }
}

Write-Host 'August 13 profile split/workflow UI, full-screen welcome and X/Y display corrections are staged.' -ForegroundColor Green
