using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace CETools.Civil3D
{
    /// <summary>
    /// Runs the one-view native editor in command context, then continues only
    /// after that command has completed and released its native dialog/objects.
    /// </summary>
    internal sealed class ProfileViewNativeEditor
    {
        private bool _completed;
        private bool _aborted;

        internal static bool EditAndApply(Document document, ObjectId sourceId, Action apply)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (sourceId.IsNull) throw new ArgumentException("A source profile view is required.", nameof(sourceId));
            if (apply == null) throw new ArgumentNullException(nameof(apply));

            var edit = new ProfileViewNativeEditor();
            document.CommandEnded += edit.OnCommandEnded;
            document.CommandCancelled += edit.OnCommandAborted;
            document.CommandFailed += edit.OnCommandAborted;
            try
            {
                // PICKFIRST is optional. Clearing it makes sourceId answer the
                // graph prompt consistently, even when launched with a batch.
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                document.Editor.Command("_.EditGraphProperties", sourceId);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
                when (exception.ErrorStatus == ErrorStatus.UserBreak)
            {
                return false;
            }
            finally
            {
                document.CommandEnded -= edit.OnCommandEnded;
                document.CommandCancelled -= edit.OnCommandAborted;
                document.CommandFailed -= edit.OnCommandAborted;
            }

            if (!edit._completed || edit._aborted) return false;
            // Do not modify target views inside CommandEnded: the native command
            // can still hold its source open until Editor.Command returns.
            apply();
            return true;
        }

        private void OnCommandEnded(object sender, CommandEventArgs args)
        {
            if (IsGraphProperties(args.GlobalCommandName)) _completed = true;
        }

        private void OnCommandAborted(object sender, CommandEventArgs args)
        {
            if (IsGraphProperties(args.GlobalCommandName)) _aborted = true;
        }

        private static bool IsGraphProperties(string command)
        {
            string name = (command ?? string.Empty).Trim().TrimStart('_', '.');
            return string.Equals(name, "EDITGRAPHPROPERTIES", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AECCEDITGRAPHPROPERTIES", StringComparison.OrdinalIgnoreCase);
        }
    }
}
