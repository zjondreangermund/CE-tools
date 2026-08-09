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

[assembly: CommandClass(typeof(CETools.Civil3D.RoadJunctionCompletionCommands))]

namespace CETools.Civil3D
{
    public sealed class RoadJunctionCompletionCommands
    {
        private const string AppName = "CE_ROAD_JUNCTION";
        private const string LayerName = "CE-ROAD-JUNCTION";

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Junction Tools",
                "Create dynamic T/cross junction bellmouths or number existing bellmouths in engineering order.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create T-junction", "CE_ROADTJUNCTION", "Create two linked bellmouth returns and number them clockwise.", "01 Create"),
                    new DisciplineWorkflowAction("Create cross-junction", "CE_ROADCROSSJUNCTION", "Create four linked bellmouth returns and number them clockwise.", "01 Create"),
                    new DisciplineWorkflowAction("Number selected junction bellmouths", "CE_JUNCTIONNUMBER", "Choose left-to-right, top-to-bottom or top-left-to-bottom-right group order, with an optional picked start junction/return.", "02 Number"),
                    new DisciplineWorkflowAction("Refresh linked junctions", "CE_JUNCTIONREFRESH", "Rebuild labels and linked bellmouth geometry from saved source handles.", "03 Refresh")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADTJUNCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateTJunction()
        {
            CreateJunction(false);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCROSSJUNCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateCrossJunction()
        {
            CreateJunction(true);
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONNUMBER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void NumberBellmouths()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = NumberingSettings();
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selected = SelectCurves(document.Editor, "\nSelect all bellmouth/return curves to number: ");
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            string prefix = CleanPrefix(model.Text("Prefix"), "J");
            int start = model.Integer("Start", 1);
            double cluster = Math.Max(model.Double("Cluster", 35.0), 0.001);
            double textPaper = Math.Max(model.Double("TextHeight", 2.5), 0.5);
            bool clockwise = !string.Equals(model.Text("Direction"), "Counter-clockwise", StringComparison.OrdinalIgnoreCase);
            string groupOrder = model.Text("GroupOrder");
            bool pickStart = string.Equals(model.Text("StartMode"), "Pick start junction / return", StringComparison.OrdinalIgnoreCase);
            Point3d? pickedStart = null;
            if (pickStart)
            {
                PromptPointResult picked = document.Editor.GetPoint("\nPick the junction or return that must receive the first number: ");
                if (picked.Status != PromptStatus.OK) return;
                pickedStart = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            }
            int labels = NumberSelection(
                document,
                selected.Value.GetObjectIds(),
                prefix,
                start,
                cluster,
                textPaper,
                clockwise,
                groupOrder,
                pickedStart);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_JUNCTIONNUMBER complete. Labels={0}; group order={1}; picked start={2}.",
                labels,
                groupOrder,
                pickedStart.HasValue ? "Yes" : "No");
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshJunctions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int refreshed = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_JUNCTIONREFRESH complete. Linked labels refreshed={0}.", refreshed);
        }

