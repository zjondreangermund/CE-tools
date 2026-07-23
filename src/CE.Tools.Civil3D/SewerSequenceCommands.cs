using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerSequenceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Renames either complete gravity pipe networks or one selected start-to-end path.
    /// Complete-network mode decomposes tree-shaped networks into branches, beginning
    /// with the longest route from the highest-rim structure and then processing the
    /// remaining branches from longest to shortest.
    /// </summary>
    public sealed class SewerSequenceCommands
    {
        private const double ElevationTolerance = 1e-9;

        private static readonly Regex BranchPattern = new Regex(
            @"^Branch\s*-\s*(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWSEQ",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nSewer sequencing mode [EntireNetwork/SelectedPath] <EntireNetwork>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("EntireNetwork");
            options.Keywords.Add("SelectedPath");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "EntireNetwork"
                : result.StringResult;

            if (string.Equals(mode, "SelectedPath", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteSelectedPath(document);
            }
            else
            {
                ExecuteEntireNetworks(document);
            }
        }

        private static void ExecuteEntireNetworks(Document document)
        {
            Editor editor = document.Editor;
            Database database = document.Database;

            PromptSelectionResult selectionResult = editor.SelectImplied();
            if (selectionResult.Status != PromptStatus.OK ||
                selectionResult.Value == null ||
                selectionResult.Value.Count == 0)
            {
                var selectionOptions = new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect one or more pipes/structures. CE Tools will expand each selection to its entire network: "
                };
                selectionResult = editor.GetSelection(selectionOptions);
            }

            if (selectionResult.Status != PromptStatus.OK ||
                selectionResult.Value == null ||
                selectionResult.Value.Count == 0)
            {
                return;
            }

            var networkIds = new HashSet<ObjectId>();
            int unsupportedSelections = 0;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId selectedId in selectionResult.Value.GetObjectIds())
                    {
                        DBObject selectedObject = transaction.GetObject(
                            selectedId,
                            OpenMode.ForRead,
                            false);

                        var structure = selectedObject as CivilStructure;
                        if (structure != null && !structure.NetworkId.IsNull)
                        {
                            networkIds.Add(structure.NetworkId);
                            continue;
                        }

                        var pipe = selectedObject as CivilPipe;
                        if (pipe != null && !pipe.NetworkId.IsNull)
                        {
                            networkIds.Add(pipe.NetworkId);
                            continue;
                        }

                        unsupportedSelections++;
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWSEQ cancelled while reading the selection: " + exception.Message);
                return;
            }

            if (networkIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SEWSEQ: select at least one Civil 3D gravity-network pipe or structure.");
                return;
            }

            List<NetworkPlan> plans;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    plans = networkIds
                        .OrderBy(id => id.Handle.Value)
                        .Select(id => BuildNetworkPlan(id, transaction))
                        .ToList();
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWSEQ cancelled. " + exception.Message);
                return;
            }

            WriteNetworkPreview(editor, plans, unsupportedSelections);

            if (!Confirm(
                    editor,
                    "Rename every structure and connected pipe in the selected network(s)"))
            {
                editor.WriteMessage("\nCE_SEWSEQ cancelled. No names were changed.");
                return;
            }

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (NetworkPlan plan in plans)
                    {
                        var network = transaction.GetObject(
                            plan.NetworkId,
                            OpenMode.ForWrite,
                            false) as CivilNetwork;
                        if (network == null)
                        {
                            throw new InvalidOperationException(
                                "A selected pipe network could not be reopened.");
                        }

                        if (network.IsReferenceObject)
                        {
                            throw new InvalidOperationException(
                                "Referenced pipe network '" + plan.NetworkName + "' cannot be renamed.");
                        }

                        ApplyTemporaryNames(plan, transaction);
                        ApplyBranchNames(plan, transaction);

                        network.Description = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE sewer sequence: {0} branch(es), highest-to-lowest",
                            plan.Branches.Count);
                    }

                    transaction.Commit();
                }

                int totalNetworks = plans.Count;
                int totalBranches = plans.Sum(plan => plan.Branches.Count);
                int totalStructures = plans.Sum(plan => plan.StructureIds.Count);
                int totalPipes = plans.Sum(plan => plan.PipeIds.Count);

                editor.WriteMessage(
                    "\nCE_SEWSEQ complete. Networks: {0}; branches: {1}; structures renamed: {2}; pipes renamed: {3}.",
                    totalNetworks,
                    totalBranches,
                    totalStructures,
                    totalPipes);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SEWSEQ cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        private static NetworkPlan BuildNetworkPlan(
            ObjectId networkId,
            Transaction transaction)
        {
            var network = transaction.GetObject(
                networkId,
                OpenMode.ForRead,
                false) as CivilNetwork;
            if (network == null)
            {
                throw new InvalidOperationException("A selected object does not resolve to a gravity pipe network.");
            }

            if (network.IsReferenceObject)
            {
                throw new InvalidOperationException(
                    "Referenced pipe network '" + network.Name + "' cannot be renamed.");
            }

            var nodes = new Dictionary<ObjectId, GraphNode>();
            var structureIds = new List<ObjectId>();

            foreach (ObjectId structureId in network.GetStructureIds())
            {
                var structure = transaction.GetObject(
                    structureId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                if (structure == null)
                {
                    continue;
                }

                if (structure.IsReferenceObject)
                {
                    throw new InvalidOperationException(
                        "Network '" + network.Name + "' contains referenced structures and cannot be renamed.");
                }

                double rimElevation = GetRimElevation(structure);
                nodes[structureId] = new GraphNode(structureId, rimElevation);
                structureIds.Add(structureId);
            }

            var edges = new Dictionary<ObjectId, GraphEdge>();
            var pipeIds = new List<ObjectId>();

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null)
                {
                    continue;
                }

                if (pipe.IsReferenceObject)
                {
                    throw new InvalidOperationException(
                        "Network '" + network.Name + "' contains referenced pipes and cannot be renamed.");
                }

                if (pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull)
                {
                    throw new InvalidOperationException(
                        "Network '" + network.Name + "' contains pipe '" + pipe.Name +
                        "' with an unconnected end. Connect both ends before whole-network sequencing.");
                }

                if (pipe.StartStructureId == pipe.EndStructureId)
                {
                    throw new InvalidOperationException(
                        "Network '" + network.Name + "' contains a pipe connected back to the same structure.");
                }

                if (!nodes.ContainsKey(pipe.StartStructureId) ||
                    !nodes.ContainsKey(pipe.EndStructureId))
                {
                    throw new InvalidOperationException(
                        "Network '" + network.Name + "' contains a pipe whose endpoint structure is unavailable.");
                }

                var edge = new GraphEdge(
                    pipeId,
                    pipe.StartStructureId,
                    pipe.EndStructureId,
                    GetPipeLength(pipe));
                edges[pipeId] = edge;
                nodes[edge.StartStructureId].Edges.Add(edge);
                nodes[edge.EndStructureId].Edges.Add(edge);
                pipeIds.Add(pipeId);
            }

            if (structureIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "Network '" + network.Name + "' contains no structures.");
            }

            List<BranchPlan> branches = BuildBranches(nodes, edges, network.Name);

            ValidateCoverage(structureIds, pipeIds, branches, network.Name);

            return new NetworkPlan(
                networkId,
                network.Name,
                structureIds,
                pipeIds,
                branches);
        }

        private static List<BranchPlan> BuildBranches(
            IDictionary<ObjectId, GraphNode> nodes,
            IDictionary<ObjectId, GraphEdge> edges,
            string networkName)
        {
            var remainingNodes = new HashSet<ObjectId>(nodes.Keys);
            var allBranches = new List<BranchPlan>();

            while (remainingNodes.Count > 0)
            {
                ObjectId seed = remainingNodes
                    .OrderByDescending(id => nodes[id].RimElevation)
                    .ThenBy(id => id.Handle.Value)
                    .First();

                var componentNodes = new HashSet<ObjectId>();
                var componentEdges = new HashSet<ObjectId>();
                var stack = new Stack<ObjectId>();
                stack.Push(seed);
                componentNodes.Add(seed);

                while (stack.Count > 0)
                {
                    ObjectId currentId = stack.Pop();
                    foreach (GraphEdge edge in nodes[currentId].Edges)
                    {
                        componentEdges.Add(edge.PipeId);
                        ObjectId otherId = edge.Other(currentId);
                        if (componentNodes.Add(otherId))
                        {
                            stack.Push(otherId);
                        }
                    }
                }

                remainingNodes.ExceptWith(componentNodes);

                if (componentEdges.Count != componentNodes.Count - 1)
                {
                    throw new InvalidOperationException(
                        "Network '" + networkName +
                        "' contains a loop or parallel pipe path. Whole-network branch sequencing currently requires a tree-shaped gravity network.");
                }

                List<BranchPlan> componentBranches = BuildTreeComponentBranches(
                    componentNodes,
                    componentEdges,
                    nodes,
                    edges);
                allBranches.AddRange(componentBranches);
            }

            BranchPlan primary = allBranches
                .Where(branch => branch.IsComponentMain)
                .OrderByDescending(branch => branch.ComponentRootRim)
                .ThenByDescending(branch => branch.Length)
                .ThenBy(branch => branch.SortHandle)
                .First();

            var ordered = new List<BranchPlan> { primary };
            ordered.AddRange(
                allBranches
                    .Where(branch => !ReferenceEquals(branch, primary))
                    .OrderByDescending(branch => branch.Length)
                    .ThenByDescending(branch => branch.HighRim)
                    .ThenBy(branch => branch.LowRim)
                    .ThenBy(branch => branch.SortHandle));

            for (int index = 0; index < ordered.Count; index++)
            {
                ordered[index].BranchNumber = index + 1;
            }

            return ordered;
        }

        private static List<BranchPlan> BuildTreeComponentBranches(
            ISet<ObjectId> componentNodes,
            ISet<ObjectId> componentEdges,
            IDictionary<ObjectId, GraphNode> nodes,
            IDictionary<ObjectId, GraphEdge> edges)
        {
            ObjectId rootId = componentNodes
                .OrderByDescending(id => nodes[id].RimElevation)
                .ThenBy(id => id.Handle.Value)
                .First();

            if (componentEdges.Count == 0)
            {
                return new List<BranchPlan>
                {
                    new BranchPlan(
                        new List<ObjectId> { rootId },
                        new List<ObjectId>(),
                        0.0,
                        nodes[rootId].RimElevation,
                        nodes[rootId].RimElevation,
                        nodes[rootId].RimElevation,
                        true,
                        rootId.Handle.Value)
                };
            }

            var parentNode = new Dictionary<ObjectId, ObjectId>();
            var parentEdge = new Dictionary<ObjectId, ObjectId>();
            var childCount = componentNodes.ToDictionary(id => id, id => 0);
            var visited = new HashSet<ObjectId> { rootId };
            var stack = new Stack<ObjectId>();
            stack.Push(rootId);

            while (stack.Count > 0)
            {
                ObjectId currentId = stack.Pop();
                foreach (GraphEdge edge in nodes[currentId].Edges
                    .Where(item => componentEdges.Contains(item.PipeId))
                    .OrderByDescending(item => nodes[item.Other(currentId)].RimElevation)
                    .ThenBy(item => item.PipeId.Handle.Value))
                {
                    ObjectId otherId = edge.Other(currentId);
                    if (!visited.Add(otherId))
                    {
                        continue;
                    }

                    parentNode[otherId] = currentId;
                    parentEdge[otherId] = edge.PipeId;
                    childCount[currentId] = childCount[currentId] + 1;
                    stack.Push(otherId);
                }
            }

            var rootPaths = componentNodes
                .Where(id => childCount[id] == 0)
                .Select(leafId => BuildRootPath(rootId, leafId, parentNode, parentEdge))
                .ToList();

            var unassignedEdges = new HashSet<ObjectId>(componentEdges);
            var assignedStructures = new HashSet<ObjectId>();
            var result = new List<BranchPlan>();

            while (unassignedEdges.Count > 0)
            {
                var candidates = new List<CandidatePath>();

                foreach (RootPath rootPath in rootPaths)
                {
                    int firstUnassignedIndex = -1;
                    for (int index = 0; index < rootPath.EdgeIds.Count; index++)
                    {
                        if (unassignedEdges.Contains(rootPath.EdgeIds[index]))
                        {
                            firstUnassignedIndex = index;
                            break;
                        }
                    }

                    if (firstUnassignedIndex < 0)
                    {
                        continue;
                    }

                    bool suffixIsAvailable = true;
                    for (int index = firstUnassignedIndex; index < rootPath.EdgeIds.Count; index++)
                    {
                        if (!unassignedEdges.Contains(rootPath.EdgeIds[index]))
                        {
                            suffixIsAvailable = false;
                            break;
                        }
                    }

                    if (!suffixIsAvailable)
                    {
                        continue;
                    }

                    var candidateNodes = rootPath.NodeIds
                        .Skip(firstUnassignedIndex)
                        .ToList();
                    var candidateEdges = rootPath.EdgeIds
                        .Skip(firstUnassignedIndex)
                        .ToList();

                    double length = candidateEdges.Sum(pipeId => edges[pipeId].Length);
                    OrientHighestEndpointFirst(candidateNodes, candidateEdges, nodes);

                    double highRim = candidateNodes.Max(id => nodes[id].RimElevation);
                    double lowRim = candidateNodes.Min(id => nodes[id].RimElevation);
                    long sortHandle = candidateEdges.Count > 0
                        ? candidateEdges[0].Handle.Value
                        : candidateNodes[0].Handle.Value;

                    candidates.Add(new CandidatePath(
                        candidateNodes,
                        candidateEdges,
                        length,
                        highRim,
                        lowRim,
                        sortHandle));
                }

                CandidatePath selected = candidates
                    .OrderByDescending(candidate => candidate.Length)
                    .ThenByDescending(candidate => candidate.HighRim)
                    .ThenBy(candidate => candidate.LowRim)
                    .ThenBy(candidate => candidate.SortHandle)
                    .FirstOrDefault();

                if (selected == null)
                {
                    throw new InvalidOperationException(
                        "The network topology could not be decomposed into continuous branches.");
                }

                var structuresToRename = new List<ObjectId>();
                foreach (ObjectId structureId in selected.NodeIds)
                {
                    if (assignedStructures.Add(structureId))
                    {
                        structuresToRename.Add(structureId);
                    }
                }

                foreach (ObjectId pipeId in selected.EdgeIds)
                {
                    unassignedEdges.Remove(pipeId);
                }

                result.Add(new BranchPlan(
                    structuresToRename,
                    selected.EdgeIds,
                    selected.Length,
                    selected.HighRim,
                    selected.LowRim,
                    nodes[rootId].RimElevation,
                    result.Count == 0,
                    selected.SortHandle));
            }

            return result;
        }

        private static RootPath BuildRootPath(
            ObjectId rootId,
            ObjectId leafId,
            IDictionary<ObjectId, ObjectId> parentNode,
            IDictionary<ObjectId, ObjectId> parentEdge)
        {
            var reverseNodes = new List<ObjectId> { leafId };
            var reverseEdges = new List<ObjectId>();
            ObjectId cursor = leafId;

            while (cursor != rootId)
            {
                ObjectId pipeId;
                ObjectId previousId;
                if (!parentEdge.TryGetValue(cursor, out pipeId) ||
                    !parentNode.TryGetValue(cursor, out previousId))
                {
                    throw new InvalidOperationException(
                        "The network tree contains an incomplete parent path.");
                }

                reverseEdges.Add(pipeId);
                cursor = previousId;
                reverseNodes.Add(cursor);
            }

            reverseNodes.Reverse();
            reverseEdges.Reverse();
            return new RootPath(reverseNodes, reverseEdges);
        }

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

        private static void ReverseInPlace<T>(IList<T> values)
        {
            int left = 0;
            int right = values.Count - 1;
            while (left < right)
            {
                T temporary = values[left];
                values[left] = values[right];
                values[right] = temporary;
                left++;
                right--;
            }
        }

        private static void ValidateCoverage(
            IReadOnlyCollection<ObjectId> structureIds,
            IReadOnlyCollection<ObjectId> pipeIds,
            IReadOnlyCollection<BranchPlan> branches,
            string networkName)
        {
            var renamedStructures = new HashSet<ObjectId>();
            var renamedPipes = new HashSet<ObjectId>();

            foreach (BranchPlan branch in branches)
            {
                foreach (ObjectId structureId in branch.StructureIds)
                {
                    if (!renamedStructures.Add(structureId))
                    {
                        throw new InvalidOperationException(
                            "Network '" + networkName + "' produced duplicate structure ownership.");
                    }
                }

                foreach (ObjectId pipeId in branch.PipeIds)
                {
                    if (!renamedPipes.Add(pipeId))
                    {
                        throw new InvalidOperationException(
                            "Network '" + networkName + "' produced duplicate pipe ownership.");
                    }
                }
            }

            if (renamedStructures.Count != structureIds.Count ||
                renamedPipes.Count != pipeIds.Count)
            {
                throw new InvalidOperationException(
                    "Network '" + networkName +
                    "' could not assign every structure and pipe to exactly one branch.");
            }
        }

        private static double GetRimElevation(CivilStructure structure)
        {
            double elevation = structure.RimElevation;
            if (double.IsNaN(elevation) || double.IsInfinity(elevation))
            {
                return structure.Position.Z;
            }

            return elevation;
        }

        private static double GetPipeLength(CivilPipe pipe)
        {
            double length = pipe.Length3DCenterToCenter;
            if (double.IsNaN(length) || double.IsInfinity(length) || length < 0.0)
            {
                return 0.0;
            }

            return length;
        }

        private static void WriteNetworkPreview(
            Editor editor,
            IReadOnlyList<NetworkPlan> plans,
            int unsupportedSelections)
        {
            editor.WriteMessage("\nCE_SEWSEQ whole-network preview");

            foreach (NetworkPlan plan in plans)
            {
                editor.WriteMessage(
                    "\n\nNetwork: {0}\n  Structures: {1}\n  Pipes: {2}\n  Branches: {3}",
                    plan.NetworkName,
                    plan.StructureIds.Count,
                    plan.PipeIds.Count,
                    plan.Branches.Count);

                foreach (BranchPlan branch in plan.Branches)
                {
                    editor.WriteMessage(
                        "\n  Branch-{0}: length={1:N3}; rim high={2:N3}; rim low={3:N3}; MH={4}; P={5}",
                        branch.BranchNumber,
                        branch.Length,
                        branch.HighRim,
                        branch.LowRim,
                        branch.StructureIds.Count,
                        branch.PipeIds.Count);
                }
            }

            if (unsupportedSelections > 0)
            {
                editor.WriteMessage(
                    "\n\n  Unsupported selected objects ignored: {0}.",
                    unsupportedSelections);
            }

            editor.WriteMessage(
                "\n  Naming: Branch-1 -> MH1.1, MH1.2 ... and P1.1, P1.2 ...;" +
                " later branches follow the same dotted sequence." +
                "\n  Shared junction structures keep the number of the first/longest branch that claimed them.");
        }

        private static void ApplyTemporaryNames(
            NetworkPlan plan,
            Transaction transaction)
        {
            string token = Guid.NewGuid().ToString("N");

            for (int index = 0; index < plan.StructureIds.Count; index++)
            {
                var structure = (CivilStructure)transaction.GetObject(
                    plan.StructureIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(
                    structure,
                    "CE_TMP_MH_" + token + "_" +
                    index.ToString(CultureInfo.InvariantCulture));
            }

            for (int index = 0; index < plan.PipeIds.Count; index++)
            {
                var pipe = (CivilPipe)transaction.GetObject(
                    plan.PipeIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(
                    pipe,
                    "CE_TMP_P_" + token + "_" +
                    index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ApplyBranchNames(
            NetworkPlan plan,
            Transaction transaction)
        {
            foreach (BranchPlan branch in plan.Branches)
            {
                string branchName = "Branch-" +
                    branch.BranchNumber.ToString(CultureInfo.InvariantCulture);

                for (int index = 0; index < branch.StructureIds.Count; index++)
                {
                    var structure = (CivilStructure)transaction.GetObject(
                        branch.StructureIds[index],
                        OpenMode.ForWrite,
                        false);
                    SetCivilName(
                        structure,
                        "MH" +
                        branch.BranchNumber.ToString(CultureInfo.InvariantCulture) +
                        "." +
                        (index + 1).ToString(CultureInfo.InvariantCulture));
                    structure.Description = branchName;
                }

                for (int index = 0; index < branch.PipeIds.Count; index++)
                {
                    var pipe = (CivilPipe)transaction.GetObject(
                        branch.PipeIds[index],
                        OpenMode.ForWrite,
                        false);
                    SetCivilName(
                        pipe,
                        "P" +
                        branch.BranchNumber.ToString(CultureInfo.InvariantCulture) +
                        "." +
                        (index + 1).ToString(CultureInfo.InvariantCulture));
                    pipe.Description = branchName;
                }
            }
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
                   string.Equals(
                       result.StringResult,
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void ExecuteSelectedPath(Document document)
        {
            Editor editor = document.Editor;
            Database database = document.Database;

            PromptEntityResult startResult = PromptForStructure(
                editor,
                "\nSelect START manhole/structure: ");
            if (startResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptEntityResult endResult = PromptForStructure(
                editor,
                "\nSelect END manhole/structure: ");
            if (endResult.Status != PromptStatus.OK)
            {
                return;
            }

            if (startResult.ObjectId == endResult.ObjectId)
            {
                editor.WriteMessage("\nStart and end structures must be different.");
                return;
            }

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    var startStructure = transaction.GetObject(
                        startResult.ObjectId,
                        OpenMode.ForRead,
                        false) as CivilStructure;
                    var endStructure = transaction.GetObject(
                        endResult.ObjectId,
                        OpenMode.ForRead,
                        false) as CivilStructure;

                    if (startStructure == null || endStructure == null)
                    {
                        editor.WriteMessage(
                            "\nBoth selected objects must be Civil 3D gravity-network structures.");
                        return;
                    }

                    if (startStructure.IsReferenceObject || endStructure.IsReferenceObject)
                    {
                        editor.WriteMessage("\nReferenced pipe-network parts cannot be renamed.");
                        return;
                    }

                    if (startStructure.NetworkId.IsNull ||
                        endStructure.NetworkId.IsNull ||
                        startStructure.NetworkId != endStructure.NetworkId)
                    {
                        editor.WriteMessage(
                            "\nThe selected structures must belong to the same pipe network.");
                        return;
                    }

                    var network = transaction.GetObject(
                        startStructure.NetworkId,
                        OpenMode.ForWrite,
                        false) as CivilNetwork;
                    if (network == null)
                    {
                        editor.WriteMessage("\nUnable to open the selected pipe network.");
                        return;
                    }

                    PathResult path = FindShortestPath(
                        network,
                        startResult.ObjectId,
                        endResult.ObjectId,
                        transaction);

                    if (path == null)
                    {
                        editor.WriteMessage(
                            "\nNo connected path exists between the selected structures.");
                        return;
                    }

                    string branchName = GetOrCreateBranchName(
                        network,
                        CivilApplication.ActiveDocument,
                        transaction);

                    ValidatePathNameConflicts(network, path, transaction);
                    ApplyTemporaryPathNames(path, transaction);

                    SetCivilName(network, branchName);
                    network.Description = branchName;

                    for (int index = 0; index < path.StructureIds.Count; index++)
                    {
                        var structure = (CivilStructure)transaction.GetObject(
                            path.StructureIds[index],
                            OpenMode.ForWrite,
                            false);
                        SetCivilName(
                            structure,
                            "MH" + (index + 1).ToString(CultureInfo.InvariantCulture));
                        structure.Description = branchName;
                    }

                    for (int index = 0; index < path.PipeIds.Count; index++)
                    {
                        var pipe = (CivilPipe)transaction.GetObject(
                            path.PipeIds[index],
                            OpenMode.ForWrite,
                            false);
                        SetCivilName(
                            pipe,
                            "P" + (index + 1).ToString(CultureInfo.InvariantCulture));
                        pipe.Description = branchName;
                    }

                    transaction.Commit();

                    editor.WriteMessage(
                        "\nCE_SEWSEQ selected-path mode complete. Network: {0}; structures renamed: {1}; pipes renamed: {2}.",
                        branchName,
                        path.StructureIds.Count,
                        path.PipeIds.Count);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWSEQ cancelled: " + exception.Message);
            }
        }

        private static PromptEntityResult PromptForStructure(
            Editor editor,
            string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage(
                "\nSelect a Civil 3D gravity-network structure/manhole.");
            options.AddAllowedClass(typeof(CivilStructure), false);
            return editor.GetEntity(options);
        }

        private static PathResult FindShortestPath(
            CivilNetwork network,
            ObjectId startStructureId,
            ObjectId endStructureId,
            Transaction transaction)
        {
            var adjacency = new Dictionary<ObjectId, List<PathEdge>>();

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null ||
                    pipe.StartStructureId.IsNull ||
                    pipe.EndStructureId.IsNull)
                {
                    continue;
                }

                AddPathEdge(
                    adjacency,
                    pipe.StartStructureId,
                    pipe.EndStructureId,
                    pipeId);
                AddPathEdge(
                    adjacency,
                    pipe.EndStructureId,
                    pipe.StartStructureId,
                    pipeId);
            }

            var queue = new Queue<ObjectId>();
            var visited = new HashSet<ObjectId>();
            var previous = new Dictionary<ObjectId, PreviousStep>();

            queue.Enqueue(startStructureId);
            visited.Add(startStructureId);

            while (queue.Count > 0)
            {
                ObjectId current = queue.Dequeue();
                if (current == endStructureId)
                {
                    break;
                }

                List<PathEdge> pathEdges;
                if (!adjacency.TryGetValue(current, out pathEdges))
                {
                    continue;
                }

                foreach (PathEdge edge in pathEdges.OrderBy(item => item.PipeId.Handle.Value))
                {
                    if (!visited.Add(edge.OtherStructureId))
                    {
                        continue;
                    }

                    previous[edge.OtherStructureId] =
                        new PreviousStep(current, edge.PipeId);
                    queue.Enqueue(edge.OtherStructureId);
                }
            }

            if (!visited.Contains(endStructureId))
            {
                return null;
            }

            var reverseStructures = new List<ObjectId> { endStructureId };
            var reversePipes = new List<ObjectId>();
            ObjectId cursor = endStructureId;

            while (cursor != startStructureId)
            {
                PreviousStep step;
                if (!previous.TryGetValue(cursor, out step))
                {
                    return null;
                }

                reversePipes.Add(step.PipeId);
                cursor = step.PreviousStructureId;
                reverseStructures.Add(cursor);
            }

            reverseStructures.Reverse();
            reversePipes.Reverse();
            return new PathResult(reverseStructures, reversePipes);
        }

        private static void AddPathEdge(
            IDictionary<ObjectId, List<PathEdge>> adjacency,
            ObjectId from,
            ObjectId to,
            ObjectId pipeId)
        {
            List<PathEdge> pathEdges;
            if (!adjacency.TryGetValue(from, out pathEdges))
            {
                pathEdges = new List<PathEdge>();
                adjacency[from] = pathEdges;
            }

            pathEdges.Add(new PathEdge(to, pipeId));
        }

        private static string GetOrCreateBranchName(
            CivilNetwork selectedNetwork,
            CivilDocument civilDocument,
            Transaction transaction)
        {
            Match currentMatch = BranchPattern.Match(
                selectedNetwork.Name ?? string.Empty);
            if (currentMatch.Success)
            {
                return "Branch-" + currentMatch.Groups[1].Value;
            }

            int maximumBranch = 0;
            foreach (ObjectId networkId in civilDocument.GetPipeNetworkIds())
            {
                var network = transaction.GetObject(
                    networkId,
                    OpenMode.ForRead,
                    false) as CivilNetwork;
                if (network == null)
                {
                    continue;
                }

                Match match = BranchPattern.Match(network.Name ?? string.Empty);
                int branchNumber;
                if (match.Success &&
                    int.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out branchNumber))
                {
                    maximumBranch = Math.Max(maximumBranch, branchNumber);
                }
            }

            return "Branch-" +
                (maximumBranch + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static void ValidatePathNameConflicts(
            CivilNetwork network,
            PathResult path,
            Transaction transaction)
        {
            var selectedStructures = new HashSet<ObjectId>(path.StructureIds);
            var selectedPipes = new HashSet<ObjectId>(path.PipeIds);
            var targetStructureNames = new HashSet<string>(
                Enumerable.Range(1, path.StructureIds.Count)
                    .Select(number =>
                        "MH" + number.ToString(CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);
            var targetPipeNames = new HashSet<string>(
                Enumerable.Range(1, path.PipeIds.Count)
                    .Select(number =>
                        "P" + number.ToString(CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId structureId in network.GetStructureIds())
            {
                if (selectedStructures.Contains(structureId))
                {
                    continue;
                }

                var structure = transaction.GetObject(
                    structureId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                if (structure != null &&
                    targetStructureNames.Contains(structure.Name))
                {
                    throw new InvalidOperationException(
                        "Structure name '" + structure.Name +
                        "' is already used outside the selected path.");
                }
            }

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                if (selectedPipes.Contains(pipeId))
                {
                    continue;
                }

                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe != null && targetPipeNames.Contains(pipe.Name))
                {
                    throw new InvalidOperationException(
                        "Pipe name '" + pipe.Name +
                        "' is already used outside the selected path.");
                }
            }
        }

        private static void ApplyTemporaryPathNames(
            PathResult path,
            Transaction transaction)
        {
            string token = Guid.NewGuid().ToString("N");

            for (int index = 0; index < path.StructureIds.Count; index++)
            {
                var structure = (CivilStructure)transaction.GetObject(
                    path.StructureIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(
                    structure,
                    "CE_TMP_MH_" + token + "_" +
                    index.ToString(CultureInfo.InvariantCulture));
            }

            for (int index = 0; index < path.PipeIds.Count; index++)
            {
                var pipe = (CivilPipe)transaction.GetObject(
                    path.PipeIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(
                    pipe,
                    "CE_TMP_P_" + token + "_" +
                    index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void SetCivilName(CivilEntity entity, string name)
        {
            entity.Name = name;
        }

        private sealed class NetworkPlan
        {
            public NetworkPlan(
                ObjectId networkId,
                string networkName,
                IReadOnlyList<ObjectId> structureIds,
                IReadOnlyList<ObjectId> pipeIds,
                IReadOnlyList<BranchPlan> branches)
            {
                NetworkId = networkId;
                NetworkName = networkName;
                StructureIds = structureIds;
                PipeIds = pipeIds;
                Branches = branches;
            }

            public ObjectId NetworkId { get; }
            public string NetworkName { get; }
            public IReadOnlyList<ObjectId> StructureIds { get; }
            public IReadOnlyList<ObjectId> PipeIds { get; }
            public IReadOnlyList<BranchPlan> Branches { get; }
        }

        private sealed class BranchPlan
        {
            public BranchPlan(
                IReadOnlyList<ObjectId> structureIds,
                IReadOnlyList<ObjectId> pipeIds,
                double length,
                double highRim,
                double lowRim,
                double componentRootRim,
                bool isComponentMain,
                long sortHandle)
            {
                StructureIds = structureIds;
                PipeIds = pipeIds;
                Length = length;
                HighRim = highRim;
                LowRim = lowRim;
                ComponentRootRim = componentRootRim;
                IsComponentMain = isComponentMain;
                SortHandle = sortHandle;
            }

            public int BranchNumber { get; set; }
            public IReadOnlyList<ObjectId> StructureIds { get; }
            public IReadOnlyList<ObjectId> PipeIds { get; }
            public double Length { get; }
            public double HighRim { get; }
            public double LowRim { get; }
            public double ComponentRootRim { get; }
            public bool IsComponentMain { get; }
            public long SortHandle { get; }
        }

        private sealed class GraphNode
        {
            public GraphNode(ObjectId structureId, double rimElevation)
            {
                StructureId = structureId;
                RimElevation = rimElevation;
                Edges = new List<GraphEdge>();
            }

            public ObjectId StructureId { get; }
            public double RimElevation { get; }
            public List<GraphEdge> Edges { get; }
        }

        private sealed class GraphEdge
        {
            public GraphEdge(
                ObjectId pipeId,
                ObjectId startStructureId,
                ObjectId endStructureId,
                double length)
            {
                PipeId = pipeId;
                StartStructureId = startStructureId;
                EndStructureId = endStructureId;
                Length = length;
            }

            public ObjectId PipeId { get; }
            public ObjectId StartStructureId { get; }
            public ObjectId EndStructureId { get; }
            public double Length { get; }

            public ObjectId Other(ObjectId structureId)
            {
                if (structureId == StartStructureId)
                {
                    return EndStructureId;
                }

                if (structureId == EndStructureId)
                {
                    return StartStructureId;
                }

                throw new InvalidOperationException(
                    "A graph edge was queried from a structure it does not connect.");
            }
        }

        private sealed class RootPath
        {
            public RootPath(
                IReadOnlyList<ObjectId> nodeIds,
                IReadOnlyList<ObjectId> edgeIds)
            {
                NodeIds = nodeIds;
                EdgeIds = edgeIds;
            }

            public IReadOnlyList<ObjectId> NodeIds { get; }
            public IReadOnlyList<ObjectId> EdgeIds { get; }
        }

        private sealed class CandidatePath
        {
            public CandidatePath(
                IReadOnlyList<ObjectId> nodeIds,
                IReadOnlyList<ObjectId> edgeIds,
                double length,
                double highRim,
                double lowRim,
                long sortHandle)
            {
                NodeIds = nodeIds;
                EdgeIds = edgeIds;
                Length = length;
                HighRim = highRim;
                LowRim = lowRim;
                SortHandle = sortHandle;
            }

            public IReadOnlyList<ObjectId> NodeIds { get; }
            public IReadOnlyList<ObjectId> EdgeIds { get; }
            public double Length { get; }
            public double HighRim { get; }
            public double LowRim { get; }
            public long SortHandle { get; }
        }

        private sealed class PathResult
        {
            public PathResult(
                IReadOnlyList<ObjectId> structureIds,
                IReadOnlyList<ObjectId> pipeIds)
            {
                StructureIds = structureIds;
                PipeIds = pipeIds;
            }

            public IReadOnlyList<ObjectId> StructureIds { get; }
            public IReadOnlyList<ObjectId> PipeIds { get; }
        }

        private struct PathEdge
        {
            public PathEdge(ObjectId otherStructureId, ObjectId pipeId)
            {
                OtherStructureId = otherStructureId;
                PipeId = pipeId;
            }

            public ObjectId OtherStructureId { get; }
            public ObjectId PipeId { get; }
        }

        private struct PreviousStep
        {
            public PreviousStep(
                ObjectId previousStructureId,
                ObjectId pipeId)
            {
                PreviousStructureId = previousStructureId;
                PipeId = pipeId;
            }

            public ObjectId PreviousStructureId { get; }
            public ObjectId PipeId { get; }
        }
    }
}
