using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NetworkPartLockCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch lock/unlock for editable gravity-network pipes and structures.
    /// Uses the lock members exposed by the installed Civil 3D build and never
    /// locks AutoCAD layers as a substitute.
    /// </summary>
    public sealed class NetworkPartLockCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKPARTLOCKS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LockUnlockAll()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Lock / Unlock Pipes and Structures",
                "Lock or unlock all editable Civil 3D gravity-network pipes and structures in the current drawing. Layers are not locked or unlocked.");
            model.AddChoice(
                "Action", "01 Network parts", "Action", "Lock all",
                "Apply the same lock state to every pipe and structure.",
                new[] { "Lock all", "Unlock all" });
            model.AddChoice(
                "Parts", "01 Network parts", "Part types", "Pipes and structures",
                "Process both part types, pipes only, or structures only.",
                new[] { "Pipes and structures", "Pipes only", "Structures only" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            bool locked = string.Equals(
                model.Text("Action"), "Lock all", StringComparison.OrdinalIgnoreCase);
            string scope = model.Text("Parts") ?? string.Empty;
            bool pipes = !scope.StartsWith("Structures", StringComparison.OrdinalIgnoreCase);
            bool structures = !scope.StartsWith("Pipes only", StringComparison.OrdinalIgnoreCase);

            int changed = 0;
            int already = 0;
            int unsupported = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (modelSpace == null) return;

                foreach (ObjectId id in modelSpace)
                {
                    if (id.IsNull || id.IsErased) continue;
                    DBObject value = null;
                    try { value = tr.GetObject(id, OpenMode.ForRead, false); }
                    catch { failed++; continue; }
                    if (value == null) continue;

                    string typeName = value.GetType().Name ?? string.Empty;
                    bool isStructure =
                        typeName.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isPipe =
                        !isStructure &&
                        typeName.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        typeName.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("Style", StringComparison.OrdinalIgnoreCase) < 0;
                    if ((!pipes || !isPipe) && (!structures || !isStructure))
                        continue;

                    try
                    {
                        value.UpgradeOpen();
                        bool? previous;
                        if (!TrySetLock(value, locked, out previous))
                        {
                            unsupported++;
                            continue;
                        }
                        if (previous.HasValue && previous.Value == locked) already++;
                        else changed++;
                        try
                        {
                            Entity entity = value as Entity;
                            if (entity != null) entity.RecordGraphicsModified(true);
                        }
                        catch { }
                    }
                    catch
                    {
                        failed++;
                    }
                }
                tr.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_NETWORKPARTLOCKS complete. State={0}; changed={1}; already={2}; unsupported={3}; failed={4}.",
                locked ? "LOCKED" : "UNLOCKED",
                changed, already, unsupported, failed);
        }

        private static bool TrySetLock(DBObject value, bool requested, out bool? previous)
        {
            previous = null;
            if (value == null) return false;

            foreach (string name in new[]
            {
                "IsLocked",
                "Locked",
                "IsPositionLocked",
                "PositionLocked",
                "IsGeometryLocked",
                "GeometryLocked"
            })
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null || property.PropertyType != typeof(bool) || !property.CanWrite)
                    continue;
                try
                {
                    if (property.CanRead)
                        previous = (bool)property.GetValue(value, null);
                    property.SetValue(value, requested, null);
                    return true;
                }
                catch { }
            }

            string[] methods = requested
                ? new[] { "Lock", "LockPosition", "LockGeometry" }
                : new[] { "Unlock", "UnlockPosition", "UnlockGeometry" };
            foreach (string name in methods)
            {
                MethodInfo method = value.GetType().GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null) continue;
                try
                {
                    method.Invoke(value, null);
                    return true;
                }
                catch { }
            }

            return false;
        }
    }
}
