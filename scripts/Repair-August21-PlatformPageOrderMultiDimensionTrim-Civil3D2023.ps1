[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 21 page/trim/dimension source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 21 page/trim/dimension anchor missing: $label" }
    return $text.Replace($old,$new)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 21 page/trim/dimension method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 21 page/trim/dimension method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 21 page/trim/dimension opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 21 page/trim/dimension closing brace not found: $label" }
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}

$centrePath = Required 'August14StructuredDisciplineProductionCentres.cs'
$dialogPath = Required 'DisciplineWorkflowDialogs.cs'
$dimensionPath = Required 'MultiDimensionCommands.cs'
$trimPath = Required 'MultiBoundaryEditCommands.cs'

# -----------------------------------------------------------------------------
# 1. Platform Production is one complete page. Keep the older child commands
# registered for backwards compatibility, but users no longer need to enter three
# sub-pages. The action groups are physically and numerically ordered 01 -> 06.
# -----------------------------------------------------------------------------
$centre = ReadText $centrePath
$platformBody = @'
            Activate("Platform");
            Run("CE-PLATFORM PRODUCTION",
                "Complete Platform production on one page: settings, preparation, creation, design, completion and delivery.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Style Centre - Platform", "CE_PROJECTSTYLES", "Select feature-line, grading, surface and annotation styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Platform style preset.", "01 SETTINGS"),

                    A("CE-Create Feature Lines", "CE_FLCREATE", "Create multiple feature lines from selected source polylines.", "02 PREPARE"),

                    A("CE-Platform Feature Lines at Fixed / Minimum Slope", "CE_PLATFORMFEATURELINESLOPE", "Create individual feature lines from multiple platform polylines at a fixed or minimum slope toward/away from a selected reference feature line.", "03 CREATE"),
                    A("CE-Stepped Platform Offsets", "CE_PLATFORMSTEPOFFSETS", "Create linked stepped offsets from platform controls.", "03 CREATE"),
                    A("CE-Close Feature Lines for Infill", "CE_FLCLOSEINFILL", "Close selected feature lines for infill while preserving control vertices.", "03 CREATE"),

                    A("CE-Platform Slopes / Levels", "CE_PLATFORMSLOPE", "Constant slope, fixed slope or flatten to highest elevation.", "04 DESIGN"),
                    A("CE-Drape / Platform Surface", "CE_PLATFORMDRAPE", "Drape linked platform controls to a selected surface.", "04 DESIGN"),

                    A("CE-Platform Setting-Out", "CE_PLATFORMSETTINGOUT", "Linked vertex/grid setting-out and tables.", "05 COMPLETE"),
                    A("CE-Platform Names / Register", "CE_PLATFORMTABLE", "Linked platform names, elevations and register.", "05 COMPLETE"),

                    A("CE-Platform Cut / Fill", "CE_PLATFORMCUTFILL", "Linked NG versus design quantities.", "06 DELIVER"),
                    A("CE-Platform Drawings / Sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section sources.", "06 DELIVER")
                });
'@
$centre = ReplaceMethodBody $centre '        public void PlatformProduction()' $platformBody 'One-page Platform Production'
WriteText $centrePath $centre

# -----------------------------------------------------------------------------
# 2. Universal workflow/settings group ordering. Numeric prefixes are compared as
# numbers rather than strings/insertion order, so every CE page shows 01, 02, 03...
# consistently even when a later repair inserted an action out of source order.
# -----------------------------------------------------------------------------
$dialog = ReadText $dialogPath
if (-not $dialog.Contains('internal static int WorkflowStageNumber(')) {
    $helperMarker = '        public static bool Confirm(string title, string message)'
    if (-not $dialog.Contains($helperMarker)) {
        throw 'August 21 workflow ordering helper marker missing.'
    }
    $helper = @'
        internal static int WorkflowStageNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return int.MaxValue;
            int index = 0;
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            int start = index;
            while (index < value.Length && char.IsDigit(value[index])) index++;
            if (index <= start) return int.MaxValue;
            int number;
            return int.TryParse(
                value.Substring(start, index - start),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out number)
                ? number
                : int.MaxValue;
        }

