[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 12 surface-popup source missing: $path"
    }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

$popup = Required 'August12SurfaceSelectionPopup.cs'
$popupText = ReadText $popup
foreach ($marker in @(
    'internal static bool TrySelectPair(',
    'CivilApplication.ActiveDocument',
    'new ProductionSettingsWindow(model)',
    'Select two different Civil 3D surfaces.')) {
    if (-not $popupText.Contains($marker)) {
        throw "Surface selector popup marker missing: $marker"
    }
}

# CE_SFCOMPARE: choose the two surfaces from a CE popup, then keep the existing
# point-pick and cut/fill calculation exactly as before.
$surfaceCommands = Required 'SurfaceCommands.cs'
$text = ReadText $surfaceCommands
$comparePattern = '(?s)        private static void CompareSurfaces\(Document document\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        private static string ClassifyDifference)'
$compareReplacement = @'
        private static void CompareSurfaces(Document document)
        {
            Editor editor = document.Editor;
            ObjectId existingSurfaceId;
            ObjectId proposedSurfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectPair(
                    document,
                    "CE Tools - Compare Surfaces",
                    "Select the base/existing and proposed/comparison Civil 3D surfaces from this drawing, then pick the point to compare.",
                    "Existing / Base surface",
                    "Proposed / Comparison surface",
                    out existingSurfaceId,
                    out proposedSurfaceId))
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick comparison point: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d point = ToWorld(editor, pointResult.Value);

            try
            {
                SurfacePointResult existing = ReadSurfacePoint(
                    document.Database,
                    existingSurfaceId,
                    point);
                SurfacePointResult proposed = ReadSurfacePoint(
                    document.Database,
                    proposedSurfaceId,
                    point);

                double difference = proposed.Elevation - existing.Elevation;
                string classification = ClassifyDifference(difference);

                editor.WriteMessage(
                    "\nCE_SFCOMPARE — X={0:N3}; Y={1:N3}; Existing {2}={3:N3}; " +
                    "Proposed {4}={5:N3}; Difference (Proposed-Existing)={6:N3}; Result={7}.",
                    point.X,
                    point.Y,
                    existing.SurfaceName,
                    existing.Elevation,
                    proposed.SurfaceName,
                    proposed.Elevation,
                    difference,
                    classification);
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFCOMPARE cancelled. The picked point is outside one or both selected surface boundaries.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFCOMPARE cancelled. {0}", exception.Message);
            }
        }
'@.TrimEnd("`r","`n")
$compareRegex = [regex]::new($comparePattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($compareRegex.IsMatch($text)) {
    $text = $compareRegex.Replace($text,$compareReplacement,1)
}
elseif (-not ($text.Contains('"CE Tools - Compare Surfaces"') -and
              $text.Contains('out existingSurfaceId') -and
              $text.Contains('out proposedSurfaceId'))) {
    throw 'CE_SFCOMPARE method could not be converted to popup surface selection.'
}
WriteText $surfaceCommands $text

# Survey correction comparison: use the same drawing-local two-surface popup.
# Keep the established tolerance and ChangedOnly/AllSampled prompts/output.
$surveyCompare = Required 'SurveyCorrectionComparisonCommands.cs'
$text = ReadText $surveyCompare
$requestPattern = '(?s)        private static bool PromptRequest\(\s*Document document,\s*out ComparisonRequest request\)\s*\{.*?\r?\n        \}(?=\r?\n\r?\n        private static ComparisonResult Compare)'
$requestReplacement = @'
        private static bool PromptRequest(
            Document document,
            out ComparisonRequest request)
        {
            request = null;
            ObjectId originalId;
            ObjectId correctedId;
            if (!August12SurfaceSelectionPopup.TrySelectPair(
                    document,
                    "CE Tools - Survey Correction Comparison",
                    "Select the original/base survey surface and the corrected/comparison surface from this drawing.",
                    "Original / Base surface",
                    "Corrected / Comparison surface",
                    out originalId,
                    out correctedId))
            {
                return false;
            }

            var toleranceOptions = new PromptDoubleOptions(
                "\nMinimum absolute elevation change to flag <0.001>: ")
            {
                AllowNegative = false,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = 0.001
            };
            PromptDoubleResult toleranceResult =
                document.Editor.GetDouble(toleranceOptions);
            if (toleranceResult.Status != PromptStatus.OK) return false;

            var modeOptions = new PromptKeywordOptions(
                "\nReport rows [ChangedOnly/AllSampled] <ChangedOnly>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("ChangedOnly");
            modeOptions.Keywords.Add("AllSampled");
            PromptResult modeResult = document.Editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return false;
            bool changedOnly = modeResult.Status != PromptStatus.OK ||
                !string.Equals(
                    modeResult.StringResult,
                    "AllSampled",
                    StringComparison.OrdinalIgnoreCase);

            request = new ComparisonRequest(
                originalId,
                correctedId,
                toleranceResult.Value,
                changedOnly);
            return true;
        }
'@.TrimEnd("`r","`n")
$requestRegex = [regex]::new($requestPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($requestRegex.IsMatch($text)) {
    $text = $requestRegex.Replace($text,$requestReplacement,1)
}
elseif (-not ($text.Contains('"CE Tools - Survey Correction Comparison"') -and
              $text.Contains('out originalId') -and
              $text.Contains('out correctedId'))) {
    throw 'Survey correction comparison request could not be converted to popup surface selection.'
}
WriteText $surveyCompare $text

# Same-build verification: neither comparison workflow may fall back to its old
# two PromptEntity surface picks.
$surfaceText = ReadText $surfaceCommands
if (-not $surfaceText.Contains('August12SurfaceSelectionPopup.TrySelectPair(') -or
    -not $surfaceText.Contains('"Existing / Base surface"') -or
    -not $surfaceText.Contains('"Proposed / Comparison surface"')) {
    throw 'CE_SFCOMPARE popup surface-selection validation failed.'
}
$surveyText = ReadText $surveyCompare
if (-not $surveyText.Contains('"Original / Base surface"') -or
    -not $surveyText.Contains('"Corrected / Comparison surface"')) {
    throw 'Survey comparison popup surface-selection validation failed.'
}

Write-Host 'CE-Compare Surfaces now selects base and comparison surfaces from a CE popup.' -ForegroundColor Green
Write-Host 'Survey Correction Comparison now uses the same drawing-local surface selector popup.' -ForegroundColor Green
