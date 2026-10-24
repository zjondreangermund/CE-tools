using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>Conservative session revision, without opening or fingerprinting drawing objects.</summary>
    internal sealed class AutoDesignDrawingChanges
    {
        private static readonly ConditionalWeakTable<Database, AutoDesignDrawingChanges> Sessions =
            new ConditionalWeakTable<Database, AutoDesignDrawingChanges>();
        private readonly string session = Guid.NewGuid().ToString("N");
        private long revision;
        private int checkpointDepth;

        private AutoDesignDrawingChanges(Database database)
        {
            database.ObjectModified += Changed;
            database.ObjectAppended += Changed;
            database.ObjectErased += Erased;
        }

        internal static AutoDesignDrawingChanges For(Database database)
        {
            return Sessions.GetValue(database, db => new AutoDesignDrawingChanges(db));
        }

        internal string Stamp
        {
            get { return "CE_AUTODESIGN_SESSION_2:" + session + ":" + Interlocked.Read(ref revision).ToString(CultureInfo.InvariantCulture); }
        }

        internal IDisposable IgnoreCheckpointWrites()
        {
            Interlocked.Increment(ref checkpointDepth);
            return new CheckpointScope(this);
        }

        private void Changed(object sender, ObjectEventArgs args) { MarkChanged(); }
        private void Erased(object sender, ObjectErasedEventArgs args) { MarkChanged(); }
        private void MarkChanged()
        {
            // Event callbacks never open DBObjects, start transactions, write or refresh.
            if (Volatile.Read(ref checkpointDepth) == 0) Interlocked.Increment(ref revision);
        }

        private sealed class CheckpointScope : IDisposable
        {
            private AutoDesignDrawingChanges owner;
            internal CheckpointScope(AutoDesignDrawingChanges owner) { this.owner = owner; }
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                if (current != null) Interlocked.Decrement(ref current.checkpointDepth);
            }
        }
    }
}
