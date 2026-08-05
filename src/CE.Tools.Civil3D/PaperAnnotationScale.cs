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
            double paper = NormalizeConfiguredPaperHeight(paperMillimetres);
            return Math.Max(
                paper * CurrentAnnotationScale(database) * DrawingUnitsPerMillimetre(database),
                MinimumHeight);
        }

        /// <summary>
        /// Height stored on annotative DBText/MText/MLeader objects in Civil 3D
        /// 2023. The managed entity property is still the model-space height;
        /// Civil 3D derives the displayed Paper text height from that value and
        /// the active annotation scale. Using paper millimetres directly here
        /// produced 0.005-style heights in metre drawings and forced users to
        /// enter artificial values such as 1200.
        /// </summary>
        public static double AnnotativeTextHeight(Database database, double paperMillimetres)
        {
            return ModelTextHeight(database, paperMillimetres);
        }

        public static double ModelDistance(
            Database database,
            double paperMillimetres)
        {
            return Math.Max(
                NormalizePaperHeight(paperMillimetres) *
                    CurrentAnnotationScale(database) *
                    DrawingUnitsPerMillimetre(database),
                MinimumHeight);
        }

        public static double PaperTextHeight(Database database, double modelHeight)
        {
            double denominator =
                CurrentAnnotationScale(database) * DrawingUnitsPerMillimetre(database);
            if (!(denominator > 0.0))
                return NormalizeConfiguredPaperHeight(modelHeight);
            return NormalizeConfiguredPaperHeight(modelHeight / denominator);
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

        /// <summary>
        /// Repairs persisted UI values from earlier builds while preserving the
        /// five absolute paper-mm choices used throughout CE Tools.
        /// </summary>
        public static double NormalizeConfiguredPaperHeight(double value)
        {
            if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
                return 2.0;

            // Earlier metre-drawing builds persisted values such as 0.005 for
            // 5 mm. Convert those drawing-unit values back to paper millimetres.
            if (value < 0.05)
                value *= 1000.0;

            // Oversized compensating values (for example 1200) were entered to
            // work around the old missing annotation-scale multiplication.
            if (value > 25.0)
                return 5.0;

            double[] choices = { 1.8, 2.0, 2.5, 3.5, 5.0 };
            double nearest = choices[0];
            double difference = Math.Abs(value - nearest);
            foreach (double choice in choices)
            {
                double candidate = Math.Abs(value - choice);
                if (candidate <= difference)
                {
                    nearest = choice;
                    difference = candidate;
                }
            }
            return nearest;
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
            double scale = ReadNamedAnnotationScale();
            if (IsValidScale(scale)) return scale;

            scale = ReadDatabaseAnnotationScale(database);
            if (IsValidScale(scale)) return scale;

            try
            {
                scale = Convert.ToDouble(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALEVALUE"),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                scale = 0.0;
            }
            if (IsValidScale(scale) && scale >= 10.0) return scale;

            if (database != null && IsValidScale(database.Dimscale))
                return database.Dimscale;
            return 1.0;
        }

        private static double ReadNamedAnnotationScale()
        {
            try
            {
                string text = Convert.ToString(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALE"),
                    CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(text)) return 0.0;
                text = text.Trim();
                int separator = text.IndexOf(':');
                if (separator > 0 && separator < text.Length - 1)
                {
                    double paper;
                    double drawing;
                    if (double.TryParse(
                            text.Substring(0, separator).Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out paper) &&
                        double.TryParse(
                            text.Substring(separator + 1).Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out drawing) &&
                        paper > 0.0 && drawing > 0.0)
                        return drawing / paper;
                }
            }
            catch
            {
                // Continue to the database annotation-scale object.
            }
            return 0.0;
        }

        private static double ReadDatabaseAnnotationScale(Database database)
        {
            if (database == null) return 0.0;
            try
            {
                PropertyInfo property = database.GetType().GetProperty(
                    "Cannoscale",
                    BindingFlags.Public | BindingFlags.Instance);
                object context = property == null || property.GetGetMethod() == null
                    ? null
                    : property.GetValue(database, null);
                if (context == null) return 0.0;
                double paper = ReadDouble(context, "PaperUnits");
                double drawing = ReadDouble(context, "DrawingUnits");
                return paper > 0.0 && drawing > 0.0
                    ? drawing / paper
                    : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double ReadDouble(object value, string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetGetMethod() == null) return 0.0;
                return Convert.ToDouble(
                    property.GetValue(value, null),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0;
            }
        }

        private static bool IsValidScale(double value)
        {
            return value > 0.0 &&
                !double.IsNaN(value) &&
                !double.IsInfinity(value);
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
