using System;

namespace CETools.Civil3D
{
    // Independent of the Civil host so priority rules can be exercised in CI.
    internal static class WaterBranchPriority
    {
        internal static int Compare(double diameterA, double lengthA, double elevationA, string keyA,
            double diameterB, double lengthB, double elevationB, string keyB, string mode)
        {
            int result;
            if (mode == "Highest elevation first")
            {
                result = elevationB.CompareTo(elevationA);
                if (result != 0) return result;
            }
            if (mode == "Longer branches, then larger pipes")
            {
                result = lengthB.CompareTo(lengthA);
                if (result != 0) return result;
            }
            result = diameterB.CompareTo(diameterA);
            if (result != 0) return result;
            result = lengthB.CompareTo(lengthA);
            if (result != 0) return result;
            result = elevationB.CompareTo(elevationA);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(keyA, keyB);
        }

        internal static bool PreferCandidate(double distance, double bestDistance, bool hasBest)
        {
            // Candidates arrive in trunk priority order; retain that order at a junction.
            return distance <= bestDistance && (!hasBest || distance < bestDistance - 0.001);
        }
    }
}
