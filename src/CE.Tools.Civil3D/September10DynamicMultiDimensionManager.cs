using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    /// <summary>
    /// Persistent source/output links for CE_MULTIDIM. Dynamic dimensions are
    /// regenerated from the original line/polyline/feature line after geometry or
    /// CANNOSCALE changes. The universal idle refresh owns execution so background
    /// rebuilds inherit its undo-recording suppression.
    /// </summary>
    internal static class DynamicMultiDimensionManager
    {
        private const string SourceRecordKey = "CE_DYNAMIC_MULTIDIM_SOURCE";
        private const string OutputRecordKey = "CE_DYNAMIC_MULTIDIM_OUTPUT";
        private const string Version = "CE_DYNAMIC_MULTIDIM_V1";

        private static bool _commandDynamic;
        private static ObjectId _currentSourceId = ObjectId.Null;
        private static string _currentMode = string.Empty;
        private static string _currentStyleName = string.Empty;
        private static double _currentOffsetPaper = 8.0;
        private static double _currentLeaderPaper = 6.0;
        private static string _currentFingerprint = string.Empty;
        private static readonly List<string> CurrentOutputs = new List<string>();

        internal static void BeginCommand(
            Document document,
            bool dynamic,
            string mode,
            double offsetPaper,
            double leaderPaper)
        {
            _commandDynamic = dynamic;
            _currentMode = mode ?? string.Empty;
            _currentOffsetPaper = Math.Max(offsetPaper, 0.0);
            _currentLeaderPaper = Math.Max(leaderPaper, 0.0);
            ClearCurrentSource();
        }

        internal static void ClearCurrentSource()
        {
            _currentSourceId = ObjectId.Null;
            _currentStyleName = string.Empty;
            _currentFingerprint = string.Empty;
            CurrentOutputs.Clear();
        }

        internal static void BeginSource(
            Transaction transaction,
            Entity source,
            string mode,
            ObjectId styleId)
        {
            ClearCurrentSource();
            if (transaction == null || source == null || !IsSupportedSource(source)) return;

            if (!_commandDynamic)
            {
                DetachExisting(transaction, source);
                return;
            }

            ErasePreviousOutputs(transaction, source);
            _currentSourceId = source.ObjectId;
            _currentMode = mode ?? string.Empty;
            _currentStyleName = ReadStyleName(transaction, styleId);
            _currentFingerprint = Fingerprint(source);
            WriteSourceRecord(transaction, source);
        }

        internal static void BeginRebuildSource(
            Transaction transaction,
            Entity source,
            string mode,
            ObjectId styleId,
            double offsetPaper,
            double leaderPaper)
        {
            _commandDynamic = true;
            _currentOffsetPaper = Math.Max(offsetPaper, 0.0);
            _currentLeaderPaper = Math.Max(leaderPaper, 0.0);
            ClearCurrentSource();
            if (transaction == null || source == null || !IsSupportedSource(source)) return;

            _currentSourceId = source.ObjectId;
            _currentMode = mode ?? string.Empty;
            _currentStyleName = ReadStyleName(transaction, styleId);
            _currentFingerprint = Fingerprint(source);
            WriteSourceRecord(transaction, source);
        }

        internal static void CaptureOutput(Transaction transaction, Dimension dimension)
        {
            if (!_commandDynamic || transaction == null || dimension == null || _currentSourceId.IsNull)
                return;

            Entity source = null;
            try { source = transaction.GetObject(_currentSourceId, OpenMode.ForWrite, false) as Entity; }
            catch { }
            if (source == null) return;

            try { PaperAnnotationScale.SetAnnotative(dimension); } catch { }
            WriteRecord(
                transaction,
                dimension,
                OutputRecordKey,
                new[]
                {
                    new TypedValue((int)DxfCode.Text, Version),
                    new TypedValue((int)DxfCode.Text, source.Handle.ToString())
                });

            string outputHandle = dimension.Handle.ToString();
            if (!CurrentOutputs.Contains(outputHandle, StringComparer.OrdinalIgnoreCase))
                CurrentOutputs.Add(outputHandle);
            WriteSourceRecord(transaction, source);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null || document.Database == null) return 0;
            Database database = document.Database;
            var dirty = new List<DynamicMultiDimensionSource>();
            var orphanOutputs = new HashSet<ObjectId>();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (Entity entity in EnumerateEntities(database, transaction))
                {
                    DynamicMultiDimensionSource record;
                    if (TryReadSourceRecord(transaction, entity, out record))
                    {
                        record.SourceId = entity.ObjectId;
                        record.SourceHandle = entity.Handle.ToString();
                        bool changed = !string.Equals(
                            record.Fingerprint,
                            Fingerprint(entity),
                            StringComparison.Ordinal);
                        bool missingOutput = record.OutputHandles.Any(handle =>
                        {
                            ObjectId id = ResolveHandle(database, handle);
                            if (id.IsNull) return true;
                            try
                            {
                                DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                                return value == null || value.IsErased;
                            }
                            catch { return true; }
                        });
                        if (changed || missingOutput) dirty.Add(record);
                    }

                    string sourceHandle;
                    if (TryReadOutputRecord(transaction, entity, out sourceHandle))
                    {
                        ObjectId sourceId = ResolveHandle(database, sourceHandle);
                        Entity source = null;
                        if (!sourceId.IsNull)
                        {
                            try { source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity; }
                            catch { }
                        }
                        DynamicMultiDimensionSource sourceRecord;
                        if (source == null || !TryReadSourceRecord(transaction, source, out sourceRecord))
                            orphanOutputs.Add(entity.ObjectId);
                    }
                }
            }

            if (dirty.Count == 0 && orphanOutputs.Count == 0) return 0;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in orphanOutputs)
                    EraseObject(transaction, id);

                foreach (DynamicMultiDimensionSource record in dirty)
                {
                    foreach (string handle in record.OutputHandles)
                        EraseObject(transaction, ResolveHandle(database, handle));
                }
                transaction.Commit();
            }

            int rebuilt = 0;
            foreach (DynamicMultiDimensionSource record in dirty
                .GroupBy(item => item.SourceId)
                .Select(group => group.First()))
            {
                try
                {
                    if (MultiDimensionCommands.RebuildDynamicSource(
                        document,
                        record.SourceId,
                        record.Mode,
                        record.StyleName,
                        record.OffsetPaper,
                        record.LeaderPaper) >= 0)
                        rebuilt++;
                }
                catch
                {
                    // Leave the source record in place so a later idle pass can retry.
                }
            }

            ClearCurrentSource();
            return rebuilt;
        }

        private static bool IsSupportedSource(Entity source)
        {
            return source is Line || source is Polyline || source is CivilFeatureLine;
        }

        private static void ErasePreviousOutputs(Transaction transaction, Entity source)
        {
            DynamicMultiDimensionSource record;
            if (!TryReadSourceRecord(transaction, source, out record)) return;
            foreach (string handle in record.OutputHandles)
                EraseObject(transaction, ResolveHandle(source.Database, handle));
        }

        private static void DetachExisting(Transaction transaction, Entity source)
        {
            DynamicMultiDimensionSource record;
            if (!TryReadSourceRecord(transaction, source, out record)) return;

            foreach (string handle in record.OutputHandles)
            {
                ObjectId outputId = ResolveHandle(source.Database, handle);
                if (outputId.IsNull) continue;
                Entity output = null;
                try { output = transaction.GetObject(outputId, OpenMode.ForRead, false) as Entity; }
                catch { }
                if (output != null) RemoveRecord(transaction, output, OutputRecordKey);
            }
            RemoveRecord(transaction, source, SourceRecordKey);
        }

        private static void WriteSourceRecord(Transaction transaction, Entity source)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, Version),
                new TypedValue((int)DxfCode.Text, _currentMode ?? string.Empty),
                new TypedValue((int)DxfCode.Text, _currentStyleName ?? string.Empty),
                new TypedValue((int)DxfCode.Real, _currentOffsetPaper),
                new TypedValue((int)DxfCode.Real, _currentLeaderPaper),
                new TypedValue((int)DxfCode.Text, _currentFingerprint ?? string.Empty)
            };
            foreach (string handle in CurrentOutputs)
                values.Add(new TypedValue((int)DxfCode.Text, handle));
            WriteRecord(transaction, source, SourceRecordKey, values.ToArray());
        }

        private static bool TryReadSourceRecord(
            Transaction transaction,
            Entity source,
            out DynamicMultiDimensionSource record)
        {
            record = new DynamicMultiDimensionSource();
            TypedValue[] values;
            if (!TryReadRecord(transaction, source, SourceRecordKey, out values) || values.Length < 6)
                return false;
            try
            {
                if (!string.Equals(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), Version, StringComparison.Ordinal))
                    return false;
                record.Mode = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                record.StyleName = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                record.OffsetPaper = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture);
                record.LeaderPaper = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture);
                record.Fingerprint = Convert.ToString(values[5].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                for (int index = 6; index < values.Length; index++)
                {
                    string handle = Convert.ToString(values[index].Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(handle)) record.OutputHandles.Add(handle);
                }
                return true;
            }
            catch { return false; }
        }

        private static bool TryReadOutputRecord(
            Transaction transaction,
            Entity output,
            out string sourceHandle)
        {
            sourceHandle = string.Empty;
            TypedValue[] values;
            if (!TryReadRecord(transaction, output, OutputRecordKey, out values) || values.Length < 2)
                return false;
            if (!string.Equals(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), Version, StringComparison.Ordinal))
                return false;
            sourceHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(sourceHandle);
        }

        private static IEnumerable<Entity> EnumerateEntities(Database database, Transaction transaction)
        {
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) yield break;
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = null;
                try { block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord; }
                catch { }
                if (block == null || block.IsFromExternalReference) continue;
                foreach (ObjectId id in block)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity != null) yield return entity;
                }
            }
        }

        private static string ReadStyleName(Transaction transaction, ObjectId styleId)
        {
            if (styleId.IsNull) return string.Empty;
            try
            {
                DimStyleTableRecord style = transaction.GetObject(styleId, OpenMode.ForRead, false) as DimStyleTableRecord;
                return style == null ? string.Empty : style.Name;
            }
            catch { return string.Empty; }
        }

        private static string Fingerprint(Entity source)
        {
            var text = new StringBuilder();
            text.Append(source.GetType().FullName).Append('|').Append(ReadCurrentScaleName()).Append('|');
            try
            {
                Line line = source as Line;
                if (line != null)
                {
                    AppendPoint(text, line.StartPoint);
                    AppendPoint(text, line.EndPoint);
                }
                else
                {
                    Polyline polyline = source as Polyline;
                    if (polyline != null)
                    {
                        text.Append(polyline.NumberOfVertices).Append('|').Append(polyline.Closed ? '1' : '0').Append('|');
                        for (int index = 0; index < polyline.NumberOfVertices; index++)
                        {
                            AppendPoint(text, polyline.GetPoint3dAt(index));
                            text.Append(polyline.GetBulgeAt(index).ToString("R", CultureInfo.InvariantCulture)).Append('|');
                        }
                    }
                    else
                    {
                        CivilFeatureLine featureLine = source as CivilFeatureLine;
                        if (featureLine != null)
                        {
                            Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                            text.Append(points == null ? 0 : points.Count).Append('|');
                            if (points != null)
                                foreach (Point3d point in points) AppendPoint(text, point);
                        }
                    }
                }
            }
            catch
            {
                text.Append(source.Handle.ToString()).Append('|');
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
                byte[] hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static void AppendPoint(StringBuilder text, Point3d point)
        {
            text.Append(point.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        }

        private static string ReadCurrentScaleName()
        {
            try
            {
                return Convert.ToString(
                    AcApplication.GetSystemVariable("CANNOSCALE"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static ObjectId ResolveHandle(Database database, string handle)
        {
            if (database == null || string.IsNullOrWhiteSpace(handle)) return ObjectId.Null;
            try
            {
                long value;
                if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                    return ObjectId.Null;
                return database.GetObjectId(false, new Handle(value), 0);
            }
            catch { return ObjectId.Null; }
        }

        private static void EraseObject(Transaction transaction, ObjectId id)
        {
            if (id.IsNull) return;
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                if (value != null && !value.IsErased) value.Erase();
            }
            catch { }
        }

        private static void WriteRecord(
            Transaction transaction,
            Entity entity,
            string key,
            TypedValue[] values)
        {
            if (transaction == null || entity == null) return;
            if (entity.ExtensionDictionary.IsNull)
            {
                entity.UpgradeOpen();
                entity.CreateExtensionDictionary();
            }
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;

            Xrecord record;
            if (dictionary.Contains(key))
                record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(values);
        }

        private static bool TryReadRecord(
            Transaction transaction,
            Entity entity,
            string key,
            out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (transaction == null || entity == null || entity.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    entity.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return false;
                values = record.Data.AsArray();
                return values != null && values.Length > 0;
            }
            catch { return false; }
        }

        private static void RemoveRecord(Transaction transaction, Entity entity, string key)
        {
            if (transaction == null || entity == null || entity.ExtensionDictionary.IsNull) return;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    entity.ExtensionDictionary,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return;
                ObjectId recordId = dictionary.GetAt(key);
                dictionary.Remove(key);
                DBObject record = transaction.GetObject(recordId, OpenMode.ForWrite, false);
                if (record != null && !record.IsErased) record.Erase();
            }
            catch { }
        }
    }

    internal sealed class DynamicMultiDimensionSource
    {
        internal ObjectId SourceId { get; set; } = ObjectId.Null;
        internal string SourceHandle { get; set; } = string.Empty;
        internal string Mode { get; set; } = string.Empty;
        internal string StyleName { get; set; } = string.Empty;
        internal double OffsetPaper { get; set; }
        internal double LeaderPaper { get; set; }
        internal string Fingerprint { get; set; } = string.Empty;
        internal List<string> OutputHandles { get; } = new List<string>();
    }
}
