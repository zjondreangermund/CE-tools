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
function RemoveHousekeepingCall([string]$body,[string]$call) {
    $escaped = [regex]::Escape($call)
    $guarded = '(?ms)^[ \t]*if\s*\(\s*!suppressUndoRecording\s*\)\s*\{\s*try\s*\{\s*' + $escaped + '\s*\}\s*catch\s*\{\s*result\.Warnings\+\+;\s*\}\s*\}\s*(?:\r?\n)?'
    $body = [regex]::Replace($body,$guarded,'',1)

    $legacy = '(?ms)^[ \t]*try\s*\{\s*' + $escaped + '\s*\}\s*catch\s*\{\s*result\.Warnings\+\+;\s*\}\s*(?:\r?\n)?'
    $body = [regex]::Replace($body,$legacy,'',1)

    # Historical installer staging has produced several intermediate shapes. If
    # the exact call survives outside the two known wrappers, remove only the call
    # token and leave the surrounding C# structure intact. An empty try/if block is
    # still valid and the canonical guarded call is inserted below exactly once.
    if ($body.Contains($call)) {
        $body = $body.Replace($call,'')
    }
    return $body
}

$text = ReadText $path
$marker = 'private static UniversalRefreshResult RefreshNow('
$start = $text.IndexOf($marker,[StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Universal refresh RefreshNow method marker was not found.' }
$open = $text.IndexOf('{',$start)
if ($open -lt 0) { throw 'Universal refresh RefreshNow opening brace was not found.' }
$close = FindMatchingBrace $text $open
$body = $text.Substring($open + 1,$close - $open - 1)

$cogoCall = 'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);'
$metadataCall = 'result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document);'
$tableCall = 'CeTablePresentationManager.CenterCeTables(document);'

$body = RemoveHousekeepingCall $body $cogoCall
$body = RemoveHousekeepingCall $body $metadataCall
$body = RemoveHousekeepingCall $body $tableCall

# Do not anchor these housekeeping calls to another optional refresh manager. The
# packaged installer can legitimately remove/reorder those managers before this
# final boundary. _pending=false is the stable end-of-refresh marker inside this
# RefreshNow method, so place the three manual-only calls immediately before it.
$anchorMatch = [regex]::Match($body,'(?m)^[ \t]*_pending\s*=\s*false\s*;\s*$')
if (-not $anchorMatch.Success) {
    throw 'Universal refresh normalization could not locate the stable _pending=false marker inside RefreshNow.'
}

$guardBlock = @'
                if (!suppressUndoRecording) { try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); } catch { result.Warnings++; } }
                if (!suppressUndoRecording) { try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); } catch { result.Warnings++; } }
                if (!suppressUndoRecording) { try { CeTablePresentationManager.CenterCeTables(document); } catch { result.Warnings++; } }
'@
$guardBlock = ($guardBlock -replace "`r?`n","`r`n").Trim("`r","`n")
$body = $body.Substring(0,$anchorMatch.Index) + $guardBlock + "`r`n" + $body.Substring($anchorMatch.Index)

$text = $text.Substring(0,$open + 1) + $body + $text.Substring($close)
WriteText $path $text

$check = ReadText $path
$required = @(
    'if (!suppressUndoRecording) { try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); } catch { result.Warnings++; } }',
    'if (!suppressUndoRecording) { try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); } catch { result.Warnings++; } }',
    'if (!suppressUndoRecording) { try { CeTablePresentationManager.CenterCeTables(document); } catch { result.Warnings++; } }'
)
foreach ($token in $required) {
    $count = ([regex]::Matches($check,[regex]::Escape($token))).Count
    if ($count -ne 1) {
        throw ('Universal refresh pre-normalization expected exactly one canonical guard but found {0}: {1}' -f $count,$token)
    }
}

Write-Host 'September 09 Universal Dynamic Refresh housekeeping pre-normalized with stable guarded calls.' -ForegroundColor Green
