[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\SewerSequenceCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Sewer Sequence source missing for deferred-label compatibility repair: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

$wholeReplacement = @'
                editor.WriteMessage(
                    "\nCE_SEWSEQ complete. Sequencing/renaming is committed; sewer pipe/structure labels are queued as a separate safe command.");
                document.SendStringToExecute("CE_SEWLABELS ", true, false, false);
'@ -replace "`r?`n", "`r`n"

$selectedReplacement = @'
                if (!labelledNetworkId.IsNull)
                {
                    editor.WriteMessage(
                        "\nCE_SEWSEQ selected path complete. Sewer pipe/structure labels are queued as a separate safe command.");
                    document.SendStringToExecute("CE_SEWLABELS ", true, false, false);
                }
'@ -replace "`r?`n", "`r`n"

function Replace-OneOf(
    [string]$source,
    [string[]]$patterns,
    [string]$replacement,
    [string]$label,
    [string]$alreadyMarker) {

    if ($source.Contains($alreadyMarker)) {
        return $source
    }

    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches(
            $source,
            $pattern,
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($matches.Count -eq 1) {
            return [regex]::Replace(
                $source,
                $pattern,
                [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement },
                [System.Text.RegularExpressions.RegexOptions]::Singleline)
        }
        if ($matches.Count -gt 1) {
            throw "Sewer Sequence deferred-label compatibility marker ambiguous for $label ($($matches.Count) matches)."
        }
    }

    throw "Sewer Sequence deferred-label compatibility marker not found: $label"
}

$wholePatterns = @(
    '\s*SewerNetworkLabelCommands\.EnsureLabels\(\s*document,\s*plans\.Select\(plan\s*=>\s*plan\.NetworkId\)\s*\);',
    '\s*editor\.WriteMessage\(\s*"\\nCE_SEWSEQ safety:\s*sequencing/renaming is complete\.[^"]*"\s*\);'
)
$text = Replace-OneOf \
    $text \
    $wholePatterns \
    $wholeReplacement \
    'whole-network deferred labels' \
    'CE_SEWSEQ complete. Sequencing/renaming is committed; sewer pipe/structure labels are queued as a separate safe command.'

$selectedPatterns = @(
    'if\s*\(!labelledNetworkId\.IsNull\)\s*\{\s*SewerNetworkLabelCommands\.EnsureLabels\(\s*document,\s*new\[\]\s*\{\s*labelledNetworkId\s*\}\s*\);\s*\}',
    'if\s*\(!labelledNetworkId\.IsNull\)\s*\{\s*editor\.WriteMessage\(\s*"\\nCE_SEWSEQ safety:\s*selected-path sequencing/renaming is complete\.[^"]*"\s*\);\s*\}'
)
$text = Replace-OneOf \
    $text \
    $selectedPatterns \
    $selectedReplacement \
    'selected-path deferred labels' \
    'CE_SEWSEQ selected path complete. Sewer pipe/structure labels are queued as a separate safe command.'

[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
$deferredPattern = 'document\.SendStringToExecute\("CE_SEWLABELS ",\s*true,\s*false,\s*false\);'
$deferredCount = [regex]::Matches($check,$deferredPattern).Count
if ($deferredCount -lt 2) {
    throw "Sewer Sequence deferred-label compatibility expected two safe CE_SEWLABELS queue points; found $deferredCount."
}
if ($check.Contains('SewerNetworkLabelCommands.EnsureLabels(')) {
    throw 'Sewer Sequence deferred-label compatibility left synchronous EnsureLabels re-entry in CE_SEWSEQ.'
}
foreach ($required in @(
    'CE_SEWSEQ complete. Sequencing/renaming is committed; sewer pipe/structure labels are queued as a separate safe command.',
    'CE_SEWSEQ selected path complete. Sewer pipe/structure labels are queued as a separate safe command.')) {
    if (-not $check.Contains($required)) {
        throw "Sewer Sequence deferred-label compatibility verification failed: $required"
    }
}

Write-Host 'Sewer Sequence deferred CE_SEWLABELS compatibility normalized for the August 20 field-recovery pass.' -ForegroundColor Green
