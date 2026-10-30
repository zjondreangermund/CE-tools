using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerLabelLayoutCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Staggers selected sewer/alignment text to remove collisions. Accepted
    /// positions can be frozen so later sorting passes leave them untouched.
    /// </summary>
    public sealed class SewerLabelLayoutCommands
    {
        private const string FreezeRecordName = "CE_TOOLS_LABEL_FREEZE";
        private const double Tolerance = 1e-8;
        private const int MaximumOffsetAttempts = 16;

        // Shared, popup-driven collision pass for CE-generated MText/DBText names.
        // Native Civil 3D label entities are intentionally excluded: their positions
        // are controlled by label styles and must not be moved as generic text.
        [CommandMethod("CE_TOOLS", "CE_LABELCOLLISIONREVIEW", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReviewNameCollisions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Name Label Collision Control",
                "Avoid overlapping CE-generated road, water and sewer text. Civil 3D native labels are not moved.");
            options.AddChoice("Discipline", "01 Scope", "Names to review", "All",
                new[] { "All", "Road", "Water", "Sewer" },
                "Only CE-generated MText and DBText on matching layers are moved.");
            options.AddChoice("Overlap", "02 Placement", "Avoid overlapping name text", "ON",
                new[] { "ON", "OFF" }, "OFF leaves existing label positions unchanged.");
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;
            if (options.Text("Overlap") != "ON") return;

            string category = options.Text("Discipline");
            var ids = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (space == null) return;
                foreach (ObjectId id in space)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (!(entity is MText) && !(entity is DBText)) continue;
                    string layer = (entity.Layer ?? string.Empty).ToUpperInvariant();
                    if (!layer.StartsWith("CE-") && !layer.StartsWith("CE_")) continue;
                    bool road = layer.Contains("ROAD") || layer.Contains("ALIGN");
                    bool water = layer.Contains("WATER");
                    bool sewer = layer.Contains("SEW") || layer.Contains("BRANCH");
                    if ((category == "All" && (road || water || sewer)) ||
                        (category == "Road" && road) || (category == "Water" && water) ||
                        (category == "Sewer" && sewer)) ids.Add(id);
                }
            }
            if (ids.Count == 0)
            {
                document.Editor.WriteMessage("\\nNo supported CE-generated name text found on the selected drawing space.");
                return;
            }
            document.Editor.SetImpliedSelection(ids.ToArray());
            try { SortSewerLabels(); }
            finally { document.Editor.SetImpliedSelection(new ObjectId[0]); }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWLABELSORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SortSewerLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect sewer, alignment and branch text to stagger: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                return;
            }

            int moved = 0;
            int frozen = 0;
            int unsupported = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                var candidates = new List<LabelCandidate>();
                foreach (ObjectId objectId in selection.Value.GetObjectIds())
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (!(entity is MText) && !(entity is DBText))
                    {
                        unsupported++;
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        unsupported++;
                        continue;
                    }

                    candidates.Add(new LabelCandidate(
                        objectId,
                        extents,
                        GetRotation(entity),
                        IsFrozen(entity, transaction)));
                }

                candidates = candidates
                    .OrderBy(item => item.Extents.MinPoint.X)
                    .ThenBy(item => item.Extents.MinPoint.Y)
                    .ToList();

                var occupied = new List<Extents3d>();
                foreach (LabelCandidate candidate in candidates)
                {
                    if (candidate.Frozen)
                    {
                        occupied.Add(candidate.Extents);
                        frozen++;
                        continue;
                    }

                    Entity entity = transaction.GetObject(
                        candidate.ObjectId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (entity == null) continue;

                    Extents3d placed = candidate.Extents;
                    if (OverlapsAny(placed, occupied))
                    {
                        double height = Math.Max(
                            Tolerance,
                            candidate.Extents.MaxPoint.Y -
                            candidate.Extents.MinPoint.Y);
                        double step = Math.Max(height * 1.35, GetTextHeight(entity) * 1.5);
                        Vector3d normal = new Vector3d(
                            -Math.Sin(candidate.Rotation),
                            Math.Cos(candidate.Rotation),
                            0.0);

                        // AutoCAD 2023's managed API does not expose Vector3d.Zero.
                        Vector3d chosen = new Vector3d(0.0, 0.0, 0.0);
                        for (int attempt = 1;
                             attempt <= MaximumOffsetAttempts;
                             attempt++)
                        {
                            int level = (attempt + 1) / 2;
                            double side = attempt % 2 == 1 ? 1.0 : -1.0;
                            Vector3d offset = normal * (step * level * side);
                            Extents3d trial = Offset(candidate.Extents, offset);
                            if (!OverlapsAny(trial, occupied))
                            {
                                chosen = offset;
                                placed = trial;
                                break;
                            }
                        }

                        if (chosen.Length > Tolerance)
                        {
                            entity.TransformBy(Matrix3d.Displacement(chosen));
                            moved++;
                        }
                    }

                    occupied.Add(placed);
                }

                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWLABELSORT complete. Moved={0}; frozen={1}; unsupported={2}. " +
                "Use CE_SEWLABELFREEZE after accepting positions.",
                moved,
                frozen,
                unsupported);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWLABELFREEZE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FreezeSewerLabels()
        {
            SetFreezeState(true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWLABELUNFREEZE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void UnfreezeSewerLabels()
        {
            SetFreezeState(false);
        }

        private static void SetFreezeState(bool freeze)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                freeze
                    ? "\nSelect text positions to freeze: "
                    : "\nSelect frozen text positions to release: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                return;
            }

            int changed = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in selection.Value.GetObjectIds())
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (!(entity is MText) && !(entity is DBText)) continue;

                    SetFrozen(entity, freeze, transaction);
                    changed++;
                }

                transaction.Commit();
            }

            document.Editor.WriteMessage(
                freeze
                    ? "\nFrozen text positions: {0}."
                    : "\nReleased text positions: {0}.",
                changed);
        }

        private static PromptSelectionResult GetSelection(
            Editor editor,
            string message)
        {
            PromptSelectionResult result = editor.SelectImplied();
            if (result.Status == PromptStatus.OK &&
                result.Value != null &&
                result.Value.Count > 0)
            {
                return result;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static bool IsFrozen(
            Entity entity,
            Transaction transaction)
        {
            if (entity.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary =
                    transaction.GetObject(
                        entity.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                return dictionary != null &&
                       dictionary.Contains(FreezeRecordName);
            }
            catch { return false; }
        }

        private static double GetRotation(Entity entity)
        {
            var mtext = entity as MText;
            if (mtext != null) return mtext.Rotation;
            var text = entity as DBText;
            return text == null ? 0.0 : text.Rotation;
        }

        private static double GetTextHeight(Entity entity)
        {
            var mtext = entity as MText;
            if (mtext != null) return Math.Max(Tolerance, mtext.TextHeight);
            var text = entity as DBText;
            return text == null
                ? 1.0
                : Math.Max(Tolerance, text.Height);
        }

        private static bool OverlapsAny(
            Extents3d candidate,
            IEnumerable<Extents3d> occupied)
        {
            return occupied.Any(item => Overlaps(candidate, item));
        }

        private static bool Overlaps(Extents3d left, Extents3d right)
        {
            return left.MinPoint.X <= right.MaxPoint.X + Tolerance &&
                   left.MaxPoint.X + Tolerance >= right.MinPoint.X &&
                   left.MinPoint.Y <= right.MaxPoint.Y + Tolerance &&
                   left.MaxPoint.Y + Tolerance >= right.MinPoint.Y;
        }

        private static Extents3d Offset(
            Extents3d extents,
            Vector3d displacement)
        {
            return new Extents3d(
                extents.MinPoint + displacement,
                extents.MaxPoint + displacement);
        }

        private static void SetFrozen(
            Entity entity,
            bool freeze,
            Transaction transaction)
        {
            if (freeze && entity.ExtensionDictionary.IsNull)
            {
                entity.CreateExtensionDictionary();
            }
            if (entity.ExtensionDictionary.IsNull) return;

            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;

            if (freeze)
            {
                if (dictionary.Contains(FreezeRecordName)) return;
                var record = new Xrecord
                {
                    Data = new ResultBuffer(
                        new TypedValue((int)DxfCode.Int16, (short)1))
                };
                dictionary.SetAt(FreezeRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            else if (dictionary.Contains(FreezeRecordName))
            {
                DBObject record = transaction.GetObject(
                    dictionary.GetAt(FreezeRecordName),
                    OpenMode.ForWrite,
                    false);
                record.Erase();
            }
        }

        private sealed class LabelCandidate
        {
            public LabelCandidate(
                ObjectId objectId,
                Extents3d extents,
                double rotation,
                bool frozen)
            {
                ObjectId = objectId;
                Extents = extents;
                Rotation = rotation;
                Frozen = frozen;
            }

            public ObjectId ObjectId { get; private set; }
            public Extents3d Extents { get; private set; }
            public double Rotation { get; private set; }
            public bool Frozen { get; private set; }
        }
    }
}
