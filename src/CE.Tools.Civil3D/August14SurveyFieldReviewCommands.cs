using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.August14SurveyFieldReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey-production field-review upgrades: popup surface comparison/report,
    /// linked annotative comparison points/tables, multi-source setting-out front
    /// door, and a stable refresh that re-anchors COGO label offsets.
    /// </summary>
    public sealed class August14SurveyFieldReviewCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURFACECOMPAREPRODUCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceComparisonProduction()
        {
            Document document = Active();
            if (document == null) return;

            ObjectId baseSurfaceId;
            ObjectId comparisonSurfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectPair(
                    document,
                    "CE Tools - Surface Comparison",
                    "Choose the base and comparison Civil 3D surfaces, then pick one or more comparison points.",
                    "Base surface",
                    "Comparison surface",
                    out baseSurfaceId,
                    out comparisonSurfaceId))
                return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Surface Comparison Points",
                "Pick multiple points after this window closes. Each CE point/annotation is linked to both surfaces and the combined table can be refreshed after either surface changes.");
            settings.AddText("Prefix", "01 Points", "Point prefix", "P", "Prefix used for continuous point numbering.");
            settings.AddPositiveInteger("Start", "01 Points", "Start number", 1, "First point number.");
            settings.AddPaperHeight("PaperHeight", "02 Annotation", "Paper text height", 2.5, "Absolute paper text height for linked comparison annotations.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix")) ? "P" : settings.Text("Prefix").Trim();
            int start = settings.Integer("Start", 1);
            double paperHeight = settings.Double("PaperHeight", 2.5);
            var points = new List<SurfaceComparisonPoint>();
            int sequence = start;
            while (true)
            {
                var options = new PromptPointOptions("\nPick surface-comparison point or press Enter to finish: ")
                {
                    AllowNone = true
                };
                PromptPointResult picked = document.Editor.GetPoint(options);
                if (picked.Status == PromptStatus.None || picked.Status == PromptStatus.Cancel) break;
                if (picked.Status != PromptStatus.OK) break;
                Point3d world = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                SurfaceComparisonPoint point;
                if (!TryBuildComparisonPoint(
                        document.Database,
                        baseSurfaceId,
                        comparisonSurfaceId,
                        prefix + sequence.ToString(CultureInfo.InvariantCulture),
                        world.X,
                        world.Y,
                        out point))
                {
                    document.Editor.WriteMessage("\nPoint {0} is outside one of the selected surfaces and was skipped.", sequence);
                    continue;
                }
                points.Add(point);
                sequence++;
            }
            if (points.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SURFACECOMPAREPRODUCTION cancelled. No valid comparison points were selected.");
                return;
            }

            CreateComparisonAnnotations(
                document,
                baseSurfaceId,
                comparisonSurfaceId,
                points,
                paperHeight);

            IList<string> columns = ComparisonColumns();
            IList<IList<string>> rows = points.Select(ToComparisonRow).Cast<IList<string>>().ToList();
            SurveyResultAction action = SurveyResultPopupWindow.Show(
                "CE Tools - Dynamic Surface Comparison",
                "The selected points are linked to both Civil 3D surfaces. Linked annotations have been placed at the comparison-surface elevations.",
                columns,
                rows,
                true,
                true);

            if (action == SurveyResultAction.Table || action == SurveyResultAction.Both)
            {
                PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked surface-comparison table: ");
                if (insertion.Status == PromptStatus.OK)
                {
                    MultiSurfaceComparisonTableStore.Create(
                        document,
                        insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem),
                        baseSurfaceId,
                        comparisonSurfaceId,
                        points,
                        paperHeight);
                }
            }
            if (action == SurveyResultAction.Excel || action == SurveyResultAction.Both)
                ExportGrid(document, "CE-Tools-Surface-Comparison.xlsx", "Surface Comparison", columns, rows);

            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SURFACECOMPAREPRODUCTION complete. Linked comparison points={0}.", points.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEREPORTPRODUCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceReportProduction()
        {
            Document document = Active();
            if (document == null) return;
            ObjectId surfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectOne(
                    document,
                    "CE Tools - Surface Report",
                    "Choose the Civil 3D surface to review. The popup can be placed as a linked annotative table and/or exported to Excel.",
                    "Surface",
                    out surfaceId))
                return;

            IList<IList<string>> rows = BuildSurfaceReport(document.Database, surfaceId);
            IList<string> columns = new List<string> { "Property", "Value" };
            SurveyResultAction action = SurveyResultPopupWindow.Show(
                "CE Tools - Surface Report",
                "This report is read directly from the selected Civil 3D surface. A placed CE table remains linked and refreshes through Universal Dynamic Refresh.",
                columns,
                rows,
                true,
                true);

            if (action == SurveyResultAction.Table || action == SurveyResultAction.Both)
            {
                PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked surface report table: ");
                if (insertion.Status == PromptStatus.OK)
                    LinkedSurfaceReportTableStore.Create(document, insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem), surfaceId);
            }
            if (action == SurveyResultAction.Excel || action == SurveyResultAction.Both)
                ExportGrid(document, "CE-Tools-Surface-Report.xlsx", "Surface Report", columns, rows);
            document.Editor.Regen();
        }

        [CommandMethod("CE_TOOLS", "CE_GRIDSETTINGOUTMULTI", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MultiPolylineGridSettingOut()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Multiple Polyline / Feature-Line Setting-Out",
                "Use the linked Vertex Setting-Out engine for multiple polylines/feature lines. It maintains one continuous point-number sequence, linked tables and source-geometry relationships.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create / update multiple-source setting-out", "CE_VERTEXSETTINGOUT", "Select multiple polylines or feature lines and use the required vertex/midpoint/centre rules.", "01 SETTING-OUT"),
                    new DisciplineWorkflowAction("Refresh all linked survey points / tables", "CE_SURVEYREFRESHSAFE", "Move linked points back onto changed source vertices, refresh tables and restore annotation scale/COGO label offsets.", "02 REFRESH"),
                    new DisciplineWorkflowAction("Export selected setting-out table to Excel / CSV", "CE_TABLEEXPORTCSV", "Export the selected linked CE table to an Excel-compatible CSV.", "03 EXPORT")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYREFRESHSAFE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurveyRefreshSafe()
        {
            Document document = Active();
            if (document == null) return;
            try { DynamicCoordinateLinkStore.Refresh(document); } catch { }
            try { UniversalDynamicRefreshManager.RefreshNow(document); } catch { }
            try { SurfaceComparisonLinkStore.RefreshAll(document); } catch { }
            try { MultiSurfaceComparisonTableStore.RefreshAll(document); } catch { }
            try { LinkedSurfaceReportTableStore.RefreshAll(document); } catch { }
            try { August11SurveyRuntimeCommands.RestoreCogoLabels(document, null); } catch { }
            try { AnnotationScaleSyncManager.ApplyCurrentScale(document); } catch { }
            try { CeTablePresentationManager.CenterCeTables(document); } catch { }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SURVEYREFRESHSAFE complete. Source-linked points, tables, surface comparison/report links, annotation scale and original COGO label offsets were restored/refreshed.");
        }

        internal static void PlotSurveyCorrectionRows(Document document, IList<IList<string>> rows)
        {
            if (document == null || rows == null || rows.Count == 0) return;
            double paperHeight = 2.0;
            try
            {
                AnnotationOptions settings = AnnotationSettingsStore.Read(document.Database);
                if (settings != null) paperHeight = settings.TextHeight;
            }
            catch { }
            double textHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight);
            double offset = PaperAnnotationScale.ModelDistance(document.Database, paperHeight * 2.0);
            int plotted = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                foreach (IList<string> row in rows)
                {
                    if (row == null || row.Count < 7) continue;
                    double x;
                    double y;
                    double z;
                    if (!double.TryParse(row[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out x) ||
                        !double.TryParse(row[2], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out y) ||
                        !double.TryParse(row[4], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out z))
                        continue;
                    var point = new DBPoint(new Point3d(x, y, z));
                    point.SetDatabaseDefaults(document.Database);
                    space.AppendEntity(point);
                    transaction.AddNewlyCreatedDBObject(point, true);
                    var text = new MText();
                    text.SetDatabaseDefaults(document.Database);
                    text.Location = new Point3d(x + offset, y + offset, z);
                    text.Attachment = AttachmentPoint.MiddleLeft;
                    text.TextHeight = textHeight;
                    text.Contents = "P" + row[0] + "\\PZ: " + row[4] + "\\P" + row[6];
                    PaperAnnotationScale.SetAnnotative(text);
                    space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    plotted++;
                }
                transaction.Commit();
            }
            if (plotted > 0)
                document.Editor.WriteMessage("\nCE Tools automatically plotted {0} survey-correction point(s) at their corrected surface elevations.", plotted);
        }

        internal static IList<IList<string>> BuildSurfaceReport(Database database, ObjectId surfaceId)
        {
            var rows = new List<IList<string>>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) return rows;
                rows.Add(Row("Surface Name", SafeName(surface)));
                rows.Add(Row("Surface Type", surface.GetType().Name));
                rows.Add(Row("Handle", surface.ObjectId.Handle.ToString()));
                try
                {
                    Extents3d extents = surface.GeometricExtents;
                    rows.Add(Row("Minimum X", extents.MinPoint.X.ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Minimum Y", extents.MinPoint.Y.ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Minimum Z", extents.MinPoint.Z.ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Maximum X", extents.MaxPoint.X.ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Maximum Y", extents.MaxPoint.Y.ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Maximum Z", extents.MaxPoint.Z.ToString("0.###", CultureInfo.InvariantCulture)));
                }
                catch { }
                string style = ReadStringProperty(surface, "StyleName");
                if (!string.IsNullOrWhiteSpace(style)) rows.Add(Row("Style", style));
                List<Point3d> vertices = ReadSurfaceVertices(surface);
                if (vertices.Count > 0)
                {
                    rows.Add(Row("Readable Vertex Count", vertices.Count.ToString(CultureInfo.InvariantCulture)));
                    rows.Add(Row("Lowest Vertex Elevation", vertices.Min(item => item.Z).ToString("0.###", CultureInfo.InvariantCulture)));
                    rows.Add(Row("Highest Vertex Elevation", vertices.Max(item => item.Z).ToString("0.###", CultureInfo.InvariantCulture)));
                }
            }
            return rows;
        }

        private static bool TryBuildComparisonPoint(Database database, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, string name, double x, double y, out SurfaceComparisonPoint result)
        {
            result = null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface baseSurface = transaction.GetObject(baseSurfaceId, OpenMode.ForRead, false) as CivilSurface;
                CivilSurface comparisonSurface = transaction.GetObject(comparisonSurfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (baseSurface == null || comparisonSurface == null) return false;
                try
                {
                    double baseZ = baseSurface.FindElevationAtXY(x, y);
                    double comparisonZ = comparisonSurface.FindElevationAtXY(x, y);
                    result = new SurfaceComparisonPoint(name, x, y, baseZ, comparisonZ);
                    return true;
                }
                catch { return false; }
            }
        }

        private static void CreateComparisonAnnotations(Document document, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, IList<SurfaceComparisonPoint> points, double paperHeight)
        {
            double textHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight);
            double offset = PaperAnnotationScale.ModelDistance(document.Database, paperHeight * 2.5);
            var linked = new List<KeyValuePair<Point3d, List<ObjectId>>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                for (int index = 0; index < points.Count; index++)
                {
                    SurfaceComparisonPoint item = points[index];
                    Point3d location = new Point3d(item.X, item.Y, item.ComparisonZ);
                    var point = new DBPoint(location);
                    point.SetDatabaseDefaults(document.Database);
                    ObjectId pointId = space.AppendEntity(point);
                    transaction.AddNewlyCreatedDBObject(point, true);

                    var text = new MText();
                    text.SetDatabaseDefaults(document.Database);
                    text.Location = new Point3d(item.X + offset, item.Y + (index % 2 == 0 ? offset : -offset), item.ComparisonZ);
                    text.Attachment = AttachmentPoint.MiddleLeft;
                    text.TextHeight = textHeight;
                    text.Contents = item.Name + "\\PBASE Z: " + item.BaseZ.ToString("0.###", CultureInfo.InvariantCulture) +
                        "\\PCOMP Z: " + item.ComparisonZ.ToString("0.###", CultureInfo.InvariantCulture) +
                        "\\PDIFF: " + item.Difference.ToString("+0.###;-0.###;0.000", CultureInfo.InvariantCulture);
                    PaperAnnotationScale.SetAnnotative(text);
                    ObjectId textId = space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    linked.Add(new KeyValuePair<Point3d, List<ObjectId>>(location, new List<ObjectId> { pointId, textId }));
                }
                transaction.Commit();
            }
            foreach (KeyValuePair<Point3d, List<ObjectId>> item in linked)
                SurfaceComparisonLinkStore.LinkEntities(document.Database, baseSurfaceId, comparisonSurfaceId, item.Key, item.Value);
        }

        private static IList<string> ComparisonColumns()
        {
            return new List<string> { "Point", "X", "Y", "Base Z", "Comparison Z", "Difference", "Result" };
        }

        private static List<string> ToComparisonRow(SurfaceComparisonPoint point)
        {
            return new List<string>
            {
                point.Name,
                point.X.ToString("0.###", CultureInfo.InvariantCulture),
                point.Y.ToString("0.###", CultureInfo.InvariantCulture),
                point.BaseZ.ToString("0.###", CultureInfo.InvariantCulture),
                point.ComparisonZ.ToString("0.###", CultureInfo.InvariantCulture),
                point.Difference.ToString("+0.###;-0.###;0.000", CultureInfo.InvariantCulture),
                point.Result
            };
        }

        private static void ExportGrid(Document document, string fileName, string sheetName, IList<string> columns, IList<IList<string>> rows)
        {
            var options = new PromptSaveFileOptions("\nSelect Excel workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools " + sheetName,
                InitialFileName = fileName
            };
            PromptFileNameResult pathResult = document.Editor.GetFileNameForSave(options);
            if (pathResult.Status != PromptStatus.OK) return;
            string path = pathResult.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
            var exportRows = new List<IList<string>> { columns };
            foreach (IList<string> row in rows) exportRows.Add(row);
            SimpleXlsxWriter.Write(path, sheetName, exportRows);
            document.Editor.WriteMessage("\nCE Tools Excel export complete: {0}", path);
        }

        private static List<Point3d> ReadSurfaceVertices(object surface)
        {
            object raw = InvokeNoArgument(surface, "GetVertices") ?? ReadProperty(surface, "Vertices");
            IEnumerable values = raw as IEnumerable;
            var points = new List<Point3d>();
            if (values == null) return points;
            foreach (object value in values)
            {
                Point3d point;
                if (TryReadPoint(value, out point)) points.Add(point);
            }
            return points;
        }

        private static object InvokeNoArgument(object owner, string methodName)
        {
            if (owner == null) return null;
            MethodInfo method = owner.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null) return null;
            try { return method.Invoke(owner, null); } catch { return null; }
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            if (owner == null) return null;
            PropertyInfo property = owner.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead) return null;
            try { return property.GetValue(owner, null); } catch { return null; }
        }

        private static string ReadStringProperty(object owner, string propertyName)
        {
            object value = ReadProperty(owner, propertyName);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool TryReadPoint(object value, out Point3d point)
        {
            if (value is Point3d) { point = (Point3d)value; return true; }
            foreach (string name in new[] { "Location", "Position", "Point" })
            {
                object raw = ReadProperty(value, name);
                if (raw is Point3d) { point = (Point3d)raw; return true; }
            }
            point = Point3d.Origin;
            return false;
        }

        private static string SafeName(CivilSurface surface)
        {
            try { return string.IsNullOrWhiteSpace(surface.Name) ? "Surface" : surface.Name; }
            catch { return "Surface"; }
        }

        private static IList<string> Row(string key, string value)
        {
            return new List<string> { key ?? string.Empty, value ?? string.Empty };
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class SurfaceComparisonPoint
    {
        internal SurfaceComparisonPoint(string name, double x, double y, double baseZ, double comparisonZ)
        {
            Name = name ?? string.Empty;
            X = x;
            Y = y;
            BaseZ = baseZ;
            ComparisonZ = comparisonZ;
            Difference = comparisonZ - baseZ;
            Result = Difference > 0.0005 ? "Fill / raise" : Difference < -0.0005 ? "Cut / lower" : "No material difference";
        }
        internal string Name { get; private set; }
        internal double X { get; private set; }
        internal double Y { get; private set; }
        internal double BaseZ { get; private set; }
        internal double ComparisonZ { get; private set; }
        internal double Difference { get; private set; }
        internal string Result { get; private set; }
    }

    internal enum SurveyResultAction
    {
        Close,
        Table,
        Excel,
        Both
    }

    internal sealed class SurveyResultPopupWindow : Window
    {
        private SurveyResultPopupWindow(string title, string note, IList<string> columns, IList<IList<string>> rows, bool allowTable, bool allowExcel)
        {
            Title = title ?? "CE Tools Survey Result";
            Width = 1100;
            Height = 650;
            MinWidth = 760;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock { Text = title ?? string.Empty, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);
            var noteBlock = new TextBlock { Text = note ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(noteBlock, 1);
            root.Children.Add(noteBlock);

            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.All, ItemsSource = BuildItems(rows) };
            for (int index = 0; columns != null && index < columns.Count; index++)
            {
                grid.Columns.Add(new DataGridTextColumn { Header = columns[index] ?? string.Empty, Binding = new Binding("Values[" + index + "]"), Width = DataGridLength.SizeToCells, MinWidth = 95 });
            }
            Grid.SetRow(grid, 2);
            root.Children.Add(grid);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            if (allowTable)
            {
                Button table = ButtonOf("Place Linked Table", 135);
                table.Click += delegate { Action = SurveyResultAction.Table; DialogResult = true; };
                buttons.Children.Add(table);
            }
            if (allowExcel)
            {
                Button excel = ButtonOf("Export Excel", 110);
                excel.Click += delegate { Action = SurveyResultAction.Excel; DialogResult = true; };
                buttons.Children.Add(excel);
            }
            if (allowTable && allowExcel)
            {
                Button both = ButtonOf("Table + Excel", 110);
                both.Click += delegate { Action = SurveyResultAction.Both; DialogResult = true; };
                buttons.Children.Add(both);
            }
            Button close = ButtonOf("Close", 90);
            close.IsCancel = true;
            close.Click += delegate { Action = SurveyResultAction.Close; DialogResult = false; };
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);
            Content = root;
        }

        internal SurveyResultAction Action { get; private set; }

        internal static SurveyResultAction Show(string title, string note, IList<string> columns, IList<IList<string>> rows, bool allowTable, bool allowExcel)
        {
            var window = new SurveyResultPopupWindow(title, note, columns, rows, allowTable, allowExcel);
            AcApplication.ShowModalWindow(window);
            return window.Action;
        }

        private static IList<SurveyGridRow> BuildItems(IList<IList<string>> rows)
        {
            var result = new List<SurveyGridRow>();
            if (rows != null) foreach (IList<string> row in rows) result.Add(new SurveyGridRow(row));
            return result;
        }

        private static Button ButtonOf(string text, double width)
        {
            return new Button { Content = text, MinWidth = width, MinHeight = 30, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        }

        private sealed class SurveyGridRow
        {
            internal SurveyGridRow(IList<string> values) { Values = values ?? new List<string>(); }
            public IList<string> Values { get; private set; }
        }
    }

    internal static class MultiSurfaceComparisonTableStore
    {
        private const string RecordName = "CE_MULTI_SURFACE_COMPARISON_V1";

        internal static ObjectId Create(Document document, Point3d insertion, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, IList<SurfaceComparisonPoint> points, double paperHeight)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return ObjectId.Null;
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId id = space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, baseSurfaceId, comparisonSurfaceId, points);
                Populate(document.Database, table, points, paperHeight);
                transaction.Commit();
                return id;
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null) return 0;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId id in block)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; } catch { continue; }
                        if (table == null) continue;
                        ObjectId baseId;
                        ObjectId comparisonId;
                        List<SurfaceComparisonPoint> stored;
                        if (!TryReadLink(document.Database, table, transaction, out baseId, out comparisonId, out stored)) continue;
                        CivilSurface baseSurface = transaction.GetObject(baseId, OpenMode.ForRead, false) as CivilSurface;
                        CivilSurface comparisonSurface = transaction.GetObject(comparisonId, OpenMode.ForRead, false) as CivilSurface;
                        if (baseSurface == null || comparisonSurface == null) continue;
                        var refreshed = new List<SurfaceComparisonPoint>();
                        foreach (SurfaceComparisonPoint item in stored)
                        {
                            try { refreshed.Add(new SurfaceComparisonPoint(item.Name, item.X, item.Y, baseSurface.FindElevationAtXY(item.X, item.Y), comparisonSurface.FindElevationAtXY(item.X, item.Y))); }
                            catch { }
                        }
                        if (refreshed.Count == 0) continue;
                        table.UpgradeOpen();
                        double height = table.Rows.Count > 0 && table.Columns.Count > 0 ? Math.Max(table.Cells[0, 0].TextHeight ?? 2.5, 0.001) : 2.5;
                        PopulateModel(table, refreshed, height);
                        WriteLink(table, transaction, baseId, comparisonId, refreshed);
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static void Populate(Database database, Table table, IList<SurfaceComparisonPoint> points, double paperHeight)
        {
            PopulateModel(table, points, PaperAnnotationScale.AnnotativeTextHeight(database, paperHeight));
        }

        private static void PopulateModel(Table table, IList<SurfaceComparisonPoint> points, double textHeight)
        {
            string[] headings = { "Point", "X", "Y", "Base Z", "Comparison Z", "Difference", "Result" };
            table.SetSize(points.Count + 2, headings.Length);
            table.SetRowHeight(textHeight * 2.4);
            table.MergeCells(CellRange.Create(table, 0, 0, 0, headings.Length - 1));
            table.Cells[0, 0].TextString = "CE TOOLS DYNAMIC SURFACE COMPARISON";
            table.Cells[0, 0].TextHeight = textHeight * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            for (int col = 0; col < headings.Length; col++)
            {
                table.Columns[col].Width = textHeight * 14.0;
                table.Cells[1, col].TextString = headings[col];
                table.Cells[1, col].TextHeight = textHeight;
                table.Cells[1, col].Alignment = CellAlignment.MiddleCenter;
            }
            for (int index = 0; index < points.Count; index++)
            {
                IList<string> row = new List<string>
                {
                    points[index].Name,
                    points[index].X.ToString("0.###", CultureInfo.InvariantCulture),
                    points[index].Y.ToString("0.###", CultureInfo.InvariantCulture),
                    points[index].BaseZ.ToString("0.###", CultureInfo.InvariantCulture),
                    points[index].ComparisonZ.ToString("0.###", CultureInfo.InvariantCulture),
                    points[index].Difference.ToString("+0.###;-0.###;0.000", CultureInfo.InvariantCulture),
                    points[index].Result
                };
                for (int col = 0; col < headings.Length; col++)
                {
                    table.Cells[index + 2, col].TextString = row[col];
                    table.Cells[index + 2, col].TextHeight = textHeight;
                    table.Cells[index + 2, col].Alignment = CellAlignment.MiddleCenter;
                }
            }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static void WriteLink(Table table, Transaction transaction, ObjectId baseId, ObjectId comparisonId, IList<SurfaceComparisonPoint> points)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName)) record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
            else { record = new Xrecord(); dictionary.SetAt(RecordName, record); transaction.AddNewlyCreatedDBObject(record, true); }
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Base=" + baseId.Handle),
                new TypedValue((int)DxfCode.Text, "Comparison=" + comparisonId.Handle)
            };
            foreach (SurfaceComparisonPoint item in points)
                values.Add(new TypedValue((int)DxfCode.Text, string.Join("|", new[] { "P", Encode(item.Name), item.X.ToString("R", CultureInfo.InvariantCulture), item.Y.ToString("R", CultureInfo.InvariantCulture) })));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadLink(Database database, Table table, Transaction transaction, out ObjectId baseId, out ObjectId comparisonId, out List<SurfaceComparisonPoint> points)
        {
            baseId = ObjectId.Null;
            comparisonId = ObjectId.Null;
            points = new List<SurfaceComparisonPoint>();
            if (table.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordName)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            string baseHandle = string.Empty;
            string comparisonHandle = string.Empty;
            var rawPoints = new List<string[]>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Base=", StringComparison.Ordinal)) baseHandle = text.Substring(5);
                else if (text.StartsWith("Comparison=", StringComparison.Ordinal)) comparisonHandle = text.Substring(11);
                else
                {
                    string[] parts = text.Split('|');
                    if (parts.Length == 4 && parts[0] == "P") rawPoints.Add(parts);
                }
            }
            if (!Resolve(database, baseHandle, out baseId) || !Resolve(database, comparisonHandle, out comparisonId)) return false;
            foreach (string[] parts in rawPoints)
            {
                double x;
                double y;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                points.Add(new SurfaceComparisonPoint(Decode(parts[1]), x, y, 0.0, 0.0));
            }
            return points.Count > 0;
        }

        private static bool Resolve(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            long handle;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle)) return false;
            try { id = database.GetObjectId(false, new Handle(handle), 0); return !id.IsNull && !id.IsErased; } catch { return false; }
        }

        private static string Encode(string value) { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty)); }
        private static string Decode(string value) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); } catch { return string.Empty; } }
    }

    internal static class LinkedSurfaceReportTableStore
    {
        private const string RecordName = "CE_LINKED_SURFACE_REPORT_V1";

        internal static ObjectId Create(Document document, Point3d insertion, ObjectId surfaceId)
        {
            IList<IList<string>> rows = August14SurveyFieldReviewCommands.BuildSurfaceReport(document.Database, surfaceId);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return ObjectId.Null;
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId id = space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, surfaceId);
                Populate(document.Database, table, rows);
                transaction.Commit();
                return id;
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null) return 0;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId id in block)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; } catch { continue; }
                        if (table == null) continue;
                        ObjectId surfaceId;
                        if (!TryReadLink(document.Database, table, transaction, out surfaceId)) continue;
                        IList<IList<string>> rows = August14SurveyFieldReviewCommands.BuildSurfaceReport(document.Database, surfaceId);
                        table.UpgradeOpen();
                        Populate(document.Database, table, rows);
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static void Populate(Database database, Table table, IList<IList<string>> rows)
        {
            double textHeight = PaperAnnotationScale.AnnotativeTextHeight(database, 2.0);
            table.SetSize(rows.Count + 2, 2);
            table.SetRowHeight(textHeight * 2.4);
            table.Columns[0].Width = textHeight * 18.0;
            table.Columns[1].Width = textHeight * 26.0;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            table.Cells[0, 0].TextString = "CE TOOLS LINKED SURFACE REPORT";
            table.Cells[0, 0].TextHeight = textHeight * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[1, 0].TextString = "Property";
            table.Cells[1, 1].TextString = "Value";
            table.Cells[1, 0].TextHeight = textHeight;
            table.Cells[1, 1].TextHeight = textHeight;
            table.Cells[1, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[1, 1].Alignment = CellAlignment.MiddleCenter;
            for (int row = 0; row < rows.Count; row++)
            {
                table.Cells[row + 2, 0].TextString = rows[row].Count > 0 ? rows[row][0] : string.Empty;
                table.Cells[row + 2, 1].TextString = rows[row].Count > 1 ? rows[row][1] : string.Empty;
                table.Cells[row + 2, 0].TextHeight = textHeight;
                table.Cells[row + 2, 1].TextHeight = textHeight;
                table.Cells[row + 2, 0].Alignment = CellAlignment.MiddleCenter;
                table.Cells[row + 2, 1].Alignment = CellAlignment.MiddleCenter;
            }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static void WriteLink(Table table, Transaction transaction, ObjectId surfaceId)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName)) record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
            else { record = new Xrecord(); dictionary.SetAt(RecordName, record); transaction.AddNewlyCreatedDBObject(record, true); }
            record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "Surface=" + surfaceId.Handle));
        }

        private static bool TryReadLink(Database database, Table table, Transaction transaction, out ObjectId surfaceId)
        {
            surfaceId = ObjectId.Null;
            if (table.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordName)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("Surface=", StringComparison.Ordinal)) continue;
                long handle;
                if (!long.TryParse(text.Substring(8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle)) return false;
                try { surfaceId = database.GetObjectId(false, new Handle(handle), 0); return !surfaceId.IsNull && !surfaceId.IsErased; } catch { return false; }
            }
            return false;
        }
    }
}
