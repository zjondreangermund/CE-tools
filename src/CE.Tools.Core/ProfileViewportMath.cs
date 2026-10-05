using System;

namespace CETools.Core
{
    public static class ProfileViewportMath
    {
        public static double CustomScale(double denominator, double modelUnitsPerMetre, double paperUnitsPerMetre)
        {
            Positive(denominator);
            Positive(modelUnitsPerMetre);
            Positive(paperUnitsPerMetre);
            double scale = paperUnitsPerMetre / modelUnitsPerMetre / denominator;
            Positive(scale);
            return scale;
        }

        // A zero scale retains the existing vertical-fit behaviour.
        public static double CoreWidth(double modelHeight, double paperWidth, double paperHeight,
            double horizontalClearance, double fixedScale)
        {
            Positive(modelHeight);
            Positive(paperWidth);
            Positive(paperHeight);
            if (fixedScale > 0 && modelHeight * fixedScale > paperHeight + 1e-6) return 0;
            double scale = fixedScale > 0 ? fixedScale : paperHeight / modelHeight;
            return Math.Max(0, paperWidth / scale - 2 * horizontalClearance);
        }

        public static int RequiredSlots(double coreWidth, double modelHeight, double paperWidth,
            double paperHeight, double horizontalClearance, bool split, double fixedScale)
        {
            double capacity = CoreWidth(modelHeight, paperWidth, paperHeight, horizontalClearance, fixedScale);
            if (capacity <= 1e-6) return 0;
            if (coreWidth <= capacity + 1e-6 || (!split && fixedScale == 0)) return 1;
            if (!split) return 0; // A specified scale must never silently shrink.
            double count = Math.Ceiling((coreWidth - 1e-6) / capacity);
            if (count > 1000) throw new ArgumentOutOfRangeException(nameof(coreWidth), "More than 1000 profile viewports would be required.");
            return Math.Max(1, (int)count);
        }

        private static void Positive(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a finite value greater than zero.");
        }
    }
}
