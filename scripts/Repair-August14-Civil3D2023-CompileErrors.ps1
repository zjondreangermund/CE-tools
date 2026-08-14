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
        throw "August 14 Civil 3D 2023 compile source missing: $path"
    }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,$utf8) }

# ---------------------------------------------------------------------------
# 1. Civil 3D 2023 exposes ProfilePVI.EntityBefore / EntityAfter as uint.
#    The profile entity collection API used below expects int indices.
# ---------------------------------------------------------------------------
$road = Required 'August13RoadProfileCorridorOutputFixCommands.cs'
$text = ReadText $road
$text = $text.Replace(
    'int before = pvi.EntityBefore;',
    'int before = checked((int)pvi.EntityBefore);')
$text = $text.Replace(
    'int after = pvi.EntityAfter;',
    'int after = checked((int)pvi.EntityAfter);')
WriteText $road $text

# ---------------------------------------------------------------------------
# 2. August 14 parking numbering: use the actual ParkingNumberLinkStore API and
#    the proven refresh implementation that exists in the 2023 source tree.
# ---------------------------------------------------------------------------
$field = Required 'August14FieldUpgradeCommands.cs'
$text = ReadText $field
$text = $text.Replace(
    'ParkingNumberLinkStore.Link(transaction, bay, text, prefix, number);',
    'ParkingNumberLinkStore.Link(document.Database, transaction, text, bay.ObjectId);')

# Existing-label upgrade originally only wrote the extension-dictionary link.
# Also write the XData link used by the second parking refresh store.
$upgradePattern = '(?m)^(\s*)ParkingNumberLinkCommands\.Link\(transaction, text, bay\);\s*\r?\n(\s*)linked\+\+;'
$upgradeRegex = [regex]::new($upgradePattern)
if ($upgradeRegex.IsMatch($text)) {
    $text = $upgradeRegex.Replace(
        $text,
        '${1}ParkingNumberLinkCommands.Link(transaction, text, bay);' + "`r`n" +
        '${1}ParkingNumberLinkStore.Link(document.Database, transaction, text, bay.ObjectId);' + "`r`n" +
        '${2}linked++;',
        1)
}
elseif (-not ($text.Contains('ParkingNumberLinkStore.Link(document.Database, transaction, text, bay.ObjectId);') -and $text.Contains('linked++;'))) {
    throw 'Parking-number upgrade linkage marker could not be repaired.'
}

$text = $text.Replace(
    'ParkingNumberAutoRefreshManager.QueueRefresh(document.Database);',
    'ParkingNumberLinkCommands.Refresh(document, false);')
WriteText $field $text

# ---------------------------------------------------------------------------
# 3. ProductionStyleCatalog.ReadNames returns List<string> in the 2023 build.
#    The August 14 safe-production dialog code stores those results as arrays.
#    Restrict each match to the first closing parenthesis so rerunning this pass
#    can never produce .ToArray().ToArray().
# ---------------------------------------------------------------------------
$safe = Required 'August14SafeProductionCommands.cs'
$text = ReadText $safe
$totalReadNames = [regex]::Matches($text, 'ProductionStyleCatalog\.ReadNames\(').Count
if ($totalReadNames -lt 6) {
    throw "Expected at least six ProductionStyleCatalog.ReadNames calls in August14SafeProductionCommands.cs; found $totalReadNames."
}
$unconvertedReadNamesPattern = 'ProductionStyleCatalog\.ReadNames\((?<args>[^)]*)\)(?!\.ToArray\(\));'
$unconvertedReadNamesRegex = [regex]::new($unconvertedReadNamesPattern)
if ($unconvertedReadNamesRegex.IsMatch($text)) {
    $text = $unconvertedReadNamesRegex.Replace(
        $text,
        'ProductionStyleCatalog.ReadNames(${args}).ToArray();')
}
WriteText $safe $text

# ---------------------------------------------------------------------------
# 4. Older 2023 staging repairs may change NetworkSourceMarker.Mark from a
#    Database owner to a Document owner. Adapt the new sewer engine to whichever
#    signature is present after all prior staging passes have completed.
# ---------------------------------------------------------------------------
$network = Required 'August11NetworkBatchCommands.cs'
$networkText = ReadText $network
$sewer = Required 'August13SewerMultiSourceNetworkCommands.cs'
$sewerText = ReadText $sewer

