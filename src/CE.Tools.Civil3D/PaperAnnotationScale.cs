using System;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Converts CE Tools paper heights in millimetres to the model-space height
    /// stored by AutoCAD entities. For example, 2.0 mm at 1:250 in a metre drawing
    /// is stored as 0.5 drawing units, not 2.0 drawing units.
    /// </summary>
    internal static class PaperAnnotationScale
    {
        private const double MinimumHeight = 1e-6;

        public static double ModelTextHeight(Database database, double paperMillimetres)
        {
            double paper = NormalizePaperHeight(paperMillimetres);
            return Math.Max(
                paper * CurrentAnnotationScale(database) * DrawingUnitsPerMillimetre(database),
                MinimumHeight);
        }

        /// <summary>
        /// Height stored on an annotative DBText/MText/MLeader. AutoCAD applies
        /// the current annotation context itself, so this value is paper height
        /// converted only to drawing units and must not be scale-multiplied.
        /// </summary>
        public static double AnnotativeTextHeight(Database database, double paperMillimetres)
        {
            return Math.Max(
                NormalizePaperHeight(paperMillimetres) * DrawingUnitsPerMillimetre(database),
                MinimumHeight);
        }

        public static double ModelDistance(
            Database database,
            double paperMillimetres)
        {
            return ModelTextHeight(database, paperMillimetres);
        }

        public static double PaperTextHeight(Database database, double modelHeight)
        {
            double denominator =
                CurrentAnnotationScale(database) * DrawingUnitsPerMillimetre(database);
            if (!(denominator > 0.0)) return NormalizePaperHeight(modelHeight);
            return NormalizePaperHeight(modelHeight / denominator);
        }

        public static double NormalizePaperHeight(double value)
        {
            if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
                return 2.0;
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 2.0) < 0.05) return 2.0;
            if (Math.Abs(value - 2.5) < 0.05) return 2.5;
            if (Math.Abs(value - 3.5) < 0.05) return 3.5;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return value;
        }

        public static bool SetAnnotative(object value)
        {
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Annotative",
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite) return false;
                if (property.PropertyType == typeof(bool))
                {
                    property.SetValue(value, true, null);
                    return true;
                }
                if (property.PropertyType.IsEnum)
                {
                    string[] candidates = { "True", "Yes", "On" };
                    foreach (string candidate in candidates)
                    {
                        if (Array.IndexOf(Enum.GetNames(property.PropertyType), candidate) < 0)
                            continue;
                        property.SetValue(
                            value,
                            Enum.Parse(property.PropertyType, candidate),
                            null);
                        return true;
                    }
                }
            }
            catch
            {
                // Some Civil 3D wrappers expose read-only annotative state.
            }
            return false;
        }

        private static double CurrentAnnotationScale(Database database)
        {
            double scale = 0.0;
            try
            {
                scale = Convert.ToDouble(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALEVALUE"),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                if (database != null) scale = database.Dimscale;
            }
            return scale > 0.0 && !double.IsNaN(scale) && !double.IsInfinity(scale)
                ? scale
                : 1.0;
        }

        private static double DrawingUnitsPerMillimetre(Database database)
        {
            string units = database == null ? string.Empty : database.Insunits.ToString();
            if (string.Equals(units, "Millimeters", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(units, "Centimeters", StringComparison.OrdinalIgnoreCase)) return 0.1;
            if (string.Equals(units, "Meters", StringComparison.OrdinalIgnoreCase)) return 0.001;
            if (string.Equals(units, "Inches", StringComparison.OrdinalIgnoreCase)) return 0.0393700787;
            if (string.Equals(units, "Feet", StringComparison.OrdinalIgnoreCase)) return 0.0032808399;

            // Civil/site drawings with large Easting/Northing values are normally
            // metre based when INSUNITS is unset.
            return 0.001;
        }
    }
}
