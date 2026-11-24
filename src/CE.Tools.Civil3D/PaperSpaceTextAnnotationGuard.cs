#if CE_CIVIL3D_2024
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PaperSpaceTextAnnotationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Protect title blocks in Civil 3D 2024. Never rewrite model-space geometry,
    /// nested block definitions, annotation scale lists, text values or heights.
    /// Repair is available only through the explicit CE_PAPERTEXTANNOOFF command.
    /// </summary>
    public sealed class PaperSpaceTextAnnotationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PAPERTEXTANNOOFF",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RepairActiveDrawing()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PaperSpaceTextAnnotationGuard.Repair(doc, true);
        }
    }

    internal static class PaperSpaceTextAnnotationGuard
    {
        // Manual command only. Opening/activating a drawing must not alter
        // annotative flags, contexts, heights, styles or any saved appearance.
        // Keep these hooks for compatibility with existing installer patches.
        internal static void Initialize() { }
        internal static void Terminate() { }

        internal static void Repair(Document document, bool report)
        {
            if (document == null) return;
            int changed = RepairCore(document, report);
            if (report)
                document.Editor.WriteMessage(
                    "\nCE_PAPERTEXTANNOOFF finished: changed={0}; " +
                    "only paper-space TEXT, MTEXT, block attributes and MLeader " +
                    "were checked. Drawing must be saved to persist.", changed);
        }

        private static int RepairCore(Document document, bool manual)
        {
            int changed = 0;
            int unsupported = 0;
            using (DocumentLock hold = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable table = tr.GetObject(
                    document.Database.BlockTableId,
                    OpenMode.ForRead, false) as BlockTable;
                if (table == null) return 0;
                foreach (ObjectId blockId in table)
                {
                    BlockTableRecord paper;
                    try
                    {
                        paper = tr.GetObject(blockId, OpenMode.ForRead, false) as
                            BlockTableRecord;
                    }
                    catch { continue; }
                    if (paper == null || !paper.IsLayout ||
                        string.Equals(paper.Name, BlockTableRecord.ModelSpace,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (ObjectId id in paper)
                    {
                        try
                        {
                            Entity entity = tr.GetObject(
                                id, OpenMode.ForRead, false) as Entity;
                            if (entity == null) continue;
                            if (entity is DBText || entity is MText ||
                                entity is MLeader)
                            {
                                if (TurnOffAnnotative(entity, tr, ref unsupported))
                                    changed++;
                            }
                            else
                            {
                                BlockReference reference = entity as BlockReference;
                                if (reference == null) continue;
                                // Attribute references belong to this paper-space
                                // INSERT. Never edit the globally shared block
                                // definition, which may also be used in model space.
                                foreach (ObjectId attributeId in reference.AttributeCollection)
                                {
                                    AttributeReference attribute = tr.GetObject(
                                        attributeId, OpenMode.ForRead, false) as
                                        AttributeReference;
                                    if (attribute != null &&
                                        TurnOffAnnotative(attribute, tr, ref unsupported))
                                        changed++;
                                }
                            }
                        }
                        catch
                        {
                            unsupported++;
                        }
                    }
                }
                tr.Commit();
            }
            if (manual && unsupported > 0)
                document.Editor.WriteMessage(
                    "\nCE_PAPERTEXTANNOOFF skipped {0} inaccessible or unsupported " +
                    "objects. Nested block definitions are intentionally unchanged.",
                    unsupported);
            return changed;
        }

        private static bool TurnOffAnnotative(
            Entity entity, Transaction transaction, ref int unsupported)
        {
            PropertyInfo prop = entity.GetType().GetProperty(
                "Annotative", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead || !prop.CanWrite)
            {
                unsupported++;
                return false;
            }
            object value = prop.GetValue(entity, null);
            object off = null;
            if (prop.PropertyType == typeof(bool))
                off = false;
            else if (prop.PropertyType.IsEnum)
            {
                foreach (string candidate in new[] { "False", "No", "Off" })
                {
                    if (Array.IndexOf(
                        Enum.GetNames(prop.PropertyType), candidate) >= 0)
                    {
                        off = Enum.Parse(prop.PropertyType, candidate);
                        break;
                    }
                }
            }
            if (off == null)
            {
                unsupported++;
                return false;
            }
            if (Equals(value, off)) return false;
            entity.UpgradeOpen();
            prop.SetValue(entity, off, null);
            return true;
        }
    }
}
#endif
