using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcTransparency = Autodesk.AutoCAD.Colors.Transparency;

[assembly: CommandClass(typeof(CETools.Civil3D.HatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Fast, controlled hatch creation and editing for civil plan, profile and
    /// section drawings. The commands extend AutoCAD's hatch workflow rather than
    /// replacing or duplicating the native HATCH command.
    /// </summary>
    public sealed class HatchCommands
    {
        private const string HatchLayerName = "CE-HATCH";
        private const string DefaultPattern = "ANSI31";
        private const double DefaultScale = 1.0;
        private const int DefaultColour = 8;
        private const int DefaultTransparency = 60;

        [CommandMethod(
            "CE_TOOLS",
            "CE_HATCHTOOLS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void HatchToolsMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCE Hatch tool [Create/Edit/Match/Back] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Create");
            options.Keywords.Add("Edit");
            options.Keywords.Add("Match");
            options.Keywords.Add("Back");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Create"
                : result.StringResult;

            if (mode.Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                Edit(document);
            }
            else if (mode.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                Match(document);
            }
            else if (mode.Equals("Back", StringComparison.OrdinalIgnoreCase))
            {
                SendToBack(document);
            }
            else
            {
                Create(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_HATCHCREATE",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void CreateCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Create(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_HATCHEDIT",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void EditCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Edit(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_HATCHMATCH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void MatchCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Match(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_HATCHBACK",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void BackCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                SendToBack(document);
            }
        }

        private static void Create(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect closed boundaries to hatch: ",
                true);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
            {
                return;
            }

            List<ObjectId> boundaryIds;
            int unsupported;
            using (Transaction transaction =
                   document.Database.TransactionManager.StartTransaction())
            {
                boundaryIds = ReadSupportedBoundaries(
                    selection.Value.GetObjectIds(),
                    transaction,
                    out unsupported);
            }

            if (boundaryIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_HATCHCREATE: no supported closed boundaries were selected.");
                return;
            }

            HatchVisualSettings settings;
            if (!PromptForSettings(
                    editor,
                    new HatchVisualSettings(
                        HatchPatternType.PreDefined,
                        DefaultPattern,
                        DefaultScale,
                        0.0,
                        DefaultColour,
                        DefaultTransparency,
                        HatchStyle.Normal),
                    out settings))
            {
                return;
            }

            editor.WriteMessage(
                "\nCE_HATCHCREATE preview: boundaries={0}; unsupported={1}; pattern={2}; scale={3:N3}; angle={4:N2} deg; colour={5}; transparency={6}%; layer={7}; associative=Yes; draw order=Back.",
                boundaryIds.Count,
                unsupported,
                settings.PatternName,
                settings.PatternScale,
                RadiansToDegrees(settings.PatternAngle),
                settings.ColourIndex,
                settings.TransparencyPercent,
                HatchLayerName);

            if (!Confirm(editor, "Create these transparent associative hatches"))
            {
                editor.WriteMessage(
                    "\nCE_HATCHCREATE cancelled. No hatches were created.");
                return;
            }

            try
            {
                int created = 0;
                int skippedLocked = 0;
                using (Transaction transaction =
                       document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId hatchLayerId = GetOrCreateHatchLayer(
                        document.Database,
                        transaction);
                    var createdByOwner = new Dictionary<ObjectId, ObjectIdCollection>();

                    foreach (ObjectId boundaryId in boundaryIds)
                    {
                        Entity boundary = transaction.GetObject(
                            boundaryId,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (boundary == null ||
                            !IsSupportedBoundary(boundary) ||
                            IsLayerLocked(transaction, boundary.LayerId))
                        {
                            skippedLocked++;
                            continue;
                        }

                        BlockTableRecord owner = transaction.GetObject(
                            boundary.OwnerId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (owner == null)
                        {
                            throw new InvalidOperationException(
                                "A selected boundary is not owned by a writable drawing space.");
                        }

                        var hatch = new Hatch();
                        hatch.SetDatabaseDefaults(document.Database);
                        hatch.LayerId = hatchLayerId;
                        hatch.Color = AcColor.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                            (short)settings.ColourIndex);
                        hatch.Transparency = ToTransparency(
                            settings.TransparencyPercent);
                        owner.AppendEntity(hatch);
                        transaction.AddNewlyCreatedDBObject(hatch, true);

                        hatch.SetHatchPattern(
                            settings.PatternType,
                            settings.PatternName);
                        if (!IsSolid(settings.PatternName))
                        {
                            hatch.PatternScale = settings.PatternScale;
                            hatch.PatternAngle = settings.PatternAngle;
                        }
                        hatch.HatchStyle = settings.HatchStyle;
                        hatch.Associative = true;

                        var loopIds = new ObjectIdCollection();
                        loopIds.Add(boundaryId);
                        hatch.AppendLoop(HatchLoopTypes.Outermost, loopIds);
                        hatch.EvaluateHatch(true);

                        ObjectIdCollection ownerIds;
                        if (!createdByOwner.TryGetValue(owner.ObjectId, out ownerIds))
                        {
                            ownerIds = new ObjectIdCollection();
                            createdByOwner.Add(owner.ObjectId, ownerIds);
                        }
                        ownerIds.Add(hatch.ObjectId);
                        created++;
                    }

                    foreach (KeyValuePair<ObjectId, ObjectIdCollection> pair in createdByOwner)
                    {
                        BlockTableRecord owner = transaction.GetObject(
                            pair.Key,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                        if (owner == null || owner.DrawOrderTableId.IsNull)
                        {
                            continue;
                        }

                        DrawOrderTable drawOrder = transaction.GetObject(
                            owner.DrawOrderTableId,
                            OpenMode.ForWrite,
                            false) as DrawOrderTable;
                        drawOrder?.MoveToBottom(pair.Value);
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_HATCHCREATE complete. Hatches created: {0}; locked or unavailable boundaries skipped: {1}. Transparency display and plot transparency must be enabled to see/plot the effect.",
                    created,
                    skippedLocked);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HATCHCREATE cancelled. No changes were committed. " +
                    exception.Message);
            }
        }

        private static void Edit(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect hatches to edit: ",
                true);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
            {
                return;
            }

            HatchVisualSettings current;
            int hatchCount;
            int unsupported;
            using (Transaction transaction =
                   document.Database.TransactionManager.StartTransaction())
            {
                current = ReadFirstHatchSettings(
                    selection.Value.GetObjectIds(),
                    transaction,
                    out hatchCount,
                    out unsupported);
            }

            if (current == null || hatchCount == 0)
            {
                editor.WriteMessage("\nCE_HATCHEDIT: no hatches were selected.");
                return;
            }

            HatchVisualSettings proposed;
            if (!PromptForSettings(editor, current, out proposed))
            {
                return;
            }

            editor.WriteMessage(
                "\nCE_HATCHEDIT preview: hatches={0}; unsupported={1}; pattern={2}; scale={3:N3}; angle={4:N2} deg; colour={5}; transparency={6}%.",
                hatchCount,
                unsupported,
                proposed.PatternName,
                proposed.PatternScale,
                RadiansToDegrees(proposed.PatternAngle),
                proposed.ColourIndex,
                proposed.TransparencyPercent);

            if (!Confirm(editor, "Apply these settings to the selected hatches"))
            {
                editor.WriteMessage("\nCE_HATCHEDIT cancelled. No hatches were changed.");
                return;
            }

            try
            {
                int changed = 0;
                int skipped = 0;
                using (Transaction transaction =
                       document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        Hatch hatch = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Hatch;
                        if (hatch == null || IsLayerLocked(transaction, hatch.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        hatch.UpgradeOpen();
                        ApplySettings(hatch, proposed);
                        changed++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_HATCHEDIT complete. Hatches changed: {0}; skipped: {1}.",
                    changed,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HATCHEDIT cancelled. No changes were committed. " +
                    exception.Message);
            }
        }

        private static void Match(Document document)
        {
            Editor editor = document.Editor;
            var sourceOptions = new PromptEntityOptions(
                "\nSelect source hatch whose display settings must be copied: ");
            sourceOptions.SetRejectMessage("\nSelect an AutoCAD hatch.");
            sourceOptions.AddAllowedClass(typeof(Hatch), false);
            PromptEntityResult sourceResult = editor.GetEntity(sourceOptions);
            if (sourceResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptSelectionResult targets = GetSelection(
                editor,
                "\nSelect target hatches: ",
                false);
            if (targets.Status != PromptStatus.OK || targets.Value == null)
            {
                return;
            }

            HatchVisualSettings sourceSettings;
            string sourceName;
            int targetCount;
            using (Transaction transaction =
                   document.Database.TransactionManager.StartTransaction())
            {
                Hatch source = transaction.GetObject(
                    sourceResult.ObjectId,
                    OpenMode.ForRead,
                    false) as Hatch;
                if (source == null)
                {
                    editor.WriteMessage("\nCE_HATCHMATCH: the source hatch is unavailable.");
                    return;
                }

                sourceSettings = ReadSettings(source);
                sourceName = source.Handle.ToString();
                targetCount = targets.Value.GetObjectIds().Count(
                    id => id != sourceResult.ObjectId &&
                          transaction.GetObject(id, OpenMode.ForRead, false) is Hatch);
            }

            if (targetCount == 0)
            {
                editor.WriteMessage("\nCE_HATCHMATCH: no target hatches were selected.");
                return;
            }

            editor.WriteMessage(
                "\nCE_HATCHMATCH preview: source={0}; targets={1}; pattern={2}; scale={3:N3}; angle={4:N2} deg; colour={5}; transparency={6}%.",
                sourceName,
                targetCount,
                sourceSettings.PatternName,
                sourceSettings.PatternScale,
                RadiansToDegrees(sourceSettings.PatternAngle),
                sourceSettings.ColourIndex,
                sourceSettings.TransparencyPercent);

            if (!Confirm(editor, "Match these target hatches to the source"))
            {
                editor.WriteMessage("\nCE_HATCHMATCH cancelled. No hatches were changed.");
                return;
            }

            try
            {
                int changed = 0;
                int skipped = 0;
                using (Transaction transaction =
                       document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in targets.Value.GetObjectIds())
                    {
                        if (id == sourceResult.ObjectId)
                        {
                            continue;
                        }

                        Hatch target = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Hatch;
                        if (target == null || IsLayerLocked(transaction, target.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        target.UpgradeOpen();
                        ApplySettings(target, sourceSettings);
                        changed++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_HATCHMATCH complete. Hatches changed: {0}; skipped: {1}.",
                    changed,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HATCHMATCH cancelled. No changes were committed. " +
                    exception.Message);
            }
        }

        private static void SendToBack(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect hatches to send behind linework: ",
                true);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
            {
                return;
            }

            try
            {
                int moved = 0;
                int skipped = 0;
                using (Transaction transaction =
                       document.Database.TransactionManager.StartTransaction())
                {
                    var byOwner = new Dictionary<ObjectId, ObjectIdCollection>();
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        Hatch hatch = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Hatch;
                        if (hatch == null || IsLayerLocked(transaction, hatch.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        ObjectIdCollection ids;
                        if (!byOwner.TryGetValue(hatch.OwnerId, out ids))
                        {
                            ids = new ObjectIdCollection();
                            byOwner.Add(hatch.OwnerId, ids);
                        }
                        ids.Add(hatch.ObjectId);
                        moved++;
                    }

                    foreach (KeyValuePair<ObjectId, ObjectIdCollection> pair in byOwner)
                    {
                        BlockTableRecord owner = transaction.GetObject(
                            pair.Key,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                        if (owner == null || owner.DrawOrderTableId.IsNull)
                        {
                            continue;
                        }

                        DrawOrderTable drawOrder = transaction.GetObject(
                            owner.DrawOrderTableId,
                            OpenMode.ForWrite,
                            false) as DrawOrderTable;
                        drawOrder?.MoveToBottom(pair.Value);
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_HATCHBACK complete. Hatches sent behind linework: {0}; skipped: {1}.",
                    moved,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HATCHBACK cancelled. No changes were committed. " +
                    exception.Message);
            }
        }

        private static PromptSelectionResult GetSelection(
            Editor editor,
            string message,
            bool allowImplied)
        {
            if (allowImplied)
            {
                PromptSelectionResult implied = editor.SelectImplied();
                if (implied.Status == PromptStatus.OK &&
                    implied.Value != null &&
                    implied.Value.Count > 0)
                {
                    return implied;
                }
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            return editor.GetSelection(options);
        }

        private static List<ObjectId> ReadSupportedBoundaries(
            IEnumerable<ObjectId> ids,
            Transaction transaction,
            out int unsupported)
        {
            var result = new List<ObjectId>();
            unsupported = 0;
            foreach (ObjectId id in ids.Distinct())
            {
                Entity entity = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity != null && IsSupportedBoundary(entity))
                {
                    result.Add(id);
                }
                else
                {
                    unsupported++;
                }
            }
            return result;
        }

        private static bool IsSupportedBoundary(Entity entity)
        {
            if (entity is Region)
            {
                return true;
            }

            var curve = entity as Curve;
            if (curve == null)
            {
                return false;
            }

            try
            {
                return curve.Closed;
            }
            catch
            {
                return false;
            }
        }

        private static HatchVisualSettings ReadFirstHatchSettings(
            IEnumerable<ObjectId> ids,
            Transaction transaction,
            out int hatchCount,
            out int unsupported)
        {
            HatchVisualSettings first = null;
            hatchCount = 0;
            unsupported = 0;
            foreach (ObjectId id in ids.Distinct())
            {
                Hatch hatch = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as Hatch;
                if (hatch == null)
                {
                    unsupported++;
                    continue;
                }

                hatchCount++;
                if (first == null)
                {
                    first = ReadSettings(hatch);
                }
            }
            return first;
        }

        private static HatchVisualSettings ReadSettings(Hatch hatch)
        {
            int colour = hatch.Color.ColorIndex;
            if (colour < 1 || colour > 255)
            {
                colour = DefaultColour;
            }

            return new HatchVisualSettings(
                hatch.PatternType,
                hatch.PatternName,
                hatch.PatternScale > 0.0 ? hatch.PatternScale : DefaultScale,
                hatch.PatternAngle,
                colour,
                ToTransparencyPercent(hatch.Transparency),
                hatch.HatchStyle);
        }

        private static bool PromptForSettings(
            Editor editor,
            HatchVisualSettings current,
            out HatchVisualSettings settings)
        {
            settings = null;

            string patternDefault = string.IsNullOrWhiteSpace(current.PatternName)
                ? DefaultPattern
                : current.PatternName;
            PromptResult patternResult = editor.GetString(new PromptStringOptions(
                "\nHatch pattern name <" + patternDefault + ">: ")
            {
                AllowSpaces = false,
                UseDefaultValue = true,
                DefaultValue = patternDefault
            });
            if (patternResult.Status != PromptStatus.OK)
            {
                return false;
            }

            string pattern = string.IsNullOrWhiteSpace(patternResult.StringResult)
                ? patternDefault
                : patternResult.StringResult.Trim();

            PromptDoubleResult scaleResult = editor.GetDouble(new PromptDoubleOptions(
                "\nHatch scale <" + current.PatternScale.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current.PatternScale > 0.0
                    ? current.PatternScale
                    : DefaultScale
            });
            if (scaleResult.Status != PromptStatus.OK)
            {
                return false;
            }

            double angleDefault = RadiansToDegrees(current.PatternAngle);
            PromptDoubleResult angleResult = editor.GetDouble(new PromptDoubleOptions(
                "\nHatch angle in degrees <" + angleDefault.ToString("0.##", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = true,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = angleDefault
            });
            if (angleResult.Status != PromptStatus.OK)
            {
                return false;
            }

            PromptIntegerResult colourResult = editor.GetInteger(new PromptIntegerOptions(
                "\nACI colour 1-255 <" + current.ColourIndex.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                LowerLimit = 1,
                UpperLimit = 255,
                UseDefaultValue = true,
                DefaultValue = current.ColourIndex >= 1 && current.ColourIndex <= 255
                    ? current.ColourIndex
                    : DefaultColour
            });
            if (colourResult.Status != PromptStatus.OK)
            {
                return false;
            }

            PromptIntegerResult transparencyResult = editor.GetInteger(
                new PromptIntegerOptions(
                    "\nTransparency percent 0-90 <" +
                    current.TransparencyPercent.ToString(CultureInfo.InvariantCulture) +
                    ">: ")
                {
                    AllowNegative = false,
                    AllowZero = true,
                    LowerLimit = 0,
                    UpperLimit = 90,
                    UseDefaultValue = true,
                    DefaultValue = Math.Max(
                        0,
                        Math.Min(90, current.TransparencyPercent))
                });
            if (transparencyResult.Status != PromptStatus.OK)
            {
                return false;
            }

            settings = new HatchVisualSettings(
                HatchPatternType.PreDefined,
                pattern,
                scaleResult.Value,
                DegreesToRadians(angleResult.Value),
                colourResult.Value,
                transparencyResult.Value,
                current.HatchStyle);
            return true;
        }

        private static void ApplySettings(
            Hatch hatch,
            HatchVisualSettings settings)
        {
            hatch.SetHatchPattern(settings.PatternType, settings.PatternName);
            if (!IsSolid(settings.PatternName))
            {
                hatch.PatternScale = settings.PatternScale;
                hatch.PatternAngle = settings.PatternAngle;
            }
            hatch.HatchStyle = settings.HatchStyle;
            hatch.Color = AcColor.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                (short)settings.ColourIndex);
            hatch.Transparency = ToTransparency(settings.TransparencyPercent);
            hatch.EvaluateHatch(true);
        }

        private static ObjectId GetOrCreateHatchLayer(
            Database database,
            Transaction transaction)
        {
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
            {
                throw new InvalidOperationException("The drawing layer table is unavailable.");
            }

            if (layers.Has(HatchLayerName))
            {
                return layers[HatchLayerName];
            }

            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = HatchLayerName,
                Color = AcColor.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    DefaultColour)
            };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool IsLayerLocked(
            Transaction transaction,
            ObjectId layerId)
        {
            LayerTableRecord layer = transaction.GetObject(
                layerId,
                OpenMode.ForRead,
                false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
        }

        private static AcTransparency ToTransparency(int percent)
        {
            int bounded = Math.Max(0, Math.Min(90, percent));
            byte alpha = (byte)Math.Round(
                255.0 * (100.0 - bounded) / 100.0,
                MidpointRounding.AwayFromZero);
            return new AcTransparency(alpha);
        }

        private static int ToTransparencyPercent(AcTransparency transparency)
        {
            if (!transparency.IsByAlpha)
            {
                return 0;
            }

            int percent = (int)Math.Round(
                100.0 * (255.0 - transparency.Alpha) / 255.0,
                MidpointRounding.AwayFromZero);
            return Math.Max(0, Math.Min(90, percent));
        }

        private static bool IsSolid(string patternName)
        {
            return string.Equals(
                patternName,
                "SOLID",
                StringComparison.OrdinalIgnoreCase);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   result.StringResult.Equals(
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private sealed class HatchVisualSettings
        {
            public HatchVisualSettings(
                HatchPatternType patternType,
                string patternName,
                double patternScale,
                double patternAngle,
                int colourIndex,
                int transparencyPercent,
                HatchStyle hatchStyle)
            {
                PatternType = patternType;
                PatternName = patternName;
                PatternScale = patternScale;
                PatternAngle = patternAngle;
                ColourIndex = colourIndex;
                TransparencyPercent = transparencyPercent;
                HatchStyle = hatchStyle;
            }

            public HatchPatternType PatternType { get; }
            public string PatternName { get; }
            public double PatternScale { get; }
            public double PatternAngle { get; }
            public int ColourIndex { get; }
            public int TransparencyPercent { get; }
            public HatchStyle HatchStyle { get; }
        }
    }
}
