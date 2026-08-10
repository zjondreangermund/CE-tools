[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Once {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
    if (-not $text.Contains($Old)) { throw "Could not integrate '$Description'. Marker not found in $Path" }
    $text = $text.Replace($Old,$New)
    [System.IO.File]::WriteAllText($Path,$text,[System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$road = Join-Path $src 'RoadProductionCommentCommands.cs'
$sewer = Join-Path $src 'SewerProductionCommands.cs'
$storm = Join-Path $src 'StormwaterProductionCommands.cs'
$water = Join-Path $src 'WaterProductionCommands.cs'
$runtime = Join-Path $src 'ProfileStyleAutoImportRuntime.cs'
foreach ($path in @($road,$sewer,$storm,$water,$runtime)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Profile style auto-import source missing: $path" }
}

$oldRoad = @'
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            List<RoadAlignmentRecord> alignments = ReadRoadAlignments(document.Database, civilDocument);
'@
$newRoad = @'
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            int autoImportedStyles;
            string autoImportMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out autoImportedStyles, out autoImportMessage);
            if (!string.IsNullOrWhiteSpace(autoImportMessage))
                document.Editor.WriteMessage("\nCE road profile style check: " + autoImportMessage);
            List<RoadAlignmentRecord> alignments = ReadRoadAlignments(document.Database, civilDocument);
'@
Replace-Once -Path $road -Old $oldRoad -New $newRoad -Description 'auto-import bundled profile/band styles before Road profiles'

$oldSewer = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            Database database = document.Database;
'@
$newSewer = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            int autoImportedStyles;
            string autoImportMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out autoImportedStyles, out autoImportMessage);
            if (!string.IsNullOrWhiteSpace(autoImportMessage))
                document.Editor.WriteMessage("\nCE sewer profile style check: " + autoImportMessage);
            Database database = document.Database;
'@
Replace-Once -Path $sewer -Old $oldSewer -New $newSewer -Description 'auto-import bundled profile/band styles before Sewer profiles'

$oldStorm = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            Database database = document.Database;
'@
$newStorm = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            int autoImportedStyles;
            string autoImportMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out autoImportedStyles, out autoImportMessage);
            if (!string.IsNullOrWhiteSpace(autoImportMessage))
                document.Editor.WriteMessage("\nCE stormwater profile style check: " + autoImportMessage);
            Database database = document.Database;
'@
Replace-Once -Path $storm -Old $oldStorm -New $newStorm -Description 'auto-import bundled profile/band styles before Stormwater profiles'

$oldWater = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Editor editor = document.Editor;
'@
$newWater = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            int autoImportedStyles;
            string autoImportMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out autoImportedStyles, out autoImportMessage);
            if (!string.IsNullOrWhiteSpace(autoImportMessage))
                document.Editor.WriteMessage("\nCE water profile style check: " + autoImportMessage);

            Editor editor = document.Editor;
'@
Replace-Once -Path $water -Old $oldWater -New $newWater -Description 'auto-import bundled profile/band styles before Water profiles'

foreach ($check in @(
    @{ Path=$road; Name='road' },
    @{ Path=$sewer; Name='sewer' },
    @{ Path=$storm; Name='stormwater' },
    @{ Path=$water; Name='water' }
)) {
    $value = [System.IO.File]::ReadAllText($check.Path)
    if (-not $value.Contains('ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles')) {
        throw "Profile style auto-import verification failed for $($check.Name)."
    }
}
Write-Host 'Automatic profile/band style import integration passed.' -ForegroundColor Cyan
