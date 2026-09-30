using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    public sealed class SewerGravityPipe
    {
        public string Id { get; set; }
        public string UpstreamNode { get; set; }
        public string DownstreamNode { get; set; }
        public double Length { get; set; }
        public double Slope { get; set; }
        public double HeadwaterInvert { get; set; }
    }

    public sealed class SewerFixedInlet
    {
        public string PipeId { get; set; }
        public string Node { get; set; }
        public double Invert { get; set; }
    }

    public sealed class SewerGravityGrade
    {
        public SewerGravityPipe Pipe { get; internal set; }
        public double UpstreamInvert { get; internal set; }
        public double DownstreamInvert { get; internal set; }
        public string ControllingIncomingPipe { get; internal set; }
        public int IncomingCount { get; internal set; }
    }

    public sealed class SewerGravityPlan
    {
        public IReadOnlyList<SewerGravityGrade> Grades { get; internal set; }
        public IReadOnlyList<string> UnresolvedPipeIds { get; internal set; }
    }

    /// <summary>
    /// Grades a directed network in invert space. All selected incoming pipes are
    /// resolved before an outgoing pipe, irrespective of branch/selection order.
    /// Unselected incoming pipes supply fixed boundary inverts. No sump or surface
    /// elevation may replace the lowest incoming invert at a connected manhole.
    /// </summary>
    public static class SewerGravityGradeSolver
    {
        public static SewerGravityPlan Solve(IEnumerable<SewerGravityPipe> source,
            IEnumerable<SewerFixedInlet> boundaries, IEnumerable<string> blockedNodes = null)
        {
            var pipes = (source ?? throw new ArgumentNullException(nameof(source))).ToList();
            var fixedInlets = (boundaries ?? Enumerable.Empty<SewerFixedInlet>()).ToList();
            if (pipes.Any(p => p == null || string.IsNullOrEmpty(p.Id) ||
                string.IsNullOrEmpty(p.UpstreamNode) || string.IsNullOrEmpty(p.DownstreamNode) ||
                !Finite(p.Length) || p.Length <= 0 || !Finite(p.Slope) || p.Slope <= 0 ||
                !Finite(p.HeadwaterInvert)) || pipes.Select(p => p.Id).Distinct().Count() != pipes.Count)
                throw new ArgumentException("Gravity pipes require unique IDs, nodes, positive lengths/slopes and finite inverts.");
            if (fixedInlets.Any(p => p == null || string.IsNullOrEmpty(p.Node) ||
                string.IsNullOrEmpty(p.PipeId) || !Finite(p.Invert)))
                throw new ArgumentException("Fixed incoming pipe inverts must be finite and identify a pipe and manhole.");

            var incoming = pipes.ToLookup(p => p.DownstreamNode);
            var outgoing = pipes.ToLookup(p => p.UpstreamNode);
            var fixedByNode = fixedInlets.ToLookup(p => p.Node);
            var blocked = new HashSet<string>(blockedNodes ?? Enumerable.Empty<string>());
            var remaining = pipes.ToDictionary(p => p.Id, p => incoming[p.UpstreamNode].Count());
            var ready = new Queue<SewerGravityPipe>(pipes.Where(p => remaining[p.Id] == 0 &&
                !blocked.Contains(p.UpstreamNode)).OrderBy(p => p.Id, StringComparer.Ordinal));
            var solved = new Dictionary<string, SewerGravityGrade>();
            var grades = new List<SewerGravityGrade>();

            while (ready.Count > 0)
            {
                SewerGravityPipe pipe = ready.Dequeue();
                var candidates = fixedByNode[pipe.UpstreamNode].Select(p =>
                    new SewerFixedInlet { PipeId = p.PipeId, Invert = p.Invert });
                candidates = candidates.Concat(incoming[pipe.UpstreamNode].Select(p =>
                    new SewerFixedInlet { PipeId = p.Id, Invert = solved[p.Id].DownstreamInvert }));
                var arrivals = candidates.OrderBy(p => p.Invert).ThenBy(p => p.PipeId, StringComparer.Ordinal).ToList();
                double invert = arrivals.Count > 0 ? arrivals[0].Invert : pipe.HeadwaterInvert;
                double downstream = invert - pipe.Slope * pipe.Length;
                if (!Finite(downstream) || downstream >= invert)
                    continue; // Invalid edge blocks its dependants; never reverse the grade.
                var grade = new SewerGravityGrade
                {
                    Pipe = pipe, UpstreamInvert = invert, DownstreamInvert = downstream,
                    ControllingIncomingPipe = arrivals.Count > 0 ? arrivals[0].PipeId : null,
                    IncomingCount = arrivals.Count
                };
                solved.Add(pipe.Id, grade);
                grades.Add(grade);
                foreach (SewerGravityPipe next in outgoing[pipe.DownstreamNode])
                    if (--remaining[next.Id] == 0 && !blocked.Contains(next.UpstreamNode))
                        ready.Enqueue(next);
            }

            return new SewerGravityPlan
            {
                Grades = grades,
                // Cycles, unknown inflows and everything depending on them remain
                // unchanged. They must never be silently treated as headwaters.
                UnresolvedPipeIds = pipes.Where(p => !solved.ContainsKey(p.Id)).Select(p => p.Id).ToList()
            };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
