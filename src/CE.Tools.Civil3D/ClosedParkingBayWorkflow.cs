using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates every generated parking bay as an individual closed polyline.
    /// The output can therefore be selected immediately by CE_PKREPORTUI,
    /// CE_PKCOUNTX and CE_PKNUMBER2 without tracing or converting linework.
    /// </summary>
    internal static class ClosedParkingBayWorkflow
    {
        private const double GeometryTolerance = 0.000001;

        public static void CreateSingleRow(Document document)
        {
            if (document == null)
                return;

            Editor editor = document.Editor;
            ParkingBaseline baseline = PromptForBaseline(document);
            if (baseline == null)
                return;

            ParkingLayout layout = PromptForLayout(editor, false);
            if (layout == null)
                return;

            int bayCount = CalculateBayCount(baseline.Length, layout.BayWidth);
            if (bayCount < 1)
            {
                editor.WriteMessage(
                    "\nCE_PKROW cancelled. The baseline is shorter than one entered bay width.");
                return;
            }

            double usedLength = bayCount * layout.BayWidth;
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Output", "One selectable block per parking bay"),
                Pair("Bays", bayCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Bay width", Format(layout.BayWidth)),
                Pair("Bay depth", Format(layout.BayDepth)),
                Pair("Divider angle", Format(layout.AngleDegrees) + " degrees"),
                Pair("Side", layout.Side),
                Pair("Baseline length", Format(baseline.Length)),
                Pair("Used length", Format(usedLength)),
                Pair("Unused remainder", Format(Math.Max(0.0, baseline.Length - usedLength)))
            };

            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Closed Parking Bay Row",
                    "Each bay will be a separate selectable block that can be counted, reported and numbered immediately.",
                    rows,
                    "Create Bays"))
            {
                editor.WriteMessage("\nCE_PKROW cancelled. No parking geometry was created.");
                return;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = OpenCurrentSpace(
                        document.Database,
                        transaction);
                    Vector3d direction = baseline.Direction;
                    double sideSign = string.Equals(
                        layout.Side,
                        "Left",
                        StringComparison.OrdinalIgnoreCase)
                        ? 1.0
                        : -1.0;
                    ObjectId bayDefinitionId = CreateBayBlockDefinition(
                        document.Database,
                        transaction,
                        layout.BayWidth,
                        layout.BayDepth,
                        sideSign * layout.AngleDegrees);
                    double rotation = Math.Atan2(direction.Y, direction.X);

                    for (int index = 0; index < bayCount; index++)
                    {
                        Point3d frontStart = baseline.Start +
                            (direction * (index * layout.BayWidth));
                        AppendBayBlock(
                            document.Database,
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            bayDefinitionId,
                            frontStart,
                            rotation);
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_PKROW complete. Parking bay blocks created={0}. " +
                    "Use CE_PKCOUNTX, CE_PKREPORTUI or CE_PKNUMBER2 on the new bays.",
                    bayCount);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKROW cancelled. No closed parking bays were committed. {0}",
                    exception.Message);
            }
        }

        public static void CreateDoubleRow(Document document)
        {
            if (document == null)
                return;

            Editor editor = document.Editor;
            ParkingBaseline baseline = PromptForBaseline(document);
            if (baseline == null)
                return;

            ParkingLayout layout = PromptForLayout(editor, true);
            if (layout == null)
                return;

            int baysPerRow = CalculateBayCount(baseline.Length, layout.BayWidth);
            if (baysPerRow < 1)
            {
                editor.WriteMessage(
                    "\nCE_PKDOUBLE cancelled. The baseline is shorter than one entered bay width.");
                return;
            }

            int totalBays = baysPerRow * 2;
            double usedLength = baysPerRow * layout.BayWidth;
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Output", "One selectable block per parking bay"),
                Pair("Bays per row", baysPerRow.ToString(CultureInfo.InvariantCulture)),
                Pair("Total bays", totalBays.ToString(CultureInfo.InvariantCulture)),
                Pair("Bay width", Format(layout.BayWidth)),
                Pair("Bay depth", Format(layout.BayDepth)),
                Pair("Aisle width", Format(layout.AisleWidth)),
                Pair("Divider angle", Format(layout.AngleDegrees) + " degrees"),
                Pair("Used length", Format(usedLength))
            };

            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Closed Double Parking Row",
                    "Each bay on both sides of the aisle will be a separate selectable block suitable for immediate reporting, counting and numbering.",
                    rows,
                    "Create Bays"))
            {
                editor.WriteMessage("\nCE_PKDOUBLE cancelled. No parking geometry was created.");
                return;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = OpenCurrentSpace(
                        document.Database,
                        transaction);
                    Vector3d direction = baseline.Direction;
                    Vector3d leftNormal = Vector3d.ZAxis.CrossProduct(direction).GetNormal();
                    Vector3d halfAisleOffset = leftNormal * (layout.AisleWidth / 2.0);
                    Point3d leftInnerStart = baseline.Start + halfAisleOffset;
                    Point3d rightInnerStart = baseline.Start - halfAisleOffset;
                    ObjectId leftDefinitionId = CreateBayBlockDefinition(
                        document.Database,
                        transaction,
                        layout.BayWidth,
                        layout.BayDepth,
                        layout.AngleDegrees);
                    ObjectId rightDefinitionId = CreateBayBlockDefinition(
                        document.Database,
                        transaction,
                        layout.BayWidth,
                        layout.BayDepth,
                        -layout.AngleDegrees);
                    double rotation = Math.Atan2(direction.Y, direction.X);

                    for (int index = 0; index < baysPerRow; index++)
                    {
                        double startStation = index * layout.BayWidth;
                        Point3d leftFrontStart = leftInnerStart +
                            (direction * startStation);
                        AppendBayBlock(
                            document.Database,
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            leftDefinitionId,
                            leftFrontStart,
                            rotation);

                        Point3d rightFrontStart = rightInnerStart +
                            (direction * startStation);
                        AppendBayBlock(
                            document.Database,
                            currentSpace,
                            transaction,
                            baseline.LayerId,
                            rightDefinitionId,
                            rightFrontStart,
                            rotation);
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_PKDOUBLE complete. Parking bay blocks created={0}. " +
                    "Use CE_PKCOUNTX, CE_PKREPORTUI or CE_PKNUMBER2 on the new bays.",
                    totalBays);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKDOUBLE cancelled. No closed parking bays were committed. {0}",
                    exception.Message);
            }
        }

        private static ParkingBaseline PromptForBaseline(Document document)
        {
            Editor editor = document.Editor;
            var options = new PromptEntityOptions(
                "\nSelect a straight line or pick a straight polyline segment: ");
            options.SetRejectMessage("\nSelect an AutoCAD Line or 2D Polyline.");
            options.AddAllowedClass(typeof(Line), false);
            options.AddAllowedClass(typeof(Polyline), false);

            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
                return null;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity == null)
                {
                    editor.WriteMessage("\nThe selected baseline could not be opened.");
                    return null;
                }

                LayerTableRecord layer = transaction.GetObject(
                    entity.LayerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer != null && layer.IsLocked)
                {
                    editor.WriteMessage(
                        "\nCE Parking Tools cancelled. The selected baseline is on a locked layer.");
                    return null;
                }

                Point3d start;
                Point3d end;
                Line line = entity as Line;
                if (line != null)
                {
                    start = line.StartPoint;
                    end = line.EndPoint;
                }
                else
                {
                    Polyline polyline = entity as Polyline;
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
                    segmentIndex = Math.Max(
                        0,
                        Math.Min(segmentIndex, maximumSegmentIndex));

                    if (polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                    {
                        editor.WriteMessage(
                            "\nCE Parking Tools currently supports straight polyline segments only.");
                        return null;
                    }

                    start = polyline.GetPoint3dAt(segmentIndex);
                    int endVertex = segmentIndex + 1;
                    if (endVertex >= polyline.NumberOfVertices)
                        endVertex = 0;
                    end = polyline.GetPoint3dAt(endVertex);
                }

                if (Math.Abs(end.Z - start.Z) > GeometryTolerance)
                {
                    editor.WriteMessage(
                        "\nCE Parking Tools currently requires a horizontal plan baseline.");
                    return null;
                }

                Vector3d planVector = new Vector3d(
                    end.X - start.X,
                    end.Y - start.Y,
                    0.0);
                if (planVector.Length <= GeometryTolerance)
                {
                    editor.WriteMessage("\nThe selected baseline has no usable plan length.");
                    return null;
                }

                return new ParkingBaseline(
                    start,
                    planVector.GetNormal(),
                    planVector.Length,
                    entity.LayerId);
            }
        }

        private static ParkingLayout PromptForLayout(
            Editor editor,
            bool includeAisle)
        {
            PromptDoubleResult width = PromptPositiveDouble(
                editor,
                "\nEnter bay width <2.500>: ",
                2.5);
            if (width.Status != PromptStatus.OK)
                return null;

            PromptDoubleResult depth = PromptPositiveDouble(
                editor,
                "\nEnter bay depth <5.000>: ",
                5.0);
            if (depth.Status != PromptStatus.OK)
                return null;

            PromptDoubleResult angle = PromptPositiveDouble(
                editor,
                "\nEnter divider angle from baseline in degrees <90>: ",
                90.0);
            if (angle.Status != PromptStatus.OK)
                return null;
            if (angle.Value >= 180.0)
            {
                editor.WriteMessage(
                    "\nParking divider angle must be greater than 0 and less than 180 degrees.");
                return null;
            }

            double aisleWidth = 0.0;
            string side = "Left";
            if (includeAisle)
            {
                PromptDoubleResult aisle = PromptPositiveDouble(
                    editor,
                    "\nEnter aisle width <6.000>: ",
                    6.0);
                if (aisle.Status != PromptStatus.OK)
                    return null;
                aisleWidth = aisle.Value;
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
                    return null;
                if (sideResult.Status == PromptStatus.OK)
                    side = sideResult.StringResult;
            }

            return new ParkingLayout(
                width.Value,
                depth.Value,
                angle.Value,
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

        private static ObjectId CreateBayBlockDefinition(
            Database database,
            Transaction transaction,
            double width,
            double depth,
            double angleDegrees)
        {
            BlockTable blockTable = (BlockTable)transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForWrite,
                false);
            var definition = new BlockTableRecord
            {
                Name = "CE_PARKING_BAY_" + Guid.NewGuid().ToString("N")
            };
            ObjectId definitionId = blockTable.Add(definition);
            transaction.AddNewlyCreatedDBObject(definition, true);

            double angle = DegreesToRadians(angleDegrees);
            Vector2d depthVector = new Vector2d(
                Math.Cos(angle) * depth,
                Math.Sin(angle) * depth);
            var bay = new Polyline(4);
            bay.SetDatabaseDefaults(database);
            bay.Layer = "0";
            bay.AddVertexAt(0, Point2d.Origin, 0.0, 0.0, 0.0);
            bay.AddVertexAt(1, new Point2d(width, 0.0), 0.0, 0.0, 0.0);
            bay.AddVertexAt(
                2,
                new Point2d(width + depthVector.X, depthVector.Y),
                0.0,
                0.0,
                0.0);
            bay.AddVertexAt(3, new Point2d(depthVector.X, depthVector.Y), 0.0, 0.0, 0.0);
            bay.Closed = true;
            definition.AppendEntity(bay);
            transaction.AddNewlyCreatedDBObject(bay, true);
            return definitionId;
        }

        private static void AppendBayBlock(
            Database database,
            BlockTableRecord currentSpace,
            Transaction transaction,
            ObjectId layerId,
            ObjectId definitionId,
            Point3d insertionPoint,
            double rotation)
        {
            var reference = new BlockReference(insertionPoint, definitionId);
            reference.SetDatabaseDefaults(database);
            reference.LayerId = layerId;
            reference.Rotation = rotation;
            currentSpace.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
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

        private static int CalculateBayCount(
            double baselineLength,
            double bayWidth)
        {
            return (int)Math.Floor(
                (baselineLength + GeometryTolerance) / bayWidth);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static string Format(double value)
        {
            return value.ToString("N3", CultureInfo.CurrentCulture);
        }

        private static KeyValuePair<string, string> Pair(
            string key,
            string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private sealed class ParkingBaseline
        {
            public ParkingBaseline(
                Point3d start,
                Vector3d direction,
                double length,
                ObjectId layerId)
            {
                Start = start;
                Direction = direction;
                Length = length;
                LayerId = layerId;
            }

            public Point3d Start { get; private set; }
            public Vector3d Direction { get; private set; }
            public double Length { get; private set; }
            public ObjectId LayerId { get; private set; }
        }

        private sealed class ParkingLayout
        {
            public ParkingLayout(
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

            public double BayWidth { get; private set; }
            public double BayDepth { get; private set; }
            public double AngleDegrees { get; private set; }
            public double AisleWidth { get; private set; }
            public string Side { get; private set; }
        }
    }
}