'@
    $dialog = $dialog.Replace($helperMarker,$helper + $helperMarker)
}
$dialog = $dialog.Replace(
    '.GroupBy(item => item.Group)' + "`r`n" + '                    .OrderBy(item => item.Key))',
    '.GroupBy(item => item.Group)' + "`r`n" +
    '                    .OrderBy(item => DisciplineWorkflowDialogs.WorkflowStageNumber(item.Key))' + "`r`n" +
    '                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))')
$settingsOld = @'
            foreach (IGrouping<string, ProductionSettingsField> group in
                model.Fields.GroupBy(item => item.Group))
'@
$settingsNew = @'
            foreach (IGrouping<string, ProductionSettingsField> group in
                model.Fields
                    .GroupBy(item => item.Group)
                    .OrderBy(item => DisciplineWorkflowDialogs.WorkflowStageNumber(item.Key))
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
'@
if ($dialog.Contains($settingsOld)) {
    $dialog = $dialog.Replace($settingsOld,$settingsNew)
}
elseif (-not $dialog.Contains('model.Fields') -or
        -not $dialog.Contains('WorkflowStageNumber(item.Key)')) {
    throw 'August 21 settings-page group ordering anchor missing.'
}
WriteText $dialogPath $dialog

# -----------------------------------------------------------------------------
# 3. CE-Multiple Dimensions: add a chain option for selected multiple OPEN
# polylines, matching the requested 2000 / 5000 / 8000 / 2000 style. The user
# picks one common dimension-line position; approximately parallel open polylines
# are sorted across their common normal and dimensioned consecutively.
# -----------------------------------------------------------------------------
$dimension = ReadText $dimensionPath
$dimension = ReplaceRequired $dimension @'
            const string arcLength = "Arc length / along curve - polyline arcs";
            const string all = "All applicable geometry";
'@ @'
            const string arcLength = "Arc length / along curve - polyline arcs";
            const string chain = "Chain - between multiple open polylines";
            const string all = "All applicable geometry";
'@ 'Multi Dimensions chain mode constant'
$dimension = ReplaceRequired $dimension @'
                new[] { aligned, horizontal, vertical, angular, radius, arcLength, all });
'@ @'
                new[] { aligned, horizontal, vertical, chain, angular, radius, arcLength, all });
'@ 'Multi Dimensions chain mode dropdown'
$dimension = ReplaceRequired $dimension @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));

            int sources = 0;
'@ @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));

            if (string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase))
            {
                DimensionOpenPolylineChain(
                    document,
                    selection,
                    requestedStyle);
                return;
            }

            int sources = 0;
'@ 'Multi Dimensions chain dispatch'

