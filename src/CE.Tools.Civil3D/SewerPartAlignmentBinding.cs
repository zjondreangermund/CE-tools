using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilPart = Autodesk.Civil.DatabaseServices.Part;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerPartAlignmentBinding))]

namespace CETools.Civil3D
{
    public sealed class SewerPartAlignmentBinding
    {
        private const string SewerAlignmentRegApp = "CE_TOOLS_SEWALIGN";
        private static readonly Regex PipeBranchPattern = new Regex(
            @"^P(?<branch>\d+)\.\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex StructureBranchPattern = new Regex(
            @"^MH(?<branch>\d+)\.\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DescriptionBranchPattern = new Regex(
            @"^Branch\s*-\s*(?<branch>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static void BindBranch(Transaction transaction, IEnumerable<ObjectId> pipeIds,
            IEnumerable<ObjectId> structureIds, ObjectId alignmentId)
        {
            foreach (ObjectId id in pipeIds.Distinct()) SetReference(transaction, id, alignmentId, false);
            // A shared junction structure can belong to several branches. Keep
            // its existing reference instead of overwriting it on every branch.
            foreach (ObjectId id in structureIds.Distinct()) SetReference(transaction, id, alignmentId, true);
        }

        private static bool SetReference(Transaction transaction, ObjectId id, ObjectId alignmentId, bool onlyIfBlank)
        {
            var part = transaction.GetObject(id, OpenMode.ForRead, false) as CivilPart;
            if (part == null || part.IsReferenceObject) return false;
            if (onlyIfBlank && !part.RefAlignmentId.IsNull && !part.RefAlignmentId.IsErased) return false;
            if (!part.IsWriteEnabled) part.UpgradeOpen();
            part.RefAlignmentId = alignmentId;
            if (part.RefAlignmentId != alignmentId) throw new InvalidOperationException("The part reference alignment did not persist.");
            return true;
        }

        [CommandMethod("CE_TOOLS", "CE_PIPEALIGNMENTMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AssignReferenceAlignment()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Pipe Reference Alignment",
                "Automatically assign each CE-sequenced sewer branch to its own generated branch alignment, or manually assign one alignment to selected parts.");
            const string automatic = "Entire selected sewer network - automatic by branch";
            const string manual = "Selected parts - choose one alignment";
            settings.AddChoice(
                "Mode", "Reference", "Assignment mode", automatic,
                "Automatic mode expands the selected pipe/structure to its complete gravity network. P1.x / MH1.x use Branch-1, P2.x / MH2.x use Branch-2, and so on.",
                new[] { automatic, manual });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = settings.Text("Mode") == automatic
                        ? "\nSelect one or more sewer pipes/structures. CE Tools will expand them to their entire network(s): "
                        : "\nSelect pipes and structures for one reference alignment: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            if (settings.Text("Mode") == automatic)
            {
                AssignEntireNetworksByBranch(
                    document,
                    civil,
                    selection.Value.GetObjectIds());
                return;
            }

            var choices = ReadAlignmentChoices(document.Database, civil);
            if (choices.Count == 0)
            {
                document.Editor.WriteMessage("\nCreate the branch alignment first.");
                return;
            }

            var manualSettings = new ProductionSettingsDialogModel(
                "CE Tools - Pipe Reference Alignment",
                "Assign one selected alignment to the selected pipes and structures. Pipe geometry and surface references are retained.");
            manualSettings.AddChoice(
                "Alignment", "Reference", "Reference alignment", choices[0].Name,
                "Choose the branch alignment these selected parts belong to.",
                choices.Select(item => item.Name));
            if (!DisciplineWorkflowDialogs.EditSettings(manualSettings)) return;

            CivilChoice choice = choices.FirstOrDefault(item => item.Name == manualSettings.Text("Alignment"));
            if (choice == null) return;
            int changed = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    if (SetReference(tr, id, choice.Id, false)) changed++;
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nReference alignment assigned and verified on {0} selected parts.",
                changed);
        }

        private static void AssignEntireNetworksByBranch(
            Document document,
            CivilDocument civil,
            IEnumerable<ObjectId> selectedIds)
        {
            var networkIds = new HashSet<ObjectId>();
            using (Transaction read = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds.Distinct())
                {
                    DBObject value = null;
                    try { value = read.GetObject(id, OpenMode.ForRead, false); }
                    catch { }
                    CivilPipe pipe = value as CivilPipe;
                    if (pipe != null && !pipe.NetworkId.IsNull)
                    {
                        networkIds.Add(pipe.NetworkId);
                        continue;
                    }
                    CivilStructure structure = value as CivilStructure;
                    if (structure != null && !structure.NetworkId.IsNull)
                        networkIds.Add(structure.NetworkId);
                }
            }

            if (networkIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PIPEALIGNMENTMULTI: select at least one gravity sewer pipe or structure.");
                return;
            }

