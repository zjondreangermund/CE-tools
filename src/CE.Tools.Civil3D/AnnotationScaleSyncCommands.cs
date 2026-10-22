using System;
using System.Collections;
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
#if !(CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027)
            AcApplication.Idle += OnIdle;
#endif
        }

        public static void Terminate()
        {
            if (!_initialized) return;
#if !(CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027)
            AcApplication.Idle -= OnIdle;
#endif
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

#if CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027
            // Passive CANNOSCALE monitoring must NEVER rewrite user dimension
            // objects or add arbitrary contexts when a layout/viewport changes.
            // This also avoids continuous regen and CE dimension rebuild loops.
            LastScaleByDatabase[document.Database] = ReadCurrentScaleName();
            return;
#endif
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
#if CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027
                    // Annotative scale sync applies to model-space engineering
                    // annotations, NEVER paper-space title blocks/text.
                    // This was forcing layouts' text Annotative=Yes on CANNOSCALE
                    // changes and making title-sheet descriptions grow.
                    if (block.IsLayout &&
                        !string.Equals(block.Name, BlockTableRecord.ModelSpace,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
#endif

                    foreach (ObjectId objectId in block)
                    {
                        Entity entity = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (!IsSupportedAnnotation(entity)) continue;

#if CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027
                        // An explicit scale sync must not turn existing
                        // dimensions/text into annotative objects or change
                        // their apparent paper height. Dimension styles own
                        // dimension appearance; CE never modifies them here.
                        if (entity is Dimension || !IsAlreadyAnnotative(entity))
                            continue;
                        entity.UpgradeOpen();
                        if (AddContext(entity, currentContext)) updated++;
#else
                        entity.UpgradeOpen();
                        bool changed = SetAnnotative(entity);
                        changed = AddContext(entity, currentContext) || changed;
                        if (changed) updated++;
#endif
                    }
                }
                transaction.Commit();
            }

            // Segment labels store their configured paper height and offset. Queue
            // the universal dynamic pass whenever an annotation scale is applied so
            // their model-space size/offset is rebuilt for the new CANNOSCALE.
#if !(CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027)
            UniversalDynamicRefreshManager.Queue();
#endif
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

#if CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027
        private static bool IsAlreadyAnnotative(object value)
        {
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Annotative", BindingFlags.Public | BindingFlags.Instance);
                object current = property == null ? null : property.GetValue(value, null);
                return current != null &&
                    (string.Equals(current.ToString(), "True",
                        StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(current.ToString(), "Yes",
                        StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
#endif

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

#if CE_CIVIL3D_2024 || CE_CIVIL3D_2025 || CE_CIVIL3D_2026 || CE_CIVIL3D_2027
        /// <summary>
        /// Read only annotation scales actually referenced by this drawing's
        /// paper-space viewports, plus its current model annotation scale.
        /// Never enumerate the entire annotation-scale library per road name.
        /// Caller supplies its existing transaction to avoid costly rescans.
        /// </summary>
        internal static IList<object> ReadUsedDrawingScaleContexts(
            Database database, Transaction transaction)
        {
            var result = new List<object>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (database == null || transaction == null) return result;
            object current = ResolveCurrentAnnotationContext(database);
            AddUniqueContext(result, names, current);
            try
            {
                BlockTable table = transaction.GetObject(
                    database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null) return result;
                foreach (ObjectId recordId in table)
                {
                    BlockTableRecord layout = transaction.GetObject(
                        recordId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (layout == null || !layout.IsLayout ||
                        string.Equals(layout.Name, BlockTableRecord.ModelSpace,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (ObjectId id in layout)
                    {
                        try
                        {
                            Viewport vp = transaction.GetObject(
                                id, OpenMode.ForRead, false) as Viewport;
                            if (vp == null || vp.Number == 1 || !vp.On)
                                continue;
                            PropertyInfo property = vp.GetType().GetProperty(
                                "AnnotationScale",
                                BindingFlags.Public | BindingFlags.Instance);
                            object context = property == null
                                ? null : property.GetValue(vp, null);
                            AddUniqueContext(result, names, context);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        private static void AddUniqueContext(
            IList<object> result, ISet<string> names, object context)
        {
            if (context == null) return;
            string name = string.Empty;
            try
            {
                PropertyInfo property = context.GetType().GetProperty(
                    "Name", BindingFlags.Public | BindingFlags.Instance);
                name = Convert.ToString(
                    property == null ? null : property.GetValue(context, null),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) return;
            if (names.Add(name)) result.Add(context);
        }

        internal static int AddUsedDrawingScaleContexts(
            Entity entity, IEnumerable<object> contexts)
        {
            if (entity == null || contexts == null) return 0;
            int count = 0;
            foreach (object context in contexts)
                if (context != null && AddContext(entity, context)) count++;
            return count;
        }
#endif

        internal static int AddAllAnnotationScaleContexts(
            Entity entity,
            Database database)
        {
            if (entity == null ||
                database == null)
                return 0;

            SetAnnotative(entity);
            int added = 0;
            try
            {
                object manager = database.GetType()
                    .GetProperty(
                        "ObjectContextManager",
                        BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(database, null);
                if (manager == null)
                    return 0;

                MethodInfo getCollection = manager.GetType().GetMethod(
                    "GetContextCollection",
                    new[] { typeof(string) });
                object collection = getCollection?.Invoke(
                    manager,
                    new object[] { "ACDB_ANNOTATIONSCALES" });
                if (collection == null)
                    return 0;

                IEnumerable enumerable =
                    collection as IEnumerable;
                if (enumerable != null)
                {
                    foreach (object context in enumerable)
                    {
                        if (context != null &&
                            AddContext(
                                entity,
                                context))
                            added++;
                    }
                    return added;
                }

                // Compatibility path for host wrappers that do not implement
                // IEnumerable but expose Count and an indexer/Item accessor.
                PropertyInfo countProperty =
                    collection.GetType().GetProperty(
                        "Count",
                        BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo itemProperty =
                    collection.GetType().GetProperty(
                        "Item",
                        BindingFlags.Public | BindingFlags.Instance);
                if (countProperty == null ||
                    itemProperty == null)
                    return added;

                int count = Convert.ToInt32(
                    countProperty.GetValue(
                        collection,
                        null),
                    CultureInfo.InvariantCulture);
                for (int index = 0;
                     index < count;
                     index++)
                {
                    object context = null;
                    try
                    {
                        context = itemProperty.GetValue(
                            collection,
                            new object[] { index });
                    }
                    catch { }
                    if (context != null &&
                        AddContext(
                            entity,
                            context))
                        added++;
                }
            }
            catch
            {
                // The current annotation-scale context is still supplied by the
                // existing idle synchronizer. Missing host enumeration support
                // must never block label creation.
            }
            return added;
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
