[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$plugin = Join-Path $root 'src\CE.Tools.Civil3D\PluginEntry.cs'
if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) { throw "PluginEntry source missing: $plugin" }
$text = [System.IO.File]::ReadAllText($plugin)

$old = @'
                        Cmd("Final Comment Closure Centre", "CE_COMMENTCLOSURE ", "Open overlap/restore, table navigation, interoperability, settings and final refresh tools."),
                        Cmd("Smart Annotation Overlap", "CE_OVERLAPSMART ", "Resolve only conflicting annotations with All/Selected scope and restorable original positions."),
'@
$new = @'
                        Cmd("Final Comment Closure Centre", "CE_COMMENTCLOSURE ", "Open overlap/restore, table navigation, interoperability, settings and final refresh tools."),
                        Cmd("Final Annotation Review", "CE_ANNOTATIONREVIEW ", "Open the consolidated COGO/MText/MLeader/table/branch-label final review workflow."),
                        Cmd("MLeader Text Above Leader", "CE_MLEADERTEXTABOVE ", "Keep arrow/reference points fixed and move only MLeader text above the leader tail."),
                        Cmd("Repair Table Grid Lines / Spacing", "CE_TABLEPRESENTATIONFIX ", "Restore visible table grid lines, centred text and readable row/column spacing."),
                        Cmd("Click Linked Table Cell to Source", "CE_TABLECELLZOOM ", "Click a linked CE table data row/cell and zoom/select its source object."),
                        Cmd("Refresh Selected Feature-Line Links", "CE_FLANNOTREFRESHSELECTED ", "Refresh only linked tables and stepped-offset sets belonging to selected feature lines."),
                        Cmd("Branch Labels Separate Layer", "CE_BRANCHLABELLAYER ", "Move detected Sewer/SW/Water branch labels onto CE-BRANCH-LABELS."),
                        Cmd("Smart Annotation Overlap", "CE_OVERLAPSMART ", "Resolve only conflicting annotations with All/Selected scope and restorable original positions."),
'@
if ($text.Contains($new)) {
    Write-Host 'Final annotation review ribbon entries are already integrated.' -ForegroundColor DarkGreen
}
elif ($text.Contains($old)) {
    $text = $text.Replace($old, $new)
    [System.IO.File]::WriteAllText($plugin, $text, [System.Text.UTF8Encoding]::new($false))
    Write-Host 'Integrated final annotation review tools into Drawing Tools ribbon.' -ForegroundColor Green
}
else {
    throw 'Could not find the final comment ribbon insertion marker.'
}

$text = [System.IO.File]::ReadAllText($plugin)
foreach ($command in @('CE_ANNOTATIONREVIEW','CE_MLEADERTEXTABOVE','CE_TABLEPRESENTATIONFIX','CE_TABLECELLZOOM','CE_FLANNOTREFRESHSELECTED','CE_BRANCHLABELLAYER')) {
    if (-not $text.Contains($command)) { throw "Final annotation ribbon verification failed: $command" }
}
Write-Host 'Final annotation ribbon integration passed.' -ForegroundColor Cyan