            int networks = 0;
            int pipesAssigned = 0;
            int structuresAssigned = 0;
            int unchanged = 0;
            int unsequenced = 0;
            int missingAlignments = 0;

            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                List<CivilAlignment> alignments = civil.GetAlignmentIds()
                    .Cast<ObjectId>()
                    .Select(id =>
                    {
                        try { return tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                        catch { return null; }
                    })
                    .Where(item => item != null)
                    .ToList();

                foreach (ObjectId networkId in networkIds.OrderBy(id => id.Handle.Value))
                {
                    CivilNetwork network = tr.GetObject(
                        networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;

                    Dictionary<int, ObjectId> branchAlignments =
                        ResolveBranchAlignments(
                            network,
                            alignments,
                            tr);

                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = tr.GetObject(
                            pipeId, OpenMode.ForRead, false) as CivilPipe;
                        int branch;
                        if (pipe == null || !TryReadBranch(pipe.Name, pipe.Description, true, out branch))
                        {
                            unsequenced++;
                            continue;
                        }
                        ObjectId alignmentId;
                        if (!branchAlignments.TryGetValue(branch, out alignmentId))
                        {
                            missingAlignments++;
                            continue;
                        }
                        if (pipe.RefAlignmentId == alignmentId)
                        {
                            unchanged++;
                            continue;
                        }
                        if (SetReference(tr, pipeId, alignmentId, false))
                            pipesAssigned++;
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = tr.GetObject(
                            structureId, OpenMode.ForRead, false) as CivilStructure;
                        int branch;
                        if (structure == null ||
                            !TryReadBranch(structure.Name, structure.Description, false, out branch))
                        {
                            unsequenced++;
                            continue;
                        }
                        ObjectId alignmentId;
                        if (!branchAlignments.TryGetValue(branch, out alignmentId))
                        {
                            missingAlignments++;
                            continue;
                        }
                        if (structure.RefAlignmentId == alignmentId)
                        {
                            unchanged++;
                            continue;
                        }
                        if (SetReference(tr, structureId, alignmentId, false))
                            structuresAssigned++;
                    }
                    networks++;
                }
                tr.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nAutomatic sewer reference-alignment assignment complete. Networks={0}; pipes assigned={1}; structures assigned={2}; already correct={3}; unsequenced parts={4}; parts with missing branch alignment={5}. Branch references follow P#/MH# sequence numbers for the entire network.",
                networks,
                pipesAssigned,
                structuresAssigned,
                unchanged,
                unsequenced,
                missingAlignments);
        }

        private static Dictionary<int, ObjectId> ResolveBranchAlignments(
            CivilNetwork network,
            IEnumerable<CivilAlignment> alignments,
            Transaction transaction)
        {
            var result = new Dictionary<int, ObjectId>();
            string networkHandle = network.ObjectId.Handle.ToString();

            foreach (CivilAlignment alignment in alignments)
            {
                string taggedNetwork;
                int taggedBranch;
                if (TryReadGeneratedAlignmentTag(
                        alignment,
                        out taggedNetwork,
                        out taggedBranch) &&
                    string.Equals(
                        taggedNetwork,
                        networkHandle,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result[taggedBranch] = alignment.ObjectId;
                }
            }

            // Older CE drawings may contain branch alignments created before the
            // XData tag was available. Use exact branch names only for branches
            // that do not already have a tagged alignment.
            foreach (CivilAlignment alignment in alignments)
            {
                int branch;
                if (!TryReadBranchAlignmentName(
                        alignment.Name,
                        network.Name,
                        out branch) ||
                    result.ContainsKey(branch))
                    continue;
                result[branch] = alignment.ObjectId;
            }
            return result;
        }

        private static bool TryReadGeneratedAlignmentTag(
            CivilAlignment alignment,
            out string networkHandle,
            out int branch)
        {
            networkHandle = string.Empty;
            branch = 0;
            if (alignment == null) return false;
            try
            {
                using (ResultBuffer data =
                    alignment.GetXDataForApplication(SewerAlignmentRegApp))
                {
                    if (data == null) return false;
                    string[] values = data.AsArray()
                        .Where(item => item.TypeCode ==
                            (int)DxfCode.ExtendedDataAsciiString)
                        .Select(item => Convert.ToString(
                            item.Value,
                            CultureInfo.InvariantCulture))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                    if (values.Length < 2 ||
                        !string.Equals(
                            values[1],
                            "Alignment",
                            StringComparison.OrdinalIgnoreCase))
                        return false;

                    string[] key = values[0].Split('|');
                    if (key.Length != 2) return false;
                    Match branchMatch = DescriptionBranchPattern.Match(key[1]);
                    if (!branchMatch.Success ||
                        !int.TryParse(
                            branchMatch.Groups["branch"].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out branch))
                        return false;
                    networkHandle = key[0];
                    return !string.IsNullOrWhiteSpace(networkHandle);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBranchAlignmentName(
            string alignmentName,
            string networkName,
            out int branch)
        {
            branch = 0;
            string name = (alignmentName ?? string.Empty).Trim();
            Match direct = DescriptionBranchPattern.Match(name);
            if (direct.Success)
                return int.TryParse(
                    direct.Groups["branch"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out branch);

            string prefix = (networkName ?? string.Empty).Trim() + " - ";
            if (!string.IsNullOrWhiteSpace(networkName) &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Match nested = DescriptionBranchPattern.Match(
                    name.Substring(prefix.Length));
                if (nested.Success)
                    return int.TryParse(
                        nested.Groups["branch"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out branch);
            }
            return false;
        }

        private static bool TryReadBranch(
            string name,
            string description,
            bool pipe,
            out int branch)
        {
            branch = 0;
            Match match = (pipe ? PipeBranchPattern : StructureBranchPattern)
                .Match(name ?? string.Empty);
            if (match.Success &&
                int.TryParse(
                    match.Groups["branch"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out branch))
                return true;

            match = DescriptionBranchPattern.Match(description ?? string.Empty);
            return match.Success &&
                int.TryParse(
                    match.Groups["branch"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out branch);
        }

        private static List<CivilChoice> ReadAlignmentChoices(
            Database database,
            CivilDocument civil)
        {
            var choices = new List<CivilChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = tr.GetObject(
                        id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment != null)
                        choices.Add(new CivilChoice(
                            id,
                            alignment.Name + " [" + id.Handle + "]"));
                }
            }
            return choices;
        }
    }
}
