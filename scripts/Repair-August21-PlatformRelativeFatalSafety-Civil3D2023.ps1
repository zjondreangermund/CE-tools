[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Source([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Platform/relative fatal-safety prerequisite missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function FindMatchingBrace([string]$text,[int]$openIndex) {
    if ($openIndex -lt 0 -or $openIndex -ge $text.Length -or $text[$openIndex] -ne '{') {
        throw 'Brace scanner did not receive an opening brace.'
    }
    $depth = 0
    $inString = $false
    $inVerbatim = $false
    $inChar = $false
    $lineComment = $false
    $blockComment = $false
    for ($i = $openIndex; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        $n = if ($i + 1 -lt $text.Length) { $text[$i + 1] } else { [char]0 }

        if ($lineComment) {
            if ($c -eq "`n") { $lineComment = $false }
            continue
        }
        if ($blockComment) {
            if ($c -eq '*' -and $n -eq '/') { $blockComment = $false; $i++ }
            continue
        }
        if ($inVerbatim) {
            if ($c -eq '"') {
                if ($n -eq '"') { $i++; continue }
                $inVerbatim = $false
            }
            continue
        }
        if ($inString) {
            if ($c -eq '\\') { $i++; continue }
            if ($c -eq '"') { $inString = $false }
            continue
        }
        if ($inChar) {
            if ($c -eq '\\') { $i++; continue }
            if ($c -eq "'") { $inChar = $false }
            continue
        }

        if ($c -eq '/' -and $n -eq '/') { $lineComment = $true; $i++; continue }
        if ($c -eq '/' -and $n -eq '*') { $blockComment = $true; $i++; continue }
        if ($c -eq '@' -and $n -eq '"') { $inVerbatim = $true; $i++; continue }
        if ($c -eq '"') { $inString = $true; continue }
        if ($c -eq "'") { $inChar = $true; continue }
        if ($c -eq '{') { $depth++; continue }
        if ($c -eq '}') {
            $depth--
            if ($depth -eq 0) { return $i }
            if ($depth -lt 0) { break }
        }
    }
    throw 'Could not find matching closing brace.'
}

function ReplaceMethodBody(
    [string]$text,
    [string]$marker,
    [string]$body,
    [string]$label) {

    $start = $text.IndexOf($marker,[System.StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Could not locate $label marker: $marker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Could not locate $label opening brace." }
    $close = FindMatchingBrace $text $open
    $replacement = "{`r`n" + ($body.Trim("`r","`n") -replace "`r?`n","`r`n") + "`r`n        }"
    return $text.Substring(0,$open) + $replacement + $text.Substring($close + 1)
}

$helper = Source 'August21PlatformRelativeFatalSafety.cs'
$surfaceHelper = Source 'August21SurfaceSafety.cs'
if (-not (ReadText $helper).Contains('internal static class August21PlatformRelativeFatalSafety')) {
    throw 'Platform/relative fatal-safety helper class is missing.'
}
if (-not (ReadText $surfaceHelper).Contains('TryApplyFeatureLineElevations')) {
    throw 'Surface fatal-safety helper is missing.'
}

# -----------------------------------------------------------------------------
# Linked relative feature lines: safe committed temporary creation + candidate-
# first rebuild. Preserve the existing command UI and XRecord format.
# -----------------------------------------------------------------------------
$relativePath = Source 'FeatureLineRelativeCommands.cs'
$relative = ReadText $relativePath
$relativeCreate = @'
            Editor editor = document.Editor;
            PromptEntityResult sourceResult = PromptFeatureLine(editor, "\nSelect SOURCE feature line: ");
            if (sourceResult.Status != PromptStatus.OK) return;

            string defaultPrefix;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        transaction, sourceResult.ObjectId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    defaultPrefix = string.IsNullOrWhiteSpace(source.Name)
                        ? "FeatureLine-STEP"
                        : source.Name + "-STEP";
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLREL cancelled. " + exception.Message);
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Linked Stepped Feature Lines",
                "Create a complete linked offset set from one source. The set is rebuilt automatically by CE Tools when the source drawing geometry changes.");
            settings.AddPositiveDouble(
                "HorizontalStep", "01 Stepped offsets", "Horizontal step", 1.0,
                "Drawing-unit offset between successive linked feature lines.");
            settings.AddText(
                "VerticalStep", "01 Stepped offsets", "Vertical step", "0.000",
                "Elevation difference per step. Use a negative value for steps below the source.");
            settings.AddPositiveInteger(
                "Count", "01 Stepped offsets", "Number of offsets", 1,
                "Create this many linked stepped feature lines from the selected source.");
            settings.AddText(
                "Prefix", "02 Naming", "Feature-line name prefix", defaultPrefix,
                "Names are created as Prefix-1, Prefix-2, and so on.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double horizontalStep = settings.Double("HorizontalStep", 1.0);
            double verticalStep;
            if (!ProductionSettingsDialogModel.TryDouble(
                    settings.Text("VerticalStep"), out verticalStep))
            {
                editor.WriteMessage("\nCE_FLREL cancelled. Vertical step must be a number.");
                return;
            }
            int count = settings.Integer("Count", 1);
            string prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix"))
                ? defaultPrefix
                : settings.Text("Prefix");

            PromptPointResult sideResult = editor.GetPoint(
                "\nPick the side on which the stepped offsets must be created: ");
            if (sideResult.Status != PromptStatus.OK) return;
            Point3d sidePoint = sideResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            double sign;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        transaction, sourceResult.ObjectId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        sign = ResolveOffsetSign(plan, horizontalStep, sidePoint);
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLREL cancelled while preparing the offset. " + exception.Message);
                return;
            }

            try
            {
                int created = August21PlatformRelativeFatalSafety.CreateRelativeSet(
                    document,
                    sourceResult.ObjectId,
                    sign,
                    horizontalStep,
                    verticalStep,
                    count,
                    prefix);
                editor.WriteMessage(
                    "\nCE_FLREL complete. Linked feature lines created: {0}. Automatic linked refresh is enabled; CE_FLRELUPDATE also rebuilds this complete source set on demand.",
                    created);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLREL stopped safely. Existing/source geometry was kept. " + exception.Message);
            }
'@
$relative = ReplaceMethodBody $relative 'private static void Create(Document document)' $relativeCreate 'FeatureLineRelative.Create'
$relativeRebuild = @'
            if (document == null || sourceId.IsNull) return 0;
            return August21PlatformRelativeFatalSafety.RebuildRelativeSource(document, sourceId);
'@
$relative = ReplaceMethodBody $relative 'private static int RebuildChildren(' $relativeRebuild 'FeatureLineRelative.RebuildChildren'
$disabledChild = @'
            throw new InvalidOperationException(
                "Unsafe in-transaction FeatureLine.Create path disabled by the August 21 fatal-safety boundary.");
'@
$relative = ReplaceMethodBody $relative 'private static ObjectId CreateChild(' $disabledChild 'FeatureLineRelative.CreateChild'
WriteText $relativePath $relative

# -----------------------------------------------------------------------------
# Platform production: committed temporary offsets; safe surface drape; safe
# automatic refresh entrypoint. The annotation/table part of RefreshAll remains.
# -----------------------------------------------------------------------------
$platformPath = Source 'PlatformProductionCommands.cs'
$platform = ReadText $platformPath
$platformSteps = @'
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Platform Stepped Offsets",
                "Closed platforms choose the outward offset side automatically. Open source feature lines use the positive offset side.");
            settings.AddPositiveDouble("Horizontal", "Steps", "Horizontal step", 1.0, "Horizontal offset per step.");
            settings.AddText("Vertical", "Steps", "Vertical step", "-0.500", "Signed vertical difference per step.");
            settings.AddPositiveInteger("Count", "Steps", "Step count", 1, "Number of linked children per source.");
            settings.AddText("Suffix", "Naming", "Child suffix", "STEP", "Used in generated feature-line names.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double vertical;
            if (!TryParseDouble(settings.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_PLATFORMSTEPOFFSETS cancelled. Enter a valid vertical step.");
                return;
            }
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform source feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double horizontal = Math.Max(0.001, settings.Double("Horizontal", 1.0));
            int count = Math.Max(1, settings.Integer("Count", 1));
            string suffix = string.IsNullOrWhiteSpace(settings.Text("Suffix")) ? "STEP" : settings.Text("Suffix").Trim();
            August21PlatformRelativeFatalSafety.PlatformStepResult result =
                August21PlatformRelativeFatalSafety.CreatePlatformSteps(
                    document,
                    selection.Value.GetObjectIds(),
                    horizontal,
                    vertical,
                    count,
                    suffix);
            document.Editor.Regen();
            UniversalDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMSTEPOFFSETS complete. Linked steps={0}; skipped={1}.",
                result.Created,
                result.Skipped);
'@
$platform = ReplaceMethodBody $platform 'public void StepOffsets()' $platformSteps 'Platform.StepOffsets'

$platformDrape = @'
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPE cancelled. No Civil 3D surfaces were found.");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drape Platform Steps",
                "The selected surface controls the draped step. The linked source platform is then updated and its other stepped offsets are rebuilt.");
            settings.AddChoice("Surface", "Surface", "Target / surveyed surface", surfaces[0].Name, "Select a controlling surface.", surfaces.Select(s => s.Name));
            settings.AddChoice("Intermediate", "Surface", "Intermediate surface points", "No", "Allow Civil 3D to add intermediate surface points.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            SurfaceChoice surface = surfaces.FirstOrDefault(s => string.Equals(s.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (surface == null) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect linked stepped-offset feature lines to drape: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            bool intermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            int linked = August21PlatformRelativeFatalSafety.DrapeSelection(
                document,
                selection.Value.GetObjectIds(),
                surface.Name,
                surface.ObjectId,
                intermediate);
            RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAPE complete. Dynamic surface links={0}.", linked);
'@
$platform = ReplaceMethodBody $platform 'public void Drape()' $platformDrape 'Platform.Drape'

$platformRefresh = @'
            if (document == null) return 0;
            int refreshed = August21PlatformRelativeFatalSafety.RefreshPlatformDrapes(document);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    string sourceHandle;
                    string label;
                    if (text != null && TryReadName(text, transaction, out sourceHandle, out label))
                    {
                        CivilFeatureLine source = OpenFeatureLine(transaction, Resolve(document.Database, sourceHandle), OpenMode.ForRead);
                        if (source != null)
                        {
                            text.UpgradeOpen();
                            text.Location = Centre(source);
                            text.Contents = LabelText(label, source);
                            refreshed++;
                        }
                        continue;
                    }
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    TableLink link;
                    if (table != null && TryReadTable(table, transaction, out link))
                    {
                        table.UpgradeOpen();
                        PopulateTable(document.Database, transaction, table, link);
                        refreshed++;
                    }
                }
                transaction.Commit();
            }
            return refreshed;
'@
$platform = ReplaceMethodBody $platform 'internal static int RefreshAll(Document document)' $platformRefresh 'Platform.RefreshAll'
$platform = ReplaceMethodBody $platform 'private static ObjectId CreateOffsetFeatureLine(' $disabledChild 'Platform.CreateOffsetFeatureLine'

# Defence in depth if a future staging pass re-enables this legacy watcher: Idle
# may enqueue a modal command but must never mutate Civil objects directly.
$platformIdle = @'
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!_pending || _busy || active == null || (DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 1.5) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int activeCommands = Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture);
            if (activeCommands != 0) return;
            _busy = true;
            try
            {
                _pending = false;
                active.SendStringToExecute("CE_PLATFORMREFRESH ", true, false, false);
            }
            catch
            {
                _pending = true;
            }
            finally
            {
                _busy = false;
            }
