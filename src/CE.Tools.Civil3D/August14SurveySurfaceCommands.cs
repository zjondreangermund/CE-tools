using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August14SurveySurfaceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey-production surface helpers: create TINs from drawing objects or
    /// delimited survey files, create an outer hull boundary that removes the
    /// long interpolated triangles outside the observed point extent, compare
    /// surfaces through CE's linked coordinate table and export AutoCAD tables.
    /// </summary>
    public sealed class August14SurveySurfaceCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURVEYSURFACEPRODUCTION", CommandFlags.Modal)]
        public void SurfaceProductionCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Survey Surface Production",
                "Use below Survey PREPARE: create a TIN from a point file or drawing objects, choose the surface style, then create/rebuild an automatic outer border to the actual point extent.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Add surface from survey file", "CE_SURFACEFROMFILE", "Read TXT/CSV XYZ/ENZ/PENZ data, optionally rotate 180 degrees about 0,0,0, create a TIN and choose its style.", "01 Create"),
                    new DisciplineWorkflowAction("Add surface from drawing objects", "CE_SURFACEFROMOBJECTS", "Create a TIN from COGO/DBPoints, 2D/3D polylines and feature-line vertices.", "01 Create"),
                    new DisciplineWorkflowAction("Auto border to actual surface-point extent", "CE_SURFACEAUTOBORDER", "Create a convex-hull outer boundary to omit long interpolation triangles outside the data extent.", "02 Boundary"),
                    new DisciplineWorkflowAction("Base / comparison surface table", "CE_SURFACECOMPARETABLE", "Use CE's linked multi-surface coordinate table; select the base and comparison surfaces, then points.", "03 Compare"),
                    new DisciplineWorkflowAction("Export selected table to Excel-compatible CSV", "CE_TABLEEXPORTCSV", "Write all selected AutoCAD table cells to a CSV that opens directly in Excel.", "03 Compare"),
                    new DisciplineWorkflowAction("Correct table column spacing", "CE_TABLECOLUMNSPACE", "Apply one explicit drawing-unit width to all columns of a selected table.", "03 Compare"),
                    new DisciplineWorkflowAction("Existing Surface Tools", "CE_SFTOOLS", "Open the existing CE surface reporting, hull diagnostic and merge tools.", "04 Review")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEFROMOBJECTS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceFromObjects()
        {
            Document document = Active();
            if (document == null) return;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null)
            {
                document.Editor.WriteMessage("\nCE_SURFACEFROMOBJECTS cancelled. No active Civil 3D document.");
                return;
            }

            List<StyleChoice> styles = ReadSurfaceStyles(document, civil);
            var model = BuildSurfaceSettings("CE Tools - Surface From Drawing Objects", styles);
            model.AddChoice("AutoBorder", "03 Boundary", "Create outer point-extent border", "Yes", "Create a convex hull through the observed XY point extent and add it as an Outer surface boundary.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect COGO/DBPoints, polylines, 3D polylines or feature lines for the TIN: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            Point3dCollection points;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                points = ReadSurfacePoints(transaction, selection.Value.GetObjectIds());
            }
            if (points.Count < 3)
            {
                document.Editor.WriteMessage("\nCE_SURFACEFROMOBJECTS cancelled. At least three usable 3D points are required.");
                return;
            }

            ObjectId surfaceId = CreateSurface(
                document,
                civil,
                model.Text("Name"),
                model.Text("Style"),
                styles,
                points,
                string.Equals(model.Text("AutoBorder"), "Yes", StringComparison.OrdinalIgnoreCase));
            if (!surfaceId.IsNull)
                document.Editor.WriteMessage("\nCE_SURFACEFROMOBJECTS complete. Surface={0}; source vertices={1}.", model.Text("Name"), points.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEFROMFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceFromFile()
        {
            Document document = Active();
            if (document == null) return;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null)
            {
                document.Editor.WriteMessage("\nCE_SURFACEFROMFILE cancelled. No active Civil 3D document.");
                return;
            }

            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "CE Tools - Select Survey Point File",
                Filter = "Survey point files (*.txt;*.csv;*.xyz)|*.txt;*.csv;*.xyz|All files (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;

            List<StyleChoice> styles = ReadSurfaceStyles(document, civil);
            var model = BuildSurfaceSettings("CE Tools - Surface From Survey File", styles);
            model.AddChoice("Format", "03 File", "Column order", "E,N,Z / X,Y,Z", "PENZ/PENZD files ignore the point-number and description columns. N,E,Z swaps the first two coordinate columns into X=E, Y=N.", new[] { "E,N,Z / X,Y,Z", "P,E,N,Z", "P,E,N,Z,D", "N,E,Z" });
            model.AddChoice("Rotate", "03 File", "Rotate imported points 180° about 0,0,0", "No", "When Yes: X becomes -X and Y becomes -Y before the TIN is created; Z is unchanged.", new[] { "No", "Yes" });
            model.AddChoice("AutoBorder", "04 Boundary", "Create outer point-extent border", "Yes", "Create an outer hull boundary immediately after the TIN is created.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            int badRows;
            Point3dCollection points = ReadPointFile(
                dialog.FileName,
                model.Text("Format"),
                string.Equals(model.Text("Rotate"), "Yes", StringComparison.OrdinalIgnoreCase),
                out badRows);
            if (points.Count < 3)
            {
                document.Editor.WriteMessage("\nCE_SURFACEFROMFILE cancelled. The selected file produced fewer than three valid points. Bad/ignored rows={0}.", badRows);
                return;
            }

            ObjectId surfaceId = CreateSurface(
                document,
                civil,
                model.Text("Name"),
                model.Text("Style"),
                styles,
                points,
                string.Equals(model.Text("AutoBorder"), "Yes", StringComparison.OrdinalIgnoreCase));
            if (!surfaceId.IsNull)
                document.Editor.WriteMessage("\nCE_SURFACEFROMFILE complete. File={0}; valid points={1}; ignored rows={2}; rotate180={3}.", Path.GetFileName(dialog.FileName), points.Count, badRows, model.Text("Rotate"));
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEAUTOBORDER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AutoSurfaceBorder()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect a TIN surface for the automatic outer point-extent border: ");
            options.SetRejectMessage("\nSelect a Civil 3D TIN surface.");
            options.AddAllowedClass(typeof(TinSurface), true);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return;

            int vertexCount = 0;
            int hullCount = 0;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    TinSurface surface = transaction.GetObject(result.ObjectId, OpenMode.ForWrite, false) as TinSurface;
                    if (surface == null) return;
                    var points = new List<Point2d>();
                    foreach (TinSurfaceVertex vertex in surface.Vertices)
                    {
                        if (vertex == null || !vertex.IsValid) continue;
                        Point3d point = vertex.Location;
                        points.Add(new Point2d(point.X, point.Y));
                    }
                    vertexCount = points.Count;
                    List<Point2d> hull = ConvexHull(points);
                    hullCount = hull.Count;
                    if (hull.Count < 3) throw new InvalidOperationException("The surface does not contain enough distinct XY vertices for a border.");
                    ObjectId borderId = CreateBorderPolyline(document.Database, transaction, hull, surface.LayerId);
                    var boundaryIds = new ObjectIdCollection { borderId };
                    surface.BoundariesDefinition.AddBoundaries(boundaryIds, 0.1, SurfaceBoundaryType.Outer, true);
                    surface.Rebuild();
                    transaction.Commit();
                }
                document.Editor.Regen();
                document.Editor.WriteMessage("\nCE_SURFACEAUTOBORDER complete. Surface vertices={0}; hull vertices={1}. Long interpolated triangles outside the outer hull are clipped.", vertexCount, hullCount);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURFACEAUTOBORDER cancelled. " + exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACECOMPARETABLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceCompareTable()
        {
            Document document = Active();
            if (document == null) return;
            document.Editor.WriteMessage("\nCE_SURFACECOMPARETABLE: select the BASE and COMPARISON surfaces (two surfaces are normally used), then select COGO/DBPoints. The resulting table remains linked and can be refreshed with CE_COORDMULTISURFACEREFRESH.");
            new August11SurveyRuntimeCommands().MultiSurfaceCoordinateTable();
        }

        [CommandMethod("CE_TOOLS", "CE_TABLEEXPORTCSV", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportTableCsv()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect an AutoCAD table to export to Excel-compatible CSV: ");
            options.SetRejectMessage("\nSelect a table.");
            options.AddAllowedClass(typeof(Table), true);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Title = "CE Tools - Export Table",
                Filter = "Excel-compatible CSV (*.csv)|*.csv",
                AddExtension = true,
                DefaultExt = "csv",
                FileName = "CE-Tools-Table.csv"
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as Table;
                    if (table == null) return;
                    using (var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true)))
                    {
                        for (int row = 0; row < table.Rows.Count; row++)
                        {
                            var values = new List<string>();
                            for (int column = 0; column < table.Columns.Count; column++)
                            {
                                string value = table.Cells[row, column].TextString ?? string.Empty;
                                values.Add(Csv(value));
                            }
                            writer.WriteLine(string.Join(",", values));
                        }
                    }
                }
                document.Editor.WriteMessage("\nCE_TABLEEXPORTCSV complete. File={0}.", dialog.FileName);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_TABLEEXPORTCSV failed: " + exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_TABLECOLUMNSPACE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CorrectColumnSpacing()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect a table whose column spacing/width must be corrected: ");
            options.SetRejectMessage("\nSelect a table.");
            options.AddAllowedClass(typeof(Table), true);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Table Column Spacing",
                "Apply a consistent width to every table column. This is useful when one or more generated setting-out columns are too narrow or crowded.");
            model.AddPositiveDouble("Width", "Columns", "Column width (drawing units)", 25.0, "The same width is applied to every column; rerun with another value to refine the table.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double width = model.Double("Width", 25.0);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(selected.ObjectId, OpenMode.ForWrite, false) as Table;
                if (table == null) return;
                table.SetColumnWidth(width);
                table.RecomputeTableBlock(true);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_TABLECOLUMNSPACE complete. Uniform column width={0:0.###}.", width);
        }

        private static ProductionSettingsDialogModel BuildSurfaceSettings(string title, IList<StyleChoice> styles)
        {
            string[] names = styles.Count == 0 ? new[] { "<Drawing default>" } : styles.Select(item => item.Name).ToArray();
            var model = new ProductionSettingsDialogModel(
                title,
                "Create a new TIN surface using the chosen Civil 3D surface style. Surface source points are copied into the TIN definition; the source geometry/file is not deleted.");
            model.AddText("Name", "01 Surface", "Surface name", "CE-EG", "Unique Civil 3D surface name.");
            model.AddChoice("Style", "02 Style", "Surface style", names[0], "Choose any installed Civil 3D Surface Style.", names);
            return model;
        }

        private static List<StyleChoice> ReadSurfaceStyles(Document document, CivilDocument civil)
        {
            var result = new List<StyleChoice>();
            if (document == null || civil == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.Styles.SurfaceStyles)
                {
                    SurfaceStyle style = transaction.GetObject(id, OpenMode.ForRead, false) as SurfaceStyle;
                    if (style != null) result.Add(new StyleChoice(style.Name, id));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId CreateSurface(
            Document document,
            CivilDocument civil,
            string requestedName,
            string styleName,
            IList<StyleChoice> styles,
            Point3dCollection points,
            bool autoBorder)
        {
            string name = string.IsNullOrWhiteSpace(requestedName) ? "CE-EG" : requestedName.Trim();
            ObjectId styleId = ObjectId.Null;
            StyleChoice style = styles.FirstOrDefault(item => string.Equals(item.Name, styleName, StringComparison.OrdinalIgnoreCase));
            if (style != null) styleId = style.Id;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId surfaceId = styleId.IsNull
                        ? TinSurface.Create(document.Database, name)
                        : TinSurface.Create(name, styleId);
                    TinSurface surface = transaction.GetObject(surfaceId, OpenMode.ForWrite, false) as TinSurface;
                    if (surface == null) throw new InvalidOperationException("Civil 3D did not return the new TIN surface.");
                    surface.AddVertices(points);
                    if (autoBorder)
                    {
                        List<Point2d> hull = ConvexHull(points.Cast<Point3d>().Select(item => new Point2d(item.X, item.Y)).ToList());
                        if (hull.Count >= 3)
                        {
                            ObjectId borderId = CreateBorderPolyline(document.Database, transaction, hull, surface.LayerId);
                            surface.BoundariesDefinition.AddBoundaries(new ObjectIdCollection { borderId }, 0.1, SurfaceBoundaryType.Outer, true);
                        }
                    }
                    surface.Rebuild();
                    transaction.Commit();
                    return surfaceId;
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nSurface creation failed: " + exception.Message);
                return ObjectId.Null;
            }
        }

        private static Point3dCollection ReadSurfacePoints(Transaction transaction, IEnumerable<ObjectId> ids)
        {
            var points = new Point3dCollection();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ObjectId id in ids)
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { continue; }
                CivilCogoPoint cogo = value as CivilCogoPoint;
                if (cogo != null)
                {
                    AddUnique(points, seen, new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation));
                    continue;
                }
                DBPoint point = value as DBPoint;
                if (point != null)
                {
                    AddUnique(points, seen, point.Position);
                    continue;
                }
                CivilFeatureLine feature = value as CivilFeatureLine;
                if (feature != null)
                {
                    foreach (Point3d item in feature.GetPoints(FeatureLinePointType.AllPoints)) AddUnique(points, seen, item);
                    continue;
                }
                Polyline3d poly3d = value as Polyline3d;
                if (poly3d != null)
                {
                    foreach (ObjectId vertexId in poly3d)
                    {
                        PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                        if (vertex != null) AddUnique(points, seen, vertex.Position);
                    }
                    continue;
                }
                Polyline polyline = value as Polyline;
                if (polyline != null)
                {
                    for (int index = 0; index < polyline.NumberOfVertices; index++)
                    {
                        Point3d item = polyline.GetPoint3dAt(index);
                        AddUnique(points, seen, item);
                    }
                }
            }
            return points;
        }

        private static Point3dCollection ReadPointFile(string path, string format, bool rotate, out int badRows)
        {
            var points = new Point3dCollection();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            badRows = 0;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw == null ? string.Empty : raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int xIndex = 0;
                int yIndex = 1;
                int zIndex = 2;
                if (string.Equals(format, "P,E,N,Z", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "P,E,N,Z,D", StringComparison.OrdinalIgnoreCase))
                {
                    xIndex = 1; yIndex = 2; zIndex = 3;
                }
                else if (string.Equals(format, "N,E,Z", StringComparison.OrdinalIgnoreCase))
                {
                    xIndex = 1; yIndex = 0; zIndex = 2;
                }
                int required = Math.Max(xIndex, Math.Max(yIndex, zIndex));
                if (parts.Length <= required)
                {
                    badRows++;
                    continue;
                }
                double x, y, z;
                if (!TryNumber(parts[xIndex], out x) || !TryNumber(parts[yIndex], out y) || !TryNumber(parts[zIndex], out z))
                {
                    badRows++;
                    continue;
                }
                if (rotate) { x = -x; y = -y; }
                AddUnique(points, seen, new Point3d(x, y, z));
            }
            return points;
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private static void AddUnique(Point3dCollection points, ISet<string> seen, Point3d point)
        {
            if (double.IsNaN(point.X) || double.IsNaN(point.Y) || double.IsNaN(point.Z)) return;
            string key = Math.Round(point.X, 6).ToString("R", CultureInfo.InvariantCulture) + "|" +
                         Math.Round(point.Y, 6).ToString("R", CultureInfo.InvariantCulture) + "|" +
                         Math.Round(point.Z, 6).ToString("R", CultureInfo.InvariantCulture);
            if (seen.Add(key)) points.Add(point);
        }

        private static ObjectId CreateBorderPolyline(Database database, Transaction transaction, IList<Point2d> hull, ObjectId layerId)
        {
            BlockTableRecord modelSpace = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite, false) as BlockTableRecord;
            if (modelSpace == null) return ObjectId.Null;
            var polyline = new Polyline();
            polyline.SetDatabaseDefaults(database);
            for (int index = 0; index < hull.Count; index++) polyline.AddVertexAt(index, hull[index], 0.0, 0.0, 0.0);
            polyline.Closed = true;
            if (!layerId.IsNull) polyline.LayerId = layerId;
            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return polyline.ObjectId;
        }

        private static List<Point2d> ConvexHull(IList<Point2d> source)
        {
            var points = source
                .GroupBy(item => Math.Round(item.X, 8).ToString("R", CultureInfo.InvariantCulture) + "|" + Math.Round(item.Y, 8).ToString("R", CultureInfo.InvariantCulture))
                .Select(group => group.First())
                .OrderBy(item => item.X)
                .ThenBy(item => item.Y)
                .ToList();
            if (points.Count <= 3) return points;
            var lower = new List<Point2d>();
            foreach (Point2d point in points)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0.0) lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }
            var upper = new List<Point2d>();
            for (int index = points.Count - 1; index >= 0; index--)
            {
                Point2d point = points[index];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0.0) upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d origin, Point2d a, Point2d b)
        {
            return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
        }

        private static string Csv(string value)
        {
            string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (text.IndexOfAny(new[] { ',', '"' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class StyleChoice
        {
            internal readonly string Name;
            internal readonly ObjectId Id;
            internal StyleChoice(string name, ObjectId id) { Name = name; Id = id; }
        }
    }
}