if (-not $dimension.Contains('private static void DimensionOpenPolylineChain(')) {
    $dimensionMarker = '        private static void ProcessPolyline('
    if (-not $dimension.Contains($dimensionMarker)) {
        throw 'August 21 Multi Dimensions helper insertion marker missing.'
    }
    $dimensionHelpers = @'
        private sealed class ChainCandidate
        {
            public Polyline Polyline { get; set; }
            public Point3d Anchor { get; set; }
            public Vector3d Direction { get; set; }
            public double Coordinate { get; set; }
        }

        private static void DimensionOpenPolylineChain(
            Document document,
            PromptSelectionResult selection,
            string requestedStyle)
        {
            if (document == null || selection.Status != PromptStatus.OK ||
                selection.Value == null)
                return;

            PromptPointResult pointResult = document.Editor.GetPoint(
                "\nPick the common dimension-line location for the open-polyline chain: ");
            if (pointResult.Status != PromptStatus.OK) return;

            Point3d dimensionLinePoint = pointResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);
            int selectedCount = selection.Value.Count;
            int acceptedCount = 0;
            int skippedCount = 0;
            int created = 0;
            string outputStyleName = string.Empty;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId styleId = EnsureAnnotativeDimensionStyle(
                        document.Database,
                        transaction,
                        requestedStyle,
                        out outputStyleName);
                    if (styleId.IsNull)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. The selected dimension style could not be prepared.");
                        return;
                    }

                    var candidates = new List<ChainCandidate>();
                    foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    {
                        Polyline polyline;
                        try
                        {
                            polyline = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Polyline;
                        }
                        catch
                        {
                            skippedCount++;
                            continue;
                        }

                        ChainCandidate candidate;
                        if (polyline == null ||
                            !TryBuildChainCandidate(
                                polyline,
                                dimensionLinePoint,
                                out candidate))
                        {
                            skippedCount++;
                            continue;
                        }
                        candidates.Add(candidate);
                    }

                    if (candidates.Count < 2)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. Select at least two usable OPEN polylines.");
                        return;
                    }

                    Vector3d reference = candidates[0].Direction.GetNormal();
                    Vector3d sum = new Vector3d(0.0, 0.0, 0.0);
                    var accepted = new List<ChainCandidate>();
                    double minimumParallelDot = Math.Cos(15.0 * Math.PI / 180.0);

                    foreach (ChainCandidate candidate in candidates)
                    {
                        Vector3d direction = candidate.Direction.GetNormal();
                        double dot = direction.DotProduct(reference);
                        if (Math.Abs(dot) < minimumParallelDot)
                        {
                            skippedCount++;
                            continue;
                        }
                        if (dot < 0.0) direction = -direction;
                        candidate.Direction = direction;
                        sum += direction;
                        accepted.Add(candidate);
                    }

                    if (accepted.Count < 2)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. Fewer than two selected open polylines are approximately parallel.");
                        return;
                    }

                    Vector3d commonDirection = sum.Length > GeometryTolerance
                        ? sum.GetNormal()
                        : reference;
                    Vector3d dimensionAxis = new Vector3d(
                        -commonDirection.Y,
                        commonDirection.X,
                        0.0);
                    if (dimensionAxis.Length <= GeometryTolerance)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. A common plan direction could not be resolved.");
                        return;
                    }
                    dimensionAxis = dimensionAxis.GetNormal();

                    foreach (ChainCandidate candidate in accepted)
                    {
                        candidate.Coordinate =
                            candidate.Anchor.X * dimensionAxis.X +
                            candidate.Anchor.Y * dimensionAxis.Y;
                    }
                    accepted = accepted
                        .OrderBy(item => item.Coordinate)
                        .ToList();
                    acceptedCount = accepted.Count;

                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null) return;

                    double rotation = Math.Atan2(
                        dimensionAxis.Y,
                        dimensionAxis.X);
                    for (int index = 0; index < accepted.Count - 1; index++)
                    {
                        ChainCandidate first = accepted[index];
                        ChainCandidate second = accepted[index + 1];
                        if (Math.Abs(second.Coordinate - first.Coordinate) <=
                            GeometryTolerance)
                        {
                            skippedCount++;
                            continue;
                        }

                        var dimension = new RotatedDimension(
                            rotation,
                            Plan(first.Anchor),
                            Plan(second.Anchor),
                            Plan(dimensionLinePoint),
                            "<>",
                            styleId);
                        AddDimension(
                            document.Database,
                            transaction,
                            space,
                            dimension,
                            ref created);
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_MULTIDIM chain stopped. {0}",
                    exception.Message);
                return;
            }

            document.Editor.Regen();
            try { AcApplication.UpdateScreen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_MULTIDIM chain complete. Selected={0}; parallel open polylines={1}; dimensions={2}; skipped={3}; style={4}.",
                selectedCount,
                acceptedCount,
                created,
                skippedCount,
                outputStyleName);
        }

        private static bool TryBuildChainCandidate(
            Polyline polyline,
            Point3d dimensionLinePoint,
            out ChainCandidate candidate)
        {
            candidate = null;
            if (polyline == null ||
                polyline.Closed ||
                polyline.NumberOfVertices < 2)
                return false;

            Point3d start;
            Point3d end;
            Point3d anchor;
            try
            {
                start = Plan(polyline.StartPoint);
                end = Plan(polyline.EndPoint);
                anchor = Plan(polyline.GetClosestPointTo(
                    dimensionLinePoint,
                    false));
            }
            catch
            {
                return false;
            }

            Vector3d direction = end - start;
            if (direction.Length <= GeometryTolerance)
                return false;

            candidate = new ChainCandidate
            {
                Polyline = polyline,
                Anchor = anchor,
                Direction = direction.GetNormal()
            };
            return true;
        }

