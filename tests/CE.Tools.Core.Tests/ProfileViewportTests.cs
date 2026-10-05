using System;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class ProfileViewportTests
    {
        internal static int Run()
        {
            Near(1, ProfileViewportMath.CustomScale(1000, 1, 1000));
            Near(0.001, ProfileViewportMath.CustomScale(1000, 1000, 1000));
            Near(1.0 / 25.4, ProfileViewportMath.CustomScale(1000, 1, 1000.0 / 25.4));
            Equal(3, ProfileViewportMath.RequiredSlots(650, 50, 300, 70, 5, true, 1));
            Equal(1, ProfileViewportMath.RequiredSlots(290, 50, 300, 70, 5, true, 1));
            Equal(0, ProfileViewportMath.RequiredSlots(100, 80, 300, 70, 5, true, 1));
            Equal(0, ProfileViewportMath.RequiredSlots(650, 50, 300, 70, 5, false, 1));
            Equal(1, ProfileViewportMath.RequiredSlots(650, 50, 300, 70, 5, false, 0));
            Equal(4, ProfileViewportMath.RequiredSlots(650, 50, 300, 70, 5, true, 0));
            bool refused = false;
            try { ProfileViewportMath.CustomScale(double.NaN, 1, 1000); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            if (!refused) throw new Exception("Invalid viewport scales must be refused.");
            return 10;
        }

        private static void Near(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Incorrect viewport unit conversion.");
        }
        private static void Equal(int expected, int actual)
        {
            if (expected != actual) throw new Exception("Incorrect profile viewport count: " + actual);
        }
    }
}
