using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcCurve = Autodesk.AutoCAD.DatabaseServices.Curve;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineConstructionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Feature-line creation, surface-elevation and elevation-point editing tools.
    /// </summary>
    public sealed class FeatureLineConstructionCommands
    {
        private const string CreateKeyword = "Create";
        private const string SurfaceKeyword = "Surface";
        private const string InsertKeyword = "Insert";
        private const string DeleteKeyword = "Delete";

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLEDIT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineEditMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nFeature Line edit [Create/Surface/Insert/Delete] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(CreateKeyword);
            options.Keywords.Add(SurfaceKeyword);
            options.Keywords.Add(InsertKeyword);
            options.Keywords.Add(DeleteKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? CreateKeyword
                : result.StringResult;

            if (string.Equals(mode, SurfaceKeyword, StringComparison.OrdinalIgnoreCase))
            {
                AssignFromSurface(document);
            }
            else if (string.Equals(mode, InsertKeyword, StringComparison.OrdinalIgnoreCase))
            {
                InsertElevationPoint(document);
            }
            else if (string.Equals(mode, DeleteKeyword, StringComparison.OrdinalIgnoreCase))
            {
                DeleteElevationPoint(document);
            }
            else
            {
                CreateFromObjects(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLCREATE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                CreateFromObjects(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSURFACE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLinesFromSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                AssignFromSurface(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLINSERT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void InsertFeatureLineElevationPoint()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                InsertElevationPoint(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLDELETE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void DeleteFeatureLineElevationPoint()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                DeleteElevationPoint(document);
            }
        }

        private static void CreateFromObjects(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect lines, arcs or polylines to convert to siteless feature lines: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            Database database = document.Database;
            int created = 0;
            int skipped = 0;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        if (selectedObject == null || selectedObject.ObjectId.IsNull)
                        {
                            skipped++;
                            continue;
                        }

                        DBObject sourceObject = transaction.GetObject(
                            selectedObject.ObjectId,
                            OpenMode.ForRead,
                            false);
                        var sourceEntity = sourceObject as AcEntity;

                        if (!IsSupportedSource(sourceObject) ||
                            sourceEntity == null ||
                            IsLayerLocked(transaction, sourceEntity.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        CivilFeatureLine.Create(string.Empty, selectedObject.ObjectId);
                        created++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLCREATE complete. Feature lines created: {0}; skipped: {1}.",
                    created,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLCREATE cancelled. No changes were committed. {0}",
                    exception.Message);
            }
        }

        private static void AssignFromSurface(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect feature lines to assign elevations from a surface: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var surfaceOptions = new PromptEntityOptions("\nSelect Civil 3D surface: ");
            surfaceOptions.SetRejectMessage("\nSelect a Civil 3D surface.");
            surfaceOptions.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult surfaceResult = editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK)
            {
                return;
            }

            var gradeBreakOptions = new PromptKeywordOptions(
                "\nInsert intermediate surface grade-break points? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            gradeBreakOptions.Keywords.Add("Yes");
            gradeBreakOptions.Keywords.Add("No");
            PromptResult gradeBreakResult = editor.GetKeywords(gradeBreakOptions);
            if (gradeBreakResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            bool includeIntermediate =
                gradeBreakResult.Status == PromptStatus.OK &&
                string.Equals(gradeBreakResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);

            Database database = document.Database;
            int changed = 0;
            int skipped = 0;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                            transaction,
                            selectedObject,
                            true);

                        if (featureLine == null ||
                            featureLine.IsReferenceObject ||
                            IsLayerLocked(transaction, featureLine.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        featureLine.AssignElevationsFromSurface(
                            surfaceResult.ObjectId,
                            includeIntermediate);
                        changed++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLSURFACE complete. Feature lines updated: {0}; skipped: {1}; intermediate points: {2}.",
                    changed,
                    skipped,
                    includeIntermediate ? "Yes" : "No");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLSURFACE cancelled. No changes were committed. {0}",
                    exception.Message);
            }
        }

        private static void InsertElevationPoint(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult entityResult = PromptForFeatureLine(
                editor,
                "\nSelect feature line: ");
            if (entityResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick location along feature line for the new elevation point: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            var modeOptions = new PromptKeywordOptions(
                "\nNew point elevation [Interpolate/Elevation] <Interpolate>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("Interpolate");
            modeOptions.Keywords.Add("Elevation");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel)
            {
                return;
            }

            bool useEnteredElevation =
                modeResult.Status == PromptStatus.OK &&
                string.Equals(modeResult.StringResult, "Elevation", StringComparison.OrdinalIgnoreCase);

            double enteredElevation = 0.0;
            if (useEnteredElevation)
            {
                PromptDoubleResult elevationResult = editor.GetDouble(
                    new PromptDoubleOptions("\nEnter elevation for the new point: ")
                    {
                        AllowNegative = true,
                        AllowZero = true,
                        AllowNone = false
                    });
                if (elevationResult.Status != PromptStatus.OK)
                {
                    return;
                }

                enteredElevation = elevationResult.Value;
            }

            Database database = document.Database;
            Point3d pickedPoint = pointResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                        transaction,
                        entityResult.ObjectId,
                        true);
                    EnsureEditable(transaction, featureLine);

                    Point3d pointOnFeatureLine = featureLine.GetClosestPointTo(pickedPoint, false);
                    featureLine.InsertElevationPoint(pointOnFeatureLine);

                    if (useEnteredElevation)
                    {
                        Point3dCollection allPoints = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                        int index = FindClosestPointIndex(allPoints, pointOnFeatureLine);
                        featureLine.SetPointElevation(index, enteredElevation);
                    }

                    transaction.Commit();

                    editor.WriteMessage(
                        "\nCE_FLINSERT complete at X={0:N3}, Y={1:N3}, Z={2:N3}.",
                        pointOnFeatureLine.X,
                        pointOnFeatureLine.Y,
                        useEnteredElevation ? enteredElevation : pointOnFeatureLine.Z);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLINSERT cancelled. No changes were committed. {0}",
                    exception.Message);
            }
        }

        private static void DeleteElevationPoint(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult entityResult = PromptForFeatureLine(
                editor,
                "\nSelect feature line: ");
            if (entityResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick near the elevation point to delete: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Database database = document.Database;
            Point3d pickedPoint = pointResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d nearestPoint;
            double nearestDistance;

            using (Transaction readTransaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                    readTransaction,
                    entityResult.ObjectId,
                    false);
                if (featureLine == null)
                {
                    editor.WriteMessage("\nThe selected object is not an ordinary feature line.");
                    return;
                }

                Point3dCollection elevationPoints = featureLine.GetPoints(
                    FeatureLinePointType.ElevationPoint);
                if (elevationPoints == null || elevationPoints.Count == 0)
                {
                    editor.WriteMessage("\nThe feature line has no removable elevation points.");
                    return;
                }

                int index = FindClosestPointIndex(elevationPoints, pickedPoint);
                nearestPoint = elevationPoints[index];
                nearestDistance = PlanDistance(nearestPoint, pickedPoint);
            }

            var confirmOptions = new PromptKeywordOptions(
                string.Format(
                    "\nDelete elevation point at X={0:N3}, Y={1:N3}, Z={2:N3} (pick distance {3:N3})? [Yes/No] <No>: ",
                    nearestPoint.X,
                    nearestPoint.Y,
                    nearestPoint.Z,
                    nearestDistance))
            {
                AllowNone = true
            };
            confirmOptions.Keywords.Add("Yes");
            confirmOptions.Keywords.Add("No");
            PromptResult confirmResult = editor.GetKeywords(confirmOptions);
            if (confirmResult.Status != PromptStatus.OK ||
                !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_FLDELETE cancelled.");
                return;
            }

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                        transaction,
                        entityResult.ObjectId,
                        true);
                    EnsureEditable(transaction, featureLine);
                    featureLine.DeleteElevationPoint(nearestPoint);
                    transaction.Commit();
                }

                editor.WriteMessage("\nCE_FLDELETE complete.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLDELETE cancelled. No changes were committed. {0}",
                    exception.Message);
            }
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

        private static PromptEntityResult PromptForFeatureLine(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an ordinary Civil 3D feature line.");
            options.AddAllowedClass(typeof(CivilFeatureLine), false);
            return editor.GetEntity(options);
        }

        private static CivilFeatureLine OpenOrdinaryFeatureLine(
            Transaction transaction,
            SelectedObject selectedObject,
            bool forWrite)
        {
            if (selectedObject == null)
            {
                return null;
            }

            return OpenOrdinaryFeatureLine(transaction, selectedObject.ObjectId, forWrite);
        }

        private static CivilFeatureLine OpenOrdinaryFeatureLine(
            Transaction transaction,
            ObjectId objectId,
            bool forWrite)
        {
            if (objectId.IsNull)
            {
                return null;
            }

            var featureLine = transaction.GetObject(
                objectId,
                forWrite ? OpenMode.ForWrite : OpenMode.ForRead,
                false) as CivilFeatureLine;

            return featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine)
                ? featureLine
                : null;
        }

        private static void EnsureEditable(
            Transaction transaction,
            CivilFeatureLine featureLine)
        {
            if (featureLine == null)
            {
                throw new InvalidOperationException("The selected object is not an ordinary feature line.");
            }

            if (featureLine.IsReferenceObject)
            {
                throw new InvalidOperationException("Referenced feature lines cannot be edited.");
            }

            if (IsLayerLocked(transaction, featureLine.LayerId))
            {
                throw new InvalidOperationException("The feature line is on a locked layer.");
            }
        }

        private static bool IsSupportedSource(DBObject sourceObject)
        {
            return sourceObject is Line ||
                   sourceObject is Arc ||
                   sourceObject is Polyline ||
                   sourceObject is Polyline2d ||
                   sourceObject is Polyline3d;
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

        private static int FindClosestPointIndex(
            Point3dCollection points,
            Point3d target)
        {
            if (points == null || points.Count == 0)
            {
                throw new InvalidOperationException("No feature-line points were available.");
            }

            int bestIndex = 0;
            double bestDistance = double.PositiveInfinity;

            for (int index = 0; index < points.Count; index++)
            {
                double distance = PlanDistance(points[index], target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double deltaX = first.X - second.X;
            double deltaY = first.Y - second.Y;
            return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }
    }
}
