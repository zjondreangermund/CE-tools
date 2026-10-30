using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class NameAnnotationScaleOrientation
    {
        // Call after final placement, height, rotation and database registration.
        // Adding a context captures those properties; existing contexts must also
        // be reset, because assigning Rotation alone updates the active scale.
        internal static void Synchronize(MText text, IEnumerable<object> scales)
        {
            var location = text.Location;
            double rotation = text.Rotation;
            text.SetPaperOrientation(false);
            text.Location = location;
            text.Rotation = rotation;
            AnnotationScaleSyncManager.AddUsedDrawingScaleContexts(text, scales);
            text.Location = location;
            text.Rotation = rotation;
            text.ResetScaleDependentProperties();
            text.RecordGraphicsModified(true);
        }
    }
}
