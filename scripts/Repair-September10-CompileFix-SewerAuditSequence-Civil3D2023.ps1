[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$auditPath = Join-Path $src 'August24FieldCompletionCommands.cs'
$sequencePath = Join-Path $src 'SewerSequenceCommands.cs'
$helperPath = Join-Path $src 'September10SewerAuditRuntime.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($path in @($auditPath,$sequencePath,$helperPath)) {
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

# The September 10 staged audit calls a batch helper with nine arguments. The
# September 09 runtime only exposes the interactive two-argument command, so route
# the audit to the dedicated non-interactive compatibility helper instead.
$audit = ReadText $auditPath
$oldAuditCall = 'September09SewerSurfaceRulesRuntime.LinkExistingPartsToSurface('
$newAuditCall = 'September10SewerAuditRuntime.LinkExistingPartsToSurface('
if ($audit.Contains($oldAuditCall)) {
    $audit = $audit.Replace($oldAuditCall,$newAuditCall)
}
if (-not $audit.Contains($newAuditCall)) {
    throw 'September 10 compile-fix could not locate the staged sewer audit surface-link call.'
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

Write-Host 'September 10 Civil 3D 2023 sewer audit/sequence compile compatibility applied.' -ForegroundColor Green
Write-Host ' - Audit surface linking uses the non-interactive nine-argument helper.'
Write-Host ' - CandidatePath node/edge storage is mutable for in-place side-branch reversal.'
