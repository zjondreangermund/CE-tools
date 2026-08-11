[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "CE global UI/style source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

# Initialize the global CE WPF theme before the first startup Workflow Centre opens.
$plugin = Required 'PluginEntry.cs'
$text = ReadText $plugin
$initMarker = @'
        public void Initialize()
        {
            ParkingOptionAutoRefreshManager.Initialize();
'@.TrimEnd("`r","`n")
$initReplacement = @'
        public void Initialize()
        {
            CeInterfaceTheme.Initialize();
            ParkingOptionAutoRefreshManager.Initialize();
'@.TrimEnd("`r","`n")
if ($text.Contains($initMarker)) {
    $text = $text.Replace($initMarker,$initReplacement)
    WriteText $plugin $text
    Write-Host 'Initialized global CE interface theme before startup Workflow Centre.' -ForegroundColor Green
}
elseif ($text.Contains('            CeInterfaceTheme.Initialize();')) {
    Write-Host 'Global CE interface theme startup hook is already present.' -ForegroundColor DarkGreen
}
else { throw 'PluginEntry Initialize marker for global CE theme was not found.' }

# Theme changes must repaint every currently-open CE Tools WPF window, not only welcome.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldWrite = '            try { File.WriteAllText(FilePath, string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark"); }'
$newWrite = @'
            try
            {
                File.WriteAllText(FilePath, string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark");
                CeInterfaceTheme.RefreshOpenWindows();
            }
'@.TrimEnd("`r","`n")
if ($text.Contains($oldWrite)) {
    $text = $text.Replace($oldWrite,$newWrite)
    WriteText $production $text
    Write-Host 'Theme changes now refresh all open CE Tools windows.' -ForegroundColor Green
}
elseif ($text.Contains('CeInterfaceTheme.RefreshOpenWindows();')) {
    Write-Host 'Global CE theme refresh hook is already present.' -ForegroundColor DarkGreen
}
else { throw 'CE theme persistence Write marker was not found.' }

# Workflow/Production Centres must stay open on Escape. Do not use WPF's
# IsCancel behavior on their explicit Close button; only a real button click closes it.
$dialogs = Required 'DisciplineWorkflowDialogs.cs'
$text = ReadText $dialogs
if ($text -match 'Content\s*=\s*"Close",\s*IsCancel\s*=\s*true,') {
    $text = [regex]::Replace(
        $text,
        'Content\s*=\s*"Close",\s*IsCancel\s*=\s*true,',
        "Content = `"Close`",`r`n                IsCancel = false,",
        1)
}
if (-not $text.Contains('            close.Click += delegate { Close(); };')) {
    $closeAnchor = '            Grid.SetRow(close, 3);'
    if (-not $text.Contains($closeAnchor)) { throw 'Workflow Centre explicit Close button marker was not found.' }
    $text = $text.Replace($closeAnchor,'            close.Click += delegate { Close(); };' + "`r`n" + $closeAnchor)
}
WriteText $dialogs $text
$text = ReadText $dialogs
if (-not $text.Contains('                IsCancel = false,') -or -not $text.Contains('            close.Click += delegate { Close(); };')) {
    throw 'Persistent Workflow Centre Escape/Close repair validation failed.'
}
Write-Host 'Workflow/Production Centres now ignore Escape and close only from the explicit Close button.' -ForegroundColor Green

# Make the Project Style Centre expose every production discipline directly.
$styleCentre = Required 'ProjectStyleCenterCommands.cs'
$text = ReadText $styleCentre
$oldDisciplines = @'
        private static readonly string[] Disciplines =
        {
            "Roads",
            "Stormwater",
            "Sewer",
            "Water",
            "Platforms"
        };
'@.TrimEnd("`r","`n")
$newDisciplines = @'
        private static readonly string[] Disciplines =
        {
            "Roads",
            "Stormwater",
            "Sewer",
            "Water",
            "Platforms",
            "Bulk Water",
            "Parking",
            "Flood"
        };
'@.TrimEnd("`r","`n")
if ($text.Contains($oldDisciplines)) {
    $text = $text.Replace($oldDisciplines,$newDisciplines)
    WriteText $styleCentre $text
    Write-Host 'Expanded Project Style Centre to all production disciplines.' -ForegroundColor Green
}
elseif ($text.Contains('            "Bulk Water",') -and $text.Contains('            "Parking",') -and $text.Contains('            "Flood"')) {
    Write-Host 'Project Style Centre already exposes all production disciplines.' -ForegroundColor DarkGreen
}
else { throw 'Project Style Centre discipline-list marker was not found.' }

# Bridge drawing-local discipline presets to the established user-global .ceps file.
$discipline = Required 'August11DisciplineStylePresetCommands.cs'
$text = ReadText $discipline
$oldSave = @'
        internal static void SavePreset(Database database, ProjectStyleSelection selection)
        {
            if (database == null || selection == null || string.IsNullOrWhiteSpace(selection.Discipline)) return;
            WriteRecord(database, PresetName(selection.Discipline), selection);
        }
'@.TrimEnd("`r","`n")
$newSave = @'
        internal static void SavePreset(Database database, ProjectStyleSelection selection)
        {
            if (database == null || selection == null || string.IsNullOrWhiteSpace(selection.Discipline)) return;
            WriteRecord(database, PresetName(selection.Discipline), selection);
            CeGlobalDisciplineStyleDefaults.Save(selection);
        }
'@.TrimEnd("`r","`n")
if ($text.Contains($oldSave)) {
    $text = $text.Replace($oldSave,$newSave)
    WriteText $discipline $text
    Write-Host 'Discipline style presets now persist globally for other drawings.' -ForegroundColor Green
}
elseif ($text.Contains('            CeGlobalDisciplineStyleDefaults.Save(selection);')) {
    Write-Host 'Global discipline-style save bridge is already present.' -ForegroundColor DarkGreen
}
else { throw 'Discipline preset SavePreset marker was not found.' }

$text = ReadText $discipline
$oldRead = @'
        internal static ProjectStyleSelection ReadPreset(Database database, string discipline)
        {
            return ReadRecord(database, PresetName(discipline), discipline);
        }
'@.TrimEnd("`r","`n")
$newRead = @'
        internal static ProjectStyleSelection ReadPreset(Database database, string discipline)
        {
            ProjectStyleSelection local = ReadRecord(database, PresetName(discipline), discipline);
            if (local.Exists) return local;
            ProjectStyleSelection global = CeGlobalDisciplineStyleDefaults.Read(discipline);
            if (global.Exists && database != null)
            {
                WriteRecord(database, PresetName(discipline), global);
                return global;
            }
            return local;
        }
'@.TrimEnd("`r","`n")
if ($text.Contains($oldRead)) {
    $text = $text.Replace($oldRead,$newRead)
    WriteText $discipline $text
    Write-Host 'Production disciplines now restore previous global style presets in new drawings.' -ForegroundColor Green
}
elseif ($text.Contains('ProjectStyleSelection global = CeGlobalDisciplineStyleDefaults.Read(discipline);')) {
    Write-Host 'Global discipline-style restore bridge is already present.' -ForegroundColor DarkGreen
}
else { throw 'Discipline preset ReadPreset marker was not found.' }

# Final structural validation.
$text = ReadText $plugin
if (-not $text.Contains('CeInterfaceTheme.Initialize();')) { throw 'Global CE theme startup hook validation failed.' }
$text = ReadText $production
if (-not $text.Contains('CeInterfaceTheme.RefreshOpenWindows();')) { throw 'Global CE theme refresh validation failed.' }
$text = ReadText $discipline
if (-not $text.Contains('CeGlobalDisciplineStyleDefaults.Save(selection);') -or
    -not $text.Contains('CeGlobalDisciplineStyleDefaults.Read(discipline)')) {
    throw 'Global discipline style persistence validation failed.'
}
$themeSource = ReadText (Required 'CeInterfaceTheme.cs')
if (-not $themeSource.Contains('Keyboard.PreviewKeyDownEvent') -or
    -not $themeSource.Contains('window is FloatingToolsWindow || window is DisciplineWorkflowWindow')) {
    throw 'Global CE Escape protection validation failed.'
}
if (-not (Test-Path -LiteralPath (Required 'CeGlobalDisciplineStyleDefaults.cs'))) { throw 'Global discipline style defaults source missing.' }

Write-Host 'Global CE interface theme / persistent Workflow Centre / cross-drawing style repair passed.' -ForegroundColor Cyan
