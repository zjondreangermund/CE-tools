using System;
using System.Collections.Generic;
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
            string name = args == null ? string.Empty : (args.GlobalCommandName ?? string.Empty);
            if (!name.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)) return;
            UniversalDynamicRefreshManager.Queue();
            PlatformDynamicRefreshManager.Queue();
        }
    }
}