$documentSignature = [regex]::IsMatch(
    $networkText,
    'internal\s+static\s+void\s+Mark\s*\(\s*Document\s+document\s*,',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$databaseSignature = [regex]::IsMatch(
    $networkText,
    'internal\s+static\s+void\s+Mark\s*\(\s*Database\s+database\s*,',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

if ($documentSignature) {
    $sewerText = [regex]::Replace(
        $sewerText,
        'NetworkSourceMarker\.Mark\(\s*document\.Database\s*,\s*sourceId\s*,\s*"Sewer"\s*\);',
        'NetworkSourceMarker.Mark(document, sourceId, "Sewer");')
}
elseif ($databaseSignature) {
    $sewerText = [regex]::Replace(
        $sewerText,
        'NetworkSourceMarker\.Mark\(\s*document\s*,\s*sourceId\s*,\s*"Sewer"\s*\);',
        'NetworkSourceMarker.Mark(document.Database, sourceId, "Sewer");')
}
else {
    throw 'NetworkSourceMarker.Mark signature could not be resolved after 2023 staging repairs.'
}
WriteText $sewer $sewerText

# ---------------------------------------------------------------------------
# Same-build verification. Fail here with one precise message instead of letting
# MSBuild report the same compatibility defects one by one.
# ---------------------------------------------------------------------------
$roadVerify = ReadText $road
if ($roadVerify.Contains('int before = pvi.EntityBefore;') -or
    $roadVerify.Contains('int after = pvi.EntityAfter;')) {
    throw 'Civil 3D 2023 ProfilePVI uint-to-int repair did not apply.'
}

$fieldVerify = ReadText $field
foreach ($bad in @(
    'ParkingNumberLinkStore.Link(transaction, bay, text, prefix, number);',
    'ParkingNumberAutoRefreshManager.QueueRefresh(document.Database);')) {
    if ($fieldVerify.Contains($bad)) {
        throw "Parking Civil 3D 2023 compile marker remains: $bad"
    }
}
if (-not $fieldVerify.Contains('ParkingNumberLinkStore.Link(document.Database, transaction, text, bay.ObjectId);')) {
    throw 'Parking-number XData linkage is missing after the 2023 repair.'
}
if (-not $fieldVerify.Contains('ParkingNumberLinkCommands.Refresh(document, false);')) {
    throw 'Parking-number refresh compatibility call is missing after the 2023 repair.'
}

$safeVerify = ReadText $safe
$unconverted = [regex]::Matches(
    $safeVerify,
    'ProductionStyleCatalog\.ReadNames\((?<args>[^)]*)\)(?!\.ToArray\(\));')
if ($unconverted.Count -gt 0) {
    throw "Safe-production List<string>-to-array conversion remains in $($unconverted.Count) call(s)."
}

$sewerVerify = ReadText $sewer
if ($documentSignature -and -not $sewerVerify.Contains('NetworkSourceMarker.Mark(document, sourceId, "Sewer");')) {
    throw 'Sewer network source marker was not adapted to the staged Document signature.'
}
if ($databaseSignature -and -not $documentSignature -and -not $sewerVerify.Contains('NetworkSourceMarker.Mark(document.Database, sourceId, "Sewer");')) {
    throw 'Sewer network source marker was not adapted to the staged Database signature.'
}

Write-Host 'August 14 Civil 3D 2023 final compiler compatibility repair passed.' -ForegroundColor Green
Write-Host 'ProfilePVI entity indices are explicit int casts for the 2023 API.' -ForegroundColor Green
Write-Host 'Dynamic parking labels use both CE link stores and the proven parking refresh path.' -ForegroundColor Green
Write-Host 'Safe-production style catalog results are converted to string arrays.' -ForegroundColor Green
Write-Host ('Sewer network marker call matches staged API owner: ' + $(if ($documentSignature) { 'Document' } else { 'Database' })) -ForegroundColor Green
