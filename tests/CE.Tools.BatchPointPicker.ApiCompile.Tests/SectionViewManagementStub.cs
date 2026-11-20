using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace CETools.Civil3D
{
    // Only the unrelated downstream layout implementation is stubbed. The batch
    // command, WPF handoff and picker compile against Autodesk's 2024 references.
    internal static class SectionViewManagementCommands
    {
        internal static HashSet<ObjectId> CaptureViewIds(Document doc) { return null; }
        internal static int ApplyCreatedSectionViewStyles(Document doc, HashSet<ObjectId> oldViews,
            CivilDocument civilDoc, string style, string bands) { return 0; }
        internal static int ArrangeGeneratedByRoad(Document doc, HashSet<ObjectId> oldViews,
            Point3d point, int sectionsPerRow, int groupsPerRow, double gap, double rowSpacing,
            double columnSpacing, bool titles) { return 0; }
    }
}
