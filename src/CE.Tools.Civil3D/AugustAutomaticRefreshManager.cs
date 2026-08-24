using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Compatibility command-ended manager. Automatic model rebuilding is owned by
    /// the dedicated dependency managers (Site Grid, linked feature lines, platform
    /// grading, etc.), not by every CE command. This keeps Enter/repeat-command,
    /// Undo/Redo and the cross-hair responsive and prevents a full model refresh
    /// merely because any CE command finished.
    /// </summary>
    internal static class AugustAutomaticRefreshManager
    {
        private static bool _initialized;
        private static readonly HashSet<Document> Attached = new HashSet<Document>();

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated += OnDocument;
            documents.DocumentActivated += OnDocument;
            documents.DocumentToBeDestroyed += OnDestroyed;
            Attach(documents.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated -= OnDocument;
            documents.DocumentActivated -= OnDocument;
            documents.DocumentToBeDestroyed -= OnDestroyed;
            foreach (Document document in new List<Document>(Attached)) Detach(document);
            Attached.Clear();
        }

        private static void OnDocument(object sender, DocumentCollectionEventArgs args)
        {
            if (args != null) Attach(args.Document);
        }

        private static void OnDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            if (args != null) Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || Attached.Contains(document)) return;
            try
            {
                document.CommandEnded += OnCommandEnded;
                Attached.Add(document);
            }
            catch { }
        }

        private static void Detach(Document document)
        {
            if (document == null || !Attached.Remove(document)) return;
            try { document.CommandEnded -= OnCommandEnded; } catch { }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            string name = ReadCommandName(args);
            if (!ShouldQueueRefresh(name)) return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
        }

        internal static bool ShouldQueueRefresh(string commandName)
        {
            // Field rule (24 Aug): NEVER queue the universal/platform refresh just
            // because a CE command ended. Commands that create linked data already
            // request their own display flush/refresh and object-specific managers
            // react only to linked-object edits. This also restores native Enter to
            // repeat the last command without a background refresh taking over.
            return false;
        }

        /*
        August 18 staged-repair compatibility anchor. The live implementation above
        intentionally supersedes the old blanket CE_ queue. Keeping this historical
        canonical block as metadata lets preserved installer repairs recognize the
        Site Grid exclusion; the final August 24 field-comments pass reasserts the
        no-blanket-refresh policy after all historical transforms.
            string name = ReadCommandName(args);
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(name, "CE_SITEGRID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_SITEGRIDREMOVE", StringComparison.OrdinalIgnoreCase))
                return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
        */

        private static string ReadCommandName(CommandEventArgs args)
        {
            if (args == null) return string.Empty;
            foreach (string propertyName in new[] { "GlobalCommandName", "CommandName" })
            {
                try
                {
                    PropertyInfo property = args.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    string value = Convert.ToString(property.GetValue(args, null));
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
            }
            return string.Empty;
        }
    }
}
