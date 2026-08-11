using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Runs interactive CE/AutoCAD commands strictly one at a time. A command is
    /// not submitted until the previous command has ended and AutoCAD is idle,
    /// so later command names cannot be consumed as answers to Editor prompts.
    /// </summary>
    internal static class CeSequentialCommandRunner
    {
        private static Document _document;
        private static readonly Queue<string> Queue = new Queue<string>();
        private static string _current = string.Empty;
        private static string _description = string.Empty;
        private static bool _launchPending;
        private static bool _waiting;
        private static bool _hooked;

        internal static bool IsRunning { get { return _hooked; } }

        internal static bool Start(Document document, IEnumerable<string> commands, string description)
        {
            if (document == null || commands == null) return false;
            if (_hooked)
            {
                document.Editor.WriteMessage("\nAnother CE sequential production workflow is already running. Finish or cancel it before starting this one.");
                return false;
            }

            foreach (string command in commands)
            {
                string normalized = Normalize(command);
                if (!string.IsNullOrWhiteSpace(normalized)) Queue.Enqueue(normalized);
            }
            if (Queue.Count == 0) return false;

            _document = document;
            _description = string.IsNullOrWhiteSpace(description) ? "CE production sequence" : description.Trim();
            _launchPending = true;
            _waiting = false;
            Hook();
            document.Editor.WriteMessage("\n{0} queued. Steps={1}. Each step will start only after the previous command finishes.", _description, Queue.Count);
            return true;
        }

        private static void Hook()
        {
            if (_hooked || _document == null) return;
            _hooked = true;
            _document.CommandEnded += OnCommandEnded;
            _document.CommandCancelled += OnCommandCancelled;
            _document.CommandFailed += OnCommandFailed;
            AcApplication.Idle += OnIdle;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (!_hooked || !_launchPending || _waiting || _document == null) return;
            if (!ReferenceEquals(AcApplication.DocumentManager.MdiActiveDocument, _document)) return;

            string commandNames;
            try { commandNames = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture); }
            catch { return; }
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            if (Queue.Count == 0)
            {
                Stop(true, false, string.Empty);
                return;
            }

            _current = Queue.Dequeue();
            _waiting = true;
            _launchPending = false;
            try
            {
                _document.SendStringToExecute(_current + " ", true, false, true);
            }
            catch (System.Exception exception)
            {
                Stop(false, true, exception.Message);
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (!_hooked || !_waiting || e == null) return;
            if (!string.Equals(Normalize(e.GlobalCommandName), _current, StringComparison.OrdinalIgnoreCase)) return;
            _waiting = false;
            _current = string.Empty;
            _launchPending = true;
        }

        private static void OnCommandCancelled(object sender, CommandEventArgs e)
        {
            if (!_hooked || !_waiting || e == null) return;
            if (!string.Equals(Normalize(e.GlobalCommandName), _current, StringComparison.OrdinalIgnoreCase)) return;
            Stop(false, true, "cancelled by the user");
        }

        private static void OnCommandFailed(object sender, CommandEventArgs e)
        {
            if (!_hooked || !_waiting || e == null) return;
            if (!string.Equals(Normalize(e.GlobalCommandName), _current, StringComparison.OrdinalIgnoreCase)) return;
            Stop(false, true, "command failed");
        }

        private static void Stop(bool completed, bool failed, string detail)
        {
            Document document = _document;
            string description = _description;
            if (_hooked && _document != null)
            {
                _document.CommandEnded -= OnCommandEnded;
                _document.CommandCancelled -= OnCommandCancelled;
                _document.CommandFailed -= OnCommandFailed;
            }
            if (_hooked) AcApplication.Idle -= OnIdle;

            Queue.Clear();
            _current = string.Empty;
            _description = string.Empty;
            _launchPending = false;
            _waiting = false;
            _hooked = false;
            _document = null;

            if (document == null) return;
            if (completed)
                document.Editor.WriteMessage("\n{0} complete.", description);
            else if (failed)
                document.Editor.WriteMessage("\n{0} stopped: {1}.", description, string.IsNullOrWhiteSpace(detail) ? "a step did not complete" : detail);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
        }
    }
}
