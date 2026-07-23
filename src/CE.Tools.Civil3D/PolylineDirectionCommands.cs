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

            var options = new PromptKeywordOptions(
                "\nPolyline direction arrows [Add/Clear] <Add>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Add");
            options.Keywords.Add("Clear");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Add"
                : result.StringResult;

            if (string.Equals(mode, "Clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearArrows(document);
            }
            else
            {
                AddArrows(document);
            }
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
                                    curve.Handle.ToString()));

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
            double textSize = database.Textsize;
            if (!(textSize > GeometryTolerance) ||
                double.IsNaN(textSize) ||
                double.IsInfinity(textSize))
            {
                textSize = 2.5;
            }

            return textSize * 2.0;
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