'@
$platform = ReplaceMethodBody $platform 'private static void Idle(object sender, EventArgs e)' $platformIdle 'PlatformDynamicRefreshManager.Idle'
$platformChanged = @'
            if (_busy || e == null || e.DBObject == null) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands) &&
                commands.IndexOf("CE_PLATFORMREFRESH", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (e.DBObject is CivilSurface || e.DBObject is CivilFeatureLine || e.DBObject is Table) Queue();
'@
$platform = ReplaceMethodBody $platform 'private static void Changed(object sender, ObjectEventArgs e)' $platformChanged 'PlatformDynamicRefreshManager.Changed'
$platformErased = @'
            if (_busy) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands) &&
                commands.IndexOf("CE_PLATFORMREFRESH", StringComparison.OrdinalIgnoreCase) >= 0) return;
            Queue();
'@
$platform = ReplaceMethodBody $platform 'private static void Erased(object sender, ObjectErasedEventArgs e)' $platformErased 'PlatformDynamicRefreshManager.Erased'
WriteText $platformPath $platform

# -----------------------------------------------------------------------------
# Stepped join: sources stay untouched and the 3D temporary is committed before
# the native Civil 3D FeatureLine.Create call.
# -----------------------------------------------------------------------------
$joinPath = Source 'FeatureLineSteppedJoinCommands.cs'
$join = ReadText $joinPath
$joinBody = @'
            August21PlatformRelativeFatalSafety.CreateJoinedFeatureLine(
                document,
                selectedIds,
                gapTolerance,
                requestedName,
                out pieceCount,
                out vertexCount,
                out largestGap,
                out outputName);
