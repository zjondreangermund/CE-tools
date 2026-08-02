[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-RequiredText {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Description
    )

    if (-not (Test-Path $Path)) {
        throw "Required source file was not found: $Path"
    }

    $text = Get-Content $Path -Raw
    if ($text.Contains($New)) {
        Write-Host "Already repaired: $Description" -ForegroundColor DarkGreen
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Could not apply repair '$Description' in '$Path'. The expected source text was not found."
    }

    Set-Content -Path $Path -Value $text.Replace($Old, $New) -Encoding UTF8
    Write-Host "Applied repair: $Description" -ForegroundColor Green
}

$src = Join-Path $RepoRoot 'src\CE.Tools.Civil3D'

# Autodesk.Windows.ComponentManager.ApplicationWindow is an HWND (IntPtr) in
# Civil 3D 2023. The ribbon is the WPF UIElement that can receive PreviewKeyDown.
Replace-RequiredText `
    -Path (Join-Path $src 'FloatingToolsWindow.cs') `
    -Old '            _shortcutTarget = ComponentManager.ApplicationWindow as UIElement;' `
    -New '            _shortcutTarget = ComponentManager.Ribbon as UIElement;' `
    -Description 'use the Civil 3D ribbon as the Ctrl+F keyboard target'

# Parking candidate axes are Vector2d values in the recovered V60 model.
$parkingPath = Join-Path $src 'ParkingSkewValidationCommands.cs'
Replace-RequiredText `
    -Path $parkingPath `
    -Old '                        Point2d longAxis = candidate.LongAxis;' `
    -New '                        Vector2d longAxis = candidate.LongAxis;' `
    -Description 'use Vector2d for parking long axis'
Replace-RequiredText `
    -Path $parkingPath `
    -Old '                        Point2d shortAxis = candidate.ShortAxis;' `
    -New '                        Vector2d shortAxis = candidate.ShortAxis;' `
    -Description 'use Vector2d for parking short axis'

$parkingText = Get-Content $parkingPath -Raw
if ($parkingText.Contains('longAxis.GetAsVector()') -or $parkingText.Contains('shortAxis.GetAsVector()')) {
    $parkingText = $parkingText.Replace('longAxis.GetAsVector()', 'longAxis')
    $parkingText = $parkingText.Replace('shortAxis.GetAsVector()', 'shortAxis')
    Set-Content -Path $parkingPath -Value $parkingText -Encoding UTF8
    Write-Host 'Applied repair: use parking axis vectors directly' -ForegroundColor Green
}

# The current report presenter requires explicit headers and data rows.
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
Replace-RequiredText `
    -Path $roadPath `
    -Old $oldRoadCall `
    -New $newRoadCall `
    -Description 'pass road-drive report headers and rows separately'

# Recovered comment workflows legitimately reuse the linked coordinate-table builder.
Replace-RequiredText `
    -Path (Join-Path $src 'SurveyCoordinateWorkflowCommands.cs') `
    -Old '        private static ObjectId CreateLinkedTable(' `
    -New '        internal static ObjectId CreateLinkedTable(' `
    -Description 'expose linked coordinate-table creation inside the CE Tools assembly'

Write-Host 'Civil 3D 2023 compatibility repairs completed.' -ForegroundColor Cyan
