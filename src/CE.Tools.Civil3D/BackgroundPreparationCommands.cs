using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BackgroundPreparationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey-preparation operations for architectural/consultant background DWGs.
    /// The existing CE_BACKGROUNDTOOLS command remains the Background/XREF manager;
    /// this class adds the requested preparation/scale workflow without duplicating
    /// that established command owner.
    /// </summary>
    public sealed class BackgroundPreparationCommands
    {
        private const string SolidHatchLayer = "CE-BG-SOLID-HATCH-FROZEN";
        private const string DimensionLayer = "CE-BG-DIMENSIONS-FROZEN";

        [CommandMethod("CE_TOOLS", "CE_BACKGROUNDPREPTOOLS", CommandFlags.Modal)]
        public void BackgroundPreparationTools()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Background Tools",
                "Prepare imported/background DWGs before Survey Production. Cleanup and scale correction act on model-space background content; the established Background/XREF manager remains available below.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("CE-Burst All Blocks", "CE_BGBURSTALL", "Burst ordinary model-space blocks while preserving visible attribute values as text. XREFs are skipped.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Background Colour 250", "CE_BGCOLOR250", "Set model-space background entities to AutoCAD colour index 250.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Audit / Overkill / Purge", "CE_BGCLEAN", "Run drawing audit, duplicate-object cleanup and purge in an ordered native-command pass.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Freeze Solid Hatches", "CE_BGFREEZESOLIDHATCH", "Move SOLID hatches to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze Dimensions", "CE_BGFREEZEDIMS", "Move model-space dimensions to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Scale Correction / Convert to Metres", "CE_BGSCALECORRECTION", "Scale the complete model-space background to metres using direct mm-to-m conversion or verified reference lengths.", "03 Scale Correction"),
                    new DisciplineWorkflowAction("CE-Existing Background / XREF Utilities", "CE_BACKGROUNDTOOLS", "Open the existing CE background audit/light/XREF split/info/backup workflow.", "04 Existing CE Tools")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BGBURSTALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BurstAllBlocks()
        {
            Document document = Active();
            if (document == null) return;
            if (!DisciplineWorkflowDialogs.Confirm(
                    "CE Tools - Burst All Blocks",
                    "Burst all ordinary model-space block references? XREF references will be skipped. Visible attribute values will be retained as DBText."))
                return;

            int exploded = 0;
            int attributes = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                // Repeated passes flatten nested ordinary blocks without touching XREFs.
                for (int pass = 0; pass < 20; pass++)
                {
                    int changedThisPass = 0;
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord model = transaction.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (model == null) break;
                        List<ObjectId> ids = model.Cast<ObjectId>().ToList();
                        foreach (ObjectId id in ids)
                        {
                            BlockReference block = null;
                            try { block = transaction.GetObject(id, OpenMode.ForWrite, false) as BlockReference; }
                            catch { skipped++; continue; }
                            if (block == null) continue;
                            if (IsXref(block, transaction)) { skipped++; continue; }
                            try
                            {
                                foreach (ObjectId attributeId in block.AttributeCollection)
                                {
                                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                                    if (attribute == null || attribute.Invisible || string.IsNullOrWhiteSpace(attribute.TextString)) continue;
                                    DBText text = new DBText();
                                    text.SetDatabaseDefaults(document.Database);
                                    text.TextString = attribute.TextString;
                                    text.Position = attribute.Position;
                                    text.Height = attribute.Height > 1e-9 ? attribute.Height : Math.Max(document.Database.Textsize, 0.001);
                                    text.Rotation = attribute.Rotation;
                                    text.TextStyleId = attribute.TextStyleId;
                                    text.LayerId = attribute.LayerId;
                                    text.Color = attribute.Color;
                                    model.AppendEntity(text);
                                    transaction.AddNewlyCreatedDBObject(text, true);
                                    attributes++;
                                }

                                DBObjectCollection pieces = new DBObjectCollection();
                                block.Explode(pieces);
                                foreach (DBObject item in pieces)
                                {
                                    Entity entity = item as Entity;
                                    if (entity == null || entity is AttributeDefinition)
                                    {
                                        item.Dispose();
                                        continue;
                                    }
                                    model.AppendEntity(entity);
                                    transaction.AddNewlyCreatedDBObject(entity, true);
                                }
                                block.Erase();
                                exploded++;
                                changedThisPass++;
                            }
                            catch { skipped++; }
                        }
                        transaction.Commit();
                    }
                    if (changedThisPass == 0) break;
                }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BGBURSTALL complete. Blocks burst={0}; attribute values retained={1}; skipped/xrefs={2}.", exploded, attributes, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_BGCOLOR250", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BackgroundColour250()
        {
            Document document = Active();
            if (document == null) return;
            int changed = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity == null) { skipped++; continue; }
                            entity.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
                            changed++;
                        }
                        catch { skipped++; }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BGCOLOR250 complete. Changed={0}; skipped={1}.", changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_BGCLEAN", CommandFlags.Modal)]
        public void AuditOverkillPurge()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            int completed = 0;
            try { editor.Command("_.AUDIT", "_Y"); completed++; }
            catch (System.Exception ex) { editor.WriteMessage("\nAudit warning: {0}", ex.Message); }
            try { editor.Command("_.-OVERKILL", "_ALL", "", ""); completed++; }
            catch (System.Exception ex) { editor.WriteMessage("\nOverkill warning: {0}", ex.Message); }
            try { editor.Command("_.-PURGE", "_ALL", "*", "_N"); completed++; }
            catch (System.Exception ex) { editor.WriteMessage("\nPurge warning: {0}", ex.Message); }
            editor.WriteMessage("\nCE_BGCLEAN complete. Native cleanup stages completed={0}/3.", completed);
        }

        [CommandMethod("CE_TOOLS", "CE_BGFREEZESOLIDHATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeSolidHatches()
        {
            MoveToFrozenLayer(
                SolidHatchLayer,
                delegate(Entity entity)
                {
                    Hatch hatch = entity as Hatch;
                    return hatch != null && string.Equals(hatch.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);
                },
                "solid hatch");
        }

        [CommandMethod("CE_TOOLS", "CE_BGFREEZEDIMS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeDimensions()
        {
            MoveToFrozenLayer(DimensionLayer, delegate(Entity entity) { return entity is Dimension; }, "dimension");
        }

        [CommandMethod("CE_TOOLS", "CE_BGSCALECORRECTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ScaleCorrection()
        {
            Document document = Active();
            if (document == null) return;

            const string direct = "Millimetres to metres - scale by 0.001";
            const string parking2500 = "Parking reference 2500 -> 2.5 m";
            const string parking5000 = "Parking reference 5000 -> 2.5 m";
            const string wall220 = "External wall reference 220 -> 0.220 m";
            const string wall440 = "External wall reference 440 -> 0.220 m";
            const string custom = "Custom reference target in metres";
            const string doubleCheck = "Double-check two reference lengths";

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Background Scale Correction",
                "Convert the complete model-space background to metres. Reference modes use target metres divided by the measured drawing length; double-check mode refuses inconsistent scale factors.");
            settings.AddChoice("Mode", "01 Scale Method", "Scale correction method", direct, "Choose direct mm-to-m conversion or verify scale with known parking/wall/reference dimensions.", new[] { direct, parking2500, parking5000, wall220, wall440, custom, doubleCheck });
            settings.AddPositiveDouble("Target1", "02 Verification", "Correct first reference length (m)", 2.5, "Used for Custom and Double-check modes.");
            settings.AddPositiveDouble("Target2", "02 Verification", "Correct second reference length (m)", 0.220, "Used for Double-check mode.");
            settings.AddPositiveDouble("Tolerance", "02 Verification", "Maximum factor difference (%)", 2.0, "Double-check mode stops if the two calculated factors differ by more than this percentage.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string mode = settings.Text("Mode");
            double factor;
            string note;
            if (string.Equals(mode, direct, StringComparison.OrdinalIgnoreCase))
            {
                factor = 0.001;
                note = "Direct millimetres-to-metres conversion";
            }
            else if (string.Equals(mode, doubleCheck, StringComparison.OrdinalIgnoreCase))
            {
                double measured1;
                double measured2;
                if (!TryPickLength(document, "Select first reference line/dimension", out measured1)) return;
                if (!TryPickLength(document, "Select second reference line/dimension", out measured2)) return;
                double factor1 = settings.Double("Target1", 2.5) / measured1;
                double factor2 = settings.Double("Target2", 0.220) / measured2;
                double average = (Math.Abs(factor1) + Math.Abs(factor2)) * 0.5;
                double difference = average <= 1e-12 ? double.MaxValue : Math.Abs(factor1 - factor2) / average * 100.0;
                double tolerance = settings.Double("Tolerance", 2.0);
                if (difference > tolerance)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(CultureInfo.InvariantCulture, "Reference checks disagree.\n\nFactor 1: {0:0.########}\nFactor 2: {1:0.########}\nDifference: {2:0.###}%\nAllowed: {3:0.###}%\n\nThe drawing was NOT scaled.", factor1, factor2, difference, tolerance),
                        "CE Tools - Scale Check Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                factor = (factor1 + factor2) * 0.5;
                note = string.Format(CultureInfo.InvariantCulture, "Two-reference verification; factor difference {0:0.###}%", difference);
            }
            else
            {
                double target;
                double expected;
                if (string.Equals(mode, parking2500, StringComparison.OrdinalIgnoreCase)) { target = 2.5; expected = 2500.0; }
                else if (string.Equals(mode, parking5000, StringComparison.OrdinalIgnoreCase)) { target = 2.5; expected = 5000.0; }
                else if (string.Equals(mode, wall220, StringComparison.OrdinalIgnoreCase)) { target = 0.220; expected = 220.0; }
                else if (string.Equals(mode, wall440, StringComparison.OrdinalIgnoreCase)) { target = 0.220; expected = 440.0; }
                else { target = settings.Double("Target1", 2.5); expected = 0.0; }

                double measured;
                if (!TryPickLength(document, "Select reference line/dimension", out measured)) return;
                factor = target / measured;
                note = string.Format(CultureInfo.InvariantCulture, "Reference measured {0:0.###}; target {1:0.###} m", measured, target);
                if (expected > 0.0)
                {
                    double deviation = Math.Abs(measured - expected) / expected * 100.0;
                    if (deviation > 10.0 && !DisciplineWorkflowDialogs.Confirm(
                        "CE Tools - Reference Check",
                        string.Format(CultureInfo.InvariantCulture, "Selected reference measures {0:0.###}, while this method normally expects approximately {1:0.###}.\nCalculated factor: {2:0.########}\n\nContinue?", measured, expected, factor)))
                        return;
                }
            }

            if (factor <= 1e-9 || factor >= 1e9 || double.IsNaN(factor) || double.IsInfinity(factor))
            {
                document.Editor.WriteMessage("\nCE_BGSCALECORRECTION stopped: invalid scale factor.");
                return;
            }
            if (!DisciplineWorkflowDialogs.Confirm(
                "CE Tools - Scale Entire Drawing",
                string.Format(CultureInfo.InvariantCulture, "{0}\n\nScale factor: {1:0.########}\n\nScale ALL model-space entities about 0,0,0 and set drawing insertion units to metres?", note, factor)))
                return;

            ApplyScale(document, factor, note);
        }

        private static void MoveToFrozenLayer(string layerName, Func<Entity, bool> predicate, string description)
        {
            Document document = Active();
            if (document == null) return;
            int moved = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity == null || !predicate(entity)) continue;
                            entity.LayerId = layerId;
                            moved++;
                        }
                        catch { skipped++; }
                    }
                }
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                if (layer != null) layer.IsFrozen = true;
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nMoved {0} {1}(s) to frozen layer {2}; skipped={3}.", moved, description, layerName, skipped);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord();
            layer.Name = name;
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TryPickLength(Document document, string message, out double length)
        {
            length = 0.0;
            PromptEntityOptions options = new PromptEntityOptions("\n" + message + ": ");
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                Dimension dimension = entity as Dimension;
                if (dimension != null)
                {
                    length = Math.Abs(dimension.Measurement);
                }
                else
                {
                    Curve curve = entity as Curve;
                    if (curve == null)
                    {
                        document.Editor.WriteMessage("\nSelect a measurable curve or dimension.");
                        return false;
                    }
                    try { length = Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); }
                    catch
                    {
                        try { length = curve.StartPoint.DistanceTo(curve.EndPoint); }
                        catch { length = 0.0; }
                    }
                }
            }
            if (length <= 1e-9 || double.IsNaN(length) || double.IsInfinity(length))
            {
                document.Editor.WriteMessage("\nReference length could not be measured.");
                return false;
            }
            document.Editor.WriteMessage("\nReference length measured: {0:0.###}", length);
            return true;
        }

        private static void ApplyScale(Document document, double factor, string note)
        {
            int scaled = 0;
            int skipped = 0;
            Matrix3d transform = Matrix3d.Scaling(factor, Point3d.Origin);
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                    {
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity == null) { skipped++; continue; }
                            entity.TransformBy(transform);
                            scaled++;
                        }
                        catch { skipped++; }
                    }
                }
                document.Database.Insunits = UnitsValue.Meters;
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BGSCALECORRECTION complete. {0}. Factor={1:0.########}; scaled={2}; skipped={3}; insertion units=metres.", note, factor, scaled, skipped);
        }

        private static bool IsXref(BlockReference block, Transaction transaction)
        {
            try
            {
                BlockTableRecord definition = transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                return definition != null && (definition.IsFromExternalReference || definition.IsFromOverlayReference);
            }
            catch { return true; }
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
