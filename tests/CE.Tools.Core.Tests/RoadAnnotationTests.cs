using System;
using System.Linq;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class RoadAnnotationTests
    {
        internal static int Run()
        {
            Check(RoadAnnotationPlan.RoadNumber("RD-02-CORRIDOR") == 2, "Road 2 corridor identity");
            Check(RoadAnnotationPlan.RoadNumber("ROAD 14") == 14, "Road 14 identity");
            Check(RoadAnnotationPlan.RoadNumber("CE-ROAD-JUNCTION-3") == 0, "Do not number from a junction layer");
            Check(RoadAnnotationPlan.RoadNumber("BROAD-2") == 0, "Do not match unrelated names");
            Check(RoadAnnotationPlan.SectionMidpoints(100, new[] { 70.0, 30, 30.000001, 0, 100 })
                .SequenceEqual(new[] { 15.0, 50, 85 }), "T/cross midpoints deduplicate and include end sections");
            Check(RoadAnnotationPlan.SectionMidpoints(80, new double[0]).Single() == 40, "Road without junctions");
            Check(RoadAnnotationPlan.SectionMidpoints(0, new[] { 1.0 }).Count == 0, "Zero-length road");
            var corners = new[] { Tuple.Create("SW", -12.0, -8.0), Tuple.Create("NE", 6.0, 11.0),
                Tuple.Create("NW", -10.0, 5.0), Tuple.Create("SE", 9.0, -7.0) };
            string order = string.Join(",", corners.OrderBy(c => RoadAnnotationPlan.ClockwiseFromTopLeft(c.Item2, c.Item3)).Select(c => c.Item1));
            Check(order == "NW,NE,SE,SW", "Skewed NW return must still be first, then clockwise");
            var numbers = new RoadPointSequence();
            string[] names = new[] { 2, 2, 4, 2, 2, 4 }.Select(road => numbers.Next("J", road)).ToArray();
            Check(names.SequenceEqual(new[] { "J2.1", "J2.2", "J4.1", "J2.3", "J2.4", "J4.2" }),
                "Multiple bellmouth sources share one sequence per actual road");
            Check(Math.Abs(RoadAnnotationPlan.ReadableAngle(Math.PI) - RoadAnnotationPlan.ReadableAngle(0)) < 1e-8,
                "Reversed roads retain readable text orientation");
            var continued = new RoadPointSequence(new System.Collections.Generic.Dictionary<int, int> { { 2, 17 } });
            Check(continued.Next("J", 2) == "J2.17" && continued.Next("J", 4) == "J4.1",
                "Retained Road 2 output does not collide with a partially replaced or continued group");
            return 11;
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Road annotation regression: " + name);
        }
    }
}
