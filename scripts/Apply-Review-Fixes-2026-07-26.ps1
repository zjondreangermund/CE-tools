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
    if (-not (Test-Path $path)) {
        throw "Review-fix source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply review fix '$Description' in '$RelativePath'."
    }

    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$sequenceFile = "src\CE.Tools.Civil3D\SewerSequenceCommands.cs"
Replace-ExactText `
    -RelativePath $sequenceFile `
    -OldText @'
                    double length = candidateEdges.Sum(pipeId => edges[pipeId].Length);
                    OrientHighestEndpointFirst(candidateNodes, candidateEdges, nodes);
'@ `
    -NewText @'
                    double length = candidateEdges.Sum(pipeId => edges[pipeId].Length);
                    OrientBranchStartTowardConnection(candidateNodes, candidateEdges);
'@ `
    -Description "orient every sewer branch from its first manhole toward its connection point"

Replace-ExactText `
    -RelativePath $sequenceFile `
    -OldText @'
        private static void OrientHighestEndpointFirst(
            IList<ObjectId> nodeIds,
            IList<ObjectId> edgeIds,
            IDictionary<ObjectId, GraphNode> nodes)
        {
            if (nodeIds.Count < 2)
            {
                return;
            }

            double firstElevation = nodes[nodeIds[0]].RimElevation;
            double lastElevation = nodes[nodeIds[nodeIds.Count - 1]].RimElevation;
            if (lastElevation <= firstElevation + ElevationTolerance)
            {
                return;
            }

            ReverseInPlace(nodeIds);
            ReverseInPlace(edgeIds);
        }
'@ `
    -NewText @'
        private static void OrientBranchStartTowardConnection(
            IList<ObjectId> nodeIds,
            IList<ObjectId> edgeIds)
        {
            if (nodeIds.Count < 2)
            {
                return;
            }

            // Root paths are built from the shared connection/root toward the
            // terminal manhole. Numbering must start at the terminal/first
            // manhole and progress toward the connection point.
            ReverseInPlace(nodeIds);
            ReverseInPlace(edgeIds);
        }
'@ `
    -Description "replace elevation-based sewer direction with topology-based branch direction"

Replace-ExactText `
    -RelativePath $sequenceFile `
    -OldText @'
                    transaction.Commit();
                }

                int totalNetworks = plans.Count;
'@ `
    -NewText @'
                    transaction.Commit();
                }

                SewerBranchAlignmentCommands.RequestAutomaticRun(
                    document,
                    plans.SelectMany(plan =>
                        plan.StructureIds.Concat(plan.PipeIds)));

                int totalNetworks = plans.Count;
'@ `
    -Description "automatically queue sewer alignments after whole-network sequencing"

