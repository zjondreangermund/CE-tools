using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.CoordinatePolylineCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates sequential Civil 3D COGO points at every polyline vertex in the
    /// stored polyline direction and adds a matching XYZ setting-out table.
    /// </summary>
    public sealed class CoordinatePolylineCommands
    {
        private const double PointTolerance = 1e-8;

        [CommandMethod(
            "CE_TOOLS",
            "CE_COORDPOLY",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreatePolylineVertexPoints()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage(
                    "\nCE_COORDPOLY cancelled. No active Civil 3D document is available.");
                return;
            }

            var entityOptions = new PromptEntityOptions(
                "\nSelect a polyline. Vertex order will follow the polyline direction: ");
            entityOptions.SetRejectMessage(
                "\nSelect an AutoCAD 2D, lightweight or 3D polyline.");
            entityOptions.AddAllowedClass(typeof(Polyline), false);
            entityOptions.AddAllowedClass(typeof(Polyline2d), false);
            entityOptions.AddAllowedClass(typeof(Polyline3d), false);

            PromptEntityResult entityResult = editor.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK)
            {
                return;
            }

            List<Point3d> vertices;
            string sourceLayer;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity source = transaction.GetObject(
                        entityResult.ObjectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (source == null)
                    {
                        editor.WriteMessage(
                            "\nCE_COORDPOLY cancelled. The selected polyline could not be opened.");
                        return;
                    }

                    sourceLayer = source.Layer;
                    vertices = ReadVertices(source, transaction);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_COORDPOLY cancelled while reading the polyline: " +
                    exception.Message);
                return;
            }

            if (vertices.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_COORDPOLY cancelled. The selected polyline contains no usable vertices.");
                return;
            }

            string defaultPrefix = BuildDefaultDescriptionPrefix(sourceLayer);
            var descriptionOptions = new PromptStringOptions(
                "\nPoint description prefix <" + defaultPrefix + ">: ")
            {
                AllowSpaces = true,
                DefaultValue = defaultPrefix,
                UseDefaultValue = true
            };
            PromptResult descriptionResult = editor.GetString(descriptionOptions);
            if (descriptionResult.Status != PromptStatus.OK)
            {
                return;
            }

            string descriptionPrefix =
                (descriptionResult.StringResult ?? defaultPrefix).Trim();
            if (descriptionPrefix.Length == 0)
            {
                descriptionPrefix = defaultPrefix;
            }

            var startOptions = new PromptIntegerOptions(
                "\nStarting description sequence number <1>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1,
                LowerLimit = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult startResult = editor.GetInteger(startOptions);
            if (startResult.Status != PromptStatus.OK)
            {
                return;
            }

            var tablePointOptions = new PromptPointOptions(
                "\nPick insertion point for the polyline vertex XYZ table: ");
            PromptPointResult tablePointResult = editor.GetPoint(tablePointOptions);
            if (tablePointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d tablePoint = tablePointResult.Value.TransformBy(
                editor.CurrentUserCoordinateSystem);

            string firstDescription = FormatDescription(
                descriptionPrefix,
                startResult.Value);
            string lastDescription = FormatDescription(
                descriptionPrefix,
                startResult.Value + vertices.Count - 1);

            editor.WriteMessage(
                "\nCE_COORDPOLY preview: vertices={0}; first={1}; last={2}. " +
                "Civil 3D point numbers will use the drawing's next-point-number sequence.",
                vertices.Count,
                firstDescription,
                lastDescription);

            if (!Confirm(editor, "Create the COGO points and XYZ table"))
            {
                editor.WriteMessage(
                    "\nCE_COORDPOLY cancelled. No points or table were created.");
                return;
            }

            var pointLocations = new Point3dCollection();
            var descriptions = new List<string>();
            for (int index = 0; index < vertices.Count; index++)
            {
                pointLocations.Add(vertices[index]);
                descriptions.Add(FormatDescription(
                    descriptionPrefix,
                    startResult.Value + index));
            }

            var createdPointIds = new List<ObjectId>();
            try
            {
                CogoPointCollection cogoPoints = civilDocument.CogoPoints;
                ObjectIdCollection added = cogoPoints.Add(
                    pointLocations,
                    true);

                foreach (ObjectId pointId in added)
                {
                    createdPointIds.Add(pointId);
                }

                if (createdPointIds.Count != vertices.Count)
                {
                    throw new InvalidOperationException(
                        "Civil 3D did not create the expected number of COGO points.");
                }

                ObjectIdCollection descriptionsSet =
                    cogoPoints.SetRawDescription(
                        createdPointIds,
                        descriptions);
                if (descriptionsSet.Count != createdPointIds.Count)
                {
                    throw new InvalidOperationException(
                        "Civil 3D could not assign every sequential point description.");
                }

                List<CoordinateRecord> records = ReadCreatedPoints(
                    document.Database,
                    createdPointIds,
                    descriptions);
                AddCoordinateTable(
                    document.Database,
                    tablePoint,
                    records);

                editor.WriteMessage(
                    "\nCE_COORDPOLY complete. COGO points created: {0}; XYZ table rows: {0}.",
                    createdPointIds.Count);
            }
            catch (System.Exception exception)
            {
                TryEraseCreatedPoints(document.Database, createdPointIds);
                editor.WriteMessage(
                    "\nCE_COORDPOLY cancelled. Created points were removed where possible. " +
                    exception.Message);
            }
        }

        private static List<Point3d> ReadVertices(
            Entity source,
            Transaction transaction)
        {
            var points = new List<Point3d>();

            var lightweight = source as Polyline;
            if (lightweight != null)
            {
                for (int index = 0; index < lightweight.NumberOfVertices; index++)
                {
                    AddDistinct(points, lightweight.GetPoint3dAt(index));
                }
                return points;
            }

            var polyline2d = source as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null)
                    {
                        AddDistinct(points, vertex.Position);
                    }
                }
                RemoveClosingDuplicate(points);
                return points;
            }

            var polyline3d = source as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null)
                    {
                        AddDistinct(points, vertex.Position);
                    }
                }
                RemoveClosingDuplicate(points);
            }

            return points;
        }

        private static void AddDistinct(
            IList<Point3d> points,
            Point3d point)
        {
            if (points.Count == 0 ||
                points[points.Count - 1].DistanceTo(point) > PointTolerance)
            {
                points.Add(point);
            }
        }

        private static void RemoveClosingDuplicate(IList<Point3d> points)
        {
            if (points.Count > 1 &&
                points[0].DistanceTo(points[points.Count - 1]) <= PointTolerance)
            {
                points.RemoveAt(points.Count - 1);
            }
        }

        private static string BuildDefaultDescriptionPrefix(string layer)
        {
            string value = string.IsNullOrWhiteSpace(layer)
                ? "PL-VTX"
                : layer.Trim();

            value = value.Replace(' ', '-');
            return value.Length > 24
                ? value.Substring(0, 24)
                : value;
        }

        private static string FormatDescription(
            string prefix,
            int sequence)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1:D3}",
                prefix,
                sequence);
        }

        private static List<CoordinateRecord> ReadCreatedPoints(
            Database database,
            IList<ObjectId> pointIds,
            IList<string> descriptions)
        {
            var records = new List<CoordinateRecord>();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                for (int index = 0; index < pointIds.Count; index++)
                {
                    CivilCogoPoint point = transaction.GetObject(
                        pointIds[index],
                        OpenMode.ForRead,
                        false) as CivilCogoPoint;
                    if (point == null)
                    {
                        throw new InvalidOperationException(
                            "A created COGO point could not be reopened.");
                    }

                    records.Add(new CoordinateRecord(
                        point.PointNumber.ToString(CultureInfo.InvariantCulture),
                        descriptions[index],
                        point.Easting,
                        point.Northing,
                        point.Elevation,
                        index + 1));
                }
            }

            return records;
        }

        private static void AddCoordinateTable(
            Database database,
            Point3d insertionPoint,
            IList<CoordinateRecord> records)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace =
                    (BlockTableRecord)transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);

                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertionPoint;
                table.SetSize(records.Count + 2, 6);

                double textHeight = GetTextHeight(database);
                table.SetRowHeight(textHeight * 2.0);
                table.SetColumnWidth(textHeight * 9.0);

                table.Cells[0, 0].TextString =
                    "POLYLINE VERTEX COGO POINTS";
                table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));

                string[] headings =
                {
                    "POINT",
                    "DESCRIPTION",
                    "X / EASTING",
                    "Y / NORTHING",
                    "Z / ELEVATION",
                    "VERTEX"
                };

                for (int column = 0; column < headings.Length; column++)
                {
                    table.Cells[1, column].TextString = headings[column];
                }

                for (int index = 0; index < records.Count; index++)
                {
                    CoordinateRecord record = records[index];
                    int row = index + 2;
                    table.Cells[row, 0].TextString = record.PointNumber;
                    table.Cells[row, 1].TextString = record.Description;
                    table.Cells[row, 2].TextString =
                        record.X.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 3].TextString =
                        record.Y.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 4].TextString =
                        record.Z.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 5].TextString =
                        record.VertexNumber.ToString(
                            CultureInfo.InvariantCulture);
                }

                currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.GenerateLayout();
                transaction.Commit();
            }
        }

        private static void TryEraseCreatedPoints(
            Database database,
            IEnumerable<ObjectId> pointIds)
        {
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId pointId in pointIds)
                    {
                        if (pointId.IsNull || pointId.IsErased)
                        {
                            continue;
                        }

                        DBObject point = transaction.GetObject(
                            pointId,
                            OpenMode.ForWrite,
                            false);
                        point.Erase();
                    }

                    transaction.Commit();
                }
            }
            catch
            {
                // Best-effort cleanup only. The command reports that cleanup
                // occurred where possible instead of hiding the original error.
            }
        }

        private static double GetTextHeight(Database database)
        {
            double textHeight = database.Textsize;
            return textHeight > 0.0 &&
                   !double.IsNaN(textHeight) &&
                   !double.IsInfinity(textHeight)
                ? textHeight
                : 2.5;
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
                   string.Equals(
                       result.StringResult,
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private sealed class CoordinateRecord
        {
            public CoordinateRecord(
                string pointNumber,
                string description,
                double x,
                double y,
                double z,
                int vertexNumber)
            {
                PointNumber = pointNumber;
                Description = description;
                X = x;
                Y = y;
                Z = z;
                VertexNumber = vertexNumber;
            }

            public string PointNumber { get; }
            public string Description { get; }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
            public int VertexNumber { get; }
        }
    }
}
