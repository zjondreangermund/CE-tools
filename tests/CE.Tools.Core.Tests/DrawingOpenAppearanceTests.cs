using System;
using CETools.Civil3D;
namespace CETools.Core.Tests
{
    internal static class DrawingOpenAppearanceTests
    {
        internal static int Run()
        {
            string[] passive = { null, "", " ", "OPEN", "_QOPEN", ".NEW", "REGEN", "ZOOM", "PAN", "PSPACE", "MSPACE", "LAYOUT", "CANNOSCALE", "DIMTXT", "TEXTSIZE", "SETVAR", "QSAVE", "NETLOAD", "RECOVER", "CE_MULTIDIM", "CE_SEWPROFILE", "MOVE 'ZOOM", "OPEN MOVE", "U", "REDO" };
            foreach (string command in passive)
                if (DrawingOpenAppearancePolicy.IsUserEditCommand(command))
                    throw new Exception("Passive command can trigger presentation refresh: " + command);
            string[] edits = { "MOVE", "_STRETCH", "GRIP_STRETCH", "ERASE", "PEDIT", "_.ROTATE" };
            foreach (string command in edits)
                if (!DrawingOpenAppearancePolicy.IsUserEditCommand(command))
                    throw new Exception("Explicit source edit was suppressed: " + command);
            return passive.Length + edits.Length;
        }
    }
}
