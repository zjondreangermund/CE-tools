using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class SewerGravityGradeTests
    {
        internal static int Run()
        {
            LowestOfThreeIncomingBranchesControlsTheEntireDownstreamRun();
            SelectionAndBranchOrderDoNotChangeTheGrade();
            UnselectedIncomingPipesControlPartialSelections();
            SeparateHeadwatersKeepTheirOwnCoverDatum();
            LaterConfluencesCanLowerTheGradeAgain();
            CyclesAndUnknownInflowsBlockDependantsOnly();
            RepeatingTheSolveDoesNotAccumulateAConnectionDrop();
            InvalidDataAndUnrepresentableGradesAreRejected();
            return 8;
        }

        private static List<SewerGravityPipe> Junction()
        {
            return new List<SewerGravityPipe>
            {
                Pipe("P1.2", "J", "K", 40, .0065, 900),
                Pipe("P1.3", "K", "outlet", 60, .005, 800),
                Pipe("P1.1", "head1", "J", 100, .0065, 110),
                Pipe("P2.1", "head2", "J", 50, .005, 108),
                Pipe("P9.4", "head9", "J", 20, .01, 105)
            };
        }

        private static void LowestOfThreeIncomingBranchesControlsTheEntireDownstreamRun()
        {
            SewerGravityPlan plan = SewerGravityGradeSolver.Solve(Junction(), null);
            Check(plan.UnresolvedPipeIds.Count == 0 && plan.Grades.Count == 5, "Resolve every selected branch.");
            var grades = plan.Grades.ToDictionary(g => g.Pipe.Id);
            Near(104.8, grades["P1.2"].UpstreamInvert);
            Near(104.54, grades["P1.2"].DownstreamInvert);
            Near(104.54, grades["P1.3"].UpstreamInvert);
            Near(104.24, grades["P1.3"].DownstreamInvert);
            Check(grades["P1.2"].IncomingCount == 3 && grades["P1.2"].ControllingIncomingPipe == "P9.4",
                "The lowest lateral branch must control, even when its branch number sorts last.");
            foreach (SewerGravityGrade grade in plan.Grades)
            {
                Near(grade.Pipe.Slope, (grade.UpstreamInvert - grade.DownstreamInvert) / grade.Pipe.Length);
                Check(grade.DownstreamInvert < grade.UpstreamInvert, "Every individual pipe must fall downstream.");
            }
        }

        private static void SelectionAndBranchOrderDoNotChangeTheGrade()
        {
            var baseline = SewerGravityGradeSolver.Solve(Junction(), null).Grades.ToDictionary(g => g.Pipe.Id);
            var random = new Random(173);
            for (int attempt = 0; attempt < 20; attempt++)
                foreach (SewerGravityGrade grade in SewerGravityGradeSolver.Solve(Junction().OrderBy(p => random.Next()), null).Grades)
                {
                    Near(baseline[grade.Pipe.Id].UpstreamInvert, grade.UpstreamInvert);
                    Near(baseline[grade.Pipe.Id].DownstreamInvert, grade.DownstreamInvert);
                }
        }

        private static void UnselectedIncomingPipesControlPartialSelections()
        {
            var fixedInlets = new[]
            {
                new SewerFixedInlet { PipeId = "outside-high", Node = "J", Invert = 2 },
                new SewerFixedInlet { PipeId = "outside-low", Node = "J", Invert = -.4 }
            };
            var outgoing = Pipe("selected-outlet", "J", "outlet", 100, .0065, 50);
            SewerGravityGrade grade = SewerGravityGradeSolver.Solve(new[] { outgoing }, fixedInlets).Grades.Single();
            Near(-.4, grade.UpstreamInvert);
            Near(-1.05, grade.DownstreamInvert);
            Check(grade.IncomingCount == 2 && grade.ControllingIncomingPipe == "outside-low", "Read all unselected inlets.");
            Near(-.4, fixedInlets[1].Invert);
            Near(50, outgoing.HeadwaterInvert);

            // An unselected inlet must also beat a selected, newly graded inlet.
            var selected = Junction();
            grade = SewerGravityGradeSolver.Solve(selected, fixedInlets).Grades.Single(g => g.Pipe.Id == "P1.2");
            Near(-.4, grade.UpstreamInvert);
            Check(grade.IncomingCount == 5, "Selected and fixed incoming pipes must both participate.");
        }

        private static void SeparateHeadwatersKeepTheirOwnCoverDatum()
        {
            var pipes = new[] { Pipe("a", "network1-head", "network1-outlet", 10, .01, 0),
                Pipe("b", "network2-head", "network2-outlet", 20, .005, 200) };
            var plan = SewerGravityGradeSolver.Solve(pipes, null);
            Near(-.1, plan.Grades.Single(g => g.Pipe.Id == "a").DownstreamInvert);
            Near(199.9, plan.Grades.Single(g => g.Pipe.Id == "b").DownstreamInvert);
        }

        private static void LaterConfluencesCanLowerTheGradeAgain()
        {
            var selected = Junction();
            selected.Add(Pipe("last-side-branch", "head3", "K", 50, .01, 100));
            var plan = SewerGravityGradeSolver.Solve(selected, null);
            var outlet = plan.Grades.Single(g => g.Pipe.Id == "P1.3");
            Near(99.5, outlet.UpstreamInvert);
            Near(99.2, outlet.DownstreamInvert);
            Check(outlet.IncomingCount == 2 && outlet.ControllingIncomingPipe == "last-side-branch",
                "Every manhole must consider its own lowest incoming pipe.");
        }

        private static void CyclesAndUnknownInflowsBlockDependantsOnly()
        {
            var pipes = new[] { Pipe("cycle-a", "a", "b", 10, .01, 100),
                Pipe("cycle-b", "b", "a", 10, .01, 100), Pipe("cycle-child", "b", "c", 10, .01, 100),
                Pipe("unknown", "unknown-inlet", "d", 10, .01, 100), Pipe("unknown-child", "d", "e", 10, .01, 100),
                Pipe("independent", "head", "outlet", 10, .01, 100) };
            var plan = SewerGravityGradeSolver.Solve(pipes, null, new[] { "unknown-inlet" });
            Check(plan.Grades.Count == 1 && plan.Grades[0].Pipe.Id == "independent", "Do not grade past an unresolved inflow.");
            Check(plan.UnresolvedPipeIds.Count == 5, "Report cycles, unknown inlets and all affected downstream pipes.");
        }

        private static void RepeatingTheSolveDoesNotAccumulateAConnectionDrop()
        {
            var pipes = Junction();
            var first = SewerGravityGradeSolver.Solve(pipes, null).Grades.ToDictionary(g => g.Pipe.Id);
            foreach (SewerGravityPipe pipe in pipes) pipe.HeadwaterInvert = first[pipe.Id].UpstreamInvert;
            foreach (SewerGravityGrade grade in SewerGravityGradeSolver.Solve(pipes, null).Grades)
                Near(first[grade.Pipe.Id].DownstreamInvert, grade.DownstreamInvert);
        }

        private static void InvalidDataAndUnrepresentableGradesAreRejected()
        {
            foreach (double length in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
                Throws(() => SewerGravityGradeSolver.Solve(new[] { Pipe("bad", "a", "b", length, .01, 100) }, null));
            foreach (double slope in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
                Throws(() => SewerGravityGradeSolver.Solve(new[] { Pipe("bad", "a", "b", 10, slope, 100) }, null));
            Throws(() => SewerGravityGradeSolver.Solve(new[] { Pipe("same", "a", "b", 10, .01, 100),
                Pipe("same", "b", "c", 10, .01, 100) }, null));
            Throws(() => SewerGravityGradeSolver.Solve(Junction(), new[] { new SewerFixedInlet { PipeId = "x", Node = "J", Invert = double.NaN } }));
            var plan = SewerGravityGradeSolver.Solve(new[] { Pipe("overflow", "a", "b", double.MaxValue, 2, 100),
                Pipe("downstream", "b", "c", 10, .01, 100) }, null);
            Check(plan.Grades.Count == 0 && plan.UnresolvedPipeIds.Count == 2, "Overflow must not propagate a fictitious incoming level.");
            Check(SewerGravityGradeSolver.Solve(new SewerGravityPipe[0], null).Grades.Count == 0, "An empty selection is safe.");
        }

        private static SewerGravityPipe Pipe(string id, string upstream, string downstream, double length, double slope, double invert)
            => new SewerGravityPipe { Id = id, UpstreamNode = upstream, DownstreamNode = downstream,
                Length = length, Slope = slope, HeadwaterInvert = invert };

        private static void Near(double expected, double actual)
            => Check(!double.IsNaN(actual) && Math.Abs(expected - actual) < 1e-9, $"Expected {expected}; got {actual}.");

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static void Throws(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Invalid gravity input was accepted.");
        }
    }
}