'@
    $dimension = $dimension.Replace(
        $dimensionMarker,
        $dimensionHelpers + $dimensionMarker)
}
WriteText $dimensionPath $dimension

# -----------------------------------------------------------------------------
# 4. Multi Trim/Extend selection repair.
# Pickfirst was silently becoming the protected boundary selection. A curve that
# the user then explicitly selected as a target could therefore be removed by the
# boundary-protection filter, producing "1 found" followed by "no supported curve
# targets". Always prompt boundaries explicitly and classify target rejects safely.
# -----------------------------------------------------------------------------
$trim = ReadText $trimPath
$selectBoundaryBody = @'
            // Do not consume PICKFIRST as boundaries. That made a preselected
            // target silently protected and then disappear from ResolveTargets.
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            var filter = new SelectionFilter(
                new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect CLOSED polyline trimming boundary objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                filter);
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            int rejected = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Polyline polyline;
                    try
                    {
                        polyline = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Polyline;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }

                    if (polyline != null &&
                        polyline.Closed &&
                        polyline.NumberOfVertices >= 3)
                        result.Add(id);
                    else
                        rejected++;
                }
            }

            if (result.Count == 0)
                document.Editor.WriteMessage(
                    "\nCE boundary edit cancelled. No closed lightweight-polyline boundaries were selected.");
            else if (rejected > 0)
                document.Editor.WriteMessage(
                    "\nCE boundary edit: usable boundaries={0}; rejected boundary selections={1}.",
                    result.Count,
                    rejected);
            return result;
'@
$trim = ReplaceMethodBody $trim '        private static List<ObjectId> SelectBoundaries(Document document)' $selectBoundaryBody 'Explicit Multi Trim boundary selection'

$resolveTargetsBody = @'
            HashSet<ObjectId> boundarySet =
                new HashSet<ObjectId>(boundaryIds ?? new List<ObjectId>());
            var result = new List<ObjectId>();
            int protectedBoundaries = 0;
            int unsupported = 0;
            int unreadable = 0;

            if (string.Equals(
                    scope,
                    "Selected",
                    StringComparison.OrdinalIgnoreCase))
            {
                // The boundary pick set has already been cleared. Always ask for
                // targets as a distinct second selection.
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                PromptSelectionResult selection =
                    document.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding = extend
                                ? "\nSelect Lines/open Polylines to extend: "
                                : "\nSelect curve objects to trim: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return result;

                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in
                        selection.Value.GetObjectIds().Distinct())
                    {
                        if (boundarySet.Contains(id))
                        {
                            protectedBoundaries++;
                            continue;
                        }
                        if (id.IsNull || id.IsErased)
                        {
                            unreadable++;
                            continue;
                        }

                        Curve curve;
                        try
                        {
                            curve = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Curve;
                        }
                        catch
                        {
                            unreadable++;
                            continue;
                        }

                        if (curve == null)
                        {
                            unsupported++;
                            continue;
                        }
                        if (extend &&
                            !(curve is Line) &&
                            !(curve is Polyline && !curve.Closed))
                        {
                            unsupported++;
                            continue;
                        }
                        result.Add(id);
                    }
                }

                if (result.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE boundary edit: no target curves remained after validation. Protected trimming boundaries={0}; unsupported={1}; unreadable={2}. Select the trimming boundaries first, then select separate target curves.",
                        protectedBoundaries,
                        unsupported,
                        unreadable);
                }
                return result;
            }

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return result;

                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    if (boundarySet.Contains(id))
                        continue;
                    if (id.IsNull || id.IsErased)
                        continue;

                    Curve curve;
                    try
                    {
                        curve = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Curve;
                    }
                    catch
                    {
                        continue;
                    }
                    if (curve == null)
                        continue;
                    if (extend &&
                        !(curve is Line) &&
                        !(curve is Polyline && !curve.Closed))
                        continue;
                    result.Add(id);
                }
            }
            return result;
