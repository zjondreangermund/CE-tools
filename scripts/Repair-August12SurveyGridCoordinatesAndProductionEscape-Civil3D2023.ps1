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
# Reverse means an actual survey-coordinate display conversion:
#   drawing X -> displayed Y with opposite sign
#   drawing Y -> displayed X with opposite sign
# Geometry remains untouched. Corner labels are independently pulled inward so
# the bottom and right annotations cannot occupy the same corner space.
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
# CE-PRODUCTION CENTRE is the persistent home window. Child discipline/workflow
# windows close on Escape and the home window remains available behind them.
# -----------------------------------------------------------------------------
$dialogs = Required 'DisciplineWorkflowDialogs.cs'
$text = ReadText $dialogs

if (-not $text.Contains('using System.Windows.Input;')) {
    $text = ReplaceOnce $text `
        'using System.Windows.Controls;' `
        "using System.Windows.Controls;`r`nusing System.Windows.Input;" `
        'WPF keyboard input import'
}

$staticAnchor = @'
    internal static class DisciplineWorkflowDialogs
    {
'@
$staticInsert = @'
    internal static class DisciplineWorkflowDialogs
    {
        private static DisciplineWorkflowWindow _persistentProductionWindow;

        public static void ShowPersistentProductionCentre(
            Document document,
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            if (document == null) return;

            if (_persistentProductionWindow != null)
            {
                try
                {
                    if (_persistentProductionWindow.IsVisible)
                    {
                        _persistentProductionWindow.Activate();
                        return;
                    }
                }
                catch { }
            }

            var window = new DisciplineWorkflowWindow(
                title,
                note,
                actions,
                true);
            _persistentProductionWindow = window;
            window.Closed += delegate
            {
                if (ReferenceEquals(_persistentProductionWindow, window))
                    _persistentProductionWindow = null;
            };
            AcApplication.ShowModelessWindow(window);
        }

'@
$text = ReplaceOnce $text $staticAnchor $staticInsert 'persistent CE-Production Centre host'

$oldWindowHeader = @'
    internal sealed class DisciplineWorkflowWindow : Window
    {
        public DisciplineWorkflowWindow(
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            Title = title ?? "CE Tools Workflow";
'@
$newWindowHeader = @'
    internal sealed class DisciplineWorkflowWindow : Window
    {
        private readonly bool _persistentCommandHost;

        public DisciplineWorkflowWindow(
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions,
            bool persistentCommandHost = false)
        {
            _persistentCommandHost = persistentCommandHost;
            Title = title ?? "CE Tools Workflow";
'@
$text = ReplaceOnce $text $oldWindowHeader $newWindowHeader 'persistent workflow-window mode'

$oldBackground = '            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));'
$newBackground = @'
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));
            PreviewKeyDown += OnPreviewKeyDown;
'@.TrimEnd("`r","`n")
# Replace only the first occurrence; the ProductionSettingsWindow gets its own
# Escape handler below.
if (-not $text.Contains('PreviewKeyDown += OnPreviewKeyDown;')) {
    $index = $text.IndexOf($oldBackground, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw 'Could not wire workflow-window Escape handling.' }
    $text = $text.Substring(0,$index) + $newBackground + $text.Substring($index + $oldBackground.Length)
}

$oldClose = @'
            var close = new Button
            {
                Content = "Close",
                IsCancel = true,
                MinWidth = 100,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
'@
$newClose = @'
            var close = new Button
            {
                Content = "Close",
                IsCancel = !_persistentCommandHost,
                MinWidth = 100,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            close.Click += OnCloseClick;
'@
$text = ReplaceOnce $text $oldClose $newClose 'persistent Production Centre Close/Escape behavior'

$oldActionClick = @'
        private void OnActionClick(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            var action = button == null ? null : button.Tag as DisciplineWorkflowAction;
            if (action == null || string.IsNullOrWhiteSpace(action.Command)) return;
            SelectedCommand = action.Command;
            DialogResult = true;
            Close();
        }
'@
$newActionClick = @'
        private void OnActionClick(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            var action = button == null ? null : button.Tag as DisciplineWorkflowAction;
            if (action == null || string.IsNullOrWhiteSpace(action.Command)) return;

            if (_persistentCommandHost)
            {
                Document document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document != null)
                {
                    document.SendStringToExecute(
                        action.Command.Trim() + " ",
                        true,
                        false,
                        true);
                }
                return;
            }

            SelectedCommand = action.Command;
            DialogResult = true;
            Close();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (_persistentCommandHost)
                return;
            DialogResult = false;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs args)
        {
            if (_persistentCommandHost)
            {
                Close();
                return;
            }
            DialogResult = false;
            Close();
        }
'@
$text = ReplaceOnce $text $oldActionClick $newActionClick 'workflow action and Escape behavior'

# ProductionSettingsWindow already has a Cancel button, but handle Escape
# explicitly so every production child/settings window closes consistently.
$settingsCtorMarker = @'
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));

            var root = new Grid { Margin = new Thickness(18) };
'@
$settingsCtorNew = @'
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));
            PreviewKeyDown += OnSettingsPreviewKeyDown;

            var root = new Grid { Margin = new Thickness(18) };
'@
# There are two similar constructors; patch the one inside ProductionSettingsWindow
# by searching after the class declaration.
if (-not $text.Contains('PreviewKeyDown += OnSettingsPreviewKeyDown;')) {
    $settingsClass = $text.IndexOf('internal sealed class ProductionSettingsWindow : Window', [StringComparison]::Ordinal)
    if ($settingsClass -lt 0) { throw 'ProductionSettingsWindow class not found.' }
    $ctorIndex = $text.IndexOf($settingsCtorMarker, $settingsClass, [StringComparison]::Ordinal)
    if ($ctorIndex -lt 0) { throw 'ProductionSettingsWindow constructor marker not found.' }
    $text = $text.Substring(0,$ctorIndex) + $settingsCtorNew + $text.Substring($ctorIndex + $settingsCtorMarker.Length)
}

$settingsMethodAnchor = @'
        private static void ShowValidation(string field, string message)
        {
'@
$settingsMethodInsert = @'
        private void OnSettingsPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Accepted = false;
            DialogResult = false;
            Close();
        }

        private static void ShowValidation(string field, string message)
        {
'@
$text = ReplaceOnce $text $settingsMethodAnchor $settingsMethodInsert 'settings-window Escape behavior'
WriteText $dialogs $text

# Make only the top CE-PRODUCTION CENTRE persistent. Discipline centres continue
# using the normal modal SelectAndRun path and therefore close on Escape.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldProductionCall = @'
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-PRODUCTION CENTRE",
'@
$newProductionCall = @'
            DisciplineWorkflowDialogs.ShowPersistentProductionCentre(
                document,
                "CE-PRODUCTION CENTRE",
'@
$text = ReplaceOnce $text $oldProductionCall $newProductionCall 'persistent CE-PRODUCTION CENTRE launch'
WriteText $production $text

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
    'ShowPersistentProductionCentre(',
    'AcApplication.ShowModelessWindow(window);',
    'if (_persistentCommandHost)',
    'args.Key != Key.Escape',
    'PreviewKeyDown += OnSettingsPreviewKeyDown;')) {
    if (-not $dialogsText.Contains($marker)) {
        throw "Production-window behavior marker missing: $marker"
    }
}
$productionText = ReadText $production
if (-not $productionText.Contains('DisciplineWorkflowDialogs.ShowPersistentProductionCentre(')) {
    throw 'CE-PRODUCTION CENTRE is not using the persistent host.'
}

Write-Host 'Survey Site Grid reverse mode now displays Y=-drawing X and X=-drawing Y.' -ForegroundColor Green
Write-Host 'Survey Site Grid corner labels are independently shifted inward to prevent overlap.' -ForegroundColor Green
Write-Host 'CE-PRODUCTION CENTRE remains open; Escape closes child workflow/settings windows.' -ForegroundColor Green
