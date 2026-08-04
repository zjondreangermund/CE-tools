using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    public sealed partial class DynamicTypicalDetailCommands
    {
        private static ObjectId CreateLinkedDetail(
            Database database,
            Point3d insertionPoint,
            DetailParameters parameters,
            DynamicDetailSettings settings,
            string sourcePath,
            string sourceHash,
            string sourceModifiedUtc)
        {
            parameters.Normalize();
            settings.Normalize();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId detailLayer = GetOrCreateLayer(database, transaction, settings.DetailLayer, DefaultDetailLayer);
                ObjectId boqLayer = GetOrCreateLayer(database, transaction, settings.BoqLayer, DefaultBoqLayer);

                var anchor = new DBPoint(insertionPoint);
                anchor.SetDatabaseDefaults(database);
                anchor.LayerId = detailLayer;
                currentSpace.AppendEntity(anchor);
                transaction.AddNewlyCreatedDBObject(anchor, true);
                anchor.CreateExtensionDictionary();

                string detailId = "DD-" + anchor.Handle;
                DynamicDetailLink link = new DynamicDetailLink(
                    SchemaVersion,
                    detailId,
                    insertionPoint,
                    parameters,
                    settings,
                    sourcePath,
                    sourceHash,
                    sourceModifiedUtc,
                    new List<string>(),
                    string.Empty);
                GeneratedSet generated = GenerateAll(
                    database,
                    currentSpace,
                    anchor,
                    link,
                    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                    detailLayer,
                    boqLayer,
                    transaction);
                WriteLink(anchor, transaction, link.WithGenerated(generated.Handles, generated.BoqTableHandle));
                transaction.Commit();
                return anchor.ObjectId;
            }
        }

        private static void Regenerate(Document document, ObjectId anchorId, DynamicDetailLink newLink, bool report, string commandName)
        {
            try
            {
                Dictionary<string, double> rates = ReadExistingRates(document.Database, newLink);
                int oldCount = newLink.GeneratedHandles.Count;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                    if (anchor == null || !HasExtensionRecord(anchor, transaction, LinkRecordName))
                        throw new InvalidOperationException("The selected dynamic-detail anchor is missing or detached.");

                    foreach (string handle in newLink.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(document.Database, handle, out id))
                            continue;
                        Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (generated != null && HasExtensionRecord(generated, transaction, GeneratedRecordName))
                            generated.Erase();
                    }

                    BlockTableRecord ownerSpace = transaction.GetObject(anchor.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (ownerSpace == null)
                        throw new InvalidOperationException("The dynamic-detail anchor owner space is unavailable.");
                    ObjectId detailLayer = GetOrCreateLayer(document.Database, transaction, newLink.Settings.DetailLayer, DefaultDetailLayer);
                    ObjectId boqLayer = GetOrCreateLayer(document.Database, transaction, newLink.Settings.BoqLayer, DefaultBoqLayer);
                    GeneratedSet generatedSet = GenerateAll(
                        document.Database,
                        ownerSpace,
                        anchor,
                        newLink,
                        rates,
                        detailLayer,
                        boqLayer,
                        transaction);
                    WriteLink(anchor, transaction, newLink.WithGenerated(generatedSet.Handles, generatedSet.BoqTableHandle));
                    transaction.Commit();

                    if (report)
                    {
                        document.Editor.WriteMessage(
                            "\n{0} complete. Detail={1}; old generated={2}; new generated={3}; review status={4}; source template writes=0.",
                            commandName,
                            newLink.DetailId,
                            oldCount,
                            generatedSet.Handles.Count,
                            newLink.Parameters.ReviewStatus);
                    }
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\n" + commandName + " failed. The uncommitted transaction preserved the previous linked output. " + exception.Message);
            }
        }

        private static GeneratedSet GenerateAll(
            Database database,
            BlockTableRecord currentSpace,
            Entity anchor,
            DynamicDetailLink link,
            IDictionary<string, double> rates,
            ObjectId detailLayer,
            ObjectId boqLayer,
            Transaction transaction)
        {
            var handles = new List<string>();
            string ownerHandle = anchor.Handle.ToString();
            AddTitleAndStatus(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);

            string type = link.Parameters.DetailType;
            if (type.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase))
                GenerateTrenchDrain(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else if (type.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase))
                GeneratePipeTrench(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else if (type.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase))
                GenerateValveChamber(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else if (type.Equals("Kerb", StringComparison.OrdinalIgnoreCase))
                GenerateKerb(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else if (type.Equals("Headwall", StringComparison.OrdinalIgnoreCase))
                GenerateHeadwall(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else
                throw new InvalidOperationException("Unsupported dynamic detail type: " + type);

            Table parameterTable = BuildParameterTable(database, link);
            parameterTable.LayerId = boqLayer;
            AppendGenerated(currentSpace, parameterTable, transaction, ownerHandle, handles);

            List<QuantityItem> quantities = CalculateQuantities(link.Parameters, rates);
            Table boqTable = BuildBoqTable(database, link, quantities);
            boqTable.LayerId = boqLayer;
            AppendGenerated(currentSpace, boqTable, transaction, ownerHandle, handles);
            WriteBoqLink(boqTable, transaction, ownerHandle, link, quantities);
            return new GeneratedSet(handles, boqTable.Handle.ToString());
        }

        private static void AddTitleAndStatus(
            Database database,
            BlockTableRecord space,
            DynamicDetailLink link,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            DynamicDetailSettings settings = link.Settings;
            Point3d origin = link.InsertionPoint;
            var title = new MText();
            title.SetDatabaseDefaults(database);
            title.LayerId = layerId;
            title.Location = origin + new Vector3d(0.0, settings.TextHeight * 2.5, 0.0);
            title.Attachment = AttachmentPoint.BottomLeft;
            title.TextHeight = settings.TextHeight * 1.25;
            title.Contents =
                link.DetailId + " - " + DisplayType(link.Parameters.DetailType) +
                "\nPARAMETER-DRIVEN GENERATED VARIANT" +
                "\nRecorded status: " + link.Parameters.ReviewStatus +
                (string.IsNullOrWhiteSpace(link.Parameters.Reviewer) ? string.Empty : " | " + link.Parameters.Reviewer) +
                "\nSource identity: " + (string.IsNullOrWhiteSpace(link.SourceHash) ? "built-in schematic" : link.SourceHash) +
                "\nNot an approved library standard unless external authority is verified.";
            title.BackgroundFill = true;
            title.UseBackgroundColor = true;
            AppendGenerated(space, title, transaction, ownerHandle, handles);

            var marker = new Circle(origin, Vector3d.ZAxis, settings.TextHeight * 0.45);
            marker.SetDatabaseDefaults(database);
            marker.LayerId = layerId;
            AppendGenerated(space, marker, transaction, ownerHandle, handles);
        }

        private static void GenerateTrenchDrain(Database database, BlockTableRecord space, DynamicDetailLink link, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double wall = ToDrawingUnits(p.WallThicknessMillimetres, s);
            ValidateSection(width, depth, wall);
            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            AddOpenInnerChannel(database, space, o + new Vector3d(wall, wall, 0.0), width - 2.0 * wall, depth - wall, layerId, transaction, ownerHandle, handles);
            AddGrating(database, space, o + new Vector3d(0.0, depth, 0.0), width, p.GratingType, s, layerId, transaction, ownerHandle, handles);
            AddReinforcementMarkers(database, space, o, width, depth, wall, layerId, transaction, ownerHandle, handles);
            AddDimensionsAndCallout(database, space, o, width, depth, p, s, "Trench drain section", layerId, transaction, ownerHandle, handles);
        }

        private static void GeneratePipeTrench(Database database, BlockTableRecord space, DynamicDetailLink link, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double diameter = ToDrawingUnits(p.PipeDiameterMillimetres, s);
            double bedding = ToDrawingUnits(p.BeddingDepthMillimetres, s);
            if (width <= diameter || depth <= diameter + bedding)
                throw new InvalidOperationException("Pipe trench width/depth must exceed the pipe diameter and bedding depth.");

            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            var beddingLine = new Line(o + new Vector3d(0.0, bedding, 0.0), o + new Vector3d(width, bedding, 0.0));
            beddingLine.SetDatabaseDefaults(database);
            beddingLine.LayerId = layerId;
            AppendGenerated(space, beddingLine, transaction, ownerHandle, handles);
            var pipe = new Circle(o + new Vector3d(width * 0.5, bedding + diameter * 0.5, 0.0), Vector3d.ZAxis, diameter * 0.5);
            pipe.SetDatabaseDefaults(database);
            pipe.LayerId = layerId;
            AppendGenerated(space, pipe, transaction, ownerHandle, handles);
            AddDimensionsAndCallout(database, space, o, width, depth, p, s, "Pipe trench section", layerId, transaction, ownerHandle, handles);
        }

        private static void GenerateValveChamber(Database database, BlockTableRecord space, DynamicDetailLink link, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double wall = ToDrawingUnits(p.WallThicknessMillimetres, s);
            ValidateSection(width, depth, wall);
            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            AddRectangle(database, space, o + new Vector3d(wall, wall, 0.0), width - 2.0 * wall, depth - 2.0 * wall, layerId, transaction, ownerHandle, handles);
            AddGrating(database, space, o + new Vector3d(width * 0.25, depth, 0.0), width * 0.5, p.GratingType, s, layerId, transaction, ownerHandle, handles);
            double rungSpacing = Math.Max(s.TextHeight * 2.5, wall * 0.8);
            for (double y = wall * 1.5; y < depth - wall * 1.5; y += rungSpacing)
            {
                var rung = new Line(o + new Vector3d(wall * 1.2, y, 0.0), o + new Vector3d(wall * 2.0, y, 0.0));
                rung.SetDatabaseDefaults(database);
                rung.LayerId = layerId;
                AppendGenerated(space, rung, transaction, ownerHandle, handles);
            }
            AddDimensionsAndCallout(database, space, o, width, depth, p, s, "Valve chamber section", layerId, transaction, ownerHandle, handles);
        }

        private static void GenerateKerb(Database database, BlockTableRecord space, DynamicDetailLink link, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            if (width <= GeometryTolerance || depth <= GeometryTolerance)
                throw new InvalidOperationException("Kerb width and depth must be positive.");
            Point3d o = link.InsertionPoint;
            var kerb = new Polyline(5);
            kerb.SetDatabaseDefaults(database);
            kerb.LayerId = layerId;
            kerb.AddVertexAt(0, new Point2d(o.X, o.Y), 0.0, 0.0, 0.0);
            kerb.AddVertexAt(1, new Point2d(o.X + width, o.Y), 0.0, 0.0, 0.0);
            kerb.AddVertexAt(2, new Point2d(o.X + width, o.Y + depth * 0.72), 0.0, 0.0, 0.0);
            kerb.AddVertexAt(3, new Point2d(o.X + width * 0.72, o.Y + depth), 0.0, 0.0, 0.0);
            kerb.AddVertexAt(4, new Point2d(o.X, o.Y + depth), 0.0, 0.0, 0.0);
            kerb.Closed = true;
            AppendGenerated(space, kerb, transaction, ownerHandle, handles);
            AddReinforcementMarkers(database, space, o, width, depth, Math.Min(width, depth) * 0.25, layerId, transaction, ownerHandle, handles);
            AddDimensionsAndCallout(database, space, o, width, depth, p, s, "Kerb section", layerId, transaction, ownerHandle, handles);
        }

        private static void GenerateHeadwall(Database database, BlockTableRecord space, DynamicDetailLink link, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double wall = ToDrawingUnits(p.WallThicknessMillimetres, s);
            double diameter = ToDrawingUnits(p.PipeDiameterMillimetres, s);
            ValidateSection(width, depth, wall);
            if (diameter >= Math.Min(width - 2.0 * wall, depth - 2.0 * wall))
                throw new InvalidOperationException("Headwall pipe diameter must fit inside the wall clear dimensions.");
            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            var opening = new Circle(o + new Vector3d(width * 0.5, Math.Max(wall + diameter * 0.5, depth * 0.45), 0.0), Vector3d.ZAxis, diameter * 0.5);
            opening.SetDatabaseDefaults(database);
            opening.LayerId = layerId;
            AppendGenerated(space, opening, transaction, ownerHandle, handles);
            double wing = Math.Max(wall, width * 0.18);
            var leftWing = new Line(o, o + new Vector3d(-wing, depth * 0.35, 0.0));
            leftWing.SetDatabaseDefaults(database);
            leftWing.LayerId = layerId;
            AppendGenerated(space, leftWing, transaction, ownerHandle, handles);
            var rightWing = new Line(o + new Vector3d(width, 0.0, 0.0), o + new Vector3d(width + wing, depth * 0.35, 0.0));
            rightWing.SetDatabaseDefaults(database);
            rightWing.LayerId = layerId;
            AppendGenerated(space, rightWing, transaction, ownerHandle, handles);
            AddReinforcementMarkers(database, space, o, width, depth, wall, layerId, transaction, ownerHandle, handles);
            AddDimensionsAndCallout(database, space, o, width, depth, p, s, "Headwall front elevation", layerId, transaction, ownerHandle, handles);
        }

        private static void AddRectangle(Database database, BlockTableRecord space, Point3d origin, double width, double height, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            var polyline = new Polyline(4);
            polyline.SetDatabaseDefaults(database);
            polyline.LayerId = layerId;
            polyline.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0.0, 0.0, 0.0);
            polyline.Closed = true;
            AppendGenerated(space, polyline, transaction, ownerHandle, handles);
        }

        private static void AddOpenInnerChannel(Database database, BlockTableRecord space, Point3d origin, double width, double height, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            var channel = new Polyline(4);
            channel.SetDatabaseDefaults(database);
            channel.LayerId = layerId;
            channel.AddVertexAt(0, new Point2d(origin.X, origin.Y + height), 0.0, 0.0, 0.0);
            channel.AddVertexAt(1, new Point2d(origin.X, origin.Y), 0.0, 0.0, 0.0);
            channel.AddVertexAt(2, new Point2d(origin.X + width, origin.Y), 0.0, 0.0, 0.0);
            channel.AddVertexAt(3, new Point2d(origin.X + width, origin.Y + height), 0.0, 0.0, 0.0);
            AppendGenerated(space, channel, transaction, ownerHandle, handles);
        }

        private static void AddGrating(Database database, BlockTableRecord space, Point3d start, double width, string gratingType, DynamicDetailSettings settings, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            var top = new Line(start, start + new Vector3d(width, 0.0, 0.0));
            top.SetDatabaseDefaults(database);
            top.LayerId = layerId;
            AppendGenerated(space, top, transaction, ownerHandle, handles);
            int divisions = Math.Max(4, Math.Min(20, (int)Math.Round(width / Math.Max(settings.TextHeight * 2.0, width / 10.0))));
            for (int index = 0; index <= divisions; index++)
            {
                double x = width * index / divisions;
                var bar = new Line(start + new Vector3d(x, 0.0, 0.0), start + new Vector3d(x + settings.TextHeight * 0.35, settings.TextHeight * 0.7, 0.0));
                bar.SetDatabaseDefaults(database);
                bar.LayerId = layerId;
                AppendGenerated(space, bar, transaction, ownerHandle, handles);
            }
            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.LayerId = layerId;
            label.Location = start + new Vector3d(width * 0.5, settings.TextHeight * 1.2, 0.0);
            label.Attachment = AttachmentPoint.BottomCenter;
            label.TextHeight = settings.TextHeight;
            label.Contents = string.IsNullOrWhiteSpace(gratingType) ? "Cover / grating to approved specification" : gratingType;
            AppendGenerated(space, label, transaction, ownerHandle, handles);
        }

        private static void AddReinforcementMarkers(Database database, BlockTableRecord space, Point3d origin, double width, double depth, double wall, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            double radius = Math.Max(wall * 0.08, Math.Min(width, depth) * 0.008);
            Point3d[] points =
            {
                origin + new Vector3d(wall * 0.5, wall * 0.5, 0.0),
                origin + new Vector3d(width - wall * 0.5, wall * 0.5, 0.0),
                origin + new Vector3d(wall * 0.5, depth - wall * 0.5, 0.0),
                origin + new Vector3d(width - wall * 0.5, depth - wall * 0.5, 0.0)
            };
            foreach (Point3d point in points)
            {
                var circle = new Circle(point, Vector3d.ZAxis, radius);
                circle.SetDatabaseDefaults(database);
                circle.LayerId = layerId;
                AppendGenerated(space, circle, transaction, ownerHandle, handles);
            }
        }

        private static void AddDimensionsAndCallout(Database database, BlockTableRecord space, Point3d origin, double width, double depth, DetailParameters parameters, DynamicDetailSettings settings, string description, ObjectId layerId, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            var widthDimension = new AlignedDimension(
                origin,
                origin + new Vector3d(width, 0.0, 0.0),
                origin + new Vector3d(0.0, -settings.DimensionOffset, 0.0),
                parameters.WidthMillimetres.ToString("0", CultureInfo.InvariantCulture) + " mm",
                database.Dimstyle);
            widthDimension.SetDatabaseDefaults(database);
            widthDimension.LayerId = layerId;
            AppendGenerated(space, widthDimension, transaction, ownerHandle, handles);

            var depthDimension = new AlignedDimension(
                origin,
                origin + new Vector3d(0.0, depth, 0.0),
                origin + new Vector3d(-settings.DimensionOffset, 0.0, 0.0),
                parameters.DepthMillimetres.ToString("0", CultureInfo.InvariantCulture) + " mm",
                database.Dimstyle);
            depthDimension.SetDatabaseDefaults(database);
            depthDimension.LayerId = layerId;
            AppendGenerated(space, depthDimension, transaction, ownerHandle, handles);

            var note = new MText();
            note.SetDatabaseDefaults(database);
            note.LayerId = layerId;
            note.Location = origin + new Vector3d(width + settings.TextHeight * 2.0, depth * 0.5, 0.0);
            note.Attachment = AttachmentPoint.MiddleLeft;
            note.TextHeight = settings.TextHeight;
            note.Contents =
                description +
                "\nConcrete: " + parameters.ConcreteStrength +
                "\nReinforcement: " + parameters.Reinforcement +
                "\nCover / grating: " + parameters.GratingType +
                "\nPipe diameter: " + parameters.PipeDiameterMillimetres.ToString("0", CultureInfo.InvariantCulture) + " mm" +
                "\nAll dimensions and specifications require engineer/authority review.";
            note.BackgroundFill = true;
            note.UseBackgroundColor = true;
            AppendGenerated(space, note, transaction, ownerHandle, handles);
        }

        private static Table BuildParameterTable(Database database, DynamicDetailLink link)
        {
            DynamicDetailSettings s = link.Settings;
            DetailParameters p = link.Parameters;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            Point3d position = link.InsertionPoint + new Vector3d(width + s.ScheduleOffset, -s.TextHeight * 8.0, 0.0);
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Detail ID", link.DetailId),
                Pair("Type", DisplayType(p.DetailType)),
                Pair("Width", p.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Depth", p.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Length / plan thickness", p.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
                Pair("Wall / slab thickness", p.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Pipe diameter", p.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Bedding depth", p.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Concrete", p.ConcreteStrength),
                Pair("Reinforcement", p.Reinforcement),
                Pair("Grating / cover", p.GratingType),
                Pair("Review status", p.ReviewStatus),
                Pair("Reviewer / reference", p.Reviewer),
                Pair("Reviewed at UTC", p.ReviewedAtUtc),
                Pair("Source template", string.IsNullOrWhiteSpace(link.SourcePath) ? "Built-in schematic / no external source selected" : link.SourcePath),
                Pair("Source SHA-256", link.SourceHash),
                Pair("Source modified UTC", link.SourceModifiedUtc)
            };

            var table = new Table { TableStyle = database.Tablestyle, Position = position };
            table.SetSize(rows.Count + 2, 2);
            table.SetRowHeight(s.TextHeight * 1.8);
            table.Columns[0].Width = s.TextHeight * 18.0;
            table.Columns[1].Width = s.TextHeight * 55.0;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            table.Cells[0, 0].TextString = "CE Dynamic Typical Detail Parameters";
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[1, 0].TextString = "Parameter";
            table.Cells[1, 1].TextString = "Value";
            for (int index = 0; index < rows.Count; index++)
            {
                table.Cells[index + 2, 0].TextString = rows[index].Key;
                table.Cells[index + 2, 1].TextString = rows[index].Value;
            }
            FormatTable(table, s.TextHeight);
            table.GenerateLayout();
            return table;
        }

        private static Table BuildBoqTable(Database database, DynamicDetailLink link, IReadOnlyList<QuantityItem> quantities)
        {
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(link.Parameters.WidthMillimetres, s);
            Point3d position = link.InsertionPoint + new Vector3d(width + s.ScheduleOffset, -s.TextHeight * 48.0, 0.0);
            var table = new Table { TableStyle = database.Tablestyle, Position = position };
            table.SetSize(quantities.Count + 3, 6);
            table.SetRowHeight(s.TextHeight * 1.8);
            double[] widths = { 10, 38, 8, 14, 14, 16 };
            for (int column = 0; column < widths.Length; column++)
                table.Columns[column].Width = s.TextHeight * widths[column];
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            table.Cells[0, 0].TextString = "Linked Dynamic Detail Quantity Schedule - " + link.DetailId;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[] headings = { "Item", "Description", "Unit", "Quantity", "Rate", "Amount" };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < quantities.Count; index++)
            {
                QuantityItem item = quantities[index];
                string[] values =
                {
                    item.Key,
                    item.Description,
                    item.Unit,
                    item.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                    item.Rate.ToString("0.00", CultureInfo.InvariantCulture),
                    item.Amount.ToString("0.00", CultureInfo.InvariantCulture)
                };
                for (int column = 0; column < values.Length; column++)
                    table.Cells[index + 2, column].TextString = values[column];
            }
            table.MergeCells(CellRange.Create(table, quantities.Count + 2, 0, quantities.Count + 2, 5));
            table.Cells[quantities.Count + 2, 0].TextString =
                "Parameter-derived preliminary quantities linked to anchor " + link.DetailId + ". Rates may be entered manually. " +
                "Reinforcement is recorded as a specification item, not a certified bar-bending schedule. Review before BOQ issue.";
            FormatTable(table, s.TextHeight);
            table.GenerateLayout();
            return table;
        }

        private static void FormatTable(Table table, double textHeight)
        {
            for (int row = 0; row < table.Rows.Count; row++)
            {
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].TextHeight = textHeight;
                    if (!(row == 0))
                        table.Cells[row, column].Alignment = CellAlignment.MiddleLeft;
                }
            }
        }

        private static List<QuantityItem> CalculateQuantities(DetailParameters p, IDictionary<string, double> rates)
        {
            double width = p.WidthMillimetres / 1000.0;
            double depth = p.DepthMillimetres / 1000.0;
            double length = p.LengthMetres;
            double wall = p.WallThicknessMillimetres / 1000.0;
            double diameter = p.PipeDiameterMillimetres / 1000.0;
            double bedding = p.BeddingDepthMillimetres / 1000.0;
            var items = new List<QuantityItem>();

            if (p.DetailType.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase))
            {
                double innerWidth = Math.Max(0.0, width - 2.0 * wall);
                double innerDepth = Math.Max(0.0, depth - wall);
                AddQuantity(items, rates, "EXC", "Trench excavation", "m³", width * depth * length);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " concrete", "m³", Math.Max(0.0, width * depth - innerWidth * innerDepth) * length);
                AddQuantity(items, rates, "GRATE", p.GratingType, "m", length);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            else if (p.DetailType.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase))
            {
                double excavation = width * depth * length;
                double beddingVolume = width * bedding * length;
                double pipeVolume = Math.PI * diameter * diameter * 0.25 * length;
                AddQuantity(items, rates, "EXC", "Trench excavation", "m³", excavation);
                AddQuantity(items, rates, "PIPE", "Pipe DN " + p.PipeDiameterMillimetres.ToString("0", CultureInfo.InvariantCulture), "m", length);
                AddQuantity(items, rates, "BED", "Selected bedding", "m³", beddingVolume);
                AddQuantity(items, rates, "BACKFILL", "Selected backfill excluding idealised pipe displacement", "m³", Math.Max(0.0, excavation - beddingVolume - pipeVolume));
            }
            else if (p.DetailType.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase))
            {
                double planLength = length;
                double innerWidth = Math.Max(0.0, width - 2.0 * wall);
                double innerLength = Math.Max(0.0, planLength - 2.0 * wall);
                double innerDepth = Math.Max(0.0, depth - wall);
                double outerVolume = width * planLength * depth;
                double voidVolume = innerWidth * innerLength * innerDepth;
                AddQuantity(items, rates, "EXC", "Valve chamber excavation envelope", "m³", outerVolume);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " chamber concrete", "m³", Math.Max(0.0, outerVolume - voidVolume));
                AddQuantity(items, rates, "COVER", p.GratingType, "No.", 1.0);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            else if (p.DetailType.Equals("Kerb", StringComparison.OrdinalIgnoreCase))
            {
                double chamferReduction = width * 0.28 * depth * 0.28 * 0.5;
                double area = Math.Max(0.0, width * depth - chamferReduction);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " kerb concrete", "m³", area * length);
                AddQuantity(items, rates, "FORM", "Kerb side formwork", "m²", 2.0 * depth * length);
                AddQuantity(items, rates, "KERB", "Completed kerb", "m", length);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            else if (p.DetailType.Equals("Headwall", StringComparison.OrdinalIgnoreCase))
            {
                double thickness = length;
                double gross = width * depth * thickness;
                double opening = Math.PI * diameter * diameter * 0.25 * thickness;
                AddQuantity(items, rates, "EXC", "Headwall excavation envelope", "m³", gross);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " headwall concrete", "m³", Math.Max(0.0, gross - opening));
                AddQuantity(items, rates, "OPEN", "Pipe opening DN " + p.PipeDiameterMillimetres.ToString("0", CultureInfo.InvariantCulture), "No.", 1.0);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            return items;
        }

        private static void AddQuantity(ICollection<QuantityItem> items, IDictionary<string, double> rates, string key, string description, string unit, double quantity)
        {
            double rate;
            if (rates == null || !rates.TryGetValue(key, out rate))
                rate = 0.0;
            items.Add(new QuantityItem(key, description, unit, Math.Max(0.0, quantity), rate));
        }

        private static Dictionary<string, double> ReadExistingRates(Database database, DynamicDetailLink link)
        {
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ObjectId tableId;
            if (string.IsNullOrWhiteSpace(link.BoqTableHandle) || !TryResolveHandle(database, link.BoqTableHandle, out tableId))
                return rates;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    if (table == null)
                        return rates;
                    for (int row = 2; row < table.Rows.Count - 1; row++)
                    {
                        string key = table.Cells[row, 0].TextString;
                        string rateText = table.Cells[row, 4].TextString;
                        double rate;
                        if (!string.IsNullOrWhiteSpace(key) && double.TryParse(rateText, NumberStyles.Any, CultureInfo.InvariantCulture, out rate))
                            rates[key.Trim()] = rate;
                    }
                }
            }
            catch
            {
                // A corrupt or manually changed schedule does not block regeneration.
            }
            return rates;
        }
    }
}
