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
            // Keep the tracked source on the same final runtime as the Civil 3D 2023
            // staging boundary. This prevents an installer/build path that skips a
            // historical repair script from falling back to the older erase-and-
            // replace implementation.
            September04VerifiedJunctionBreakRuntime.BreakPolylinesAtJunctions(document);
        }
    }
}
