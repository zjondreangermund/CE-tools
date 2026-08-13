[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 13 true sewer multi-source file missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

# The real implementation is committed as source and uses Civil 3D's .NET API
# to create ONE gravity network from all selected source objects. The August 12
# compatibility pass still adds the legacy CE_SEWERNETWORKFROMPOLYLINES command;
# convert that command into an alias instead of letting it launch the native
# single-object CreateNetworkFromObject queue.
$trueMulti = Required 'August13SewerMultiSourceNetworkCommands.cs'
$trueText = ReadText $trueMulti
foreach ($marker in @(
    '"CE_SEWERNETWORKMULTI"',
    'CivilNetwork.Create(',
    'network.AddLinePipe(',
    'network.AddStructure(',
    'ConnectToStructure(',
    'Select ALL sewer source lines/polylines/feature lines for ONE sewer network')) {
    if (-not $trueText.Contains($marker)) {
        throw "True sewer multi-source implementation marker missing: $marker"
    }
}

$network = Required 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$legacyPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_SEWERNETWORKFROMPOLYLINES".*?\r?\n        public void CreateSewerNetworksBatch\(\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        private static void StartNetworkBatch)'
$legacyReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_SEWERNETWORKFROMPOLYLINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSewerNetworksBatch()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            document.Editor.WriteMessage(
                "\nCE_SEWERNETWORKFROMPOLYLINES now uses CE's true multi-source sewer-network engine. Select the complete source set once; Civil 3D's single-object network-from-object selection is bypassed.");
            document.SendStringToExecute(
                "CE_SEWERNETWORKMULTI ",
                true,
                false,
                true);
        }
'@.TrimEnd("`r","`n")
$legacyRegex = [regex]::new(
    $legacyPattern,
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($legacyRegex.IsMatch($text)) {
    $text = $legacyRegex.Replace($text,$legacyReplacement,1)
}
elseif (-not ($text.Contains('"CE_SEWERNETWORKFROMPOLYLINES"') -and
              $text.Contains('"CE_SEWERNETWORKMULTI "'))) {
    throw 'Legacy sewer multi-polyline command could not be redirected to CE_SEWERNETWORKMULTI.'
}
WriteText $network $text

# Route the visible Sewer Production action directly to the new implementation.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$text = $text.Replace(
    'Action("CREATE - Sewer Network from Multiple Polylines", "CE_SEWERNETWORKFROMPOLYLINES", "Select all sewer source polylines/lines/feature lines in one selection and run the complete set as one queued gravity-network batch.", "03 CREATE"),',
    'Action("CREATE - Sewer Network from Multiple Polylines", "CE_SEWERNETWORKMULTI", "Select all sewer source lines/polylines/feature lines once and create ONE connected Civil 3D gravity sewer network directly from the complete set.", "03 CREATE"),')
WriteText $production $text

# Same-build guards: this installer must never ship the old sewer button back to
# NetworkFromObjectBatchManager/CREATE NETWORK FROM OBJECT.
$networkText = ReadText $network
$productionText = ReadText $production
if (-not $networkText.Contains('document.SendStringToExecute(') -or
    -not $networkText.Contains('"CE_SEWERNETWORKMULTI "')) {
    throw 'Legacy sewer command does not redirect to the true multi-source command.'
}
if (-not $productionText.Contains('"CE_SEWERNETWORKMULTI"')) {
    throw 'Sewer Production Centre is not routed to CE_SEWERNETWORKMULTI.'
}

$methodMatch = [regex]::Match(
    $networkText,
    '(?s)\[CommandMethod\("CE_TOOLS", "CE_SEWERNETWORKFROMPOLYLINES".*?public void CreateSewerNetworksBatch\(\).*?\n        \}')
if (-not $methodMatch.Success) {
    throw 'Could not verify the legacy sewer command body.'
}
if ($methodMatch.Value.Contains('NetworkFromObjectBatchManager.Start(') -or
    $methodMatch.Value.Contains('"_.CreateNetworkFromObject "')) {
    throw 'Legacy sewer command still contains the single-object native batch path.'
}

# August 13 road field pass. Run it from this already-chained final repair so the
# one-click installer cannot finish without the vertical-curve/corridor output fixes.
$roadOutputRepair = Join-Path $root 'scripts\Repair-August13RoadProfileCorridorOutput-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $roadOutputRepair -PathType Leaf)) {
    throw "August 13 road profile/corridor output repair was not found: $roadOutputRepair"
}
& $roadOutputRepair -RepoRoot $root
$global:LASTEXITCODE = 0

Write-Host 'CE Sewer Network from Multiple Polylines now creates one gravity network directly from the complete selected set.' -ForegroundColor Green
Write-Host 'Legacy CE_SEWERNETWORKFROMPOLYLINES redirects to CE_SEWERNETWORKMULTI; native single-object CreateNetworkFromObject is bypassed.' -ForegroundColor Green
Write-Host 'Sewer Production Centre is wired directly to the true multi-source sewer command.' -ForegroundColor Green
Write-Host 'Road final-profile vertical curves, CE-TOP/CE-BOTTOM corridor surfaces and slope-pattern creation are chained into this one-click build.' -ForegroundColor Green
