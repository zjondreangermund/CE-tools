[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$auditPath = Join-Path $src 'August24FieldCompletionCommands.cs'
$sequencePath = Join-Path $src 'SewerSequenceCommands.cs'
$helperPath = Join-Path $src 'September10SewerAuditRuntime.cs'
$profilePath = Join-Path $src 'SewerProductionCommands.cs'
$platformPath = Join-Path $src 'August21PlatformRelativeFatalSafety.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($path in @($auditPath,$sequencePath,$helperPath,$profilePath,$platformPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "September 10 compile-fix input missing: $path"
    }
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function FindMatchingBrace([string]$text,[int]$open) {
    $depth = 0
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $i }
        }
    }
    throw 'September 10 compile-fix could not find a matching closing brace.'
}
function MethodRegion([string]$text,[string]$signature) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "September 10 compile-fix method marker missing: $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "September 10 compile-fix opening brace missing: $signature" }
    $close = FindMatchingBrace $text $open
    return [pscustomobject]@{ Start=$start; Open=$open; Close=$close; Text=$text.Substring($start,$close-$start+1) }
}
function ReplaceMethodRegion([string]$text,$region,[string]$methodText) {
    return $text.Substring(0,$region.Start) + $methodText + $text.Substring($region.Close + 1)
}

# The September 10 staged audit calls a batch helper with nine arguments. The
# September 09 runtime only exposes the interactive two-argument command, so route
# the audit to the dedicated non-interactive compatibility helper instead.
$audit = ReadText $auditPath
$oldAuditCall = 'September09SewerSurfaceRulesRuntime.LinkExistingPartsToSurface('
$newAuditCall = 'September10SewerAuditRuntime.LinkExistingPartsToSurface('
if (-not $audit.Contains('CE TOOLS SEWER ENGINEERING AUDIT')) {
    if ($audit.Contains($oldAuditCall)) {
        $audit = $audit.Replace($oldAuditCall,$newAuditCall)
    }
    if (-not $audit.Contains($newAuditCall)) {
        throw 'September 10 compile-fix could not locate the staged sewer audit surface-link call.'
    }
}
WriteText $auditPath $audit

# The September 10 branch-direction patch reverses CandidatePath lists in-place.
# CandidatePath historically exposes IReadOnlyList<ObjectId>, which cannot be passed
# to ReverseInPlace(IList<T>). Make only CandidatePath's private storage mutable;
# callers still receive the same ordered contents and the topology fix can reverse
# the selected side branch before names are assigned.
$sequence = ReadText $sequencePath
$classMarker = '        private sealed class CandidatePath'
$classAt = $sequence.IndexOf($classMarker,[StringComparison]::Ordinal)
if ($classAt -lt 0) { throw 'September 10 compile-fix CandidatePath marker missing.' }
$open = $sequence.IndexOf('{',$classAt)
if ($open -lt 0) { throw 'September 10 compile-fix CandidatePath opening brace missing.' }
$close = FindMatchingBrace $sequence $open
$candidate = $sequence.Substring($classAt,$close - $classAt + 1)

$candidate = $candidate.Replace('                NodeIds = nodeIds;','                NodeIds = nodeIds.ToList();')
$candidate = $candidate.Replace('                EdgeIds = edgeIds;','                EdgeIds = edgeIds.ToList();')
$candidate = $candidate.Replace('            public IReadOnlyList<ObjectId> NodeIds { get; }','            public List<ObjectId> NodeIds { get; }')
$candidate = $candidate.Replace('            public IReadOnlyList<ObjectId> EdgeIds { get; }','            public List<ObjectId> EdgeIds { get; }')

foreach ($required in @(
    'NodeIds = nodeIds.ToList();',
    'EdgeIds = edgeIds.ToList();',
    'public List<ObjectId> NodeIds { get; }',
    'public List<ObjectId> EdgeIds { get; }')) {
    if (-not $candidate.Contains($required)) {
        throw "September 10 compile-fix CandidatePath normalization failed: $required"
    }
}

$sequence = $sequence.Substring(0,$classAt) + $candidate + $sequence.Substring($close + 1)
if ($sequence.Contains('ReverseInPlace(selected.NodeIds);') -and -not $sequence.Contains('public List<ObjectId> NodeIds { get; }')) {
    throw 'September 10 compile-fix left an IReadOnlyList side-branch reversal.'
}
WriteText $sequencePath $sequence

