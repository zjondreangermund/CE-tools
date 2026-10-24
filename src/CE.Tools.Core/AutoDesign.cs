using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    public enum AutoDesignMode { AnalyseOnly, CompleteExistingDesign, FullAutoDesign }
    public enum AutoDesignStatus { Current, Ready, NeedsApproval, Blocked, Running, Completed, Failed, Cancelled }

    /// <summary>A headless adapter is responsible for idempotence and reporting partial failures.</summary>
    public sealed class AutoDesignStep
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public string[] Dependencies { get; set; } = new string[0];
        public string ApprovalReason { get; set; }
        public bool FullDesignOnly { get; set; }
        public Action Execute { get; set; }
    }

    public sealed class AutoDesignCheckpoint
    {
        public string Fingerprint { get; set; }
        public AutoDesignStatus Status { get; set; }
        public string Message { get; set; }
    }

    public sealed class AutoDesignItem
    {
        public AutoDesignStep Step { get; internal set; }
        public AutoDesignStatus Status { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>Finite dependency runner. No event subscription or automatic retry can feed a refresh loop.</summary>
    public sealed class AutoDesignEngine
    {
        private bool running;

        public IList<AutoDesignItem> Plan(IEnumerable<AutoDesignStep> steps, AutoDesignMode mode,
            string fingerprint, IDictionary<string, AutoDesignCheckpoint> checkpoints)
        {
            var ordered = Order(steps);
            var result = new List<AutoDesignItem>();
            foreach (var step in ordered)
            {
                if (step.FullDesignOnly && mode != AutoDesignMode.FullAutoDesign) continue;
                AutoDesignCheckpoint saved;
                bool current = checkpoints.TryGetValue(step.Id, out saved) &&
                    saved.Status == AutoDesignStatus.Completed && saved.Fingerprint == fingerprint;
                var item = new AutoDesignItem { Step = step };
                if (!string.IsNullOrWhiteSpace(step.ApprovalReason))
                { item.Status = AutoDesignStatus.NeedsApproval; item.Message = step.ApprovalReason; }
                else if (step.Dependencies.Any(id => !result.Any(x => x.Step.Id == id &&
                    (x.Status == AutoDesignStatus.Current || x.Status == AutoDesignStatus.Ready))))
                { item.Status = AutoDesignStatus.Blocked; item.Message = "A prerequisite needs approval or is unavailable."; }
                else if (current && !step.Dependencies.Any(id => result.Any(x => x.Step.Id == id && x.Status == AutoDesignStatus.Ready)))
                { item.Status = AutoDesignStatus.Current; item.Message = "Unchanged since successful completion."; }
                else
                { item.Status = AutoDesignStatus.Ready; item.Message = "Missing checkpoint or drawing/settings changed."; }
                result.Add(item);
            }
            return result;
        }

        public void Run(IList<AutoDesignItem> plan, AutoDesignMode mode,
            IDictionary<string, AutoDesignCheckpoint> checkpoints, Func<string> fingerprint,
            Action persist, Action<AutoDesignItem, int, int> progress, Func<bool> cancelled)
        {
            if (running) throw new InvalidOperationException("AutoDesign is already running.");
            if (mode == AutoDesignMode.AnalyseOnly) return; // Strictly read-only, including checkpoints.
            running = true;
            bool partialFailure = false;
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    var item = plan[i];
                    if (item.Status != AutoDesignStatus.Ready) continue;
                    if (cancelled()) { item.Status = AutoDesignStatus.Cancelled; break; }
                    if (item.Step.Dependencies.Any(id => !plan.Any(x => x.Step.Id == id &&
                        (x.Status == AutoDesignStatus.Completed || x.Status == AutoDesignStatus.Current))))
                    { item.Status = AutoDesignStatus.Blocked; item.Message = "Prerequisite did not finish successfully."; continue; }
                    item.Status = AutoDesignStatus.Running;
                    checkpoints[item.Step.Id] = new AutoDesignCheckpoint { Status = item.Status };
                    persist(); // A crash leaves a Running checkpoint, retried on the next invocation.
                    progress(item, i + 1, plan.Count);
                    try
                    {
                        if (item.Step.Execute == null) throw new InvalidOperationException("No headless adapter is registered.");
                        item.Step.Execute();
                        item.Status = AutoDesignStatus.Completed;
                        item.Message = "Completed; final engineering review still required.";
                    }
                    catch (OperationCanceledException)
                    { item.Status = AutoDesignStatus.Cancelled; item.Message = "Interrupted; rerun to resume."; }
                    catch (Exception ex)
                    { item.Status = AutoDesignStatus.Failed; item.Message = ex.Message; }
                    checkpoints[item.Step.Id] = new AutoDesignCheckpoint { Status = item.Status, Message = item.Message };
                    // Rebase only successful stages from THIS plan after internal output changes.
                    // Never bless an old checkpoint from a different mode/scope or a failed stage.
                    string after = fingerprint();
                    if (item.Status == AutoDesignStatus.Failed || item.Status == AutoDesignStatus.Cancelled)
                    {
                        // Adapters may commit partial work before throwing. Earlier stages must
                        // be reconsidered rather than treating that partial mutation as current.
                        partialFailure = true;
                        foreach (var checkpoint in checkpoints.Values) checkpoint.Fingerprint = null;
                    }
                    foreach (var completed in plan.Where(x => x.Status == AutoDesignStatus.Completed || x.Status == AutoDesignStatus.Current))
                    {
                        if (item.Status == AutoDesignStatus.Completed && (!partialFailure || completed == item) && checkpoints.ContainsKey(completed.Step.Id)) checkpoints[completed.Step.Id].Fingerprint = after;
                    }
                    persist();
                    progress(item, i + 1, plan.Count);
                    if (item.Status == AutoDesignStatus.Cancelled) break;
                }
            }
            finally { running = false; }
        }

        private static IList<AutoDesignStep> Order(IEnumerable<AutoDesignStep> source)
        {
            var steps = source.ToList();
            if (steps.Any(x => string.IsNullOrWhiteSpace(x.Id)) || steps.Select(x => x.Id).Distinct().Count() != steps.Count)
                throw new ArgumentException("Step identifiers must be nonempty and unique.");
            var byId = steps.ToDictionary(x => x.Id);
            var ordered = new List<AutoDesignStep>();
            var visiting = new HashSet<string>();
            var visited = new HashSet<string>();
            Action<AutoDesignStep> visit = null;
            visit = step =>
            {
                if (visited.Contains(step.Id)) return;
                if (!visiting.Add(step.Id)) throw new ArgumentException("AutoDesign dependency cycle: " + step.Id);
                foreach (string id in step.Dependencies)
                {
                    if (!byId.ContainsKey(id)) throw new ArgumentException("Unknown dependency: " + id);
                    visit(byId[id]);
                }
                visiting.Remove(step.Id); visited.Add(step.Id); ordered.Add(step);
            };
            foreach (var step in steps) visit(step);
            return ordered;
        }
    }
}
