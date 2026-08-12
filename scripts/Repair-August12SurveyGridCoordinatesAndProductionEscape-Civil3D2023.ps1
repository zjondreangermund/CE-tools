[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 12 grid/window source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}
function ReplaceOnce([string]$text,[string]$old,[string]$new,[string]$description) {
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        throw "Could not integrate $description. Marker not found."
    }
    return $text.Replace($old,$new)
}

# -----------------------------------------------------------------------------
# Survey Site Grid
# Reverse is an actual survey-coordinate display conversion:
#   drawing X -> displayed Y with opposite sign
#   drawing Y -> displayed X with opposite sign
# Geometry remains untouched. Corner labels are independently pulled inward so
# bottom/right annotations do not overlap at the frame corners.
# -----------------------------------------------------------------------------
$siteGrid = Required 'August12SurveySiteGridCommands.cs'
$text = ReadText $siteGrid

$oldDescription = '                "Normal: vertical lines show X and horizontal lines show Y. Reverse swaps the X/Y label convention without changing geometry.",'
$newDescription = '                "Normal: vertical lines show X and horizontal lines show Y. Reverse uses survey display: drawing X becomes Y with the opposite sign, and drawing Y becomes X with the opposite sign. Geometry does not move.",'
$text = ReplaceOnce $text $oldDescription $newDescription 'survey-grid reverse-coordinate description'

$oldLabels = @'
            double modelTextHeight = ModelTextHeight(
                database,
                settings.PaperTextHeight);
            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);

            for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
            {
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        xValues[xIndex],
                        bounds.MinY + insideOffset,
                        bounds.Elevation),
                    modelTextHeight,
                    0.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LX",
                    xIndex,
                    -1);
                created++;
            }

            for (int yIndex = 0; yIndex < yValues.Count; yIndex++)
            {
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        bounds.MaxX - insideOffset,
                        yValues[yIndex],
                        bounds.Elevation),
                    modelTextHeight,
                    Math.PI / 2.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LY",
                    -1,
                    yIndex);
                created++;
            }
'@
$newLabels = @'
            double modelTextHeight = ModelTextHeight(
                database,
                settings.PaperTextHeight);
            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);
            double cornerClearance = Math.Max(
                modelTextHeight * 4.0,
                insideOffset * 2.0);

            for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
            {
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                double displayedCoordinate = settings.ReverseXY
                    ? -xValues[xIndex]
                    : xValues[xIndex];
                double labelX = xValues[xIndex];
                if (xIndex == 0)
                    labelX = Math.Min(
                        bounds.MaxX - insideOffset,
                        labelX + cornerClearance);
                else if (xIndex == xValues.Count - 1)
                    labelX = Math.Max(
                        bounds.MinX + insideOffset,
                        labelX - cornerClearance);

                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + displayedCoordinate.ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        labelX,
                        bounds.MinY + insideOffset,
                        bounds.Elevation),
                    modelTextHeight,
                    0.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LX",
                    xIndex,
                    -1);
                created++;
            }

            for (int yIndex = 0; yIndex < yValues.Count; yIndex++)
            {
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                double displayedCoordinate = settings.ReverseXY
                    ? -yValues[yIndex]
                    : yValues[yIndex];
                double labelY = yValues[yIndex];
                if (yIndex == 0)
                    labelY = Math.Min(
                        bounds.MaxY - insideOffset,
                        labelY + cornerClearance);
                else if (yIndex == yValues.Count - 1)
                    labelY = Math.Max(
                        bounds.MinY + insideOffset,
                        labelY - cornerClearance);

                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + displayedCoordinate.ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        bounds.MaxX - insideOffset,
                        labelY,
                        bounds.Elevation),
                    modelTextHeight,
                    Math.PI / 2.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LY",
                    -1,
                    yIndex);
                created++;
            }
'@
$text = ReplaceOnce $text $oldLabels $newLabels 'survey-grid coordinate reversal and corner-label clearance'
WriteText $siteGrid $text

# -----------------------------------------------------------------------------
# Production-window lifetime
# This pass runs AFTER Repair-August12PersistentProductionUi. That earlier pass
# intentionally made all workflow centres modeless/persistent. The field request
# is narrower: keep ONLY CE-PRODUCTION CENTRE open. Discipline centres are modal
# children again, close after choosing a command, and close when Escape is used.
# -----------------------------------------------------------------------------
$dialogs = Required 'DisciplineWorkflowDialogs.cs'
$text = ReadText $dialogs

