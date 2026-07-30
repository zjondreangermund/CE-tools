[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Phase 4 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 4 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$exchangeFile = "src\CE.Tools.Civil3D\SpecialistModelExchangeCommands.cs"
$oldSummary = @'
    internal sealed class ResultImportSummary
    {
        private readonly HashSet<string> _scenarios =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Created { get; set; }
        public int Scenarios { get { return _scenarios.Count; } }
        public double? MinimumDepth { get; private set; }
        public double? MaximumDepth { get; private set; }
        public double? MinimumVelocity { get; private set; }
        public double? MaximumVelocity { get; private set; }

        public void Include(ImportedResultRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.Scenario)) _scenarios.Add(row.Scenario);
            IncludeRange(row.Depth, ref MinimumDepth, ref MaximumDepth);
            IncludeRange(row.Velocity, ref MinimumVelocity, ref MaximumVelocity);
        }

        private static void IncludeRange(double? value, ref double? minimum, ref double? maximum)
        {
            if (!value.HasValue) return;
            minimum = !minimum.HasValue || value.Value < minimum.Value ? value : minimum;
            maximum = !maximum.HasValue || value.Value > maximum.Value ? value : maximum;
        }
    }
'@
$newSummary = @'
    internal sealed class ResultImportSummary
    {
        private readonly HashSet<string> _scenarios =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private double? _minimumDepth;
        private double? _maximumDepth;
        private double? _minimumVelocity;
        private double? _maximumVelocity;

        public int Created { get; set; }
        public int Scenarios { get { return _scenarios.Count; } }
        public double? MinimumDepth { get { return _minimumDepth; } }
        public double? MaximumDepth { get { return _maximumDepth; } }
        public double? MinimumVelocity { get { return _minimumVelocity; } }
        public double? MaximumVelocity { get { return _maximumVelocity; } }

        public void Include(ImportedResultRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.Scenario)) _scenarios.Add(row.Scenario);
            IncludeRange(row.Depth, ref _minimumDepth, ref _maximumDepth);
            IncludeRange(row.Velocity, ref _minimumVelocity, ref _maximumVelocity);
        }

        private static void IncludeRange(double? value, ref double? minimum, ref double? maximum)
        {
            if (!value.HasValue) return;
            minimum = !minimum.HasValue || value.Value < minimum.Value ? value : minimum;
            maximum = !maximum.HasValue || value.Value > maximum.Value ? value : maximum;
        }
    }
'@
Replace-ExactText `
    -RelativePath $exchangeFile `
    -OldText $oldSummary `
    -NewText $newSummary `
    -Description "use backing fields for specialist-result summary ranges"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText @'
            AddProductionPanel(tab);
            return true;
'@ `
    -NewText @'
            AddProductionPanel(tab);
            AddIntegrationPanel(tab);
            return true;
'@ `
    -Description "add the CE Tools integration panel to the ribbon"

Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText @'
        private static RibbonItem[] Row(params RibbonItem[] items)
'@ `
    -NewText @'
        private static void AddIntegrationPanel(RibbonTab tab)
        {
            AddPanel(tab, "CE_TOOLS_CATEGORY_INTEGRATION", "Integration", Row(
                Menu("CE_TOOLS_MODEL_EXCHANGE_MENU", "Specialist Model\nExchange", "Create auditable vendor-neutral exchange packages and import specialist-model result CSV files as removable review graphics.",
                    Cmd("Specialist Model Exchange Tools", "CE_MODELEXCHANGETOOLS ", "Open export, template, result import, information and clear workflows."),
                    Cmd("Export Specialist Model Package", "CE_MODELEXPORTPACKAGE ", "Export selected point/curve geometry to CSV with a JSON manifest, explicit units/CRS metadata and SHA-256 checksums."),
                    Cmd("Create Result CSV Template", "CE_MODELRESULTTEMPLATE ", "Create the documented X/Y/Z/depth/velocity/water-level/scenario/time CSV template."),
                    Cmd("Import Specialist Model Results", "CE_MODELRESULTIMPORT ", "Import a bounded CSV result set as categorised removable review markers with traceable XData."),
                    Cmd("Imported Result Information", "CE_MODELRESULTINFO ", "Review source, scenario, time, coordinates, depth, velocity, water level and screening hazard index."),
                    Cmd("Clear Imported Model Results", "CE_MODELRESULTCLEAR ", "Erase only CE Tools imported specialist-result graphics after confirmation.")),
                Menu("CE_TOOLS_PUMP_SYSTEM_MENU", "Pump & System\nCurves", "Screen manufacturer pump curves against Hazen-Williams and minor-loss system curves, including duty-point, NPSH and folder ranking.",
                    Cmd("Pump and System Curve Tools", "CE_PUMPSYSTEMTOOLS ", "Open template, single-pump review and folder-ranking workflows."),
                    Cmd("Create Pump Curve CSV Template", "CE_PUMPCURVETEMPLATE ", "Create the documented FlowLps/HeadM/efficiency/power/NPSHr manufacturer-curve template."),
                    Cmd("Review One Pump and System Curve", "CE_PUMPSYSTEMREVIEW ", "Calculate the system curve, find the duty point, interpolate manufacturer values and review NPSH margin."),
                    Cmd("Rank Pump Curves in a Folder", "CE_PUMPFOLDERREVIEW ", "Screen and rank up to 100 manufacturer CSV curves against one system definition and optional target flow.")),
                Menu("CE_TOOLS_ROAD_DRIVE_MENU", "Road Drive &\nDesign Review", "Sample a Civil 3D alignment/profile path, screen grades/curvature and export an external-visualisation camera path.",
                    Cmd("Road Drive Review Tools", "CE_ROADDRIVETOOLS ", "Open road-drive review, camera export, information and clear workflows."),
                    Cmd("Review Road Drive and Design", "CE_ROADDRIVEREVIEW ", "Screen sampled grade, grade-change, horizontal radius and speed-based lateral acceleration and create removable issue graphics."),
                    Cmd("Export Road Drive Camera Path", "CE_ROADDRIVEEXPORT ", "Export station/X/Y/Z/heading/pitch frames for external visualisation after verifying coordinate and camera conventions."),
                    Cmd("Road Drive Review Information", "CE_ROADDRIVEINFO ", "Review stored alignment, profile, criteria, issue and source-handle metadata."),
                    Cmd("Clear Road Drive Review", "CE_ROADDRIVECLEAR ", "Erase only tagged CE Tools road-drive paths, issue markers and labels."))));
        }

        private static RibbonItem[] Row(params RibbonItem[] items)
