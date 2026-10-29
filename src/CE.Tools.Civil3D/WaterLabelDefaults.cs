using System;
using Autodesk.AutoCAD.DatabaseServices;
namespace CETools.Civil3D
{
    internal static class WaterLabelDefaults
    {
        internal static string Read(Database database, string key, string fallback)
        {
            var preset = August11DisciplineStylePresetManager.ReadPreset(database, "Water");
            if (!preset.Exists) preset = CeGlobalDisciplineStyleDefaults.Read("Water");
            string value;
            return preset.Exists && preset.Values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) &&
                value != CivilStyleCatalogV2.DrawingDefault ? value : fallback;
        }
    }
}
