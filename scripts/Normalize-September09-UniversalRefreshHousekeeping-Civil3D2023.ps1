[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\UniversalDynamicRefreshCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Universal refresh source missing: $path"
}
$utf8 = New-Object System.Text.UTF8Encoding($false)

function ReadText([string]$value) {
    return [System.IO.File]::ReadAllText($value) -replace "`r?`n", "`r`n"
}
function WriteText([string]$value,[string]$text) {
    [System.IO.File]::WriteAllText($value,($text -replace "`r?`n","`r`n"),$utf8)
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
    throw 'Universal refresh RefreshNow closing brace was not found.'
}
function NormalizeHousekeepingCall(
    [string]$body,
    [string]$call,
    [string]$anchor,
    [string]$label) {

    $canonical = '                try { ' + $call + ' }' + "`r`n" +
                 '                catch { result.Warnings++; }'
    if ($body.Contains($canonical)) { return $body }

    $escaped = [regex]::Escape($call)
    $guarded = '(?ms)^[ \t]*if\s*\(\s*!suppressUndoRecording\s*\)\s*\{\s*try\s*\{\s*' + $escaped + '\s*\}\s*catch\s*\{\s*result\.Warnings\+\+;\s*\}\s*\}\s*(?:\r?\n)?'
    $body = [regex]::Replace($body,$guarded,'',1)

    $legacy = '(?ms)^[ \t]*try\s*\{\s*' + $escaped + '\s*\}\s*catch\s*\{\s*result\.Warnings\+\+;\s*\}\s*(?:\r?\n)?'
    $body = [regex]::Replace($body,$legacy,'',1)

    # Last-resort normalization for a historical staged shape not covered above:
    # remove the call-bearing line itself, then put one canonical legacy block in
    # RefreshNow. Any surrounding empty try/if block remains valid C# and the
    # September 09 finalizer will immediately replace this canonical block with
    # the !suppressUndoRecording guarded form.
    if ($body.Contains($call)) {
        $linePattern = '(?m)^[^\r\n]*' + $escaped + '[^\r\n]*(?:\r?\n)?'
        $body = [regex]::Replace($body,$linePattern,'',1)
    }

    $at = $body.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($at -lt 0) {
        throw ('Universal refresh normalization anchor missing for {0}: {1}' -f $label,$anchor)
    }
    return $body.Substring(0,$at) + $canonical + "`r`n" + $body.Substring($at)
}

$text = ReadText $path
$marker = 'private static UniversalRefreshResult RefreshNow('
$start = $text.IndexOf($marker,[StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Universal refresh RefreshNow method marker was not found.' }
$open = $text.IndexOf('{',$start)
if ($open -lt 0) { throw 'Universal refresh RefreshNow opening brace was not found.' }
$close = FindMatchingBrace $text $open
$body = $text.Substring($open + 1,$close - $open - 1)

$body = NormalizeHousekeepingCall $body `
    'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);' `
    '                try { RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true); }' `
    'COGO style refresh'
$body = NormalizeHousekeepingCall $body `
    'result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document);' `
    '                try { FinalFeatureLineReportCommands.RefreshAll(document); }' `
    'metadata refresh'
$body = NormalizeHousekeepingCall $body `
    'CeTablePresentationManager.CenterCeTables(document);' `
    '                _pending = false;' `
    'table presentation refresh'

$text = $text.Substring(0,$open + 1) + $body + $text.Substring($close)
WriteText $path $text

$check = ReadText $path
foreach ($required in @(
    'try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); }',
    'try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }',
    'try { CeTablePresentationManager.CenterCeTables(document); }')) {
    if (-not $check.Contains($required)) {
        throw "Universal refresh pre-normalization failed: $required"
    }
}

Write-Host 'September 09 Universal Dynamic Refresh housekeeping pre-normalized for the sewer/surface finalizer.' -ForegroundColor Green
