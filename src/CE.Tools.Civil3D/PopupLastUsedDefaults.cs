using System;
using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;

namespace CETools.Civil3D
{
    /// <summary>User-local last-confirmed values; never writes drawing objects.</summary>
    internal static class PopupLastUsedDefaults
    {
        private sealed class UnsavedScope { internal readonly string Key = Guid.NewGuid().ToString("N"); }
        private static readonly ConditionalWeakTable<Document, UnsavedScope> UnsavedDrawings =
            new ConditionalWeakTable<Document, UnsavedScope>();

        internal static ProductionSettingsDialogModel Load(ProductionSettingsDialogModel model, Document document, bool preferSuppliedDefaults = false)
        {
            if (model == null || document == null) return null;
            try
            {
                string name = document.Name;
                string scope = !string.IsNullOrWhiteSpace(name) && Path.IsPathRooted(name)
                    ? Path.GetFullPath(name).ToUpperInvariant()
                    : "UNSAVED:" + UnsavedDrawings.GetValue(document, d => new UnsavedScope()).Key;
                var remembered = new ProductionSettingsDialogModel("CE_LAST_USED/" + scope + "/" + model.Title, string.Empty);
                // Share fields so the existing store's choice validation applies to the real dialog.
                foreach (ProductionSettingsField field in model.Fields) remembered.Fields.Add(field);
                if (!preferSuppliedDefaults) CrossDrawingProductionSettingsStore.Load(remembered);
                return remembered;
            }
            catch { return null; }
        }

        internal static bool Save(ProductionSettingsDialogModel remembered)
        {
            if (remembered == null) return true;
            try { CrossDrawingProductionSettingsStore.Save(remembered); return true; }
            catch { return false; }
        }
    }
}
