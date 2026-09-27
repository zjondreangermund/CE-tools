using System;
using Autodesk.Civil.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class ProfileViewBandPersistence
    {
        internal static void Update(
            ProfileView view,
            Action<ProfileViewBandItem> edit,
            Action<ProfileViewBandItem> verify)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.IsWriteEnabled)
                throw new InvalidOperationException("Open the profile view for write before editing its bands.");

            // GetTop/BottomBandItems returns an editable copy. A successful
            // property read on that copy says nothing about the drawing.
            // Persist each location, dispose it, then verify a fresh collection.
            ProfileViewBandSet bands = view.Bands;
            using (ProfileViewBandItemCollection top = bands.GetTopBandItems())
            {
                for (int index = 0; index < top.Count; index++) edit(top[index]);
                if (top.Count > 0) bands.SetTopBandItems(top);
            }
            using (ProfileViewBandItemCollection bottom = bands.GetBottomBandItems())
            {
                for (int index = 0; index < bottom.Count; index++) edit(bottom[index]);
                if (bottom.Count > 0) bands.SetBottomBandItems(bottom);
            }
            using (ProfileViewBandItemCollection top = bands.GetTopBandItems())
                for (int index = 0; index < top.Count; index++) verify(top[index]);
            using (ProfileViewBandItemCollection bottom = bands.GetBottomBandItems())
                for (int index = 0; index < bottom.Count; index++) verify(bottom[index]);
        }

        internal static void EnableLabels(ProfileView view, out int found, out int enabled)
        {
            int savedFound = 0;
            int savedEnabled = 0;
            Update(view, item => item.ShowLabels = true, item =>
            {
                savedFound++;
                if (item.ShowLabels) savedEnabled++;
            });
            found = savedFound;
            enabled = savedEnabled;
        }
    }
}
