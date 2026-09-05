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
            // Keep tracked source on the same final route as the Civil 3D 2023
            // September 05 staging boundary. The native split runtime detects T/X
            // junctions in plan and keeps each selected source object/handle.
            September04FieldGeometryCompletionCommands.BreakAtJunctions(document);
        }
    }
}
