using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerNetworkDynamicSequenceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Rebuilds CE sewer branch numbering from the live connected topology.
    /// Branch numbers are always compact (1..N), pipe and manhole sequences restart
    /// at .1, and shared downstream junctions retain the receiving branch name.
    /// </summary>
    public sealed class SewerNetworkDynamicSequenceCommands
    {
        private static readonly Regex CePipeName = new Regex(
            @"^P\d+\.\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CeBranchName = new Regex(
            @"^Branch\s*-\s*\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [CommandMethod("CE_TOOLS", "CE_SEWAUTOSEQ", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResequenceSelectedNetwork()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ObjectId networkId = PromptNetwork(document);
            if (networkId.IsNull) return;
            SewerDynamicSequenceResult result = Resequence(
                document,
                new[] { networkId },
                true);
            document.Editor.Regen();
            WriteResult(document.Editor, "CE_SEWAUTOSEQ", result);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWAUTOSEQALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResequenceAllCeNetworks()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            SewerDynamicSequenceResult result = ResequenceAll(document, false);
            document.Editor.Regen();
            WriteResult(document.Editor, "CE_SEWAUTOSEQALL", result);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWAUTOSEQSETTINGS", CommandFlags.Modal)]
        public void ConfigureAutomaticResequence()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Sewer Sequence",
                "Automatically compact and refresh CE sewer numbering after a pipe or manhole is deleted, reconnected or moved.");
            model.AddChoice(
                "Enabled",
                "Automatic Resequence",
                "Dynamic sewer resequencing",
                SewerNetworkDynamicSequenceManager.Enabled ? "Enabled" : "Disabled",
                "Only networks already carrying CE Branch/P/MH names are changed automatically.",
                new[] { "Enabled", "Disabled" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            SewerNetworkDynamicSequenceManager.Enabled = string.Equals(
                model.Text("Enabled"),
                "Enabled",
                StringComparison.OrdinalIgnoreCase);
            SewerNetworkDynamicSequenceManager.Queue();
        }

        internal static SewerDynamicSequenceResult ResequenceAll(
            Document document,
            bool force)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return new SewerDynamicSequenceResult();
            var ids = new List<ObjectId>();
            try
            {
                foreach (ObjectId id in civilDocument.GetPipeNetworkIds())
                    if (!id.IsNull && !id.IsErased) ids.Add(id);
            }
            catch
            {
                return new SewerDynamicSequenceResult
                {
                    Warning = "The active pipe-network collection was unavailable."
                };
            }
            return Resequence(document, ids, force);
        }

        internal static SewerDynamicSequenceResult Resequence(
            Document document,
            IEnumerable<ObjectId> networkIds,
            bool force)
        {
            var result = new SewerDynamicSequenceResult();
            if (document == null || networkIds == null) return result;
            Database database = document.Database;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            var refreshedNetworks = new List<ObjectId>();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId networkId in networkIds
                        .Where(id => !id.IsNull && !id.IsErased)
                        .Distinct())
                    {
                        CivilNetwork network;
                        try
                        {
                            network = transaction.GetObject(
                                networkId,
                                OpenMode.ForWrite,
                                false) as CivilNetwork;
                        }
                        catch
                        {
                            result.SkippedNetworks++;
                            continue;
                        }
                        if (network == null || network.IsReferenceObject)
                        {
                            result.SkippedNetworks++;
                            continue;
                        }
                        if (!force && !IsCeSequenced(network, transaction))
                        {
                            result.SkippedNetworks++;
                            continue;
                        }

                        SewerTopology topology = BuildTopology(network, transaction);
                        if (topology.Edges.Count == 0)
                        {
                            result.SkippedNetworks++;
                            continue;
                        }

                        List<SewerBranchPath> branches = BuildBranches(topology);
                        if (branches.Count == 0)
                        {
                            result.SkippedNetworks++;
                            continue;
                        }

                        ApplyBranches(
                            network,
                            branches,
                            transaction,
                            result);
                        refreshedNetworks.Add(networkId);
                        result.NetworksUpdated++;
                        result.BranchesUpdated += branches.Count;
                    }
                    transaction.Commit();
                }

                foreach (ObjectId networkId in refreshedNetworks)
                {
                    SewerNetworkLabelCommands.EnsureLabels(
                        document,
                        new[] { networkId });
                }
                if (refreshedNetworks.Count > 0)
                {
                    SewerLabelStyleSyncCommands.ApplySelectedStyles(document);
                    RefreshGeneratedAlignments(
                        document,
                        civilDocument,
                        refreshedNetworks,
                        result);
                    try
                    {
                        LinkedRefreshEngine.Refresh(document, false);
                    }
                    catch
                    {
                        result.RefreshFailures++;
                    }
                }
            }
            catch (System.Exception exception)
            {
                result.Warning = "Dynamic sewer resequence stopped: " + exception.Message;
            }
            return result;
        }

        private static bool IsCeSequenced(
            CivilNetwork network,
            Transaction transaction)
        {
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                CivilPipe pipe;
                try
                {
                    pipe = transaction.GetObject(
                        pipeId,
                        OpenMode.ForRead,
                        false) as CivilPipe;
                }
                catch
                {
                    continue;
                }
                if (pipe != null &&
                    (CePipeName.IsMatch(pipe.Name ?? string.Empty) ||
                     CeBranchName.IsMatch(pipe.Description ?? string.Empty)))
                    return true;
            }
            return false;
        }

        private static SewerTopology BuildTopology(
            CivilNetwork network,
            Transaction transaction)
        {
            var topology = new SewerTopology(network.ObjectId, network.Name);
            foreach (ObjectId structureId in network.GetStructureIds())
            {
                CivilStructure structure;
                try
                {
                    structure = transaction.GetObject(
                        structureId,
                        OpenMode.ForRead,
                        false) as CivilStructure;
                }
                catch
                {
                    continue;
                }
                if (structure == null) continue;
                double rim;
                try { rim = structure.RimElevation; }
                catch { rim = structure.Position.Z; }
                if (double.IsNaN(rim) || double.IsInfinity(rim))
                    rim = structure.Position.Z;
                topology.Nodes[structureId] = new SewerTopologyNode(
                    structureId,
                    rim,
                    structure.Position);
            }

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                CivilPipe pipe;
                try
                {
                    pipe = transaction.GetObject(
                        pipeId,
                        OpenMode.ForRead,
                        false) as CivilPipe;
                }
                catch
                {
                    continue;
                }
                if (pipe == null || pipe.StartStructureId.IsNull ||
                    pipe.EndStructureId.IsNull ||
                    !topology.Nodes.ContainsKey(pipe.StartStructureId) ||
                    !topology.Nodes.ContainsKey(pipe.EndStructureId))
                    continue;

                double length = ReadLength(pipe);
                var edge = new SewerTopologyEdge(
                    pipeId,
                    pipe.StartStructureId,
                    pipe.EndStructureId,
                    Math.Max(length, 0.001),
                    (pipe.Name ?? string.Empty).StartsWith(
                        "P1.",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        pipe.Description,
                        "Branch-1",
                        StringComparison.OrdinalIgnoreCase));
                topology.Edges.Add(edge);
                topology.Nodes[edge.Start].Edges.Add(edge);
                topology.Nodes[edge.End].Edges.Add(edge);
            }
            return topology;
        }

        private static double ReadLength(CivilPipe pipe)
        {
            foreach (string name in new[] { "Length2D", "Length3D", "Length" })
            {
                try
                {
                    PropertyInfo property = pipe.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    object value = property.GetValue(pipe, null);
                    if (value != null)
                    {
                        double length = Convert.ToDouble(
                            value,
                            CultureInfo.InvariantCulture);
                        if (length > 0.0) return length;
                    }
                }
                catch
                {
                    // Try another length property.
                }
            }
            try
            {
                return pipe.GetPointAtParam(0.0).DistanceTo(
                    pipe.GetPointAtParam(1.0));
            }
            catch
            {
                return 0.001;
            }
        }

        private static List<SewerBranchPath> BuildBranches(
            SewerTopology topology)
        {
            var result = new List<SewerBranchPath>();
            foreach (List<ObjectId> component in ReadComponents(topology))
            {
                SewerBranchPath main = BuildExistingMain(topology, component) ??
                    BuildDiameterPath(topology, component);
                if (main == null || main.Edges.Count == 0) continue;
                OrientHighToLow(main, topology);
                result.Add(main);

                var usedEdges = new HashSet<ObjectId>(
                    main.Edges.Select(edge => edge.Id));
                var queue = new Queue<BranchSeed>();
                for (int index = 0; index < main.Nodes.Count; index++)
                {
                    ObjectId nodeId = main.Nodes[index];
                    foreach (SewerTopologyEdge edge in topology.Nodes[nodeId].Edges
                        .Where(edge => !usedEdges.Contains(edge.Id))
                        .OrderBy(edge => edge.Id.Handle.Value))
                    {
                        queue.Enqueue(new BranchSeed(nodeId, edge));
                    }
                }

                while (queue.Count > 0)
                {
                    BranchSeed seed = queue.Dequeue();
                    if (usedEdges.Contains(seed.Edge.Id)) continue;
                    SewerBranchPath branch = WalkBranchSegment(
                        topology,
                        seed.Anchor,
                        seed.Edge,
                        usedEdges,
                        queue);
                    if (branch == null || branch.Edges.Count == 0) continue;
                    OrientHighToLow(branch, topology);
                    result.Add(branch);
                }
            }
            return result;
        }

        private static List<List<ObjectId>> ReadComponents(
            SewerTopology topology)
        {
            var result = new List<List<ObjectId>>();
            var remaining = new HashSet<ObjectId>(topology.Nodes.Keys);
            while (remaining.Count > 0)
            {
                ObjectId start = remaining
                    .OrderBy(id => id.Handle.Value)
                    .First();
                var component = new List<ObjectId>();
                var queue = new Queue<ObjectId>();
                queue.Enqueue(start);
                remaining.Remove(start);
                while (queue.Count > 0)
                {
                    ObjectId current = queue.Dequeue();
                    component.Add(current);
                    foreach (SewerTopologyEdge edge in topology.Nodes[current].Edges)
                    {
                        ObjectId next = edge.Other(current);
                        if (remaining.Remove(next)) queue.Enqueue(next);
                    }
                }
                if (component.Any(id => topology.Nodes[id].Edges.Count > 0))
                    result.Add(component);
            }
            return result
                .OrderByDescending(component => component.Count)
                .ThenBy(component => component.Min(id => id.Handle.Value))
                .ToList();
        }

        private static SewerBranchPath BuildExistingMain(
            SewerTopology topology,
            ICollection<ObjectId> component)
        {
            var componentSet = new HashSet<ObjectId>(component);
            var candidates = new List<SewerTopologyEdge>();
            foreach (SewerTopologyEdge edge in topology.Edges)
            {
                if (!componentSet.Contains(edge.Start) ||
                    !componentSet.Contains(edge.End)) continue;
                candidates.Add(edge);
            }
            candidates = candidates
                .Where(edge => edge != null && edge.IsBranchOne)
                .ToList();
            return BuildSimplePath(topology, candidates);
        }

        private static SewerBranchPath BuildSimplePath(
            SewerTopology topology,
            IList<SewerTopologyEdge> edges)
        {
            if (edges == null || edges.Count == 0) return null;
            var adjacency = new Dictionary<ObjectId, List<SewerTopologyEdge>>();
            foreach (SewerTopologyEdge edge in edges)
            {
                AddEdge(adjacency, edge.Start, edge);
                AddEdge(adjacency, edge.End, edge);
            }
            if (adjacency.Any(item => item.Value.Count > 2)) return null;
            List<ObjectId> endpoints = adjacency
                .Where(item => item.Value.Count == 1)
                .Select(item => item.Key)
                .ToList();
            if (endpoints.Count != 2) return null;
            var path = new SewerBranchPath();
            ObjectId current = endpoints[0];
            ObjectId previousEdge = ObjectId.Null;
            path.Nodes.Add(current);
            while (true)
            {
                SewerTopologyEdge next = adjacency[current]
                    .FirstOrDefault(edge => edge.Id != previousEdge);
                if (next == null) break;
                path.Edges.Add(next);
                previousEdge = next.Id;
                current = next.Other(current);
                path.Nodes.Add(current);
            }
            return path.Edges.Count == edges.Count ? path : null;
        }

        private static void AddEdge(
            IDictionary<ObjectId, List<SewerTopologyEdge>> adjacency,
            ObjectId node,
            SewerTopologyEdge edge)
        {
            List<SewerTopologyEdge> list;
            if (!adjacency.TryGetValue(node, out list))
            {
                list = new List<SewerTopologyEdge>();
                adjacency[node] = list;
            }
            list.Add(edge);
        }

        private static SewerBranchPath BuildDiameterPath(
            SewerTopology topology,
            ICollection<ObjectId> component)
        {
            List<ObjectId> candidates = component
                .Where(id => topology.Nodes[id].Edges.Count <= 1)
                .ToList();
            if (candidates.Count < 2) candidates = component.ToList();
            double bestDistance = double.MinValue;
            SewerBranchPath best = null;
            foreach (ObjectId start in candidates)
            {
                DijkstraResult paths = ShortestPaths(topology, start, component);
                foreach (ObjectId end in candidates)
                {
                    if (end == start || !paths.Distance.ContainsKey(end)) continue;
                    double distance = paths.Distance[end];
                    if (distance <= bestDistance) continue;
                    SewerBranchPath path = ReconstructPath(
                        topology,
                        start,
                        end,
                        paths);
                    if (path == null) continue;
                    bestDistance = distance;
                    best = path;
                }
            }
            return best;
        }

        private static DijkstraResult ShortestPaths(
            SewerTopology topology,
            ObjectId start,
            ICollection<ObjectId> component)
        {
            var allowed = new HashSet<ObjectId>(component);
            var result = new DijkstraResult();
            foreach (ObjectId id in allowed)
                result.Distance[id] = double.MaxValue;
            result.Distance[start] = 0.0;
            var remaining = new HashSet<ObjectId>(allowed);
            while (remaining.Count > 0)
            {
                ObjectId current = remaining
                    .OrderBy(id => result.Distance[id])
                    .ThenBy(id => id.Handle.Value)
                    .First();
                remaining.Remove(current);
                if (result.Distance[current] == double.MaxValue) break;
                foreach (SewerTopologyEdge edge in topology.Nodes[current].Edges)
                {
                    ObjectId next = edge.Other(current);
                    if (!allowed.Contains(next) || !remaining.Contains(next)) continue;
                    double candidate = result.Distance[current] + edge.Length;
                    if (candidate + 1e-8 >= result.Distance[next]) continue;
                    result.Distance[next] = candidate;
                    result.PreviousNode[next] = current;
                    result.PreviousEdge[next] = edge;
                }
            }
            return result;
        }

        private static SewerBranchPath ReconstructPath(
            SewerTopology topology,
            ObjectId start,
            ObjectId end,
            DijkstraResult paths)
        {
            var nodes = new List<ObjectId> { end };
            var edges = new List<SewerTopologyEdge>();
            ObjectId current = end;
            while (current != start)
            {
                ObjectId previous;
                SewerTopologyEdge edge;
                if (!paths.PreviousNode.TryGetValue(current, out previous) ||
                    !paths.PreviousEdge.TryGetValue(current, out edge))
                    return null;
                edges.Add(edge);
                current = previous;
                nodes.Add(current);
            }
            nodes.Reverse();
            edges.Reverse();
            return new SewerBranchPath(nodes, edges);
        }

        private static SewerBranchPath WalkBranchSegment(
            SewerTopology topology,
            ObjectId anchor,
            SewerTopologyEdge first,
            ISet<ObjectId> usedEdges,
            Queue<BranchSeed> queue)
        {
            var path = new SewerBranchPath();
            path.Nodes.Add(anchor);
            ObjectId current = anchor;
            SewerTopologyEdge edge = first;
            while (edge != null && !usedEdges.Contains(edge.Id))
            {
                usedEdges.Add(edge.Id);
                path.Edges.Add(edge);
                current = edge.Other(current);
                path.Nodes.Add(current);
                List<SewerTopologyEdge> available = topology.Nodes[current].Edges
                    .Where(candidate => !usedEdges.Contains(candidate.Id))
                    .OrderBy(candidate => candidate.Id.Handle.Value)
                    .ToList();
                if (available.Count == 1)
                {
                    edge = available[0];
                    continue;
                }
                foreach (SewerTopologyEdge child in available)
                    queue.Enqueue(new BranchSeed(current, child));
                break;
            }
            return path;
        }

        private static void OrientHighToLow(
            SewerBranchPath path,
            SewerTopology topology)
        {
            if (path == null || path.Nodes.Count < 2) return;
            SewerTopologyNode first = topology.Nodes[path.Nodes[0]];
            SewerTopologyNode last = topology.Nodes[path.Nodes[path.Nodes.Count - 1]];
            bool reverse = first.Rim < last.Rim - 1e-8 ||
                (Math.Abs(first.Rim - last.Rim) <= 1e-8 &&
                 first.Id.Handle.Value > last.Id.Handle.Value);
            if (!reverse) return;
            path.Nodes.Reverse();
            path.Edges.Reverse();
        }

        private static void ApplyBranches(
            CivilNetwork network,
            IList<SewerBranchPath> branches,
            Transaction transaction,
            SewerDynamicSequenceResult result)
        {
            var namedStructures = new HashSet<ObjectId>();
            for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)
            {
                int branchNumber = branchIndex + 1;
                SewerBranchPath branch = branches[branchIndex];
                string branchName = "Branch-" +
                    branchNumber.ToString(CultureInfo.InvariantCulture);
                for (int edgeIndex = 0; edgeIndex < branch.Edges.Count; edgeIndex++)
                {
                    CivilPipe pipe = transaction.GetObject(
                        branch.Edges[edgeIndex].Id,
                        OpenMode.ForWrite,
                        false) as CivilPipe;
                    if (pipe == null) continue;
                    pipe.Name = "P" +
                        branchNumber.ToString(CultureInfo.InvariantCulture) + "." +
                        (edgeIndex + 1).ToString(CultureInfo.InvariantCulture);
                    pipe.Description = branchName;
                    result.PipesRenumbered++;
                }

                int structureSequence = 1;
                for (int nodeIndex = 0; nodeIndex < branch.Nodes.Count; nodeIndex++)
                {
                    ObjectId structureId = branch.Nodes[nodeIndex];
                    if (namedStructures.Contains(structureId)) continue;
                    CivilStructure structure = transaction.GetObject(
                        structureId,
                        OpenMode.ForWrite,
                        false) as CivilStructure;
                    if (structure == null) continue;
                    structure.Name = "MH" +
                        branchNumber.ToString(CultureInfo.InvariantCulture) + "." +
                        structureSequence.ToString(CultureInfo.InvariantCulture);
                    structure.Description = branchName;
                    namedStructures.Add(structureId);
                    structureSequence++;
                    result.StructuresRenumbered++;
                }
            }
            network.Description = string.Format(
                CultureInfo.InvariantCulture,
                "CE dynamic sewer sequence: branches={0}; updated={1:yyyy-MM-dd HH:mm:ss}",
                branches.Count,
                DateTime.Now);
        }

        private static void RefreshGeneratedAlignments(
            Document document,
            CivilDocument civilDocument,
            IList<ObjectId> networkIds,
            SewerDynamicSequenceResult result)
        {
            try
            {
                Type commandType = typeof(SewerBranchAlignmentCommands);
                MethodInfo build = commandType.GetMethod(
                    "BuildNetworkPlan",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo create = commandType.GetMethod(
                    "CreateAlignmentsAndLabels",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (build == null || create == null) return;
                Type planType = build.ReturnType;
                Type listType = typeof(List<>).MakeGenericType(planType);
                IList plans = Activator.CreateInstance(listType) as IList;
                if (plans == null) return;

                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId networkId in networkIds)
                    {
                        object plan = build.Invoke(
                            null,
                            new object[] { networkId, transaction });
                        if (plan != null) plans.Add(plan);
                    }
                }
                if (plans.Count == 0) return;
                object[] arguments =
                {
                    document.Database,
                    civilDocument,
                    plans,
                    0,
                    0
                };
                create.Invoke(null, arguments);
                result.AlignmentsRefreshed += Convert.ToInt32(
                    arguments[3],
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                result.RefreshFailures++;
            }
        }

        private static ObjectId PromptNetwork(Document document)
        {
            var options = new PromptEntityOptions(
                "\nSelect a sewer pipe or manhole from the network to resequence: ");
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return ObjectId.Null;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForRead,
                    false);
                CivilPipe pipe = value as CivilPipe;
                if (pipe != null) return pipe.NetworkId;
                CivilStructure structure = value as CivilStructure;
                return structure == null ? ObjectId.Null : structure.NetworkId;
            }
        }

        private static void WriteResult(
            Editor editor,
            string command,
            SewerDynamicSequenceResult result)
        {
            editor.WriteMessage(
                "\n{0} complete. Networks={1}; branches={2}; pipes={3}; manholes={4}; alignments={5}; skipped networks={6}; refresh failures={7}.{8}",
                command,
                result.NetworksUpdated,
                result.BranchesUpdated,
                result.PipesRenumbered,
                result.StructuresRenumbered,
                result.AlignmentsRefreshed,
                result.SkippedNetworks,
                result.RefreshFailures,
                string.IsNullOrWhiteSpace(result.Warning)
                    ? string.Empty
                    : " " + result.Warning);
        }

        private sealed class BranchSeed
        {
            public BranchSeed(ObjectId anchor, SewerTopologyEdge edge)
            {
                Anchor = anchor;
                Edge = edge;
            }
            public ObjectId Anchor { get; private set; }
            public SewerTopologyEdge Edge { get; private set; }
        }

        private sealed class DijkstraResult
        {
            public DijkstraResult()
            {
                Distance = new Dictionary<ObjectId, double>();
                PreviousNode = new Dictionary<ObjectId, ObjectId>();
                PreviousEdge = new Dictionary<ObjectId, SewerTopologyEdge>();
            }
            public Dictionary<ObjectId, double> Distance { get; private set; }
            public Dictionary<ObjectId, ObjectId> PreviousNode { get; private set; }
            public Dictionary<ObjectId, SewerTopologyEdge> PreviousEdge { get; private set; }
        }
    }

    internal sealed class SewerDynamicSequenceResult
    {
        public int NetworksUpdated { get; set; }
        public int BranchesUpdated { get; set; }
        public int PipesRenumbered { get; set; }
        public int StructuresRenumbered { get; set; }
        public int AlignmentsRefreshed { get; set; }
        public int SkippedNetworks { get; set; }
        public int RefreshFailures { get; set; }
        public string Warning { get; set; }
    }

    internal sealed class SewerTopology
    {
        public SewerTopology(ObjectId networkId, string name)
        {
            NetworkId = networkId;
            Name = name ?? string.Empty;
            Nodes = new Dictionary<ObjectId, SewerTopologyNode>();
            Edges = new List<SewerTopologyEdge>();
        }
        public ObjectId NetworkId { get; private set; }
        public string Name { get; private set; }
        public Dictionary<ObjectId, SewerTopologyNode> Nodes { get; private set; }
        public List<SewerTopologyEdge> Edges { get; private set; }
    }

    internal sealed class SewerTopologyNode
    {
        public SewerTopologyNode(ObjectId id, double rim, Point3d position)
        {
            Id = id;
            Rim = rim;
            Position = position;
            Edges = new List<SewerTopologyEdge>();
        }
        public ObjectId Id { get; private set; }
        public double Rim { get; private set; }
        public Point3d Position { get; private set; }
        public List<SewerTopologyEdge> Edges { get; private set; }
    }

    internal sealed class SewerTopologyEdge
    {
        public SewerTopologyEdge(
            ObjectId id,
            ObjectId start,
            ObjectId end,
            double length,
            bool isBranchOne)
        {
            Id = id;
            Start = start;
            End = end;
            Length = length;
            IsBranchOne = isBranchOne;
        }
        public ObjectId Id { get; private set; }
        public ObjectId Start { get; private set; }
        public ObjectId End { get; private set; }
        public double Length { get; private set; }
        public bool IsBranchOne { get; private set; }
        public ObjectId Other(ObjectId node)
        {
            if (node == Start) return End;
            if (node == End) return Start;
            throw new InvalidOperationException(
                "A sewer edge was queried from a non-connected structure.");
        }
    }

    internal sealed class SewerBranchPath
    {
        public SewerBranchPath()
        {
            Nodes = new List<ObjectId>();
            Edges = new List<SewerTopologyEdge>();
        }
        public SewerBranchPath(
            IEnumerable<ObjectId> nodes,
            IEnumerable<SewerTopologyEdge> edges)
        {
            Nodes = nodes.ToList();
            Edges = edges.ToList();
        }
        public List<ObjectId> Nodes { get; private set; }
        public List<SewerTopologyEdge> Edges { get; private set; }
    }

    internal static class SewerNetworkDynamicSequenceManager
    {
        private static Database _database;
        private static bool _initialised;
        private static bool _busy;
        private static bool _pending;
        private static DateTime _lastChangeUtc = DateTime.MinValue;
        private static DateTime _lastRunUtc = DateTime.MinValue;

        public static bool Enabled { get; set; } = true;

        public static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            _initialised = false;
        }

        public static void Queue()
        {
            _pending = true;
            _lastChangeUtc = DateTime.UtcNow;
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (!Enabled || !_pending || _busy || document == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 1100.0)
                return;
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 900.0)
                return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            _busy = true;
            try
            {
                SewerNetworkDynamicSequenceCommands.ResequenceAll(
                    document,
                    false);
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;
            }
            catch
            {
                _pending = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private static void AttachDatabase(Database database)
        {
            if (ReferenceEquals(_database, database)) return;
            DetachDatabase();
            _database = database;
            if (_database == null) return;
            _database.ObjectModified += OnObjectChanged;
            _database.ObjectAppended += OnObjectChanged;
            _database.ObjectErased += OnObjectErased;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnObjectChanged;
                _database.ObjectAppended -= OnObjectChanged;
                _database.ObjectErased -= OnObjectErased;
            }
            _database = null;
        }

        private static void OnObjectErased(
            object sender,
            ObjectErasedEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            if (eventArgs.DBObject is CivilPipe ||
                eventArgs.DBObject is CivilStructure ||
                eventArgs.DBObject is CivilNetwork)
            {
                Queue();
            }
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            if (eventArgs.DBObject is CivilPipe ||
                eventArgs.DBObject is CivilStructure ||
                eventArgs.DBObject is CivilNetwork)
            {
                Queue();
            }
        }
    }
}
