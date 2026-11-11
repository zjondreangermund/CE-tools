using System;
using CETools.Civil3D;
namespace CETools.Core.Tests
{
    internal static class SewerStructureIntervalTests
    {
        internal static int Run()
        {
            Check(0, 242.88, 60, new[] { 0.0, 60.72, 121.44, 182.16, 242.88 });
            Check(100, 342.88, 60, new[] { 100.0, 160.72, 221.44, 282.16, 342.88 });
            Check(0, 79.93, 60, new[] { 0.0, 79.93 });
            Check(0, 120, 60, new[] { 0.0, 60.0, 120.0 });
            Check(0, 30, 60, new[] { 0.0, 30.0 }); // fixed anchors cannot be moved
            Check(0, 119.999, 60, new[] { 0.0, 119.999 }); // minimum, not maximum
            Check(-100, 20, 60, new[] { -100.0, -40.0, 20.0 });
            foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, 0.0000001 })
            {
                bool rejected = false;
                try { SewerStructureIntervalPlanner.Stations(0, 242.88, invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Invalid interval accepted.");
            }
            return 12;
        }
        private static void Check(double start, double end, double minimum, double[] expected)
        {
            var actual = SewerStructureIntervalPlanner.Stations(start, end, minimum);
            if (actual.Length != expected.Length) throw new Exception("Wrong structure count.");
            for (int i = 0; i < actual.Length; i++)
                if (Math.Abs(actual[i] - expected[i]) > 1e-8) throw new Exception("Wrong alignment distance.");
        }
    }
}