if (-not $text.Contains('using System.Windows.Input;')) {
    $text = ReplaceOnce $text `
        'using System.Windows.Controls;' `
        "using System.Windows.Controls;`r`nusing System.Windows.Input;" `
        'WPF keyboard input import'
}

$persistentSelect = @'
        public static void SelectAndRun(
            Document document,
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            if (document == null) return;
            var window = new DisciplineWorkflowWindow(title, note, actions)
            {
                KeepOpenOnAction = true
            };
            AcApplication.ShowModelessWindow(window);
        }
'@
$rootOnlySelect = @'
        public static void SelectAndRun(
            Document document,
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            if (document == null) return;

            bool keepProductionCentreOpen = string.Equals(
                title,
                "CE-PRODUCTION CENTRE",
                StringComparison.OrdinalIgnoreCase);
            var window = new DisciplineWorkflowWindow(title, note, actions)
            {
                // KeepOpenOnAction = true is intentionally restricted to the
                // top CE-PRODUCTION CENTRE through keepProductionCentreOpen.
                KeepOpenOnAction = keepProductionCentreOpen
            };

            if (keepProductionCentreOpen)
            {
                AcApplication.ShowModelessWindow(window);
                return;
            }

            AcApplication.ShowModalWindow(window);
            if (string.IsNullOrWhiteSpace(window.SelectedCommand))
                return;

            string discipline = ResolveModalStyleDiscipline(title);
            if (!string.IsNullOrWhiteSpace(discipline))
            {
                try
                {
                    using (DocumentLock documentLock = document.LockDocument())
                    {
                        August11DisciplineStylePresetManager.ActivateForProduction(
                            document.Database,
                            discipline);
                    }
                }
                catch (System.Exception presetException)
                {
                    try
                    {
                        document.Editor.WriteMessage(
                            "\nCE Tools: {0} style preset could not be pre-activated; the command will still run. {1}",
                            discipline,
                            presetException.Message);
                    }
                    catch { }
                }
            }

            document.SendStringToExecute(
                window.SelectedCommand.Trim() + " ",
                true,
                false,
                true);
        }

        private static string ResolveModalStyleDiscipline(string title)
        {
            string value = (title ?? string.Empty).ToUpperInvariant();
            if (value.Contains("BULK WATER")) return "Bulk Water";
            if (value.Contains("STORMWATER")) return "Stormwater";
            if (value.Contains("SEWER")) return "Sewer";
            if (value.Contains("PLATFORM")) return "Platforms";
            if (value.Contains("PARKING")) return "Parking";
            if (value.Contains("FLOOD")) return "Flood";
            if (value.Contains("SURVEY")) return "Survey";
            if (value.Contains("ROAD")) return "Roads";
            if (value.Contains("WATER")) return "Water";
            return string.Empty;
        }
'@
$text = ReplaceOnce $text $persistentSelect $rootOnlySelect 'root-only persistent Production Centre behavior'

# The persistent root must ignore Escape. Child workflow windows leave Escape
# unhandled so their existing IsCancel Close button closes them normally.
if (-not $text.Contains('PreviewKeyDown += OnWorkflowPreviewKeyDown;')) {
    $classIndex = $text.IndexOf('internal sealed class DisciplineWorkflowWindow : Window', [StringComparison]::Ordinal)
    if ($classIndex -lt 0) { throw 'DisciplineWorkflowWindow class was not found.' }
    $showMarker = '            ShowInTaskbar = false;'
    $showIndex = $text.IndexOf($showMarker, $classIndex, [StringComparison]::Ordinal)
    if ($showIndex -lt 0) { throw 'DisciplineWorkflowWindow ShowInTaskbar marker was not found.' }
    $insertAt = $showIndex + $showMarker.Length
    $text = $text.Substring(0,$insertAt) + "`r`n            PreviewKeyDown += OnWorkflowPreviewKeyDown;" + $text.Substring($insertAt)
}

$actionAnchor = '        private void OnActionClick(object sender, RoutedEventArgs args)'
$escapeMethod = @'
        private void OnWorkflowPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            if (!KeepOpenOnAction) return;

            // CE-PRODUCTION CENTRE is the navigation home. Escape is reserved
            // for cancelling/closing the child window or active Civil 3D prompt.
            args.Handled = true;
        }

'@
if (-not $text.Contains('private void OnWorkflowPreviewKeyDown(')) {
    if (-not $text.Contains($actionAnchor)) {
        throw 'OnActionClick marker for Production Centre Escape handling was not found.'
    }
    $text = $text.Replace($actionAnchor,$escapeMethod + $actionAnchor)
}
WriteText $dialogs $text

# Same-build guards.
$gridText = ReadText $siteGrid
foreach ($marker in @(
    '? -xValues[xIndex]',
    '? -yValues[yIndex]',
    'double cornerClearance = Math.Max(',
    'drawing X becomes Y with the opposite sign')) {
    if (-not $gridText.Contains($marker)) {
        throw "Survey-grid correction marker missing: $marker"
    }
}
$dialogsText = ReadText $dialogs
foreach ($marker in @(
    '"CE-PRODUCTION CENTRE"',
    'KeepOpenOnAction = true is intentionally restricted',
    'KeepOpenOnAction = keepProductionCentreOpen',
    'AcApplication.ShowModelessWindow(window);',
    'AcApplication.ShowModalWindow(window);',
    'ResolveModalStyleDiscipline(title)',
    'August11DisciplineStylePresetManager.ActivateForProduction(',
    'PreviewKeyDown += OnWorkflowPreviewKeyDown;',
    'if (!KeepOpenOnAction) return;',
    'window.SelectedCommand.Trim() + " "')) {
    if (-not $dialogsText.Contains($marker)) {
        throw "Production-window behavior marker missing: $marker"
    }
}

Write-Host 'Survey Site Grid reverse mode now displays Y=-drawing X and X=-drawing Y.' -ForegroundColor Green
Write-Host 'Survey Site Grid corner labels are independently shifted inward to prevent overlap.' -ForegroundColor Green
Write-Host 'Only CE-PRODUCTION CENTRE remains persistent; child workflow windows close on Escape.' -ForegroundColor Green
Write-Host 'Modal discipline children still reactivate their saved style preset before command dispatch.' -ForegroundColor Green
