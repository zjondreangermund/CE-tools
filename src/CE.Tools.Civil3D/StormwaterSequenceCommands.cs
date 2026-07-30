using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.StormwaterSequenceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Decomposes a tree-shaped Civil 3D gravity network into one main stormwater
    /// route and sequential branches. The main route can be selected by two
    /// structures or calculated automatically as the longest endpoint-to-endpoint
    /// route. Names and CE metadata are written only after a complete preview and
    /// confirmation.
    /// </summary>
    public sealed class StormwaterSequenceCommands
    {
        private const double LengthTolerance = 1e-8;

        [CommandMethod(
            "CE_TOOLS",
            "CE_SWSEQ",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SequenceStormwaterNetwork()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect stormwater gravity-network pipes or structures: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return;

            List<ObjectId> networkIds;
            int unsupported;
            try
            {
                ReadSelectedNetworks(
                    database,
                    selection.Value.GetObjectIds(),
                    out networkIds,
                    out unsupported);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SWSEQ cancelled while reading the selection: " +
                    exception.Message);
                return;
            }

            if (networkIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SWSEQ: select at least one Civil 3D gravity-network pipe or structure.");
                return;
            }

            PromptKeywordOptions modeOptions = new PromptKeywordOptions(
                "\nMain branch method [Automatic/SelectMain] <Automatic>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("Automatic");
            modeOptions.Keywords.Add("SelectMain");

            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel)
                return;

            bool selectMain =
                modeResult.Status == PromptStatus.OK &&
                modeResult.StringResult.Equals(
                    "SelectMain",
                    StringComparison.OrdinalIgnoreCase);

            ObjectId selectedStartId = ObjectId.Null;
            ObjectId selectedEndId = ObjectId.Null;
            if (selectMain)
            {
                if (networkIds.Count != 1)
                {
                    editor.WriteMessage(
                        "\nCE_SWSEQ SelectMain supports one network at a time. " +
                        "Use Automatic for multiple selected networks.");
                    return;
                }

                if (!PromptMainStructures(
                        editor,
                        database,
                        networkIds[0],
                        out selectedStartId,
                        out selectedEndId))
                    return;
            }

            List<StormwaterNetworkPlan> plans;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    plans = new List<StormwaterNetworkPlan>();
                    foreach (ObjectId networkId in networkIds.OrderBy(id => id.Handle.Value))
                    {
                        StormwaterGraph graph =
                            BuildGraph(networkId, transaction);
                        StormwaterPath mainPath = selectMain
                            ? FindPath(graph, selectedStartId, selectedEndId)
                            : FindAutomaticMainPath(graph);
                        OrientFromHighToLow(mainPath, graph);
                        List<StormwaterPath> branches =
                            ExtractBranches(graph, mainPath);
                        plans.Add(new StormwaterNetworkPlan(
                            graph,
                            mainPath,
                            branches));
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SWSEQ cancelled. " + exception.Message);
                return;
            }

            WritePreview(editor, plans, unsupported, selectMain);

            if (!Confirm(
                    editor,
                    "Apply the displayed stormwater main/branch names and sequence"))
            {
                editor.WriteMessage(
                    "\nCE_SWSEQ cancelled. No network part names were changed.");
                return;
            }

            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    StormwaterMetadata.EnsureRegApp(database, transaction);
                    foreach (StormwaterNetworkPlan plan in plans)
                        ApplyPlan(plan, transaction);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_SWSEQ complete. Networks: {0}; branches including main: {1}; pipes: {2}; structures: {3}.",
                    plans.Count,
                    plans.Sum(plan => plan.Branches.Count + 1),
                    plans.Sum(plan => plan.Graph.Edges.Count),
                    plans.Sum(plan => plan.Graph.Nodes.Count));
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SWSEQ cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        private static void ReadSelectedNetworks(
            Database database,
            IEnumerable<ObjectId> selectedIds,
            out List<ObjectId> networkIds,
            out int unsupported)
        {
            var ids = new HashSet<ObjectId>();
            unsupported = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId selectedId in selectedIds)
                {
                    DBObject selected = transaction.GetObject(
                        selectedId,
                        OpenMode.ForRead,
                        false);

                    var pipe = selected as CivilPipe;
                    if (pipe != null && !pipe.NetworkId.IsNull)
                    {
                        ids.Add(pipe.NetworkId);
                        continue;
                    }

                    var structure = selected as CivilStructure;
                    if (structure != null && !structure.NetworkId.IsNull)
                    {
                        ids.Add(structure.NetworkId);
                        continue;
                    }

                    unsupported++;
                }
            }

            networkIds = ids.ToList();
        }

        private static bool PromptMainStructures(
            Editor editor,
            Database database,
            ObjectId expectedNetworkId,
            out ObjectId startId,
            out ObjectId endId)
        {
            startId = ObjectId.Null;
            endId = ObjectId.Null;

            PromptEntityOptions startOptions = new PromptEntityOptions(
                "\nSelect the first structure on the stormwater main branch: ");
            startOptions.SetRejectMessage(
                "\nSelect a Civil 3D gravity-network structure.");
            startOptions.AddAllowedClass(typeof(CivilStructure), true);

            PromptEntityResult startResult = editor.GetEntity(startOptions);
            if (startResult.Status != PromptStatus.OK)
                return false;

            PromptEntityOptions endOptions = new PromptEntityOptions(
                "\nSelect the last structure on the stormwater main branch: ");
            endOptions.SetRejectMessage(
                "\nSelect a Civil 3D gravity-network structure.");
            endOptions.AddAllowedClass(typeof(CivilStructure), true);

            PromptEntityResult endResult = editor.GetEntity(endOptions);
            if (endResult.Status != PromptStatus.OK)
                return false;

            if (startResult.ObjectId == endResult.ObjectId)
            {
                editor.WriteMessage(
                    "\nCE_SWSEQ: the main branch requires two different structures.");
                return false;
            }

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                var start = transaction.GetObject(
                    startResult.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                var end = transaction.GetObject(
                    endResult.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilStructure;

                if (start == null ||
                    end == null ||
                    start.NetworkId != expectedNetworkId ||
                    end.NetworkId != expectedNetworkId)
                {
                    editor.WriteMessage(
                        "\nCE_SWSEQ: both selected main structures must belong to the selected network.");
                    return false;
                }
            }

            startId = startResult.ObjectId;
            endId = endResult.ObjectId;
            return true;
        }

        private static StormwaterGraph BuildGraph(
            ObjectId networkId,
            Transaction transaction)
        {
            var network = transaction.GetObject(
                networkId,
                OpenMode.ForRead,
                false) as CivilNetwork;
            if (network == null)
                throw new InvalidOperationException(
                    "A selected object does not resolve to a gravity pipe network.");

            if (network.IsReferenceObject)
                throw new InvalidOperationException(
                    "Referenced network '" + network.Name +
                    "' cannot be renamed by CE Tools.");

            var graph = new StormwaterGraph(networkId, network.Name);

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
                if (pipe == null)
                    continue;

                if (pipe.IsReferenceObject)
                    throw new InvalidOperationException(
                        "Network '" + network.Name +
                        "' contains a referenced pipe.");

                if (pipe.StartStructureId.IsNull ||
                    pipe.EndStructureId.IsNull)
                    throw new InvalidOperationException(
                        "Network '" + network.Name +
                        "' contains an unconnected pipe.");

                StormwaterNode start =
                    GetOrCreateNode(graph, pipe.StartStructureId, transaction);
                StormwaterNode end =
                    GetOrCreateNode(graph, pipe.EndStructureId, transaction);

                double length = ReadPipeLength(pipe);
                var edge = new StormwaterEdge(
                    pipeId,
                    start.StructureId,
                    end.StructureId,
                    length);

                graph.Edges.Add(edge);
                start.Edges.Add(edge);
                end.Edges.Add(edge);
            }

            if (graph.Edges.Count == 0)
                throw new InvalidOperationException(
                    "Network '" + network.Name + "' contains no connected pipes.");

            if (graph.Edges.Count != graph.Nodes.Count - 1)
                throw new InvalidOperationException(
                    "Network '" + network.Name +
                    "' must be a connected tree for automatic branch sequencing. " +
                    "Loops, disconnected groups and isolated connected cycles require engineering review.");

            ValidateConnected(graph);
            return graph;
        }

        private static StormwaterNode GetOrCreateNode(
            StormwaterGraph graph,
            ObjectId structureId,
            Transaction transaction)
        {
            StormwaterNode existing;
            if (graph.Nodes.TryGetValue(structureId, out existing))
                return existing;

            var structure = transaction.GetObject(
                structureId,
                OpenMode.ForRead,
                false) as CivilStructure;
            if (structure == null)
                throw new InvalidOperationException(
                    "A connected structure could not be opened.");

            if (structure.IsReferenceObject)
                throw new InvalidOperationException(
                    "Network '" + graph.NetworkName +
                    "' contains a referenced structure.");

            double rim = structure.RimElevation;
            if (double.IsNaN(rim) || double.IsInfinity(rim))
                rim = structure.Position.Z;

            var node = new StormwaterNode(
                structureId,
                structure.Position,
                rim);
            graph.Nodes.Add(structureId, node);
            return node;
        }

        private static double ReadPipeLength(CivilPipe pipe)
        {
            try
            {
                Point3d start = pipe.GetPointAtParam(0.0);
                Point3d end = pipe.GetPointAtParam(1.0);
                double length = start.DistanceTo(end);
                return length > LengthTolerance ? length : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static void ValidateConnected(StormwaterGraph graph)
        {
            var visited = new HashSet<ObjectId>();
            var stack = new Stack<ObjectId>();
            ObjectId first = graph.Nodes.Keys.First();
            stack.Push(first);

            while (stack.Count > 0)
            {
                ObjectId current = stack.Pop();
                if (!visited.Add(current))
                    continue;

                foreach (StormwaterEdge edge in graph.Nodes[current].Edges)
                    stack.Push(edge.Other(current));
            }

            if (visited.Count != graph.Nodes.Count)
                throw new InvalidOperationException(
                    "Network '" + graph.NetworkName +
                    "' contains disconnected pipe groups.");
        }

        private static StormwaterPath FindAutomaticMainPath(
            StormwaterGraph graph)
        {
            List<ObjectId> endpoints = graph.Nodes.Values
                .Where(node => node.Edges.Count == 1)
                .Select(node => node.StructureId)
                .OrderBy(id => id.Handle.Value)
                .ToList();

            if (endpoints.Count < 2)
                throw new InvalidOperationException(
                    "Network '" + graph.NetworkName +
                    "' does not have two usable endpoints.");

            StormwaterPath best = null;
            for (int first = 0; first < endpoints.Count - 1; first++)
            {
                for (int second = first + 1; second < endpoints.Count; second++)
                {
                    StormwaterPath candidate =
                        FindPath(graph, endpoints[first], endpoints[second]);
                    if (best == null ||
                        candidate.Length > best.Length + LengthTolerance ||
                        (Math.Abs(candidate.Length - best.Length) <= LengthTolerance &&
                         CandidateStartsHigher(candidate, best, graph)))
                    {
                        best = candidate;
                    }
                }
            }

            if (best == null)
                throw new InvalidOperationException(
                    "The automatic main branch could not be calculated.");

            return best;
        }

        private static bool CandidateStartsHigher(
            StormwaterPath candidate,
            StormwaterPath current,
            StormwaterGraph graph)
        {
            double candidateHigh = Math.Max(
                graph.Nodes[candidate.NodeIds.First()].RimElevation,
                graph.Nodes[candidate.NodeIds.Last()].RimElevation);
            double currentHigh = Math.Max(
                graph.Nodes[current.NodeIds.First()].RimElevation,
                graph.Nodes[current.NodeIds.Last()].RimElevation);

            if (Math.Abs(candidateHigh - currentHigh) > LengthTolerance)
                return candidateHigh > currentHigh;

            return candidate.NodeIds.First().Handle.Value <
                   current.NodeIds.First().Handle.Value;
        }

        private static StormwaterPath FindPath(
            StormwaterGraph graph,
            ObjectId startId,
            ObjectId endId)
        {
            if (!graph.Nodes.ContainsKey(startId) ||
                !graph.Nodes.ContainsKey(endId))
                throw new InvalidOperationException(
                    "A selected main structure is not part of the chosen network.");

            var parentNode = new Dictionary<ObjectId, ObjectId>();
            var parentEdge = new Dictionary<ObjectId, StormwaterEdge>();
            var queue = new Queue<ObjectId>();
            var visited = new HashSet<ObjectId>();

            queue.Enqueue(startId);
            visited.Add(startId);

            while (queue.Count > 0)
            {
                ObjectId current = queue.Dequeue();
                if (current == endId)
                    break;

                foreach (StormwaterEdge edge in graph.Nodes[current].Edges)
                {
                    ObjectId next = edge.Other(current);
                    if (!visited.Add(next))
                        continue;

                    parentNode[next] = current;
                    parentEdge[next] = edge;
                    queue.Enqueue(next);
                }
            }

            if (!visited.Contains(endId))
                throw new InvalidOperationException(
                    "No connected route exists between the selected main structures.");

            var nodes = new List<ObjectId>();
            var edges = new List<StormwaterEdge>();
            ObjectId cursor = endId;
            nodes.Add(cursor);

            while (cursor != startId)
            {
                StormwaterEdge edge = parentEdge[cursor];
                edges.Add(edge);
                cursor = parentNode[cursor];
                nodes.Add(cursor);
            }

            nodes.Reverse();
            edges.Reverse();
            return new StormwaterPath(nodes, edges);
        }

        private static void OrientFromHighToLow(
            StormwaterPath path,
            StormwaterGraph graph)
        {
            StormwaterNode first = graph.Nodes[path.NodeIds.First()];
            StormwaterNode last = graph.Nodes[path.NodeIds.Last()];

            bool reverse =
                first.RimElevation < last.RimElevation - LengthTolerance ||
                (Math.Abs(first.RimElevation - last.RimElevation) <= LengthTolerance &&
                 first.StructureId.Handle.Value >
                 last.StructureId.Handle.Value);

            if (reverse)
                path.Reverse();
        }

        private static List<StormwaterPath> ExtractBranches(
            StormwaterGraph graph,
            StormwaterPath mainPath)
        {
            var usedEdges = new HashSet<ObjectId>(
                mainPath.Edges.Select(edge => edge.PipeId));
            var assignedNodes = new HashSet<ObjectId>(mainPath.NodeIds);
            var nodeOrder = new Dictionary<ObjectId, int>();
            int nextOrder = 0;

            foreach (ObjectId nodeId in mainPath.NodeIds)
                nodeOrder[nodeId] = nextOrder++;

            var branches = new List<StormwaterPath>();

            while (usedEdges.Count < graph.Edges.Count)
            {
                BranchCandidate best = null;

                foreach (ObjectId rootId in assignedNodes
                    .OrderBy(id => nodeOrder[id])
                    .ThenBy(id => id.Handle.Value)
                    .ToList())
                {
                    foreach (StormwaterEdge edge in graph.Nodes[rootId].Edges
                        .Where(item => !usedEdges.Contains(item.PipeId))
                        .OrderByDescending(item => item.Length)
                        .ThenBy(item => item.PipeId.Handle.Value))
                    {
                        StormwaterPath candidatePath =
                            FindLongestUnusedPath(
                                graph,
                                rootId,
                                edge,
                                usedEdges,
                                new HashSet<ObjectId>());

                        var candidate = new BranchCandidate(
                            rootId,
                            nodeOrder[rootId],
                            candidatePath);

                        if (best == null ||
                            candidate.RootOrder < best.RootOrder ||
                            (candidate.RootOrder == best.RootOrder &&
                             candidate.Path.Length >
                             best.Path.Length + LengthTolerance) ||
                            (candidate.RootOrder == best.RootOrder &&
                             Math.Abs(candidate.Path.Length - best.Path.Length) <= LengthTolerance &&
                             candidate.Path.Edges.First().PipeId.Handle.Value <
                             best.Path.Edges.First().PipeId.Handle.Value))
                        {
                            best = candidate;
                        }
                    }
                }

                if (best == null)
                    throw new InvalidOperationException(
                        "The remaining stormwater network branches could not be sequenced.");

                foreach (StormwaterEdge edge in best.Path.Edges)
                    usedEdges.Add(edge.PipeId);

                foreach (ObjectId nodeId in best.Path.NodeIds)
                {
                    if (assignedNodes.Add(nodeId))
                        nodeOrder[nodeId] = nextOrder++;
                }

                branches.Add(best.Path);
            }

            return branches;
        }

        private static StormwaterPath FindLongestUnusedPath(
            StormwaterGraph graph,
            ObjectId rootId,
            StormwaterEdge firstEdge,
            ISet<ObjectId> usedEdges,
            ISet<ObjectId> recursionEdges)
        {
            var localRecursion = new HashSet<ObjectId>(recursionEdges)
            {
                firstEdge.PipeId
            };

            ObjectId nextId = firstEdge.Other(rootId);
            List<StormwaterEdge> available = graph.Nodes[nextId].Edges
                .Where(edge =>
                    edge.PipeId != firstEdge.PipeId &&
                    !usedEdges.Contains(edge.PipeId) &&
                    !localRecursion.Contains(edge.PipeId))
                .ToList();

            if (available.Count == 0)
            {
                return new StormwaterPath(
                    new[] { rootId, nextId },
                    new[] { firstEdge });
            }

            StormwaterPath bestTail = null;
            foreach (StormwaterEdge nextEdge in available)
            {
                StormwaterPath tail = FindLongestUnusedPath(
                    graph,
                    nextId,
                    nextEdge,
                    usedEdges,
                    localRecursion);

                if (bestTail == null ||
                    tail.Length > bestTail.Length + LengthTolerance ||
                    (Math.Abs(tail.Length - bestTail.Length) <= LengthTolerance &&
                     tail.Edges.First().PipeId.Handle.Value <
                     bestTail.Edges.First().PipeId.Handle.Value))
                {
                    bestTail = tail;
                }
            }

            var nodes = new List<ObjectId> { rootId };
            nodes.AddRange(bestTail.NodeIds);
            var edges = new List<StormwaterEdge> { firstEdge };
            edges.AddRange(bestTail.Edges);
            return new StormwaterPath(nodes, edges);
        }

        private static void WritePreview(
            Editor editor,
            IEnumerable<StormwaterNetworkPlan> plans,
            int unsupported,
            bool selectedMain)
        {
            editor.WriteMessage(
                "\nCE_SWSEQ preview. Main method: {0}; unsupported selections ignored: {1}.",
                selectedMain ? "selected start/end" : "automatic longest route",
                unsupported);

            foreach (StormwaterNetworkPlan plan in plans)
            {
                editor.WriteMessage(
                    "\n  Network: {0}; main length: {1:0.###}; side branches: {2}.",
                    plan.Graph.NetworkName,
                    plan.MainPath.Length,
                    plan.Branches.Count);

                editor.WriteMessage(
                    "\n    SW-MAIN: pipes={0}; nodes={1}; from rim {2:0.###} to {3:0.###}.",
                    plan.MainPath.Edges.Count,
                    plan.MainPath.NodeIds.Count,
                    plan.Graph.Nodes[plan.MainPath.NodeIds.First()].RimElevation,
                    plan.Graph.Nodes[plan.MainPath.NodeIds.Last()].RimElevation);

                for (int index = 0; index < plan.Branches.Count; index++)
                {
                    StormwaterPath branch = plan.Branches[index];
                    editor.WriteMessage(
                        "\n    SW-B{0:00}: pipes={1}; nodes={2}; length={3:0.###}; attaches at {4}.",
                        index + 1,
                        branch.Edges.Count,
                        branch.NodeIds.Count,
                        branch.Length,
                        ReadStructureName(
                            branch.NodeIds.First(),
                            plan.Graph));
                }
            }
        }

        private static string ReadStructureName(
            ObjectId structureId,
            StormwaterGraph graph)
        {
            StormwaterNode node = graph.Nodes[structureId];
            return "handle " + node.StructureId.Handle;
        }

        private static void ApplyPlan(
            StormwaterNetworkPlan plan,
            Transaction transaction)
        {
            var network = transaction.GetObject(
                plan.Graph.NetworkId,
                OpenMode.ForWrite,
                false) as CivilNetwork;
            if (network == null)
                throw new InvalidOperationException(
                    "A stormwater network could not be reopened.");

            string token = Guid.NewGuid().ToString("N");
            foreach (StormwaterNode node in plan.Graph.Nodes.Values)
            {
                var structure = transaction.GetObject(
                    node.StructureId,
                    OpenMode.ForWrite,
                    false) as CivilStructure;
                if (structure != null)
                    structure.Name = "CE-TMP-SW-N-" + token + "-" +
                        node.StructureId.Handle;
            }

            foreach (StormwaterEdge edge in plan.Graph.Edges)
            {
                var pipe = transaction.GetObject(
                    edge.PipeId,
                    OpenMode.ForWrite,
                    false) as CivilPipe;
                if (pipe != null)
                    pipe.Name = "CE-TMP-SW-P-" + token + "-" +
                        edge.PipeId.Handle;
            }

            ApplyPathNames(
                plan.Graph,
                plan.MainPath,
                "SW-MAIN",
                transaction,
                true);

            for (int index = 0; index < plan.Branches.Count; index++)
            {
                ApplyPathNames(
                    plan.Graph,
                    plan.Branches[index],
                    "SW-B" + (index + 1).ToString("00", CultureInfo.InvariantCulture),
                    transaction,
                    false);
            }

            network.Description = string.Format(
                CultureInfo.InvariantCulture,
                "CE stormwater sequence | main + {0} branch(es) | review before issue",
                plan.Branches.Count);
        }

        private static void ApplyPathNames(
            StormwaterGraph graph,
            StormwaterPath path,
            string branchKey,
            Transaction transaction,
            bool renameFirstStructure)
        {
            string networkHandle = graph.NetworkId.Handle.ToString();

            for (int index = 0; index < path.Edges.Count; index++)
            {
                var pipe = transaction.GetObject(
                    path.Edges[index].PipeId,
                    OpenMode.ForWrite,
                    false) as CivilPipe;
                if (pipe == null)
                    continue;

                int sequence = index + 1;
                pipe.Name = branchKey + "-P" +
                    sequence.ToString("00", CultureInfo.InvariantCulture);
                pipe.Description =
                    "CE stormwater | " + branchKey +
                    " | Pipe " +
                    sequence.ToString("00", CultureInfo.InvariantCulture);
                StormwaterMetadata.WriteTag(
                    pipe,
                    new StormwaterPartTag(
                        networkHandle,
                        branchKey,
                        sequence,
                        "Pipe"));
            }

            int nodeSequence = 1;
            for (int index = 0; index < path.NodeIds.Count; index++)
            {
                if (index == 0 && !renameFirstStructure)
                    continue;

                var structure = transaction.GetObject(
                    path.NodeIds[index],
                    OpenMode.ForWrite,
                    false) as CivilStructure;
                if (structure == null)
                    continue;

                structure.Name = branchKey + "-N" +
                    nodeSequence.ToString("00", CultureInfo.InvariantCulture);
                structure.Description =
                    "CE stormwater | " + branchKey +
                    " | Node " +
                    nodeSequence.ToString("00", CultureInfo.InvariantCulture);
                StormwaterMetadata.WriteTag(
                    structure,
                    new StormwaterPartTag(
                        networkHandle,
                        branchKey,
                        nodeSequence,
                        "Structure"));
                nodeSequence++;
            }
        }

        private static bool Confirm(Editor editor, string message)
        {
            PromptKeywordOptions options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");

            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   result.StringResult.Equals(
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StormwaterNetworkPlan
        {
            public StormwaterNetworkPlan(
                StormwaterGraph graph,
                StormwaterPath mainPath,
                IReadOnlyList<StormwaterPath> branches)
            {
                Graph = graph;
                MainPath = mainPath;
                Branches = branches;
            }

            public StormwaterGraph Graph { get; }
            public StormwaterPath MainPath { get; }
            public IReadOnlyList<StormwaterPath> Branches { get; }
        }

        private sealed class BranchCandidate
        {
            public BranchCandidate(
                ObjectId rootId,
                int rootOrder,
                StormwaterPath path)
            {
                RootId = rootId;
                RootOrder = rootOrder;
                Path = path;
            }

            public ObjectId RootId { get; }
            public int RootOrder { get; }
            public StormwaterPath Path { get; }
        }
    }

    internal sealed class StormwaterGraph
    {
        public StormwaterGraph(
            ObjectId networkId,
            string networkName)
        {
            NetworkId = networkId;
            NetworkName = networkName ?? string.Empty;
            Nodes = new Dictionary<ObjectId, StormwaterNode>();
            Edges = new List<StormwaterEdge>();
        }

        public ObjectId NetworkId { get; }
        public string NetworkName { get; }
        public IDictionary<ObjectId, StormwaterNode> Nodes { get; }
        public IList<StormwaterEdge> Edges { get; }
    }

    internal sealed class StormwaterNode
    {
        public StormwaterNode(
            ObjectId structureId,
            Point3d position,
            double rimElevation)
        {
            StructureId = structureId;
            Position = position;
            RimElevation = rimElevation;
            Edges = new List<StormwaterEdge>();
        }

        public ObjectId StructureId { get; }
        public Point3d Position { get; }
        public double RimElevation { get; }
        public IList<StormwaterEdge> Edges { get; }
    }

    internal sealed class StormwaterEdge
    {
        public StormwaterEdge(
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

        public ObjectId Other(ObjectId nodeId)
        {
            if (nodeId == StartStructureId)
                return EndStructureId;
            if (nodeId == EndStructureId)
                return StartStructureId;

            throw new InvalidOperationException(
                "A stormwater edge was queried from a non-connected node.");
        }
    }

    internal sealed class StormwaterPath
    {
        public StormwaterPath(
            IEnumerable<ObjectId> nodeIds,
            IEnumerable<StormwaterEdge> edges)
        {
            NodeIds = nodeIds.ToList();
            Edges = edges.ToList();
        }

        public IList<ObjectId> NodeIds { get; }
        public IList<StormwaterEdge> Edges { get; }
        public double Length => Edges.Sum(edge => edge.Length);

        public void Reverse()
        {
            ((List<ObjectId>)NodeIds).Reverse();
            ((List<StormwaterEdge>)Edges).Reverse();
        }
    }

    internal sealed class StormwaterPartTag
    {
        public StormwaterPartTag(
            string networkHandle,
            string branchKey,
            int sequence,
            string role)
        {
            NetworkHandle = networkHandle ?? string.Empty;
            BranchKey = branchKey ?? string.Empty;
            Sequence = sequence;
            Role = role ?? string.Empty;
        }

        public string NetworkHandle { get; }
        public string BranchKey { get; }
        public int Sequence { get; }
        public string Role { get; }
    }

    internal static class StormwaterMetadata
    {
        public const string SequenceRegAppName = "CE_TOOLS_SWSEQ";
        public const string AlignmentRegAppName = "CE_TOOLS_SWALIGN";
        public const string ProfileRegAppName = "CE_TOOLS_SWPROFILE";

        public static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            EnsureOneRegApp(
                database,
                transaction,
                SequenceRegAppName);
            EnsureOneRegApp(
                database,
                transaction,
                AlignmentRegAppName);
            EnsureOneRegApp(
                database,
                transaction,
                ProfileRegAppName);
        }

        private static void EnsureOneRegApp(
            Database database,
            Transaction transaction,
            string name)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false);
            if (table.Has(name))
                return;

            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        public static void WriteTag(
            DBObject databaseObject,
            StormwaterPartTag tag)
        {
            databaseObject.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    SequenceRegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    tag.NetworkHandle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    tag.BranchKey),
                new TypedValue(
                    (int)DxfCode.ExtendedDataInteger32,
                    tag.Sequence),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    tag.Role));
        }

        public static bool TryReadTag(
            DBObject databaseObject,
            out StormwaterPartTag tag)
        {
            tag = null;
            using (ResultBuffer buffer =
                databaseObject.GetXDataForApplication(
                    SequenceRegAppName))
            {
                if (buffer == null)
                    return false;

                TypedValue[] values = buffer.AsArray();
                string[] strings = values
                    .Where(value =>
                        value.TypeCode ==
                        (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                int sequence = values
                    .Where(value =>
                        value.TypeCode ==
                        (int)DxfCode.ExtendedDataInteger32)
                    .Select(value => Convert.ToInt32(
                        value.Value,
                        CultureInfo.InvariantCulture))
                    .FirstOrDefault();

                if (strings.Length < 3)
                    return false;

                tag = new StormwaterPartTag(
                    strings[0],
                    strings[1],
                    sequence,
                    strings[2]);
                return true;
            }
        }
    }
}