# September 16 compile recovery: a profile-alignment recovery block was accidentally
# inserted into CE_SEWSEQMAIN, where civilDocument does not exist, while CE_SEWPROFILE
# retained uses of records without declaring/loading that list. Move the recovery
# logic to the profile command only. The rewrite is method-scoped and idempotent.
$profile = ReadText $profilePath
$sequenceRegion = MethodRegion $profile '        public void SequenceWithSelectedMain()'
$sequenceMethod = $sequenceRegion.Text
$badAt = $sequenceMethod.IndexOf('            List<SewerAlignmentRecord> records;',[StringComparison]::Ordinal)
if ($badAt -ge 0) {
    $nextAt = $sequenceMethod.IndexOf('            PromptEntityOptions partOptions',[StringComparison]::Ordinal)
    if ($nextAt -lt 0 -or $nextAt -le $badAt) {
        throw 'September 16 compile-fix could not isolate the misplaced sewer-profile recovery block.'
    }
    $sequenceMethod = $sequenceMethod.Substring(0,$badAt) + $sequenceMethod.Substring($nextAt)
    $profile = ReplaceMethodRegion $profile $sequenceRegion $sequenceMethod
}

$profileRegion = MethodRegion $profile '        public void CreateProfiles()'
$profileMethod = $profileRegion.Text
if (-not $profileMethod.Contains('            List<SewerAlignmentRecord> records;')) {
    $databaseAnchor = '            Database database = document.Database;'
    $databaseAt = $profileMethod.IndexOf($databaseAnchor,[StringComparison]::Ordinal)
    if ($databaseAt -lt 0) {
        throw 'September 16 compile-fix CE_SEWPROFILE database anchor missing.'
    }
    $insertAt = $databaseAt + $databaseAnchor.Length
    $recovery = @'


            List<SewerAlignmentRecord> records;
            using (Transaction alignmentRead = database.TransactionManager.StartTransaction())
                records = ReadGeneratedAlignments(civilDocument, alignmentRead);
            if (records.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SEWPROFILE found no CE sewer alignments. Alignment creation has been queued first; profile creation will resume after CE_SEWALIGN finishes.");
                CeSequentialCommandRunner.Start(
                    document,
                    new[] { "CE_SEWALIGN", "CE_SEWPROFILE" },
                    "CE sewer alignment + profile recovery");
                return;
            }
'@ -replace "`r?`n", "`r`n"
    $profileMethod = $profileMethod.Insert($insertAt,$recovery)
    $profile = ReplaceMethodRegion $profile $profileRegion $profileMethod
}

$sequenceRegion = MethodRegion $profile '        public void SequenceWithSelectedMain()'
$profileRegion = MethodRegion $profile '        public void CreateProfiles()'
if ($sequenceRegion.Text.Contains('ReadGeneratedAlignments(civilDocument')) {
    throw 'September 16 compile-fix left sewer-profile recovery inside CE_SEWSEQMAIN.'
}
foreach ($required in @(
    'List<SewerAlignmentRecord> records;',
    'records = ReadGeneratedAlignments(civilDocument, alignmentRead);',
    'if (records.Count == 0)')) {
    if (-not $profileRegion.Text.Contains($required)) {
        throw "September 16 compile-fix CE_SEWPROFILE recovery guard missing: $required"
    }
}
WriteText $profilePath $profile

# Civil 3D 2023 exposes Entity.ColorIndex as an int, while the stored snapshot and
# ApplyColour helper use the short ACI value. Normalize both the snapshot capture
# and restore call after every earlier staged rewrite so CS0266 cannot reappear.
$platform = ReadText $platformPath
$legacyColourCapture = '                        ColorIndex = child.ColorIndex,'
$compatibleColourCapture = '                        ColorIndex = (short)child.ColorIndex,'
if ($platform.Contains($legacyColourCapture)) {
    $platform = $platform.Replace($legacyColourCapture,$compatibleColourCapture)
}
if (-not $platform.Contains($compatibleColourCapture)) {
    throw 'September 16 compile-fix could not normalize the platform ColorIndex snapshot capture.'
}

$legacyColourCall = '                    ApplyColour(document, candidateId, old.ColorIndex);'
$compatibleColourCall = '                    ApplyColour(document, candidateId, (short)old.ColorIndex);'
if ($platform.Contains($legacyColourCall)) {
    $platform = $platform.Replace($legacyColourCall,$compatibleColourCall)
}
if (-not $platform.Contains($compatibleColourCall)) {
    throw 'September 16 compile-fix could not normalize the platform ColorIndex restore call.'
}
WriteText $platformPath $platform

Write-Host 'September 10/16 Civil 3D 2023 compile compatibility applied.' -ForegroundColor Green
Write-Host ' - Legacy audit surface linking uses the non-interactive helper; the current engineering audit remains read-only.'
Write-Host ' - CandidatePath node/edge storage is mutable for in-place side-branch reversal.'
Write-Host ' - CE_SEWPROFILE owns its alignment recovery list; CE_SEWSEQMAIN no longer references civilDocument.'
Write-Host ' - Platform feature-line ColorIndex capture/restore converts AutoCAD int ACI values to short safely.'
