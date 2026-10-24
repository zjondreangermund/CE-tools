using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class AutoDesignTests
    {
        internal static int Run()
        {
            DependencyOrderAndNoOp();
            AnalysisIsReadOnly();
            ApprovalBlocksDependents();
            FailureRecovery();
            CancellationResumes();
            InterruptedCheckpointRetries();
            InvalidGraphsRejected();
            ModesAndScopesStayIsolated();
            InputChangesInvalidateDependents();
            ReentrancyRejected();
            PartialFailureDoesNotBlessOtherStages();
            PersistenceFailureStopsExecution();
            MissingProfilesDoNotBlockQuantities();
            MissingSourcesStillBlockQuantities();
            return 14;
        }
        private static AutoDesignStep Step(string id, Action action = null, params string[] dependencies)
        { return new AutoDesignStep { Id = id, Execute = action ?? (() => { }), Dependencies = dependencies }; }
        private static Dictionary<string, AutoDesignCheckpoint> Journal() { return new Dictionary<string, AutoDesignCheckpoint>(); }
        private static void Run(AutoDesignEngine engine, IList<AutoDesignItem> plan, IDictionary<string, AutoDesignCheckpoint> journal,
            Func<string> fingerprint = null, Func<bool> cancelled = null)
        { engine.Run(plan, AutoDesignMode.CompleteExistingDesign, journal, fingerprint ?? (() => "drawing"), () => { }, (x, i, n) => { }, cancelled ?? (() => false)); }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("AutoDesign: " + message); }
        private static void DependencyOrderAndNoOp()
        {
            var engine = new AutoDesignEngine(); var journal = Journal(); var log = new List<string>(); string hash = "input";
            var steps = new[] { Step("labels", () => { log.Add("labels"); hash = "output2"; }, "network"), Step("network", () => { log.Add("network"); hash = "output1"; }) };
            var plan = engine.Plan(steps, AutoDesignMode.CompleteExistingDesign, hash, journal);
            Run(engine, plan, journal, () => hash);
            Check(string.Join(",", log) == "network,labels", "topological execution");
            Check(engine.Plan(steps, AutoDesignMode.CompleteExistingDesign, hash, journal).All(x => x.Status == AutoDesignStatus.Current), "internal writes must not cause endless refresh");
        }
        private static void AnalysisIsReadOnly()
        {
            var e = new AutoDesignEngine(); var j = Journal(); int writes = 0;
            var p = e.Plan(new[] { Step("a", () => writes++) }, AutoDesignMode.AnalyseOnly, "x", j);
            e.Run(p, AutoDesignMode.AnalyseOnly, j, () => "x", () => writes++, (x, i, n) => writes++, () => false);
            Check(writes == 0 && j.Count == 0, "analysis must not run, persist, or modify");
        }
        private static void ApprovalBlocksDependents()
        {
            var a = Step("route"); a.ApprovalReason = "Approve outfall";
            var e = new AutoDesignEngine(); var j = Journal(); int calls = 0;
            var p = e.Plan(new[] { Step("network", () => calls++, "route"), a }, AutoDesignMode.FullAutoDesign, "x", j);
            Run(e, p, j);
            Check(calls == 0 && p[0].Status == AutoDesignStatus.NeedsApproval && p[1].Status == AutoDesignStatus.Blocked, "no implicit engineering approval");
        }
        private static void FailureRecovery()
        {
            bool fail = true; int calls = 0; var e = new AutoDesignEngine(); var j = Journal();
            var steps = new[] { Step("a", () => { if (fail) throw new InvalidOperationException("bad input"); }), Step("b", () => calls++, "a"), Step("independent", () => calls++) };
            var p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j); Run(e, p, j);
            Check(p[0].Status == AutoDesignStatus.Failed && p[1].Status == AutoDesignStatus.Blocked && calls == 1, "failure isolates dependent work");
            fail = false; p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j); Run(e, p, j);
            Check(p.All(x => x.Status == AutoDesignStatus.Current || x.Status == AutoDesignStatus.Completed), "resume after correction");
        }
        private static void CancellationResumes()
        {
            int calls = 0; var e = new AutoDesignEngine(); var j = Journal();
            var steps = new[] { Step("a", () => calls++), Step("b", () => calls++, "a") };
            var p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j); Run(e, p, j, cancelled: () => calls == 1);
            Check(calls == 1 && p[1].Status == AutoDesignStatus.Cancelled, "cancel between steps");
            p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j); Run(e, p, j);
            Check(calls == 2, "resume without duplicating completed work");
        }
        private static void InterruptedCheckpointRetries()
        {
            var e = new AutoDesignEngine(); var j = Journal(); j["a"] = new AutoDesignCheckpoint { Status = AutoDesignStatus.Running, Fingerprint = "drawing" };
            Check(e.Plan(new[] { Step("a") }, AutoDesignMode.CompleteExistingDesign, "drawing", j)[0].Status == AutoDesignStatus.Ready, "crash checkpoint retries");
        }
        private static void InvalidGraphsRejected()
        {
            var e = new AutoDesignEngine(); int errors = 0;
            foreach (var graph in new[] { new[] { Step("a", null, "b"), Step("b", null, "a") }, new[] { Step("a", null, "missing") }, new[] { Step("a"), Step("a") } })
                try { e.Plan(graph, AutoDesignMode.FullAutoDesign, "x", Journal()); } catch (ArgumentException) { errors++; }
            Check(errors == 3, "cycles, missing dependencies, duplicate IDs rejected");
        }
        private static void ModesAndScopesStayIsolated()
        {
            var e = new AutoDesignEngine(); var j = Journal();
            j["other-scope"] = new AutoDesignCheckpoint { Status = AutoDesignStatus.Completed, Fingerprint = "old" };
            var full = Step("create"); full.FullDesignOnly = true; full.ApprovalReason = "Approve criteria";
            var steps = new[] { Step("a"), full };
            var p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j); Run(e, p, j);
            Check(p.Count == 1 && j["other-scope"].Fingerprint == "old", "excluded work never rebased");
            Check(e.Plan(steps, AutoDesignMode.FullAutoDesign, "drawing", j).Last().Status == AutoDesignStatus.NeedsApproval, "full mode does not auto-approve");
        }
        private static void InputChangesInvalidateDependents()
        {
            var e = new AutoDesignEngine(); var j = Journal(); var steps = new[] { Step("a"), Step("b", null, "a") };
            Run(e, e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j), j);
            Check(e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "moved pipe", j).All(x => x.Status == AutoDesignStatus.Ready), "external changes invalidate affected plan");
        }
        private static void ReentrancyRejected()
        {
            var e = new AutoDesignEngine(); var j = Journal(); bool rejected = false; IList<AutoDesignItem> p = null;
            var step = Step("a", () => { try { Run(e, p, j); } catch (InvalidOperationException) { rejected = true; } });
            p = e.Plan(new[] { step }, AutoDesignMode.FullAutoDesign, "drawing", j); Run(e, p, j);
            Check(rejected, "nested execution rejected");
        }
        private static void PartialFailureDoesNotBlessOtherStages()
        {
            var e = new AutoDesignEngine(); var j = Journal(); string hash = "drawing";
            var steps = new[] { Step("a"), Step("failure", () => { hash = "partial output"; throw new InvalidOperationException(); }), Step("independent") };
            var p = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, hash, j); Run(e, p, j, () => hash);
            Check(j["a"].Fingerprint != hash, "partial writes cannot bless previously completed stage");
        }
        private static void PersistenceFailureStopsExecution()
        {
            var e = new AutoDesignEngine(); var j = Journal(); int calls = 0;
            var p = e.Plan(new[] { Step("a", () => calls++) }, AutoDesignMode.FullAutoDesign, "x", j);
            try { e.Run(p, AutoDesignMode.FullAutoDesign, j, () => "x", () => { throw new InvalidOperationException("cannot checkpoint"); }, (x, i, n) => { }, () => false); }
            catch (InvalidOperationException) { }
            Check(calls == 0, "never mutate if pre-step checkpoint cannot be written");
        }
        private static void MissingProfilesDoNotBlockQuantities()
        {
            var e = new AutoDesignEngine(); var j = Journal(); int outputCalls = 0, profileCalls = 0;
            var profiles = Step("profiles", () => profileCalls++, "sequence");
            profiles.ApprovalReason = AutoDesignWorkflowPolicy.ProfileApproval(true, 0);
            var steps = new[]
            {
                Step("sewer"), Step("sequence", null, "sewer"), Step("labels", null, "sequence"), profiles,
                Step("outputs", () => outputCalls++, AutoDesignWorkflowPolicy.OutputDependencies(true, false)),
                Step("validation", null, AutoDesignWorkflowPolicy.ValidationDependencies(true, false))
            };
            var plan = e.Plan(steps, AutoDesignMode.CompleteExistingDesign, "drawing", j);
            Check(plan.Single(x => x.Step.Id == "outputs").Status == AutoDesignStatus.Ready, "missing profiles must not block source-based quantities");
            Run(e, plan, j);
            Check(outputCalls == 1 && profileCalls == 0, "quantities run without invoking absent profile adapters");
            Check(plan.Single(x => x.Step.Id == "profiles").Status == AutoDesignStatus.NeedsApproval && !plan.Any(x => x.Status == AutoDesignStatus.Failed), "known absent views are review gates, not failed executions");
            Check(plan.Single(x => x.Step.Id == "validation").Status == AutoDesignStatus.Blocked, "missing profiles still prevent final sign-off");
            Check(AutoDesignWorkflowPolicy.ProfileApproval(true, 2) == null, "existing linked views permit refresh");
        }

        private static void MissingSourcesStillBlockQuantities()
        {
            var e = new AutoDesignEngine(); var j = Journal(); int calls = 0;
            var source = Step("sewer"); source.ApprovalReason = "Choose existing network";
            var plan = e.Plan(new[] { source, Step("outputs", () => calls++, AutoDesignWorkflowPolicy.OutputDependencies(true, false)) },
                AutoDesignMode.CompleteExistingDesign, "drawing", j);
            Run(e, plan, j);
            Check(calls == 0 && plan.Last().Status == AutoDesignStatus.Blocked, "unselected/unvalidated sources still block quantities");
            Check(AutoDesignWorkflowPolicy.ProfileApproval(false, 0).Contains("Select the existing"), "missing selection must not imply missing network geometry");
            Check(AutoDesignWorkflowPolicy.OutputDependencies(false, true).SequenceEqual(new[] { "road-sources" }), "road quantities require rebuilt sources, not display labels");
            Check(AutoDesignWorkflowPolicy.ValidationDependencies(false, true).Contains("road-labels"), "road labels remain a final validation prerequisite");
        }
    }
}
