using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceCorrectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Audits Civil 3D TIN-like surfaces for zero elevations, local spikes,
    /// extreme high/low points, open-edge hole loops and likely contamination.
    /// Corrections and simplification always create a new CE-generated surface;
    /// the source surface is never modified or erased by these commands.
    /// </summary>
    public sealed class SurfaceCorrectionCommands
    {
        private const string RegAppName = "CE_TOOLS_SURFACE_CORRECTION";
        private const string DictionaryName = "CE_TOOLS";
        private const string SettingsRecord = "SURFACE_CORRECTION_SETTINGS";
        private const string CorrectedSuffix = " - CE CORRECTED";
        private const string SimplifiedSuffix = " - CE SIMPLIFIED";
        private const double GeometryTolerance = 1e-9;

        private static readonly string[] ContaminationKeywords =
        {
            "BUILDING", "HOUSE", "ROOF", "TREE", "VEGETATION", "POLE",
            "LIGHT", "SIGN", "OVERHEAD", "OHL", "POWER", "MANHOLE",
            "MH", "INVERT", "SEWER", "STORM", "VALVE", "HYDRANT",
            "STRUCTURE", "CHAMBER", "TANK"
        };

        [CommandMethod("CE_SURFCTOOLS", CommandFlags.Modal)]
        public void SurfaceCorrectionTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nSurface correction tools [Audit/Correct/Simplify/Restore/Settings/Info] <Audit>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Audit", "Correct", "Simplify", "Restore", "Settings", "Info"
            })
                options.Keywords.Add(keyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Audit";
            string command;
            if (choice.Equals("Correct", StringComparison.OrdinalIgnoreCase))
                command = "CE_SURFCORRECT ";
            else if (choice.Equals("Simplify", StringComparison.OrdinalIgnoreCase))
                command = "CE_SURFSIMPLIFY ";
            else if (choice.Equals("Restore", StringComparison.OrdinalIgnoreCase))
                command = "CE_SURFCRESTORE ";
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                command = "CE_SURFCSETTINGS ";
            else if (choice.Equals("Info", StringComparison.OrdinalIgnoreCase))
                command = "CE_SURFCINFO ";
            else
                command = "CE_SURFAUDIT ";

            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_SURFCSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            CorrectionSettings settings = CorrectionSettings.Read(document.Database);

            if (!PromptNonNegativeDouble(editor, "Zero-elevation tolerance", settings.ZeroTolerance, out settings.ZeroTolerance))
                return;
            if (!PromptPositiveDouble(editor, "Local spike/low-point tolerance", settings.SpikeTolerance, out settings.SpikeTolerance))
                return;
            if (!PromptPositiveDouble(editor, "Neighbour search radius", settings.NeighbourRadius, out settings.NeighbourRadius))
                return;
            if (!PromptPositiveInteger(editor, "Minimum neighbours", settings.MinimumNeighbours, out settings.MinimumNeighbours))
                return;
            if (!PromptPositiveDouble(editor, "Contamination search radius", settings.ContaminationRadius, out settings.ContaminationRadius))
                return;
            if (!PromptPositiveInteger(editor, "Maximum audit vertices", settings.MaximumAuditVertices, out settings.MaximumAuditVertices))
                return;
            if (!PromptPositiveDouble(editor, "Default simplification grid size", settings.SimplificationGrid, out settings.SimplificationGrid))
                return;
            if (!PromptPositiveInteger(editor, "Maximum report rows", settings.MaximumReportRows, out settings.MaximumReportRows))
                return;

            settings.Write(document.Database);
            editor.WriteMessage("\nCE_SURFCSETTINGS saved in the current DWG.");
        }

        [CommandMethod("CE_SURFAUDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AuditSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            ObjectId surfaceId;
            if (!PromptSurface(document.Editor, out surfaceId))
                return;

            CorrectionSettings settings = CorrectionSettings.Read(document.Database);
            SurfaceAudit audit;
            try
            {
                audit = AnalyseSurface(document.Database, surfaceId, settings);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURFAUDIT cancelled. " + exception.Message);
                return;
            }

            ShowAudit(document, audit, settings);
            WriteAuditSummary(document.Editor, audit);
        }

        [CommandMethod("CE_SURFCORRECT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateCorrectedSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            ObjectId surfaceId;
            if (!PromptSurface(document.Editor, out surfaceId))
                return;

            CorrectionSettings settings = CorrectionSettings.Read(document.Database);
            SurfaceAudit audit;
            try
            {
                audit = AnalyseSurface(document.Database, surfaceId, settings);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURFCORRECT cancelled. " + exception.Message);
                return;
            }

            WriteAuditSummary(document.Editor, audit);
            if (audit.Vertices.Count == 0)
            {
                document.Editor.WriteMessage("\nNo readable surface vertices were found.");
                return;
            }

            PromptKeywordOptions contaminationOptions = new PromptKeywordOptions(
                "\nContamination handling [Keep/Exclude] <Keep>: ")
            {
                AllowNone = true
            };
            contaminationOptions.Keywords.Add("Keep");
            contaminationOptions.Keywords.Add("Exclude");
            PromptResult contaminationResult = document.Editor.GetKeywords(contaminationOptions);
            if (contaminationResult.Status == PromptStatus.Cancel)
                return;
            bool excludeContamination = contaminationResult.Status == PromptStatus.OK &&
                contaminationResult.StringResult.Equals("Exclude", StringComparison.OrdinalIgnoreCase);

            List<Point3d> corrected = BuildCorrectedPoints(
                audit,
                excludeContamination,
                settings);
            int replaced = audit.Issues.Count(issue =>
                issue.Kind == IssueKind.ZeroElevation ||
                issue.Kind == IssueKind.LocalSpike ||
                issue.Kind == IssueKind.LocalLow);
            int excluded = excludeContamination
                ? audit.Issues.Where(issue => issue.Kind == IssueKind.Contamination)
                    .Select(issue => issue.VertexIndex)
                    .Distinct()
                    .Count()
                : 0;

            document.Editor.WriteMessage(
                "\nCE_SURFCORRECT preview: source vertices={0}; corrected output vertices={1}; replacement candidates={2}; excluded contamination candidates={3}.",
                audit.Vertices.Count,
                corrected.Count,
                replaced,
                excluded);
            document.Editor.WriteMessage(
                "\nThe source surface will remain unchanged. A separate CE corrected surface will be created.");

            if (!Confirm(document.Editor, "Create the reversible corrected surface copy"))
                return;

            try
            {
                string generatedName;
                ObjectId generatedId = CreateGeneratedSurface(
                    document.Database,
                    civilDocument,
                    audit,
                    corrected,
                    "Corrected",
                    CorrectedSuffix,
                    settings,
                    out generatedName);
                document.Editor.WriteMessage(
                    "\nCE_SURFCORRECT complete. Created '{0}' ({1}). Original surface '{2}' was not modified.",
                    generatedName,
                    generatedId.Handle,
                    audit.SurfaceName);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFCORRECT cancelled. No generated surface was committed. " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SURFSIMPLIFY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateSimplifiedSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            ObjectId surfaceId;
            if (!PromptSurface(document.Editor, out surfaceId))
                return;

            CorrectionSettings settings = CorrectionSettings.Read(document.Database);
            double grid;
            if (!PromptPositiveDouble(
                    document.Editor,
                    "Simplification grid size",
                    settings.SimplificationGrid,
                    out grid))
                return;

            SurfaceAudit audit;
            try
            {
                audit = AnalyseSurface(document.Database, surfaceId, settings);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURFSIMPLIFY cancelled. " + exception.Message);
                return;
            }

            List<Point3d> simplified = SimplifyPoints(audit.Vertices, grid);
            double reduction = audit.Vertices.Count == 0
                ? 0.0
                : 100.0 * (audit.Vertices.Count - simplified.Count) / audit.Vertices.Count;

            document.Editor.WriteMessage(
                "\nCE_SURFSIMPLIFY preview: source vertices={0}; retained vertices={1}; estimated reduction={2:0.0}%; grid={3:0.###}.",
                audit.Vertices.Count,
                simplified.Count,
                reduction,
                grid);
            document.Editor.WriteMessage(
                "\nThe original surface remains unchanged. Boundary fidelity, breaklines, volumes and design tolerances must be checked after simplification.");

            if (simplified.Count < 3)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFSIMPLIFY cancelled. The grid retained fewer than three points.");
                return;
            }
            if (!Confirm(document.Editor, "Create the reversible simplified surface copy"))
                return;

            try
            {
                CorrectionSettings generatedSettings = settings.Clone();
                generatedSettings.SimplificationGrid = grid;
                string generatedName;
                ObjectId generatedId = CreateGeneratedSurface(
                    document.Database,
                    civilDocument,
                    audit,
                    simplified,
                    "Simplified",
                    SimplifiedSuffix,
                    generatedSettings,
                    out generatedName);
                document.Editor.WriteMessage(
                    "\nCE_SURFSIMPLIFY complete. Created '{0}' ({1}). Original surface '{2}' was not modified.",
                    generatedName,
                    generatedId.Handle,
                    audit.SurfaceName);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFSIMPLIFY cancelled. No generated surface was committed. " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SURFCRESTORE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RestoreOriginalSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            PromptEntityOptions options = new PromptEntityOptions(
                "\nSelect a CE corrected or simplified surface to remove: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
                return;

            string type;
            string sourceHandle;
            string settingsText;
            string name;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject selected = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false);
                if (!TryReadTag(selected, out type, out sourceHandle, out settingsText))
                {
                    document.Editor.WriteMessage(
                        "\nCE_SURFCRESTORE cancelled. The selected surface is not a CE generated correction/simplification surface.");
                    return;
                }
                name = ReadName(selected);
            }

            document.Editor.WriteMessage(
                "\nCE_SURFCRESTORE will erase generated surface '{0}'. The original source handle is {1} and was never modified by CE Tools.",
                name,
                sourceHandle);
            if (!Confirm(document.Editor, "Erase only this generated surface"))
                return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBObject selected = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForWrite,
                        false);
                    selected.Erase();
                    transaction.Commit();
                }
                document.Editor.WriteMessage(
                    "\nCE_SURFCRESTORE complete. Generated surface removed; original source remains unchanged.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURFCRESTORE failed. " + exception.Message);
            }
        }

        [CommandMethod("CE_SURFCINFO", CommandFlags.Modal)]
        public void SurfaceCorrectionInformation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            CorrectionSettings settings = CorrectionSettings.Read(document.Database);
            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    DBObject surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false);
                    string type;
                    string sourceHandle;
                    string settingsText;
                    if (!TryReadTag(surface, out type, out sourceHandle, out settingsText))
                        continue;
                    ObjectId sourceId;
                    string sourceState = TryGetObjectId(document.Database, sourceHandle, out sourceId)
                        ? "Live"
                        : "Missing";
                    rows.Add(new List<string>
                    {
                        ReadName(surface),
                        type,
                        surfaceId.Handle.ToString(),
                        sourceHandle,
                        sourceState,
                        settingsText
                    });
                }
            }

            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "No CE generated surfaces",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty
                });
            }

            string note =
                "Generated correction/simplification surfaces are separate copies. " +
                "The source is never modified by CE_SURFCORRECT or CE_SURFSIMPLIFY. " +
                "Current tolerances: zero=" + settings.ZeroTolerance.ToString("0.###", CultureInfo.InvariantCulture) +
                ", spike=" + settings.SpikeTolerance.ToString("0.###", CultureInfo.InvariantCulture) +
                ", neighbour radius=" + settings.NeighbourRadius.ToString("0.###", CultureInfo.InvariantCulture) +
                ", contamination radius=" + settings.ContaminationRadius.ToString("0.###", CultureInfo.InvariantCulture) + ".";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Surface Correction Information",
                note,
                new List<string>
                {
                    "Generated Surface", "Type", "Handle", "Source Handle", "Source State", "Settings"
                },
                rows,
                "CE Surface Correction Register");
        }

        private static SurfaceAudit AnalyseSurface(
            Database database,
            ObjectId surfaceId,
            CorrectionSettings settings)
        {
            string name;
            List<Point3d> allVertices;
            List<TriangleRecord> triangles;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false);
                name = ReadName(surface);
                allVertices = ReadSurfaceVertices(surface);
                triangles = ReadSurfaceTriangles(surface);
            }

            if (allVertices.Count == 0)
                throw new InvalidOperationException(
                    "The selected surface exposes no readable TIN vertices through the installed Civil 3D API.");

            List<Point3d> vertices = SampleVertices(allVertices, settings.MaximumAuditVertices);
            var audit = new SurfaceAudit(
                surfaceId,
                surfaceId.Handle.ToString(),
                name,
                vertices,
                allVertices.Count,
                triangles);

            Dictionary<CellKey, List<int>> grid = BuildGrid(vertices, settings.NeighbourRadius);
            var localMedians = new Dictionary<int, double>();
            for (int index = 0; index < vertices.Count; index++)
            {
                Point3d point = vertices[index];
                List<int> neighbours = FindNeighbours(
                    vertices,
                    grid,
                    index,
                    settings.NeighbourRadius);
                if (neighbours.Count >= settings.MinimumNeighbours)
                {
                    double median = Median(neighbours.Select(item => vertices[item].Z));
                    localMedians[index] = median;
                    double difference = point.Z - median;
                    if (Math.Abs(difference) > settings.SpikeTolerance)
                    {
                        audit.Issues.Add(new SurfaceIssue(
                            difference > 0.0 ? IssueKind.LocalSpike : IssueKind.LocalLow,
                            index,
                            point,
                            Math.Abs(difference),
                            median,
                            difference > 0.0
                                ? "Elevation exceeds local median"
                                : "Elevation is below local median"));
                    }
                }

                if (Math.Abs(point.Z) <= settings.ZeroTolerance)
                {
                    double suggested;
                    localMedians.TryGetValue(index, out suggested);
                    audit.Issues.Add(new SurfaceIssue(
                        IssueKind.ZeroElevation,
                        index,
                        point,
                        Math.Abs(point.Z),
                        suggested,
                        "Zero or near-zero elevation"));
                }
            }
            audit.LocalMedians = localMedians;

            double lowCut = Percentile(vertices.Select(item => item.Z), 0.001);
            double highCut = Percentile(vertices.Select(item => item.Z), 0.999);
            for (int index = 0; index < vertices.Count; index++)
            {
                Point3d point = vertices[index];
                if (point.Z <= lowCut || point.Z >= highCut)
                {
                    audit.Issues.Add(new SurfaceIssue(
                        point.Z <= lowCut ? IssueKind.ExtremeLow : IssueKind.ExtremeHigh,
                        index,
                        point,
                        point.Z,
                        point.Z,
                        point.Z <= lowCut
                            ? "Global low-tail elevation review"
                            : "Global high-tail elevation review"));
                }
            }

            List<ContaminationCandidate> candidates = ReadContaminationCandidates(database);
            if (candidates.Count > 0)
            {
                double radiusSquared = settings.ContaminationRadius * settings.ContaminationRadius;
                for (int index = 0; index < vertices.Count; index++)
                {
                    Point3d point = vertices[index];
                    ContaminationCandidate nearest = candidates
                        .Where(candidate => PlanDistanceSquared(point, candidate.Point) <= radiusSquared)
                        .OrderBy(candidate => PlanDistanceSquared(point, candidate.Point))
                        .FirstOrDefault();
                    if (nearest == null)
                        continue;
                    audit.Issues.Add(new SurfaceIssue(
                        IssueKind.Contamination,
                        index,
                        point,
                        Math.Sqrt(PlanDistanceSquared(point, nearest.Point)),
                        point.Z,
                        "Near " + nearest.Category + " object on layer " + nearest.Layer));
                }
            }

            HoleSummary holeSummary = AnalyseOpenEdges(triangles);
            audit.OpenBoundaryEdges = holeSummary.OpenEdges;
            audit.BoundaryLoops = holeSummary.Loops;
            if (holeSummary.Loops > 1)
            {
                audit.Issues.Add(new SurfaceIssue(
                    IssueKind.PossibleHole,
                    -1,
                    Point3d.Origin,
                    holeSummary.Loops - 1,
                    0.0,
                    "Multiple open-edge loops detected; review internal holes and boundaries"));
            }

            return audit;
        }

        private static void ShowAudit(
            Document document,
            SurfaceAudit audit,
            CorrectionSettings settings)
        {
            var rows = new List<IList<string>>();
            foreach (SurfaceIssue issue in audit.Issues
                .OrderBy(item => item.Kind)
                .ThenByDescending(item => item.Magnitude)
                .Take(settings.MaximumReportRows))
            {
                rows.Add(new List<string>
                {
                    issue.Kind.ToString(),
                    issue.VertexIndex < 0 ? "-" : (issue.VertexIndex + 1).ToString(CultureInfo.InvariantCulture),
                    issue.Point.X.ToString("0.###", CultureInfo.InvariantCulture),
                    issue.Point.Y.ToString("0.###", CultureInfo.InvariantCulture),
                    issue.Point.Z.ToString("0.###", CultureInfo.InvariantCulture),
                    issue.Magnitude.ToString("0.###", CultureInfo.InvariantCulture),
                    issue.SuggestedElevation.ToString("0.###", CultureInfo.InvariantCulture),
                    issue.Description
                });
            }
            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "No flagged issue", "-", "-", "-", "-", "-", "-",
                    "No issue exceeded the configured source-level audit thresholds."
                });
            }

            string note =
                "Surface: " + audit.SurfaceName +
                " | source vertices=" + audit.TotalSourceVertices +
                " | audited vertices=" + audit.Vertices.Count +
                " | triangles read=" + audit.Triangles.Count +
                " | open edges=" + audit.OpenBoundaryEdges +
                " | boundary loops=" + audit.BoundaryLoops +
                ". Results are screening indicators and require visual/engineering review.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Surface Quality Audit",
                note,
                new List<string>
                {
                    "Issue", "Vertex", "X", "Y", "Z", "Magnitude", "Suggested Z", "Reason"
                },
                rows,
                "CE Surface Quality Audit - " + audit.SurfaceName);
        }

        private static void WriteAuditSummary(Editor editor, SurfaceAudit audit)
        {
            editor.WriteMessage(
                "\nCE surface audit: {0}; source vertices={1}; audited={2}; triangles={3}; open edges={4}; loops={5}.",
                audit.SurfaceName,
                audit.TotalSourceVertices,
                audit.Vertices.Count,
                audit.Triangles.Count,
                audit.OpenBoundaryEdges,
                audit.BoundaryLoops);
            foreach (IGrouping<IssueKind, SurfaceIssue> group in audit.Issues.GroupBy(item => item.Kind))
                editor.WriteMessage("\n  {0}: {1}", group.Key, group.Count());
        }

        private static List<Point3d> BuildCorrectedPoints(
            SurfaceAudit audit,
            bool excludeContamination,
            CorrectionSettings settings)
        {
            var replacements = new Dictionary<int, double>();
            foreach (SurfaceIssue issue in audit.Issues)
            {
                if (issue.VertexIndex < 0)
                    continue;
                if (issue.Kind != IssueKind.ZeroElevation &&
                    issue.Kind != IssueKind.LocalSpike &&
                    issue.Kind != IssueKind.LocalLow)
                    continue;
                double median;
                if (audit.LocalMedians.TryGetValue(issue.VertexIndex, out median) &&
                    !double.IsNaN(median) &&
                    !double.IsInfinity(median))
                    replacements[issue.VertexIndex] = median;
            }

            var excluded = new HashSet<int>();
            if (excludeContamination)
            {
                foreach (SurfaceIssue issue in audit.Issues.Where(item => item.Kind == IssueKind.Contamination))
                    if (issue.VertexIndex >= 0)
                        excluded.Add(issue.VertexIndex);
            }

            var result = new List<Point3d>();
            for (int index = 0; index < audit.Vertices.Count; index++)
            {
                if (excluded.Contains(index))
                    continue;
                Point3d point = audit.Vertices[index];
                double elevation;
                if (replacements.TryGetValue(index, out elevation))
                    point = new Point3d(point.X, point.Y, elevation);
                result.Add(point);
            }
            return result;
        }

        private static List<Point3d> SimplifyPoints(
            IReadOnlyList<Point3d> points,
            double gridSize)
        {
            var cells = new Dictionary<CellKey, CellAccumulator>();
            foreach (Point3d point in points)
            {
                CellKey key = CellKey.From(point, gridSize);
                CellAccumulator cell;
                if (!cells.TryGetValue(key, out cell))
                {
                    cell = new CellAccumulator();
                    cells[key] = cell;
                }
                cell.Add(point);
            }

            var result = new List<Point3d>();
            foreach (CellAccumulator cell in cells.Values)
            {
                result.Add(cell.Centroid);
                if (cell.Count > 2)
                {
                    AddUniquePlanPoint(result, cell.Minimum);
                    AddUniquePlanPoint(result, cell.Maximum);
                }
            }
            return result;
        }

        private static void AddUniquePlanPoint(ICollection<Point3d> points, Point3d candidate)
        {
            if (!points.Any(point =>
                Math.Abs(point.X - candidate.X) <= GeometryTolerance &&
                Math.Abs(point.Y - candidate.Y) <= GeometryTolerance &&
                Math.Abs(point.Z - candidate.Z) <= GeometryTolerance))
                points.Add(candidate);
        }

        private static ObjectId CreateGeneratedSurface(
            Database database,
            CivilDocument civilDocument,
            SurfaceAudit audit,
            IReadOnlyList<Point3d> points,
            string generatedType,
            string nameSuffix,
            CorrectionSettings settings,
            out string generatedName)
        {
            if (points.Count < 3)
                throw new InvalidOperationException("At least three output points are required.");

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                var existingNames = new HashSet<string>(
                    civilDocument.GetSurfaceIds()
                        .Select(id => transaction.GetObject(id, OpenMode.ForRead, false))
                        .Select(ReadName),
                    StringComparer.OrdinalIgnoreCase);
                generatedName = UniqueName(audit.SurfaceName + nameSuffix, existingNames);

                ObjectId surfaceId = InvokeCreateTinSurface(
                    database,
                    generatedName,
                    audit.SurfaceId,
                    transaction);
                DBObject generated = transaction.GetObject(surfaceId, OpenMode.ForWrite, false);
                AddPointsToTinSurface(generated, points);
                generated.XData = BuildTag(
                    generatedType,
                    audit.SurfaceHandle,
                    settings.ToSummary());
                TrySetProperty(
                    generated,
                    "Description",
                    "CE " + generatedType.ToLowerInvariant() +
                    " surface from source handle " + audit.SurfaceHandle +
                    ". Original source was not modified.");
                transaction.Commit();
                return surfaceId;
            }
        }

        private static ObjectId InvokeCreateTinSurface(
            Database database,
            string name,
            ObjectId sourceSurfaceId,
            Transaction transaction)
        {
            Type tinType = typeof(CivilSurface).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.TinSurface",
                true);
            ObjectId sourceStyleId = ObjectId.Null;
            DBObject source = transaction.GetObject(sourceSurfaceId, OpenMode.ForRead, false);
            object styleValue = ReadProperty(source, "StyleId");
            if (styleValue is ObjectId)
                sourceStyleId = (ObjectId)styleValue;

            foreach (MethodInfo method in tinType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var values = new object[parameters.Length];
                bool supported = true;
                int objectIdIndex = 0;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type parameterType = parameters[index].ParameterType;
                    if (parameterType == typeof(string))
                        values[index] = name;
                    else if (parameterType == typeof(Database))
                        values[index] = database;
                    else if (parameterType == typeof(ObjectId))
                    {
                        values[index] = objectIdIndex++ == 0 && !sourceStyleId.IsNull
                            ? sourceStyleId
                            : ObjectId.Null;
                    }
                    else
                    {
                        supported = false;
                        break;
                    }
                }
                if (!supported)
                    continue;
                try
                {
                    object result = method.Invoke(null, values);
                    if (result is ObjectId)
                        return (ObjectId)result;
                    var dbObject = result as DBObject;
                    if (dbObject != null)
                        return dbObject.ObjectId;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null)
                        throw exception.InnerException;
                    throw;
                }
            }
            throw new MissingMethodException(
                "No supported TinSurface.Create overload was found in the installed Civil 3D API.");
        }

        private static void AddPointsToTinSurface(
            DBObject surface,
            IReadOnlyList<Point3d> points)
        {
            object definition = ReadProperty(surface, "Definition");
            if (definition == null)
                throw new MissingMemberException(
                    "The generated surface exposes no Definition object.");

            Point3dCollection collection = new Point3dCollection(points.ToArray());
            foreach (string methodName in new[]
            {
                "AddPointCollection", "AddPoints", "AddPointGroup"
            })
            {
                foreach (MethodInfo method in definition.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance)
                    .Where(item => item.Name == methodName))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        continue;
                    object argument = null;
                    if (parameters[0].ParameterType.IsAssignableFrom(typeof(Point3dCollection)))
                        argument = collection;
                    else if (parameters[0].ParameterType.IsAssignableFrom(typeof(Point3d[])))
                        argument = points.ToArray();
                    else if (typeof(IEnumerable<Point3d>).IsAssignableFrom(parameters[0].ParameterType))
                        argument = points;
                    if (argument == null)
                        continue;
                    method.Invoke(definition, new[] { argument });
                    RebuildSurface(surface);
                    return;
                }
            }

            MethodInfo addPoint = definition.GetType().GetMethod(
                "AddPoint",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Point3d) },
                null);
            if (addPoint != null)
            {
                foreach (Point3d point in points)
                    addPoint.Invoke(definition, new object[] { point });
                RebuildSurface(surface);
                return;
            }

            throw new MissingMethodException(
                "No supported surface-definition point-add method was found.");
        }

        private static void RebuildSurface(DBObject surface)
        {
            MethodInfo rebuild = surface.GetType().GetMethod(
                "Rebuild",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (rebuild != null)
                rebuild.Invoke(surface, null);
        }

        private static List<Point3d> ReadSurfaceVertices(DBObject surface)
        {
            var result = new List<Point3d>();
            object vertices = ReadProperty(surface, "Vertices") ??
                              InvokeNoArgument(surface, "GetVertices");
            var enumerable = vertices as IEnumerable;
            if (enumerable == null)
                return result;

            foreach (object vertex in enumerable)
            {
                Point3d point;
                if (TryReadPoint(vertex, out point))
                    AddDistinctPoint(result, point);
            }
            return result;
        }

        private static List<TriangleRecord> ReadSurfaceTriangles(DBObject surface)
        {
            var result = new List<TriangleRecord>();
            object triangles = ReadProperty(surface, "Triangles") ??
                               InvokeNoArgument(surface, "GetTriangles");
            var enumerable = triangles as IEnumerable;
            if (enumerable == null)
                return result;

            foreach (object triangle in enumerable)
            {
                Point3d a;
                Point3d b;
                Point3d c;
                if (TryReadTriangle(triangle, out a, out b, out c))
                    result.Add(new TriangleRecord(a, b, c));
            }
            return result;
        }

        private static bool TryReadTriangle(
            object triangle,
            out Point3d a,
            out Point3d b,
            out Point3d c)
        {
            string[][] names =
            {
                new[] { "Vertex1", "Point1", "A" },
                new[] { "Vertex2", "Point2", "B" },
                new[] { "Vertex3", "Point3", "C" }
            };
            Point3d[] points = new Point3d[3];
            for (int index = 0; index < names.Length; index++)
            {
                bool found = false;
                foreach (string name in names[index])
                {
                    object value = ReadProperty(triangle, name);
                    if (value != null && TryReadPoint(value, out points[index]))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    a = b = c = Point3d.Origin;
                    return false;
                }
            }
            a = points[0];
            b = points[1];
            c = points[2];
            return true;
        }

        private static bool TryReadPoint(object value, out Point3d point)
        {
            if (value is Point3d)
            {
                point = (Point3d)value;
                return true;
            }
            foreach (string name in new[] { "Location", "Position", "Point" })
            {
                object property = ReadProperty(value, name);
                if (property is Point3d)
                {
                    point = (Point3d)property;
                    return true;
                }
            }
            point = Point3d.Origin;
            return false;
        }

        private static List<Point3d> SampleVertices(
            IReadOnlyList<Point3d> vertices,
            int maximum)
        {
            if (vertices.Count <= maximum)
                return vertices.ToList();
            int step = Math.Max(1, (int)Math.Ceiling(vertices.Count / (double)maximum));
            var result = new List<Point3d>();
            for (int index = 0; index < vertices.Count; index += step)
                result.Add(vertices[index]);
            return result;
        }

        private static Dictionary<CellKey, List<int>> BuildGrid(
            IReadOnlyList<Point3d> points,
            double cellSize)
        {
            var grid = new Dictionary<CellKey, List<int>>();
            for (int index = 0; index < points.Count; index++)
            {
                CellKey key = CellKey.From(points[index], cellSize);
                List<int> list;
                if (!grid.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(index);
            }
            return grid;
        }

        private static List<int> FindNeighbours(
            IReadOnlyList<Point3d> points,
            IDictionary<CellKey, List<int>> grid,
            int index,
            double radius)
        {
            Point3d point = points[index];
            CellKey centre = CellKey.From(point, radius);
            double radiusSquared = radius * radius;
            var result = new List<int>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<int> candidates;
                    if (!grid.TryGetValue(new CellKey(centre.X + dx, centre.Y + dy), out candidates))
                        continue;
                    foreach (int candidate in candidates)
                    {
                        if (candidate == index)
                            continue;
                        if (PlanDistanceSquared(point, points[candidate]) <= radiusSquared)
                            result.Add(candidate);
                    }
                }
            }
            return result;
        }

        private static List<ContaminationCandidate> ReadContaminationCandidates(Database database)
        {
            var result = new List<ContaminationCandidate>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                    blocks[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId id in modelSpace)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity is CivilSurface)
                        continue;
                    string searchable =
                        (entity.Layer ?? string.Empty) + " " +
                        entity.GetType().Name + " " +
                        Convert.ToString(ReadProperty(entity, "Name"), CultureInfo.InvariantCulture) + " " +
                        Convert.ToString(ReadProperty(entity, "Description"), CultureInfo.InvariantCulture);
                    string category = ContaminationKeywords.FirstOrDefault(keyword =>
                        searchable.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (string.IsNullOrEmpty(category))
                        continue;
                    try
                    {
                        Extents3d extents = entity.GeometricExtents;
                        Point3d point = new Point3d(
                            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                            (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                        result.Add(new ContaminationCandidate(point, category, entity.Layer));
                    }
                    catch
                    {
                        // Objects without valid extents do not participate in proximity screening.
                    }
                }
            }
            return result;
        }

        private static HoleSummary AnalyseOpenEdges(IReadOnlyList<TriangleRecord> triangles)
        {
            if (triangles.Count == 0)
                return new HoleSummary(0, 0);

            var counts = new Dictionary<EdgeKey, int>();
            foreach (TriangleRecord triangle in triangles)
            {
                Increment(counts, new EdgeKey(triangle.A, triangle.B));
                Increment(counts, new EdgeKey(triangle.B, triangle.C));
                Increment(counts, new EdgeKey(triangle.C, triangle.A));
            }
            List<EdgeKey> open = counts.Where(item => item.Value == 1)
                .Select(item => item.Key)
                .ToList();
            if (open.Count == 0)
                return new HoleSummary(0, 0);

            var adjacency = new Dictionary<PlanPointKey, HashSet<PlanPointKey>>();
            foreach (EdgeKey edge in open)
            {
                AddAdjacency(adjacency, edge.A, edge.B);
                AddAdjacency(adjacency, edge.B, edge.A);
            }
            var unvisited = new HashSet<PlanPointKey>(adjacency.Keys);
            int loops = 0;
            while (unvisited.Count > 0)
            {
                PlanPointKey start = unvisited.First();
                var queue = new Queue<PlanPointKey>();
                queue.Enqueue(start);
                unvisited.Remove(start);
                while (queue.Count > 0)
                {
                    PlanPointKey current = queue.Dequeue();
                    HashSet<PlanPointKey> neighbours;
                    if (!adjacency.TryGetValue(current, out neighbours))
                        continue;
                    foreach (PlanPointKey neighbour in neighbours)
                    {
                        if (unvisited.Remove(neighbour))
                            queue.Enqueue(neighbour);
                    }
                }
                loops++;
            }
            return new HoleSummary(open.Count, loops);
        }

        private static void Increment(IDictionary<EdgeKey, int> counts, EdgeKey edge)
        {
            int value;
            counts.TryGetValue(edge, out value);
            counts[edge] = value + 1;
        }

        private static void AddAdjacency(
            IDictionary<PlanPointKey, HashSet<PlanPointKey>> adjacency,
            PlanPointKey from,
            PlanPointKey to)
        {
            HashSet<PlanPointKey> list;
            if (!adjacency.TryGetValue(from, out list))
            {
                list = new HashSet<PlanPointKey>();
                adjacency[from] = list;
            }
            list.Add(to);
        }

        private static double PlanDistanceSquared(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] ordered = values.OrderBy(value => value).ToArray();
            if (ordered.Length == 0)
                return double.NaN;
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) * 0.5
                : ordered[middle];
        }

        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            double[] ordered = values.OrderBy(value => value).ToArray();
            if (ordered.Length == 0)
                return double.NaN;
            double position = Math.Max(0.0, Math.Min(1.0, percentile)) * (ordered.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return ordered[lower];
            double fraction = position - lower;
            return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
        }

        private static void AddDistinctPoint(ICollection<Point3d> points, Point3d point)
        {
            if (!points.Any(existing => existing.DistanceTo(point) <= GeometryTolerance))
                points.Add(point);
        }

        private static object InvokeNoArgument(object owner, string methodName)
        {
            MethodInfo method = owner.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
                return null;
            try { return method.Invoke(owner, null); }
            catch { return null; }
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            if (owner == null)
                return null;
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead)
                return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static void TrySetProperty(object owner, string propertyName, object value)
        {
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite)
                return;
            try { property.SetValue(owner, value, null); }
            catch { }
        }

        private static string ReadName(DBObject item)
        {
            string name = Convert.ToString(ReadProperty(item, "Name"), CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(name)
                ? item.GetType().Name + " " + item.ObjectId.Handle
                : name;
        }

        private static bool PromptSurface(Editor editor, out ObjectId surfaceId)
        {
            PromptEntityOptions options = new PromptEntityOptions("\nSelect a Civil 3D surface: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult result = editor.GetEntity(options);
            surfaceId = result.Status == PromptStatus.OK
                ? result.ObjectId
                : ObjectId.Null;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptNonNegativeDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string label,
            int current,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false);
            if (table.Has(RegAppName))
                return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ResultBuffer BuildTag(
            string generatedType,
            string sourceHandle,
            string settingsText)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, generatedType ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, settingsText ?? string.Empty));
        }

        private static bool TryReadTag(
            DBObject item,
            out string generatedType,
            out string sourceHandle,
            out string settingsText)
        {
            generatedType = sourceHandle = settingsText = string.Empty;
            using (ResultBuffer data = item.GetXDataForApplication(RegAppName))
            {
                if (data == null)
                    return false;
                string[] values = data.AsArray()
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                if (values.Length < 3)
                    return false;
                generatedType = values[0];
                sourceHandle = values[1];
                settingsText = values[2];
                return generatedType == "Corrected" || generatedType == "Simplified";
            }
        }

        private static bool TryGetObjectId(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static string UniqueName(string preferred, ISet<string> existing)
        {
            string candidate = preferred;
            int suffix = 2;
            while (existing.Contains(candidate))
                candidate = preferred + " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private enum IssueKind
        {
            ZeroElevation,
            LocalSpike,
            LocalLow,
            ExtremeHigh,
            ExtremeLow,
            Contamination,
            PossibleHole
        }

        private sealed class SurfaceAudit
        {
            public SurfaceAudit(
                ObjectId surfaceId,
                string surfaceHandle,
                string surfaceName,
                List<Point3d> vertices,
                int totalSourceVertices,
                List<TriangleRecord> triangles)
            {
                SurfaceId = surfaceId;
                SurfaceHandle = surfaceHandle;
                SurfaceName = surfaceName;
                Vertices = vertices;
                TotalSourceVertices = totalSourceVertices;
                Triangles = triangles;
                Issues = new List<SurfaceIssue>();
                LocalMedians = new Dictionary<int, double>();
            }

            public ObjectId SurfaceId { get; }
            public string SurfaceHandle { get; }
            public string SurfaceName { get; }
            public List<Point3d> Vertices { get; }
            public int TotalSourceVertices { get; }
            public List<TriangleRecord> Triangles { get; }
            public List<SurfaceIssue> Issues { get; }
            public Dictionary<int, double> LocalMedians { get; set; }
            public int OpenBoundaryEdges { get; set; }
            public int BoundaryLoops { get; set; }
        }

        private sealed class SurfaceIssue
        {
            public SurfaceIssue(
                IssueKind kind,
                int vertexIndex,
                Point3d point,
                double magnitude,
                double suggestedElevation,
                string description)
            {
                Kind = kind;
                VertexIndex = vertexIndex;
                Point = point;
                Magnitude = magnitude;
                SuggestedElevation = suggestedElevation;
                Description = description;
            }

            public IssueKind Kind { get; }
            public int VertexIndex { get; }
            public Point3d Point { get; }
            public double Magnitude { get; }
            public double SuggestedElevation { get; }
            public string Description { get; }
        }

        private sealed class TriangleRecord
        {
            public TriangleRecord(Point3d a, Point3d b, Point3d c)
            {
                A = a;
                B = b;
                C = c;
            }
            public Point3d A { get; }
            public Point3d B { get; }
            public Point3d C { get; }
        }

        private sealed class ContaminationCandidate
        {
            public ContaminationCandidate(Point3d point, string category, string layer)
            {
                Point = point;
                Category = category;
                Layer = layer ?? string.Empty;
            }
            public Point3d Point { get; }
            public string Category { get; }
            public string Layer { get; }
        }

        private struct CellKey : IEquatable<CellKey>
        {
            public CellKey(long x, long y)
            {
                X = x;
                Y = y;
            }
            public long X { get; }
            public long Y { get; }
            public static CellKey From(Point3d point, double size)
            {
                return new CellKey(
                    (long)Math.Floor(point.X / size),
                    (long)Math.Floor(point.Y / size));
            }
            public bool Equals(CellKey other) { return X == other.X && Y == other.Y; }
            public override bool Equals(object obj) { return obj is CellKey && Equals((CellKey)obj); }
            public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); } }
        }

        private sealed class CellAccumulator
        {
            private double _x;
            private double _y;
            private double _z;
            public int Count { get; private set; }
            public Point3d Minimum { get; private set; }
            public Point3d Maximum { get; private set; }
            public Point3d Centroid => Count == 0
                ? Point3d.Origin
                : new Point3d(_x / Count, _y / Count, _z / Count);
            public void Add(Point3d point)
            {
                if (Count == 0 || point.Z < Minimum.Z) Minimum = point;
                if (Count == 0 || point.Z > Maximum.Z) Maximum = point;
                _x += point.X;
                _y += point.Y;
                _z += point.Z;
                Count++;
            }
        }

        private struct PlanPointKey : IEquatable<PlanPointKey>
        {
            private const double Scale = 1000.0;
            public PlanPointKey(Point3d point)
            {
                X = (long)Math.Round(point.X * Scale);
                Y = (long)Math.Round(point.Y * Scale);
            }
            public long X { get; }
            public long Y { get; }
            public bool Equals(PlanPointKey other) { return X == other.X && Y == other.Y; }
            public override bool Equals(object obj) { return obj is PlanPointKey && Equals((PlanPointKey)obj); }
            public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); } }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public EdgeKey(Point3d first, Point3d second)
            {
                PlanPointKey a = new PlanPointKey(first);
                PlanPointKey b = new PlanPointKey(second);
                if (a.X < b.X || (a.X == b.X && a.Y <= b.Y))
                {
                    A = a;
                    B = b;
                }
                else
                {
                    A = b;
                    B = a;
                }
            }
            public PlanPointKey A { get; }
            public PlanPointKey B { get; }
            public bool Equals(EdgeKey other) { return A.Equals(other.A) && B.Equals(other.B); }
            public override bool Equals(object obj) { return obj is EdgeKey && Equals((EdgeKey)obj); }
            public override int GetHashCode() { unchecked { return (A.GetHashCode() * 397) ^ B.GetHashCode(); } }
        }

        private sealed class HoleSummary
        {
            public HoleSummary(int openEdges, int loops)
            {
                OpenEdges = openEdges;
                Loops = loops;
            }
            public int OpenEdges { get; }
            public int Loops { get; }
        }

        private sealed class CorrectionSettings
        {
            public double ZeroTolerance = 0.001;
            public double SpikeTolerance = 3.0;
            public double NeighbourRadius = 10.0;
            public int MinimumNeighbours = 3;
            public double ContaminationRadius = 2.0;
            public int MaximumAuditVertices = 25000;
            public double SimplificationGrid = 2.0;
            public int MaximumReportRows = 500;

            public CorrectionSettings Clone()
            {
                return (CorrectionSettings)MemberwiseClone();
            }

            public string ToSummary()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "zero={0:R};spike={1:R};neighbour={2:R};minNeighbours={3};contamination={4:R};maxAudit={5};simplifyGrid={6:R}",
                    ZeroTolerance,
                    SpikeTolerance,
                    NeighbourRadius,
                    MinimumNeighbours,
                    ContaminationRadius,
                    MaximumAuditVertices,
                    SimplificationGrid);
            }

            public static CorrectionSettings Read(Database database)
            {
                var settings = new CorrectionSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false);
                    if (!nod.Contains(DictionaryName))
                        return settings;
                    DBDictionary ce = (DBDictionary)transaction.GetObject(
                        nod.GetAt(DictionaryName),
                        OpenMode.ForRead,
                        false);
                    if (!ce.Contains(SettingsRecord))
                        return settings;
                    Xrecord record = (Xrecord)transaction.GetObject(
                        ce.GetAt(SettingsRecord),
                        OpenMode.ForRead,
                        false);
                    string[] values = record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(item => item.TypeCode == (int)DxfCode.Text)
                            .Select(item => Convert.ToString(item.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 8)
                    {
                        double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ZeroTolerance);
                        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.SpikeTolerance);
                        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.NeighbourRadius);
                        int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MinimumNeighbours);
                        double.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ContaminationRadius);
                        int.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MaximumAuditVertices);
                        double.TryParse(values[6], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.SimplificationGrid);
                        int.TryParse(values[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MaximumReportRows);
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false);
                    DBDictionary ce;
                    if (nod.Contains(DictionaryName))
                        ce = (DBDictionary)transaction.GetObject(
                            nod.GetAt(DictionaryName),
                            OpenMode.ForWrite,
                            false);
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(DictionaryName, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }

                    Xrecord record;
                    if (ce.Contains(SettingsRecord))
                        record = (Xrecord)transaction.GetObject(
                            ce.GetAt(SettingsRecord),
                            OpenMode.ForWrite,
                            false);
                    else
                    {
                        record = new Xrecord();
                        ce.SetAt(SettingsRecord, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    string[] values =
                    {
                        ZeroTolerance.ToString("R", CultureInfo.InvariantCulture),
                        SpikeTolerance.ToString("R", CultureInfo.InvariantCulture),
                        NeighbourRadius.ToString("R", CultureInfo.InvariantCulture),
                        MinimumNeighbours.ToString(CultureInfo.InvariantCulture),
                        ContaminationRadius.ToString("R", CultureInfo.InvariantCulture),
                        MaximumAuditVertices.ToString(CultureInfo.InvariantCulture),
                        SimplificationGrid.ToString("R", CultureInfo.InvariantCulture),
                        MaximumReportRows.ToString(CultureInfo.InvariantCulture)
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value))
                        .ToArray());
                    transaction.Commit();
                }
            }

            private void Normalize()
            {
                if (ZeroTolerance < 0.0) ZeroTolerance = 0.001;
                if (SpikeTolerance <= 0.0) SpikeTolerance = 3.0;
                if (NeighbourRadius <= 0.0) NeighbourRadius = 10.0;
                if (MinimumNeighbours < 1) MinimumNeighbours = 3;
                if (ContaminationRadius <= 0.0) ContaminationRadius = 2.0;
                if (MaximumAuditVertices < 100) MaximumAuditVertices = 25000;
                if (SimplificationGrid <= 0.0) SimplificationGrid = 2.0;
                if (MaximumReportRows < 10) MaximumReportRows = 500;
            }
        }
    }
}
