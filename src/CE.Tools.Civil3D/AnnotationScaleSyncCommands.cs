using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AnnotationScaleSyncCommands))]

namespace CETools.Civil3D
{
    public sealed class AnnotationScaleSyncCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_ANNOSCALESYNC",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SynchronizeCurrentScale()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            int updated;
            using (DocumentLock documentLock = document.LockDocument())
            {
                updated = AnnotationScaleSyncManager.ApplyCurrentScale(document);
            }
            document.Editor.WriteMessage(
                "\nCE_ANNOSCALESYNC added the current annotation scale to {0} supported annotation object(s).",
                updated);
        }
    }

    /// <summary>
    /// Watches CANNOSCALE on Application.Idle. When it changes, supported text,
    /// dimensions and leaders are made annotative and receive the current
    /// annotation-scale context. Work is deferred until AutoCAD is quiescent.
    /// </summary>
    internal static class AnnotationScaleSyncManager
    {
        private static readonly Dictionary<Database, string> LastScaleByDatabase =
            new Dictionary<Database, string>();
        private static bool _initialized;
        private static bool _busy;
        private static DateTime _lastPollUtc = DateTime.MinValue;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.DocumentManager.DocumentToBeDestroyed +=
                OnDocumentToBeDestroyed;
            AcApplication.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            AcApplication.Idle -= OnIdle;
            AcApplication.DocumentManager.DocumentToBeDestroyed -=
                OnDocumentToBeDestroyed;
            LastScaleByDatabase.Clear();
            _initialized = false;
        }

        private static void OnDocumentToBeDestroyed(
            object sender,
            DocumentCollectionEventArgs eventArgs)
        {
            if (eventArgs != null && eventArgs.Document != null)
                LastScaleByDatabase.Remove(eventArgs.Document.Database);
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            if (_busy ||
                (DateTime.UtcNow - _lastPollUtc).TotalMilliseconds < 500.0)
                return;
            _lastPollUtc = DateTime.UtcNow;

            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            string currentScale = ReadCurrentScaleName();
            string previousScale;
            if (LastScaleByDatabase.TryGetValue(
                    document.Database,
                    out previousScale) &&
                string.Equals(
                    previousScale,
                    currentScale,
                    StringComparison.OrdinalIgnoreCase))
                return;

            _busy = true;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    ApplyCurrentScale(document);
                }
                LastScaleByDatabase[document.Database] = currentScale;
                document.Editor.Regen();
            }
            catch
            {
                // Retry on the next idle cycle; scale changes can briefly occur
                // while Civil 3D owns the document or is rebuilding labels.
            }
            finally
            {
                _busy = false;
            }
        }

        internal static int ApplyCurrentScale(Document document)
        {
            if (document == null) return 0;
            Database database = document.Database;
            object currentContext = ResolveCurrentAnnotationContext(database);
            if (currentContext == null) return 0;

            int updated = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null ||
                        (!block.IsLayout && !block.IsAnonymous))
                        continue;

                    foreach (ObjectId objectId in block)
                    {
                        Entity entity = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (!IsSupportedAnnotation(entity)) continue;

                        entity.UpgradeOpen();
                        bool changed = SetAnnotative(entity);
                        changed = AddContext(entity, currentContext) || changed;
                        if (changed) updated++;
                    }
                }
                transaction.Commit();
            }
            return updated;
        }

        private static bool IsSupportedAnnotation(Entity entity)
        {
            if (entity == null) return false;
            if (entity is DBText ||
                entity is MText ||
                entity is Dimension ||
                entity is MLeader)
                return true;

            string typeName = entity.GetType().Name;
            return typeName.IndexOf(
                       "Label",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf(
                       "Table",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool SetAnnotative(object value)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Annotative",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;

                object current = property.GetValue(value, null);
                object target = property.PropertyType.IsEnum
                    ? Enum.Parse(property.PropertyType, "True", true)
                    : (object)true;
                if (Equals(current, target)) return false;
                property.SetValue(value, target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool AddContext(object value, object context)
        {
            try
            {
                MethodInfo method = value.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(item =>
                        string.Equals(
                            item.Name,
                            "AddContext",
                            StringComparison.Ordinal) &&
                        item.GetParameters().Length == 1);
                if (method == null) return false;
                method.Invoke(value, new[] { context });
                return true;
            }
            catch
            {
                // Existing contexts and unsupported entity types are harmless.
                return false;
            }
        }

        private static object ResolveCurrentAnnotationContext(Database database)
        {
            try
            {
                object manager = database.GetType()
                    .GetProperty(
                        "ObjectContextManager",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(database, null);
                if (manager == null) return null;

                MethodInfo getCollection = manager.GetType().GetMethod(
                    "GetContextCollection",
                    new[] { typeof(string) });
                object collection = getCollection?.Invoke(
                    manager,
                    new object[] { "ACDB_ANNOTATIONSCALES" });
                if (collection == null) return null;

                MethodInfo getContext = collection.GetType().GetMethod(
                    "GetContext",
                    new[] { typeof(string) });
                return getContext?.Invoke(
                    collection,
                    new object[] { ReadCurrentScaleName() });
            }
            catch
            {
                return null;
            }
        }

        private static string ReadCurrentScaleName()
        {
            try
            {
                return Convert.ToString(
                    AcApplication.GetSystemVariable("CANNOSCALE"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
