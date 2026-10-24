using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

static class Program
{
    static int checks;
    static void Check(bool value) { checks++; if (!value) throw new Exception("AutoDesign check " + checks + " failed"); }
    static void Main()
    {
        var nodes = new[] { "1", "2", "3" };
        var edges = new[] { new AutoDesignConnection { Id = "A", Start = "1", End = "2" }, new AutoDesignConnection { Id = "B", Start = "2", End = "3" } };
        Check(AutoDesignTopology.Validate(nodes, edges).Count == 0);
        Check(AutoDesignTopology.Validate(nodes, edges.Take(1)).Any(x => x.Contains("Disconnected")));
        Check(AutoDesignTopology.Validate(nodes, edges.Concat(new[] { new AutoDesignConnection { Id = "C", Start = "3", End = "1" } })).Any(x => x.Contains("loop")));
        Check(AutoDesignTopology.Validate(nodes, edges.Concat(new[] { edges[0] })).Any(x => x.Contains("duplicate")));
        Check(AutoDesignTopology.Validate(nodes, new[] { new AutoDesignConnection { Id = "C", Start = "1", End = "missing" } }).Any(x => x.Contains("Unconnected")));
        Check(AutoDesignTopology.Validate(nodes, new[] { new AutoDesignConnection { Id = "C", Start = "1", End = "1" } }).Any(x => x.Contains("Self-connected")));
        Check(AutoDesignTopology.Validate(new string[0], new AutoDesignConnection[0]).Count == 0);
        var engine = new AutoDesignEngine(); var state = new Dictionary<string, AutoDesignCheckpoint>(); int writes = 0;
        var source = new AutoDesignStep { Id = "source", Execute = () => writes++ };
        var plan = engine.Plan(new[] { source }, AutoDesignMode.AnalyseOnly, "drawing", state);
        engine.Run(plan, AutoDesignMode.AnalyseOnly, state, () => "drawing", () => writes++, (x,i,n) => writes++, () => false);
        Check(writes == 0 && state.Count == 0);
        plan = engine.Plan(new[] { source }, AutoDesignMode.CompleteExistingDesign, "drawing", state);
        engine.Run(plan, AutoDesignMode.CompleteExistingDesign, state, () => "drawing", () => { }, (x,i,n) => { }, () => false);
        Check(writes == 1 && engine.Plan(new[] { source }, AutoDesignMode.CompleteExistingDesign, "drawing", state)[0].Status == AutoDesignStatus.Current);
        Check(engine.Plan(new[] { source }, AutoDesignMode.CompleteExistingDesign, "edited", state)[0].Status == AutoDesignStatus.Ready);
        source.ApprovalReason = "Review outfall";
        Check(engine.Plan(new[] { source }, AutoDesignMode.FullAutoDesign, "drawing", state)[0].Status == AutoDesignStatus.NeedsApproval);
        Console.WriteLine("AutoDesign topology/integration checks passed: " + checks);
    }
}
