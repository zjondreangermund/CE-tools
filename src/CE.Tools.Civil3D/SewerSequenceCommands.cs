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
    /// Traces one connected gravity-network path from a selected start structure
    /// to a selected end structure and applies the standard Branch/P/MH sequence.
    /// </summary>
    public sealed class SewerSequenceCommands
    {
        private static readonly Regex BranchPattern = new Regex(
            @"^Branch\s*-\s*(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWSEQ",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

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
                        editor.WriteMessage("\nBoth selected objects must be Civil 3D gravity-network structures.");
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
                        editor.WriteMessage("\nThe selected structures must belong to the same pipe network.");
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
                        editor.WriteMessage("\nNo connected path exists between the selected structures.");
                        return;
                    }

                    string branchName = GetOrCreateBranchName(
                        network,
                        CivilApplication.ActiveDocument,
                        transaction);

                    ValidateNameConflicts(network, path, transaction);
                    ApplyTemporaryNames(path, transaction);

                    SetCivilName(network, branchName);
                    network.Description = branchName;

                    for (int index = 0; index < path.StructureIds.Count; index++)
                    {
                        var structure = (CivilStructure)transaction.GetObject(
                            path.StructureIds[index],
                            OpenMode.ForWrite,
                            false);
                        SetCivilName(structure, "MH" + (index + 1).ToString(CultureInfo.InvariantCulture));
                        structure.Description = branchName;
                    }

                    for (int index = 0; index < path.PipeIds.Count; index++)
                    {
                        var pipe = (CivilPipe)transaction.GetObject(
                            path.PipeIds[index],
                            OpenMode.ForWrite,
                            false);
                        SetCivilName(pipe, "P" + (index + 1).ToString(CultureInfo.InvariantCulture));
                        pipe.Description = branchName;
                    }

                    transaction.Commit();

                    editor.WriteMessage(
                        $"\nCE_SEWSEQ complete. Network: {branchName}; " +
                        $"structures renamed: {path.StructureIds.Count}; " +
                        $"pipes renamed: {path.PipeIds.Count}.");
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWSEQ cancelled: " + exception.Message);
            }
        }

        private static PromptEntityResult PromptForStructure(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a Civil 3D gravity-network structure/manhole.");
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
                var pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                if (pipe == null || pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull)
                {
                    continue;
                }

                AddEdge(adjacency, pipe.StartStructureId, pipe.EndStructureId, pipeId);
                AddEdge(adjacency, pipe.EndStructureId, pipe.StartStructureId, pipeId);
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

                List<PathEdge> edges;
                if (!adjacency.TryGetValue(current, out edges))
                {
                    continue;
                }

                foreach (PathEdge edge in edges.OrderBy(item => item.PipeId.Handle.Value))
                {
                    if (!visited.Add(edge.OtherStructureId))
                    {
                        continue;
                    }

                    previous[edge.OtherStructureId] = new PreviousStep(current, edge.PipeId);
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

        private static void AddEdge(
            IDictionary<ObjectId, List<PathEdge>> adjacency,
            ObjectId from,
            ObjectId to,
            ObjectId pipeId)
        {
            List<PathEdge> edges;
            if (!adjacency.TryGetValue(from, out edges))
            {
                edges = new List<PathEdge>();
                adjacency[from] = edges;
            }

            edges.Add(new PathEdge(to, pipeId));
        }

        private static string GetOrCreateBranchName(
            CivilNetwork selectedNetwork,
            CivilDocument civilDocument,
            Transaction transaction)
        {
            Match currentMatch = BranchPattern.Match(selectedNetwork.Name ?? string.Empty);
            if (currentMatch.Success)
            {
                return "Branch-" + currentMatch.Groups[1].Value;
            }

            int maximumBranch = 0;
            foreach (ObjectId networkId in civilDocument.GetPipeNetworkIds())
            {
                var network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                if (network == null)
                {
                    continue;
                }

                Match match = BranchPattern.Match(network.Name ?? string.Empty);
                int branchNumber;
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out branchNumber))
                {
                    maximumBranch = Math.Max(maximumBranch, branchNumber);
                }
            }

            return "Branch-" + (maximumBranch + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static void ValidateNameConflicts(
            CivilNetwork network,
            PathResult path,
            Transaction transaction)
        {
            var selectedStructures = new HashSet<ObjectId>(path.StructureIds);
            var selectedPipes = new HashSet<ObjectId>(path.PipeIds);
            var targetStructureNames = new HashSet<string>(
                Enumerable.Range(1, path.StructureIds.Count)
                    .Select(number => "MH" + number.ToString(CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);
            var targetPipeNames = new HashSet<string>(
                Enumerable.Range(1, path.PipeIds.Count)
                    .Select(number => "P" + number.ToString(CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId structureId in network.GetStructureIds())
            {
                if (selectedStructures.Contains(structureId))
                {
                    continue;
                }

                var structure = transaction.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                if (structure != null && targetStructureNames.Contains(structure.Name))
                {
                    throw new InvalidOperationException(
                        $"Structure name '{structure.Name}' is already used outside the selected path.");
                }
            }

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                if (selectedPipes.Contains(pipeId))
                {
                    continue;
                }

                var pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                if (pipe != null && targetPipeNames.Contains(pipe.Name))
                {
                    throw new InvalidOperationException(
                        $"Pipe name '{pipe.Name}' is already used outside the selected path.");
                }
            }
        }

        private static void ApplyTemporaryNames(PathResult path, Transaction transaction)
        {
            string token = Guid.NewGuid().ToString("N");

            for (int index = 0; index < path.StructureIds.Count; index++)
            {
                var structure = (CivilStructure)transaction.GetObject(
                    path.StructureIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(structure, "CE_TMP_MH_" + token + "_" + index);
            }

            for (int index = 0; index < path.PipeIds.Count; index++)
            {
                var pipe = (CivilPipe)transaction.GetObject(
                    path.PipeIds[index],
                    OpenMode.ForWrite,
                    false);
                SetCivilName(pipe, "CE_TMP_P_" + token + "_" + index);
            }
        }

        private static void SetCivilName(CivilEntity entity, string name)
        {
            // Pipe and Structure expose Part.Name as read-only in some API views,
            // while the inherited Civil Entity name setter is the supported route
            // used here for network-part renaming.
            entity.Name = name;
        }

        private sealed class PathResult
        {
            public PathResult(IReadOnlyList<ObjectId> structureIds, IReadOnlyList<ObjectId> pipeIds)
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
            public PreviousStep(ObjectId previousStructureId, ObjectId pipeId)
            {
                PreviousStructureId = previousStructureId;
                PipeId = pipeId;
            }

            public ObjectId PreviousStructureId { get; }
            public ObjectId PipeId { get; }
        }
    }
}