'@
$join = ReplaceMethodBody $join 'private static void CreateJoinedFeatureLine(' $joinBody 'FeatureLineSteppedJoin.CreateJoinedFeatureLine'
WriteText $joinPath $join

# -----------------------------------------------------------------------------
# Universal automatic refresh: Idle only enqueues CE_DYNAMICREFRESHALL. Actual
# Civil mutations therefore happen in normal command context. Platform refresh is
# folded into that command so the dormant legacy Platform watcher is unnecessary.
# -----------------------------------------------------------------------------
$universalPath = Source 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath
$universalIdle = @'
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!Enabled || !_pending || _busy || _undoRedoActive || active == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalSeconds < DelaySeconds) return;
            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;
            try
            {
                _pending = false;
                _lastRefreshUtc = DateTime.UtcNow;
                active.SendStringToExecute("CE_DYNAMICREFRESHALL ", true, false, false);
            }
            catch
            {
                _pending = true;
            }
'@
$universal = ReplaceMethodBody $universal 'private static void OnIdle(object sender, EventArgs e)' $universalIdle 'UniversalDynamicRefreshManager.OnIdle'
$universalEnded = @'
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (string.Equals(command, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase))
            {
                _pending = false;
                _lastRefreshUtc = DateTime.UtcNow;
                return;
            }
            if (IsUndoRedo(command))
            {
                _undoRedoActive = false;
                _pending = false;
                _lastChangeUtc = DateTime.UtcNow;
                return;
            }
            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("MOVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("STRETCH", StringComparison.OrdinalIgnoreCase) >= 0)
                Queue();
'@
$universal = ReplaceMethodBody $universal 'private static void OnCommandEnded(object sender, CommandEventArgs e)' $universalEnded 'UniversalDynamicRefreshManager.OnCommandEnded'
if (-not $universal.Contains('PlatformProductionCommands.RefreshAll(document);')) {
    $anchor = '                try { FinalFeatureLineReportCommands.RefreshAll(document); }' + "`r`n" +
              '                catch { result.Warnings++; }'
    if (-not $universal.Contains($anchor)) {
        throw 'Universal refresh platform insertion anchor is missing.'
    }
    $replacement = $anchor + "`r`n" +
        '                try { PlatformProductionCommands.RefreshAll(document); }' + "`r`n" +
        '                catch { result.Warnings++; }'
    $universal = $universal.Replace($anchor,$replacement)
}
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# Sewer automatic sequence fallback: when Universal Refresh is disabled, Idle
# enqueues CE_SEWAUTOSEQALL rather than editing a Civil network from Idle.
# Database events raised by that command are ignored to avoid a requeue loop.
# -----------------------------------------------------------------------------
$sewerPath = Source 'SewerNetworkDynamicSequenceManager.cs'
$sewer = ReadText $sewerPath
$sewerIdle = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (!Enabled || !_pending || _busy || document == null) return;
            if (UniversalDynamicRefreshManager.Enabled)
            {
                UniversalDynamicRefreshManager.Queue();
                _pending = false;
                return;
            }
            if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 1200.0) return;
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 900.0) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;

            _busy = true;
            try
            {
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;
                document.SendStringToExecute("CE_SEWAUTOSEQALL ", true, false, false);
            }
            catch
            {
                _pending = true;
            }
            finally
            {
                _busy = false;
            }
