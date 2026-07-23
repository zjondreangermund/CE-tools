using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Straight-baseline parking row generation, counting and numbering tools.
    /// </summary>
    public sealed class ParkingCommands
    {
        private const string RowKeyword = "Row";
        private const string DoubleRowKeyword = "DoubleRow";
        private const string CountKeyword = "Count";
        private const string NumberKeyword = "Number";
        private const double GeometryTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ParkingTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nParking tool [Row/DoubleRow/Count/Number] <Row>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(RowKeyword);
            options.Keywords.Add(DoubleRowKeyword);
            options.Keywords.Add(CountKeyword);
            options.Keywords.Add(NumberKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? RowKeyword
                : result.StringResult;

            if (string.Equals(mode, DoubleRowKeyword, StringComparison.OrdinalIgnoreCase))
            {
                CreateDoubleRow(document);
            }
            else if (string.Equals(mode, CountKeyword, StringComparison.OrdinalIgnoreCase))
            {
                CountParkingBays(document);
            }
            else if (string.Equals(mode, NumberKeyword, StringComparison.OrdinalIgnoreCase))
            {
                NumberParkingBays(document);
            }
            else
            {
                CreateSingleRow(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PKROW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingRow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                CreateSingleRow(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PKDOUBLE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingDoubleRow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                CreateDoubleRow(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKCOUNT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ParkingCount()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                CountParkingBays(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKNUMBER",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ParkingNumber()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                NumberParkingBays(document);
            }
        }

        private static void CreateSingleRow(Document document)
        {
            Editor editor = document.Editor;
            BaselineInfo baseline = PromptForBaseline(document);
            if (baseline == null)
            {
                return;
            }

            ParkingParameters parameters = PromptForParkingParameters(editor, includeAisle: false);
            if (parameters == null)
            {
                return;
            }

            int bayCount = CalculateBayCount(baseline.Length, parameters.BayWidth);
            if (bayCount < 1)
            {
                editor.WriteMessage(
                    "\nCE_PKROW cancelled. The baseline is shorter than one entered bay width.");
                return;
            }

            double usedLength = bayCount * parameters.BayWidth;
            editor.WriteMessage(
                "\nCE_PKROW preview: Bays={0}; baseline length={1:N3}; used length={2:N3}; " +
                "unused remainder={3:N3}; width={4:N3}; depth={5:N3}; angle={6:N3} degrees; side={7}.",
                bayCount,
                baseline.Length,
                usedLength,
                Math.Max(0.0, baseline.Length - usedLength),
                parameters.BayWidth,
                parameters.BayDepth,
                parameters.AngleDegrees,
                parameters.Side);

            if (!ConfirmCreation(editor, "Create this parking row"))
            {
                editor.WriteMessage("\nCE_PKROW cancelled. No geometry was created.");
                return;
            }

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = OpenCurrentSpace(
                        document.Database,
                        transaction);

                    Vector3d direction = baseline.Direction;
                    double sideSign = string.Equals(
                        parameters.Side,
                        "Left",
                        StringComparison.OrdinalIgnoreCase)
                        ? 1.0
                        : -1.0;
                    Vector3d dividerDirection = direction.RotateBy(
                        sideSign * DegreesToRadians(parameters.AngleDegrees),
                        Vector3d.ZAxis);

                    Point3d firstBack = Point3d.Origin;
                    Point3d lastBack = Point3d.Origin;
                    int createdLines = 0;

                    for (int index = 0; index <= bayCount; index++)
                    {
                        Point3d front = baseline.Start +
                            (direction * (index * parameters.BayWidth));
                        Point3d back = front +
                            (dividerDirection * parameters.BayDepth);

                        AppendLine(
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            front,
                            back);
                        createdLines++;

                        if (index == 0)
                        {
                            firstBack = back;
                        }

                        if (index == bayCount)
                        {
                            lastBack = back;
                        }
                    }

                    AppendLine(
                        currentSpace,
                        transaction,
                        baseline.LayerId,
                        firstBack,
                        lastBack);
                    createdLines++;

                    transaction.Commit();
                    editor.WriteMessage(
                        "\nCE_PKROW complete. Bays={0}; geometry lines created={1}.",
                        bayCount,
                        createdLines);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKROW cancelled. No parking geometry was committed. {0}",
                    exception.Message);
            }
        }

        private static void CreateDoubleRow(Document document)
        {
            Editor editor = document.Editor;
            BaselineInfo baseline = PromptForBaseline(document);
            if (baseline == null)
            {
                return;
            }

            ParkingParameters parameters = PromptForParkingParameters(editor, includeAisle: true);
            if (parameters == null)
            {
                return;
            }

            int baysPerRow = CalculateBayCount(baseline.Length, parameters.BayWidth);
            if (baysPerRow < 1)
            {
                editor.WriteMessage(
                    "\nCE_PKDOUBLE cancelled. The baseline is shorter than one entered bay width.");
                return;
            }

            double usedLength = baysPerRow * parameters.BayWidth;
            editor.WriteMessage(
                "\nCE_PKDOUBLE preview: Bays per row={0}; total bays={1}; used length={2:N3}; " +
                "bay width={3:N3}; bay depth={4:N3}; aisle={5:N3}; angle={6:N3} degrees.",
                baysPerRow,
                baysPerRow * 2,
                usedLength,
                parameters.BayWidth,
                parameters.BayDepth,
                parameters.AisleWidth,
                parameters.AngleDegrees);

            if (!ConfirmCreation(editor, "Create these opposing parking rows"))
            {
                editor.WriteMessage("\nCE_PKDOUBLE cancelled. No geometry was created.");
                return;
            }

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = OpenCurrentSpace(
                        document.Database,
                        transaction);
                    Vector3d direction = baseline.Direction;
                    Vector3d leftNormal = Vector3d.ZAxis.CrossProduct(direction).GetNormal();
                    Vector3d leftDivider = direction.RotateBy(
                        DegreesToRadians(parameters.AngleDegrees),
                        Vector3d.ZAxis);
                    Vector3d rightDivider = direction.RotateBy(
                        -DegreesToRadians(parameters.AngleDegrees),
                        Vector3d.ZAxis);
                    Vector3d halfAisleOffset = leftNormal * (parameters.AisleWidth / 2.0);

                    Point3d leftInnerStart = baseline.Start + halfAisleOffset;
                    Point3d rightInnerStart = baseline.Start - halfAisleOffset;
                    Point3d leftInnerEnd = leftInnerStart + (direction * usedLength);
                    Point3d rightInnerEnd = rightInnerStart + (direction * usedLength);
                    Point3d leftBackStart = Point3d.Origin;
                    Point3d leftBackEnd = Point3d.Origin;
                    Point3d rightBackStart = Point3d.Origin;
                    Point3d rightBackEnd = Point3d.Origin;
                    int createdLines = 0;

                    AppendLine(
                        currentSpace,
                        transaction,
                        baseline.LayerId,
                        leftInnerStart,
                        leftInnerEnd);
                    AppendLine(
                        currentSpace,
                        transaction,
                        baseline.LayerId,
                        rightInnerStart,
                        rightInnerEnd);
                    createdLines += 2;

                    for (int index = 0; index <= baysPerRow; index++)
                    {
                        double station = index * parameters.BayWidth;
                        Point3d leftFront = leftInnerStart + (direction * station);
                        Point3d rightFront = rightInnerStart + (direction * station);
                        Point3d leftBack = leftFront +
                            (leftDivider * parameters.BayDepth);
                        Point3d rightBack = rightFront +
                            (rightDivider * parameters.BayDepth);

                        AppendLine(
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            leftFront,
                            leftBack);
                        AppendLine(
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            rightFront,
                            rightBack);
                        createdLines += 2;

                        if (index == 0)
                        {
                            leftBackStart = leftBack;
                            rightBackStart = rightBack;
                        }

                        if (index == baysPerRow)
                        {
                            leftBackEnd = leftBack;
                            rightBackEnd = rightBack;
                        }
                    }

                    AppendLine(
                        currentSpace,
                        transaction,
                        baseline.LayerId,
                        leftBackStart,
                        leftBackEnd);
                    AppendLine(
                        currentSpace,
                        transaction,
                        baseline.LayerId,
                        rightBackStart,
                        rightBackEnd);
                    createdLines += 2;

                    transaction.Commit();
                    editor.WriteMessage(
                        "\nCE_PKDOUBLE complete. Bays per row={0}; total bays={1}; geometry lines created={2}.",
                        baysPerRow,
                        baysPerRow * 2,
                        createdLines);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKDOUBLE cancelled. No parking geometry was committed. {0}",
                    exception.Message);
            }
        }

        private static void CountParkingBays(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        skipped++;
                        continue;
                    }

                    Autodesk.AutoCAD.DatabaseServices.DBObject databaseObject = transaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false);
                    string groupName = null;

                    var blockReference = databaseObject as BlockReference;
                    if (blockReference != null)
                    {
                        groupName = "Block: " + GetBlockName(transaction, blockReference);
                    }
                    else
                    {
                        var polyline = databaseObject as Polyline;
                        if (polyline != null && polyline.Closed)
                        {
                            groupName = "Closed polyline layer: " + polyline.Layer;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        skipped++;
                        continue;
                    }

                    total++;
                    int existing;
                    groups.TryGetValue(groupName, out existing);
                    groups[groupName] = existing + 1;
                }
            }

            foreach (KeyValuePair<string, int> group in groups)
            {
                editor.WriteMessage("\n  {0}: {1}", group.Key, group.Value);
            }

            editor.WriteMessage(
                "\nCE_PKCOUNT complete. Parking bays counted={0}; skipped={1}; groups={2}.",
                total,
                skipped,
                groups.Count);
        }

        private static void NumberParkingBays(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines to number: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var prefixOptions = new PromptStringOptions("\nEnter bay number prefix <P>: ")
            {
                AllowSpaces = false,
                DefaultValue = "P",
                UseDefaultValue = true
            };
            PromptResult prefixResult = editor.GetString(prefixOptions);
            if (prefixResult.Status != PromptStatus.OK)
            {
                return;
            }

            var startOptions = new PromptIntegerOptions("\nEnter starting number <1>: ")
            {
                AllowNone = true,
                DefaultValue = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult startResult = editor.GetInteger(startOptions);
            if (startResult.Status != PromptStatus.OK)
            {
                return;
            }

            var incrementOptions = new PromptIntegerOptions("\nEnter numbering increment <1>: ")
            {
                AllowNone = true,
                DefaultValue = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult incrementResult = editor.GetInteger(incrementOptions);
            if (incrementResult.Status != PromptStatus.OK)
            {
                return;
            }

            if (incrementResult.Value == 0)
            {
                editor.WriteMessage("\nCE_PKNUMBER cancelled. Increment cannot be zero.");
                return;
            }

            double defaultHeight = GetTextHeight(document.Database);
            var heightOptions = new PromptDoubleOptions(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "\nEnter bay number text height <{0:N3}>: ",
                    defaultHeight))
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultHeight,
                UseDefaultValue = true
            };
            PromptDoubleResult heightResult = editor.GetDouble(heightOptions);
            if (heightResult.Status != PromptStatus.OK)
            {
                return;
            }

            NumberPreview preview = BuildNumberPreview(document.Database, selection);
            if (preview.Accepted == 0)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBER preview: no editable parking bay blocks or closed polylines found. Skipped={0}.",
                    preview.Skipped);
                return;
            }

            editor.WriteMessage(
                "\nCE_PKNUMBER preview: labels={0}; skipped={1}; prefix={2}; start={3}; increment={4}. " +
                "Labels follow the selection-set order.",
                preview.Accepted,
                preview.Skipped,
                prefixResult.StringResult,
                startResult.Value,
                incrementResult.Value);

            if (!ConfirmCreation(editor, "Create these parking bay numbers"))
            {
                editor.WriteMessage("\nCE_PKNUMBER cancelled. No labels were created.");
                return;
            }

            int placed = 0;
            int skipped = 0;
            int number = startResult.Value;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = OpenCurrentSpace(
                        document.Database,
                        transaction);

                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        Entity entity = OpenNumberableEntity(
                            transaction,
                            selectedObject == null ? ObjectId.Null : selectedObject.ObjectId);
                        if (entity == null || IsLayerLocked(transaction, entity.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        Point3d center;
                        if (!TryGetCenter(entity, out center))
                        {
                            skipped++;
                            continue;
                        }

                        var text = new MText();
                        text.SetDatabaseDefaults(document.Database);
                        text.LayerId = entity.LayerId;
                        text.Location = center;
                        text.Attachment = AttachmentPoint.MiddleCenter;
                        text.TextHeight = heightResult.Value;
                        text.Contents = prefixResult.StringResult +
                            number.ToString(CultureInfo.InvariantCulture);

                        currentSpace.AppendEntity(text);
                        transaction.AddNewlyCreatedDBObject(text, true);
                        placed++;
                        number += incrementResult.Value;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_PKNUMBER complete. Labels placed={0}; skipped={1}.",
                    placed,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBER cancelled. No numbering labels were committed. {0}",
                    exception.Message);
            }
        }

        private static BaselineInfo PromptForBaseline(Document document)
        {
            Editor editor = document.Editor;
            var options = new PromptEntityOptions(
                "\nSelect a straight line or pick a straight polyline segment: ");
            options.SetRejectMessage("\nSelect an AutoCAD Line or 2D Polyline.");
            options.AddAllowedClass(typeof(Line), false);
            options.AddAllowedClass(typeof(Polyline), false);

            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity == null)
                {
                    editor.WriteMessage("\nThe selected object could not be opened.");
                    return null;
                }

                if (IsLayerLocked(transaction, entity.LayerId))
                {
                    editor.WriteMessage(
                        "\nCE Parking Tools cancelled. The selected baseline is on a locked layer.");
                    return null;
                }

                Point3d start;
                Point3d end;
                var line = entity as Line;
                if (line != null)
                {
                    start = line.StartPoint;
                    end = line.EndPoint;
                }
                else
                {
                    var polyline = entity as Polyline;
                    if (polyline == null || polyline.NumberOfVertices < 2)
                    {
                        editor.WriteMessage("\nThe selected polyline has no usable segment.");
                        return null;
                    }

                    Point3d closest = polyline.GetClosestPointTo(result.PickedPoint, false);
                    double parameter = polyline.GetParameterAtPoint(closest);
                    int segmentIndex = (int)Math.Floor(parameter);
                    int maximumSegmentIndex = polyline.Closed
                        ? polyline.NumberOfVertices - 1
                        : polyline.NumberOfVertices - 2;
                    segmentIndex = Math.Max(0, Math.Min(segmentIndex, maximumSegmentIndex));

                    if (polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                    {
                        editor.WriteMessage(
                            "\nCE Parking Tools currently supports straight polyline segments only.");
                        return null;
                    }

                    start = polyline.GetPoint3dAt(segmentIndex);
                    int endVertex = segmentIndex + 1;
                    if (endVertex >= polyline.NumberOfVertices)
                    {
                        endVertex = 0;
                    }

                    end = polyline.GetPoint3dAt(endVertex);
                }

                double zDifference = Math.Abs(end.Z - start.Z);
                Vector3d planVector = new Vector3d(
                    end.X - start.X,
                    end.Y - start.Y,
                    0.0);
                if (planVector.Length <= GeometryTolerance)
                {
                    editor.WriteMessage("\nThe selected baseline has no usable plan length.");
                    return null;
                }

                if (zDifference > GeometryTolerance)
                {
                    editor.WriteMessage(
                        "\nCE Parking Tools currently requires a horizontal plan baseline.");
                    return null;
                }

                return new BaselineInfo(
                    start,
                    new Point3d(end.X, end.Y, start.Z),
                    planVector.GetNormal(),
                    planVector.Length,
                    entity.LayerId);
            }
        }

        private static ParkingParameters PromptForParkingParameters(
            Editor editor,
            bool includeAisle)
        {
            PromptDoubleResult widthResult = PromptPositiveDouble(
                editor,
                "\nEnter bay width <2.500>: ",
                2.5);
            if (widthResult.Status != PromptStatus.OK)
            {
                return null;
            }

            PromptDoubleResult depthResult = PromptPositiveDouble(
                editor,
                "\nEnter bay depth <5.000>: ",
                5.0);
            if (depthResult.Status != PromptStatus.OK)
            {
                return null;
            }

            PromptDoubleResult angleResult = PromptPositiveDouble(
                editor,
                "\nEnter divider angle from baseline in degrees <90>: ",
                90.0);
            if (angleResult.Status != PromptStatus.OK)
            {
                return null;
            }

            if (angleResult.Value >= 180.0)
            {
                editor.WriteMessage(
                    "\nParking divider angle must be greater than 0 and less than 180 degrees.");
                return null;
            }

            double aisleWidth = 0.0;
            string side = "Left";

            if (includeAisle)
            {
                PromptDoubleResult aisleResult = PromptPositiveDouble(
                    editor,
                    "\nEnter aisle width <6.000>: ",
                    6.0);
                if (aisleResult.Status != PromptStatus.OK)
                {
                    return null;
                }

                aisleWidth = aisleResult.Value;
            }
            else
            {
                var sideOptions = new PromptKeywordOptions(
                    "\nCreate parking bays on which side [Left/Right] <Left>: ")
                {
                    AllowNone = true
                };
                sideOptions.Keywords.Add("Left");
                sideOptions.Keywords.Add("Right");
                PromptResult sideResult = editor.GetKeywords(sideOptions);
                if (sideResult.Status == PromptStatus.Cancel)
                {
                    return null;
                }

                if (sideResult.Status == PromptStatus.OK)
                {
                    side = sideResult.StringResult;
                }
            }

            return new ParkingParameters(
                widthResult.Value,
                depthResult.Value,
                angleResult.Value,
                aisleWidth,
                side);
        }

        private static PromptDoubleResult PromptPositiveDouble(
            Editor editor,
            string message,
            double defaultValue)
        {
            return editor.GetDouble(
                new PromptDoubleOptions(message)
                {
                    AllowNone = true,
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = defaultValue,
                    UseDefaultValue = true
                });
        }

        private static bool ConfirmCreation(Editor editor, string message)
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
                string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int CalculateBayCount(double baselineLength, double bayWidth)
        {
            return (int)Math.Floor((baselineLength + GeometryTolerance) / bayWidth);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void AppendLine(
            BlockTableRecord currentSpace,
            Transaction transaction,
            ObjectId layerId,
            Point3d start,
            Point3d end)
        {
            var line = new Line(start, end);
            line.SetDatabaseDefaults();
            line.LayerId = layerId;
            currentSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
        }

        private static BlockTableRecord OpenCurrentSpace(
            Database database,
            Transaction transaction)
        {
            return (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite,
                false);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static string GetBlockName(
            Transaction transaction,
            BlockReference blockReference)
        {
            ObjectId definitionId = blockReference.IsDynamicBlock
                ? blockReference.DynamicBlockTableRecord
                : blockReference.BlockTableRecord;
            var definition = transaction.GetObject(
                definitionId,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            return definition == null ? "<Unknown>" : definition.Name;
        }

        private static NumberPreview BuildNumberPreview(
            Database database,
            PromptSelectionResult selection)
        {
            var preview = new NumberPreview();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    Entity entity = OpenNumberableEntity(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId);
                    Point3d center;
                    if (entity == null ||
                        IsLayerLocked(transaction, entity.LayerId) ||
                        !TryGetCenter(entity, out center))
                    {
                        preview.Skipped++;
                    }
                    else
                    {
                        preview.Accepted++;
                    }
                }
            }

            return preview;
        }

        private static Entity OpenNumberableEntity(
            Transaction transaction,
            ObjectId objectId)
        {
            if (objectId.IsNull || objectId.IsErased)
            {
                return null;
            }

            Entity entity = transaction.GetObject(
                objectId,
                OpenMode.ForRead,
                false) as Entity;
            if (entity is BlockReference)
            {
                return entity;
            }

            var polyline = entity as Polyline;
            return polyline != null && polyline.Closed ? polyline : null;
        }

        private static bool TryGetCenter(Entity entity, out Point3d center)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                center = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0);
                return true;
            }
            catch
            {
                center = Point3d.Origin;
                return false;
            }
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            if (layerId.IsNull)
            {
                return false;
            }

            var layer = transaction.GetObject(
                layerId,
                OpenMode.ForRead,
                false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
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

        private sealed class BaselineInfo
        {
            public BaselineInfo(
                Point3d start,
                Point3d end,
                Vector3d direction,
                double length,
                ObjectId layerId)
            {
                Start = start;
                End = end;
                Direction = direction;
                Length = length;
                LayerId = layerId;
            }

            public Point3d Start { get; }

            public Point3d End { get; }

            public Vector3d Direction { get; }

            public double Length { get; }

            public ObjectId LayerId { get; }
        }

        private sealed class ParkingParameters
        {
            public ParkingParameters(
                double bayWidth,
                double bayDepth,
                double angleDegrees,
                double aisleWidth,
                string side)
            {
                BayWidth = bayWidth;
                BayDepth = bayDepth;
                AngleDegrees = angleDegrees;
                AisleWidth = aisleWidth;
                Side = side;
            }

            public double BayWidth { get; }

            public double BayDepth { get; }

            public double AngleDegrees { get; }

            public double AisleWidth { get; }

            public string Side { get; }
        }

        private sealed class NumberPreview
        {
            public int Accepted { get; set; }

            public int Skipped { get; set; }
        }
    }
}
