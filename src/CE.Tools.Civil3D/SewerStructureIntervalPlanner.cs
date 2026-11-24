using System;
using System.Collections.Generic;
namespace CETools.Civil3D
{
    // Physical distances along alignment geometry, never displayed station equations.
    internal static class SewerStructureIntervalPlanner
    {
        // Add-only planning: existing endpoints are anchors, interval is a maximum.
        internal static double[] InfillStations(double start, double end, double maximum)
        {
            if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(end) ||
                double.IsInfinity(end) || double.IsNaN(maximum) || double.IsInfinity(maximum) ||
                maximum <= 0 || end <= start) throw new ArgumentOutOfRangeException("maximum");
            double count = Math.Max(1, Math.Ceiling((end - start) / maximum));
            if (double.IsInfinity(count) || count > 10000)
                throw new InvalidOperationException("More than 10,000 intervals requested in one pipe.");
            var stations = new double[(int)count + 1];
            for (int i = 0; i < stations.Length; i++) stations[i] = start + (end - start) * i / count;
            stations[0] = start; stations[stations.Length - 1] = end;
            return stations;
        }

        internal static double[] Stations(double start, double end, double minimum)
        {
            if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(end) ||
                double.IsInfinity(end) || double.IsNaN(minimum) || double.IsInfinity(minimum) ||
                minimum <= 0 || end <= start) throw new ArgumentOutOfRangeException("minimum");
            double count = Math.Max(1, Math.Floor((end - start) / minimum));
            if (count > 10000) throw new InvalidOperationException("More than 10,000 intervals requested in one span.");
            var stations = new double[(int)count + 1];
            for (int i = 0; i < stations.Length; i++) stations[i] = start + (end - start) * i / count;
            stations[0] = start; stations[stations.Length - 1] = end;
            return stations;
        }
    }
}
