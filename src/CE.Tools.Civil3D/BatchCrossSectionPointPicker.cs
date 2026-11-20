using System;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    internal static class BatchCrossSectionPointPicker
    {
        internal static bool TryPick(Editor editor, IntPtr owner, Action<string> trace, out Point3d point)
        {
            point = default(Point3d);
            if (owner == IntPtr.Zero)
                throw new InvalidOperationException("The settings window has no native handle.");

            trace("Point pick: enter modal editor interaction");
            try
            {
                using (EditorUserInteraction interaction = editor.StartUserInteraction(owner))
                {
                    trace("Point pick: GetPoint begin");
                    PromptPointResult result = editor.GetPoint(
                        "\nPick TOP-LEFT base point for cross-section layout: ");
                    trace("Point pick: GetPoint returned " + result.Status);
                    if (result.Status != PromptStatus.OK) return false;
                    trace("Point pick: convert UCS to WCS");
                    point = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                    return true;
                }
            }
            finally
            {
                trace("Point pick: interaction scope exited");
            }
        }
    }
}
