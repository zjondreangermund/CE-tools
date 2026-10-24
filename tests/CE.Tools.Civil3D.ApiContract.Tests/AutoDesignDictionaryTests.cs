using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CETools.Civil3D;
using Autodesk.AutoCAD.DatabaseServices;

internal static class AutoDesignDictionaryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var source = new DBDictionary();
        var first = new ObjectId { Value = new DBObject() };
        var second = new ObjectId { Value = new DBObject() };
        source.Add("Z-last", second); source.Add("A-first", first);
        bool oldCastFailed = false;
        try { source.Cast<DBDictionaryEntry>().ToList(); }
        catch (InvalidCastException) { oldCastFailed = true; }
        check(oldCastFailed, "Fixture must reproduce incompatible boxed dictionary entries.");
        var entries = AutoDesignDictionaryEntries.Read(source);
        check(entries.Count == 2 && entries[0].Key == "A-first" && entries[1].Key == "Z-last", "Read and sort all native entries without LINQ casts.");
        check(entries[0].Value.Equals(first) && entries[1].Value.Equals(second), "Preserve entry ObjectIds.");
        check(source.EnumeratorDisposed, "Dispose the native enumerator.");
        check(AutoDesignDictionaryEntries.Read(new DBDictionary()).Count == 0, "Empty dictionaries are valid.");
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    // Deliberately different typed/native and non-generic enumeration surfaces.
    // This is a contract regression, not an emulation of Autodesk's native runtime.
    public struct DBDictionaryEntry
    {
        public string Key;
        public ObjectId Value;
    }
    public sealed class DBDictionary : IEnumerable
    {
        private readonly Dictionary<string, ObjectId> values = new Dictionary<string, ObjectId>();
        internal bool EnumeratorDisposed;
        public void Add(string key, ObjectId value) { values.Add(key, value); }
        public DbDictionaryEnumerator GetEnumerator() { return new DbDictionaryEnumerator(values, () => EnumeratorDisposed = true); }
        IEnumerator IEnumerable.GetEnumerator() { return values.Select(x => new DictionaryEntry(x.Key, x.Value)).GetEnumerator(); }
    }
    public sealed class DbDictionaryEnumerator : IDisposable
    {
        private readonly IEnumerator<KeyValuePair<string, ObjectId>> iterator;
        private readonly Action disposed;
        internal DbDictionaryEnumerator(Dictionary<string, ObjectId> values, Action disposed)
        { iterator = values.GetEnumerator(); this.disposed = disposed; }
        public bool MoveNext() { return iterator.MoveNext(); }
        public DBDictionaryEntry Entry { get { return new DBDictionaryEntry { Key = iterator.Current.Key, Value = iterator.Current.Value }; } }
        public void Dispose() { iterator.Dispose(); disposed(); }
    }
}