'@ `
    -Description "add exchange pump-system and road-drive ribbon commands"

$testsFile = "tests\CE.Tools.Core.Tests\Program.cs"
Replace-ExactText `
    -RelativePath $testsFile `
    -OldText @'
                PumpReviewChecksNpshMargin();
'@ `
    -NewText @'
                PumpReviewChecksNpshMargin();
                StraightRoadPassesScreening();
                SteepRoadFlagsGrade();
                TightCurveFlagsRadius();
                CameraPathHasHeadingAndPitch();
'@ `
    -Description "run road-drive core tests"

Replace-ExactText `
    -RelativePath $testsFile `
    -OldText @'
        private static HydrologyGridAnalysis CreateSingleOutletAnalysis()
'@ `
    -NewText @'
        private static void StraightRoadPassesScreening()
        {
            var samples = new[]
            {
                new RoadDriveSample(0.0, 0.0, 0.0, 0.0),
                new RoadDriveSample(10.0, 10.0, 0.0, 0.0),
                new RoadDriveSample(20.0, 20.0, 0.0, 0.0)
            };
            RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                samples,
                new RoadDriveCriteria(60.0, 8.0, 6.0, 0.0, 0.25, 2.5, 3.4));

            Equal(0, analysis.Issues.Count);
            Equal(3, analysis.CameraFrames.Count);
            Near(0.0, analysis.CameraFrames[0].HeadingDegrees);
            Near(0.0, analysis.CameraFrames[0].PitchDegrees);
            Pass();
        }

        private static void SteepRoadFlagsGrade()
        {
            var samples = new[]
            {
                new RoadDriveSample(0.0, 0.0, 0.0, 0.0),
                new RoadDriveSample(10.0, 10.0, 0.0, 2.0),
                new RoadDriveSample(20.0, 20.0, 0.0, 4.0)
            };
            RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                samples,
                new RoadDriveCriteria(40.0, 8.0, 100.0, 0.0, 1.0, 2.5, 3.4));

            True(analysis.Issues.Any(issue => issue.Type == RoadDriveIssueType.Grade));
            Near(20.0, analysis.MaximumAbsoluteGradePercent);
            Pass();
        }

        private static void TightCurveFlagsRadius()
        {
            var samples = new[]
            {
                new RoadDriveSample(0.0, 0.0, 0.0, 0.0),
                new RoadDriveSample(10.0, 10.0, 0.0, 0.0),
                new RoadDriveSample(20.0, 10.0, 10.0, 0.0)
            };
            RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                samples,
                new RoadDriveCriteria(60.0, 20.0, 100.0, 50.0, 0.25, 2.5, 3.4));

            True(analysis.MinimumHorizontalRadiusMetres.HasValue);
            Near(Math.Sqrt(50.0), analysis.MinimumHorizontalRadiusMetres.Value);
            True(analysis.Issues.Any(issue => issue.Type == RoadDriveIssueType.HorizontalRadius));
            True(analysis.Issues.Any(issue => issue.Type == RoadDriveIssueType.LateralAcceleration));
            Pass();
        }

        private static void CameraPathHasHeadingAndPitch()
        {
            var samples = new[]
            {
                new RoadDriveSample(0.0, 0.0, 0.0, 0.0),
                new RoadDriveSample(10.0, 10.0, 10.0, 1.0),
                new RoadDriveSample(20.0, 20.0, 20.0, 2.0)
            };
            RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                samples,
                new RoadDriveCriteria(30.0, 20.0, 100.0, 0.0, 1.0, 2.5, 3.4));

            Near(45.0, analysis.CameraFrames[0].HeadingDegrees);
            True(analysis.CameraFrames[0].PitchDegrees > 0.0);
            Equal(analysis.Samples.Count, analysis.CameraFrames.Count);
            Pass();
        }

        private static HydrologyGridAnalysis CreateSingleOutletAnalysis()
'@ `
    -Description "add deterministic road-drive geometry tests"

Write-Host "Master Items Phase 4 exchange pump-system and road-drive source is wired." -ForegroundColor Green
