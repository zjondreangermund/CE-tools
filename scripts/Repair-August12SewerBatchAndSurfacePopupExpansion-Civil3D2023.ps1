[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 12 sewer/surface source missing: $path"
    }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

# -----------------------------------------------------------------------------
# Sewer network from multiple polylines
# The older batch command consumed any PickFirst selection immediately. When one
# polyline happened to be preselected the user never got a true multi-selection
# prompt. Replace the entry point with an explicit source-selection mode and add
# a sewer-specific command that fixes the discipline to Sewer.
# -----------------------------------------------------------------------------
$network = Required 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$networkPattern = '(?s)        \[CommandMethod\("CE_TOOLS", "CE_NETWORKFROMPOLYLINESBATCH".*?\r?\n        public void CreateNetworksBatch\(\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        \[CommandMethod\("CE_TOOLS", "CE_NETWORKCONNECTSELECTED")'
$networkReplacement = @'
        [CommandMethod("CE_TOOLS", "CE_NETWORKFROMPOLYLINESBATCH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateNetworksBatch()
        {
            StartNetworkBatch(null);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERNETWORKFROMPOLYLINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSewerNetworksBatch()
        {
            StartNetworkBatch("Sewer");
        }

        private static void StartNetworkBatch(string forcedDiscipline)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (NetworkFromObjectBatchManager.IsRunning)
            {
                document.Editor.WriteMessage("\nA CE network-from-polylines batch is already running.");
                return;
            }

            bool sewerOnly = string.Equals(
                forcedDiscipline,
                "Sewer",
                StringComparison.OrdinalIgnoreCase);
            var model = new ProductionSettingsDialogModel(
                sewerOnly
                    ? "CE Tools - Sewer Network from Multiple Polylines"
                    : "CE Tools - Multiple Networks from Polylines",
                sewerOnly
                    ? "Select the complete sewer source set once. Window/crossing selection and multiple picks are supported. CE Tools then queues every selected polyline/line/feature line through Civil 3D's native gravity-network-from-object workflow."
                    : "Select the complete source set once. Window/crossing selection and multiple picks are supported. CE Tools then queues every selected polyline/line/feature line through the correct Civil 3D network-from-object workflow.");

            if (!sewerOnly)
            {
                model.AddChoice(
                    "Discipline",
                    "01 Network",
                    "Discipline",
                    "Sewer",
                    "Choose gravity or pressure network production.",
                    new[] { "Sewer", "Stormwater", "Water", "Bulk Water" });
            }
            model.AddChoice(
                "Duplicate",
                "02 Safety",
                "Previously completed CE source",
                "Skip previously completed",
                "Skip sources already marked as completed for the selected discipline, or intentionally process them again.",
                new[] { "Skip previously completed", "Process again" });
            model.AddChoice(
                "SourceSelection",
                "03 Sources",
                "Source selection",
                "Select multiple now",
                "Select the whole source set now, or deliberately reuse the current PickFirst/preselection.",
                new[] { "Select multiple now", "Use current preselection" });

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string discipline = sewerOnly ? "Sewer" : model.Text("Discipline");
            bool skipCompleted = string.Equals(
                model.Text("Duplicate"),
                "Skip previously completed",
                StringComparison.OrdinalIgnoreCase);
            bool usePreselection = string.Equals(
                model.Text("SourceSelection"),
                "Use current preselection",
                StringComparison.OrdinalIgnoreCase);

            PromptSelectionResult selected = null;
            if (usePreselection)
            {
                selected = document.Editor.SelectImplied();
            }

            if (selected == null ||
                selected.Status != PromptStatus.OK ||
                selected.Value == null ||
                selected.Value.Count == 0)
            {
                // Do not let a stale one-object PickFirst selection silently
                // collapse this command back to one-by-one source selection.
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = sewerOnly
                        ? "\nSelect ALL sewer source polylines/lines/feature lines: "
                        : "\nSelect ALL line/polyline/feature-line network sources: ",
                    MessageForRemoval = "\nRemove source objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            List<ObjectId> sources = FilterSources(
                document.Database,
                selected.Value.GetObjectIds());
            if (skipCompleted)
            {
                sources = sources
                    .Where(id => !NetworkSourceMarker.IsCompleted(
                        document.Database,
                        id,
                        discipline))
                    .ToList();
            }

            if (sources.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\n{0}: no new supported source objects remain for {1}.",
                    sewerOnly ? "CE_SEWERNETWORKFROMPOLYLINES" : "CE_NETWORKFROMPOLYLINESBATCH",
                    discipline);
                return;
            }

            NetworkFromObjectBatchManager.Start(document, sources, discipline);
            document.Editor.WriteMessage(
                "\n{0} started. Source polylines queued={1}; discipline={2}. Complete each Civil 3D native network dialog normally; CE Tools automatically advances through the entire selected set.",
                sewerOnly ? "CE_SEWERNETWORKFROMPOLYLINES" : "CE_NETWORKFROMPOLYLINESBATCH",
                sources.Count,
                discipline);
        }
'@.TrimEnd("`r","`n")
$networkRegex = [regex]::new($networkPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($networkRegex.IsMatch($text)) {
    $text = $networkRegex.Replace($text,$networkReplacement,1)
}
elseif (-not ($text.Contains('"CE_SEWERNETWORKFROMPOLYLINES"') -and
              $text.Contains('"Select multiple now"') -and
              $text.Contains('Select ALL sewer source polylines/lines/feature lines'))) {
    throw 'Sewer multi-polyline network entry point could not be installed.'
}
WriteText $network $text

# Put the dedicated command directly in Sewer Production so the operator does
# not need to open the generic multi-network hub or choose Sewer again.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldSewerAction = '                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Select many source polylines and create them without duplicate source runs.", "03 CREATE"),'
$newSewerAction = '                Action("CREATE - Sewer Network from Multiple Polylines", "CE_SEWERNETWORKFROMPOLYLINES", "Select all sewer source polylines/lines/feature lines in one selection and run the complete set as one queued gravity-network batch.", "03 CREATE"),'
if ($text.Contains($oldSewerAction)) {
    $text = $text.Replace($oldSewerAction,$newSewerAction)
}
elseif (-not $text.Contains('"CE_SEWERNETWORKFROMPOLYLINES"')) {
    throw 'Sewer Production Centre batch-network action marker was not found.'
}
WriteText $production $text

# -----------------------------------------------------------------------------
# Surface popup expansion
# Comparison already uses a two-surface popup. Add a reusable one-surface popup
# so elevation/label and future grading commands can select drawing surfaces from
# a dropdown instead of forcing model-space surface clicks.
# -----------------------------------------------------------------------------
$popup = Required 'August12SurfaceSelectionPopup.cs'
$text = ReadText $popup
if (-not $text.Contains('internal static bool TrySelectOne(')) {
    $anchor = '        internal static bool TrySelectPair('
    if (-not $text.Contains($anchor)) {
        throw 'Surface popup TrySelectPair marker was not found.'
    }
    $singleMethod = @'
        internal static bool TrySelectOne(
            Document document,
            string title,
            string note,
            string label,
            out ObjectId surfaceId)
        {
            surfaceId = ObjectId.Null;
            if (document == null) return false;

            List<SurfaceChoice> choices = ReadSurfaceChoices(document);
            if (choices.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: no Civil 3D surfaces were found in the active drawing.");
                return false;
            }

            var labels = choices.Select(item => item.Label).ToList();
            var model = new ProductionSettingsDialogModel(
                string.IsNullOrWhiteSpace(title)
                    ? "CE Tools - Select Surface"
                    : title,
                string.IsNullOrWhiteSpace(note)
                    ? "Choose a Civil 3D surface from the active drawing."
                    : note);
            model.AddChoice(
                "Surface",
                "01 Surface",
                string.IsNullOrWhiteSpace(label) ? "Surface" : label,
                labels[0],
                "Select from the Civil 3D surfaces in this drawing.",
                labels);

            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return false;

            SurfaceChoice selected = FindChoice(
                choices,
                model.Text("Surface"));
            if (selected == null)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: the selected surface is no longer available.");
                return false;
            }

            surfaceId = selected.ObjectId;
            return true;
        }

'@
    $text = $text.Replace($anchor,$singleMethod + $anchor)
}
WriteText $popup $text

$surfaceCommands = Required 'SurfaceCommands.cs'
$text = ReadText $surfaceCommands

$elevationPattern = '(?s)        private static void ReportElevation\(Document document\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        private static void PlaceElevationLabel)'
$elevationReplacement = @'
        private static void ReportElevation(Document document)
        {
            Editor editor = document.Editor;
            ObjectId surfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectOne(
                    document,
                    "CE Tools - Surface Elevation",
                    "Choose the Civil 3D surface from the popup, then pick the point to query.",
                    "Surface",
                    out surfaceId))
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick point for surface elevation: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d point = ToWorld(editor, pointResult.Value);

            try
            {
                SurfacePointResult result = ReadSurfacePoint(
                    document.Database,
                    surfaceId,
                    point);

                editor.WriteMessage(
                    "\nCE_SFELEV — Surface={0}; X={1:N3}; Y={2:N3}; Elevation={3:N3}.",
                    result.SurfaceName,
                    point.X,
                    point.Y,
                    result.Elevation);
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFELEV cancelled. The picked point is outside the selected surface boundary.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFELEV cancelled. {0}", exception.Message);
            }
        }
