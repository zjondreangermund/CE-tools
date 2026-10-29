using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CETools.Civil3D
{
    internal sealed class CorridorRegionSnapshot
    {
        internal string CorridorHandle, CorridorName = "", BaselineName = "", RegionName = "";
        internal int Baseline, Region;
        internal double Start, End;
        internal bool Enabled;
        internal bool? BaselineEnabled;
        internal string Encode()
        {
            return string.Join("|", CorridorHandle, Baseline.ToString(CultureInfo.InvariantCulture), Region.ToString(CultureInfo.InvariantCulture),
                Start.ToString("R", CultureInfo.InvariantCulture), End.ToString("R", CultureInfo.InvariantCulture), Enabled ? "1" : "0",
                BaselineEnabled.HasValue ? (BaselineEnabled.Value ? "1" : "0") : "?",
                Uri.EscapeDataString(CorridorName), Uri.EscapeDataString(BaselineName), Uri.EscapeDataString(RegionName));
        }
        internal static CorridorRegionSnapshot Parse(string line)
        {
            var p = (line ?? "").Split('|'); int b, r; double start, end;
            if ((p.Length != 6 && p.Length != 7 && p.Length != 10) ||
                !int.TryParse(p[1], out b) || !int.TryParse(p[2], out r) ||
                !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out start) ||
                !double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out end)) return null;
            return new CorridorRegionSnapshot { CorridorHandle = p[0], Baseline = b, Region = r, Start = start, End = end,
                Enabled = p[5] == "1", BaselineEnabled = p.Length >= 7 && p[6] != "?" ? (bool?)(p[6] == "1") : null,
                CorridorName = p.Length == 10 ? Uri.UnescapeDataString(p[7]) : "",
                BaselineName = p.Length == 10 ? Uri.UnescapeDataString(p[8]) : "",
                RegionName = p.Length == 10 ? Uri.UnescapeDataString(p[9]) : "" };
        }
        private static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool Range(CorridorRegionSnapshot a, CorridorRegionSnapshot b)
        { return Math.Abs(a.Start - b.Start) < 0.001 && Math.Abs(a.End - b.End) < 0.001; }
        private static string Signature(IEnumerable<CorridorRegionSnapshot> states)
        {
            return string.Join(";", states.OrderBy(s => s.Baseline).ThenBy(s => s.Start).ThenBy(s => s.End)
                .Select(s => s.Baseline + ":" + Math.Round(s.Start, 3).ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(s.End, 3).ToString(CultureInfo.InvariantCulture)));
        }
        internal static List<CorridorRegionSnapshot> Find(CorridorRegionSnapshot current,
            IList<CorridorRegionSnapshot> saved, IList<CorridorRegionSnapshot> live)
        {
            var corridor = saved.Where(s => Same(s.CorridorHandle, current.CorridorHandle)).ToList();
            if (corridor.Count == 0 && current.CorridorName.Length > 0)
                corridor = saved.Where(s => s.CorridorName.Length > 0 && Same(s.CorridorName, current.CorridorName)).ToList();
            if (corridor.Count == 0)
            {
                // Old snapshots contain handles only. Recover a recreated corridor only
                // when its complete region signature is unique on BOTH sides.
                string signature = Signature(live.Where(s => Same(s.CorridorHandle, current.CorridorHandle)));
                var candidates = saved.GroupBy(s => s.CorridorHandle).Where(g => Signature(g) == signature).ToList();
                int liveCandidates = live.GroupBy(s => s.CorridorHandle).Count(g => Signature(g) == signature);
                if (candidates.Count != 1 || liveCandidates != 1) return new List<CorridorRegionSnapshot>();
                corridor = candidates[0].ToList();
            }
            var baseline = corridor.Where(s => s.BaselineName.Length > 0 && current.BaselineName.Length > 0 ?
                Same(s.BaselineName, current.BaselineName) : s.Baseline == current.Baseline).ToList();
            var named = baseline.Where(s => s.RegionName.Length > 0 && Same(s.RegionName, current.RegionName)).ToList();
            var exact = baseline.Where(s => Range(s, current)).ToList();
            if (exact.Count == 1) return exact;
            // Split/merged regions may inherit a state only where saved intervals cover
            // the full new range continuously and agree on both enabled flags.
            var overlaps = baseline.Where(s => s.End > current.Start + 0.0001 && s.Start < current.End - 0.0001).OrderBy(s => s.Start).ToList();
            if (overlaps.Count == 0 && named.Count == 1) return named;
            if (overlaps.Count == 0 || overlaps.Select(s => s.Enabled).Distinct().Count() != 1 ||
                overlaps.Select(s => s.BaselineEnabled).Distinct().Count() != 1) return new List<CorridorRegionSnapshot>();
            double covered = current.Start;
            foreach (var state in overlaps)
            {
                if (state.Start > covered + 0.001) return new List<CorridorRegionSnapshot>();
                covered = Math.Max(covered, state.End);
            }
            return covered >= current.End - 0.001 ? overlaps : new List<CorridorRegionSnapshot>();
        }
    }
}
