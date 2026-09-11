[CmdletBinding()]
param([Parameter(Mandatory=$false)][string]$RepoRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
$universalPath = Join-Path $root 'src\CE.Tools.Civil3D\UniversalDynamicRefreshCommands.cs'
$sequencePath = Join-Path $root 'src\CE.Tools.Civil3D\SewerSequenceCommands.cs'
$normalizerPath = Join-Path $root 'scripts\Normalize-September09-UniversalRefreshHousekeeping-Civil3D2023.ps1'

foreach ($path in @($universalPath, $sequencePath, $normalizerPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required regression input is missing: $path"
    }
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`n"
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function MethodBody([string]$text, [string]$marker) {
    $start = $text.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $marker" }
    $open = $text.IndexOf('{', $start)
    if ($open -lt 0) { throw "Opening brace missing: $marker" }
    $depth = 0
    for ($i = $open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $text.Substring($open + 1, $i - $open - 1) }
        }
    }
    throw "Closing brace missing: $marker"
}

function AssertBoundary([string]$text, [string]$label) {
    $resequence = 'SewerNetworkDynamicSequenceCommands.ResequenceAll(document, false);'
    $resequenceCount = ([regex]::Matches($text, [regex]::Escape($resequence))).Count
    Require ($resequenceCount -eq 1) "$label must contain exactly one sewer resequence call; found $resequenceCount."

    $refreshBody = MethodBody $text '        private static UniversalRefreshResult RefreshNow('
    $manualOnly = '(?s)if\s*\(\s*!suppressUndoRecording\s*\)\s*\{\s*try\s*\{\s*SewerNetworkDynamicSequenceCommands\.ResequenceAll\(document,\s*false\);'
    Require ([regex]::IsMatch($refreshBody, $manualOnly)) "$label allows sewer resequencing outside the manual-only refresh guard."

    $willStart = MethodBody $text '        private static void OnCommandWillStart('
    Require ($willStart.Contains('if (IsSewerSequence(command))')) "$label does not suppress pending refresh at CE_SEWSEQ start."
    Require ($willStart.Contains('_pending = false;')) "$label does not clear pending refresh at CE_SEWSEQ start."

    $ended = MethodBody $text '        private static void OnCommandEnded('
    Require ($ended.Contains('if (IsSewerSequence(command))')) "$label does not suppress CE_SEWSEQ command-completion refresh."
    Require ($ended.Contains('_pending = false;')) "$label does not clear sequence-generated object-event refresh requests."

    Require ($text.Contains('return string.Equals(command, "CE_SEWSEQ", StringComparison.OrdinalIgnoreCase);')) "$label is missing the exact CE_SEWSEQ self-refresh boundary."
}

$universal = ReadText $universalPath
$sequence = ReadText $sequencePath
AssertBoundary $universal 'Canonical UniversalDynamicRefreshCommands.cs'

Require ($sequence.Contains('[CommandMethod(') -and $sequence.Contains('"CE_SEWSEQ"')) 'CE_SEWSEQ command registration is missing.'
Require ($sequence.Contains('SewerNetworkLabelCommands.EnsureLabels(')) 'CE_SEWSEQ deferred label handoff was removed.'

# Installer staging runs the September 09 housekeeping normalizer against the same
# source. Prove that this later repair pass does not strip the September 11 boundary.
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ce-sewseq-boundary-' + [Guid]::NewGuid().ToString('N'))
try {
    $tempSource = Join-Path $tempRoot 'src\CE.Tools.Civil3D'
    New-Item -ItemType Directory -Path $tempSource -Force | Out-Null
    Copy-Item -LiteralPath $universalPath -Destination (Join-Path $tempSource 'UniversalDynamicRefreshCommands.cs')
    & $normalizerPath -RepoRoot $tempRoot | Out-Host
    $staged = ReadText (Join-Path $tempSource 'UniversalDynamicRefreshCommands.cs')
    AssertBoundary $staged 'Installer-normalized UniversalDynamicRefreshCommands.cs'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host 'September 11 sewer sequence regen boundary regression passed.' -ForegroundColor Green
