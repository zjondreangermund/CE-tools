using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.CoordinateCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey and setting-out coordinate utilities exposed through one command.
    /// </summary>
    public sealed class CoordinateCommands
    {
        private const string PickKeyword = "Pick";
        private const string CogoKeyword = "Cogo";
        private const string CrossKeyword = "Cross";
        private const string TableKeyword = "Table";

        [CommandMethod(
            "CE_TOOLS",
            "CE_COORDINATE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            var options = new PromptKeywordOptions(
                "\nCoordinate tool [Pick/Cogo/Cross/Table] <Pick>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(PickKeyword);
            options.Keywords.Add(CogoKeyword);
            options.Keywords.Add(CrossKeyword);
            options.Keywords.Add(TableKeyword);

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? PickKeyword
                : result.StringResult;

            if (string.Equals(mode, CogoKeyword, StringComparison.OrdinalIgnoreCase))
            {
                BatchLabelCogoPoints(document);
            }
            else if (string.Equals(mode, CrossKeyword, StringComparison.OrdinalIgnoreCase))
            {
                PlaceCoordinateCross(document);
            }
            else if (string.Equals(mode, TableKeyword, StringComparison.OrdinalIgnoreCase))
            {
                CreateCoordinateTable(document);
            }
            else
            {
                PlacePickedCoordinate(document);
            }
        }

        private static void PlacePickedCoordinate(Document document)
        {
            Editor editor = document.Editor;
            PromptPointResult pointResult = editor.GetPoint("\nPick coordinate point: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d target = ToWorld(editor, pointResult.Value);
            PromptPointOptions labelOptions = new PromptPointOptions("\nPlace coordinate label: ")
            {
                BasePoint = pointResult.Value,
                UseBasePoint = true,
                UseDashedLine = true
            };
            PromptPointResult labelResult = editor.GetPoint(labelOptions);
            if (labelResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d label = ToWorld(editor, labelResult.Value);
            string contents = FormatCoordinate(target, null, null, 3);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                AddMLeader(document.Database, transaction, target, label, contents);
                transaction.Commit();
            }
        }

        private static void BatchLabelCogoPoints(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect Civil 3D COGO points to label: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int labelled = 0;
            int skipped = 0;
            Database database = document.Database;
            double textHeight = GetTextHeight(database);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null)
                    {
                        continue;
                    }

                    var point = transaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false) as CivilCogoPoint;

                    if (point == null)
                    {
                        skipped++;
                        continue;
                    }

                    var location = new Point3d(point.Easting, point.Northing, point.Elevation);
                    var label = new Point3d(
                        location.X + (textHeight * 8.0),
                        location.Y + (textHeight * 5.0),
                        location.Z);

                    string pointId = string.IsNullOrWhiteSpace(point.PointName)
                        ? point.PointNumber.ToString(CultureInfo.InvariantCulture)
                        : point.PointName;

                    AddMLeader(
                        database,
                        transaction,
                        location,
                        label,
                        FormatCoordinate(location, pointId, point.RawDescription, 3));
                    labelled++;
                }

                transaction.Commit();
            }

            editor.WriteMessage(
                $"\nCE_COORDINATE Cogo complete. Labelled: {labelled}; skipped: {skipped}.");
        }

        private static void PlaceCoordinateCross(Document document)
        {
            Editor editor = document.Editor;
            PromptPointResult pointResult = editor.GetPoint("\nPick coordinate-cross point: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d target = ToWorld(editor, pointResult.Value);
            Database database = document.Database;
            double textHeight = GetTextHeight(database);
            double halfSize = Math.Max(textHeight * 1.5, 0.001);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = GetCurrentSpace(database, transaction);

                var horizontal = new Line(
                    new Point3d(target.X - halfSize, target.Y, target.Z),
                    new Point3d(target.X + halfSize, target.Y, target.Z));
                horizontal.SetDatabaseDefaults(database);
                modelSpace.AppendEntity(horizontal);
                transaction.AddNewlyCreatedDBObject(horizontal, true);

                var vertical = new Line(
                    new Point3d(target.X, target.Y - halfSize, target.Z),
                    new Point3d(target.X, target.Y + halfSize, target.Z));
                vertical.SetDatabaseDefaults(database);
                modelSpace.AppendEntity(vertical);
                transaction.AddNewlyCreatedDBObject(vertical, true);

                var label = new Point3d(
                    target.X + (halfSize * 1.5),
                    target.Y + (halfSize * 1.5),
                    target.Z);

                AddMLeader(
                    database,
                    transaction,
                    target,
                    label,
                    FormatCoordinate(target, null, null, 3));

                transaction.Commit();
            }
        }

        private static void CreateCoordinateTable(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect AutoCAD points and/or Civil 3D COGO points: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var records = new List<CoordinateRecord>();
            int skipped = 0;
            Database database = document.Database;

            using (Transaction readTransaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null)
                    {
                        continue;
                    }

                    DBObject databaseObject = readTransaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false);

                    var cogoPoint = databaseObject as CivilCogoPoint;
                    if (cogoPoint != null)
                    {
                        string pointId = string.IsNullOrWhiteSpace(cogoPoint.PointName)
                            ? cogoPoint.PointNumber.ToString(CultureInfo.InvariantCulture)
                            : cogoPoint.PointName;

                        records.Add(new CoordinateRecord(
                            pointId,
                            cogoPoint.RawDescription,
                            cogoPoint.Easting,
                            cogoPoint.Northing,
                            cogoPoint.Elevation));
                        continue;
                    }

                    var dbPoint = databaseObject as DBPoint;
                    if (dbPoint != null)
                    {
                        records.Add(new CoordinateRecord(
                            (records.Count + 1).ToString(CultureInfo.InvariantCulture),
                            string.Empty,
                            dbPoint.Position.X,
                            dbPoint.Position.Y,
                            dbPoint.Position.Z));
                        continue;
                    }

                    skipped++;
                }
            }

            if (records.Count == 0)
            {
                editor.WriteMessage("\nNo supported coordinate points were selected.");
                return;
            }

            PromptPointResult insertionResult = editor.GetPoint("\nPick coordinate-table insertion point: ");
            if (insertionResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d insertionPoint = ToWorld(editor, insertionResult.Value);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = GetCurrentSpace(database, transaction);
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertionPoint;
                table.SetSize(records.Count + 2, 6);

                double textHeight = GetTextHeight(database);
                table.SetRowHeight(textHeight * 2.0);
                table.SetColumnWidth(textHeight * 8.0);

                table.Cells[0, 0].TextString = "COORDINATE SETTING-OUT TABLE";
                table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));

                string[] headings =
                {
                    "POINT",
                    "DESCRIPTION",
                    "X / EASTING",
                    "Y / NORTHING",
                    "Z / ELEVATION",
                    "NOTES"
                };

                for (int column = 0; column < headings.Length; column++)
                {
                    table.Cells[1, column].TextString = headings[column];
                }

                for (int index = 0; index < records.Count; index++)
                {
                    CoordinateRecord record = records[index];
                    int row = index + 2;
                    table.Cells[row, 0].TextString = record.Point;
                    table.Cells[row, 1].TextString = record.Description;
                    table.Cells[row, 2].TextString = record.X.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 3].TextString = record.Y.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 4].TextString = record.Z.ToString("N3", CultureInfo.CurrentCulture);
                    table.Cells[row, 5].TextString = string.Empty;
                }

                currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.GenerateLayout();
                transaction.Commit();
            }

            editor.WriteMessage(
                $"\nCE_COORDINATE Table complete. Added: {records.Count}; skipped: {skipped}.");
        }

        private static void AddMLeader(
            Database database,
            Transaction transaction,
            Point3d target,
            Point3d label,
            string contents)
        {
            BlockTableRecord currentSpace = GetCurrentSpace(database, transaction);
            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.TextHeight = GetTextHeight(database);
            mtext.Contents = contents;
            mtext.Location = label;

            var leader = new MLeader();
            leader.SetDatabaseDefaults(database);
            leader.ContentType = ContentType.MTextContent;
            leader.MText = mtext;

            int leaderIndex = leader.AddLeader();
            int leaderLineIndex = leader.AddLeaderLine(leaderIndex);
            leader.AddFirstVertex(leaderLineIndex, target);
            leader.AddLastVertex(leaderLineIndex, label);

            currentSpace.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
        }

        private static string FormatCoordinate(
            Point3d point,
            string pointId,
            string description,
            int precision)
        {
            string numberFormat = "N" + precision.ToString(CultureInfo.InvariantCulture);
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(pointId))
            {
                lines.Add(pointId);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                lines.Add(description);
            }

            lines.Add("X: " + point.X.ToString(numberFormat, CultureInfo.CurrentCulture));
            lines.Add("Y: " + point.Y.ToString(numberFormat, CultureInfo.CurrentCulture));
            lines.Add("Z: " + point.Z.ToString(numberFormat, CultureInfo.CurrentCulture));
            return string.Join("\\P", lines);
        }

        private static Point3d ToWorld(Editor editor, Point3d pointInCurrentUcs)
        {
            return pointInCurrentUcs.TransformBy(editor.CurrentUserCoordinateSystem);
        }

        private static double GetTextHeight(Database database)
        {
            double textHeight = database.Textsize;
            return textHeight > 0.0 && !double.IsNaN(textHeight) && !double.IsInfinity(textHeight)
                ? textHeight
                : 2.5;
        }

        private static BlockTableRecord GetCurrentSpace(
            Database database,
            Transaction transaction)
        {
            return (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite,
                false);
        }

        private sealed class CoordinateRecord
        {
            public CoordinateRecord(string point, string description, double x, double y, double z)
            {
                Point = point ?? string.Empty;
                Description = description ?? string.Empty;
                X = x;
                Y = y;
                Z = z;
            }

            public string Point { get; }
            public string Description { get; }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
        }
    }
}
