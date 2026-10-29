using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
namespace CETools.Civil3D
{
    internal static class BranchLabelRepeatPlan
    {
        internal static IList<double> Select(IList<double> pipeCentres, double start, double end,
            bool thresholdEnabled, double threshold, string frequency)
        {
            var centres = pipeCentres.Where(x => x >= start && x <= end).Distinct().OrderBy(x => x).ToList();
            if (end <= start) return new List<double>();
            if (centres.Count == 0) return new List<double> { (start + end) * 0.5 };
            if (thresholdEnabled && end - start <= threshold)
                return new List<double> { Nearest(centres, (start + end) * 0.5) };
            var result = new List<double>();
            if (frequency == "Consistent 50 m")
            {
                for (double at = start + Math.Min(25, (end - start) * 0.5); at < end; at += 50)
                    result.Add(Nearest(centres, at));
            }
            else
            {
                int stride = frequency == "Every second pipe" ? 2 : 1;
                for (int i = 0; i < centres.Count; i += stride) result.Add(centres[i]);
            }
            return result.Distinct().OrderBy(x => x).ToList();
        }
        private static double Nearest(IEnumerable<double> values, double target)
        { return values.OrderBy(x => Math.Abs(x - target)).ThenBy(x => x).First(); }
        internal static int BranchNumber(string name)
        {
            var match = Regex.Match(name ?? "", @"\bBRANCH[\s-]*(\d+)\b", RegexOptions.IgnoreCase);
            int number; return match.Success && int.TryParse(match.Groups[1].Value, out number) ? number : int.MaxValue;
        }
    }
}
