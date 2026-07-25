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
                    Cmd("Clear Imported Model Results", "CE_MODELRESULTCLEAR ", "Erase only CE Tools imported specialist-result graphics after confirmation."))));
        }

        private static RibbonItem[] Row(params RibbonItem[] items)
'@ `
    -Description "add specialist model exchange ribbon commands"

Write-Host "Master Items Phase 4 specialist-model exchange source is wired." -ForegroundColor Green
