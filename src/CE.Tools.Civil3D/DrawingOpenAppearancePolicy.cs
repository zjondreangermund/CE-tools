using System;
namespace CETools.Civil3D
{
    /// <summary>Ignore host initialization, navigation and annotation-scale churn.</summary>
    internal static class DrawingOpenAppearancePolicy
    {
        internal static bool IsUserEditCommand(string commandNames)
        {
            if (string.IsNullOrWhiteSpace(commandNames)) return false;
            foreach (string item in commandNames.Split(new[] { ' ', '\t', '\r', '\n', '\'', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string command = item.Trim().TrimStart('.', '_').ToUpperInvariant();
                if (command.StartsWith("CE_", StringComparison.Ordinal) || command.StartsWith("CETOOLS", StringComparison.Ordinal)) return false;
                switch (command)
                {
                    case "OPEN": case "QOPEN": case "NEW": case "QNEW": case "RECOVER": case "RECOVERALL":
                    case "CLOSE": case "CLOSEALL": case "QUIT": case "NETLOAD": case "APPLOAD":
                    case "REGEN": case "REGENALL": case "ZOOM": case "PAN": case "VPORTS":
                    case "MSPACE": case "PSPACE": case "LAYOUT": case "LAYOUTTAB": case "MODEL":
                    case "CANNOSCALE": case "CANNOSCALEVALUE": case "ANNOAUTOSCALE": case "ANNOALLVISIBLE":
                    case "DIMSCALE": case "DIMTXT": case "DIMASZ": case "TEXTSIZE": case "SETVAR":
                    case "SAVE": case "QSAVE": case "SAVEAS": case "PLOT": case "PREVIEW":
                    case "UNDO": case "U": case "REDO": case "MREDO":
                        return false;
                }
            }
            return true;
        }
    }
}
