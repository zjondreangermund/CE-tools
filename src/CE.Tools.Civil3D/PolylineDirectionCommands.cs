using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AutoCADSolid = Autodesk.AutoCAD.DatabaseServices.Solid;

[assembly: CommandClass(typeof(CETools.Civil3D.PolylineDirectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Adds removable plan-direction arrows to ordinary AutoCAD polylines.
    /// Existing CE arrows linked to selected polylines are replaced so repeated
    /// use does not create duplicate annotations.
    /// </summary>
    public sealed class PolylineDirectionCommands
    {
        private const string RegAppName = "CE_TOOLS_PLDIR";
        private const double GeometryTolerance = 1e-9;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLDIR",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void PolylineDirectionMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Polyline Direction",
                "Create and maintain dynamic direction arrows without a command-line option menu.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Add direction arrows", "CE_PLDIRADD", "Add linked arrows to selected polylines.", "01 Arrows"),
                    new DisciplineWorkflowAction("Refresh arrows", "CE_PLDIRREFRESH", "Rebuild linked arrows after geometry changes.", "01 Arrows"),
                    new DisciplineWorkflowAction("Reverse polylines", "CE_PLDIRREVERSE", "Reverse selected polylines and refresh their arrows.", "02 Geometry"),
                    new DisciplineWorkflowAction("Clear arrows", "CE_PLDIRCLEAR", "Remove CE Tools direction arrows.", "03 Cleanup")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLDIRADD",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void AddDirectionArrows()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                AddArrows(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLDIRCLEAR",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ClearDirectionArrows()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ClearArrows(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLDIRREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshDirectionArrows()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                RefreshArrows(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PLDIRREVERSE",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ReverseDirectionPolylines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReversePolylines(document);
            }
        }

        private static void AddArrows(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetPolylineSelection(
                editor,
                "\nSelect ordinary polylines to show their direction: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                return;
            }

            double defaultSize = GetDefaultArrowSize(document.Database);
            var sizeOptions = new PromptDoubleOptions(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "\nArrow length <{0:N3}>: ",
                    defaultSize))
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultSize,
                UseDefaultValue = true
            };
            PromptDoubleResult sizeResult = editor.GetDouble(sizeOptions);
            if (sizeResult.Status != PromptStatus.OK)
            {
                return;
            }

            var spacingOptions = new PromptDoubleOptions(
                "\nArrow spacing; enter 0 for one arrow at each polyline midpoint <0>: ")
            {
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = 0.0,
                UseDefaultValue = true
            };
            PromptDoubleResult spacingResult = editor.GetDouble(spacingOptions);
            if (spacingResult.Status != PromptStatus.OK)
            {
                return;
            }

            if (!Confirm(
                    editor,
                    "Replace existing CE direction arrows and add the new arrows"))
            {
                editor.WriteMessage("\nCE_PLDIR cancelled. No arrows were changed.");
                return;
            }

            Database database = document.Database;
            int polylinesChanged = 0;
            int arrowsCreated = 0;
            int skipped = 0;

            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace =
                        (BlockTableRecord)transaction.GetObject(
                            database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false);

                    EnsureRegApp(database, transaction);

                    var selectedCurves = new List<Curve>();
                    var sourceHandles = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (ObjectId objectId in selection.Value.GetObjectIds())
                    {
                        Curve curve = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false) as Curve;

                        if (!IsSupportedPolyline(curve) ||
                            curve.OwnerId != database.CurrentSpaceId ||
                            IsLayerLocked(curve, transaction))
                        {
                            skipped++;
                            continue;
                        }

                        double length = GetCurveLength(curve);
                        if (!(length > GeometryTolerance))
                        {
                            skipped++;
                            continue;
                        }

                        selectedCurves.Add(curve);
                        sourceHandles.Add(curve.Handle.ToString());
                    }

                    int removed = EraseLinkedArrows(
                        currentSpace,
                        sourceHandles,
                        transaction);

                    foreach (Curve curve in selectedCurves)
                    {
                        double length = GetCurveLength(curve);
                        List<double> distances = BuildArrowDistances(
                            length,
                            spacingResult.Value);

                        int createdForCurve = 0;
                        foreach (double distance in distances)
                        {
                            Point3d point = curve.GetPointAtDist(distance);
                            double parameter = curve.GetParameterAtDistance(distance);
                            Vector3d tangent = curve.GetFirstDerivative(parameter);
                            Vector2d planDirection = new Vector2d(
                                tangent.X,
                                tangent.Y);

                            if (planDirection.Length <= GeometryTolerance)
                            {
                                continue;
                            }

                            planDirection = planDirection.GetNormal();
                            AutoCADSolid arrow = CreateArrow(
                                database,
                                point,
                                planDirection,
                                sizeResult.Value,
                                curve.Layer);

                            arrow.XData = new ResultBuffer(
                                new TypedValue(
                                    (int)DxfCode.ExtendedDataRegAppName,
                                    RegAppName),
                                new TypedValue(
                                    (int)DxfCode.ExtendedDataAsciiString,
                                    curve.Handle.ToString()),
                                new TypedValue(
                                    (int)DxfCode.ExtendedDataReal,
                                    length <= GeometryTolerance ? 0.5 : distance / length),
                                new TypedValue(
                                    (int)DxfCode.ExtendedDataReal,
                                    sizeResult.Value));

                            currentSpace.AppendEntity(arrow);
                            transaction.AddNewlyCreatedDBObject(arrow, true);
                            createdForCurve++;
                            arrowsCreated++;
                        }

                        if (createdForCurve > 0)
                        {
                            polylinesChanged++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }

                    transaction.Commit();

                    editor.WriteMessage(
                        "\nCE_PLDIR complete. Polylines: {0}; arrows added: {1}; old arrows replaced: {2}; skipped: {3}.",
                        polylinesChanged,
                        arrowsCreated,
                        removed,
                        skipped);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PLDIR cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        internal static int RefreshLinkedArrows(Document document)
        {
            if (document == null) return 0;

            Database database = document.Database;
            int refreshed = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace =
                    (BlockTableRecord)transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false);

                foreach (ObjectId objectId in currentSpace)
                {
                    AutoCADSolid arrow = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as AutoCADSolid;
                    if (arrow == null || arrow.XData == null) continue;

                    string sourceHandle;
                    double fraction;
                    double size;
                    if (!TryReadArrowLink(
                        arrow,
                        out sourceHandle,
                        out fraction,
                        out size))
                    {
                        continue;
                    }

                    ObjectId sourceId;
                    try
                    {
                        sourceId = database.GetObjectId(
                            false,
                            new Handle(Convert.ToInt64(sourceHandle, 16)),
                            0);
                    }
                    catch
                    {
                        continue;
                    }

                    Curve curve = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Curve;
                    if (!IsSupportedPolyline(curve)) continue;

                    double length = GetCurveLength(curve);
                    if (length <= GeometryTolerance) continue;

                    if (!(fraction >= 0.0 && fraction <= 1.0))
                    {
                        Point3d centre = AverageArrowPoints(arrow);
                        Point3d closest = curve.GetClosestPointTo(centre, false);
                        fraction = curve.GetDistAtPoint(closest) / length;
                    }
                    if (!(size > GeometryTolerance))
                    {
                        size = arrow.GetPointAt(2).DistanceTo(arrow.GetPointAt(0));
                    }

                    double distance = Math.Max(
                        0.0,
                        Math.Min(length, fraction * length));
                    Point3d point = curve.GetPointAtDist(distance);
                    double parameter = curve.GetParameterAtDistance(distance);
                    Vector3d tangent = curve.GetFirstDerivative(parameter);
                    Vector2d direction = new Vector2d(tangent.X, tangent.Y);
                    if (direction.Length <= GeometryTolerance) continue;
                    direction = direction.GetNormal();

                    AutoCADSolid replacement = CreateArrow(
                        database,
                        point,
                        direction,
                        size,
                        curve.Layer);
                    arrow.UpgradeOpen();
                    for (int index = 0; index < 4; index++)
                    {
                        short vertexIndex = (short)index;
                        arrow.SetPointAt(
                            vertexIndex,
                            replacement.GetPointAt(vertexIndex));
                    }
                    arrow.Layer = curve.Layer;
                    arrow.XData = new ResultBuffer(
                        new TypedValue(
                            (int)DxfCode.ExtendedDataRegAppName,
                            RegAppName),
                        new TypedValue(
                            (int)DxfCode.ExtendedDataAsciiString,
                            sourceHandle),
                        new TypedValue(
                            (int)DxfCode.ExtendedDataReal,
                            fraction),
                        new TypedValue(
                            (int)DxfCode.ExtendedDataReal,
                            size));
                    replacement.Dispose();
                    refreshed++;
                }

                transaction.Commit();
            }

            return refreshed;
        }

        private static void RefreshArrows(Document document)
        {
            try
            {
                int refreshed = RefreshLinkedArrows(document);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PLDIRREFRESH complete. Linked arrows refreshed: {0}.",
                    refreshed);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLDIRREFRESH failed. No partial refresh was committed: {0}",
                    exception.Message);
            }
        }

        private static void ReversePolylines(Document document)
        {
            PromptSelectionResult selection = GetPolylineSelection(
                document.Editor,
                "\nSelect polylines to reverse and refresh: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                return;
            }

            if (!Confirm(
                    document.Editor,
                    "Reverse the selected polylines and their linked direction arrows"))
            {
                document.Editor.WriteMessage(
                    "\nCE_PLDIRREVERSE cancelled. No geometry was changed.");
                return;
            }

            int reversed = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in selection.Value.GetObjectIds())
                {
                    Curve curve = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Curve;
                    if (!IsSupportedPolyline(curve) ||
                        IsLayerLocked(curve, transaction))
                    {
                        continue;
                    }

                    curve.UpgradeOpen();
                    curve.ReverseCurve();
                    reversed++;
                }
                transaction.Commit();
            }

            int refreshed = RefreshLinkedArrows(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PLDIRREVERSE complete. Polylines reversed: {0}; linked arrows refreshed: {1}.",
                reversed,
                refreshed);
        }

        private static bool TryReadArrowLink(
            AutoCADSolid arrow,
            out string sourceHandle,
            out double fraction,
            out double size)
        {
            sourceHandle = null;
            fraction = double.NaN;
            size = 0.0;
            ResultBuffer xdata = arrow.GetXDataForApplication(RegAppName);
            if (xdata == null) return false;

            int realIndex = 0;
            foreach (TypedValue value in xdata)
            {
                if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                    sourceHandle == null)
                {
                    sourceHandle = value.Value as string;
                }
                else if (value.TypeCode == (int)DxfCode.ExtendedDataReal)
                {
                    double number = Convert.ToDouble(
                        value.Value,
                        CultureInfo.InvariantCulture);
                    if (realIndex++ == 0) fraction = number;
                    else size = number;
                }
            }
            return !string.IsNullOrWhiteSpace(sourceHandle);
        }

        private static Point3d AverageArrowPoints(AutoCADSolid arrow)
        {
            Point3d first = arrow.GetPointAt(0);
            Point3d second = arrow.GetPointAt(1);
            Point3d tip = arrow.GetPointAt(2);
            return new Point3d(
                (first.X + second.X + tip.X) / 3.0,
                (first.Y + second.Y + tip.Y) / 3.0,
                (first.Z + second.Z + tip.Z) / 3.0);
        }

        private static void ClearArrows(Document document)
        {
            Editor editor = document.Editor;
            var scopeOptions = new PromptKeywordOptions(
                "\nClear CE direction arrows [SelectedPolylines/All] <SelectedPolylines>: ")
            {
                AllowNone = true
            };
            scopeOptions.Keywords.Add("SelectedPolylines");
            scopeOptions.Keywords.Add("All");

            PromptResult scopeResult = editor.GetKeywords(scopeOptions);
            if (scopeResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            bool clearAll = scopeResult.Status == PromptStatus.OK &&
                            string.Equals(
                                scopeResult.StringResult,
                                "All",
                                StringComparison.OrdinalIgnoreCase);

            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!clearAll)
            {
                PromptSelectionResult selection = GetPolylineSelection(
                    editor,
                    "\nSelect polylines whose CE direction arrows must be removed: ");
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null ||
                    selection.Value.Count == 0)
                {
                    return;
                }

                using (Transaction readTransaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId objectId in selection.Value.GetObjectIds())
                    {
                        Curve curve = readTransaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false) as Curve;
                        if (IsSupportedPolyline(curve))
                        {
                            handles.Add(curve.Handle.ToString());
                        }
                    }
                }

                if (handles.Count == 0)
                {
                    editor.WriteMessage(
                        "\nCE_PLDIRCLEAR: no supported polylines were selected.");
                    return;
                }
            }

            if (!Confirm(
                    editor,
                    clearAll
                        ? "Remove every CE polyline direction arrow in the current space"
                        : "Remove CE direction arrows linked to the selected polylines"))
            {
                editor.WriteMessage(
                    "\nCE_PLDIRCLEAR cancelled. No arrows were removed.");
                return;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace =
                        (BlockTableRecord)transaction.GetObject(
                            document.Database.CurrentSpaceId,
                            OpenMode.ForRead,
                            false);

                    int removed = EraseLinkedArrows(
                        currentSpace,
                        clearAll ? null : handles,
                        transaction);

                    transaction.Commit();
                    editor.WriteMessage(
                        "\nCE_PLDIRCLEAR complete. Direction arrows removed: {0}.",
                        removed);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PLDIRCLEAR cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        private static PromptSelectionResult GetPolylineSelection(
            Editor editor,
            string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK &&
                implied.Value != null &&
                implied.Value.Count > 0)
            {
                return implied;
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            return editor.GetSelection(options);
        }

        private static bool IsSupportedPolyline(Curve curve)
        {
            return curve is Polyline ||
                   curve is Polyline2d ||
                   curve is Polyline3d;
        }

        private static bool IsLayerLocked(
            Entity entity,
            Transaction transaction)
        {
            LayerTableRecord layer = transaction.GetObject(
                entity.LayerId,
                OpenMode.ForRead,
                false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
        }

        private static double GetCurveLength(Curve curve)
        {
            try
            {
                return curve.GetDistanceAtParameter(curve.EndParam) -
                       curve.GetDistanceAtParameter(curve.StartParam);
            }
            catch
            {
                return 0.0;
            }
        }

        private static List<double> BuildArrowDistances(
            double length,
            double spacing)
        {
            var distances = new List<double>();

            if (!(spacing > GeometryTolerance))
            {
                distances.Add(length * 0.5);
                return distances;
            }

            double first = Math.Min(spacing * 0.5, length * 0.5);
            for (double distance = first;
                 distance < length - GeometryTolerance;
                 distance += spacing)
            {
                distances.Add(distance);
            }

            if (distances.Count == 0)
            {
                distances.Add(length * 0.5);
            }

            return distances;
        }

        private static AutoCADSolid CreateArrow(
            Database database,
            Point3d centre,
            Vector2d direction,
            double length,
            string layer)
        {
            Vector2d perpendicular = new Vector2d(-direction.Y, direction.X);
            double halfLength = length * 0.5;
            double halfWidth = length * 0.28;

            Point3d tip = new Point3d(
                centre.X + (direction.X * halfLength),
                centre.Y + (direction.Y * halfLength),
                centre.Z);
            Point3d tail = new Point3d(
                centre.X - (direction.X * halfLength),
                centre.Y - (direction.Y * halfLength),
                centre.Z);
            Point3d left = new Point3d(
                tail.X + (perpendicular.X * halfWidth),
                tail.Y + (perpendicular.Y * halfWidth),
                centre.Z);
            Point3d right = new Point3d(
                tail.X - (perpendicular.X * halfWidth),
                tail.Y - (perpendicular.Y * halfWidth),
                centre.Z);

            var arrow = new AutoCADSolid(left, right, tip, tip);
            arrow.SetDatabaseDefaults(database);
            arrow.Layer = layer;
            return arrow;
        }

        private static int EraseLinkedArrows(
            BlockTableRecord currentSpace,
            ISet<string> sourceHandles,
            Transaction transaction)
        {
            int removed = 0;

            foreach (ObjectId objectId in currentSpace)
            {
                DBObject databaseObject = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false);
                ResultBuffer xdata =
                    databaseObject.GetXDataForApplication(RegAppName);
                if (xdata == null)
                {
                    continue;
                }

                string sourceHandle = null;
                foreach (TypedValue value in xdata)
                {
                    if (value.TypeCode ==
                        (int)DxfCode.ExtendedDataAsciiString)
                    {
                        sourceHandle = value.Value as string;
                        break;
                    }
                }

                if (sourceHandles != null &&
                    (string.IsNullOrWhiteSpace(sourceHandle) ||
                     !sourceHandles.Contains(sourceHandle)))
                {
                    continue;
                }

                databaseObject.UpgradeOpen();
                databaseObject.Erase();
                removed++;
            }

            return removed;
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false);
            if (table.Has(RegAppName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static double GetDefaultArrowSize(Database database)
        {
            return 1.0;
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
    }
}
