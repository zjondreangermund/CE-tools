[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\RoadProductionCommentCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Road production source missing: $path" }
$text = [System.IO.File]::ReadAllText($path)

# The resolver call is intentionally formatted across multiple lines in C#.
# Verify it with a whitespace-tolerant regex rather than an impossible one-line
# string match. This also keeps the repair idempotent on subsequent builds.
$verificationPattern = '(?s)AugustRoadStyleDefaults\.Resolve\(\s*database,\s*collection,\s*preferred,\s*"Road",\s*out actualName\s*\)'

if (-not [regex]::IsMatch($text, $verificationPattern)) {
    $pattern = '(?s)        private static ObjectId ResolveStyle\(\s*Database database,\s*object collection,\s*string preferred,\s*out string actualName\)\s*\{.*?\n        \}\n\n        private static Dictionary<string, string> ReadProjectStyleSelection'
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) { throw "Could not isolate road ResolveStyle fallback. Matches=$($matches.Count)" }
    $replacement = @'
        private static ObjectId ResolveStyle(
            Database database,
            object collection,
            string preferred,
            out string actualName)
        {
            // Roads must never fall back blindly to the first Civil 3D style.
            // Exact saved/project choice wins; otherwise choose the best road-
            // named style and strongly avoid Sewer/Water/Storm/Pipe styles.
            return AugustRoadStyleDefaults.Resolve(
                database,
                collection,
                preferred,
                "Road",
                out actualName);
        }

        private static Dictionary<string, string> ReadProjectStyleSelection
'@
    $text = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
    Write-Host 'Repaired road style fallback so utility styles are not selected for roads.' -ForegroundColor Green
}
else {
    Write-Host 'Road style fallback is already repaired.' -ForegroundColor DarkGreen
}

if (-not [regex]::IsMatch($text, $verificationPattern)) {
    throw 'Road style fallback repair verification failed.'
}

[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Road style fallback compatibility repair passed.' -ForegroundColor Cyan
