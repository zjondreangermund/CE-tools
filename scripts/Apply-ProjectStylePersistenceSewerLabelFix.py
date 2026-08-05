#!/usr/bin/env python3
"""Wire project-style presets and sewer-label synchronization into CE Tools."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PROJECT = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectStyleCenterCommands.cs"
LABELS = ROOT / "src" / "CE.Tools.Civil3D" / "SewerNetworkLabelCommands.cs"
SYNC = ROOT / "src" / "CE.Tools.Civil3D" / "SewerLabelStyleSyncCommands.cs"


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"Expected integration marker was not found in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    PLUGIN,
    "            FloatingToolsCommands.Initialize();\n            AcApplication.Idle += OnApplicationIdle;",
    "            FloatingToolsCommands.Initialize();\n"
    "            ProjectStylePresetManager.Initialize();\n"
    "            AcApplication.Idle += OnApplicationIdle;",
)
replace_once(
    PLUGIN,
    "            AcApplication.Idle -= OnApplicationIdle;\n            FloatingToolsCommands.Terminate();",
    "            AcApplication.Idle -= OnApplicationIdle;\n"
    "            ProjectStylePresetManager.Terminate();\n"
    "            FloatingToolsCommands.Terminate();",
)
replace_once(
    PROJECT,
    "                WriteSelection(document.Database, selection);\n                document.Editor.WriteMessage(",
    "                WriteSelection(document.Database, selection);\n"
    "                ProjectStylePresetManager.SaveFromDrawing(document);\n"
    "                document.Editor.WriteMessage(",
)
replace_once(
    LABELS,
    "            try\n            {\n                return EnsureLabelsCore(document, networkIds);\n            }",
    "            try\n            {\n"
    "                SewerNetworkLabelResult result = EnsureLabelsCore(\n"
    "                    document, networkIds);\n"
    "                SewerLabelStyleSyncCommands.ApplySelectedStyles(document);\n"
    "                return result;\n"
    "            }",
)
replace_once(
    SYNC,
    "                                if (isPipe) result.PipeLabelsUpdated++;\n                                else result.StructureLabelsUpdated++;",
    "                                if (isPipe)\n"
    "                                {\n"
    "                                    result.PipeLabelsUpdated++;\n"
    "                                    ApplyPipeLabelPresentation(value, transaction);\n"
    "                                }\n"
    "                                else result.StructureLabelsUpdated++;",
)

presentation_methods = r'''
        private static void ApplyPipeLabelPresentation(
            object label,
            Transaction transaction)
        {
            if (label == null || transaction == null) return;
            ObjectId featureId = ReadObjectIdProperty(
                label,
                "FeatureId", "PipeId", "ParentEntityId");
            if (featureId.IsNull || featureId.IsErased) return;

            CivilPipe pipe;
            try
            {
                pipe = transaction.GetObject(
                    featureId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
            }
            catch
            {
                return;
            }
            if (pipe == null) return;

            string description = string.IsNullOrWhiteSpace(pipe.Description)
                ? pipe.Name
                : pipe.Description;
            double length = ReadDoubleProperty(
                pipe,
                "Length2D", "Length3D", "Length");
            if (length <= 0.0)
            {
                try
                {
                    length = pipe.GetPointAtParam(0.0).DistanceTo(
                        pipe.GetPointAtParam(1.0));
                }
                catch { }
            }
            double slope = ReadDoubleProperty(pipe, "Slope");
            if (Math.Abs(slope) <= 1.0) slope *= 100.0;

            string contents = (description ?? string.Empty) +
                "\\P" + length.ToString("0.00", CultureInfo.CurrentCulture) +
                " m\\P@ " + slope.ToString("0.00", CultureInfo.CurrentCulture) + "%";
            List<ObjectId> components = ReadTextComponentIds(label);
            for (int index = 0; index < components.Count; index++)
                TrySetTextOverride(
                    label,
                    components[index],
                    index == 0 ? contents : string.Empty);
        }

        private static List<ObjectId> ReadTextComponentIds(object label)
        {
            var result = new List<ObjectId>();
            if (label == null) return result;
            foreach (string name in new[]
            {
                "GetTextComponentIds",
                "GetLabelTextComponentIds"
            })
            {
                try
                {
                    MethodInfo method = label.GetType().GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    System.Collections.IEnumerable values = method == null
                        ? null
                        : method.Invoke(label, null) as System.Collections.IEnumerable;
                    if (values == null) continue;
                    foreach (object value in values)
                    {
                        if (value is ObjectId) result.Add((ObjectId)value);
                    }
                    if (result.Count > 0) return result;
                }
                catch
                {
                    result.Clear();
                }
            }
            return result;
        }

        private static void TrySetTextOverride(
            object label,
            ObjectId componentId,
            string contents)
        {
            if (label == null || componentId.IsNull) return;
            foreach (string name in new[]
            {
                "SetTextComponentOverride",
                "SetLabelTextComponentOverride"
            })
            {
                foreach (MethodInfo method in label.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => string.Equals(
                        candidate.Name,
                        name,
                        StringComparison.Ordinal)))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 2 ||
                        parameters[0].ParameterType != typeof(ObjectId) ||
                        parameters[1].ParameterType != typeof(string))
                        continue;
                    var arguments = new object[parameters.Length];
                    arguments[0] = componentId;
                    arguments[1] = contents ?? string.Empty;
                    bool supported = true;
                    for (int index = 2; index < parameters.Length; index++)
                    {
                        Type type = parameters[index].ParameterType;
                        if (type.IsEnum)
                            arguments[index] = Enum.GetValues(type).GetValue(0);
                        else
                        {
                            supported = false;
                            break;
                        }
                    }
                    if (!supported) continue;
                    try
                    {
                        method.Invoke(label, arguments);
                        return;
                    }
                    catch
                    {
                        // Try the next compatible Civil 3D overload.
                    }
                }
            }
        }

        private static double ReadDoubleProperty(
            object value,
            params string[] names)
        {
            if (value == null) return 0.0;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    object current = property.GetValue(value, null);
                    if (current == null) continue;
                    return Convert.ToDouble(current, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // Try another property name.
                }
            }
            return 0.0;
        }

'''
text = SYNC.read_text(encoding="utf-8-sig")
marker = "        private static void ClearTextOverrides(object value)\n"
if "private static void ApplyPipeLabelPresentation(" not in text:
    if marker not in text:
        raise SystemExit("Pipe-label presentation insertion marker was not found")
    SYNC.write_text(text.replace(marker, presentation_methods + marker, 1), encoding="utf-8")

print("Applied project-style persistence and sewer-label synchronization wiring.")
