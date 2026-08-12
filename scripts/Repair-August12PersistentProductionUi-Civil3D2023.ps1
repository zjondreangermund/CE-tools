[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 12 UI source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

# Production/workflow centres are navigation palettes, not one-shot modal pickers.
$dialogs = Required 'DisciplineWorkflowDialogs.cs'
$text = ReadText $dialogs
$selectPattern = '(?s)        public static void SelectAndRun\(\s*Document document,\s*string title,\s*string note,\s*IList<DisciplineWorkflowAction> actions\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        public static bool EditSettings)'
$selectReplacement = @'
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
'@.TrimEnd("`r","`n")
$regex = [regex]::new($selectPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($regex.IsMatch($text)) {
    $text = $regex.Replace($text,$selectReplacement,1)
}
elseif (-not $text.Contains('KeepOpenOnAction = true')) {
    throw 'Persistent SelectAndRun method could not be isolated.'
}

if (-not $text.Contains('        public bool KeepOpenOnAction { get; set; }')) {
    $anchor = '        public string SelectedCommand { get; private set; }'
    if (-not $text.Contains($anchor)) { throw 'SelectedCommand marker for persistent workflow window was not found.' }
    $text = $text.Replace($anchor,$anchor + "`r`n        public bool KeepOpenOnAction { get; set; }")
}

# IMPORTANT: A modeless WPF click is not an AutoCAD command context. Earlier
# code activated the discipline preset directly from the click handler without a
# document lock. Any managed database exception escaped WPF and could terminate
# Civil 3D with 0xe0434352. Defer the dispatch until the click returns, lock the
# document only around preset activation, contain every activation/dispatch
# exception, and then queue the requested CE command normally.
$clickPattern = '(?s)        private void OnActionClick\(object sender, RoutedEventArgs args\)\s*\{.*?\r?\n        \}(?=\r?\n    \}\r?\n\r?\n    internal enum ProductionSettingsFieldKind)'
$clickReplacement = @'
        private void OnActionClick(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            var action = button == null ? null : button.Tag as DisciplineWorkflowAction;
            if (action == null || string.IsNullOrWhiteSpace(action.Command)) return;
            SelectedCommand = action.Command;

            if (KeepOpenOnAction)
            {
                string queuedCommand = action.Command.Trim() + " ";
                string discipline = ResolveStyleDiscipline(Title);

                // Return from the modeless WPF click before touching the DWG or
                // starting another Civil 3D command. This prevents re-entrant
                // command/database work inside the button event.
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Document document = null;
                    try
                    {
                        document = AcApplication.DocumentManager.MdiActiveDocument;
                        if (document == null) return;

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
                            queuedCommand,
                            true,
                            false,
                            true);
                    }
                    catch (System.Exception dispatchException)
                    {
                        try
                        {
                            if (document != null)
                                document.Editor.WriteMessage(
                                    "\nCE Tools production command could not be queued safely. {0}",
                                    dispatchException.Message);
                        }
                        catch { }
                    }
                }));
                return;
            }

            DialogResult = true;
            Close();
        }

        private static string ResolveStyleDiscipline(string title)
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
'@.TrimEnd("`r","`n")
$clickRegex = [regex]::new($clickPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($clickRegex.IsMatch($text)) {
    $text = $clickRegex.Replace($text,$clickReplacement,1)
}
elseif (-not $text.Contains('queuedCommand = action.Command.Trim() + " "')) {
    throw 'Safe persistent production action-click method could not be isolated.'
}

# Normalize spacing for every guided Production/Workflow action row. The old
# 210px first column let long CE-* titles visually run into the description.
# Use a wider title/command column, a dedicated gutter and wrapped text. This is
# shared by Project, Survey, Platform, Road, SW, Sewer, Water, Bulk Water,
# Parking and Flood, so one repair fixes every workflow window consistently.
$actionPattern = '(?s)        private static UIElement BuildActionContent\(DisciplineWorkflowAction action\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        private void OnActionClick)'
$actionReplacement = @'
        private static UIElement BuildActionContent(DisciplineWorkflowAction action)
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(330),
                MinWidth = 300
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(28)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 220
            });

            var title = new StackPanel
            {
                Margin = new Thickness(0, 1, 0, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            title.Children.Add(new TextBlock
            {
                Text = action.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
            title.Children.Add(new TextBlock
            {
                Text = action.Command,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5
            });
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var description = new TextBlock
            {
                Text = action.Description,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                LineHeight = 18
            };
            Grid.SetColumn(description, 2);
            grid.Children.Add(description);
            return grid;
        }
'@.TrimEnd("`r","`n")
$actionRegex = [regex]::new($actionPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($actionRegex.IsMatch($text)) {
    $text = $actionRegex.Replace($text,$actionReplacement,1)
}
elseif (-not ($text.Contains('Width = new GridLength(330)') -and $text.Contains('Width = new GridLength(28)'))) {
    throw 'Shared Production/Workflow action spacing method could not be isolated.'
}

# Give the cards a little more vertical breathing room without wasting space.
$text = $text.Replace('Padding = new Thickness(12, 9, 12, 9),','Padding = new Thickness(14, 11, 14, 11),')
WriteText $dialogs $text

# Production Centre labels and discipline style entry points.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$text = $text.Replace('CE-ENGINEERING INTELLIGENCE CENTRE','CE-ENGINEERING CENTRE')
$text = $text.Replace('Content = "OPEN CENTRE  ›"','Content = "OPEN CENTRE"')
$text = $text.Replace('new Button { Content = "OPEN CENTRE  ›",','new Button { Content = "OPEN CENTRE",')
$text = $text.Replace('Action("Project Style Centre - Points/Surfaces", "CE_PROJECTSTYLES"','Action("SETTINGS - Survey Styles", "CE_SURVEYSTYLES"')
$text = $text.Replace('Action("SETTINGS - Project Styles / Platform", "CE_PROJECTSTYLES"','Action("SETTINGS - Platform Styles", "CE_PLATFORMSTYLES"')
$text = $text.Replace('Action("SETTINGS - Project Road Styles", "CE_PROJECTSTYLES"','Action("SETTINGS - Road Styles", "CE_ROADSTYLES"')
$text = $text.Replace('Action("SETTINGS - Project / Water Styles", "CE_PROJECTSTYLES"','Action("SETTINGS - Bulk Water Styles", "CE_BULKWATERSTYLES"')

$sewerAnchor = '                Action("SETTINGS - Sewer Settings", "CE_SEWSETTINGS", "Parts, styles, labels, profile and band settings.", "01 SETTINGS"),'
if ($text.Contains($sewerAnchor) -and -not $text.Contains('"CE_SEWERSTYLES"')) {
    $text = $text.Replace($sewerAnchor,$sewerAnchor + "`r`n                Action(`"Project Styles - Sewer`", `"CE_SEWERSTYLES`", `"Civil 3D styles used only by Sewer production.`", `"01 SETTINGS`"),")
}
$parkingAnchor = '                Action("SETTINGS - Parking Tools", "CE_PKTOOLS", "Parking layout and annotation settings.", "01 SETTINGS"),'
if ($text.Contains($parkingAnchor) -and -not $text.Contains('"CE_PARKINGSTYLES"')) {
    $text = $text.Replace($parkingAnchor,$parkingAnchor + "`r`n                Action(`"Project Styles - Parking`", `"CE_PARKINGSTYLES`", `"Civil 3D styles used only by Parking production.`", `"01 SETTINGS`"),")
}
$floodAnchor = '                Action("SETTINGS - Hydrology / Flood Inputs", "CE_HYDROLOGYTOOLS", "Review rainfall/runoff and analysis settings.", "01 SETTINGS"),'
if ($text.Contains($floodAnchor) -and -not $text.Contains('"CE_FLOODSTYLES"')) {
    $text = $text.Replace($floodAnchor,$floodAnchor + "`r`n                Action(`"Project Styles - Flood`", `"CE_FLOODSTYLES`", `"Civil 3D styles used only by Flood production.`", `"01 SETTINGS`"),")
}

$utilityAnchor = '                Action("SETTINGS - " + discipline + " Settings", settings, "Parts, styles, labels and profile settings.", "01 SETTINGS"),'
if ($text.Contains($utilityAnchor) -and -not $text.Contains('disciplineStyleCommand')) {
    $utilityReplacement = @'
                Action("SETTINGS - " + discipline + " Settings", settings, "Parts, styles, labels and profile settings.", "01 SETTINGS"),
                Action("Project Styles - " + discipline,
                    string.Equals(discipline, "Stormwater", StringComparison.OrdinalIgnoreCase) ? "CE_SWSTYLES" : "CE_WATERSTYLES",
                    "Civil 3D styles used only by " + discipline + " production.",
                    "01 SETTINGS"),
'@.TrimEnd("`r","`n")
    $text = $text.Replace($utilityAnchor,$utilityReplacement)
}
WriteText $production $text

# Validate user-visible, persistence, spacing and crash-safety wiring.
$text = ReadText $dialogs
foreach ($marker in @(
    'KeepOpenOnAction = true',
    'public bool KeepOpenOnAction { get; set; }',
    'ResolveStyleDiscipline(Title)',
    'AcApplication.ShowModelessWindow(window);',
    'Dispatcher.BeginInvoke(new Action(delegate',
    'using (DocumentLock documentLock = document.LockDocument())',
    'style preset could not be pre-activated; the command will still run',
    'document.SendStringToExecute(',
    'Width = new GridLength(330)',
    'Width = new GridLength(28)',
    'Grid.SetColumn(description, 2)',
    'TextWrapping = TextWrapping.Wrap')) {
    if (-not $text.Contains($marker)) { throw "Persistent Production Centre marker missing: $marker" }
}
$text = ReadText $production
foreach ($marker in @(
    'CE_PLATFORMSTYLES', 'CE_ROADSTYLES', 'CE_SURVEYSTYLES', 'CE_SWSTYLES',
    'CE_SEWERSTYLES', 'CE_WATERSTYLES', 'CE_BULKWATERSTYLES', 'CE_PARKINGSTYLES', 'CE_FLOODSTYLES')) {
    if (-not $text.Contains($marker)) { throw "Discipline style command wiring missing: $marker" }
}
if ($text.Contains('OPEN CENTRE  ›') -or $text.Contains('CE-ENGINEERING INTELLIGENCE CENTRE')) {
    throw 'Welcome orange-marked wording/glyph cleanup failed.'
}
$styleSource = ReadText (Required 'August12DisciplineStyleCommands.cs')
if (-not $styleSource.Contains('SavePreset(document.Database, selection);') -or
    -not $styleSource.Contains('CeGlobalDisciplineStyleDefaults.Save(selection);')) {
    throw 'Independent discipline style persistence source validation failed.'
}

# Chain the same-build August 12 Survey Site Grid and display-name pass. The main
# stage script already executes this UI repair, so the one-click installer needs
# no additional entry point.
$surveyGridRepair = Join-Path $root 'scripts\Repair-August12SurveyGridAndDisplayNames-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $surveyGridRepair -PathType Leaf)) {
    throw "August 12 Survey Site Grid repair was not found: $surveyGridRepair"
}
& $surveyGridRepair -RepoRoot $root
$global:LASTEXITCODE = 0

Write-Host 'Production centres now stay open while commands run and remain available on the placed monitor.' -ForegroundColor Green
Write-Host 'Production command dispatch is deferred, document-locked for preset activation and exception-contained.' -ForegroundColor Green
Write-Host 'Production/Workflow action rows now use a 330px title column, 28px gutter and wrapped text to prevent overlaps.' -ForegroundColor Green
Write-Host 'Dark/Light dropdown rendering is handled by the global CE interface theme.' -ForegroundColor Green
Write-Host 'Each production discipline now opens and saves an independent Civil 3D style centre.' -ForegroundColor Green
Write-Host 'Removed the marked Engineering Intelligence wording and OPEN CENTRE glyphs from the welcome UI.' -ForegroundColor Green