'@
$trim = ReplaceMethodBody $trim '        private static List<ObjectId> ResolveTargets(' $resolveTargetsBody 'Multi Trim target validation'
$trim = $trim.Replace(
    'document.Editor.WriteMessage("\n{0}: no supported curve targets were selected/found.", commandName);',
    'document.Editor.WriteMessage("\n{0}: no supported curve targets remained after boundary/target validation.", commandName);')
WriteText $trimPath $trim

# -----------------------------------------------------------------------------
# Final regression guards. Fail the build before MSBuild if a staged repair later
# reintroduces the old page order, drops chain dimensions, or consumes PICKFIRST
# as Multi Trim boundaries.
# -----------------------------------------------------------------------------
$centreCheck = ReadText $centrePath
foreach ($required in @(
    '"Complete Platform production on one page',
    '"01 SETTINGS"',
    '"02 PREPARE"',
    '"03 CREATE"',
    '"04 DESIGN"',
    '"05 COMPLETE"',
    '"06 DELIVER"',
    '"CE_PLATFORMFEATURELINESLOPE"')) {
    if (-not $centreCheck.Contains($required)) {
        throw "August 21 Platform one-page guard missing: $required"
    }
}

$dialogCheck = ReadText $dialogPath
foreach ($required in @(
    'WorkflowStageNumber(',
    'OrderBy(item => DisciplineWorkflowDialogs.WorkflowStageNumber(item.Key))')) {
    if (-not $dialogCheck.Contains($required)) {
        throw "August 21 workflow numbering guard missing: $required"
    }
}

$dimensionCheck = ReadText $dimensionPath
foreach ($required in @(
    '"Chain - between multiple open polylines"',
    'DimensionOpenPolylineChain(',
    'new RotatedDimension(',
    'minimumParallelDot')) {
    if (-not $dimensionCheck.Contains($required)) {
        throw "August 21 Multi Dimensions chain guard missing: $required"
    }
}

$trimCheck = ReadText $trimPath
$selectStart = $trimCheck.IndexOf(
    'private static List<ObjectId> SelectBoundaries(Document document)',
    [StringComparison]::Ordinal)
$askScopeStart = $trimCheck.IndexOf(
    'private static string AskScope(',
    $selectStart,
    [StringComparison]::Ordinal)
if ($selectStart -lt 0 -or $askScopeStart -lt 0) {
    throw 'August 21 Multi Trim selection guard could not locate SelectBoundaries.'
}
$selectText = $trimCheck.Substring(
    $selectStart,
    $askScopeStart - $selectStart)
if ($selectText.Contains('SelectImplied()')) {
    throw 'August 21 Multi Trim boundary selection still consumes PICKFIRST.'
}
foreach ($required in @(
    'Select CLOSED polyline trimming boundary objects',
    'Protected trimming boundaries=',
    'no target curves remained after validation')) {
    if (-not $trimCheck.Contains($required)) {
        throw "August 21 Multi Trim guard missing: $required"
    }
}

Write-Host 'August 21 Platform/page-order/chain-dimension/Multi-Trim pass applied.' -ForegroundColor Green
Write-Host ' - Platform Production is one ordered 01-06 page.' -ForegroundColor Green
Write-Host ' - Workflow and settings groups sort by numeric stage on every page.' -ForegroundColor Green
Write-Host ' - CE-Multiple Dimensions includes consecutive dimensions between multiple open polylines.' -ForegroundColor Green
Write-Host ' - Multi Trim/Extend explicitly separates boundary selection from target selection.' -ForegroundColor Green
