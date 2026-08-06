using System;
using Autodesk.AutoCAD.EditorInput;

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps the production-setting call sites concise while routing numeric
    /// values through the shared positive-double field supported by CE Tools.
    /// </summary>
    internal static class ProductionSettingsDialogModelCompatibility
    {
        public static void AddDouble(
            this ProductionSettingsDialogModel model,
            string key,
            string group,
            string label,
            double value,
            string description)
        {
            if (model == null) throw new ArgumentNullException("model");
            model.AddPositiveDouble(key, group, label, value, description);
        }
    }

    /// <summary>
    /// AutoCAD Editor.GetAngle returns PromptDoubleResult in the Civil 3D 2023
    /// managed API. This small adapter preserves the more descriptive local type
    /// used by the junction workflow without changing its behaviour.
    /// </summary>
    internal sealed class PromptAngleResult
    {
        private readonly PromptDoubleResult _result;

        private PromptAngleResult(PromptDoubleResult result)
        {
            _result = result;
        }

        public PromptStatus Status
        {
            get { return _result == null ? PromptStatus.Error : _result.Status; }
        }

        public double Value
        {
            get { return _result == null ? 0.0 : _result.Value; }
        }

        public static implicit operator PromptAngleResult(PromptDoubleResult result)
        {
            return new PromptAngleResult(result);
        }
    }
}
