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
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.GradingDrainageDiagnosticCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Non-destructive grading diagnostics. Source geometry is never edited;
    /// generated review lines, circles and labels can be cleared independently.
    /// </summary>
    public sealed class GradingDrainageDiagnosticCommands
    {
        private const string RegAppName = "CE_GRADING_REVIEW";
        private const string LowSlopeLayer = "CE-REVIEW-LOW-SLOPE";
        private const string LowPointLayer = "CE-REVIEW-LOW-POINT";
        private const double GeometryTolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_GRADINGDIAGNOSTICS", CommandFlags.Modal)]
        public void GradingDiagnostics()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nGrading diagnostics [LowSlope/LowPoints/Clear] <LowSlope>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("LowSlope");
            options.Keywords.Add("LowPoints");
            options.Keywords.Add("Clear");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string command = result.Status == PromptStatus.OK
                ? result.StringResult
                : "LowSlope";
            if (string.Equals(command, "LowPoints", StringComparison.OrdinalIgnoreCase))
                document.SendStringToExecute("CE_LOWPOINTS ", true, false, true);
            else if (string.Equals(command, "Clear", StringComparison.OrdinalIgnoreCase))
                document.SendStringToExecute("CE_GRADINGREVIEWCLEAR ", true, false, true);
            else
                document.SendStringToExecute("CE_LOWSLOPE ", true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_LOWSLOPE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void HighlightLowSlopes()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double threshold;
            if (!PromptPositiveDouble(
                    editor,
                    "Minimum acceptable absolute grade (%)",
                    0.5,
                    out threshold))
            {
                return;
            }

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect feature lines, lines, 2D polylines or 3D polylines to analyse: ");
            if (selection.Status != PromptStatus.OK) return;

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
                return;

            List<SlopeObservation> observations = ReadSlopeObservations(
                document.Database,
                selection);
            List<SlopeObservation> low = observations
                .Where(item => Math.Abs(item.GradePercent) < threshold)
                .ToList();
            if (low.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_LOWSLOPE complete. Analysed segments={0}; none were below {1:N3}%.",
                    observations.Count,
                    threshold);
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Analysed segments", observations.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Segments below threshold", low.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Threshold", threshold.ToString("N3", CultureInfo.CurrentCulture) + "%"),
                Pair("Output layer", LowSlopeLayer),
                Pair("Source geometry changed", "No")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Low-Slope Review",
                    "Red review lines and grade labels will be created over segments with an absolute grade below the selected threshold.",
                    review,
                    "Create Review"))
            {
                editor.WriteMessage("\nCE_LOWSLOPE cancelled. No review graphics were created.");
                return;
            }

            int created = CreateLowSlopeGraphics(
                document.Database,
                low,
                threshold,
                annotation.TextHeight);
            editor.Regen();
            editor.WriteMessage(
                "\nCE_LOWSLOPE complete. Review segments created={0}; threshold={1:N3}%.",
                created,
                threshold);

            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Low-Slope Results",
                "These are review observations only. Use CE_GRADINGREVIEWCLEAR to remove generated graphics.",
                BuildSlopeRows(low, threshold),
                "CE TOOLS LOW-SLOPE REVIEW");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_LOWPOINTS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void IdentifyLowPoints()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect feature lines, lines, 2D polylines or 3D polylines to inspect for low points: ");
            if (selection.Status != PromptStatus.OK) return;

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
                return;

            List<LowPointObservation> lowPoints = ReadLowPoints(
                document.Database,
                selection);
            if (lowPoints.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_LOWPOINTS complete. No usable low points were found in the selected geometry.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Candidate low points", lowPoints.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Output layer", LowPointLayer),
                Pair("Source geometry changed", "No"),
                Pair("Marker type", "Circle and elevation label")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Low-Point Review",
                    "Candidate local and global low points will be marked without changing the selected source geometry.",
                    review,
                    "Create Markers"))
            {
                editor.WriteMessage("\nCE_LOWPOINTS cancelled. No review graphics were created.");
                return;
            }

            int created = CreateLowPointGraphics(
                document.Database,
                lowPoints,
                annotation.TextHeight);
            editor.Regen();
            editor.WriteMessage(
                "\nCE_LOWPOINTS complete. Candidate low-point markers created={0}.",
                created);

            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Candidate Low Points",
                "Review points against the design surface and drainage intent before using them for hydraulic calculations.",
                BuildLowPointRows(lowPoints),
                "CE TOOLS CANDIDATE LOW POINTS");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_GRADINGREVIEWCLEAR",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearReviewGraphics()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            int count = CountReviewObjects(document.Database);
            if (count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_GRADINGREVIEWCLEAR: no CE grading review graphics were found in the current space.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Review objects to remove", count.ToString(CultureInfo.InvariantCulture)),
                Pair("Design/source objects retained", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Grading Review",
                    "Only CE-generated low-slope and low-point review graphics will be erased.",
                    rows,
                    "Clear Review"))
            {
                document.Editor.WriteMessage("\nCE_GRADINGREVIEWCLEAR cancelled.");
                return;
            }

            int erased = EraseReviewObjects(document.Database);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_GRADINGREVIEWCLEAR complete. Review objects removed={0}.",
                erased);
        }

        private static List<SlopeObservation> ReadSlopeObservations(
            Database database,
            PromptSelectionResult selection)
        {
            var result = new List<SlopeObservation>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    DBObject value = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false);
                    List<Point3d> points = ReadPoints(value, transaction);
                    if (points.Count < 2) continue;

                    string source = FriendlyName(value) + " " + selected.ObjectId.Handle;
                    for (int index = 0; index < points.Count - 1; index++)
                    {
                        Point3d start = points[index];
                        Point3d end = points[index + 1];
                        double horizontal = PlanDistance(start, end);
                        if (horizontal <= GeometryTolerance) continue;
                        double grade = ((end.Z - start.Z) / horizontal) * 100.0;
                        result.Add(new SlopeObservation(
                            selected.ObjectId,
                            source,
                            index + 1,
                            start,
                            end,
                            horizontal,
                            grade));
                    }
                }
            }
            return result;
        }

        private static List<LowPointObservation> ReadLowPoints(
            Database database,
            PromptSelectionResult selection)
        {
            var observations = new List<LowPointObservation>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    DBObject value = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false);
                    List<Point3d> points = ReadPoints(value, transaction);
                    if (points.Count == 0) continue;

                    string source = FriendlyName(value) + " " + selected.ObjectId.Handle;
                    int globalIndex = 0;
                    for (int index = 1; index < points.Count; index++)
                    {
                        if (points[index].Z < points[globalIndex].Z)
                            globalIndex = index;
                    }

                    var accepted = new HashSet<int>();
                    accepted.Add(globalIndex);
                    for (int index = 1; index < points.Count - 1; index++)
                    {
                        if (points[index].Z <= points[index - 1].Z + GeometryTolerance &&
                            points[index].Z <= points[index + 1].Z + GeometryTolerance)
                        {
                            accepted.Add(index);
                        }
                    }

                    foreach (int index in accepted.OrderBy(item => item))
                    {
                        observations.Add(new LowPointObservation(
                            selected.ObjectId,
                            source,
                            index + 1,
                            points[index],
                            index == globalIndex ? "Global minimum" : "Local minimum"));
                    }
                }
            }

            return observations
                .GroupBy(item => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:R}|{1:R}|{2:R}",
                    item.Location.X,
                    item.Location.Y,
                    item.Location.Z))
                .Select(group => group.First())
                .OrderBy(item => item.Location.Z)
                .ToList();
        }

        private static List<Point3d> ReadPoints(
            DBObject value,
            Transaction transaction)
        {
            var points = new List<Point3d>();
            Line line = value as Line;
            if (line != null)
            {
                points.Add(line.StartPoint);
                points.Add(line.EndPoint);
                return points;
            }

            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    points.Add(polyline.GetPoint3dAt(index));
                if (polyline.Closed && points.Count > 1)
                    points.Add(points[0]);
                return points;
            }

            Polyline3d polyline3d = value as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                if (polyline3d.Closed && points.Count > 1)
                    points.Add(points[0]);
                return points;
            }

            CivilFeatureLine featureLine = value as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection collection = featureLine.GetPoints(
                    FeatureLinePointType.AllPoints);
                foreach (Point3d point in collection)
                    points.Add(point);
                if (featureLine.Closed && points.Count > 1)
                    points.Add(points[0]);
            }
            return points;
        }

        private static int CreateLowSlopeGraphics(
            Database database,
            IList<SlopeObservation> observations,
            double threshold,
            double textHeight)
        {
            int created = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    LowSlopeLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                foreach (SlopeObservation observation in observations)
                {
                    var reviewLine = new Line(observation.Start, observation.End);
                    reviewLine.SetDatabaseDefaults(database);
                    reviewLine.LayerId = layerId;
                    reviewLine.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                    WriteReviewTag(
                        reviewLine,
                        "LowSlopeLine",
                        observation.SourceId.Handle.ToString());
                    currentSpace.AppendEntity(reviewLine);
                    transaction.AddNewlyCreatedDBObject(reviewLine, true);
                    created++;

                    Point3d midpoint = new Point3d(
                        (observation.Start.X + observation.End.X) / 2.0,
                        (observation.Start.Y + observation.End.Y) / 2.0,
                        (observation.Start.Z + observation.End.Z) / 2.0);
                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.LayerId = layerId;
                    label.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                    label.Location = midpoint;
                    label.Attachment = AttachmentPoint.BottomCenter;
                    label.TextHeight = Math.Max(textHeight, 0.001);
                    label.Contents = string.Format(
                        CultureInfo.CurrentCulture,
                        "GRADE {0:N3}% < {1:N3}%",
                        observation.GradePercent,
                        threshold);
                    label.BackgroundFill = true;
                    label.UseBackgroundColor = true;
                    WriteReviewTag(
                        label,
                        "LowSlopeLabel",
                        observation.SourceId.Handle.ToString());
                    currentSpace.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);
                    created++;
                }
                transaction.Commit();
            }
            return created;
        }

        private static int CreateLowPointGraphics(
            Database database,
            IList<LowPointObservation> observations,
            double textHeight)
        {
            int created = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    LowPointLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                double radius = Math.Max(textHeight * 0.75, 0.001);
                foreach (LowPointObservation observation in observations)
                {
                    var circle = new Circle(
                        observation.Location,
                        Vector3d.ZAxis,
                        radius);
                    circle.SetDatabaseDefaults(database);
                    circle.LayerId = layerId;
                    circle.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                    WriteReviewTag(
                        circle,
                        "LowPointMarker",
                        observation.SourceId.Handle.ToString());
                    currentSpace.AppendEntity(circle);
                    transaction.AddNewlyCreatedDBObject(circle, true);
                    created++;

                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.LayerId = layerId;
                    label.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                    label.Location = observation.Location +
                        new Vector3d(radius * 1.2, radius * 1.2, 0.0);
                    label.Attachment = AttachmentPoint.BottomLeft;
                    label.TextHeight = Math.Max(textHeight, 0.001);
                    label.Contents = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}\\PX {1:N3}\\PY {2:N3}\\PZ {3:N3}",
                        observation.Kind,
                        observation.Location.X,
                        observation.Location.Y,
                        observation.Location.Z);
                    label.BackgroundFill = true;
                    label.UseBackgroundColor = true;
                    WriteReviewTag(
                        label,
                        "LowPointLabel",
                        observation.SourceId.Handle.ToString());
                    currentSpace.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);
                    created++;
                }
                transaction.Commit();
            }
            return created;
        }

        private static List<KeyValuePair<string, string>> BuildSlopeRows(
            IList<SlopeObservation> observations,
            double threshold)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Threshold", threshold.ToString("N3", CultureInfo.CurrentCulture) + "%"),
                Pair("Flagged segments", observations.Count.ToString(CultureInfo.InvariantCulture))
            };
            int shown = Math.Min(observations.Count, 100);
            for (int index = 0; index < shown; index++)
            {
                SlopeObservation item = observations[index];
                rows.Add(Pair(
                    item.Source + " segment " +
                    item.SegmentIndex.ToString(CultureInfo.InvariantCulture),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Length {0:N3}; grade {1:N3}%; Z {2:N3} to {3:N3}",
                        item.HorizontalLength,
                        item.GradePercent,
                        item.Start.Z,
                        item.End.Z)));
            }
            if (observations.Count > shown)
                rows.Add(Pair("Additional flagged segments", (observations.Count - shown).ToString(CultureInfo.InvariantCulture)));
            return rows;
        }

        private static List<KeyValuePair<string, string>> BuildLowPointRows(
            IList<LowPointObservation> observations)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Candidate points", observations.Count.ToString(CultureInfo.InvariantCulture))
            };
            int shown = Math.Min(observations.Count, 100);
            for (int index = 0; index < shown; index++)
            {
                LowPointObservation item = observations[index];
                rows.Add(Pair(
                    item.Source + " point " +
                    item.PointIndex.ToString(CultureInfo.InvariantCulture),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}; X {1:N3}; Y {2:N3}; Z {3:N3}",
                        item.Kind,
                        item.Location.X,
                        item.Location.Y,
                        item.Location.Z)));
            }
            return rows;
        }

        private static int CountReviewObjects(Database database)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity != null && entity.GetXDataForApplication(RegAppName) != null)
                        count++;
                }
            }
            return count;
        }

        private static int EraseReviewObjects(Database database)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId objectId in currentSpace.Cast<ObjectId>().ToList())
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null || entity.GetXDataForApplication(RegAppName) == null)
                        continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                    count++;
                }
                transaction.Commit();
            }
            return count;
        }

        private static void WriteReviewTag(
            Entity entity,
            string kind,
            string sourceHandle)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Kind=" + kind),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Source=" + sourceHandle));
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId objectId = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return objectId;
        }

        private static PromptSelectionResult GetSelection(
            Editor editor,
            string message)
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

        private static bool PromptPositiveDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static string FriendlyName(DBObject value)
        {
            if (value == null) return "Object";
            string name = value.GetType().Name;
            return name.Replace("Polyline3d", "3D Polyline").Replace("Polyline", "Polyline");
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class SlopeObservation
    {
        public SlopeObservation(
            ObjectId sourceId,
            string source,
            int segmentIndex,
            Point3d start,
            Point3d end,
            double horizontalLength,
            double gradePercent)
        {
            SourceId = sourceId;
            Source = source;
            SegmentIndex = segmentIndex;
            Start = start;
            End = end;
            HorizontalLength = horizontalLength;
            GradePercent = gradePercent;
        }

        public ObjectId SourceId { get; private set; }
        public string Source { get; private set; }
        public int SegmentIndex { get; private set; }
        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
        public double HorizontalLength { get; private set; }
        public double GradePercent { get; private set; }
    }

    internal sealed class LowPointObservation
    {
        public LowPointObservation(
            ObjectId sourceId,
            string source,
            int pointIndex,
            Point3d location,
            string kind)
        {
            SourceId = sourceId;
            Source = source;
            PointIndex = pointIndex;
            Location = location;
            Kind = kind;
        }

        public ObjectId SourceId { get; private set; }
        public string Source { get; private set; }
        public int PointIndex { get; private set; }
        public Point3d Location { get; private set; }
        public string Kind { get; private set; }
    }
}