Replace-ExactText `
    -RelativePath $sequenceFile `
    -OldText @'
                    transaction.Commit();

                    editor.WriteMessage(
'@ `
    -NewText @'
                    transaction.Commit();

                    SewerBranchAlignmentCommands.RequestAutomaticRun(
                        document,
                        path.StructureIds.Concat(path.PipeIds));

                    editor.WriteMessage(
'@ `
    -Description "automatically queue sewer alignment after selected-path sequencing"

$alignmentFile = "src\CE.Tools.Civil3D\SewerBranchAlignmentCommands.cs"
Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
        private const double GeometryTolerance = 1e-8;
        private const int CurvedPipeSegments = 12;
'@ `
    -NewText @'
        private const double GeometryTolerance = 1e-8;
        private const int CurvedPipeSegments = 12;
        private const double BranchLabelPaperHeight = 5.0;
        private const double BranchLabelRepeatSpacing = 50.0;
        private const int MaximumLabelsPerBranch = 200;
        private static bool _automaticRequestPending;
'@ `
    -Description "add fixed annotative branch-label settings and automatic-run state"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWALIGN",
'@ `
    -NewText @'
        internal static void RequestAutomaticRun(
            Document document,
            IEnumerable<ObjectId> sourceIds)
        {
            if (document == null || sourceIds == null)
            {
                return;
            }

            ObjectId[] ids = sourceIds
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            _automaticRequestPending = true;
            document.Editor.SetImpliedSelection(ids);
            document.SendStringToExecute("CE_SEWALIGN ", true, false, true);
        }

        private static bool ConsumeAutomaticRequest()
        {
            bool pending = _automaticRequestPending;
            _automaticRequestPending = false;
            return pending;
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWALIGN",
'@ `
    -Description "add automatic sewer-alignment queue entry point"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
'@ `
    -NewText @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            bool automaticRequest = ConsumeAutomaticRequest();
            Editor editor = document.Editor;
'@ `
    -Description "consume automatic sewer-alignment request at command start"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
                        "\n    {0}: pipes={1}; structures={2}; sampled vertices={3}; alignment direction=high cover to low cover",
'@ `
    -NewText @'
                        "\n    {0}: pipes={1}; structures={2}; sampled vertices={3}; alignment direction=first manhole to connection point",
'@ `
    -Description "report corrected sewer alignment direction"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
            if (!Confirm(
                    editor,
                    "Create or refresh the sewer branch alignments and plan labels"))
'@ `
    -NewText @'
            if (!automaticRequest &&
                !Confirm(
                    editor,
                    "Create or refresh the sewer branch alignments and plan labels"))
'@ `
    -Description "skip the second confirmation when sequencing automatically generates alignments"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
            ObjectId startId = endpoints
                .OrderByDescending(id => GetRimElevation(id, transaction))
                .ThenBy(id => id.Handle.Value)
                .First();
'@ `
    -NewText @'
            ObjectId startId = endpoints
                .OrderBy(id => GetStructureSequence(
                    id,
                    branchNumber,
                    transaction))
                .ThenBy(id => id.Handle.Value)
                .First();
'@ `
    -Description "build sewer alignment geometry from MH branch.1 toward the connection"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
        private static double GetRimElevation(
            ObjectId structureId,
            Transaction transaction)
        {
            var structure = transaction.GetObject(
                structureId,
                OpenMode.ForRead,
                false) as CivilStructure;
            if (structure == null)
            {
                return double.MinValue;
            }

            double rim = structure.RimElevation;
            return double.IsNaN(rim) || double.IsInfinity(rim)
                ? structure.Position.Z
                : rim;
        }
'@ `
    -NewText @'
        private static int GetStructureSequence(
            ObjectId structureId,
            int branchNumber,
            Transaction transaction)
        {
            var structure = transaction.GetObject(
                structureId,
                OpenMode.ForRead,
                false) as CivilStructure;
            if (structure == null)
            {
                return int.MaxValue;
            }

            Match match = Regex.Match(
                structure.Name ?? string.Empty,
                "^MH" + branchNumber.ToString(CultureInfo.InvariantCulture) +
                @"\.(?<sequence>\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int sequence;
            return match.Success &&
                   int.TryParse(
                       match.Groups["sequence"].Value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out sequence)
                ? sequence
                : int.MaxValue;
        }
'@ `
    -Description "resolve branch endpoint from manhole sequence instead of elevation"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
                        Point3d labelPoint = GetMidpoint(branch.PlanPoints);
                        var label = new MText();
                        label.SetDatabaseDefaults(database);
                        label.LayerId = layerId;
                        label.Location = labelPoint;
                        label.Attachment = AttachmentPoint.MiddleCenter;
                        label.TextHeight = GetTextHeight(database);
                        label.Contents = branch.BranchName;
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(branchKey, "Label");
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        labelsCreated++;
'@ `
    -NewText @'
                        foreach (BranchLabelPlacement placement in
                            BuildLabelPlacements(branch.PlanPoints))
                        {
                            var label = new MText();
                            label.SetDatabaseDefaults(database);
                            label.LayerId = layerId;
                            label.Location = placement.Point;
                            label.Attachment = AttachmentPoint.MiddleCenter;
                            label.Annotative = AnnotativeStates.True;
                            label.TextHeight = BranchLabelPaperHeight;
                            label.Rotation = placement.Rotation;
                            label.Contents = branch.BranchName;
                            label.BackgroundFill = true;
                            label.UseBackgroundColor = true;
                            label.XData = BuildTag(branchKey, "Label");
                            modelSpace.AppendEntity(label);
                            transaction.AddNewlyCreatedDBObject(label, true);
                            labelsCreated++;
                        }
'@ `
    -Description "create repeated annotative height-5 branch labels parallel to sewer pipes"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
        private static Point3d GetMidpoint(IReadOnlyList<Point3d> points)
        {
            double totalLength = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                totalLength += points[index - 1].DistanceTo(points[index]);
            }

            double target = totalLength * 0.5;
            double travelled = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                Point3d start = points[index - 1];
                Point3d end = points[index];
                double segmentLength = start.DistanceTo(end);
                if (travelled + segmentLength >= target &&
                    segmentLength > GeometryTolerance)
                {
                    double fraction = (target - travelled) / segmentLength;
                    return start + ((end - start) * fraction);
                }

                travelled += segmentLength;
            }

            return points[points.Count / 2];
        }

        private static double GetTextHeight(Database database)
        {
            double value = database.Textsize;
            return value > 0.0 &&
                   !double.IsNaN(value) &&
                   !double.IsInfinity(value)
                ? value
                : 2.5;
        }
'@ `
    -NewText @'
        private static IReadOnlyList<BranchLabelPlacement> BuildLabelPlacements(
            IReadOnlyList<Point3d> points)
        {
            var result = new List<BranchLabelPlacement>();
            if (points == null || points.Count < 2)
            {
                return result;
            }

            double totalLength = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                totalLength += points[index - 1].DistanceTo(points[index]);
            }
            if (totalLength <= GeometryTolerance)
            {
                return result;
            }

            int labelCount = Math.Max(
                1,
                Math.Min(
                    MaximumLabelsPerBranch,
                    (int)Math.Ceiling(totalLength / BranchLabelRepeatSpacing)));
            double interval = totalLength / labelCount;
            for (int labelIndex = 0; labelIndex < labelCount; labelIndex++)
            {
                double target = Math.Min(
                    totalLength,
                    (labelIndex + 0.5) * interval);
                result.Add(PlacementAtDistance(points, target));
            }
            return result;
        }

        private static BranchLabelPlacement PlacementAtDistance(
            IReadOnlyList<Point3d> points,
            double target)
        {
            double travelled = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                Point3d start = points[index - 1];
                Point3d end = points[index];
                Vector3d direction = end - start;
                double segmentLength = direction.Length;
                if (segmentLength <= GeometryTolerance)
                {
                    continue;
                }

                if (travelled + segmentLength >= target)
                {
                    double fraction = Math.Max(
                        0.0,
                        Math.Min(1.0, (target - travelled) / segmentLength));
                    double rotation = Math.Atan2(direction.Y, direction.X);
                    if (rotation > Math.PI * 0.5)
                    {
                        rotation -= Math.PI;
                    }
                    else if (rotation < -Math.PI * 0.5)
                    {
                        rotation += Math.PI;
                    }
                    return new BranchLabelPlacement(
                        start + (direction * fraction),
                        rotation);
                }
                travelled += segmentLength;
            }

            Point3d last = points[points.Count - 1];
            Vector3d fallback = last - points[points.Count - 2];
            return new BranchLabelPlacement(
                last,
                Math.Atan2(fallback.Y, fallback.X));
        }
