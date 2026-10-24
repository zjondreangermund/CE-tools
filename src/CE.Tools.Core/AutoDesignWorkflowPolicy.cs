using System.Collections.Generic;

namespace CETools.Core
{
    public static class AutoDesignWorkflowPolicy
    {
        public static string[] OutputDependencies(bool sewer, bool roads)
        {
            var result = new List<string>();
            // Quantities require validated geometry/rebuilt sources, not optional display outputs.
            if (sewer) result.Add("sewer");
            if (roads) result.Add("road-sources");
            return result.ToArray();
        }

        public static string[] ValidationDependencies(bool sewer, bool roads)
        {
            var result = new List<string> { "outputs" };
            if (sewer) result.AddRange(new[] { "sequence", "labels", "profiles" });
            if (roads) result.Add("road-labels");
            return result.ToArray();
        }

        public static string ProfileApproval(bool sourceSelected, int linkedViews)
        {
            if (!sourceSelected) return "Select the existing Sewer network in AutoDesign options first. This does not mean the drawing has no network.";
            return linkedViews > 0 ? null : "No CE-linked native profile views were found for this Sewer network. Create/link them with CE_SEWPROFILE after approving styles, datum and placement. Drawn grids or blocks alone are not native profile views. Quantities can refresh independently.";
        }
    }
}
