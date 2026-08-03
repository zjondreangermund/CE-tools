[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-RequiredText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Repair path is empty for: $Description"
    }
    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = [System.IO.Path]::GetFullPath($Path)
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required source file was not found: $Path"
    }

    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) {
        Write-Host "Already repaired: $Description" -ForegroundColor DarkGreen
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Could not apply repair '$Description' in '$Path'. The expected source text was not found."
    }

    [System.IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Applied repair: $Description" -ForegroundColor Green
}

# CMD can pass a repository path ending in a backslash immediately before the
# closing quote. On some Windows/PowerShell combinations that quote becomes part
# of the argument. Remove only surrounding quotes and resolve the existing folder
# without rebuilding the path from an unsafe string.
$cleanRepoRoot = $RepoRoot.Trim().Trim('"')
if ([string]::IsNullOrWhiteSpace($cleanRepoRoot)) {
    throw 'The repository root argument is empty.'
}
if (-not (Test-Path -LiteralPath $cleanRepoRoot -PathType Container)) {
    throw "Repository root was not found: $cleanRepoRoot"
}
$repoFullPath = (Resolve-Path -LiteralPath $cleanRepoRoot).ProviderPath
$src = Join-Path -Path $repoFullPath -ChildPath 'src\CE.Tools.Civil3D'

Replace-RequiredText -Path (Join-Path $src 'FloatingToolsWindow.cs') -Old '            _shortcutTarget = ComponentManager.ApplicationWindow as UIElement;' -New '            _shortcutTarget = ComponentManager.Ribbon as UIElement;' -Description 'use the Civil 3D ribbon as the Ctrl+F keyboard target'

$parkingPath = Join-Path $src 'ParkingSkewValidationCommands.cs'
Replace-RequiredText -Path $parkingPath -Old '                        Point2d longAxis = candidate.LongAxis;' -New '                        Vector2d longAxis = candidate.LongAxis;' -Description 'use Vector2d for parking long axis'
Replace-RequiredText -Path $parkingPath -Old '                        Point2d shortAxis = candidate.ShortAxis;' -New '                        Vector2d shortAxis = candidate.ShortAxis;' -Description 'use Vector2d for parking short axis'

$parkingText = [System.IO.File]::ReadAllText($parkingPath)
if ($parkingText.Contains('longAxis.GetAsVector()') -or $parkingText.Contains('shortAxis.GetAsVector()')) {
    $parkingText = $parkingText.Replace('longAxis.GetAsVector()', 'longAxis')
    $parkingText = $parkingText.Replace('shortAxis.GetAsVector()', 'shortAxis')
    [System.IO.File]::WriteAllText($parkingPath, $parkingText, [System.Text.UTF8Encoding]::new($false))
    Write-Host 'Applied repair: use parking axis vectors directly' -ForegroundColor Green
}

$roadPath = Join-Path $src 'RoadDriveReviewCommands.cs'
$oldRoadCall = @'
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Drive and Design Review",
                    subtitle,
                    rows,
                    "CE TOOLS ROAD DRIVE REVIEW");
'@
$newRoadCall = @'
                IList<string> headers = rows.Count > 0
                    ? rows[0]
                    : new List<string> { "CATEGORY", "STATION", "TYPE", "VALUE", "LIMIT", "SEVERITY", "MESSAGE" };
                IList<IList<string>> reportRows = rows.Count > 1
                    ? rows.Skip(1).ToList()
                    : new List<IList<string>>();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Drive and Design Review",
                    subtitle,
                    headers,
                    reportRows,
                    "CE TOOLS ROAD DRIVE REVIEW");
'@
Replace-RequiredText -Path $roadPath -Old $oldRoadCall -New $newRoadCall -Description 'pass road-drive report headers and rows separately'

Replace-RequiredText -Path (Join-Path $src 'SurveyCoordinateWorkflowCommands.cs') -Old '        private static ObjectId CreateLinkedTable(' -New '        internal static ObjectId CreateLinkedTable(' -Description 'expose linked coordinate-table creation inside the CE Tools assembly'

Write-Host 'Civil 3D 2023 compatibility repairs completed.' -ForegroundColor Cyan
