using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    internal sealed class August25BreakPlan
    {
        internal ObjectId SourceId;
        internal readonly List<double> Distances = new List<double>();
    }

    internal static class August25CadSupplementaryBreakEngine
    {
        internal static void Run(Document document)
        {
            // Use the verified deterministic T/X splitter directly in current
            // source as well as in the build finalizer. The older native
            // GetSplitCurves route can throw eDegenerateGeometry on coincident,
            // near-endpoint or tightly clustered junctions. The verified runtime
            // plans junction stations in XY first and only commits validated spans.
            September04VerifiedJunctionBreakRuntime.BreakPolylinesAtJunctions(document);
        }
    }
}
