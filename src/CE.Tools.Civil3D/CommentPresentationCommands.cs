using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CommentPresentationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Shared presentation and refresh infrastructure for repeated comments about
    /// annotative text, table scale, overlap correction, automatic rebuilds and
    /// dynamic linked reports. The commands work on existing CE Tools objects and
    /// preserve the original discipline-specific commands.
    /// </summary>
    public sealed class CommentPresentationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PRESENTATIONTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PresentationTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var choices = new List<CommandChoice>
            {
                new CommandChoice("Make selected text/tables annotative", "CE_MAKEANNOTATIVE "),
                new CommandChoice("Automatically format Civil and CE labels", "CE_CIVILLABELFORMAT "),
                new CommandChoice("Scale selected tables to CE text height", "CE_TABLESCALE "),
                new CommandChoice("Resolve selected annotation overlaps", "CE_OVERLAPFIX "),
                new CommandChoice("Reverse multiple polylines", "CE_PLREVERSE "),
                new CommandChoice("Refresh all linked CE tables and rebuild Civil objects", "CE_REFRESHALL "),
                new CommandChoice("Automatic linked refresh settings", "CE_AUTOREFRESH "),
                new CommandChoice("Refresh and rebuild status", "CE_REFRESHSTATUS "),
                new CommandChoice("Cleanup Manager window", "CE_CLEANUPUI "),
                new CommandChoice("Hatch Tools window", "CE_HATCHUI ")
            };
            var window = new CommandChoiceWindow(
                "CE Tools - Presentation and Dynamic Refresh",
                "Select a shared drawing-production command.",
                choices);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.SelectedChoice == null) return;
            document.SendStringToExecute(
                window.SelectedChoice.Command,
                true,
                false,
                true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_MAKEANNOTATIVE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MakeAnnotative()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect text, dimensions, leaders and tables to make annotative and scale: ");
            if (selection.Status != PromptStatus.OK) return;

            int changed = 0;
            int unsupported = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        unsupported++;
                        continue;
                    }

                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            selected.ObjectId,
                            OpenMode.ForWrite,
                            false) as Entity;
                    }
                    catch
                    {
                        unsupported++;
                        continue;
                    }
                    if (entity == null || IsLayerLocked(transaction, entity.LayerId))
                    {
                        unsupported++;
                        continue;
                    }

                    bool handled = SetAnnotativeByReflection(entity);
                    double modelHeight = PaperAnnotationScale.ModelTextHeight(
                        document.Database,
                        settings.TextHeight);
                    handled = ApplyTextHeight(entity, modelHeight) || handled;
                    var table = entity as Table;
                    if (table != null)
                    {
                        ScaleTable(table, modelHeight);
                        handled = true;
                    }

                    if (handled) changed++;
                    else unsupported++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_MAKEANNOTATIVE complete. Updated={0}; unsupported/locked={1}; CE height={2:N1}.",
                changed,
                unsupported,
                settings.TextHeight);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_TABLESCALE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ScaleTables()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect AutoCAD tables to resize relative to the CE annotation height: ");
            if (selection.Status != PromptStatus.OK) return;

            int changed = 0;
            int skipped = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    Table table = selected == null || selected.ObjectId.IsNull
                        ? null
                        : transaction.GetObject(
                            selected.ObjectId,
                            OpenMode.ForWrite,
                            false) as Table;
                    if (table == null || IsLayerLocked(transaction, table.LayerId))
                    {
                        skipped++;
                        continue;
                    }
                    ScaleTable(
                        table,
                        PaperAnnotationScale.ModelTextHeight(
                            document.Database,
                            settings.TextHeight));
                    SetAnnotativeByReflection(table);
                    changed++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_TABLESCALE complete. Tables updated={0}; skipped={1}; text height={2:N1}.",
                changed,
                skipped,
                settings.TextHeight);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_OVERLAPFIX",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ResolveOverlaps()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect text, leaders, dimensions and tables to check for overlap: ");
            if (selection.Status != PromptStatus.OK) return;

            var items = new List<PlacementItem>();
            var anchors = new List<Point3d>();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            selected.ObjectId,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }
                    if (entity == null || !IsAnnotationEntity(entity)) continue;
                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }
                    items.Add(new PlacementItem(entity.ObjectId, extents));
                }

                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (entity == null || !IsPointMarker(entity)) continue;
                        try
                        {
                            Extents3d extents = entity.GeometricExtents;
                            anchors.Add(Centre(extents));
                        }
                        catch
                        {
                            // Ignore proxy markers that expose no extents.
                        }
                    }
                }
            }

            items.Sort(delegate(PlacementItem first, PlacementItem second)
            {
                int vertical = second.Extents.MaxPoint.Y.CompareTo(first.Extents.MaxPoint.Y);
                return vertical != 0
                    ? vertical
                    : first.Extents.MinPoint.X.CompareTo(second.Extents.MinPoint.X);
            });

            double modelTextHeight = PaperAnnotationScale.ModelTextHeight(
                document.Database,
                settings.TextHeight);
            double gap = Math.Max(modelTextHeight * 0.35, 0.000001);
            var placed = new List<Extents3d>();
            int moved = 0;
            int unchanged = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (PlacementItem item in items)
                {
                    Entity entity = transaction.GetObject(
                        item.ObjectId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (entity == null || IsLayerLocked(transaction, entity.LayerId))
                    {
                        unchanged++;
                        continue;
                    }

                    Point3d originalCentre = Centre(item.Extents);
                    Point3d anchor = FindNearestAnchor(originalCentre, anchors);
                    PlacementCandidate best = FindClosestPlacement(
                        item.Extents,
                        anchor,
                        placed,
                        gap,
                        modelTextHeight);

                    if (best.Distance > 0.0000001)
                    {
                        MoveAnnotation(entity, best.Displacement);
                        moved++;
                    }
                    else
                    {
                        unchanged++;
                    }
                    placed.Add(best.Extents);
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_OVERLAPFIX complete. Moved={0}; unchanged/locked={1}. Labels were placed in the nearest clear position around their marker.",
                moved,
                unchanged);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLREVERSE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReversePolylines()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect multiple polylines or curves to reverse: ");
            if (selection.Status != PromptStatus.OK) return;

            int reversed = 0;
            int skipped = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        skipped++;
                        continue;
                    }
                    Entity entity = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (entity == null || IsLayerLocked(transaction, entity.LayerId))
                    {
                        skipped++;
                        continue;
                    }

                    MethodInfo reverse = entity.GetType().GetMethod(
                        "ReverseCurve",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (reverse == null)
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        reverse.Invoke(entity, null);
                        reversed++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                transaction.Commit();
            }

            int refreshedArrows = 0;
            try
            {
                refreshedArrows =
                    PolylineDirectionCommands.RefreshLinkedArrows(document);
            }
            catch
            {
                CommentAutoRefreshManager.MarkPending();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PLREVERSE complete. Reversed={0}; skipped={1}; direction arrows refreshed={2}.",
                reversed,
                skipped,
                refreshedArrows);
        }

        [CommandMethod("CE_TOOLS", "CE_REBUILDALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RebuildAll()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            RefreshSummary summary;
            using (DocumentLock documentLock = document.LockDocument())
            {
                summary = LinkedRefreshEngine.RebuildCivilObjects(document);
            }
            document.Editor.Regen();
            WriteRefreshSummary(document.Editor, "CE_REBUILDALL", summary);
        }

        [CommandMethod("CE_TOOLS", "CE_REBUILDSERVICES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RebuildServices()
        {
            RebuildAll();
        }

        [CommandMethod("CE_TOOLS", "CE_CLEANUPUI", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CleanupWindow()
        {
            ShowCommandWindow(
                "CE Tools - Cleanup Manager",
                "Choose the cleanup stage. The selected command still reports its result on the AutoCAD command line.",
                new List<CommandChoice>
                {
                    new CommandChoice("Full cleanup: OVERKILL + AUDIT + PURGE", "CE_DRAWCLEAN All "),
                    new CommandChoice("OVERKILL only", "CE_DRAWCLEAN Overkill "),
                    new CommandChoice("AUDIT only", "CE_DRAWCLEAN Audit "),
                    new CommandChoice("PURGE only", "CE_DRAWCLEAN Purge ")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_HATCHUI", CommandFlags.Modal | CommandFlags.Redraw)]
        public void HatchWindow()
        {
            ShowCommandWindow(
                "CE Tools - Hatch Settings and Actions",
                "Choose an action. Create and Edit open the existing CE hatch settings workflow so pattern, scale, angle, colour and transparency remain explicit.",
                new List<CommandChoice>
                {
                    new CommandChoice("Create transparent associative hatches", "CE_HATCHCREATE "),
                    new CommandChoice("Edit selected hatch settings", "CE_HATCHEDIT "),
                    new CommandChoice("Match hatch settings", "CE_HATCHMATCH "),
                    new CommandChoice("Send hatches behind linework", "CE_HATCHBACK ")
                });
        }

        private static void ShowCommandWindow(
            string title,
            string note,
            IList<CommandChoice> choices)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var window = new CommandChoiceWindow(title, note, choices);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.SelectedChoice == null) return;
            document.SendStringToExecute(
                window.SelectedChoice.Command,
                true,
                false,
                true);
        }

        private static bool SetAnnotativeByReflection(object value)
        {
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Annotative",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                object enabled;
                if (property.PropertyType == typeof(bool))
                {
                    enabled = true;
                }
                else if (property.PropertyType.IsEnum)
                {
                    string name = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(item => string.Equals(
                            item,
                            "True",
                            StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item, "Yes", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item, "On", StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    enabled = Enum.Parse(property.PropertyType, name);
                }
                else
                {
                    return false;
                }
                property.SetValue(value, enabled, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ApplyTextHeight(Entity entity, double height)
        {
            var mtext = entity as MText;
            if (mtext != null)
            {
                mtext.TextHeight = height;
                return true;
            }
            var text = entity as DBText;
            if (text != null)
            {
                text.Height = height;
                return true;
            }
            var attribute = entity as AttributeReference;
            if (attribute != null)
            {
                attribute.Height = height;
                return true;
            }
            var leader = entity as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText != null)
                {
                    leaderText.TextHeight = height;
                    leader.MText = leaderText;
                    return true;
                }
            }

            try
            {
                PropertyInfo property = entity.GetType().GetProperty(
                    "TextHeight",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite &&
                    property.PropertyType == typeof(double))
                {
                    property.SetValue(entity, height, null);
                    return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static void ScaleTable(Table table, double height)
        {
            double safeHeight = NormalizeHeight(height);
            table.SetRowHeight(Math.Max(safeHeight * 2.0, 0.001));
            for (int column = 0; column < table.Columns.Count; column++)
            {
                double current = table.Columns[column].Width;
                double minimum = safeHeight * 5.0;
                double maximum = safeHeight * 35.0;
                if (current < minimum)
                    table.Columns[column].Width = minimum;
                else if (current > maximum)
                    table.Columns[column].Width = maximum;
            }
            for (int row = 0; row < table.Rows.Count; row++)
            {
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    try
                    {
                        table.Cells[row, column].TextHeight =
                            row == 0 ? safeHeight * 1.15 : safeHeight;
                    }
                    catch
                    {
                        // Some merged or formula cells reject direct height changes.
                    }
                }
            }
            table.GenerateLayout();
        }

        private static double NormalizeHeight(double value)
        {
            return PaperAnnotationScale.NormalizePaperHeight(value);
        }

        private static bool IsAnnotationEntity(Entity entity)
        {
            return entity is MText ||
                   entity is DBText ||
                   entity is MLeader ||
                   entity is Dimension ||
                   entity is Table ||
                   entity is AttributeReference;
        }

        private static bool IsPointMarker(Entity entity)
        {
            if (entity is DBPoint || entity is Circle) return true;
            string name = entity.GetType().Name;
            return name.IndexOf("CogoPoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Point3d Centre(Extents3d extents)
        {
            return new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
        }

        private static Point3d FindNearestAnchor(
            Point3d origin,
            IEnumerable<Point3d> anchors)
        {
            Point3d best = origin;
            double bestDistance = double.MaxValue;
            foreach (Point3d candidate in anchors)
            {
                double distance = origin.DistanceTo(candidate);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private static PlacementCandidate FindClosestPlacement(
            Extents3d original,
            Point3d anchor,
            IList<Extents3d> placed,
            double gap,
            double textHeight)
        {
            Point3d originalCentre = Centre(original);
            var candidates = new List<PlacementCandidate>
            {
                PlacementCandidate.From(original, new Vector3d(0.0, 0.0, 0.0))
            };
            double halfWidth = Math.Max(
                (original.MaxPoint.X - original.MinPoint.X) * 0.5,
                textHeight);
            double halfHeight = Math.Max(
                (original.MaxPoint.Y - original.MinPoint.Y) * 0.5,
                textHeight * 0.5);
            Vector2d[] directions =
            {
                new Vector2d(1, 0), new Vector2d(-1, 0),
                new Vector2d(0, 1), new Vector2d(0, -1),
                new Vector2d(1, 1), new Vector2d(-1, 1),
                new Vector2d(1, -1), new Vector2d(-1, -1)
            };
            for (int ring = 1; ring <= 8; ring++)
            {
                foreach (Vector2d direction in directions)
                {
                    Vector2d unit = direction.GetNormal();
                    double radial = ring * Math.Max(textHeight * 0.75, gap);
                    Point3d target = new Point3d(
                        anchor.X + unit.X * (halfWidth + gap + radial),
                        anchor.Y + unit.Y * (halfHeight + gap + radial),
                        originalCentre.Z);
                    candidates.Add(PlacementCandidate.From(
                        original,
                        target - originalCentre));
                }
            }

            PlacementCandidate best = candidates[0];
            double bestScore = double.MaxValue;
            foreach (PlacementCandidate candidate in candidates)
            {
                int collisions = CountOverlaps(candidate.Extents, placed, gap);
                double anchorDistance = Centre(candidate.Extents).DistanceTo(anchor);
                double score = collisions * 1000000000.0 + anchorDistance;
                if (score >= bestScore) continue;
                best = candidate;
                bestScore = score;
                if (collisions == 0 && candidate.Distance <= textHeight) break;
            }
            return best;
        }

        private static int CountOverlaps(
            Extents3d candidate,
            IEnumerable<Extents3d> placed,
            double gap)
        {
            int count = 0;
            foreach (Extents3d other in placed)
            {
                if (candidate.MaxPoint.X + gap < other.MinPoint.X ||
                    candidate.MinPoint.X - gap > other.MaxPoint.X ||
                    candidate.MaxPoint.Y + gap < other.MinPoint.Y ||
                    candidate.MinPoint.Y - gap > other.MaxPoint.Y)
                    continue;
                count++;
            }
            return count;
        }

        private static void MoveAnnotation(Entity entity, Vector3d displacement)
        {
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try
                {
                    leader.TextLocation = leader.TextLocation + displacement;
                    return;
                }
                catch
                {
                    // Fall through for older/proxy MLeader implementations.
                }
            }
            entity.TransformBy(Matrix3d.Displacement(displacement));
        }

        private static bool OverlapsAny(
            Extents3d candidate,
            IEnumerable<Extents3d> placed,
            double gap)
        {
            foreach (Extents3d other in placed)
            {
                if (candidate.MaxPoint.X + gap < other.MinPoint.X ||
                    candidate.MinPoint.X - gap > other.MaxPoint.X ||
                    candidate.MaxPoint.Y + gap < other.MinPoint.Y ||
                    candidate.MinPoint.Y - gap > other.MaxPoint.Y)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static Extents3d Translate(Extents3d extents, double dx, double dy)
        {
            return new Extents3d(
                new Point3d(
                    extents.MinPoint.X + dx,
                    extents.MinPoint.Y + dy,
                    extents.MinPoint.Z),
                new Point3d(
                    extents.MaxPoint.X + dx,
                    extents.MaxPoint.Y + dy,
                    extents.MaxPoint.Z));
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            if (layerId.IsNull) return false;
            try
            {
                LayerTableRecord layer = transaction.GetObject(
                    layerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch
            {
                return true;
            }
        }

        private static void WriteRefreshSummary(
            Editor editor,
            string command,
            RefreshSummary summary)
        {
            editor.WriteMessage(
                "\n{0} complete. Coordinate followers={1}; coordinate tables={2}; BOQ tables={3}; linked feature lines={4}; surfaces rebuilt={5}; corridors rebuilt={6}; skipped/failed={7}.",
                command,
                summary.CoordinateFollowers,
                summary.CoordinateTables,
                summary.BoqTables,
                summary.FeatureLines,
                summary.Surfaces,
                summary.Corridors,
                summary.Failed);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class PlacementItem
        {
            public PlacementItem(ObjectId objectId, Extents3d extents)
            {
                ObjectId = objectId;
                Extents = extents;
            }

            public ObjectId ObjectId { get; private set; }
            public Extents3d Extents { get; private set; }
        }

        private sealed class PlacementCandidate
        {
            private PlacementCandidate(Extents3d extents, Vector3d displacement)
            {
                Extents = extents;
                Displacement = displacement;
                Distance = displacement.Length;
            }

            public Extents3d Extents { get; private set; }
            public Vector3d Displacement { get; private set; }
            public double Distance { get; private set; }

            public static PlacementCandidate From(
                Extents3d extents,
                Vector3d displacement)
            {
                return new PlacementCandidate(
                    Translate(extents, displacement.X, displacement.Y),
                    displacement);
            }
        }
    }

    internal static class CommentAutoRefreshManager
    {
        private static Database _database;
        private static bool _initialised;
        private static bool _busy;
        private static bool _pending;
        private static DateTime _lastRunUtc = DateTime.MinValue;

        public static bool Enabled { get; set; } = true;
        public static bool Pending { get { return _pending; } }
        public static string LastRefreshText
        {
            get
            {
                return _lastRunUtc == DateTime.MinValue
                    ? "<Not run in this session>"
                    : _lastRunUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            }
        }

        public static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            _initialised = false;
        }

        public static void MarkPending()
        {
            _pending = true;
            LinkedTableAutoRefreshManager.Queue(
                AcApplication.DocumentManager.MdiActiveDocument);
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (!Enabled || !_pending || _busy || document == null) return;
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 800.0) return;

            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            _busy = true;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    // Automatic annotation refresh must never rebuild every
                    // surface/corridor in the drawing. Apart from being much
                    // heavier than required, a rebuild can make Civil 3D open
                    // Event Viewer when an unrelated surface contains a stale
                    // external-file reference. Explicit CE refresh commands
                    // remain responsible for Civil-object rebuilds.
                    LinkedRefreshEngine.Refresh(document, false);
                }
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;
            }
            catch
            {
                _pending = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private static void AttachDatabase(Database database)
        {
            if (ReferenceEquals(_database, database)) return;
            DetachDatabase();
            _database = database;
            if (_database != null)
                _database.ObjectModified += OnObjectModified;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
                _database.ObjectModified -= OnObjectModified;
            _database = null;
        }

        private static void OnObjectModified(object sender, ObjectEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            if (eventArgs.DBObject is Xrecord || eventArgs.DBObject is DBDictionary) return;
            _pending = true;
        }
    }

    internal static class LinkedRefreshEngine
    {
        private const string CoordinateLinkRecord = "CE_COORDINATE_LINKS";
        private const string BoqLinkRecord = "CE_BOQ_LINKS";
        private const string DynamicFollowerRecord = "CE_DYNAMIC_COORDINATE_FOLLOWER";

        public static RefreshSummary Refresh(Document document, bool rebuildCivil)
        {
            var summary = new RefreshSummary();
            // Rebuild design sources first so every linked label, table and
            // estimate reads the latest Civil 3D geometry in this same pass.
            if (rebuildCivil)
                summary.Add(RebuildCivilObjects(document));
            try
            {
                summary.CoordinateFollowers +=
                    DynamicCoordinateLinkStore.Refresh(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.CoordinateFollowers +=
                    SurfaceComparisonLinkStore.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.CoordinateFollowers +=
                    AlignmentAnnotationLinkStore.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.CoordinateFollowers +=
                    ProfileAnnotationLinkStore.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.CoordinateFollowers +=
                    CorridorAnnotationLinkStore.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.CoordinateFollowers +=
                    PolylineDirectionCommands.RefreshLinkedArrows(document);
                CogoPointProjectStyleCommands.ApplySelectedStyles(
                    document,
                    true);
            }
            catch
            {
                summary.Failed++;
            }

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
            MethodInfo coordinateRefresh = typeof(SurveyCoordinateWorkflowCommands).GetMethod(
                "RefreshLinkedTable",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo boqRefresh = typeof(BillOfQuantitiesCommands).GetMethod(
                "RefreshTable",
                BindingFlags.NonPublic | BindingFlags.Static);

            foreach (LinkedTableItem item in tables)
            {
                try
                {
                    if (item.Kind == LinkedTableKind.Coordinate && coordinateRefresh != null)
                    {
                        object[] arguments =
                        {
                            document.Database,
                            item.ObjectId,
                            0,
                            0
                        };
                        coordinateRefresh.Invoke(null, arguments);
                        summary.CoordinateTables++;
                    }
                    else if (item.Kind == LinkedTableKind.Boq && boqRefresh != null)
                    {
                        object value = boqRefresh.Invoke(
                            null,
                            new object[] { document, item.ObjectId, false });
                        if (value is bool && (bool)value) summary.BoqTables++;
                        else summary.Failed++;
                    }
                }
                catch
                {
                    summary.Failed++;
                }
            }

            // Refresh every CE schedule family, not only the two legacy table
            // schemas discovered above. Each command owns its link persistence
            // and safely skips drawings that contain none of its tables.
            summary.CoordinateTables += SettingOutScheduleCommands.RefreshAll(document);
            summary.BoqTables += NetworkAssetScheduleCommands.RefreshAll(document);
            summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);
            summary.BoqTables += StandardQuantityTemplateCommands.RefreshAll(document);
            summary.BoqTables += SewerExcavationCommentCommands.RefreshAll(document);
            summary.BoqTables += WaterSewerCostEstimateCommands.RefreshAll(document);
            try
            {
                summary.CoordinateFollowers +=
                    ParkingNumberLinkStore.RefreshAll(document);
                    ParkingReportLinkStore.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }
            try
            {
                summary.FeatureLines +=
                    FeatureLineRelativeCommands.RefreshAll(document);
                VertexSettingOutCommands.RefreshAll(document);
            }
            catch
            {
                summary.Failed++;
            }

            return summary;
        }

        public static RefreshSummary RebuildCivilObjects(Document document)
        {
            var summary = new RefreshSummary();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return summary;

            summary.Surfaces = RebuildCollection(
                document.Database,
                ReadCivilObjectIds(civilDocument, "GetSurfaceIds"),
                ref summary.Failed);
            summary.Corridors = RebuildCollection(
                document.Database,
                ReadCivilObjectIds(civilDocument, "GetCorridorIds"),
                ref summary.Failed);
            return summary;
        }

        public static RefreshInventory ReadInventory(Database database)
        {
            var inventory = new RefreshInventory();
            foreach (LinkedTableItem item in ReadLinkedTables(database))
            {
                if (item.Kind == LinkedTableKind.Coordinate) inventory.CoordinateTables++;
                else if (item.Kind == LinkedTableKind.Boq) inventory.BoqTables++;
            }
            inventory.CoordinateFollowers = DynamicCoordinateLinkStore.CountLinks(database);
            return inventory;
        }

        private static List<LinkedTableItem> ReadLinkedTables(Database database)
        {
            var result = new List<LinkedTableItem>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blockTable == null) return result;
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId entityId in block)
                    {
                        Table table = transaction.GetObject(
                            entityId,
                            OpenMode.ForRead,
                            false) as Table;
                        if (table == null || table.ExtensionDictionary.IsNull) continue;
                        DBDictionary dictionary = transaction.GetObject(
                            table.ExtensionDictionary,
                            OpenMode.ForRead,
                            false) as DBDictionary;
                        if (dictionary == null) continue;
                        if (dictionary.Contains(CoordinateLinkRecord))
                            result.Add(new LinkedTableItem(table.ObjectId, LinkedTableKind.Coordinate));
                        else if (dictionary.Contains(BoqLinkRecord))
                            result.Add(new LinkedTableItem(table.ObjectId, LinkedTableKind.Boq));
                    }
                }
            }
            return result;
        }

        private static List<ObjectId> ReadCivilObjectIds(
            CivilDocument civilDocument,
            string methodName)
        {
            var ids = new List<ObjectId>();
            try
            {
                MethodInfo method = civilDocument.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                object value = method == null ? null : method.Invoke(civilDocument, null);
                IEnumerable enumerable = value as IEnumerable;
                if (enumerable == null) return ids;
                foreach (object item in enumerable)
                {
                    if (item is ObjectId)
                    {
                        ObjectId id = (ObjectId)item;
                        if (!id.IsNull && !id.IsErased) ids.Add(id);
                    }
                }
            }
            catch
            {
                return ids;
            }
            return ids;
        }

        private static int RebuildCollection(
            Database database,
            IEnumerable<ObjectId> ids,
            ref int failed)
        {
            int rebuilt = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    }
                    catch
                    {
                        failed++;
                        continue;
                    }
                    MethodInfo rebuild = value.GetType().GetMethod(
                        "Rebuild",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (rebuild == null)
                    {
                        failed++;
                        continue;
                    }
                    try
                    {
                        rebuild.Invoke(value, null);
                        rebuilt++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                transaction.Commit();
            }
            return rebuilt;
        }

        private sealed class LinkedTableItem
        {
            public LinkedTableItem(ObjectId objectId, LinkedTableKind kind)
            {
                ObjectId = objectId;
                Kind = kind;
            }
            public ObjectId ObjectId { get; private set; }
            public LinkedTableKind Kind { get; private set; }
        }

        private enum LinkedTableKind
        {
            Coordinate,
            Boq
        }
    }

    internal sealed class RefreshSummary
    {
        public int CoordinateFollowers;
        public int CoordinateTables;
        public int BoqTables;
        public int FeatureLines;
        public int Surfaces;
        public int Corridors;
        public int Failed;

        public void Add(RefreshSummary other)
        {
            if (other == null) return;
            CoordinateFollowers += other.CoordinateFollowers;
            CoordinateTables += other.CoordinateTables;
            BoqTables += other.BoqTables;
            FeatureLines += other.FeatureLines;
            Surfaces += other.Surfaces;
            Corridors += other.Corridors;
            Failed += other.Failed;
        }
    }

    internal sealed class RefreshInventory
    {
        public int CoordinateTables;
        public int BoqTables;
        public int CoordinateFollowers;
    }

    internal sealed class CommandChoice
    {
        public CommandChoice(string title, string command)
        {
            Title = title;
            Command = command;
        }
        public string Title { get; private set; }
        public string Command { get; private set; }
        public override string ToString() { return Title; }
    }

    internal sealed class CommandChoiceWindow : Window
    {
        private readonly ListBox _list;

        public CommandChoiceWindow(
            string title,
            string note,
            IEnumerable<CommandChoice> choices)
        {
            Title = title;
            Width = 650;
            Height = 520;
            MinWidth = 480;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Accepted = false;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = title,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var description = new TextBlock
            {
                Text = note,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(description, Dock.Top);
            root.Children.Add(description);

            _list = new ListBox
            {
                ItemsSource = choices == null
                    ? new List<CommandChoice>()
                    : choices.ToList(),
                FontSize = 14
            };
            _list.MouseDoubleClick += delegate { Accept(); };
            root.Children.Add(_list);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var run = new Button
            {
                Content = "Run",
                MinWidth = 90,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            run.Click += delegate { Accept(); };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Padding = new Thickness(12, 6, 12, 6),
                IsCancel = true
            };
            buttons.Children.Add(run);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
        }

        public bool Accepted { get; private set; }
        public CommandChoice SelectedChoice { get; private set; }

        private void Accept()
        {
            SelectedChoice = _list.SelectedItem as CommandChoice;
            if (SelectedChoice == null) return;
            Accepted = true;
            DialogResult = true;
            Close();
        }
    }
}
