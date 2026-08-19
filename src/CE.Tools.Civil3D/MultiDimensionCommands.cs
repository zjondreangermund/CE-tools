using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.MultiDimensionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch dimensions AutoCAD polylines and Civil 3D feature lines. Output uses
    /// an annotative CE copy of the selected drawing dimension style so existing
    /// dimensions that reference the user's source style are never changed.
    /// </summary>
    public sealed class MultiDimensionCommands
    {
        private const double GeometryTolerance = 1e-8;

        [CommandMethod("CE_TOOLS", "CE_MULTIDIM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MultiDimension()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<string> dimensionStyles;
            string currentStyle;
            ReadDimensionStyles(document.Database, out dimensionStyles, out currentStyle);
            if (dimensionStyles.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_MULTIDIM stopped. No usable dimension styles were found in this drawing.");
                return;
            }

            const string aligned = "Aligned - straight segments";
            const string horizontal = "Linear - horizontal";
            const string vertical = "Linear - vertical";
            const string angular = "Angular - line vertices";
            const string radius = "Radius - polyline arc segments";
            const string arcLength = "Arc length / along curve - polyline arcs";
            const string all = "All applicable geometry";

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Dimensions",
                "Add multiple annotative dimensions to multiple AutoCAD polylines and Civil 3D feature lines. Arc length and radius dimensions use true polyline arc geometry.");
            settings.AddChoice(
                "Type", "01 Dimension", "Dimension type", aligned,
                "Choose one dimension family or All applicable geometry.",
                new[] { aligned, horizontal, vertical, angular, radius, arcLength, all });
            settings.AddChoice(
                "DimStyle", "01 Dimension", "Dimension style", currentStyle,
                "Select a drawing dimension style. CE Tools creates/updates an annotative copy so the source style and existing dimensions remain unchanged.",
                dimensionStyles);
            settings.AddPositiveDouble(
                "Offset", "02 Placement", "Dimension offset (paper mm)", 8.0,
                "Automatic offset from the source geometry, converted using the current annotation scale.");
            settings.AddPositiveDouble(
                "ArcLeader", "02 Placement", "Radius leader extension (paper mm)", 6.0,
                "Leader extension beyond a polyline arc for radius dimensions.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple polylines and/or Civil 3D feature lines to dimension: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                selection = document.Editor.GetSelection(options);
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            string mode = settings.Text("Type");
            string requestedStyle = settings.Text("DimStyle");
            double offset = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("Offset", 8.0));
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));

            int sources = 0;
            int dimensions = 0;
            int skippedSources = 0;
            int skippedGeometry = 0;
            int failed = 0;
            string outputStyleName = string.Empty;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId styleId = EnsureAnnotativeDimensionStyle(
                        document.Database,
                        transaction,
                        requestedStyle,
                        out outputStyleName);
                    if (styleId.IsNull)
                    {
                        document.Editor.WriteMessage("\nCE_MULTIDIM stopped. The selected dimension style could not be prepared.");
                        return;
                    }

                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null) return;

                    foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    {
                        Entity entity = null;
                        try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { failed++; continue; }
                        if (entity == null) { skippedSources++; continue; }

                        Polyline polyline = entity as Polyline;
                        if (polyline != null)
                        {
                            sources++;
                            ProcessPolyline(
                                document.Database,
                                transaction,
                                space,
                                polyline,
                                mode,
                                styleId,
                                offset,
                                leader,
                                ref dimensions,
                                ref skippedGeometry,
                                ref failed);
                            continue;
                        }

                        CivilFeatureLine featureLine = entity as CivilFeatureLine;
                        if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
                        {
                            sources++;
                            ProcessFeatureLine(
                                document.Database,
                                transaction,
                                space,
                                featureLine,
                                mode,
                                styleId,
                                offset,
                                ref dimensions,
                                ref skippedGeometry,
                                ref failed);
                            continue;
                        }

                        skippedSources++;
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_MULTIDIM stopped. {0}", exception.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_MULTIDIM complete. Sources={0}; dimensions={1}; unsupported sources={2}; non-applicable geometry={3}; failed={4}; style={5}.",
                sources,
                dimensions,
                skippedSources,
                skippedGeometry,
                failed,
                outputStyleName);
        }

        private static void ProcessPolyline(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Polyline polyline,
            string mode,
            ObjectId styleId,
            double offset,
            double leader,
            ref int created,
            ref int skipped,
            ref int failed)
        {
            int vertexCount = polyline.NumberOfVertices;
            if (vertexCount < 2) { skipped++; return; }
            Point3d centroid = PolylineCentroid(polyline);
            int segmentCount = polyline.Closed ? vertexCount : vertexCount - 1;
            bool doAll = string.Equals(mode, "All applicable geometry", StringComparison.OrdinalIgnoreCase);

            for (int index = 0; index < segmentCount; index++)
            {
                int nextIndex = (index + 1) % vertexCount;
                Point3d start = Plan(polyline.GetPoint3dAt(index));
                Point3d end = Plan(polyline.GetPoint3dAt(nextIndex));
                if (start.DistanceTo(end) <= GeometryTolerance) { skipped++; continue; }

                double bulge = 0.0;
                try { bulge = polyline.GetBulgeAt(index); }
                catch { bulge = 0.0; }
                bool isArc = Math.Abs(bulge) > GeometryTolerance;

                try
                {
                    if (!isArc && (doAll || string.Equals(mode, "Aligned - straight segments", StringComparison.OrdinalIgnoreCase)))
                    {
                        Point3d dimPoint = OffsetLinePoint(start, end, centroid, offset);
                        AddDimension(database, transaction, space,
                            new AlignedDimension(start, end, dimPoint, "<>", styleId), ref created);
                    }
                    else if (!isArc && string.Equals(mode, "Linear - horizontal", StringComparison.OrdinalIgnoreCase))
                    {
                        Point3d dimPoint = HorizontalDimPoint(start, end, centroid, offset);
                        AddDimension(database, transaction, space,
                            new RotatedDimension(0.0, start, end, dimPoint, "<>", styleId), ref created);
                    }
                    else if (!isArc && string.Equals(mode, "Linear - vertical", StringComparison.OrdinalIgnoreCase))
                    {
                        Point3d dimPoint = VerticalDimPoint(start, end, centroid, offset);
                        AddDimension(database, transaction, space,
                            new RotatedDimension(Math.PI * 0.5, start, end, dimPoint, "<>", styleId), ref created);
                    }

                    if (isArc && (doAll || string.Equals(mode, "Arc length / along curve - polyline arcs", StringComparison.OrdinalIgnoreCase)))
                    {
                        ArcGeometry arc;
                        if (TryArc(start, end, bulge, out arc))
                        {
                            Vector3d radial = Unit(arc.MidPoint - arc.Center);
                            Point3d arcPoint = arc.Center + radial * (arc.Radius + offset);
                            AddDimension(database, transaction, space,
                                new ArcDimension(arc.Center, start, end, arcPoint, "<>", styleId), ref created);
                        }
                        else skipped++;
                    }

                    if (isArc && (doAll || string.Equals(mode, "Radius - polyline arc segments", StringComparison.OrdinalIgnoreCase)))
                    {
                        ArcGeometry arc;
                        if (TryArc(start, end, bulge, out arc))
                        {
                            AddDimension(database, transaction, space,
                                new RadialDimension(arc.Center, arc.MidPoint, leader, "<>", styleId), ref created);
                        }
                        else skipped++;
                    }
                }
                catch { failed++; }
            }

            if (doAll || string.Equals(mode, "Angular - line vertices", StringComparison.OrdinalIgnoreCase))
            {
                int first = polyline.Closed ? 0 : 1;
                int lastExclusive = polyline.Closed ? vertexCount : vertexCount - 1;
                for (int vertex = first; vertex < lastExclusive; vertex++)
                {
                    int previousVertex = (vertex - 1 + vertexCount) % vertexCount;
                    int nextVertex = (vertex + 1) % vertexCount;
                    int previousSegment = previousVertex;
                    int nextSegment = vertex;
                    double previousBulge = 0.0;
                    double nextBulge = 0.0;
                    try { previousBulge = polyline.GetBulgeAt(previousSegment); } catch { }
                    try { nextBulge = polyline.GetBulgeAt(nextSegment); } catch { }
                    if (Math.Abs(previousBulge) > GeometryTolerance || Math.Abs(nextBulge) > GeometryTolerance)
                    {
                        skipped++;
                        continue;
                    }

                    Point3d center = Plan(polyline.GetPoint3dAt(vertex));
                    Point3d previous = Plan(polyline.GetPoint3dAt(previousVertex));
                    Point3d next = Plan(polyline.GetPoint3dAt(nextVertex));
                    try
                    {
                        LineAngularDimension2 dimension = CreateAngular(
                            center,
                            previous,
                            next,
                            centroid,
                            offset,
                            styleId);
                        if (dimension == null) { skipped++; continue; }
                        AddDimension(database, transaction, space, dimension, ref created);
                    }
                    catch { failed++; }
                }
            }
        }

        private static void ProcessFeatureLine(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            CivilFeatureLine featureLine,
            string mode,
            ObjectId styleId,
            double offset,
            ref int created,
            ref int skipped,
            ref int failed)
        {
            Point3dCollection collection;
            try { collection = featureLine.GetPoints(FeatureLinePointType.AllPoints); }
            catch { skipped++; return; }
            if (collection == null || collection.Count < 2) { skipped++; return; }

            List<Point3d> points = RemoveRepeatedPlanPoints(collection.Cast<Point3d>().Select(Plan).ToList());
            if (points.Count < 2) { skipped++; return; }
            Point3d centroid = Average(points);
            bool doAll = string.Equals(mode, "All applicable geometry", StringComparison.OrdinalIgnoreCase);
            bool linearMode = doAll ||
                string.Equals(mode, "Aligned - straight segments", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Linear - horizontal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Linear - vertical", StringComparison.OrdinalIgnoreCase);

            if (linearMode)
            {
                for (int index = 0; index < points.Count - 1; index++)
                {
                    Point3d start = points[index];
                    Point3d end = points[index + 1];
                    if (start.DistanceTo(end) <= GeometryTolerance) { skipped++; continue; }
                    try
                    {
                        Dimension dimension;
                        if (string.Equals(mode, "Linear - horizontal", StringComparison.OrdinalIgnoreCase))
                            dimension = new RotatedDimension(0.0, start, end, HorizontalDimPoint(start, end, centroid, offset), "<>", styleId);
                        else if (string.Equals(mode, "Linear - vertical", StringComparison.OrdinalIgnoreCase))
                            dimension = new RotatedDimension(Math.PI * 0.5, start, end, VerticalDimPoint(start, end, centroid, offset), "<>", styleId);
                        else
                            dimension = new AlignedDimension(start, end, OffsetLinePoint(start, end, centroid, offset), "<>", styleId);
                        AddDimension(database, transaction, space, dimension, ref created);
                    }
                    catch { failed++; }
                }
            }

            if (doAll || string.Equals(mode, "Angular - line vertices", StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 1; index < points.Count - 1; index++)
                {
                    try
                    {
                        LineAngularDimension2 dimension = CreateAngular(
                            points[index],
                            points[index - 1],
                            points[index + 1],
                            centroid,
                            offset,
                            styleId);
                        if (dimension == null) { skipped++; continue; }
                        AddDimension(database, transaction, space, dimension, ref created);
                    }
                    catch { failed++; }
                }
            }

            if (string.Equals(mode, "Radius - polyline arc segments", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Arc length / along curve - polyline arcs", StringComparison.OrdinalIgnoreCase))
            {
                // Civil FeatureLine.GetPoints exposes control/elevation points but not a
                // stable AutoCAD-2023 managed arc-segment API. Never invent a radius.
                skipped++;
            }
        }

        private static void AddDimension(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Dimension dimension,
            ref int created)
        {
            dimension.SetDatabaseDefaults(database);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            try { dimension.SetFromStyle(); } catch { PaperAnnotationScale.SetAnnotative(dimension); }
            created++;
        }

        private static LineAngularDimension2 CreateAngular(
            Point3d vertex,
            Point3d previous,
            Point3d next,
            Point3d centroid,
            double offset,
            ObjectId styleId)
        {
            Vector3d first = previous - vertex;
            Vector3d second = next - vertex;
            if (first.Length <= GeometryTolerance || second.Length <= GeometryTolerance) return null;
            first = first.GetNormal();
            second = second.GetNormal();
            Vector3d bisector = first + second;
            if (bisector.Length <= GeometryTolerance)
            {
                bisector = new Vector3d(-first.Y, first.X, 0.0);
            }
            else bisector = bisector.GetNormal();

            Point3d option1 = vertex + bisector * offset;
            Point3d option2 = vertex - bisector * offset;
            Point3d arcPoint = option1.DistanceTo(centroid) >= option2.DistanceTo(centroid)
                ? option1
                : option2;

            var dimension = new LineAngularDimension2();
            dimension.XLine1Start = vertex;
            dimension.XLine1End = previous;
            dimension.XLine2Start = vertex;
            dimension.XLine2End = next;
            dimension.ArcPoint = arcPoint;
            dimension.DimensionStyle = styleId;
            dimension.DimensionText = "<>";
            return dimension;
        }

        private static Point3d OffsetLinePoint(Point3d start, Point3d end, Point3d centroid, double offset)
        {
            Point3d midpoint = Mid(start, end);
            Vector3d line = end - start;
            if (line.Length <= GeometryTolerance) return midpoint;
            Vector3d normal = new Vector3d(-line.Y, line.X, 0.0).GetNormal();
            Point3d option1 = midpoint + normal * offset;
            Point3d option2 = midpoint - normal * offset;
            return option1.DistanceTo(centroid) >= option2.DistanceTo(centroid) ? option1 : option2;
        }

        private static Point3d HorizontalDimPoint(Point3d start, Point3d end, Point3d centroid, double offset)
        {
            Point3d midpoint = Mid(start, end);
            double sign = midpoint.Y >= centroid.Y ? 1.0 : -1.0;
            return new Point3d(midpoint.X, midpoint.Y + sign * offset, 0.0);
        }

        private static Point3d VerticalDimPoint(Point3d start, Point3d end, Point3d centroid, double offset)
        {
            Point3d midpoint = Mid(start, end);
            double sign = midpoint.X >= centroid.X ? 1.0 : -1.0;
            return new Point3d(midpoint.X + sign * offset, midpoint.Y, 0.0);
        }

        private static bool TryArc(Point3d start, Point3d end, double bulge, out ArcGeometry arc)
        {
            arc = new ArcGeometry();
            double chord = start.DistanceTo(end);
            if (chord <= GeometryTolerance || Math.Abs(bulge) <= GeometryTolerance) return false;

            double radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
            Vector3d direction = end - start;
            Vector3d left = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
            double centerOffset = chord * (1.0 - bulge * bulge) / (4.0 * bulge);
            Point3d center = Mid(start, end) + left * centerOffset;
            double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double sweep = 4.0 * Math.Atan(bulge);
            double middleAngle = startAngle + sweep * 0.5;
            Point3d middle = new Point3d(
                center.X + radius * Math.Cos(middleAngle),
                center.Y + radius * Math.Sin(middleAngle),
                0.0);

            arc.Center = center;
            arc.Radius = radius;
            arc.MidPoint = middle;
            return radius > GeometryTolerance;
        }

        private static void ReadDimensionStyles(Database database, out List<string> names, out string current)
        {
            names = new List<string>();
            current = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DimStyleTable table = transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead, false) as DimStyleTable;
                if (table != null)
                {
                    foreach (ObjectId id in table)
                    {
                        DimStyleTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as DimStyleTableRecord;
                        if (record == null || record.IsDependent || string.IsNullOrWhiteSpace(record.Name)) continue;
                        names.Add(record.Name);
                    }
                }
                DimStyleTableRecord active = transaction.GetObject(database.Dimstyle, OpenMode.ForRead, false) as DimStyleTableRecord;
                if (active != null) current = active.Name;
            }
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(current) || !names.Contains(current, StringComparer.OrdinalIgnoreCase))
                current = names.FirstOrDefault() ?? string.Empty;
        }

        private static ObjectId EnsureAnnotativeDimensionStyle(
            Database database,
            Transaction transaction,
            string selectedName,
            out string outputName)
        {
            outputName = string.Empty;
            DimStyleTable table = transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead, false) as DimStyleTable;
            if (table == null || string.IsNullOrWhiteSpace(selectedName) || !table.Has(selectedName)) return ObjectId.Null;
            ObjectId sourceId = table[selectedName];
            DimStyleTableRecord source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as DimStyleTableRecord;
            if (source == null) return ObjectId.Null;

            string safeBaseName = selectedName.Trim();
            string cloneName = safeBaseName.StartsWith("CE-ANN-", StringComparison.OrdinalIgnoreCase)
                ? safeBaseName
                : "CE-ANN-" + safeBaseName;
            if (cloneName.Length > 250) cloneName = cloneName.Substring(0, 250);

            DimStyleTableRecord target;
            ObjectId targetId;
            if (table.Has(cloneName))
            {
                targetId = table[cloneName];
                target = transaction.GetObject(targetId, OpenMode.ForWrite, false) as DimStyleTableRecord;
                if (target == null) return ObjectId.Null;
                if (targetId != sourceId) target.CopyFrom(source);
            }
            else
            {
                table.UpgradeOpen();
                target = new DimStyleTableRecord();
                target.CopyFrom(source);
                target.Name = cloneName;
                PaperAnnotationScale.SetAnnotative(target);
                try { target.Dimscale = 0.0; } catch { }
                targetId = table.Add(target);
                transaction.AddNewlyCreatedDBObject(target, true);
            }

            PaperAnnotationScale.SetAnnotative(target);
            try { target.Dimscale = 0.0; } catch { }
            outputName = cloneName;
            return targetId;
        }

        private static Point3d PolylineCentroid(Polyline polyline)
        {
            var points = new List<Point3d>();
            for (int index = 0; index < polyline.NumberOfVertices; index++)
                points.Add(Plan(polyline.GetPoint3dAt(index)));
            return Average(points);
        }

        private static Point3d Average(IList<Point3d> points)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            double x = 0.0;
            double y = 0.0;
            foreach (Point3d point in points) { x += point.X; y += point.Y; }
            return new Point3d(x / points.Count, y / points.Count, 0.0);
        }

        private static List<Point3d> RemoveRepeatedPlanPoints(List<Point3d> points)
        {
            var result = new List<Point3d>();
            foreach (Point3d point in points)
            {
                if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > GeometryTolerance)
                    result.Add(point);
            }
            return result;
        }

        private static Point3d Plan(Point3d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private static Point3d Mid(Point3d first, Point3d second)
        {
            return new Point3d((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5, 0.0);
        }

        private static Vector3d Unit(Vector3d vector)
        {
            return vector.Length <= GeometryTolerance ? Vector3d.XAxis : vector.GetNormal();
        }

        private struct ArcGeometry
        {
            public Point3d Center;
            public Point3d MidPoint;
            public double Radius;
        }
    }
}