        private static void CreateJunction(bool cross)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                cross ? "CE Tools - Create Cross Junction" : "CE Tools - Create T-Junction",
                "Create linked bellmouth return curves. The junction receives a J-number and each return is numbered clockwise from the top-left return.");
            model.AddDouble("Radius", "01 Geometry", "Bellmouth radius", 10.0, "Design radius for every return.");
            model.AddDouble("Width", "01 Geometry", "Road half-width", 3.7, "Offset from each road centreline to the kerb/edge return.");
            model.AddText("Prefix", "02 Numbering", "Junction prefix", "J", "Use J for J1.1, J1.2, J1.3 and J1.4.");
            model.AddPositiveInteger("Start", "02 Numbering", "Junction number", 1, "Junction number assigned to this intersection.");
            model.AddChoice("Direction", "02 Numbering", "Return order", "Clockwise", "Start at the top-left/NW return.", new[] { "Clockwise", "Counter-clockwise" });
            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative paper height for return labels.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptPointResult point = document.Editor.GetPoint("\nPick the road-centreline intersection point: ");
            if (point.Status != PromptStatus.OK) return;
            PromptAngleResult mainAngle = document.Editor.GetAngle(new PromptAngleOptions("\nSpecify the main-road direction: ") { BasePoint = point.Value, UseBasePoint = true });
            if (mainAngle.Status != PromptStatus.OK) return;
            double sideAngle;
            if (cross)
            {
                PromptAngleResult side = document.Editor.GetAngle(new PromptAngleOptions("\nSpecify the crossing-road direction: ") { BasePoint = point.Value, UseBasePoint = true });
                if (side.Status != PromptStatus.OK) return;
                sideAngle = side.Value;
            }
            else
            {
                PromptAngleResult side = document.Editor.GetAngle(new PromptAngleOptions("\nSpecify the side-road direction away from the main road: ") { BasePoint = point.Value, UseBasePoint = true });
                if (side.Status != PromptStatus.OK) return;
                sideAngle = side.Value;
            }

            Point3d centre = point.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            double radius = Math.Max(model.Double("Radius", 10.0), 0.01);
            double width = Math.Max(model.Double("Width", 3.7), 0.01);
            string prefix = CleanPrefix(model.Text("Prefix"), "J");
            int junction = model.Integer("Start", 1);
            double textPaper = Math.Max(model.Double("TextHeight", 2.5), 0.5);
            bool clockwise = !string.Equals(model.Text("Direction"), "Counter-clockwise", StringComparison.OrdinalIgnoreCase);
            int count = cross ? 4 : 2;
            var generated = new List<ObjectId>();

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(document.Database, transaction);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                double[] quadrants = cross
                    ? new[] { Math.PI * 0.75, Math.PI * 0.25, -Math.PI * 0.25, -Math.PI * 0.75 }
                    : SelectTQuadrants(mainAngle.Value, sideAngle);
                if (!clockwise) Array.Reverse(quadrants);
                for (int index = 0; index < count; index++)
                {
                    double angle = quadrants[index];
                    double sx = Math.Cos(angle) >= 0.0 ? 1.0 : -1.0;
                    double sy = Math.Sin(angle) >= 0.0 ? 1.0 : -1.0;
                    Point3d arcCentre = centre + new Vector3d(sx * (width + radius), sy * (width + radius), 0.0);
                    double startAngle;
                    double endAngle;
                    if (sx > 0.0 && sy > 0.0) { startAngle = Math.PI; endAngle = Math.PI * 1.5; }
                    else if (sx < 0.0 && sy > 0.0) { startAngle = Math.PI * 1.5; endAngle = Math.PI * 2.0; }
                    else if (sx < 0.0 && sy < 0.0) { startAngle = 0.0; endAngle = Math.PI * 0.5; }
                    else { startAngle = Math.PI * 0.5; endAngle = Math.PI; }
                    var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, startAngle, endAngle);
                    arc.SetDatabaseDefaults(document.Database);
                    arc.LayerId = layerId;
                    arc.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    ObjectId arcId = space.AppendEntity(arc);
                    transaction.AddNewlyCreatedDBObject(arc, true);
                    string label = prefix + junction.ToString(CultureInfo.InvariantCulture) + "." + (index + 1).ToString(CultureInfo.InvariantCulture);
                    ObjectId textId = CreateLabel(document.Database, transaction, space, layerId, arc.GetPointAtParameter((arc.StartParam + arc.EndParam) * 0.5), label, textPaper, arcId, centre);
                    generated.Add(arcId);
                    generated.Add(textId);
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n{0} complete. Junction={1}{2}; linked bellmouths={3}.", cross ? "CE_ROADCROSSJUNCTION" : "CE_ROADTJUNCTION", prefix, junction, count);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return 0;
                foreach (ObjectId id in model)
                {
                    MText label;
                    try { label = transaction.GetObject(id, OpenMode.ForWrite, false) as MText; }
                    catch { continue; }
                    if (label == null) continue;
                    JunctionLink link;
                    if (!TryReadLink(label, document.Database, out link)) continue;
                    Curve curve;
                    try { curve = transaction.GetObject(link.SourceId, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve == null) continue;
                    Point3d anchor;
                    try { anchor = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5); }
                    catch { continue; }
                    label.Location = anchor + link.Offset;
                    refreshed++;
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static int NumberSelection(
            Document document,
            IEnumerable<ObjectId> ids,
            string prefix,
            int start,
            double clusterDistance,
            double textPaper,
            bool clockwise,
            string groupOrder,
            Point3d? pickedStart)
        {
            var items = new List<JunctionCurveItem>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Distinct())
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve == null) continue;
                    Point3d midpoint;
                    try { midpoint = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5); }
                    catch { continue; }
                    items.Add(new JunctionCurveItem(id, midpoint));
                }
            }
            if (items.Count == 0) return 0;

            List<List<JunctionCurveItem>> groups = OrderGroups(
                Cluster(items, clusterDistance),
                groupOrder,
                clusterDistance,
                pickedStart);
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(document.Database, transaction);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return 0;
                EraseExistingLabels(transaction, space, new HashSet<ObjectId>(items.Select(item => item.Id)));
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    List<JunctionCurveItem> group = groups[groupIndex];
                    Point3d centre = GroupCentre(group);
                    List<JunctionCurveItem> ordered = group
                        .OrderBy(item => ClockwiseKey(item.Anchor, centre))
                        .ToList();
                    if (!clockwise) ordered.Reverse();
                    if (pickedStart.HasValue && groupIndex == 0)
                        ordered = RotateToNearest(ordered, pickedStart.Value);
                    int returnIndex = 1;
                    foreach (JunctionCurveItem item in ordered)
                    {
                        string label = prefix + (start + groupIndex).ToString(CultureInfo.InvariantCulture) + "." + returnIndex.ToString(CultureInfo.InvariantCulture);
                        CreateLabel(document.Database, transaction, space, layerId, item.Anchor, label, textPaper, item.Id, centre);
                        returnIndex++;
                        created++;
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static List<List<JunctionCurveItem>> OrderGroups(
            IList<List<JunctionCurveItem>> groups,
            string order,
            double clusterDistance,
            Point3d? pickedStart)
        {
            double rowBand = Math.Max(clusterDistance * 0.50, 0.001);
            IEnumerable<List<JunctionCurveItem>> ordered;
            if (string.Equals(order, "Left to right", StringComparison.OrdinalIgnoreCase))
            {
                ordered = groups.OrderBy(group => GroupCentre(group).X)
                    .ThenByDescending(group => GroupCentre(group).Y);
            }
            else if (string.Equals(order, "Top to bottom", StringComparison.OrdinalIgnoreCase))
            {
                ordered = groups.OrderByDescending(group => GroupCentre(group).Y)
                    .ThenBy(group => GroupCentre(group).X);
            }
            else
            {
                ordered = groups
                    .OrderByDescending(group => Math.Round(GroupCentre(group).Y / rowBand))
                    .ThenBy(group => GroupCentre(group).X)
                    .ThenByDescending(group => GroupCentre(group).Y);
            }
            var result = ordered.ToList();
            if (!pickedStart.HasValue || result.Count < 2) return result;
            int startIndex = 0;
            double best = double.MaxValue;
            for (int index = 0; index < result.Count; index++)
            {
                double distance = GroupCentre(result[index]).DistanceTo(pickedStart.Value);
                if (distance < best) { best = distance; startIndex = index; }
            }
            return result.Skip(startIndex).Concat(result.Take(startIndex)).ToList();
        }

        private static Point3d GroupCentre(IList<JunctionCurveItem> group)
        {
            return new Point3d(
                group.Average(item => item.Anchor.X),
                group.Average(item => item.Anchor.Y),
                group.Average(item => item.Anchor.Z));
        }

        private static List<JunctionCurveItem> RotateToNearest(
            IList<JunctionCurveItem> items,
            Point3d picked)
        {
            if (items == null || items.Count == 0) return new List<JunctionCurveItem>();
            int start = 0;
            double best = double.MaxValue;
            for (int index = 0; index < items.Count; index++)
            {
                double distance = items[index].Anchor.DistanceTo(picked);
                if (distance < best) { best = distance; start = index; }
            }
            return items.Skip(start).Concat(items.Take(start)).ToList();
        }

        private static void EraseExistingLabels(
            Transaction transaction,
            BlockTableRecord space,
            ISet<ObjectId> sourceIds)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                MText label;
                try { label = transaction.GetObject(id, OpenMode.ForWrite, false) as MText; }
                catch { continue; }
                if (label == null) continue;
                JunctionLink link;
                if (!TryReadLink(label, space.Database, out link) || !sourceIds.Contains(link.SourceId)) continue;
                try { label.Erase(); } catch { }
            }
        }

        private static List<List<JunctionCurveItem>> Cluster(IList<JunctionCurveItem> items, double distance)
        {
            var remaining = new HashSet<JunctionCurveItem>(items);
            var result = new List<List<JunctionCurveItem>>();
            while (remaining.Count > 0)
            {
                JunctionCurveItem seed = remaining.First();
                var group = new List<JunctionCurveItem>();
                var queue = new Queue<JunctionCurveItem>();
                queue.Enqueue(seed);
                remaining.Remove(seed);
                while (queue.Count > 0)
                {
                    JunctionCurveItem current = queue.Dequeue();
                    group.Add(current);
                    foreach (JunctionCurveItem candidate in remaining.Where(item => item.Anchor.DistanceTo(current.Anchor) <= distance).ToList())
                    {
                        remaining.Remove(candidate);
                        queue.Enqueue(candidate);
                    }
                }
                result.Add(group);
            }
            return result;
        }

        private static double ClockwiseKey(Point3d point, Point3d centre)
        {
            double angle = Math.Atan2(point.Y - centre.Y, point.X - centre.X);
            double start = Math.PI * 0.75;
            double clockwise = start - angle;
            while (clockwise < 0.0) clockwise += Math.PI * 2.0;
            while (clockwise >= Math.PI * 2.0) clockwise -= Math.PI * 2.0;
            return clockwise;
        }

        private static ObjectId CreateLabel(Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId, Point3d anchor, string text, double paperHeight, ObjectId sourceId, Point3d groupCentre)
        {
            double height = PaperAnnotationScale.ModelTextHeight(database, paperHeight);
            Vector3d radial = anchor - groupCentre;
            if (radial.Length < 1e-8) radial = Vector3d.YAxis;
            radial = radial.GetNormal() * Math.Max(height * 2.0, 0.001);
            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.LayerId = layerId;
            label.Location = anchor + radial;
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.TextHeight = height;
            label.Contents = text;
            label.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
            PaperAnnotationScale.SetAnnotative(label);
            ObjectId id = space.AppendEntity(label);
            transaction.AddNewlyCreatedDBObject(label, true);
            label.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceId.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataReal, radial.X),
                new TypedValue((int)DxfCode.ExtendedDataReal, radial.Y),
                new TypedValue((int)DxfCode.ExtendedDataReal, radial.Z));
            return id;
        }

        private static bool TryReadLink(MText text, Database database, out JunctionLink link)
        {
            link = null;
            ResultBuffer buffer = text.GetXDataForApplication(AppName);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 5) return false;
            try
            {
                long handle = long.Parse(Convert.ToString(values[1].Value), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ObjectId id = database.GetObjectId(false, new Handle(handle), 0);
                link = new JunctionLink(id, new Vector3d(Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture)));
                return !id.IsNull;
            }
            catch { return false; }
        }

        private static double[] SelectTQuadrants(double mainAngle, double sideAngle)
        {
            Vector2d main = new Vector2d(Math.Cos(mainAngle), Math.Sin(mainAngle));
            Vector2d side = new Vector2d(Math.Cos(sideAngle), Math.Sin(sideAngle));
            double cross = main.X * side.Y - main.Y * side.X;
            return cross >= 0.0
                ? new[] { Math.PI * 0.75, Math.PI * 0.25 }
                : new[] { -Math.PI * 0.25, -Math.PI * 0.75 };
        }

        private static ProductionSettingsDialogModel NumberingSettings()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Junction Bellmouth Numbering",
                "Choose the junction sequence explicitly. Top-left to bottom-right is the default; horizontal roads can run left to right and vertical roads can run top to bottom. A picked start rotates the sequence to the selected junction/return.");
            model.AddText("Prefix", "01 Numbering", "Junction prefix", "J", "J creates J1.1, J1.2, J2.1...");
            model.AddPositiveInteger("Start", "01 Numbering", "Starting junction number", 1, "First automatic junction group number.");
            model.AddChoice("GroupOrder", "01 Numbering", "Junction group direction", "Top-left to bottom-right", "Use left-to-right for horizontal roads and top-to-bottom for vertical roads.", new[] { "Top-left to bottom-right", "Left to right", "Top to bottom" });
            model.AddChoice("StartMode", "01 Numbering", "Sequence start", "Automatic start", "Pick a junction/return when numbering must start from a specific existing point.", new[] { "Automatic start", "Pick start junction / return" });
            model.AddChoice("Direction", "01 Numbering", "Return direction inside each junction", "Clockwise", "The automatic corner is top-left; a picked first return overrides the first junction start.", new[] { "Clockwise", "Counter-clockwise" });
            model.AddDouble("Cluster", "02 Grouping", "Junction grouping distance", 35.0, "Bellmouth midpoints within this distance are treated as one junction.");
            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative label paper height.");
            return model;
        }

        private static PromptSelectionResult SelectCurves(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0) return implied;
            return editor.GetSelection(new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
        }

        private static string CleanPrefix(string value, string fallback)
        {
            string cleaned = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
        }

        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table == null || table.Has(AppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(LayerName)) return layers[LayerName];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = LayerName, Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private sealed class JunctionCurveItem
        {
            internal JunctionCurveItem(ObjectId id, Point3d anchor) { Id = id; Anchor = anchor; }
            internal ObjectId Id { get; private set; }
            internal Point3d Anchor { get; private set; }
        }

        private sealed class JunctionLink
        {
            internal JunctionLink(ObjectId sourceId, Vector3d offset) { SourceId = sourceId; Offset = offset; }
            internal ObjectId SourceId { get; private set; }
            internal Vector3d Offset { get; private set; }
        }
    }
}
