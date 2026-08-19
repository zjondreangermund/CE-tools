[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August11NetworkBatchCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 19 staged sewer batch source missing: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n"
$marker = 'Passing the COMPLETE selected set to CE_SEWERNETWORKMULTI'

if (-not $text.Contains($marker)) {
    $anchor = '            NetworkFromObjectBatchManager.Start(document, sources, discipline);'
    if (-not $text.Contains($anchor)) {
        throw 'August 19 staged sewer adapter could not locate NetworkFromObjectBatchManager.Start(document, sources, discipline).'
    }

    $replacement = @'
            if (string.Equals(discipline, "Sewer", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.SetImpliedSelection(sources.ToArray());
                document.Editor.WriteMessage(
                    "\nCE sewer multi-source handoff: Source polylines selected={0}. Passing the COMPLETE selected set to CE_SEWERNETWORKMULTI; there is no second one-by-one CreateNetworkFromObject selection prompt.",
                    sources.Count);
                document.SendStringToExecute("CE_SEWERNETWORKMULTI ", true, false, true);
                return;
            }

            NetworkFromObjectBatchManager.Start(document, sources, discipline);
'@ -replace "`n","`r`n"

    $text = $text.Replace($anchor,$replacement.TrimEnd("`r","`n"))
}

if (-not $text.Contains($marker) -or
    -not $text.Contains('document.Editor.SetImpliedSelection(sources.ToArray());') -or
    -not $text.Contains('document.SendStringToExecute("CE_SEWERNETWORKMULTI ", true, false, true);')) {
    throw 'August 19 staged sewer adapter did not install the true multi-source Sewer handoff.'
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 19 staged generic Sewer batch now passes the complete existing selection to CE_SEWERNETWORKMULTI.' -ForegroundColor Green
Write-Host 'Stormwater/Water/Bulk Water retain their existing batch path.' -ForegroundColor Green
