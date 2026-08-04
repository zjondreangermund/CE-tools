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

[assembly: CommandClass(typeof(CETools.Civil3D.SewerProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Completes the sewer production workflow around the existing CE_SEWSEQ and
    /// CE_SEWALIGN commands. It adds a selected-main whole-network sequence,
    /// repeatable alignment formatting, linked existing-ground profiles/profile
    /// views, explicit refresh and drawing-specific style settings.
    /// </summary>
    public sealed class SewerProductionCommands
    {
        private const string AlignmentRegAppName = "CE_TOOLS_SEWALIGN";
        private const string ProfileRegAppName = "CE_TOOLS_SEWPROFILE";
        private const string SettingsDictionaryName = "CE_TOOLS";
        private const string SettingsRecordName = "SEWER_PRODUCTION_SETTINGS";
        private const string DefaultProfileLayer = "CE-SEWER-PROFILE";
        private const double Tolerance = 1e-8;

        [CommandMethod("CE_SEWTOOLS", CommandFlags.Modal)]
        public void SewerTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;
            string command = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools — Sewer Workflow",
                "Sequence a complete network automatically or select Branch-1 first, then create linked alignments, profiles and production labels.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Choose production styles", "CE_SEWSETTINGS", "Choose alignment/profile styles, alignment/profile label sets, profile-view style, band set, and pipe/structure labels before production starts.", "0 — Production setup"),
                    new DisciplineWorkflowAction("Automatic network sequence", "CE_SEWSEQ", "Sequence and number the complete connected sewer network.", "1 — Network"),
                    new DisciplineWorkflowAction("Sequence with selected main", "CE_SEWSEQMAIN", "Select the intended main route before branch numbering.", "1 — Network"),
                    new DisciplineWorkflowAction("Create / refresh Civil labels", "CE_SEWLABELS", "Add the selected Civil 3D pipe and structure plan labels without duplicating existing labels.", "1 — Network"),
                    new DisciplineWorkflowAction("Create sewer alignments", "CE_SEWALIGN", "Create linked branch alignments from the sequenced network.", "2 — Alignments"),
                    new DisciplineWorkflowAction("Refresh alignments", "CE_SEWREFRESH", "Rebuild generated sewer alignments from their live network sources.", "2 — Alignments"),
                    new DisciplineWorkflowAction("Format alignments and labels", "CE_SEWFORMAT", "Reapply production styles and repeated branch labels.", "2 — Alignments"),
                    new DisciplineWorkflowAction("Create sewer profiles", "CE_SEWPROFILE", "Select a surface, pick an insertion point and create profile views.", "3 — Profiles"),
                    new DisciplineWorkflowAction("Sewer information", "CE_SEWINFO", "Review settings, alignments, profile views and network links.", "5 — Review")
                });
            if (!string.IsNullOrWhiteSpace(command))
                document.SendStringToExecute(command.Trim() + " ", true, false, true);
        }

        [CommandMethod("CE_SEWSEQMAIN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SequenceWithSelectedMain()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;

            PromptEntityOptions partOptions = new PromptEntityOptions(
                "\nSelect one sewer pipe or structure from the network: ");
            PromptEntityResult partResult = editor.GetEntity(partOptions);
            if (partResult.Status != PromptStatus.OK)
                return;

            ObjectId networkId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject selected = transaction.GetObject(
                    partResult.ObjectId,
                    OpenMode.ForRead,
                    false);
                var pipe = selected as CivilPipe;
                var structure = selected as CivilStructure;
                networkId = pipe != null
                    ? pipe.NetworkId
                    : structure != null
                        ? structure.NetworkId
                        : ObjectId.Null;
            }

            if (networkId.IsNull)
            {
                editor.WriteMessage(
                    "\nCE_SEWSEQMAIN: select a Civil 3D gravity-network pipe or structure.");
                return;
            }

            ObjectId startId;
            ObjectId endId;
            if (!PromptMainStructures(
                    editor,
                    database,
                    networkId,
                    out startId,
                    out endId))
                return;

            SewerSequencePlan plan;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    SewerGraph graph = BuildGraph(networkId, transaction);
                    SewerPath main = FindPath(graph, startId, endId);
                    OrientHighToLow(main, graph);
                    List<SewerPath> branches = ExtractBranches(graph, main);
                    plan = new SewerSequencePlan(graph, main, branches);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWSEQMAIN cancelled. " + exception.Message);
                return;
            }

            editor.WriteMessage(
                "\nCE_SEWSEQMAIN preview for network '{0}': Branch-1 main pipes={1}; side branches={2}.",
                plan.Graph.NetworkName,
                plan.Main.Edges.Count,
                plan.Branches.Count);
            for (int index = 0; index < plan.Branches.Count; index++)
            {
                editor.WriteMessage(
                    "\n  Branch-{0}: pipes={1}; length={2:0.###}.",
                    index + 2,
                    plan.Branches[index].Edges.Count,
                    plan.Branches[index].Length);
            }

            if (!Confirm(
                    editor,
                    "Rename the complete sewer network using the selected route as Branch-1"))
            {
                editor.WriteMessage(
                    "\nCE_SEWSEQMAIN cancelled. No names were changed.");
                return;
            }

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    ApplySelectedMainPlan(plan, transaction);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_SEWSEQMAIN complete. Branches: {0}; pipes: {1}; structures: {2}.",
                    plan.Branches.Count + 1,
                    plan.Graph.Edges.Count,
                    plan.Graph.Nodes.Count);
                SewerNetworkLabelCommands.EnsureLabels(
                    document,
                    new[] { networkId });
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SEWSEQMAIN cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SEWSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            SewerProductionSettings settings = SewerProductionSettings.Read(document.Database);
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            var model = new ProductionSettingsDialogModel(
                "CE Tools — Sewer Settings",
                "Blank style names use the first compatible drawing style. Label height is a paper height; layout values control batch profile-view placement.");
            model.AddChoice("AlignmentStyle", "Civil 3D Styles", "Alignment style", settings.AlignmentStyle, "Select an installed style for generated sewer branch alignments.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.AlignmentStyles, "Alignment Style"));
            model.AddChoice("AlignmentLabelSetStyle", "Civil 3D Styles", "Alignment label-set style", settings.AlignmentLabelSetStyle, "Label set applied automatically to generated sewer alignments.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles, "Alignment Label Set Style"));
            model.AddChoice("ProfileStyle", "Civil 3D Styles", "Profile style", settings.ProfileStyle, "Style for generated existing-ground profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileStyles, "Profile Style"));
            model.AddChoice("ProfileLabelSetStyle", "Civil 3D Styles", "Profile label-set style", settings.ProfileLabelSetStyle, "Label set for generated profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles, "Profile Label Set Style"));
            model.AddChoice("ProfileViewStyle", "Civil 3D Styles", "Profile-view style", settings.ProfileViewStyle, "Style for generated sewer profile views.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewStyles, "Profile View Style"));
            model.AddChoice("ProfileViewBandSetStyle", "Civil 3D Styles", "Profile-view band-set style", settings.ProfileViewBandSetStyle, "Band set for generated profile views.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewBandSetStyles, "Profile View Band Set Style"));
            model.AddChoice("PipePlanLabelStyle", "Civil 3D Styles", "Pipe plan-label style", settings.PipePlanLabelStyle, "Plan label added automatically to sewer pipes after sequencing.", SewerNetworkLabelCommands.ReadPipeLabelStyleNames(document));
            model.AddChoice("StructurePlanLabelStyle", "Civil 3D Styles", "Structure plan-label style", settings.StructurePlanLabelStyle, "Plan label added automatically to sewer manholes after sequencing.", SewerNetworkLabelCommands.ReadStructureLabelStyleNames(document));
            model.AddText("ProfileLayer", "Layers and Annotation", "Profile output layer", settings.ProfileLayer, "Layer for sewer profiles and profile views.");
            model.AddPaperHeight("LabelHeight", "Layers and Annotation", "Plan branch-label paper height", settings.LabelHeight, "Select a standard annotative paper height or enter another positive value.");
            model.AddChoice("BranchLabelSide", "Layers and Annotation", "Branch-label offset side", settings.BranchLabelSide, "Place every branch name above, below, or alternate sides while keeping it clear of the alignment.", new[] { "Alternating", "Above", "Below" });
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
            settings.PipePlanLabelStyle = model.Text("PipePlanLabelStyle");
            settings.StructurePlanLabelStyle = model.Text("StructurePlanLabelStyle");
            settings.ProfileLayer = model.Text("ProfileLayer");
            settings.LabelHeight = model.Double("LabelHeight", settings.LabelHeight);
            settings.BranchLabelSide = model.Text("BranchLabelSide");
            settings.ProfileColumns = model.Integer("ProfileColumns", settings.ProfileColumns);
            settings.ProfileHorizontalSpacing = model.Double("ProfileHorizontalSpacing", settings.ProfileHorizontalSpacing);
            settings.ProfileVerticalSpacing = model.Double("ProfileVerticalSpacing", settings.ProfileVerticalSpacing);

            settings.Write(document.Database);
            document.Editor.WriteMessage("\nCE_SEWSETTINGS saved in the current DWG.");
        }

        [CommandMethod("CE_SEWREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAlignments()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            var selectedParts = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var networkHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SewerAlignmentRecord record in ReadGeneratedAlignments(civilDocument, transaction))
                    networkHandles.Add(record.NetworkHandle);

                foreach (string handleText in networkHandles)
                {
                    ObjectId networkId;
                    if (!TryGetObjectId(document.Database, handleText, out networkId))
                        continue;

                    var network = transaction.GetObject(
                        networkId,
                        OpenMode.ForRead,
                        false) as CivilNetwork;
                    if (network == null)
                        continue;

                    ObjectId partId = network.GetPipeIds().Cast<ObjectId>().FirstOrDefault();
                    if (partId.IsNull)
                        partId = network.GetStructureIds().Cast<ObjectId>().FirstOrDefault();
                    if (!partId.IsNull)
                        selectedParts.Add(partId);
                }
            }

            if (selectedParts.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWREFRESH: no linked CE sewer alignments with live source networks were found.");
                return;
            }

            document.Editor.SetImpliedSelection(selectedParts.ToArray());
            document.Editor.WriteMessage(
                "\nCE_SEWREFRESH prepared {0} source network(s). CE_SEWALIGN will open its preview before changing anything.",
                selectedParts.Count);
            document.SendStringToExecute("CE_SEWALIGN ", true, false, true);
        }

        [CommandMethod("CE_SEWFORMAT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FormatAlignmentsAndLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Database database = document.Database;
            SewerProductionSettings settings = SewerProductionSettings.Read(database);
            int styled = 0;
            int labelsMoved = 0;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    string actualStyle;
                    ObjectId styleId = ResolveStyleId(
                        civilDocument.Styles.AlignmentStyles,
                        settings.AlignmentStyle,
                        "alignment style",
                        transaction,
                        out actualStyle);

                    List<SewerAlignmentRecord> records = ReadGeneratedAlignments(
                        civilDocument,
                        transaction);
                    var alignmentByBranch = new Dictionary<string, CivilAlignment>(
                        StringComparer.OrdinalIgnoreCase);
                    var recordByBranch = new Dictionary<string, SewerAlignmentRecord>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (SewerAlignmentRecord record in records)
                    {
                        var alignment = transaction.GetObject(
                            record.AlignmentId,
                            OpenMode.ForWrite,
                            false) as CivilAlignment;
                        if (alignment == null)
                            continue;

                        SetStyleByReflection(alignment, styleId);
                        alignment.Description =
                            "CE sewer alignment - " + record.BranchName +
                            " | style=" + actualStyle;
                        alignmentByBranch[record.BranchKey] = alignment;
                        recordByBranch[record.BranchKey] = record;
                        styled++;
                    }

                    BlockTable blocks = (BlockTable)transaction.GetObject(
                        database.BlockTableId,
                        OpenMode.ForRead,
                        false);
                    BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                        blocks[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead,
                        false);

                    foreach (ObjectId objectId in modelSpace)
                    {
                        var label = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false) as MText;
                        if (label == null)
                            continue;

                        string branchKey;
                        string objectType;
                        if (!TryReadAlignmentTag(label, out branchKey, out objectType) ||
                            objectType != "Label")
                            continue;

                        CivilAlignment branchAlignment;
                        SewerAlignmentRecord branchRecord;
                        if (!alignmentByBranch.TryGetValue(branchKey, out branchAlignment) ||
                            !recordByBranch.TryGetValue(branchKey, out branchRecord))
                            continue;

                        label.UpgradeOpen();
                        SewerBranchLabelPlacement.Placement placement;
                        if (!TryBuildLabelPlacement(
                                branchAlignment,
                                label.Location,
                                out placement))
                            continue;
                        bool placeAbove = string.Equals(
                                settings.BranchLabelSide,
                                "Above",
                                StringComparison.OrdinalIgnoreCase) ||
                            (!string.Equals(
                                settings.BranchLabelSide,
                                "Below",
                                StringComparison.OrdinalIgnoreCase) &&
                             (BranchNumber(branchRecord.BranchName) % 2) != 0);
                        SewerBranchLabelPlacement.ConfigureLabel(
                            label,
                            database,
                            placement,
                            branchRecord.BranchName,
                            settings.LabelHeight,
                            placeAbove);
                        labelsMoved++;
                    }

                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_SEWFORMAT complete. Alignments styled: {0}; labels repositioned: {1}.",
                    styled,
                    labelsMoved);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWFORMAT cancelled. " + exception.Message);
            }
        }

        private static bool TryBuildLabelPlacement(
            CivilAlignment alignment,
            Point3d currentLocation,
            out SewerBranchLabelPlacement.Placement placement)
        {
            placement = null;
            if (alignment == null) return false;
            try
            {
                double station = 0.0;
                double ignoredOffset = 0.0;
                alignment.StationOffset(
                    currentLocation.X,
                    currentLocation.Y,
                    ref station,
                    ref ignoredOffset);
                station = Math.Max(
                    alignment.StartingStation,
                    Math.Min(alignment.EndingStation, station));
                double span = Math.Max(
                    alignment.EndingStation - alignment.StartingStation,
                    0.001);
                double delta = Math.Max(0.001, Math.Min(span * 0.001, 0.10));
                double before = Math.Max(alignment.StartingStation, station - delta);
                double after = Math.Min(alignment.EndingStation, station + delta);
                double x = 0.0;
                double y = 0.0;
                double x1 = 0.0;
                double y1 = 0.0;
                double x2 = 0.0;
                double y2 = 0.0;
                alignment.PointLocation(station, 0.0, ref x, ref y);
                alignment.PointLocation(before, 0.0, ref x1, ref y1);
                alignment.PointLocation(after, 0.0, ref x2, ref y2);
                double rotation = Math.Atan2(y2 - y1, x2 - x1);
                while (rotation > Math.PI / 2.0) rotation -= Math.PI;
                while (rotation < -Math.PI / 2.0) rotation += Math.PI;
                placement = new SewerBranchLabelPlacement.Placement(
                    new Point3d(x, y, currentLocation.Z),
                    rotation);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [CommandMethod("CE_SEWPROFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;

            List<SurfaceChoice> surfaceChoices =
                WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaceChoices.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SEWPROFILE cancelled. The drawing contains no Civil 3D surfaces.");
                return;
            }
            var surfaceWindow = new SurfaceSelectionWindow(
                surfaceChoices,
                "Select the existing-ground surface for the sewer profiles. Double-clicking a row also selects it.");
            AcApplication.ShowModalWindow(surfaceWindow);
            SurfaceChoice selectedSurface = surfaceWindow.SelectedSurface;
            if (selectedSurface == null)
                return;
            SewerProductionSettings settings = SewerProductionSettings.Read(database);

            PromptPointResult pointResult = editor.GetPoint(
                "\nSpecify the upper-left insertion point for the first sewer profile view: ");
            if (pointResult.Status != PromptStatus.OK)
                return;

            List<SewerAlignmentRecord> records;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
                records = ReadGeneratedAlignments(civilDocument, transaction);

            if (records.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SEWPROFILE: no CE-generated sewer alignments were found. Run CE_SEWALIGN first.");
                return;
            }

            editor.WriteMessage(
                "\nCE_SEWPROFILE preview. Branch alignments: {0}; views per row: {1}.",
                records.Count,
                settings.ProfileColumns);
            if (!Confirm(editor, "Create or refresh the sewer profiles and profile views"))
                return;

            try
            {
                int profiles;
                int views;
                int parts;
                CreateProfileObjects(
                    database,
                    civilDocument,
                    settings,
                    selectedSurface.ObjectId,
                    records,
                    pointResult.Value,
                    settings.ProfileColumns,
                    settings.ProfileHorizontalSpacing,
                    settings.ProfileVerticalSpacing,
                    out profiles,
                    out views,
                    out parts);

                editor.WriteMessage(
                    "\nCE_SEWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",
                    profiles,
                    views,
                    parts);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SEWPROFILE cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_SEWINFO", CommandFlags.Modal)]
        public void ShowInformation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null)
                return;

            SewerProductionSettings settings = SewerProductionSettings.Read(document.Database);
            int alignments = 0;
            int labels = 0;
            int profileViews = 0;
            var networks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                if (civilDocument != null)
                {
                    List<SewerAlignmentRecord> records = ReadGeneratedAlignments(civilDocument, transaction);
                    alignments = records.Count;
                    foreach (SewerAlignmentRecord record in records)
                        networks.Add(record.NetworkHandle);
                }

                BlockTable blocks = (BlockTable)transaction.GetObject(
                    document.Database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId blockId in blocks)
                {
                    var block = transaction.GetObject(
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
                        string branch;
                        string type;
                        if (TryReadAlignmentTag(databaseObject, out branch, out type) &&
                            type == "Label")
                            labels++;
                        if (TryReadProfileTag(databaseObject, out branch, out type) &&
                            type == "ProfileView")
                            profileViews++;
                    }
                }
            }

            document.Editor.WriteMessage(
                "\nCE sewer production information:" +
                "\n  Source networks: " + networks.Count +
                "\n  Generated alignments: " + alignments +
                "\n  Generated plan labels: " + labels +
                "\n  Generated profile views: " + profileViews +
                "\n  Alignment style: " + Display(settings.AlignmentStyle) +
                "\n  Profile style: " + Display(settings.ProfileStyle) +
                "\n  Profile label set: " + Display(settings.ProfileLabelSetStyle) +
                "\n  Profile-view style: " + Display(settings.ProfileViewStyle) +
                "\n  Band-set style: " + Display(settings.ProfileViewBandSetStyle) +
                "\n  Profile layer: " + settings.ProfileLayer +
                "\n  Refresh model: explicit CE_SEWREFRESH / CE_SEWPROFILE; native surface-profile linkage remains controlled by Civil 3D.");
        }

        private static bool PromptMainStructures(
            Editor editor,
            Database database,
            ObjectId networkId,
            out ObjectId startId,
            out ObjectId endId)
        {
            startId = ObjectId.Null;
            endId = ObjectId.Null;
            PromptEntityOptions firstOptions = new PromptEntityOptions(
                "\nSelect the first structure on the intended Branch-1 main: ");
            firstOptions.SetRejectMessage("\nSelect a Civil 3D gravity-network structure.");
            firstOptions.AddAllowedClass(typeof(CivilStructure), true);
            PromptEntityResult first = editor.GetEntity(firstOptions);
            if (first.Status != PromptStatus.OK)
                return false;

            PromptEntityOptions lastOptions = new PromptEntityOptions(
                "\nSelect the last structure on the intended Branch-1 main: ");
            lastOptions.SetRejectMessage("\nSelect a Civil 3D gravity-network structure.");
            lastOptions.AddAllowedClass(typeof(CivilStructure), true);
            PromptEntityResult last = editor.GetEntity(lastOptions);
            if (last.Status != PromptStatus.OK)
                return false;

            if (first.ObjectId == last.ObjectId)
            {
                editor.WriteMessage("\nSelect two different main-route structures.");
                return false;
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var firstStructure = transaction.GetObject(
                    first.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                var lastStructure = transaction.GetObject(
                    last.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                if (firstStructure == null || lastStructure == null ||
                    firstStructure.NetworkId != networkId ||
                    lastStructure.NetworkId != networkId)
                {
                    editor.WriteMessage(
                        "\nBoth selected structures must belong to the selected sewer network.");
                    return false;
                }
            }

            startId = first.ObjectId;
            endId = last.ObjectId;
            return true;
        }

        private static SewerGraph BuildGraph(ObjectId networkId, Transaction transaction)
        {
            var network = transaction.GetObject(
                networkId,
                OpenMode.ForRead,
                false) as CivilNetwork;
            if (network == null)
                throw new InvalidOperationException("The selected part has no gravity network.");
            if (network.IsReferenceObject)
                throw new InvalidOperationException("Referenced sewer networks cannot be renamed.");

            var graph = new SewerGraph(networkId, network.Name);
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null)
                    continue;
                if (pipe.IsReferenceObject)
                    throw new InvalidOperationException("The network contains a referenced pipe.");
                if (pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull)
                    throw new InvalidOperationException("The network contains an unconnected pipe.");

                SewerNode start = GetNode(graph, pipe.StartStructureId, transaction);
                SewerNode end = GetNode(graph, pipe.EndStructureId, transaction);
                double length = pipe.Length3DCenterToCenter;
                if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0.0)
                    length = 1.0;
                var edge = new SewerEdge(pipeId, start.Id, end.Id, length);
                graph.Edges.Add(edge);
                start.Edges.Add(edge);
                end.Edges.Add(edge);
            }

            if (graph.Edges.Count == 0)
                throw new InvalidOperationException("The network contains no connected pipes.");
            if (graph.Edges.Count != graph.Nodes.Count - 1)
                throw new InvalidOperationException(
                    "Selected-main sequencing requires a connected tree. Loops or disconnected groups require engineering review.");
            ValidateConnected(graph);
            return graph;
        }

        private static SewerNode GetNode(
            SewerGraph graph,
            ObjectId id,
            Transaction transaction)
        {
            SewerNode node;
            if (graph.Nodes.TryGetValue(id, out node))
                return node;
            var structure = transaction.GetObject(
                id,
                OpenMode.ForRead,
                false) as CivilStructure;
            if (structure == null)
                throw new InvalidOperationException("A connected structure could not be opened.");
            if (structure.IsReferenceObject)
                throw new InvalidOperationException("The network contains a referenced structure.");
            double rim = structure.RimElevation;
            if (double.IsNaN(rim) || double.IsInfinity(rim))
                rim = structure.Position.Z;
            node = new SewerNode(id, rim);
            graph.Nodes.Add(id, node);
            return node;
        }

        private static void ValidateConnected(SewerGraph graph)
        {
            var visited = new HashSet<ObjectId>();
            var stack = new Stack<ObjectId>();
            stack.Push(graph.Nodes.Keys.First());
            while (stack.Count > 0)
            {
                ObjectId current = stack.Pop();
                if (!visited.Add(current))
                    continue;
                foreach (SewerEdge edge in graph.Nodes[current].Edges)
                    stack.Push(edge.Other(current));
            }
            if (visited.Count != graph.Nodes.Count)
                throw new InvalidOperationException("The network contains disconnected pipe groups.");
        }

        private static SewerPath FindPath(SewerGraph graph, ObjectId start, ObjectId end)
        {
            var parentNode = new Dictionary<ObjectId, ObjectId>();
            var parentEdge = new Dictionary<ObjectId, SewerEdge>();
            var queue = new Queue<ObjectId>();
            var visited = new HashSet<ObjectId> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                ObjectId current = queue.Dequeue();
                if (current == end)
                    break;
                foreach (SewerEdge edge in graph.Nodes[current].Edges)
                {
                    ObjectId next = edge.Other(current);
                    if (!visited.Add(next))
                        continue;
                    parentNode[next] = current;
                    parentEdge[next] = edge;
                    queue.Enqueue(next);
                }
            }
            if (!visited.Contains(end))
                throw new InvalidOperationException("No connected route exists between the selected main structures.");

            var nodes = new List<ObjectId> { end };
            var edges = new List<SewerEdge>();
            ObjectId cursor = end;
            while (cursor != start)
            {
                edges.Add(parentEdge[cursor]);
                cursor = parentNode[cursor];
                nodes.Add(cursor);
            }
            nodes.Reverse();
            edges.Reverse();
            return new SewerPath(nodes, edges);
        }

        private static void OrientHighToLow(SewerPath path, SewerGraph graph)
        {
            double first = graph.Nodes[path.Nodes.First()].Rim;
            double last = graph.Nodes[path.Nodes.Last()].Rim;
            if (last > first + Tolerance)
                path.Reverse();
        }

        private static List<SewerPath> ExtractBranches(SewerGraph graph, SewerPath main)
        {
            var used = new HashSet<ObjectId>(main.Edges.Select(edge => edge.Id));
            var assigned = new HashSet<ObjectId>(main.Nodes);
            var order = new Dictionary<ObjectId, int>();
            int nextOrder = 0;
            foreach (ObjectId node in main.Nodes)
                order[node] = nextOrder++;
            var branches = new List<SewerPath>();

            while (used.Count < graph.Edges.Count)
            {
                SewerCandidate best = null;
                foreach (ObjectId root in assigned.OrderBy(id => order[id]).ThenBy(id => id.Handle.Value))
                {
                    foreach (SewerEdge edge in graph.Nodes[root].Edges.Where(item => !used.Contains(item.Id)))
                    {
                        SewerPath path = LongestUnusedPath(
                            graph,
                            root,
                            edge,
                            used,
                            new HashSet<ObjectId>());
                        var candidate = new SewerCandidate(root, order[root], path);
                        if (best == null || candidate.RootOrder < best.RootOrder ||
                            (candidate.RootOrder == best.RootOrder && candidate.Path.Length > best.Path.Length + Tolerance))
                            best = candidate;
                    }
                }
                if (best == null)
                    throw new InvalidOperationException("The remaining branches could not be sequenced.");
                foreach (SewerEdge edge in best.Path.Edges)
                    used.Add(edge.Id);
                foreach (ObjectId node in best.Path.Nodes)
                    if (assigned.Add(node))
                        order[node] = nextOrder++;
                branches.Add(best.Path);
            }
            return branches;
        }

        private static SewerPath LongestUnusedPath(
            SewerGraph graph,
            ObjectId root,
            SewerEdge first,
            ISet<ObjectId> used,
            ISet<ObjectId> recursion)
        {
            var local = new HashSet<ObjectId>(recursion) { first.Id };
            ObjectId next = first.Other(root);
            List<SewerEdge> available = graph.Nodes[next].Edges
                .Where(edge => edge.Id != first.Id && !used.Contains(edge.Id) && !local.Contains(edge.Id))
                .ToList();
            if (available.Count == 0)
                return new SewerPath(new[] { root, next }, new[] { first });

            SewerPath tail = available
                .Select(edge => LongestUnusedPath(graph, next, edge, used, local))
                .OrderByDescending(path => path.Length)
                .ThenBy(path => path.Edges.First().Id.Handle.Value)
                .First();
            var nodes = new List<ObjectId> { root };
            nodes.AddRange(tail.Nodes);
            var edges = new List<SewerEdge> { first };
            edges.AddRange(tail.Edges);
            return new SewerPath(nodes, edges);
        }

        private static void ApplySelectedMainPlan(SewerSequencePlan plan, Transaction transaction)
        {
            var network = transaction.GetObject(
                plan.Graph.NetworkId,
                OpenMode.ForWrite,
                false) as CivilNetwork;
            if (network == null)
                throw new InvalidOperationException("The sewer network could not be reopened.");

            string token = Guid.NewGuid().ToString("N");
            int temporaryIndex = 0;
            foreach (SewerNode node in plan.Graph.Nodes.Values)
            {
                var structure = transaction.GetObject(node.Id, OpenMode.ForWrite, false) as CivilStructure;
                if (structure != null)
                    structure.Name = "CE_TMP_MH_" + token + "_" + temporaryIndex++;
            }
            temporaryIndex = 0;
            foreach (SewerEdge edge in plan.Graph.Edges)
            {
                var pipe = transaction.GetObject(edge.Id, OpenMode.ForWrite, false) as CivilPipe;
                if (pipe != null)
                    pipe.Name = "CE_TMP_P_" + token + "_" + temporaryIndex++;
            }

            ApplySewerPath(plan.Main, 1, transaction, true);
            for (int index = 0; index < plan.Branches.Count; index++)
            {
                // Branch extraction walks from the downstream junction outward.
                // Production numbering must read from the upstream first manhole
                // back toward that junction, so reverse each side branch first.
                plan.Branches[index].Reverse();
                ApplySewerPath(plan.Branches[index], index + 2, transaction, false);
            }

            network.Description = string.Format(
                CultureInfo.InvariantCulture,
                "CE sewer sequence: selected Branch-1 main, {0} side branch(es)",
                plan.Branches.Count);
        }

        private static void ApplySewerPath(
            SewerPath path,
            int branchNumber,
            Transaction transaction,
            bool renameFirstNode)
        {
            string branchName = "Branch-" + branchNumber.ToString(CultureInfo.InvariantCulture);
            for (int index = 0; index < path.Edges.Count; index++)
            {
                var pipe = transaction.GetObject(path.Edges[index].Id, OpenMode.ForWrite, false) as CivilPipe;
                if (pipe == null)
                    continue;
                pipe.Name = "P" + branchNumber.ToString(CultureInfo.InvariantCulture) + "." +
                    (index + 1).ToString(CultureInfo.InvariantCulture);
                pipe.Description = branchName;
            }

            int nodeSequence = 1;
            for (int index = 0; index < path.Nodes.Count; index++)
            {
                // A side branch's final node is its shared downstream junction;
                // retain the main/receiving-branch name on that shared structure.
                if (!renameFirstNode && index == path.Nodes.Count - 1)
                    continue;
                var structure = transaction.GetObject(path.Nodes[index], OpenMode.ForWrite, false) as CivilStructure;
                if (structure == null)
                    continue;
                structure.Name = "MH" + branchNumber.ToString(CultureInfo.InvariantCulture) + "." +
                    nodeSequence.ToString(CultureInfo.InvariantCulture);
                structure.Description = branchName;
                nodeSequence++;
            }
        }

        private static List<SewerAlignmentRecord> ReadGeneratedAlignments(
            CivilDocument civilDocument,
            Transaction transaction)
        {
            var result = new List<SewerAlignmentRecord>();
            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false);
                string branchKey;
                string objectType;
                if (!TryReadAlignmentTag(alignment, out branchKey, out objectType) || objectType != "Alignment")
                    continue;
                string[] parts = branchKey.Split(new[] { '|' }, 2);
                if (parts.Length != 2)
                    continue;
                result.Add(new SewerAlignmentRecord(alignmentId, branchKey, parts[0], parts[1]));
            }
            return result.OrderBy(item => BranchNumber(item.BranchName)).ToList();
        }

        private static bool TryReadAlignmentTag(DBObject databaseObject, out string branchKey, out string objectType)
        {
            branchKey = string.Empty;
            objectType = string.Empty;
            using (ResultBuffer data = databaseObject.GetXDataForApplication(AlignmentRegAppName))
            {
                if (data == null)
                    return false;
                string[] values = data.AsArray()
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                if (values.Length < 2)
                    return false;
                branchKey = values[0];
                objectType = values[1];
                return true;
            }
        }

        private static void CreateProfileObjects(
            Database database,
            CivilDocument civilDocument,
            SewerProductionSettings settings,
            ObjectId surfaceId,
            IReadOnlyList<SewerAlignmentRecord> records,
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
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction, ProfileRegAppName);
                ObjectId layerId = GetOrCreateLayer(database, transaction, settings.ProfileLayer, DefaultProfileLayer);
                string profileStyleName;
                ObjectId profileStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileStyles,
                    settings.ProfileStyle,
                    "profile style",
                    transaction,
                    out profileStyleName);
                string profileLabelName;
                ObjectId profileLabelId = ResolveStyleId(
                    civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles,
                    settings.ProfileLabelSetStyle,
                    "profile label-set style",
                    transaction,
                    out profileLabelName);
                string viewStyleName;
                ObjectId viewStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewStyles,
                    settings.ProfileViewStyle,
                    "profile-view style",
                    transaction,
                    out viewStyleName);
                string bandName;
                ObjectId bandId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewBandSetStyles,
                    settings.ProfileViewBandSetStyle,
                    "profile-view band-set style",
                    transaction,
                    out bandName);

                BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                    blocks[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite,
                    false);

                for (int index = 0; index < records.Count; index++)
                {
                    SewerAlignmentRecord record = records[index];
                    RemoveProfileObjects(database, civilDocument, record.BranchKey, transaction);
                    string unique = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + index;
                    ObjectId profileId = CreateSurfaceProfile(
                        record.BranchName + "-EG-" + unique,
                        record.AlignmentId,
                        surfaceId,
                        layerId,
                        profileStyleId,
                        profileLabelId);
                    DBObject profile = transaction.GetObject(profileId, OpenMode.ForWrite, false);
                    WriteProfileTag(profile, record.BranchKey, "Profile");
                    profilesCreated++;

                    int row = index / columns;
                    int column = index % columns;
                    Point3d location = new Point3d(
                        basePoint.X + column * horizontalSpacing,
                        basePoint.Y - row * verticalSpacing,
                        basePoint.Z);
                    ObjectId viewId = CreateProfileView(
                        record.BranchName + "-PROFILE-" + unique,
                        record.AlignmentId,
                        location,
                        bandId,
                        viewStyleId);
                    DBObject view = transaction.GetObject(viewId, OpenMode.ForWrite, false);
                    ProfileStyleLinker.Apply(view, viewStyleId, bandId);
                    WriteProfileTag(view, record.BranchKey, "ProfileView");
                    viewsCreated++;

                    ObjectId networkId = ObjectId.Null;
                    if (TryGetObjectId(database, record.NetworkHandle, out networkId))
                    {
                        var network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                        if (network != null)
                            partsAdded += AddBranchParts(network, record.BranchName, viewId, transaction);
                    }
                    ProfileViewBandDataBinder.Bind(
                        view,
                        profileId,
                        ObjectId.Null,
                        networkId);

                    var title = new MText();
                    title.SetDatabaseDefaults(database);
                    title.LayerId = layerId;
                    title.Location = location + new Vector3d(
                        0.0,
                        PaperAnnotationScale.ModelDistance(
                            database,
                            settings.LabelHeight * 4.0),
                        0.0);
                    title.Attachment = AttachmentPoint.BottomLeft;
                    title.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.LabelHeight);
                    PaperAnnotationScale.SetAnnotative(title);
                    title.Contents = record.BranchName.ToUpperInvariant() +
                        " SEWER PROFILE\\PProfile: " + profileStyleName +
                        " | View: " + viewStyleName + " | Bands: " + bandName;
                    WriteProfileTag(title, record.BranchKey, "Title");
                    modelSpace.AppendEntity(title);
                    transaction.AddNewlyCreatedDBObject(title, true);
                }
                transaction.Commit();
            }
        }

        private static int AddBranchParts(
            CivilNetwork network,
            string branchName,
            ObjectId viewId,
            Transaction transaction)
        {
            int count = 0;
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                if (pipe != null && string.Equals(pipe.Description, branchName, StringComparison.OrdinalIgnoreCase) &&
                    TryAddToProfileView(pipe, viewId))
                    count++;
            }
            foreach (ObjectId structureId in network.GetStructureIds())
            {
                var structure = transaction.GetObject(structureId, OpenMode.ForWrite, false) as CivilStructure;
                if (structure != null && string.Equals(structure.Description, branchName, StringComparison.OrdinalIgnoreCase) &&
                    TryAddToProfileView(structure, viewId))
                    count++;
            }
            return count;
        }

        private static bool TryAddToProfileView(DBObject part, ObjectId viewId)
        {
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
                method.Invoke(part, new object[] { viewId });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ObjectId CreateSurfaceProfile(
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId)
        {
            Type type = typeof(CivilAlignment).Assembly.GetType("Autodesk.Civil.DatabaseServices.Profile", true);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "CreateFromSurface")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!BuildProfileArguments(method.GetParameters(), name, alignmentId, surfaceId, layerId, styleId, labelSetId, out arguments))
                    continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException)
                {
                }
            }
            throw new InvalidOperationException("No compatible Profile.CreateFromSurface overload was found.");
        }

        private static bool BuildProfileArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            ObjectId[] fallback = { alignmentId, surfaceId, layerId, styleId, labelSetId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string))
                    arguments[index] = name;
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("surface")) arguments[index] = surfaceId;
                    else if (parameterName.Contains("layer")) arguments[index] = layerId;
                    else if (parameterName.Contains("label")) arguments[index] = labelSetId;
                    else if (parameterName.Contains("style")) arguments[index] = styleId;
                    else if (fallbackIndex < fallback.Length) arguments[index] = fallback[fallbackIndex++];
                    else return false;
                }
                else if (parameter.HasDefaultValue)
                    arguments[index] = parameter.DefaultValue;
                else
                    return false;
            }
            return true;
        }

        private static ObjectId CreateProfileView(
            string name,
            ObjectId alignmentId,
            Point3d point,
            ObjectId bandId,
            ObjectId styleId)
        {
            Type type = typeof(CivilAlignment).Assembly.GetType("Autodesk.Civil.DatabaseServices.ProfileView", true);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!BuildViewArguments(method.GetParameters(), name, alignmentId, point, bandId, styleId, out arguments))
                    continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException)
                {
                }
            }
            throw new InvalidOperationException("No compatible ProfileView.Create overload was found.");
        }

        private static bool BuildViewArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            Point3d point,
            ObjectId bandId,
            ObjectId styleId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            ObjectId[] fallback = { alignmentId, bandId, styleId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string)) arguments[index] = name;
                else if (parameter.ParameterType == typeof(Point3d)) arguments[index] = point;
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("band")) arguments[index] = bandId;
                    else if (parameterName.Contains("style")) arguments[index] = styleId;
                    else if (fallbackIndex < fallback.Length) arguments[index] = fallback[fallbackIndex++];
                    else return false;
                }
                else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                else return false;
            }
            return true;
        }

        private static void RemoveProfileObjects(
            Database database,
            CivilDocument civilDocument,
            string branchKey,
            Transaction transaction)
        {
            BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false);
            foreach (ObjectId blockId in blocks)
            {
                var block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null)
                    continue;
                foreach (ObjectId objectId in block.Cast<ObjectId>().ToList())
                {
                    DBObject databaseObject = transaction.GetObject(objectId, OpenMode.ForRead, false);
                    string branch;
                    string type;
                    if (TryReadProfileTag(databaseObject, out branch, out type) && branch == branchKey &&
                        (type == "ProfileView" || type == "Title"))
                    {
                        databaseObject.UpgradeOpen();
                        databaseObject.Erase();
                    }
                }
            }

            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false);
                foreach (ObjectId profileId in ReadObjectIds(alignment, "GetProfileIds").ToList())
                {
                    DBObject profile = transaction.GetObject(profileId, OpenMode.ForRead, false);
                    string branch;
                    string type;
                    if (TryReadProfileTag(profile, out branch, out type) && branch == branchKey && type == "Profile")
                    {
                        profile.UpgradeOpen();
                        profile.Erase();
                    }
                }
            }
        }

        private static IEnumerable<ObjectId> ReadObjectIds(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
                return Enumerable.Empty<ObjectId>();
            var values = method.Invoke(target, null) as IEnumerable;
            if (values == null)
                return Enumerable.Empty<ObjectId>();
            var result = new List<ObjectId>();
            foreach (object value in values)
                if (value is ObjectId)
                    result.Add((ObjectId)value);
            return result;
        }

        private static void WriteProfileTag(DBObject databaseObject, string branchKey, string type)
        {
            databaseObject.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ProfileRegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, branchKey),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type));
        }

        private static bool TryReadProfileTag(DBObject databaseObject, out string branch, out string type)
        {
            branch = string.Empty;
            type = string.Empty;
            using (ResultBuffer data = databaseObject.GetXDataForApplication(ProfileRegAppName))
            {
                if (data == null)
                    return false;
                string[] values = data.AsArray()
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                if (values.Length < 2)
                    return false;
                branch = values[0];
                type = values[1];
                return true;
            }
        }

        private static ObjectId ResolveStyleId(
            IEnumerable<ObjectId> styleIds,
            string requested,
            string description,
            Transaction transaction,
            out string actualName)
        {
            List<ObjectId> ids = styleIds.ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("The drawing contains no " + description + ".");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                foreach (ObjectId id in ids)
                {
                    DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = ReadName(style);
                    if (name.Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        actualName = name;
                        return id;
                    }
                }
                throw new InvalidOperationException("The requested " + description + " '" + requested + "' was not found.");
            }
            DBObject first = transaction.GetObject(ids[0], OpenMode.ForRead, false);
            actualName = ReadName(first);
            return ids[0];
        }

        private static string ReadName(object value)
        {
            if (value == null) return "(unnamed)";
            PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo getter = property == null ? null : property.GetGetMethod();
            object name = getter == null ? null : getter.Invoke(value, null);
            return Convert.ToString(name, CultureInfo.InvariantCulture) ?? "(unnamed)";
        }

        private static void SetStyleByReflection(object civilObject, ObjectId styleId)
        {
            PropertyInfo property = civilObject.GetType().GetProperty("StyleId", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo setter = property == null ? null : property.GetSetMethod();
            if (setter == null)
                throw new InvalidOperationException("This Civil 3D build does not expose a writable StyleId on generated alignments.");
            setter.Invoke(civilObject, new object[] { styleId });
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false);
            if (table.Has(name))
            {
                ObjectId id = table[name];
                var layer = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer != null && layer.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            ObjectId layerId = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return layerId;
        }

        private static void EnsureRegApp(Database database, Transaction transaction, string name)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false);
            if (table.Has(name))
                return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool TryGetObjectId(Database database, string handleText, out ObjectId id)
        {
            id = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                id = database.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull && id.IsValid && !id.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static int BranchNumber(string branchName)
        {
            string digits = new string((branchName ?? string.Empty).Where(char.IsDigit).ToArray());
            int value;
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                ? value
                : int.MaxValue;
        }

        private static bool PromptText(Editor editor, string label, string current, out string value)
        {
            PromptStringOptions options = new PromptStringOptions(
                "\n" + label + " <" + Display(current) + ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.OK ? result.StringResult.Trim() : current;
            return true;
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "first available" : value;
        }

        private static bool Confirm(Editor editor, string message)
        {
            return DisciplineWorkflowDialogs.Confirm("CE Tools — Sewer", message + "?");
        }

        private sealed class SewerAlignmentRecord
        {
            public SewerAlignmentRecord(ObjectId alignmentId, string branchKey, string networkHandle, string branchName)
            {
                AlignmentId = alignmentId;
                BranchKey = branchKey;
                NetworkHandle = networkHandle;
                BranchName = branchName;
            }
            public ObjectId AlignmentId { get; }
            public string BranchKey { get; }
            public string NetworkHandle { get; }
            public string BranchName { get; }
        }

        private sealed class SewerSequencePlan
        {
            public SewerSequencePlan(SewerGraph graph, SewerPath main, IReadOnlyList<SewerPath> branches)
            {
                Graph = graph;
                Main = main;
                Branches = branches;
            }
            public SewerGraph Graph { get; }
            public SewerPath Main { get; }
            public IReadOnlyList<SewerPath> Branches { get; }
        }

        private sealed class SewerCandidate
        {
            public SewerCandidate(ObjectId root, int rootOrder, SewerPath path)
            {
                Root = root;
                RootOrder = rootOrder;
                Path = path;
            }
            public ObjectId Root { get; }
            public int RootOrder { get; }
            public SewerPath Path { get; }
        }
    }

    internal sealed class SewerGraph
    {
        public SewerGraph(ObjectId networkId, string networkName)
        {
            NetworkId = networkId;
            NetworkName = networkName ?? string.Empty;
            Nodes = new Dictionary<ObjectId, SewerNode>();
            Edges = new List<SewerEdge>();
        }
        public ObjectId NetworkId { get; }
        public string NetworkName { get; }
        public IDictionary<ObjectId, SewerNode> Nodes { get; }
        public IList<SewerEdge> Edges { get; }
    }

    internal sealed class SewerNode
    {
        public SewerNode(ObjectId id, double rim)
        {
            Id = id;
            Rim = rim;
            Edges = new List<SewerEdge>();
        }
        public ObjectId Id { get; }
        public double Rim { get; }
        public IList<SewerEdge> Edges { get; }
    }

    internal sealed class SewerEdge
    {
        public SewerEdge(ObjectId id, ObjectId start, ObjectId end, double length)
        {
            Id = id;
            Start = start;
            End = end;
            Length = length;
        }
        public ObjectId Id { get; }
        public ObjectId Start { get; }
        public ObjectId End { get; }
        public double Length { get; }
        public ObjectId Other(ObjectId node)
        {
            if (node == Start) return End;
            if (node == End) return Start;
            throw new InvalidOperationException("A sewer edge was queried from a non-connected node.");
        }
    }

    internal sealed class SewerPath
    {
        public SewerPath(IEnumerable<ObjectId> nodes, IEnumerable<SewerEdge> edges)
        {
            Nodes = nodes.ToList();
            Edges = edges.ToList();
        }
        public IList<ObjectId> Nodes { get; }
        public IList<SewerEdge> Edges { get; }
        public double Length => Edges.Sum(edge => edge.Length);
        public void Reverse()
        {
            ((List<ObjectId>)Nodes).Reverse();
            ((List<SewerEdge>)Edges).Reverse();
        }
    }

    internal sealed class SewerProductionSettings
    {
        public string AlignmentStyle { get; set; } = string.Empty;
        public string AlignmentLabelSetStyle { get; set; } = string.Empty;
        public string ProfileStyle { get; set; } = string.Empty;
        public string ProfileLabelSetStyle { get; set; } = string.Empty;
        public string ProfileViewStyle { get; set; } = string.Empty;
        public string ProfileViewBandSetStyle { get; set; } = string.Empty;
        public string PipePlanLabelStyle { get; set; } = string.Empty;
        public string StructurePlanLabelStyle { get; set; } = string.Empty;
        public string ProfileLayer { get; set; } = "CE-SEWER-PROFILE";
        public double LabelHeight { get; set; } = 5.0;
        public string BranchLabelSide { get; set; } = "Alternating";
        public int ProfileColumns { get; set; } = 2;
        public double ProfileHorizontalSpacing { get; set; } = 250.0;
        public double ProfileVerticalSpacing { get; set; } = 120.0;

        public static SewerProductionSettings Read(Database database)
        {
            var settings = new SewerProductionSettings();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false);
                if (!named.Contains("CE_TOOLS"))
                    return settings;
                var ce = transaction.GetObject(named.GetAt("CE_TOOLS"), OpenMode.ForRead, false) as DBDictionary;
                if (ce == null || !ce.Contains("SEWER_PRODUCTION_SETTINGS"))
                    return settings;
                var record = transaction.GetObject(ce.GetAt("SEWER_PRODUCTION_SETTINGS"), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null)
                    return settings;
                foreach (TypedValue typedValue in record.Data)
                {
                    if (typedValue.TypeCode != (int)DxfCode.Text)
                        continue;
                    string text = typedValue.Value as string;
                    int separator = string.IsNullOrEmpty(text) ? -1 : text.IndexOf('=');
                    if (separator <= 0)
                        continue;
                    Apply(settings, text.Substring(0, separator), text.Substring(separator + 1));
                }
            }
            return settings;
        }

        public void Write(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite, false);
                DBDictionary ce;
                if (named.Contains("CE_TOOLS"))
                    ce = transaction.GetObject(named.GetAt("CE_TOOLS"), OpenMode.ForWrite, false) as DBDictionary;
                else
                {
                    ce = new DBDictionary();
                    named.SetAt("CE_TOOLS", ce);
                    transaction.AddNewlyCreatedDBObject(ce, true);
                }
                Xrecord record;
                if (ce.Contains("SEWER_PRODUCTION_SETTINGS"))
                    record = transaction.GetObject(ce.GetAt("SEWER_PRODUCTION_SETTINGS"), OpenMode.ForWrite, false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    ce.SetAt("SEWER_PRODUCTION_SETTINGS", record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                record.Data = new ResultBuffer(
                    Value("AlignmentStyle", AlignmentStyle),
                    Value("AlignmentLabelSetStyle", AlignmentLabelSetStyle),
                    Value("ProfileStyle", ProfileStyle),
                    Value("ProfileLabelSetStyle", ProfileLabelSetStyle),
                    Value("ProfileViewStyle", ProfileViewStyle),
                    Value("ProfileViewBandSetStyle", ProfileViewBandSetStyle),
                    Value("PipePlanLabelStyle", PipePlanLabelStyle),
                    Value("StructurePlanLabelStyle", StructurePlanLabelStyle),
                    Value("ProfileLayer", ProfileLayer),
                    Value("LabelHeight", LabelHeight.ToString("R", CultureInfo.InvariantCulture)),
                    Value("BranchLabelSide", BranchLabelSide),
                    Value("ProfileColumns", ProfileColumns.ToString(CultureInfo.InvariantCulture)),
                    Value("ProfileHorizontalSpacing", ProfileHorizontalSpacing.ToString("R", CultureInfo.InvariantCulture)),
                    Value("ProfileVerticalSpacing", ProfileVerticalSpacing.ToString("R", CultureInfo.InvariantCulture)));
                transaction.Commit();
            }
        }

        private static TypedValue Value(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + (value ?? string.Empty));
        }

        private static void Apply(SewerProductionSettings settings, string key, string value)
        {
            if (key == "AlignmentStyle") settings.AlignmentStyle = value;
            else if (key == "AlignmentLabelSetStyle") settings.AlignmentLabelSetStyle = value;
            else if (key == "ProfileStyle") settings.ProfileStyle = value;
            else if (key == "ProfileLabelSetStyle") settings.ProfileLabelSetStyle = value;
            else if (key == "ProfileViewStyle") settings.ProfileViewStyle = value;
            else if (key == "ProfileViewBandSetStyle") settings.ProfileViewBandSetStyle = value;
            else if (key == "PipePlanLabelStyle") settings.PipePlanLabelStyle = value;
            else if (key == "StructurePlanLabelStyle") settings.StructurePlanLabelStyle = value;
            else if (key == "ProfileLayer") settings.ProfileLayer = value;
            else if (key == "BranchLabelSide") settings.BranchLabelSide = value;
            else if (key == "LabelHeight")
            {
                double height;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out height) && height > 0.0)
                {
                    // Repair values previously persisted in metre drawing units
                    // (for example 0.005) back to their paper-mm equivalent.
                    settings.LabelHeight = height <= 0.05
                        ? height * 1000.0
                        : Math.Abs(height - 2.5) < 0.001
                            ? 5.0
                            : height;
                }
            }
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
