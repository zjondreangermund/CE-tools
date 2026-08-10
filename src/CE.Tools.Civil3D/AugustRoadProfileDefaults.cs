using System;

namespace CETools.Civil3D
{
    internal static class AugustRoadProfileDefaults
    {
        internal const string DefaultBandSet = "Road-Single-Band Set 1-Full Grid";

        internal static string PreferredBandSet(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured) || configured.StartsWith("<", StringComparison.Ordinal))
                return DefaultBandSet;
            return configured;
        }
    }
}
