using Autodesk.AutoCAD.ApplicationServices;

namespace CETools.Civil3D
{
    internal static class August25CadSupplementaryBreakEngine
    {
        internal static void Run(Document document)
        {
            August21SafePolylineBreakEngine.Run(document);
        }
    }
}
