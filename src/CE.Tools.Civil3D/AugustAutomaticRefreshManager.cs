using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Ensures newly placed linked tables/annotations are queued immediately after
    /// the creating CE command ends. The existing universal idle manager performs
    /// the actual mutation, so this adds no competing transaction loop.
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
            string name = NormalizeCommandName(ReadCommandName(args));
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;

            // A refresh/maintenance command has already brought linked outputs up to
            // date. Re-queueing another idle refresh immediately afterwards creates
            // a second presentation pass, visible table flicker and unnecessary
            // undo/transaction noise. Creation/edit commands still queue normally.
            if (IsRefreshMaintenanceCommand(name)) return;

            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
        }

        private static bool IsRefreshMaintenanceCommand(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOf("REFRESH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(name, "CE_COGOPOINTSYNC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_COGOOVERLAPFIX", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CE_TABLECENTERALL", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCommandName(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
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