'@
$sewer = ReplaceMethodBody $sewer 'private static void OnIdle(object sender, EventArgs eventArgs)' $sewerIdle 'SewerNetworkDynamicSequenceManager.OnIdle'
$sewerErased = @'
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames) &&
                commandNames.IndexOf("CE_SEWAUTOSEQ", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (eventArgs.DBObject is CivilPipe ||
                eventArgs.DBObject is CivilStructure ||
                eventArgs.DBObject is CivilNetwork)
            {
                Queue();
            }
'@
$sewer = ReplaceMethodBody $sewer 'private static void OnObjectErased(' $sewerErased 'SewerNetworkDynamicSequenceManager.OnObjectErased'
$sewerChanged = @'
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames) &&
                commandNames.IndexOf("CE_SEWAUTOSEQ", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (eventArgs.DBObject is CivilPipe ||
                eventArgs.DBObject is CivilStructure ||
                eventArgs.DBObject is CivilNetwork)
            {
                Queue();
            }
'@
$sewer = ReplaceMethodBody $sewer 'private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)' $sewerChanged 'SewerNetworkDynamicSequenceManager.OnObjectChanged'
WriteText $sewerPath $sewer

# Do not let command-ended helpers immediately requeue the two commands that are
# themselves the safe automatic refresh workers.
$autoPath = Source 'AugustAutomaticRefreshManager.cs'
$auto = ReadText $autoPath
$autoEnded = @'
            string name = ReadCommandName(e);
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(name, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_PLATFORMREFRESH", StringComparison.OrdinalIgnoreCase))
                return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
'@
# Historical staging normalizes this parameter to e before the final boundary.
$auto = ReplaceMethodBody $auto 'private static void OnCommandEnded(object sender, CommandEventArgs e)' $autoEnded 'AugustAutomaticRefreshManager.OnCommandEnded'
WriteText $autoPath $auto

# -----------------------------------------------------------------------------
# Final assertions. These are deliberately narrow and fatal: if a historical
# staging repair changes the source shape, compilation must stop instead of
# silently restoring one of the known crash-prone paths.
# -----------------------------------------------------------------------------
$relativeCheck = ReadText $relativePath
$platformCheck = ReadText $platformPath
$joinCheck = ReadText $joinPath
$universalCheck = ReadText $universalPath
$sewerCheck = ReadText $sewerPath
$autoCheck = ReadText $autoPath

foreach ($required in @(
    'August21PlatformRelativeFatalSafety.CreateRelativeSet(',
    'August21PlatformRelativeFatalSafety.RebuildRelativeSource(document, sourceId)',
    'Unsafe in-transaction FeatureLine.Create path disabled')) {
    if (-not $relativeCheck.Contains($required)) { throw "Relative feature-line final safety missing: $required" }
}
foreach ($required in @(
    'August21PlatformRelativeFatalSafety.CreatePlatformSteps(',
    'August21PlatformRelativeFatalSafety.DrapeSelection(',
    'August21PlatformRelativeFatalSafety.RefreshPlatformDrapes(document)',
    'active.SendStringToExecute("CE_PLATFORMREFRESH "')) {
    if (-not $platformCheck.Contains($required)) { throw "Platform final safety missing: $required" }
}
if ($platformCheck.Contains('child.AssignElevationsFromSurface(surface.ObjectId, intermediate);') -or
    $platformCheck.Contains('rebuilt.AssignElevationsFromSurface(surfaceId, snapshot.Link.Intermediate);')) {
    throw 'Unsafe Platform AssignElevationsFromSurface path survived the final safety pass.'
}
if (-not $joinCheck.Contains('August21PlatformRelativeFatalSafety.CreateJoinedFeatureLine(')) {
    throw 'Stepped join final safety delegation is missing.'
}
if ($joinCheck.Contains('CivilFeatureLine.Create(outputName, sourcePolyline.ObjectId)')) {
    throw 'Unsafe stepped-join native creation path survived the final safety pass.'
}
foreach ($required in @(
    'active.SendStringToExecute("CE_DYNAMICREFRESHALL "',
    'PlatformProductionCommands.RefreshAll(document);',
    'string.Equals(command, "CE_DYNAMICREFRESHALL"')) {
    if (-not $universalCheck.Contains($required)) { throw "Universal final safety missing: $required" }
}
if ($universalCheck.Contains('RefreshNow(active, true);')) {
    throw 'Universal Refresh still mutates Civil objects directly from Idle.'
}
if (-not $sewerCheck.Contains('document.SendStringToExecute("CE_SEWAUTOSEQALL "')) {
    throw 'Sewer automatic sequence fallback still lacks command-context queuing.'
}
if ($sewerCheck.Contains('SewerNetworkDynamicSequenceCommands.ResequenceAll(' + "`r`n" + '                    document,')) {
    throw 'Sewer automatic sequence still contains the known direct Idle resequence call.'
}
if (-not $autoCheck.Contains('string.Equals(name, "CE_DYNAMICREFRESHALL"')) {
    throw 'Automatic refresh loop suppression is missing.'
}

Write-Host 'Platform, linked feature-line, drape, Universal Idle and sewer-sequence fatal-safety boundary applied.' -ForegroundColor Green
