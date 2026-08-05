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
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;

[assembly: CommandClass(typeof(CETools.Civil3D.StormwaterProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates and refreshes stormwater branch alignments from CE-sequenced
    /// gravity networks or selected lightweight polylines. It also creates linked
    /// existing-ground profiles and profile views through version-tolerant Civil
    /// 3D reflection, applies selected styles, adds network parts to profile views
    /// where the host API supports it, and records traceable source handles.
    /// </summary>
    public sealed class StormwaterProductionCommands
    {
        private const string SettingsRecordName = "STORMWATER_PRODUCTION_SETTINGS";
        private const string CeDictionaryName = "CE_TOOLS";
        private const string DefaultAlignmentLayer = "CE-SW-ALIGNMENT";
        private const string DefaultProfileLayer = "CE-SW-PROFILE";
        private const double GeometryTolerance = 1e-8;
        private const int CurvedPipeSegments = 12;

        [CommandMethod("CE_SWTOOLS", CommandFlags.Modal)]
        public void StormwaterTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;
            string command = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools — Stormwater Workflow",
                "Sequence networks, generate linked alignments and profiles, then review or refresh the model. Configuration is stored in the active DWG.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Choose production styles", "CE_SWSETTINGS", "Choose alignment/profile styles, label sets, profile-view style and band set before production starts.", "0 — Production setup"),
                    new DisciplineWorkflowAction("Sequence network", "CE_SWSEQ", "Create the main and branch order from a stormwater network.", "1 — Network"),
                    new DisciplineWorkflowAction("Create or refresh alignments", "CE_SWALIGN", "Build linked branch alignments and staggered plan labels.", "2 — Alignments"),
                    new DisciplineWorkflowAction("Create or refresh profiles", "CE_SWPROFILE", "Select a surface, pick an insertion point and create linked profile views.", "3 — Profiles"),
                    new DisciplineWorkflowAction("Stormwater information", "CE_SWINFO", "Review stored settings and generated object links.", "5 — Review")
                });
            if (!string.IsNullOrWhiteSpace(command))
                document.SendStringToExecute(command.Trim() + " ", true, false, true);
        }

        [CommandMethod("CE_SWSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            StormwaterProductionSettings settings = StormwaterProductionSettings.Read(document.Database);
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            var model = new ProductionSettingsDialogModel(
                "CE Tools — Stormwater Settings",
                "Blank style names use the first compatible style in the current drawing. Paper label heights and profile-view layout are stored with this DWG.");
            model.AddChoice("AlignmentStyle", "Civil 3D Styles", "Alignment style", settings.AlignmentStyle, "Select an installed style; blank uses the first available style.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.AlignmentStyles, "Alignment Style"));
            model.AddChoice("AlignmentLabelSetStyle", "Civil 3D Styles", "Alignment label-set style", settings.AlignmentLabelSetStyle, "Labels applied to generated branch alignments.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles, "Alignment Label Set Style"));
            model.AddChoice("ProfileStyle", "Civil 3D Styles", "Profile style", settings.ProfileStyle, "Style used for generated existing-ground profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileStyles, "Profile Style"));
            model.AddChoice("ProfileLabelSetStyle", "Civil 3D Styles", "Profile label-set style", settings.ProfileLabelSetStyle, "Label set applied to generated profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles, "Profile Label Set Style"));
            model.AddChoice("ProfileViewStyle", "Civil 3D Styles", "Profile-view style", settings.ProfileViewStyle, "Style applied to generated profile views.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewStyles, "Profile View Style"));
            model.AddChoice("ProfileViewBandSetStyle", "Civil 3D Styles", "Profile-view band-set style", settings.ProfileViewBandSetStyle, "Band set applied when profile views are created.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewBandSetStyles, "Profile View Band Set Style"));
            model.AddText("AlignmentLayer", "Layers and Annotation", "Alignment layer", settings.AlignmentLayer, "Layer for CE stormwater alignments and plan labels.");
            model.AddText("ProfileLayer", "Layers and Annotation", "Profile layer", settings.ProfileLayer, "Layer for generated profiles and profile views.");
            model.AddPaperHeight("LabelTextHeight", "Layers and Annotation", "Plan branch-label paper height", settings.LabelTextHeight, "Select 1.8, 2.0, 2.5, 3.5 or 5.0, or enter another positive height.");
            model.AddChoice("BranchLabelSide", "Layers and Annotation", "Branch-label offset side", settings.BranchLabelSide, "Place branch names above, below, or alternate sides at a scale-aware perpendicular offset.", new[] { "Alternating", "Above", "Below" });
            model.AddPositiveInteger("ProfileColumns", "Profile View Layout", "Profile views per row", settings.ProfileColumns, "Number of generated views before wrapping to the next row.");
            model.AddPositiveDouble("ProfileHorizontalSpacing", "Profile View Layout", "Horizontal spacing", settings.ProfileHorizontalSpacing, "Drawing-unit spacing between profile-view columns.");
            model.AddPositiveDouble("ProfileVerticalSpacing", "Profile View Layout", "Vertical spacing", settings.ProfileVerticalSpacing, "Drawing-unit spacing between profile-view rows.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            settings.AlignmentStyle = model.Text("AlignmentStyle");
            settings.AlignmentLabelSetStyle = model.Text("AlignmentLabelSetStyle");
            settings.ProfileStyle = model.Text("ProfileStyle");
            settings.ProfileLabelSetStyle = model.Text("ProfileLabelSetStyle");
            settings.ProfileViewStyle = model.Text("ProfileViewStyle");
            settings.ProfileViewBandSetStyle = model.Text("ProfileViewBandSetStyle");
            settings.AlignmentLayer = model.Text("AlignmentLayer");
            settings.ProfileLayer = model.Text("ProfileLayer");
            settings.LabelTextHeight = model.Double("LabelTextHeight", settings.LabelTextHeight);
            settings.BranchLabelSide = model.Text("BranchLabelSide");
            settings.ProfileColumns = model.Integer("ProfileColumns", settings.ProfileColumns);
            settings.ProfileHorizontalSpacing = model.Double("ProfileHorizontalSpacing", settings.ProfileHorizontalSpacing);
            settings.ProfileVerticalSpacing = model.Double("ProfileVerticalSpacing", settings.ProfileVerticalSpacing);

            settings.Write(document.Database);
            document.Editor.WriteMessage(
                "\nCE_SWSETTINGS saved. Style names are resolved and checked when alignments or profiles are created.");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SWALIGN",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        [CommandMethod(
            "CE_SWREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void CreateOrRefreshAlignments()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage(
                    "\nCE_SWALIGN cancelled. No active Civil 3D document is available.");
                return;
            }

            string sourceChoice = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Stormwater Alignment Source",
                "Choose the source type. Civil 3D object selection continues in the drawing canvas.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Existing pipe network", "Network", "Build branch alignments from selected stormwater pipes and structures.", "01 Source"),
                    new DisciplineWorkflowAction("Selected polylines", "Polylines", "Build branches from one or more selected open polylines.", "01 Source")
                });
            if (string.IsNullOrWhiteSpace(sourceChoice)) return;

            bool fromPolylines = sourceChoice.Equals(
                "Polylines",
                StringComparison.OrdinalIgnoreCase);

            List<StormwaterAlignmentPlan> plans;
            int unsupported;
            try
            {
                plans = fromPolylines
                    ? ReadPolylinePlans(document, out unsupported)
                    : ReadNetworkPlans(document, out unsupported);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SWALIGN cancelled. " + exception.Message);
                return;
            }

            if (plans == null || plans.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SWALIGN: no usable stormwater branches were found.");
                return;
            }

            editor.WriteMessage(
                "\nCE_SWALIGN preview. Source: {0}; branches: {1}; unsupported selections ignored: {2}.",
                fromPolylines ? "polylines" : "CE-sequenced gravity network",
                plans.Count,
                unsupported);
            foreach (StormwaterAlignmentPlan plan in plans)
            {
                editor.WriteMessage(
                    "\n  {0}: source objects={1}; sampled vertices={2}; source={3}.",
                    plan.BranchKey,
                    plan.SourceHandles.Count,
                    plan.PlanPoints.Count,
                    plan.SourceKind);
            }

            if (!Confirm(
                    editor,
                    "Create or refresh these stormwater alignments and staggered plan labels"))
            {
                editor.WriteMessage(
                    "\nCE_SWALIGN cancelled. No alignments or labels were changed.");
                return;
            }

            try
            {
                StormwaterProductionSettings settings =
                    StormwaterProductionSettings.Read(database);
                int alignmentsCreated;
                int labelsCreated;
                CreateAlignments(
                    database,
                    civilDocument,
                    settings,
                    plans,
                    out alignmentsCreated,
                    out labelsCreated);

                editor.WriteMessage(
                    "\nCE_SWALIGN complete. Alignments created/refreshed: {0}; plan labels: {1}.",
                    alignmentsCreated,
                    labelsCreated);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SWALIGN cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SWPROFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateOrRefreshProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage(
                    "\nCE_SWPROFILE cancelled. No active Civil 3D document is available.");
                return;
            }
            StormwaterProductionSettings settings = StormwaterProductionSettings.Read(database);
            List<SurfaceChoice> surfaceChoices = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaceChoices.Count == 0)
            {
                editor.WriteMessage("\nCE_SWPROFILE cancelled. The drawing contains no Civil 3D surfaces.");
                return;
            }
            var surfaceWindow = new SurfaceSelectionWindow(
                surfaceChoices,
                "Select the existing-ground surface for the stormwater profiles. Double-clicking a row also selects it.");
            AcApplication.ShowModalWindow(surfaceWindow);
            SurfaceChoice selectedSurface = surfaceWindow.SelectedSurface;
            if (selectedSurface == null) return;

            PromptPointResult insertionResult = editor.GetPoint(
                "\nSpecify the upper-left insertion point for the first profile view: ");
            if (insertionResult.Status != PromptStatus.OK)
                return;

            List<StormwaterAlignmentRecord> alignments;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                alignments = ReadGeneratedAlignments(
                    civilDocument,
                    transaction);
            }

            if (alignments.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SWPROFILE: no CE stormwater alignments were found. Run CE_SWALIGN first.");
                return;
            }

            editor.WriteMessage(
                "\nCE_SWPROFILE preview. Alignments: {0}; layout: {1} per row; surface handle: {2}.",
                alignments.Count,
                settings.ProfileColumns,
                selectedSurface.ObjectId.Handle);

            if (!Confirm(
                    editor,
                    "Create or refresh existing-ground profiles, profile views and network-part displays"))
            {
                editor.WriteMessage(
                    "\nCE_SWPROFILE cancelled. No profile objects were changed.");
                return;
            }

            try
            {
                int profilesCreated;
                int viewsCreated;
                int partsAdded;
                CreateProfiles(
                    database,
                    civilDocument,
                    settings,
                    selectedSurface.ObjectId,
                    alignments,
                    insertionResult.Value,
                    settings.ProfileColumns,
                    settings.ProfileHorizontalSpacing,
                    settings.ProfileVerticalSpacing,
                    out profilesCreated,
                    out viewsCreated,
                    out partsAdded);

                editor.WriteMessage(
                    "\nCE_SWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",
                    profilesCreated,
                    viewsCreated,
                    partsAdded);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SWPROFILE cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SWINFO", CommandFlags.Modal)]
        public void ShowInformation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Database database = document.Database;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            StormwaterProductionSettings settings =
                StormwaterProductionSettings.Read(database);

            int alignments = 0;
            int profileViews = 0;
            int labels = 0;
            int sequencedPipes = 0;
            int sequencedStructures = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                if (civilDocument != null)
                {
                    alignments = ReadGeneratedAlignments(
                        civilDocument,
                        transaction).Count;
                }

                BlockTable blockTable = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null)
                        continue;

                    foreach (ObjectId objectId in block)
                    {
                        DBObject databaseObject = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false);
                        string objectType;
                        string branch;
                        if (TryReadProductionTag(
                                databaseObject,
                                StormwaterMetadata.ProfileRegAppName,
                                out branch,
                                out objectType) &&
                            objectType == "ProfileView")
                            profileViews++;

                        if (TryReadProductionTag(
                                databaseObject,
                                StormwaterMetadata.AlignmentRegAppName,
                                out branch,
                                out objectType) &&
                            objectType == "Label")
                            labels++;

                        StormwaterPartTag partTag;
                        if (StormwaterMetadata.TryReadTag(
                                databaseObject,
                                out partTag))
                        {
                            if (partTag.Role == "Pipe")
                                sequencedPipes++;
                            else if (partTag.Role == "Structure")
                                sequencedStructures++;
                        }
                    }
                }
            }

            document.Editor.WriteMessage(
                "\nCE stormwater production information:" +
                "\n  Sequenced pipes: " + sequencedPipes +
                "\n  Sequenced structures visible in drawing spaces: " + sequencedStructures +
                "\n  Generated alignments: " + alignments +
                "\n  Generated plan labels: " + labels +
                "\n  Generated profile views: " + profileViews +
                "\n  Alignment layer: " + settings.AlignmentLayer +
                "\n  Profile layer: " + settings.ProfileLayer +
                "\n  Alignment style: " + DisplaySetting(settings.AlignmentStyle) +
                "\n  Alignment label set: " + DisplaySetting(settings.AlignmentLabelSetStyle) +
                "\n  Profile style: " + DisplaySetting(settings.ProfileStyle) +
                "\n  Profile label set: " + DisplaySetting(settings.ProfileLabelSetStyle) +
                "\n  Profile-view style: " + DisplaySetting(settings.ProfileViewStyle) +
                "\n  Band-set style: " + DisplaySetting(settings.ProfileViewBandSetStyle) +
                "\n  Update model: explicit CE_SWREFRESH / CE_SWPROFILE. Surface profiles remain Civil 3D-linked where the host API supports CreateFromSurface.");
        }

        private static List<StormwaterAlignmentPlan> ReadNetworkPlans(
            Document document,
            out int unsupported)
        {
            Editor editor = document.Editor;
            Database database = document.Database;
            unsupported = 0;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect one or more CE-sequenced stormwater pipes or structures: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return new List<StormwaterAlignmentPlan>();

            var networkIds = new HashSet<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId selectedId in selection.Value.GetObjectIds())
                {
                    DBObject selected = transaction.GetObject(
                        selectedId,
                        OpenMode.ForRead,
                        false);
                    var pipe = selected as CivilPipe;
                    if (pipe != null && !pipe.NetworkId.IsNull)
                    {
                        networkIds.Add(pipe.NetworkId);
                        continue;
                    }

                    var structure = selected as CivilStructure;
                    if (structure != null && !structure.NetworkId.IsNull)
                    {
                        networkIds.Add(structure.NetworkId);
                        continue;
                    }

                    unsupported++;
                }
            }

            var plans = new List<StormwaterAlignmentPlan>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in networkIds)
                {
                    var network = transaction.GetObject(
                        networkId,
                        OpenMode.ForRead,
                        false) as CivilNetwork;
                    if (network == null)
                        continue;

                    var groups = new Dictionary<string, List<ObjectId>>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        DBObject pipeObject = transaction.GetObject(
                            pipeId,
                            OpenMode.ForRead,
                            false);
                        StormwaterPartTag tag;
                        if (!StormwaterMetadata.TryReadTag(
                                pipeObject,
                                out tag) ||
                            !tag.Role.Equals(
                                "Pipe",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        List<ObjectId> ids;
                        if (!groups.TryGetValue(tag.BranchKey, out ids))
                        {
                            ids = new List<ObjectId>();
                            groups.Add(tag.BranchKey, ids);
                        }
                        ids.Add(pipeId);
                    }

                    if (groups.Count == 0)
                        throw new InvalidOperationException(
                            "Network '" + network.Name +
                            "' contains no CE stormwater sequence tags. Run CE_SWSEQ first.");

                    foreach (KeyValuePair<string, List<ObjectId>> group in
                        groups.OrderBy(item => BranchSortKey(item.Key)))
                    {
                        OrderedPipePath ordered =
                            OrderPipeGroup(group.Key, group.Value, transaction);
                        List<Point3d> points =
                            BuildPipePlanPoints(ordered, transaction);
                        plans.Add(new StormwaterAlignmentPlan(
                            group.Key,
                            "Network",
                            points,
                            group.Value
                                .Select(id => id.Handle.ToString())
                                .ToList(),
                            ObjectId.Null));
                    }
                }
            }

            return plans;
        }

        private static OrderedPipePath OrderPipeGroup(
            string branchKey,
            IReadOnlyCollection<ObjectId> pipeIds,
            Transaction transaction)
        {
            var adjacency =
                new Dictionary<ObjectId, List<StormwaterPipeRecord>>();
            var records = new List<StormwaterPipeRecord>();

            foreach (ObjectId pipeId in pipeIds)
            {
                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null ||
                    pipe.StartStructureId.IsNull ||
                    pipe.EndStructureId.IsNull)
                    throw new InvalidOperationException(
                        branchKey +
                        " contains an unavailable or unconnected pipe.");

                StormwaterPartTag tag;
                StormwaterMetadata.TryReadTag(pipe, out tag);
                int sequence = tag == null
                    ? int.MaxValue
                    : tag.Sequence;

                var record = new StormwaterPipeRecord(
                    pipeId,
                    pipe.StartStructureId,
                    pipe.EndStructureId,
                    sequence);
                records.Add(record);
                AddPipe(adjacency, record.StartStructureId, record);
                AddPipe(adjacency, record.EndStructureId, record);
            }

            if (adjacency.Any(item => item.Value.Count > 2))
                throw new InvalidOperationException(
                    branchKey +
                    " is not a single continuous path.");

            List<ObjectId> endpoints = adjacency
                .Where(item => item.Value.Count == 1)
                .Select(item => item.Key)
                .ToList();
            if (endpoints.Count != 2)
                throw new InvalidOperationException(
                    branchKey +
                    " must have exactly two endpoints.");

            ObjectId startId = endpoints
                .OrderByDescending(id =>
                    GetRimElevation(id, transaction))
                .ThenBy(id => id.Handle.Value)
                .First();

            var remaining = new HashSet<ObjectId>(
                records.Select(record => record.PipeId));
            var orderedPipes = new List<ObjectId>();
            var orderedStructures = new List<ObjectId>();
            ObjectId current = startId;

            while (true)
            {
                orderedStructures.Add(current);
                StormwaterPipeRecord next = adjacency[current]
                    .Where(record => remaining.Contains(record.PipeId))
                    .OrderBy(record => record.Sequence)
                    .ThenBy(record => record.PipeId.Handle.Value)
                    .FirstOrDefault();
                if (next == null)
                    break;

                remaining.Remove(next.PipeId);
                orderedPipes.Add(next.PipeId);
                current = next.Other(current);
            }

            if (remaining.Count != 0)
                throw new InvalidOperationException(
                    branchKey +
                    " contains disconnected or ambiguous pipe geometry.");

            return new OrderedPipePath(
                orderedStructures,
                orderedPipes);
        }

        private static void AddPipe(
            IDictionary<ObjectId, List<StormwaterPipeRecord>> adjacency,
            ObjectId structureId,
            StormwaterPipeRecord record)
        {
            List<StormwaterPipeRecord> list;
            if (!adjacency.TryGetValue(structureId, out list))
            {
                list = new List<StormwaterPipeRecord>();
                adjacency.Add(structureId, list);
            }

            list.Add(record);
        }

        private static double GetRimElevation(
            ObjectId structureId,
            Transaction transaction)
        {
            var structure = transaction.GetObject(
                structureId,
                OpenMode.ForRead,
                false) as CivilStructure;
            if (structure == null)
                return double.MinValue;

            double rim = structure.RimElevation;
            return double.IsNaN(rim) || double.IsInfinity(rim)
                ? structure.Position.Z
                : rim;
        }

        private static List<Point3d> BuildPipePlanPoints(
            OrderedPipePath path,
            Transaction transaction)
        {
            var points = new List<Point3d>();

            for (int index = 0; index < path.PipeIds.Count; index++)
            {
                var pipe = transaction.GetObject(
                    path.PipeIds[index],
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null)
                    continue;

                bool forward =
                    pipe.StartStructureId == path.StructureIds[index];
                int segmentCount = IsCurvedPipe(pipe)
                    ? CurvedPipeSegments
                    : 1;

                for (int segment = 0; segment <= segmentCount; segment++)
                {
                    double fraction = segment / (double)segmentCount;
                    double parameter = forward
                        ? fraction
                        : 1.0 - fraction;
                    Point3d point = pipe.GetPointAtParam(parameter);
                    AddDistinctPoint(points, point);
                }
            }

            return points;
        }

        private static bool IsCurvedPipe(CivilPipe pipe)
        {
            try
            {
                return pipe.Curve2d != null;
            }
            catch
            {
                return false;
            }
        }

        private static List<StormwaterAlignmentPlan> ReadPolylinePlans(
            Document document,
            out int unsupported)
        {
            Editor editor = document.Editor;
            Database database = document.Database;
            unsupported = 0;

            string mainChoice = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Stormwater Main Branch",
                "Choose automatic sequencing or identify the main branch before selecting the remaining polylines.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Automatic main branch", "Automatic", "CE Tools orders the selected branches automatically.", "01 Sequence"),
                    new DisciplineWorkflowAction("Select main branch first", "SelectMain", "Pick the main polyline first, then select the complete network.", "01 Sequence")
                });
            if (string.IsNullOrWhiteSpace(mainChoice))
                return new List<StormwaterAlignmentPlan>();

            bool selectMain = mainChoice.Equals(
                "SelectMain",
                StringComparison.OrdinalIgnoreCase);

            ObjectId selectedMainId = ObjectId.Null;
            if (selectMain)
            {
                PromptEntityOptions entityOptions = new PromptEntityOptions(
                    "\nSelect the main stormwater polyline first: ");
                entityOptions.SetRejectMessage(
                    "\nSelect an open lightweight polyline.");
                entityOptions.AddAllowedClass(typeof(Polyline), true);
                PromptEntityResult entityResult =
                    editor.GetEntity(entityOptions);
                if (entityResult.Status != PromptStatus.OK)
                    return new List<StormwaterAlignmentPlan>();
                selectedMainId = entityResult.ObjectId;
            }

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect all stormwater branch polylines: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return new List<StormwaterAlignmentPlan>();

            var polylineIds = new HashSet<ObjectId>(
                selection.Value.GetObjectIds());
            if (!selectedMainId.IsNull)
                polylineIds.Add(selectedMainId);

            var candidates = new List<PolylineCandidate>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in polylineIds)
                {
                    var polyline = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Polyline;
                    if (polyline == null ||
                        polyline.Closed ||
                        polyline.NumberOfVertices < 2)
                    {
                        unsupported++;
                        continue;
                    }

                    candidates.Add(new PolylineCandidate(
                        objectId,
                        polyline.Length,
                        ReadPolylinePoints(polyline)));
                }

                if (candidates.Count == 0)
                    return new List<StormwaterAlignmentPlan>();

                PolylineCandidate main = !selectedMainId.IsNull
                    ? candidates.FirstOrDefault(
                        item => item.ObjectId == selectedMainId)
                    : candidates
                        .OrderByDescending(item => item.Length)
                        .ThenBy(item => item.ObjectId.Handle.Value)
                        .First();

                if (main == null)
                    throw new InvalidOperationException(
                        "The selected main polyline is not an open lightweight polyline.");

                var plans = new List<StormwaterAlignmentPlan>
                {
                    new StormwaterAlignmentPlan(
                        "SW-MAIN",
                        "Polyline",
                        main.Points,
                        new[] { main.ObjectId.Handle.ToString() },
                        main.ObjectId)
                };

                var branches = candidates
                    .Where(item => item.ObjectId != main.ObjectId)
                    .OrderBy(item =>
                        ReadAttachmentDistance(
                            main.ObjectId,
                            item.ObjectId,
                            transaction))
                    .ThenByDescending(item => item.Length)
                    .ThenBy(item => item.ObjectId.Handle.Value)
                    .ToList();

                for (int index = 0; index < branches.Count; index++)
                {
                    PolylineCandidate branch = branches[index];
                    plans.Add(new StormwaterAlignmentPlan(
                        "SW-B" + (index + 1).ToString(
                            "00",
                            CultureInfo.InvariantCulture),
                        "Polyline",
                        branch.Points,
                        new[] { branch.ObjectId.Handle.ToString() },
                        branch.ObjectId));
                }

                return plans;
            }
        }

        private static double ReadAttachmentDistance(
            ObjectId mainId,
            ObjectId branchId,
            Transaction transaction)
        {
            var main = transaction.GetObject(
                mainId,
                OpenMode.ForRead,
                false) as Polyline;
            var branch = transaction.GetObject(
                branchId,
                OpenMode.ForRead,
                false) as Polyline;
            if (main == null || branch == null)
                return double.MaxValue;

            try
            {
                Point3d closest = main.GetClosestPointTo(
                    branch.StartPoint,
                    false);
                return main.GetDistAtPoint(closest);
            }
            catch
            {
                return main.StartPoint.DistanceTo(branch.StartPoint);
            }
        }

        private static List<Point3d> ReadPolylinePoints(
            Polyline polyline)
        {
            var points = new List<Point3d>();
            for (int index = 0; index < polyline.NumberOfVertices; index++)
                points.Add(polyline.GetPoint3dAt(index));
            return points;
        }

        private static void AddDistinctPoint(
            ICollection<Point3d> points,
            Point3d point)
        {
            Point3d planPoint = new Point3d(
                point.X,
                point.Y,
                0.0);
            if (points.Count == 0 ||
                points.Last().DistanceTo(planPoint) >
                GeometryTolerance)
                points.Add(planPoint);
        }

        private static void CreateAlignments(
            Database database,
            CivilDocument civilDocument,
            StormwaterProductionSettings settings,
            IReadOnlyList<StormwaterAlignmentPlan> plans,
            out int alignmentsCreated,
            out int labelsCreated)
        {
            alignmentsCreated = 0;
            labelsCreated = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                StormwaterMetadata.EnsureRegApp(database, transaction);

                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    settings.AlignmentLayer,
                    DefaultAlignmentLayer);
                string actualAlignmentStyle;
                ObjectId alignmentStyleId = ResolveStyleId(
                    civilDocument.Styles.AlignmentStyles,
                    settings.AlignmentStyle,
                    "alignment style",
                    transaction,
                    out actualAlignmentStyle);
                string actualLabelSet;
                ObjectId alignmentLabelSetId = ResolveStyleId(
                    civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles,
                    settings.AlignmentLabelSetStyle,
                    "alignment label-set style",
                    transaction,
                    out actualLabelSet);

                BlockTable blockTable = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                BlockTableRecord modelSpace =
                    (BlockTableRecord)transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace],
                        OpenMode.ForWrite,
                        false);

                var reservedNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                for (int index = 0; index < plans.Count; index++)
                {
                    StormwaterAlignmentPlan plan = plans[index];
                    RemoveGeneratedProfileObjectsForBranch(
                        database,
                        civilDocument,
                        plan.BranchKey,
                        transaction);
                    RemoveGeneratedAlignmentObjects(
                        civilDocument,
                        modelSpace,
                        plan.BranchKey,
                        transaction);

                    ObjectId sourcePolylineId = AddSourcePolyline(
                        database,
                        modelSpace,
                        plan,
                        layerId,
                        transaction);

                    var options = new CivilPolylineOptions
                    {
                        AddCurvesBetweenTangents = false,
                        EraseExistingEntities = true,
                        PlineId = sourcePolylineId
                    };

                    string alignmentName = ResolveAlignmentName(
                        civilDocument,
                        plan.BranchKey,
                        reservedNames,
                        transaction);
                    ObjectId alignmentId = CivilAlignment.Create(
                        civilDocument,
                        options,
                        alignmentName,
                        ObjectId.Null,
                        layerId,
                        alignmentStyleId,
                        alignmentLabelSetId);

                    var alignment = transaction.GetObject(
                        alignmentId,
                        OpenMode.ForWrite,
                        false) as CivilAlignment;
                    if (alignment == null)
                        throw new InvalidOperationException(
                            "Civil 3D did not return the created stormwater alignment.");

                    alignment.Description =
                        "CE stormwater | " + plan.BranchKey +
                        " | " + plan.SourceKind +
                        " | style=" + actualAlignmentStyle +
                        " | labels=" + actualLabelSet;
                    WriteProductionTag(
                        alignment,
                        StormwaterMetadata.AlignmentRegAppName,
                        plan.BranchKey,
                        "Alignment",
                        plan.SourceKind,
                        plan.SourceHandles);
                    alignmentsCreated++;

                    bool placeAbove = string.Equals(
                            settings.BranchLabelSide,
                            "Above",
                            StringComparison.OrdinalIgnoreCase) ||
                        (!string.Equals(
                            settings.BranchLabelSide,
                            "Below",
                            StringComparison.OrdinalIgnoreCase) &&
                         index % 2 == 0);
                    IReadOnlyList<SewerBranchLabelPlacement.Placement> placements =
                        SewerBranchLabelPlacement.BuildPlacements(plan.PlanPoints);
                    foreach (SewerBranchLabelPlacement.Placement placement in placements)
                    {
                        var label = new MText();
                        label.SetDatabaseDefaults(database);
                        label.LayerId = layerId;
                        SewerBranchLabelPlacement.ConfigureLabel(
                            label,
                            database,
                            placement,
                            plan.BranchKey,
                            settings.LabelTextHeight,
                            placeAbove);
                        WriteProductionTag(
                            label,
                            StormwaterMetadata.AlignmentRegAppName,
                            plan.BranchKey,
                            "Label",
                            plan.SourceKind,
                            plan.SourceHandles);
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        labelsCreated++;
                    }
                }

                transaction.Commit();
            }
        }

        private static ObjectId AddSourcePolyline(
            Database database,
            BlockTableRecord modelSpace,
            StormwaterAlignmentPlan plan,
            ObjectId layerId,
            Transaction transaction)
        {
            Polyline polyline = null;
            if (plan.SourceKind == "Polyline" &&
                !plan.SourcePolylineId.IsNull)
            {
                var source = transaction.GetObject(
                    plan.SourcePolylineId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (source != null)
                    polyline = source.Clone() as Polyline;
            }

            if (polyline == null)
            {
                polyline = new Polyline(plan.PlanPoints.Count);
                for (int index = 0; index < plan.PlanPoints.Count; index++)
                {
                    Point3d point = plan.PlanPoints[index];
                    polyline.AddVertexAt(
                        index,
                        new Point2d(point.X, point.Y),
                        0.0,
                        0.0,
                        0.0);
                }
            }

            polyline.SetDatabaseDefaults(database);
            polyline.LayerId = layerId;
            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return polyline.ObjectId;
        }

        private static void CreateProfiles(
            Database database,
            CivilDocument civilDocument,
            StormwaterProductionSettings settings,
            ObjectId surfaceId,
            IReadOnlyList<StormwaterAlignmentRecord> alignments,
            Point3d basePoint,
            int columns,
            double horizontalSpacing,
            double verticalSpacing,
            out int profilesCreated,
            out int viewsCreated,
            out int partsAdded)
        {
            profilesCreated = 0;
            viewsCreated = 0;
            partsAdded = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                StormwaterMetadata.EnsureRegApp(database, transaction);

                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    settings.ProfileLayer,
                    DefaultProfileLayer);
                string profileStyleName;
                ObjectId profileStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileStyles,
                    settings.ProfileStyle,
                    "profile style",
                    transaction,
                    out profileStyleName);
                string profileLabelSetName;
                ObjectId profileLabelSetId = ResolveStyleId(
                    civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles,
                    settings.ProfileLabelSetStyle,
                    "profile label-set style",
                    transaction,
                    out profileLabelSetName);
                string profileViewStyleName;
                ObjectId profileViewStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewStyles,
                    settings.ProfileViewStyle,
                    "profile-view style",
                    transaction,
                    out profileViewStyleName);
                string bandSetName;
                ObjectId bandSetStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewBandSetStyles,
                    settings.ProfileViewBandSetStyle,
                    "profile-view band-set style",
                    transaction,
                    out bandSetName);

                BlockTable blockTable = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                BlockTableRecord modelSpace =
                    (BlockTableRecord)transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace],
                        OpenMode.ForWrite,
                        false);

                for (int index = 0; index < alignments.Count; index++)
                {
                    StormwaterAlignmentRecord record = alignments[index];
                    RemoveGeneratedProfileObjectsForBranch(
                        database,
                        civilDocument,
                        record.BranchKey,
                        transaction);

                    string profileName =
                        ResolveCivilObjectName(
                            record.BranchKey + "-EG",
                            record.BranchKey + "-EG-" +
                            DateTime.UtcNow.Ticks.ToString(
                                CultureInfo.InvariantCulture));
                    ObjectId profileId = CreateSurfaceProfileByReflection(
                        profileName,
                        record.AlignmentId,
                        surfaceId,
                        layerId,
                        profileStyleId,
                        profileLabelSetId);
                    DBObject profile = transaction.GetObject(
                        profileId,
                        OpenMode.ForWrite,
                        false);
                    WriteProductionTag(
                        profile,
                        StormwaterMetadata.ProfileRegAppName,
                        record.BranchKey,
                        "Profile",
                        record.SourceKind,
                        record.SourceHandles);
                    profilesCreated++;

                    int row = index / columns;
                    int column = index % columns;
                    Point3d location = new Point3d(
                        basePoint.X + column * horizontalSpacing,
                        basePoint.Y - row * verticalSpacing,
                        basePoint.Z);
                    string profileViewName =
                        ResolveCivilObjectName(
                            record.BranchKey + "-PROFILE",
                            record.BranchKey + "-PROFILE-" +
                            DateTime.UtcNow.Ticks.ToString(
                                CultureInfo.InvariantCulture));
                    ObjectId profileViewId =
                        CreateProfileViewByReflection(
                            profileViewName,
                            record.AlignmentId,
                            location,
                            bandSetStyleId,
                            profileViewStyleId);
                    DBObject profileView = transaction.GetObject(
                        profileViewId,
                        OpenMode.ForWrite,
                        false);
                    ProfileStyleLinker.Apply(
                        profileView,
                        profileViewStyleId,
                        bandSetStyleId);
                    WriteProductionTag(
                        profileView,
                        StormwaterMetadata.ProfileRegAppName,
                        record.BranchKey,
                        "ProfileView",
                        record.SourceKind,
                        record.SourceHandles);
                    viewsCreated++;

                    ObjectId bandNetworkId = ObjectId.Null;
                    if (record.SourceKind == "Network")
                    {
                        foreach (string handleText in record.SourceHandles)
                        {
                            ObjectId sourceId;
                            if (!TryGetObjectId(
                                    database,
                                    handleText,
                                    out sourceId))
                                continue;

                            DBObject part = transaction.GetObject(
                                sourceId,
                                OpenMode.ForRead,
                                false);
                            CivilPipe bandPipe = part as CivilPipe;
                            CivilStructure bandStructure = part as CivilStructure;
                            if (bandPipe != null && !bandPipe.NetworkId.IsNull)
                                bandNetworkId = bandPipe.NetworkId;
                            else if (bandStructure != null && !bandStructure.NetworkId.IsNull)
                                bandNetworkId = bandStructure.NetworkId;
                            if (TryAddPartToProfileView(
                                    part,
                                    profileViewId))
                                partsAdded++;
                        }
                    }
                    ProfileViewBandDataBinder.Bind(
                        profileView,
                        profileId,
                        ObjectId.Null,
                        bandNetworkId);

                    var title = new MText();
                    title.SetDatabaseDefaults(database);
                    title.LayerId = layerId;
                    title.Location = location +
                        new Vector3d(
                            0.0,
                            PaperAnnotationScale.ModelDistance(
                                database,
                                settings.LabelTextHeight * 4.0),
                            0.0);
                    title.Attachment = AttachmentPoint.BottomLeft;
                    title.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.LabelTextHeight);
                    PaperAnnotationScale.SetAnnotative(title);
                    title.Contents =
                        record.BranchKey +
                        " STORMWATER PROFILE" +
                        "\\PProfile style: " + profileStyleName +
                        " | View: " + profileViewStyleName +
                        " | Bands: " + bandSetName;
                    WriteProductionTag(
                        title,
                        StormwaterMetadata.ProfileRegAppName,
                        record.BranchKey,
                        "Title",
                        record.SourceKind,
                        record.SourceHandles);
                    modelSpace.AppendEntity(title);
                    transaction.AddNewlyCreatedDBObject(title, true);
                }

                transaction.Commit();
            }
        }

        private static List<StormwaterAlignmentRecord> ReadGeneratedAlignments(
            CivilDocument civilDocument,
            Transaction transaction)
        {
            var records = new List<StormwaterAlignmentRecord>();

            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                string branch;
                string objectType;
                string sourceKind;
                List<string> sourceHandles;
                if (!TryReadProductionTag(
                        alignment,
                        StormwaterMetadata.AlignmentRegAppName,
                        out branch,
                        out objectType,
                        out sourceKind,
                        out sourceHandles) ||
                    objectType != "Alignment")
                    continue;

                records.Add(new StormwaterAlignmentRecord(
                    alignmentId,
                    branch,
                    sourceKind,
                    sourceHandles));
            }

            return records
                .OrderBy(record => BranchSortKey(record.BranchKey))
                .ToList();
        }

        private static void RemoveGeneratedAlignmentObjects(
            CivilDocument civilDocument,
            BlockTableRecord modelSpace,
            string branchKey,
            Transaction transaction)
        {
            foreach (ObjectId alignmentId in
                civilDocument.GetAlignmentIds().Cast<ObjectId>().ToList())
            {
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                string branch;
                string objectType;
                if (TryReadProductionTag(
                        alignment,
                        StormwaterMetadata.AlignmentRegAppName,
                        out branch,
                        out objectType) &&
                    branch == branchKey &&
                    objectType == "Alignment")
                {
                    alignment.UpgradeOpen();
                    alignment.Erase();
                }
            }

            foreach (ObjectId objectId in modelSpace.Cast<ObjectId>().ToList())
            {
                DBObject databaseObject = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false);
                string branch;
                string objectType;
                if (TryReadProductionTag(
                        databaseObject,
                        StormwaterMetadata.AlignmentRegAppName,
                        out branch,
                        out objectType) &&
                    branch == branchKey &&
                    objectType == "Label")
                {
                    databaseObject.UpgradeOpen();
                    databaseObject.Erase();
                }
            }
        }

        private static void RemoveGeneratedProfileObjectsForBranch(
            Database database,
            CivilDocument civilDocument,
            string branchKey,
            Transaction transaction)
        {
            BlockTable blockTable = (BlockTable)transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false);

            foreach (ObjectId blockId in blockTable)
            {
                BlockTableRecord block = transaction.GetObject(
                    blockId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (block == null)
                    continue;

                foreach (ObjectId objectId in block.Cast<ObjectId>().ToList())
                {
                    DBObject databaseObject = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false);
                    string branch;
                    string objectType;
                    if (TryReadProductionTag(
                            databaseObject,
                            StormwaterMetadata.ProfileRegAppName,
                            out branch,
                            out objectType) &&
                        branch == branchKey &&
                        (objectType == "ProfileView" ||
                         objectType == "Title"))
                    {
                        databaseObject.UpgradeOpen();
                        databaseObject.Erase();
                    }
                }
            }

            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                IEnumerable<ObjectId> profileIds =
                    ReadObjectIdsByReflection(
                        alignment,
                        "GetProfileIds");
                foreach (ObjectId profileId in profileIds.ToList())
                {
                    DBObject profile = transaction.GetObject(
                        profileId,
                        OpenMode.ForRead,
                        false);
                    string branch;
                    string objectType;
                    if (TryReadProductionTag(
                            profile,
                            StormwaterMetadata.ProfileRegAppName,
                            out branch,
                            out objectType) &&
                        branch == branchKey &&
                        objectType == "Profile")
                    {
                        profile.UpgradeOpen();
                        profile.Erase();
                    }
                }
            }
        }

        private static IEnumerable<ObjectId> ReadObjectIdsByReflection(
            object target,
            string methodName)
        {
            if (target == null)
                return Enumerable.Empty<ObjectId>();

            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
                return Enumerable.Empty<ObjectId>();

            object value = method.Invoke(target, null);
            var ids = value as IEnumerable;
            if (ids == null)
                return Enumerable.Empty<ObjectId>();

            var result = new List<ObjectId>();
            foreach (object item in ids)
            {
                if (item is ObjectId)
                    result.Add((ObjectId)item);
            }
            return result;
        }

        private static ObjectId CreateSurfaceProfileByReflection(
            string profileName,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId profileStyleId,
            ObjectId profileLabelSetId)
        {
            Type profileType = typeof(CivilAlignment).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.Profile",
                true);

            foreach (MethodInfo method in profileType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "CreateFromSurface")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!TryBuildProfileArguments(
                        method.GetParameters(),
                        profileName,
                        alignmentId,
                        surfaceId,
                        layerId,
                        profileStyleId,
                        profileLabelSetId,
                        out arguments))
                    continue;

                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException)
                {
                    // Try another overload before reporting failure.
                }
            }

            throw new InvalidOperationException(
                "This Civil 3D build did not expose a compatible Profile.CreateFromSurface overload.");
        }

        private static bool TryBuildProfileArguments(
            ParameterInfo[] parameters,
            string profileName,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId profileStyleId,
            ObjectId profileLabelSetId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            int unnamedObjectIdIndex = 0;
            ObjectId[] fallbackIds =
            {
                alignmentId,
                surfaceId,
                layerId,
                profileStyleId,
                profileLabelSetId
            };

            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string name = (parameter.Name ?? string.Empty).ToLowerInvariant();

                if (parameter.ParameterType == typeof(string))
                {
                    arguments[index] = profileName;
                    continue;
                }

                if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (name.Contains("alignment"))
                        arguments[index] = alignmentId;
                    else if (name.Contains("surface"))
                        arguments[index] = surfaceId;
                    else if (name.Contains("layer"))
                        arguments[index] = layerId;
                    else if (name.Contains("label"))
                        arguments[index] = profileLabelSetId;
                    else if (name.Contains("style"))
                        arguments[index] = profileStyleId;
                    else if (unnamedObjectIdIndex < fallbackIds.Length)
                        arguments[index] =
                            fallbackIds[unnamedObjectIdIndex++];
                    else
                        return false;
                    continue;
                }

                if (parameter.HasDefaultValue)
                {
                    arguments[index] = parameter.DefaultValue;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static ObjectId CreateProfileViewByReflection(
            string profileViewName,
            ObjectId alignmentId,
            Point3d insertionPoint,
            ObjectId bandSetStyleId,
            ObjectId profileViewStyleId)
        {
            Type viewType = typeof(CivilAlignment).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.ProfileView",
                true);

            foreach (MethodInfo method in viewType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!TryBuildProfileViewArguments(
                        method.GetParameters(),
                        profileViewName,
                        alignmentId,
                        insertionPoint,
                        bandSetStyleId,
                        profileViewStyleId,
                        out arguments))
                    continue;

                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException)
                {
                    // Try another overload before reporting failure.
                }
            }

            throw new InvalidOperationException(
                "This Civil 3D build did not expose a compatible ProfileView.Create overload.");
        }

        private static bool TryBuildProfileViewArguments(
            ParameterInfo[] parameters,
            string profileViewName,
            ObjectId alignmentId,
            Point3d insertionPoint,
            ObjectId bandSetStyleId,
            ObjectId profileViewStyleId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            int unnamedObjectIdIndex = 0;
            ObjectId[] fallbackIds =
            {
                alignmentId,
                bandSetStyleId,
                profileViewStyleId
            };

            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string name = (parameter.Name ?? string.Empty).ToLowerInvariant();

                if (parameter.ParameterType == typeof(string))
                {
                    arguments[index] = profileViewName;
                    continue;
                }

                if (parameter.ParameterType == typeof(Point3d))
                {
                    arguments[index] = insertionPoint;
                    continue;
                }

                if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (name.Contains("alignment"))
                        arguments[index] = alignmentId;
                    else if (name.Contains("band"))
                        arguments[index] = bandSetStyleId;
                    else if (name.Contains("style"))
                        arguments[index] = profileViewStyleId;
                    else if (unnamedObjectIdIndex < fallbackIds.Length)
                        arguments[index] =
                            fallbackIds[unnamedObjectIdIndex++];
                    else
                        return false;
                    continue;
                }

                if (parameter.HasDefaultValue)
                {
                    arguments[index] = parameter.DefaultValue;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool TryAddPartToProfileView(
            DBObject part,
            ObjectId profileViewId)
        {
            if (part == null)
                return false;

            MethodInfo method = part.GetType().GetMethod(
                "AddToProfileView",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(ObjectId) },
                null);
            if (method == null)
                return false;

            try
            {
                method.Invoke(part, new object[] { profileViewId });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ObjectId ResolveStyleId(
            IEnumerable<ObjectId> styleIds,
            string requestedName,
            string description,
            Transaction transaction,
            out string actualName)
        {
            List<ObjectId> ids = styleIds.ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException(
                    "The drawing contains no " + description + ".");

            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                foreach (ObjectId id in ids)
                {
                    DBObject style = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                    string name = ReadName(style);
                    if (name.Equals(
                            requestedName.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        actualName = name;
                        return id;
                    }
                }

                throw new InvalidOperationException(
                    "The requested " + description + " '" +
                    requestedName + "' was not found.");
            }

            DBObject first = transaction.GetObject(
                ids[0],
                OpenMode.ForRead,
                false);
            actualName = ReadName(first);
            return ids[0];
        }

        private static string ReadName(object value)
        {
            if (value == null)
                return "(unnamed)";

            PropertyInfo property = value.GetType().GetProperty(
                "Name",
                BindingFlags.Public | BindingFlags.Instance);
            object name = property == null
                ? null
                : property.GetValue(value, null);
            return Convert.ToString(
                name,
                CultureInfo.InvariantCulture) ?? "(unnamed)";
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requestedName,
            string fallbackName)
        {
            string layerName = string.IsNullOrWhiteSpace(requestedName)
                ? fallbackName
                : requestedName.Trim();

            LayerTable table = (LayerTable)transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false);
            if (table.Has(layerName))
            {
                ObjectId id = table[layerName];
                LayerTableRecord existing = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (existing != null && existing.IsLocked)
                    throw new InvalidOperationException(
                        "Layer '" + layerName + "' is locked.");
                return id;
            }

            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = layerName };
            ObjectId layerId = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static string ResolveAlignmentName(
            CivilDocument civilDocument,
            string branchKey,
            ISet<string> reservedNames,
            Transaction transaction)
        {
            var existing = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                var alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                if (alignment != null)
                    existing.Add(alignment.Name);
            }

            string candidate = branchKey;
            if (!existing.Contains(candidate) &&
                reservedNames.Add(candidate))
                return candidate;

            string baseName = "SW-" + branchKey;
            candidate = baseName;
            int suffix = 2;
            while (existing.Contains(candidate) ||
                   !reservedNames.Add(candidate))
            {
                candidate = baseName + "-" +
                    suffix.ToString(
                        CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        private static string ResolveCivilObjectName(
            string preferred,
            string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred)
                ? fallback
                : preferred;
        }

        private static Point3d GetMidpoint(
            IReadOnlyList<Point3d> points)
        {
            if (points == null || points.Count == 0)
                return Point3d.Origin;
            if (points.Count == 1)
                return points[0];

            double total = 0.0;
            for (int index = 1; index < points.Count; index++)
                total += points[index - 1].DistanceTo(points[index]);

            double target = total * 0.5;
            double travelled = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                Point3d start = points[index - 1];
                Point3d end = points[index];
                double length = start.DistanceTo(end);
                if (travelled + length >= target &&
                    length > GeometryTolerance)
                {
                    double fraction =
                        (target - travelled) / length;
                    return start +
                        (end - start) * fraction;
                }

                travelled += length;
            }

            return points[points.Count / 2];
        }

        private static Vector3d GetMidpointNormal(
            IReadOnlyList<Point3d> points)
        {
            if (points == null || points.Count < 2)
                return Vector3d.YAxis;

            int middle = Math.Max(
                1,
                points.Count / 2);
            Vector3d tangent =
                points[middle] -
                points[middle - 1];
            tangent = new Vector3d(
                tangent.X,
                tangent.Y,
                0.0);
            if (tangent.Length <= GeometryTolerance)
                return Vector3d.YAxis;

            tangent = tangent.GetNormal();
            return new Vector3d(
                -tangent.Y,
                tangent.X,
                0.0);
        }

        private static void WriteProductionTag(
            DBObject databaseObject,
            string regAppName,
            string branchKey,
            string objectType,
            string sourceKind,
            IEnumerable<string> sourceHandles)
        {
            databaseObject.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    regAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    branchKey ?? string.Empty),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    objectType ?? string.Empty),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    sourceKind ?? string.Empty),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    string.Join(
                        ",",
                        sourceHandles ?? Enumerable.Empty<string>())));
        }

        private static bool TryReadProductionTag(
            DBObject databaseObject,
            string regAppName,
            out string branchKey,
            out string objectType)
        {
            string sourceKind;
            List<string> sourceHandles;
            return TryReadProductionTag(
                databaseObject,
                regAppName,
                out branchKey,
                out objectType,
                out sourceKind,
                out sourceHandles);
        }

        private static bool TryReadProductionTag(
            DBObject databaseObject,
            string regAppName,
            out string branchKey,
            out string objectType,
            out string sourceKind,
            out List<string> sourceHandles)
        {
            branchKey = string.Empty;
            objectType = string.Empty;
            sourceKind = string.Empty;
            sourceHandles = new List<string>();

            using (ResultBuffer buffer =
                databaseObject.GetXDataForApplication(
                    regAppName))
            {
                if (buffer == null)
                    return false;

                string[] values = buffer.AsArray()
                    .Where(value =>
                        value.TypeCode ==
                        (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();

                if (values.Length < 4)
                    return false;

                branchKey = values[0];
                objectType = values[1];
                sourceKind = values[2];
                sourceHandles = values[3]
                    .Split(new[] { ',' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                return true;
            }
        }

        private static bool TryGetObjectId(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                return false;

            try
            {
                objectId = database.GetObjectId(
                    false,
                    new Handle(value),
                    0);
                return !objectId.IsNull &&
                       objectId.IsValid &&
                       !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static int BranchSortKey(string branchKey)
        {
            if (string.Equals(
                    branchKey,
                    "SW-MAIN",
                    StringComparison.OrdinalIgnoreCase))
                return 0;

            string digits = new string(
                (branchKey ?? string.Empty)
                .Where(char.IsDigit)
                .ToArray());
            int value;
            return int.TryParse(
                digits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value)
                ? value + 1
                : int.MaxValue;
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string current,
            out string value)
        {
            PromptStringOptions options = new PromptStringOptions(
                "\n" + label + " <" +
                DisplaySetting(current) +
                ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }

            value = result.Status == PromptStatus.OK
                ? result.StringResult.Trim()
                : current;
            return true;
        }

        private static string DisplaySetting(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "first available"
                : value;
        }

        private static bool Confirm(
            Editor editor,
            string message)
        {
            return DisciplineWorkflowDialogs.Confirm("CE Tools — Stormwater", message + "?");
        }

        private sealed class StormwaterAlignmentPlan
        {
            public StormwaterAlignmentPlan(
                string branchKey,
                string sourceKind,
                IReadOnlyList<Point3d> planPoints,
                IEnumerable<string> sourceHandles,
                ObjectId sourcePolylineId)
            {
                BranchKey = branchKey;
                SourceKind = sourceKind;
                PlanPoints = planPoints;
                SourceHandles = sourceHandles.ToList();
                SourcePolylineId = sourcePolylineId;
            }

            public string BranchKey { get; }
            public string SourceKind { get; }
            public IReadOnlyList<Point3d> PlanPoints { get; }
            public IReadOnlyList<string> SourceHandles { get; }
            public ObjectId SourcePolylineId { get; }
        }

        private sealed class StormwaterAlignmentRecord
        {
            public StormwaterAlignmentRecord(
                ObjectId alignmentId,
                string branchKey,
                string sourceKind,
                IReadOnlyList<string> sourceHandles)
            {
                AlignmentId = alignmentId;
                BranchKey = branchKey;
                SourceKind = sourceKind;
                SourceHandles = sourceHandles;
            }

            public ObjectId AlignmentId { get; }
            public string BranchKey { get; }
            public string SourceKind { get; }
            public IReadOnlyList<string> SourceHandles { get; }
        }

        private sealed class StormwaterPipeRecord
        {
            public StormwaterPipeRecord(
                ObjectId pipeId,
                ObjectId startStructureId,
                ObjectId endStructureId,
                int sequence)
            {
                PipeId = pipeId;
                StartStructureId = startStructureId;
                EndStructureId = endStructureId;
                Sequence = sequence;
            }

            public ObjectId PipeId { get; }
            public ObjectId StartStructureId { get; }
            public ObjectId EndStructureId { get; }
            public int Sequence { get; }

            public ObjectId Other(ObjectId structureId)
            {
                if (structureId == StartStructureId)
                    return EndStructureId;
                if (structureId == EndStructureId)
                    return StartStructureId;
                throw new InvalidOperationException(
                    "A pipe was queried from a structure it does not connect.");
            }
        }

        private sealed class OrderedPipePath
        {
            public OrderedPipePath(
                IReadOnlyList<ObjectId> structureIds,
                IReadOnlyList<ObjectId> pipeIds)
            {
                StructureIds = structureIds;
                PipeIds = pipeIds;
            }

            public IReadOnlyList<ObjectId> StructureIds { get; }
            public IReadOnlyList<ObjectId> PipeIds { get; }
        }

        private sealed class PolylineCandidate
        {
            public PolylineCandidate(
                ObjectId objectId,
                double length,
                IReadOnlyList<Point3d> points)
            {
                ObjectId = objectId;
                Length = length;
                Points = points;
            }

            public ObjectId ObjectId { get; }
            public double Length { get; }
            public IReadOnlyList<Point3d> Points { get; }
        }
    }

    internal sealed class StormwaterProductionSettings
    {
        public string AlignmentStyle { get; set; } = string.Empty;
        public string AlignmentLabelSetStyle { get; set; } = string.Empty;
        public string ProfileStyle { get; set; } = string.Empty;
        public string ProfileLabelSetStyle { get; set; } = string.Empty;
        public string ProfileViewStyle { get; set; } = string.Empty;
        public string ProfileViewBandSetStyle { get; set; } = string.Empty;
        public string AlignmentLayer { get; set; } = "CE-SW-ALIGNMENT";
        public string ProfileLayer { get; set; } = "CE-SW-PROFILE";
        public double LabelTextHeight { get; set; } = 2.5;
        public string BranchLabelSide { get; set; } = "Alternating";
        public int ProfileColumns { get; set; } = 2;
        public double ProfileHorizontalSpacing { get; set; } = 250.0;
        public double ProfileVerticalSpacing { get; set; } = 120.0;

        public static StormwaterProductionSettings Read(
            Database database)
        {
            var settings = new StormwaterProductionSettings();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects =
                    (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false);
                if (!namedObjects.Contains("CE_TOOLS"))
                    return settings;

                DBDictionary ceDictionary =
                    transaction.GetObject(
                        namedObjects.GetAt("CE_TOOLS"),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                if (ceDictionary == null ||
                    !ceDictionary.Contains(
                        "STORMWATER_PRODUCTION_SETTINGS"))
                    return settings;

                Xrecord record = transaction.GetObject(
                    ceDictionary.GetAt(
                        "STORMWATER_PRODUCTION_SETTINGS"),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null)
                    return settings;

                foreach (TypedValue value in record.Data)
                {
                    if (value.TypeCode !=
                        (int)DxfCode.Text)
                        continue;

                    string text = value.Value as string;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    int separator = text.IndexOf('=');
                    if (separator <= 0)
                        continue;

                    string key = text.Substring(0, separator);
                    string settingValue =
                        text.Substring(separator + 1);
                    Apply(settings, key, settingValue);
                }
            }

            return settings;
        }

        public void Write(Database database)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects =
                    (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false);

                DBDictionary ceDictionary;
                if (namedObjects.Contains("CE_TOOLS"))
                {
                    ceDictionary = transaction.GetObject(
                        namedObjects.GetAt("CE_TOOLS"),
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                }
                else
                {
                    ceDictionary = new DBDictionary();
                    namedObjects.SetAt(
                        "CE_TOOLS",
                        ceDictionary);
                    transaction.AddNewlyCreatedDBObject(
                        ceDictionary,
                        true);
                }

                Xrecord record;
                if (ceDictionary.Contains(
                        "STORMWATER_PRODUCTION_SETTINGS"))
                {
                    record = transaction.GetObject(
                        ceDictionary.GetAt(
                            "STORMWATER_PRODUCTION_SETTINGS"),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                }
                else
                {
                    record = new Xrecord();
                    ceDictionary.SetAt(
                        "STORMWATER_PRODUCTION_SETTINGS",
                        record);
                    transaction.AddNewlyCreatedDBObject(
                        record,
                        true);
                }

                record.Data = new ResultBuffer(
                    ToValue("AlignmentStyle", AlignmentStyle),
                    ToValue("AlignmentLabelSetStyle", AlignmentLabelSetStyle),
                    ToValue("ProfileStyle", ProfileStyle),
                    ToValue("ProfileLabelSetStyle", ProfileLabelSetStyle),
                    ToValue("ProfileViewStyle", ProfileViewStyle),
                    ToValue("ProfileViewBandSetStyle", ProfileViewBandSetStyle),
                    ToValue("AlignmentLayer", AlignmentLayer),
                    ToValue("ProfileLayer", ProfileLayer),
                    ToValue(
                        "LabelTextHeight",
                        LabelTextHeight.ToString(
                            "R",
                            CultureInfo.InvariantCulture)),
                    ToValue("BranchLabelSide", BranchLabelSide),
                    ToValue("ProfileColumns", ProfileColumns.ToString(CultureInfo.InvariantCulture)),
                    ToValue("ProfileHorizontalSpacing", ProfileHorizontalSpacing.ToString("R", CultureInfo.InvariantCulture)),
                    ToValue("ProfileVerticalSpacing", ProfileVerticalSpacing.ToString("R", CultureInfo.InvariantCulture)));

                transaction.Commit();
            }
        }

        private static TypedValue ToValue(
            string key,
            string value)
        {
            return new TypedValue(
                (int)DxfCode.Text,
                key + "=" + (value ?? string.Empty));
        }

        private static void Apply(
            StormwaterProductionSettings settings,
            string key,
            string value)
        {
            if (key == "AlignmentStyle")
                settings.AlignmentStyle = value;
            else if (key == "AlignmentLabelSetStyle")
                settings.AlignmentLabelSetStyle = value;
            else if (key == "ProfileStyle")
                settings.ProfileStyle = value;
            else if (key == "ProfileLabelSetStyle")
                settings.ProfileLabelSetStyle = value;
            else if (key == "ProfileViewStyle")
                settings.ProfileViewStyle = value;
            else if (key == "ProfileViewBandSetStyle")
                settings.ProfileViewBandSetStyle = value;
            else if (key == "AlignmentLayer")
                settings.AlignmentLayer = value;
            else if (key == "ProfileLayer")
                settings.ProfileLayer = value;
            else if (key == "LabelTextHeight")
            {
                double height;
                if (double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out height) &&
                    height > 0.0)
                    settings.LabelTextHeight = height;
            }
            else if (key == "BranchLabelSide")
                settings.BranchLabelSide = value;
            else if (key == "ProfileColumns")
            {
                int columns;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out columns) && columns > 0)
                    settings.ProfileColumns = columns;
            }
            else if (key == "ProfileHorizontalSpacing")
            {
                double spacing;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out spacing) && spacing > 0.0)
                    settings.ProfileHorizontalSpacing = spacing;
            }
            else if (key == "ProfileVerticalSpacing")
            {
                double spacing;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out spacing) && spacing > 0.0)
                    settings.ProfileVerticalSpacing = spacing;
            }
        }
    }
}
