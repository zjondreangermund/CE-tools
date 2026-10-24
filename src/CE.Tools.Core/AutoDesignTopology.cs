using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    public sealed class AutoDesignConnection
    {
        public string Id { get; set; }
        public string Start { get; set; }
        public string End { get; set; }
    }

    public static class AutoDesignTopology
    {
        /// <summary>Conservative gravity-network checks, without choosing an outfall or changing grades.</summary>
        public static IList<string> Validate(IEnumerable<string> structureIds, IEnumerable<AutoDesignConnection> connections)
        {
            var nodes = structureIds.ToDictionary(x => x, x => new HashSet<string>(), StringComparer.Ordinal);
            var findings = new List<string>();
            var edges = new HashSet<string>();
            int validEdges = 0;
            foreach (var pipe in connections)
            {
                if (pipe.Start == null || pipe.End == null || !nodes.ContainsKey(pipe.Start) || !nodes.ContainsKey(pipe.End))
                { findings.Add("Unconnected/cross-network pipe " + pipe.Id); continue; }
                if (pipe.Start == pipe.End) { findings.Add("Self-connected pipe " + pipe.Id); continue; }
                string key = string.CompareOrdinal(pipe.Start, pipe.End) < 0 ? pipe.Start + ":" + pipe.End : pipe.End + ":" + pipe.Start;
                if (!edges.Add(key)) { findings.Add("Parallel/duplicate connection " + pipe.Id + " requires review."); continue; }
                validEdges++;
                nodes[pipe.Start].Add(pipe.End); nodes[pipe.End].Add(pipe.Start);
            }
            foreach (var node in nodes.Where(x => x.Value.Count == 0)) findings.Add("Isolated structure " + node.Key);
            var visited = new HashSet<string>();
            int components = 0;
            foreach (var node in nodes.Keys)
            {
                if (!visited.Add(node)) continue;
                components++;
                var queue = new Queue<string>(); queue.Enqueue(node);
                while (queue.Count > 0)
                    foreach (var next in nodes[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
            }
            if (components > 1) findings.Add("Disconnected network components: " + components + ". Confirm the intended separate networks.");
            if (validEdges > nodes.Count - components) findings.Add("Network contains a loop; approve the topology before gravity design.");
            return findings;
        }
    }
}
