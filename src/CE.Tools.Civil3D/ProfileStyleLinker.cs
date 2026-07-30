using System;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Re-applies the selected profile-view style and band-set after creation.
    /// Civil 3D 2023 exposes several ProfileView.Create overloads and some builds
    /// accept the IDs but retain the drawing defaults. This compatibility pass
    /// makes the requested project styles authoritative.
    /// </summary>
    internal static class ProfileStyleLinker
    {
        public static void Apply(
            DBObject profileView,
            ObjectId profileViewStyleId,
            ObjectId bandSetStyleId)
        {
            if (profileView == null) return;
            TrySetObjectId(profileView, "StyleId", profileViewStyleId);
            TrySetObjectId(profileView, "ProfileViewStyleId", profileViewStyleId);

            if (TrySetObjectId(profileView, "BandSetStyleId", bandSetStyleId) ||
                TrySetObjectId(profileView, "ProfileViewBandSetStyleId", bandSetStyleId))
            {
                return;
            }

            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) return;
            foreach (string methodName in new[]
            {
                "ImportBandSetStyle",
                "ApplyBandSetStyle",
                "SetBandSetStyle"
            })
            {
                MethodInfo method = bands.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ObjectId) },
                    null);
                if (method == null) continue;
                try
                {
                    method.Invoke(bands, new object[] { bandSetStyleId });
                    return;
                }
                catch
                {
                    // Try the next Civil 3D-version-compatible member.
                }
            }
        }

        private static bool TrySetObjectId(
            object target,
            string propertyName,
            ObjectId value)
        {
            if (target == null || value.IsNull) return false;
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null ||
                !property.CanWrite ||
                property.PropertyType != typeof(ObjectId))
            {
                return false;
            }
            try
            {
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object ReadProperty(object target, string propertyName)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead) return null;
            try
            {
                return property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }
    }
}
