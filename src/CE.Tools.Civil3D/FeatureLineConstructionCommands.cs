using System;
using System.Collections.Generic;
using System.Linq;
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

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Feature Line Construction",
                "Create and heal feature lines, then maintain surface/elevation points.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create from objects", "CE_FLCREATE", "Convert supported lines, arcs and polylines to feature lines.", "01 Create"),
                    new DisciplineWorkflowAction("Heal stepped feature lines", "CE_FLSTEPJOIN", "Auto-order stepped pieces, close small gaps and preserve every piece endpoint as a vertex.", "01 Create"),
                    new DisciplineWorkflowAction("Assign surface elevations", "CE_FLSURFACE", "Set feature-line elevations from a selected Civil 3D surface.", "02 Elevations"),
                    new DisciplineWorkflowAction("Insert elevation point", "CE_FLINSERT", "Insert an interpolated or specified elevation point.", "03 Edit Points"),
                    new DisciplineWorkflowAction("Delete elevation point", "CE_FLDELETE", "Remove a selected removable elevation point with confirmation.", "03 Edit Points")
                });
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
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            string[] surfaceNames = new[] { "<Keep source elevations>" }
                .Concat(surfaces.Select(surface => surface.Name))
                .ToArray();
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Create Feature Lines",
                "Choose the elevation source before selecting the objects to convert.");
            settings.AddChoice(
                "Surface",
                "Elevation Source",
                "Surface",
                surfaceNames[0],
                "Keep source elevations or assign the new feature lines to a Civil 3D surface.",
                surfaceNames);
            settings.AddChoice(
                "Intermediate",
                "Elevation Source",
                "Add intermediate surface points",
                "No",
                "Insert surface grade-break points between the original vertices.",
                new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string selectedSurfaceName = settings.Text("Surface");
            SurfaceChoice selectedSurface = surfaces.FirstOrDefault(surface =>
                string.Equals(surface.Name, selectedSurfaceName,
                    StringComparison.OrdinalIgnoreCase));
            bool includeIntermediate = string.Equals(
                settings.Text("Intermediate"), "Yes",
                StringComparison.OrdinalIgnoreCase);

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

                        ObjectId featureLineId = CivilFeatureLine.Create(
                            string.Empty,
                            selectedObject.ObjectId);
                        if (selectedSurface != null)
                        {
                            CivilFeatureLine featureLine = transaction.GetObject(
                                featureLineId,
                                OpenMode.ForWrite,
                                false) as CivilFeatureLine;
                            if (featureLine != null)
                                featureLine.AssignElevationsFromSurface(
                                    selectedSurface.ObjectId,
                                    includeIntermediate);
                        }
                        created++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLCREATE complete. Feature lines created: {0}; skipped: {1}; surface: {2}; intermediate points: {3}.",
                    created,
                    skipped,
                    selectedSurface == null ? "source elevations" : selectedSurface.Name,
                    includeIntermediate ? "Yes" : "No");
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