'@.TrimEnd("`r","`n")
$elevationRegex = [regex]::new($elevationPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($elevationRegex.IsMatch($text)) {
    $text = $elevationRegex.Replace($text,$elevationReplacement,1)
}
elseif (-not ($text.Contains('"CE Tools - Surface Elevation"') -and
              $text.Contains('August12SurfaceSelectionPopup.TrySelectOne('))) {
    throw 'CE_SFELEV could not be converted to popup surface selection.'
}

# Replace only the initial surface pick in CE_SFLABEL; the point and MLeader
# placement workflow remains unchanged.
$labelOld = @'
            Editor editor = document.Editor;
            PromptEntityResult surfaceResult = PromptForSurface(
                editor,
                "\nSelect Civil 3D surface: ");
            if (surfaceResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult targetResult = editor.GetPoint(
'@
$labelNew = @'
            Editor editor = document.Editor;
            ObjectId surfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectOne(
                    document,
                    "CE Tools - Surface Elevation Label",
                    "Choose the Civil 3D surface from the popup, then pick the target and label position.",
                    "Surface",
                    out surfaceId))
            {
                return;
            }

            PromptPointResult targetResult = editor.GetPoint(
'@
if ($text.Contains($labelOld)) {
    $text = $text.Replace($labelOld,$labelNew)
}
elseif (-not $text.Contains('"CE Tools - Surface Elevation Label"')) {
    throw 'CE_SFLABEL initial surface selection marker was not found.'
}

$labelMethodStart = $text.IndexOf('        private static void PlaceElevationLabel(Document document)',[StringComparison]::Ordinal)
$compareStart = $text.IndexOf('        private static void CompareSurfaces(Document document)',[StringComparison]::Ordinal)
if ($labelMethodStart -ge 0 -and $compareStart -gt $labelMethodStart) {
    $labelSection = $text.Substring($labelMethodStart,$compareStart - $labelMethodStart)
    if ($labelSection.Contains('surfaceResult.ObjectId')) {
        $labelSection = $labelSection.Replace('surfaceResult.ObjectId','surfaceId')
        $text = $text.Substring(0,$labelMethodStart) + $labelSection + $text.Substring($compareStart)
    }
}
WriteText $surfaceCommands $text

# Same-build guards.
$networkText = ReadText $network
foreach ($marker in @(
    '"CE_SEWERNETWORKFROMPOLYLINES"',
    '"Select multiple now"',
    'Select ALL sewer source polylines/lines/feature lines',
    'document.Editor.SetImpliedSelection(new ObjectId[0]);',
    'NetworkFromObjectBatchManager.Start(document, sources, discipline);')) {
    if (-not $networkText.Contains($marker)) {
        throw "Sewer batch-network marker missing: $marker"
    }
}
$productionText = ReadText $production
if (-not $productionText.Contains('"CREATE - Sewer Network from Multiple Polylines"') -or
    -not $productionText.Contains('"CE_SEWERNETWORKFROMPOLYLINES"')) {
    throw 'Sewer Production Centre does not expose the dedicated multi-polyline network command.'
}
$popupText = ReadText $popup
if (-not $popupText.Contains('internal static bool TrySelectOne(')) {
    throw 'Single-surface popup selector is missing.'
}
$surfaceText = ReadText $surfaceCommands
foreach ($marker in @(
    '"CE Tools - Surface Elevation"',
    '"CE Tools - Surface Elevation Label"',
    'August12SurfaceSelectionPopup.TrySelectOne(',
    'August12SurfaceSelectionPopup.TrySelectPair(')) {
    if (-not $surfaceText.Contains($marker)) {
        throw "Surface popup integration marker missing: $marker"
    }
}

Write-Host 'Sewer Production now exposes a dedicated multiple-polyline network command.' -ForegroundColor Green
Write-Host 'The sewer batch always offers a true multi-selection instead of silently consuming a one-object PickFirst selection.' -ForegroundColor Green
Write-Host 'Surface comparison, elevation and elevation-label workflows select Civil 3D surfaces from CE popup dropdowns.' -ForegroundColor Green
