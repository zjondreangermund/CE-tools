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

# Civil 3D 2023 does not expose the RibbonRow API used by newer Autodesk ribbon
# assemblies. Keep this repair deliberately granular so unrelated UI changes
# (for example CE- title prefixes) cannot make the compatibility pass fail.
$pluginPath = Join-Path $src 'PluginEntry.cs'
$oldRowFactory = @'
        private static RibbonRow Row(params RibbonItem[] items)
        {
            var row = new RibbonRow();
            foreach (RibbonItem item in items) row.RowItems.Add(item);
            return row;
        }
'@
$newRowFactory = @'
        private static RibbonItem[] Row(params RibbonItem[] items)
        {
            return items;
        }
'@
Replace-RequiredText -Path $pluginPath -Old $oldRowFactory -New $newRowFactory -Description 'replace unsupported RibbonRow factory for Civil 3D 2023'

Replace-RequiredText -Path $pluginPath -Old '            params RibbonRow[] rows)' -New '            params RibbonItem[][] rows)' -Description 'use RibbonItem arrays for Civil 3D 2023 panel rows'

$oldPanelRows = '            foreach (RibbonRow row in rows) source.Rows.Add(row);'
$newPanelRows = @'
            foreach (RibbonItem[] row in rows)
            {
                foreach (RibbonItem item in row)
                    source.Items.Add(item);
            }
'@
Replace-RequiredText -Path $pluginPath -Old $oldPanelRows -New $newPanelRows -Description 'add panel items directly for Civil 3D 2023'

# Latest Windows Civil 3D 2023 compile findings. These are kept here because the
# installer deliberately repairs the downloaded source tree before invoking MSBuild.
$cogoPath = Join-Path $src 'CogoPointProjectStyleCommands.cs'
$oldCogoUsings = @'
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
'@
$newCogoUsings = @'
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
'@
Replace-RequiredText -Path $cogoPath -Old $oldCogoUsings -New $newCogoUsings -Description 'import AutoCAD EditorInput types used by COGO overlap selection'

$usagePath = Join-Path $src 'CommandUsageTracker.cs'
$oldUsageAggregate = @'
                        target.Clicks += source.Clicks;
                        target.TotalSeconds += source.TotalSeconds;
                        target.EstimatedClicksSaved += source.EstimatedClicksSaved;
                        target.EstimatedSecondsSaved += source.EstimatedSecondsSaved;
                        target.IsFavorite = target.IsFavorite || source.IsFavorite;
'@
$newUsageAggregate = @'
                        target.Clicks += source.Clicks;
                        target.TotalSeconds += source.TotalSeconds;
                        // Estimated savings are derived from the aggregated Clicks value.
                        target.IsFavorite = target.IsFavorite || source.IsFavorite;
'@
Replace-RequiredText -Path $usagePath -Old $oldUsageAggregate -New $newUsageAggregate -Description 'avoid assigning derived read-only command usage savings properties'

$vertexPath = Join-Path $src 'VertexSettingOutCommands.cs'
$oldRefreshHeader = @'
        private static void RefreshTable(
            Document document,
            ObjectId tableId,
            out int pointCount,
            out int dimensionCount)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
                throw new InvalidOperationException("No active Civil 3D document is available.");

            using (DocumentLock documentLock = document.LockDocument())
'@
$newRefreshHeader = @'
        private static void RefreshTable(
            Document document,
            ObjectId tableId,
            out int pointCount,
            out int dimensionCount)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
                throw new InvalidOperationException("No active Civil 3D document is available.");
            bool applyCogoStyles = false;

            using (DocumentLock documentLock = document.LockDocument())
'@
Replace-RequiredText -Path $vertexPath -Old $oldRefreshHeader -New $newRefreshHeader -Description 'retain COGO style refresh state outside the table transaction'

$oldLinkRead = @'
                VertexSettingLink link = ReadTableLink(table);
                List<ObjectId> sourceIds = link.SourceHandles
'@
$newLinkRead = @'
                VertexSettingLink link = ReadTableLink(table);
                applyCogoStyles = string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase);
                List<ObjectId> sourceIds = link.SourceHandles
'@
Replace-RequiredText -Path $vertexPath -Old $oldLinkRead -New $newLinkRead -Description 'capture linked COGO output mode before leaving transaction scope'

Replace-RequiredText -Path $vertexPath -Old '            if (string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))' -New '            if (applyCogoStyles)' -Description 'use retained COGO output mode after the transaction'

$oldLevelMethod = @'
        private static void ApplyLevelReferences(
            Database database,
'@
$newLevelMethod = @'
        private static double SampleSurfaceLevel(
            Autodesk.Civil.DatabaseServices.Surface surface,
            Point3d point)
        {
            if (surface == null) return double.NaN;
            try
            {
                double elevation = surface.FindElevationAtXY(point.X, point.Y);
                return double.IsNaN(elevation) || double.IsInfinity(elevation)
                    ? double.NaN
                    : elevation;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static void ApplyLevelReferences(
            Database database,
'@
Replace-RequiredText -Path $vertexPath -Old $oldLevelMethod -New $newLevelMethod -Description 'restore safe Civil surface level sampling helper'

# Fail early if a future source edit prevents any of the compile repairs above.
$cogoText = [System.IO.File]::ReadAllText($cogoPath)
if (-not $cogoText.Contains('using Autodesk.AutoCAD.EditorInput;')) {
    throw 'Civil 3D 2023 repair verification failed: COGO EditorInput import is missing.'
}
$usageText = [System.IO.File]::ReadAllText($usagePath)
if ($usageText.Contains('target.EstimatedClicksSaved +=') -or $usageText.Contains('target.EstimatedSecondsSaved +=')) {
    throw 'Civil 3D 2023 repair verification failed: derived usage properties are still assigned.'
}
$vertexText = [System.IO.File]::ReadAllText($vertexPath)
foreach ($requiredVertexMarker in @(
    'bool applyCogoStyles = false;',
    'applyCogoStyles = string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase);',
    'if (applyCogoStyles)',
    'private static double SampleSurfaceLevel('
)) {
    if (-not $vertexText.Contains($requiredVertexMarker)) {
        throw "Civil 3D 2023 repair verification failed: missing vertex marker: $requiredVertexMarker"
    }
}

Write-Host 'Civil 3D 2023 compatibility repairs completed.' -ForegroundColor Cyan
