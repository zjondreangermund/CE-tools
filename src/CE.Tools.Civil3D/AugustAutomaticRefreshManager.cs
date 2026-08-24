using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Queues linked-output refresh only after CE commands that can actually change
    /// drawing/model data. Workflow centres, settings/menu launchers and review-only
    /// commands must not regenerate the model merely because a popup was opened.
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
            string name = (commandName ?? string.Empty).Trim().ToUpperInvariant();
            if (!name.StartsWith("CE_", StringComparison.Ordinal)) return false;

            string[] nonMutatingTokens =
            {
                "PRODUCTIONSTRUCTURED",
                "PRODUCTIONCENTRE",
                "WORKFLOW",
                "MENU",
                "TOOLS",
                "SETTINGS",
                "SETTINGSPRODUCTION",
                "REPORTCENTRE"
            };
            foreach (string token in nonMutatingTokens)
                if (name.IndexOf(token, StringComparison.Ordinal) >= 0) return false;

            return true;
        }

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