'@ `
    -Description "replace single midpoint labels with interval-based aligned placements"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
        private sealed class NetworkAlignmentPlan
'@ `
    -NewText @'
        private sealed class BranchLabelPlacement
        {
            public BranchLabelPlacement(Point3d point, double rotation)
            {
                Point = point;
                Rotation = rotation;
            }

            public Point3d Point { get; }
            public double Rotation { get; }
        }

        private sealed class NetworkAlignmentPlan
'@ `
    -Description "add branch label placement value type"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText @'
            AddProductionPanel(tab);
            AddIntegrationPanel(tab);
            return true;
'@ `
    -NewText @'
            AddProductionPanel(tab);
            AddIntegrationPanel(tab);
            AddReviewFixesPanel(tab);
            return true;
'@ `
    -Description "add the 26 July runtime-corrections panel"

Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText @'
        private static RibbonItem[] Row(params RibbonItem[] items)
'@ `
    -NewText @'
        private static void AddReviewFixesPanel(RibbonTab tab)
        {
            AddPanel(tab, "CE_TOOLS_CATEGORY_REVIEW_FIXES", "Review Fixes", Row(
                Menu("CE_TOOLS_SEWER_REVIEW_FIXES_MENU", "Sewer Sequence\n& Labels", "Sequence every branch from its first manhole to the connection point and automatically create aligned repeated branch labels.",
                    Cmd("Sequence Network and Create Alignments", "CE_SEWSEQ ", "Rename branches first-manhole-to-connection and automatically generate/refresh sewer alignments."),
                    Cmd("Refresh Sewer Alignments and Labels", "CE_SEWALIGN ", "Create or refresh annotative paper-height-5 branch labels rotated with pipe direction and repeated on long branches.")),
                Menu("CE_TOOLS_SURVEY_REVIEW_FIXES_MENU", "Survey Change\nReport", "Compare original and corrected survey surfaces without modifying either source.",
                    Cmd("Survey Comparison Tools", "CE_SURVEYCOMPARETOOLS ", "Open original-versus-corrected report or Excel export workflows."),
                    Cmd("Survey Correction Popup and Table", "CE_SURVEYCHANGES ", "Show X/Y/original Z/corrected Z/delta changes and optionally place a drawing table."),
                    Cmd("Export Survey Correction Changes", "CE_SURVEYCHANGEEXPORT ", "Export original-versus-corrected survey elevation changes to XLSX.")),
                Menu("CE_TOOLS_FAST_BLOCK_MENU", "Fast Block\nEditing", "Avoid slow in-place architectural block editing.",
                    Cmd("Open Normal Block Editor", "CE_BLOCKEDITFAST ", "Open ordinary blocks with BEDIT and XREFs with XOPEN; REFEDIT is not used."))));
        }

        private static RibbonItem[] Row(params RibbonItem[] items)
'@ `
    -Description "add sewer survey and fast-block runtime correction commands"

Write-Host "26 July 2026 runtime review fixes were applied." -ForegroundColor Green
