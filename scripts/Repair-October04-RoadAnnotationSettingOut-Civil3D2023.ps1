[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (($RepoRoot -as [string]).Trim().Trim('"'))).ProviderPath
$filePath = [IO.Path]::Combine($root, 'src', 'CE.Tools.Civil3D', 'VertexSettingOutCommands.cs')
if (-not [IO.File]::Exists($filePath)) { throw "Road setting-out source is missing: $filePath" }
$text = [IO.File]::ReadAllText([string]$filePath) -replace "`r?`n", "`n"

# Keep the historical refresh/deletion-suppression logic. Restore only the new
# owning-road map and layer overrides after older staged repairs replace methods.
$start = $text.IndexOf('        private static void RefreshTable(')
$end = $text.IndexOf('        private static List<VertexSettingRecord> FlattenAndName(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Vertex refresh boundary is missing.' }
$body = $text.Substring($start, $end - $start)
if (-not $body.Contains('RestoreRoadNumbers(document.Database, transaction, sources, link);')) {
    $anchor = '                List<VertexSettingRecord> records = FlattenAndName('
    if (-not $body.Contains($anchor)) { throw 'Vertex naming anchor is missing.' }
    $body = $body.Replace($anchor, "                RestoreRoadNumbers(document.Database, transaction, sources, link);`n" + $anchor)
}
$body = $body.Replace('link.StartRecordKey);', 'link.StartRecordKey, link.RoadSeeds);')
$text = $text.Substring(0, $start) + $body + $text.Substring($end)

$start = $text.IndexOf('        private static bool UpdateOutput(')
$end = $text.IndexOf('        private static ObjectId CreateDimension(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Vertex output update boundary is missing.' }
$body = $text.Substring($start, $end - $start)
foreach ($entry in @(
    @{ Variable='cogo'; Kind='COGO'; Layer='PointLayer'; Fallback='CE-ROAD-JUNCTION-POINTS' },
    @{ Variable='mtext'; Kind='MText'; Layer='LeaderLayer'; Fallback='CE-ROAD-JUNCTION-LEADERS' }
)) {
    $variable = $entry.Variable
    $assignment = "$variable.LayerId = RoadAnnotationPlacement.Layer($variable.Database, transaction, link.$($entry.Layer), `"$($entry.Fallback)`");"
    if (-not $body.Contains($assignment)) {
        $pattern = '(if \(' + $variable + ' != null && string.Equals\(link.OutputType, "' + $entry.Kind + '", StringComparison.OrdinalIgnoreCase\)\)\s*\{)'
        if (-not [regex]::IsMatch($body, $pattern)) { throw "Vertex $variable layer anchor is missing." }
        $body = [regex]::Replace($body, $pattern, ('$1' + "`n                " + $assignment), 1)
    }
}
$text = $text.Substring(0, $start) + $body + $text.Substring($end)
foreach ($required in @('ROADMAP=', 'POINTLAYER=', 'LEADERLAYER=', 'ARROWSIZE=', 'CreateForJunctions(', 'ReplaceSelectedGroups(')) {
    if (-not $text.Contains($required)) { throw "Road/junction setting-out feature lost during staging: $required" }
}
[IO.File]::WriteAllText([string]$filePath, ($text -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host 'Road/junction owning-road sequence and layer refresh settings preserved.'
