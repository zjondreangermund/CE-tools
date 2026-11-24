using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>Temporarily permits hatch/geometry updates; restores layer state even on failure.</summary>
    internal sealed class SheetCloneLayerScope : IDisposable
    {
        private readonly List<Action> restore = new List<Action>();
        internal SheetCloneLayerScope(Database database, Transaction transaction)
        {
            try
            {
                var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForWrite);
                    bool locked = layer.IsLocked, frozen = layer.IsFrozen;
                    restore.Add(() => { layer.IsFrozen = frozen; layer.IsLocked = locked; });
                    layer.IsLocked = false;
                    if (frozen) layer.IsFrozen = false;
                }
            }
            catch { Dispose(); throw; }
        }

        // Source layers have already been restored when this is called. New cloned
        // layers must inherit those original flags, not the temporary unlocked flags.
        internal SheetCloneLayerScope(Transaction source, Transaction destination, IdMapping mapping)
        {
            try
            {
                foreach (IdPair pair in mapping)
                {
                    if (pair.Value.IsNull || pair.Value.IsErased) continue;
                    var original = source.GetObject(pair.Key, OpenMode.ForRead) as LayerTableRecord;
                    if (original == null) continue;
                    var clone = (LayerTableRecord)destination.GetObject(pair.Value, OpenMode.ForWrite);
                    bool locked = pair.IsCloned ? original.IsLocked : clone.IsLocked;
                    bool frozen = pair.IsCloned ? original.IsFrozen : clone.IsFrozen;
                    restore.Add(() => { clone.IsFrozen = frozen; clone.IsLocked = locked; });
                    clone.IsLocked = false;
                    if (clone.IsFrozen) clone.IsFrozen = false;
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            // No swallowed restoration errors: the enclosing transaction must roll back.
            try { for (int i = restore.Count - 1; i >= 0; --i) restore[i](); }
            finally { restore.Clear(); }
        }
    }
}
