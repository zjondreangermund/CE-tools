using Autodesk.Civil.DatabaseServices.Styles;

namespace CETools.Civil3D
{
    internal static class CivilStyleNames
    {
        internal static string Get(StyleBase style)
        {
            // StyleBase overrides only the Name setter. Reflection on the
            // derived property cannot read it. Use the inherited Civil DBObject
            // getter explicitly (AeccDbMgd 13.x).
            return style == null ? string.Empty :
                ((Autodesk.Civil.DatabaseServices.DBObject)style).Name;
        }
    }
}
