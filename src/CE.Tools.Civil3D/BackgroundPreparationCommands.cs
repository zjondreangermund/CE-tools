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
    /// Preparation tools for consultant / architectural background drawings before
    /// they are used by Survey and design production. The commands deliberately work
    /// on AutoCAD model-space entities and avoid modifying xref definitions.
    /// </summary>
    public sealed class BackgroundPreparationCommands
    {
        private const string SolidHatchLayer = "CE-BG-SOLID-HATCH-FROZEN";
        private const string DimensionLayer = "CE-BG-DIMENSIONS-FROZEN";

        [CommandMethod("CE_TOOLS", "CE_BACKGROUNDTOOLS", CommandFlags.Modal)]
        public void BackgroundTools()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Background Tools",
                "Prepare imported/background DWGs before Survey Production. Use the cleanup and scale checks before using the drawing as engineering reference data.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("CE-Burst All Blocks", "CE_BGBURSTALL", "Burst ordinary model-space blocks while preserving visible attribute values as text. Xrefs are skipped.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Background Colour 250", "CE_BGCOLOR250", "Set all model-space background entities to AutoCAD colour index 250.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Audit / Overkill / Purge", "CE_BGCLEAN", "Run drawing audit, duplicate-object cleanup and purge in one ordered pass.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Freeze Solid Hatches", "CE_BGFREEZESOLIDHATCH", "Move SOLID hatches to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze Dimensions", "CE_BGFREEZEDIMS", "Move model-space dimensions to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Scale Correction / Convert to Metres", "CE_BGSCALECORRECTION", "Scale the full model-space background to metres using direct mm-to-m conversion or one/two checked reference dimensions.", "03 Scale Correction")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BGBURSTALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BurstAllBlocks()
        {
            Document document = Active();
            if (document == null) return;
            Database database = document.Database;
            int exploded = 0;
            int skipped = 0;
            int attributes = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                // Multiple passes also flatten ordinary nested blocks. The hard limit
                // prevents pathological/self-referencing content from looping forever.
                for (int pass = 0; pass < 20; pass++)
                {
                    int passExploded = 0;
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite, false) as BlockTableRecord;
                        if (model == null) break;
                        List<ObjectId> ids = model.Cast<ObjectId>().ToList();
                        foreach (ObjectId id in ids)
                        {
                            BlockReference block = transaction.GetObject(id, OpenMode.ForWrite, false) as BlockReference;
                            if (block == null) continue;
                            if (IsExternalReference(block, transaction))
                            {
                                skipped++;
                                continue;
                            }

                            try
                            {
                                foreach (ObjectId attributeId in block.AttributeCollection)
                                {
                                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                                    if (attribute == null || attribute.Invisible || string.IsNullOrEmpty(attribute.TextString)) continue;
                                    DBText text = AttributeText(attribute, database);
                                    model.AppendEntity(text);
                                    transaction.AddNewlyCreatedDBObject(text, true);
                                    attributes++;
                                }

                                DBObjectCollection pieces = new DBObjectCollection();
                                block.Explode(pieces);
                                foreach (DBObject value in pieces)
                                {
                                    Entity entity = value as Entity;
                                    if (entity == null || entity is AttributeDefinition)
                                    {
                                        value.Dispose();
                                        continue;
                                    }
                                    model.AppendEntity(entity);
                                    transaction.AddNewlyCreatedDBObject(entity, true);
                                }
                                block.Erase();
                                passExploded++;
                                exploded++;
                            }
                            catch
                            {
                                skipped++;
                            }
                        }
                        transaction.Commit();
                    }
                    if (passExploded == 0) break;
                }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE-Burst All Blocks complete. Blocks burst={0}; attribute values preserved={1}; skipped/xrefs={2}.", exploded, attributes, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_BGCOLOR250", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BackgroundColour250()
        {
            Document document = Active();
            if (document == null) return;
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (entity == null) continue;
                        entity.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
                        changed++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE-Background Colour 250 complete. Entities changed={0}.", changed);
        }

        [CommandMethod("CE_TOOLS", "CE_BGCLEAN", CommandFlags.Modal)]
        public void AuditOverkillPurge()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            int completed = 0;
            try
            {
                editor.Command("_.AUDIT", "_Y");
                completed++;
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCE Background Audit warning: {0}", ex.Message);
            }
            try
            {
                editor.Command("_.-OVERKILL", "_ALL", "", "");
                completed++;
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCE Background Overkill warning: {0}", ex.Message);
            }
            try
            {
                editor.Command("_.-PURGE", "_ALL", "*", "_N");
                completed++;
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCE Background Purge warning: {0}", ex.Message);
            }
            editor.WriteMessage("\nCE-Audit / Overkill / Purge pass complete. Native cleanup stages completed={0}/3.", completed);
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

            const string direct = "Millimetres to metres - scale entire drawing by 0.001";
            const string parking2500 = "Parking reference 2500 -> 2.5 m";
            const string parking5000 = "Parking reference 5000 -> 2.5 m";
            const string wall220 = "External wall reference 220 -> 0.220 m";
            const string wall440 = "External wall reference 440 -> 0.220 m";
            const string custom = "Custom reference - enter correct metre length";
            const string doubleCheck = "Double-check two references before scaling";

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Background Scale Correction",
                "Choose how CE Tools should verify and convert the complete model-space background to metres. Reference modes calculate target metres / measured drawing length. Double-check mode refuses inconsistent reference factors.");
            model.AddChoice("Mode", "01 Scale Method", "Scale correction method", direct, "Use a known parking/wall reference, a custom target, or two independent checks.", new[] { direct, parking2500, parking5000, wall220, wall440, custom, doubleCheck });
            model.AddPositiveDouble("Target1", "02 Reference Checks", "Correct first reference length (m)", 2.5, "Used for Custom and Double-check modes.");
            model.AddPositiveDouble("Target2", "02 Reference Checks", "Correct second reference length (m)", 0.220, "Used only for Double-check mode.");
            model.AddPositiveDouble("Tolerance", "02 Reference Checks", "Maximum factor difference (%)", 2.0, "Double-check mode stops if the two calculated scale factors differ by more than this percentage.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string mode = model.Text("Mode");
            double factor;
            string diagnostic;
            if (string.Equals(mode, direct, StringComparison.OrdinalIgnoreCase))
            {
                factor = 0.001;
                diagnostic = "Direct millimetres-to-metres conversion";
            }
            else if (string.Equals(mode, doubleCheck, StringComparison.OrdinalIgnoreCase))
            {
                double measured1;
                double measured2;
                if (!TryPickReferenceLength(document, "Select first reference line/dimension", out measured1)) return;
                if (!TryPickReferenceLength(document, "Select second reference line/dimension", out measured2)) return;
                double factor1 = model.Double("Target1", 2.5) / measured1;
                double factor2 = model.Double("Target2", 0.220) / measured2;
                double average = (Math.Abs(factor1) + Math.Abs(factor2)) * 0.5;
                double differencePercent = average <= 1e-12 ? double.MaxValue : Math.Abs(factor1 - factor2) / average * 100.0;
                double tolerance = model.Double("Tolerance", 2.0);
                if (differencePercent > tolerance)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(CultureInfo.InvariantCulture, "The two reference checks do not agree.\n\nFactor 1: {0:0.########}\nFactor 2: {1:0.########}\nDifference: {2:0.###}%\nAllowed: {3:0.###}%\n\nThe drawing was NOT scaled.", factor1, factor2, differencePercent, tolerance),
                        "CE Tools - Scale Check Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                factor = (factor1 + factor2) * 0.5;
                diagnostic = string.Format(CultureInfo.InvariantCulture, "Two-reference verification: {0:0.###}% factor agreement", differencePercent);
            }
            else
            {
                double target;
                double expected;
                if (string.Equals(mode, parking2500, StringComparison.OrdinalIgnoreCase)) { target = 2.5; expected = 2500.0; }
                else if (string.Equals(mode, parking5000, StringComparison.OrdinalIgnoreCase)) { target = 2.5; expected = 5000.0; }
                else if (string.Equals(mode, wall220, StringComparison.OrdinalIgnoreCase)) { target = 0.220; expected = 220.0; }
                else if (string.Equals(mode, wall440, StringComparison.OrdinalIgnoreCase)) { target = 0.220; expected = 440.0; }
                else { target = model.Double("Target1", 2.5); expected = 0.0; }

                double measured;
                if (!TryPickReferenceLength(document, "Select reference line/dimension", out measured)) return;
                factor = target / measured;
                diagnostic = string.Format(CultureInfo.InvariantCulture, "Reference measured {0:0.###}; target {1:0.###} m", measured, target);
                if (expected > 0.0)
                {
                    double deviation = Math.Abs(measured - expected) / expected * 100.0;
                    if (deviation > 10.0 && !DisciplineWorkflowDialogs.Confirm(
                        "CE Tools - Reference Check",
                        string.Format(CultureInfo.InvariantCulture, "Selected reference measures {0:0.###}, but this method expects approximately {1:0.###}.\n\nCalculated scale factor is {2:0.########}. Continue anyway?", measured, expected, factor)))
                        return;
                }
            }

            if (!IsReasonableScale(factor))
            {
                System.Windows.MessageBox.Show("Calculated scale factor is invalid or extreme. The drawing was not scaled.", "CE Tools - Scale Correction", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!DisciplineWorkflowDialogs.Confirm(
                "CE Tools - Scale Entire Drawing",
                string.Format(CultureInfo.InvariantCulture, "{0}\n\nScale factor: {1:0.########}\n\nScale ALL model-space entities about 0,0,0 and set drawing insertion units to metres?", diagnostic, factor)))
                return;

            ApplyModelSpaceScale(document, factor, diagnostic);
        }

        private static void MoveToFrozenLayer(string layerName, Func<Entity, bool> predicate, string description)
        {
            Document document = Active();
            if (document == null) return;
            int moved = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (entity == null || !predicate(entity)) continue;
                        entity.LayerId = layerId;
                        moved++;
                    }
                }
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                if (layer != null) layer.IsFrozen = true;
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE Background Tools: moved {0} {1}(s) to frozen layer {2}.", moved, description, layerName);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord();
            layer.Name = name;
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TryPickReferenceLength(Document document, string prompt, out double length)
        {
            length = 0.0;
            PromptEntityOptions options = new PromptEntityOptions("\n" + prompt + ": ");
            options.SetRejectMessage("\nSelect a line/polyline/arc/spline or dimension with a measurable length.");
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
                        document.Editor.WriteMessage("\nSelected object is not a measurable curve/dimension.");
                        return false;
                    }
                    try
                    {
                        length = Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam));
                    }
                    catch
                    {
                        try { length = curve.StartPoint.DistanceTo(curve.EndPoint); }
                        catch { length = 0.0; }
                    }
                }
                transaction.Commit();
            }
            if (length <= 1e-9 || double.IsNaN(length) || double.IsInfinity(length))
            {
                document.Editor.WriteMessage("\nReference length could not be measured.");
                return false;
            }
            document.Editor.WriteMessage("\nReference length measured: {0:0.###}", length);
            return true;
        }

        private static void ApplyModelSpaceScale(Document document, double factor, string diagnostic)
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
                        Entity entity = null;
                        try
                        {
                            entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity == null) { skipped++; continue; }
                            entity.TransformBy(transform);
                            scaled++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }
                document.Database.Insunits = UnitsValue.Meters;
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE Background scale correction complete. {0}. Factor={1:0.########}; scaled={2}; skipped={3}; INSUNITS=metres.", diagnostic, factor, scaled, skipped);
        }

        private static bool IsExternalReference(BlockReference block, Transaction transaction)
        {
            try
            {
                BlockTableRecord definition = transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                return definition != null && (definition.IsFromExternalReference || definition.IsFromOverlayReference);
            }
            catch { return true; }
        }

        private static DBText AttributeText(AttributeReference attribute, Database database)
        {
            DBText text = new DBText();
            text.SetDatabaseDefaults(database);
            text.TextString = attribute.TextString ?? string.Empty;
            text.Position = attribute.Position;
            text.Height = attribute.Height > 1e-9 ? attribute.Height : Math.Max(database.Textsize, 0.001);
            text.Rotation = attribute.Rotation;
            text.Oblique = attribute.Oblique;
            text.WidthFactor = attribute.WidthFactor;
            text.TextStyleId = attribute.TextStyleId;
            text.LayerId = attribute.LayerId;
            text.Color = attribute.Color;
            text.HorizontalMode = attribute.HorizontalMode;
            text.VerticalMode = attribute.VerticalMode;
            if (attribute.HorizontalMode != TextHorizontalMode.TextLeft || attribute.VerticalMode != TextVerticalMode.TextBase)
                text.AlignmentPoint = attribute.AlignmentPoint;
            return text;
        }

        private static bool IsReasonableScale(double factor)
        {
            return factor > 1e-9 && factor < 1e9 && !double.IsNaN(factor) && !double.IsInfinity(factor);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
