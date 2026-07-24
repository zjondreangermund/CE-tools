using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.WorkflowRepairCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Hardened workflows for commands raised in the 23 July 2026 review.
    /// The original commands remain available while the ribbon routes users to
    /// these explicit mutation commands during Civil 3D validation.
    /// </summary>
    public sealed class WorkflowRepairCommands
    {
        private const double GeometryTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORREBUILDX",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RebuildCorridorsExplicitly()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D corridors to rebuild: ");
            if (selection.Status != PromptStatus.OK) return;

            CorridorMutationPreview preview = BuildCorridorPreview(
                document.Database,
                selection);
            if (preview.EditableIds.Count == 0)
            {
                WriteRejectedSummary(editor, preview.RejectedReasons);
                editor.WriteMessage(
                    "\nCE_CORREBUILDX cancelled. No editable corridors were selected.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "Action",
                    "Rebuild selected editable corridors"),
                new KeyValuePair<string, string>(
                    "Editable corridors",
                    preview.EditableIds.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Out of date",
                    preview.OutOfDate.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Already up to date",
                    preview.UpToDate.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Rejected",
                    preview.RejectedCount.ToString(CultureInfo.InvariantCulture))
            };
            AppendRejectedRows(rows, preview.RejectedReasons);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools Corridor Rebuild",
                "This command calls Corridor.Rebuild() for every editable selected corridor. " +
                "Up-to-date corridors are also rebuilt so the command cannot behave like a report-only tool.",
                rows,
                "Rebuild"))
            {
                editor.WriteMessage(
                    "\nCE_CORREBUILDX cancelled. No corridors were rebuilt.");
                return;
            }

            int rebuilt = 0;
            int failed = 0;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId objectId in preview.EditableIds)
                    {
                        CivilCorridor corridor = Open<CivilCorridor>(
                            transaction,
                            objectId,
                            OpenMode.ForWrite);
                        if (corridor == null ||
                            corridor.IsReferenceObject ||
                            IsLayerLocked(transaction, corridor.LayerId))
                        {
                            failed++;
                            continue;
                        }

                        corridor.Rebuild();
                        rebuilt++;
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_CORREBUILDX complete. Corridor.Rebuild() called for {0}; skipped after preview={1}.",
                    rebuilt,
                    failed);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_CORREBUILDX cancelled. No rebuild transaction was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLRAISEX",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RaiseLowerFeatureLinesExplicitly()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect ordinary feature lines to raise or lower: ");
            if (selection.Status != PromptStatus.OK) return;

            PromptDoubleResult deltaResult = editor.GetDouble(
                new PromptDoubleOptions(
                    "\nEnter elevation difference (+ raise, - lower): ")
                {
                    AllowNegative = true,
                    AllowZero = false,
                    AllowNone = false
                });
            if (deltaResult.Status != PromptStatus.OK) return;

            FeatureLineMutationPreview preview = BuildFeatureLinePreview(
                document.Database,
                selection);
            if (preview.EditableIds.Count == 0)
            {
                WriteRejectedSummary(editor, preview.RejectedReasons);
                editor.WriteMessage(
                    "\nCE_FLRAISEX cancelled. No editable ordinary feature lines were selected.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "Action",
                    deltaResult.Value > 0.0 ? "Raise elevations" : "Lower elevations"),
                new KeyValuePair<string, string>(
                    "Elevation difference",
                    deltaResult.Value.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>(
                    "Feature lines",
                    preview.EditableIds.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Points to change",
                    preview.PointCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Current elevation range",
                    FormatRange(preview.MinimumElevation, preview.MaximumElevation)),
                new KeyValuePair<string, string>(
                    "Proposed elevation range",
                    FormatRange(
                        preview.MinimumElevation + deltaResult.Value,
                        preview.MaximumElevation + deltaResult.Value)),
                new KeyValuePair<string, string>(
                    "Rejected",
                    preview.RejectedCount.ToString(CultureInfo.InvariantCulture))
            };
            AppendRejectedRows(rows, preview.RejectedReasons);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools Feature Line Raise / Lower",
                "The selected feature-line point elevations will be edited in one transaction.",
                rows,
                deltaResult.Value > 0.0 ? "Raise" : "Lower"))
            {
                editor.WriteMessage(
                    "\nCE_FLRAISEX cancelled. No feature-line elevations were changed.");
                return;
            }

            int changedLines = 0;
            int changedPoints = 0;
            int skipped = 0;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId objectId in preview.EditableIds)
                    {
                        CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                            transaction,
                            objectId,
                            OpenMode.ForWrite);
                        if (!IsEditableFeatureLine(transaction, featureLine))
                        {
                            skipped++;
                            continue;
                        }

                        Point3dCollection points = featureLine.GetPoints(
                            FeatureLinePointType.AllPoints);
                        if (points == null || points.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        for (int index = 0; index < points.Count; index++)
                        {
                            Point3d point = points[index];
                            if (featureLine.IsElevationRelativeToSurface(point))
                            {
                                double currentOffset =
                                    featureLine.GetPointRelativeElevation(point);
                                featureLine.SetPointRelativeElevation(
                                    point,
                                    true,
                                    currentOffset + deltaResult.Value);
                            }
                            else
                            {
                                featureLine.SetPointElevation(
                                    index,
                                    point.Z + deltaResult.Value);
                            }

                            changedPoints++;
                        }

                        changedLines++;
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_FLRAISEX complete. Elevations edited on {0} feature lines; points changed={1}; skipped after preview={2}.",
                    changedLines,
                    changedPoints,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLRAISEX cancelled. No elevation changes were committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSURFACEUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AssignFeatureLinesFromSelectedSurface()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect ordinary feature lines to assign elevations from a surface: ");
            if (selection.Status != PromptStatus.OK) return;

            List<SurfaceChoice> surfaces = ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI cancelled. The current Civil 3D drawing contains no accessible surfaces.");
                return;
            }

            var dialog = new SurfaceSelectionWindow(surfaces);
            AcApplication.ShowModalWindow(dialog);
            SurfaceChoice selectedSurface = dialog.SelectedSurface;
            if (selectedSurface == null)
            {
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI cancelled. No surface was selected.");
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
            if (gradeBreakResult.Status == PromptStatus.Cancel) return;
            bool includeIntermediate = gradeBreakResult.Status == PromptStatus.OK &&
                string.Equals(
                    gradeBreakResult.StringResult,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase);

            FeatureLineMutationPreview preview = BuildFeatureLinePreview(
                document.Database,
                selection);
            if (preview.EditableIds.Count == 0)
            {
                WriteRejectedSummary(editor, preview.RejectedReasons);
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI cancelled. No editable ordinary feature lines were selected.");
                return;
            }

            var reviewRows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Surface", selectedSurface.Name),
                new KeyValuePair<string, string>("Surface type", selectedSurface.Type),
                new KeyValuePair<string, string>("Surface style", selectedSurface.Style),
                new KeyValuePair<string, string>(
                    "Feature lines",
                    preview.EditableIds.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Intermediate grade breaks",
                    includeIntermediate ? "Yes" : "No"),
                new KeyValuePair<string, string>(
                    "Rejected",
                    preview.RejectedCount.ToString(CultureInfo.InvariantCulture))
            };
            AppendRejectedRows(reviewRows, preview.RejectedReasons);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools Feature Line Surface Assignment",
                "Review the selected Civil 3D surface before changing feature-line elevations.",
                reviewRows,
                "Assign"))
            {
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI cancelled. No feature-line elevations were changed.");
                return;
            }

            int changed = 0;
            int skipped = 0;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId objectId in preview.EditableIds)
                    {
                        CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                            transaction,
                            objectId,
                            OpenMode.ForWrite);
                        if (!IsEditableFeatureLine(transaction, featureLine))
                        {
                            skipped++;
                            continue;
                        }

                        featureLine.AssignElevationsFromSurface(
                            selectedSurface.ObjectId,
                            includeIntermediate);
                        changed++;
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI complete. Surface={0}; feature lines updated={1}; skipped after preview={2}; intermediate points={3}.",
                    selectedSurface.Name,
                    changed,
                    skipped,
                    includeIntermediate ? "Yes" : "No");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLSURFACEUI cancelled. No surface elevations were committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLCONSTGRADE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyConstantGradeBetweenEndpoints()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect ordinary feature lines for constant grade between each line's endpoints: ");
            if (selection.Status != PromptStatus.OK) return;

            ConstantGradePreview preview = BuildConstantGradePreview(
                document.Database,
                selection);
            if (preview.EditableIds.Count == 0)
            {
                WriteRejectedSummary(editor, preview.RejectedReasons);
                editor.WriteMessage(
                    "\nCE_FLCONSTGRADE cancelled. No suitable open feature lines were selected.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "Action",
                    "Interpolate every existing point between its feature line's endpoint elevations"),
                new KeyValuePair<string, string>(
                    "Feature lines",
                    preview.EditableIds.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Points to set",
                    preview.PointCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Rejected",
                    preview.RejectedCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Distance basis",
                    "Cumulative plan distance through existing feature-line points")
            };
            AppendRejectedRows(rows, preview.RejectedReasons);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools Constant Grade",
                "Endpoint elevations remain unchanged. Intermediate PI and elevation points are assigned a linear grade between those endpoints.",
                rows,
                "Apply Grade"))
            {
                editor.WriteMessage(
                    "\nCE_FLCONSTGRADE cancelled. No elevations were changed.");
                return;
            }

            int changedLines = 0;
            int changedPoints = 0;
            int skipped = 0;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId objectId in preview.EditableIds)
                    {
                        CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                            transaction,
                            objectId,
                            OpenMode.ForWrite);
                        if (!IsEditableFeatureLine(transaction, featureLine))
                        {
                            skipped++;
                            continue;
                        }

                        Point3dCollection points = featureLine.GetPoints(
                            FeatureLinePointType.AllPoints);
                        List<double> distances;
                        double totalDistance;
                        if (!TryBuildPlanDistances(points, out distances, out totalDistance) ||
                            PlanDistance(points[0], points[points.Count - 1]) <= GeometryTolerance)
                        {
                            skipped++;
                            continue;
                        }

                        double startElevation = points[0].Z;
                        double endElevation = points[points.Count - 1].Z;
                        double elevationDifference = endElevation - startElevation;

                        for (int index = 0; index < points.Count; index++)
                        {
                            double fraction = totalDistance <= GeometryTolerance
                                ? 0.0
                                : distances[index] / totalDistance;
                            double targetElevation =
                                startElevation + (elevationDifference * fraction);
                            Point3d point = points[index];

                            if (featureLine.IsElevationRelativeToSurface(point))
                            {
                                featureLine.SetPointRelativeElevation(
                                    point,
                                    false,
                                    targetElevation);
                            }
                            else
                            {
                                featureLine.SetPointElevation(index, targetElevation);
                            }

                            changedPoints++;
                        }

                        changedLines++;
                    }

                    transaction.Commit();
                }

                editor.Regen();
                editor.WriteMessage(
                    "\nCE_FLCONSTGRADE complete. Feature lines graded={0}; points set={1}; skipped after preview={2}.",
                    changedLines,
                    changedPoints,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLCONSTGRADE cancelled. No constant-grade changes were committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKCOUNTX",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ValidateAndCountParkingBays()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines to validate and count: ");
            if (selection.Status != PromptStatus.OK) return;

            ParkingValidationResult validation = BuildParkingValidation(
                document.Database,
                selection);
            var columns = new List<string> { "Result", "Group / Reason", "Count" };
            var rows = new List<IList<string>>();

            foreach (KeyValuePair<string, int> group in validation.Groups)
            {
                rows.Add(new List<string>
                {
                    "Accepted",
                    group.Key,
                    group.Value.ToString(CultureInfo.InvariantCulture)
                });
            }

            foreach (KeyValuePair<string, int> reason in validation.RejectedReasons)
            {
                rows.Add(new List<string>
                {
                    "Rejected",
                    reason.Key,
                    reason.Value.ToString(CultureInfo.InvariantCulture)
                });
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Accepted parking bays: {0}; rejected objects: {1}; accepted groups: {2}.",
                validation.Candidates.Count,
                validation.RejectedCount,
                validation.Groups.Count);
            editor.WriteMessage("\nCE_PKCOUNTX complete. " + note);
            WriteRejectedSummary(editor, validation.RejectedReasons);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Parking Validation and Count",
                note,
                columns,
                rows,
                "Parking Validation and Count");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKNUMBER2",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ValidateAndNumberParkingBays()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines to validate and number: ");
            if (selection.Status != PromptStatus.OK) return;

            AnnotationOptions annotationOptions;
            if (!AnnotationSettingsStore.Prepare(
                document,
                false,
                out annotationOptions))
            {
                return;
            }

            var prefixOptions = new PromptStringOptions(
                "\nEnter parking number prefix <P>: ")
            {
                AllowSpaces = false,
                DefaultValue = "P",
                UseDefaultValue = true
            };
            PromptResult prefixResult = editor.GetString(prefixOptions);
            if (prefixResult.Status != PromptStatus.OK) return;

            var startOptions = new PromptIntegerOptions(
                "\nEnter starting parking number <1>: ")
            {
                AllowNone = true,
                DefaultValue = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult startResult = editor.GetInteger(startOptions);
            if (startResult.Status != PromptStatus.OK) return;

            var incrementOptions = new PromptIntegerOptions(
                "\nEnter numbering increment <1>: ")
            {
                AllowNone = true,
                DefaultValue = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult incrementResult = editor.GetInteger(incrementOptions);
            if (incrementResult.Status != PromptStatus.OK) return;
            if (incrementResult.Value == 0)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBER2 cancelled. The numbering increment cannot be zero.");
                return;
            }

            ParkingValidationResult validation = BuildParkingValidation(
                document.Database,
                selection);
            if (validation.Candidates.Count == 0)
            {
                WriteRejectedSummary(editor, validation.RejectedReasons);
                editor.WriteMessage(
                    "\nCE_PKNUMBER2 cancelled. No valid parking bay blocks or closed polylines were selected.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "Accepted parking bays",
                    validation.Candidates.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Rejected objects",
                    validation.RejectedCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Prefix", prefixResult.StringResult),
                new KeyValuePair<string, string>(
                    "Starting number",
                    startResult.Value.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Increment",
                    incrementResult.Value.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "Text height",
                    annotationOptions.TextHeight.ToString("N1", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>(
                    "Marker circles",
                    annotationOptions.DrawMarker ? "Yes" : "No"),
                new KeyValuePair<string, string>(
                    "Order",
                    "Selection-set order")
            };
            AppendRejectedRows(rows, validation.RejectedReasons);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools Parking Numbering",
                "Only validated block references and valid closed polylines will be numbered.",
                rows,
                "Number Bays"))
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBER2 cancelled. No parking numbers were created.");
                return;
            }

            int number = startResult.Value;
            int placed = 0;
            int skipped = 0;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);

                    foreach (ParkingCandidate candidate in validation.Candidates)
                    {
                        Entity entity = Open<Entity>(
                            transaction,
                            candidate.ObjectId,
                            OpenMode.ForRead);
                        if (entity == null || IsLayerLocked(transaction, entity.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        if (annotationOptions.DrawMarker)
                        {
                            AnnotationWriter.AddMarker(
                                currentSpace,
                                transaction,
                                candidate.Center,
                                annotationOptions.TextHeight,
                                candidate.LayerId);
                        }

                        var text = new MText();
                        text.SetDatabaseDefaults(document.Database);
                        text.LayerId = candidate.LayerId;
                        text.Location = candidate.Center;
                        text.Attachment = AttachmentPoint.MiddleCenter;
                        text.TextHeight = annotationOptions.TextHeight;
                        text.Contents = prefixResult.StringResult +
                            number.ToString(CultureInfo.InvariantCulture);
                        currentSpace.AppendEntity(text);
                        transaction.AddNewlyCreatedDBObject(text, true);

                        number += incrementResult.Value;
                        placed++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_PKNUMBER2 complete. Numbers placed={0}; rejected before numbering={1}; skipped after preview={2}; text height={3:N1}.",
                    placed,
                    validation.RejectedCount,
                    skipped,
                    annotationOptions.TextHeight);
                WriteRejectedSummary(editor, validation.RejectedReasons);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBER2 cancelled. No numbering transaction was committed. {0}",
                    exception.Message);
            }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static PromptSelectionResult GetSelection(
            Editor editor,
            string message)
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

        private static CorridorMutationPreview BuildCorridorPreview(
            Database database,
            PromptSelectionResult selection)
        {
            var preview = new CorridorMutationPreview();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        preview.Reject("Invalid selection entry");
                        continue;
                    }

                    CivilCorridor corridor = Open<CivilCorridor>(
                        transaction,
                        selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (corridor == null)
                    {
                        preview.Reject("Not a Civil 3D corridor");
                    }
                    else if (corridor.IsReferenceObject)
                    {
                        preview.Reject("Referenced corridor is read-only");
                    }
                    else if (IsLayerLocked(transaction, corridor.LayerId))
                    {
                        preview.Reject("Corridor layer is locked");
                    }
                    else
                    {
                        preview.EditableIds.Add(corridor.ObjectId);
                        if (corridor.IsOutOfDate) preview.OutOfDate++;
                        else preview.UpToDate++;
                    }
                }
            }

            return preview;
        }

        private static FeatureLineMutationPreview BuildFeatureLinePreview(
            Database database,
            PromptSelectionResult selection)
        {
            var preview = new FeatureLineMutationPreview();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        preview.Reject("Invalid selection entry");
                        continue;
                    }

                    CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                        transaction,
                        selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (featureLine == null)
                    {
                        preview.Reject("Not an ordinary editable feature line");
                    }
                    else if (featureLine.IsReferenceObject)
                    {
                        preview.Reject("Referenced feature line is read-only");
                    }
                    else if (IsLayerLocked(transaction, featureLine.LayerId))
                    {
                        preview.Reject("Feature-line layer is locked");
                    }
                    else
                    {
                        Point3dCollection points = featureLine.GetPoints(
                            FeatureLinePointType.AllPoints);
                        if (points == null || points.Count == 0)
                        {
                            preview.Reject("Feature line contains no editable points");
                            continue;
                        }

                        preview.EditableIds.Add(featureLine.ObjectId);
                        preview.PointCount += points.Count;
                        preview.MinimumElevation = Math.Min(
                            preview.MinimumElevation,
                            featureLine.MinElevation);
                        preview.MaximumElevation = Math.Max(
                            preview.MaximumElevation,
                            featureLine.MaxElevation);
                    }
                }
            }

            return preview;
        }

        private static ConstantGradePreview BuildConstantGradePreview(
            Database database,
            PromptSelectionResult selection)
        {
            var preview = new ConstantGradePreview();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        preview.Reject("Invalid selection entry");
                        continue;
                    }

                    CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                        transaction,
                        selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (featureLine == null)
                    {
                        preview.Reject("Not an ordinary feature line");
                    }
                    else if (featureLine.IsReferenceObject)
                    {
                        preview.Reject("Referenced feature line is read-only");
                    }
                    else if (IsLayerLocked(transaction, featureLine.LayerId))
                    {
                        preview.Reject("Feature-line layer is locked");
                    }
                    else
                    {
                        Point3dCollection points = featureLine.GetPoints(
                            FeatureLinePointType.AllPoints);
                        List<double> distances;
                        double totalDistance;
                        if (points == null ||
                            points.Count < 2 ||
                            !TryBuildPlanDistances(points, out distances, out totalDistance))
                        {
                            preview.Reject("Feature line has insufficient plan length");
                        }
                        else if (PlanDistance(points[0], points[points.Count - 1]) <=
                                 GeometryTolerance)
                        {
                            preview.Reject("Closed or coincident feature-line endpoints");
                        }
                        else
                        {
                            preview.EditableIds.Add(featureLine.ObjectId);
                            preview.PointCount += points.Count;
                        }
                    }
                }
            }

            return preview;
        }

        private static List<SurfaceChoice> ReadSurfaceChoices(Document document)
        {
            var choices = new List<SurfaceChoice>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return choices;

            try
            {
                ObjectIdCollection surfaceIds = civilDocument.GetSurfaceIds();
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId surfaceId in surfaceIds)
                    {
                        CivilSurface surface = Open<CivilSurface>(
                            transaction,
                            surfaceId,
                            OpenMode.ForRead);
                        if (surface == null) continue;

                        string minimum = string.Empty;
                        string maximum = string.Empty;
                        try
                        {
                            var properties = surface.GetGeneralProperties();
                            minimum = properties.MinimumElevation.ToString(
                                "N3",
                                CultureInfo.CurrentCulture);
                            maximum = properties.MaximumElevation.ToString(
                                "N3",
                                CultureInfo.CurrentCulture);
                        }
                        catch
                        {
                            minimum = "<Unavailable>";
                            maximum = "<Unavailable>";
                        }

                        choices.Add(new SurfaceChoice(
                            surface.ObjectId,
                            surface.Name,
                            surface.GetType().Name,
                            surface.StyleName,
                            minimum,
                            maximum,
                            surface.IsOutOfDate ? "Out of date" : "Current"));
                    }
                }
            }
            catch
            {
                // Return any choices read before the API failure.
            }

            choices.Sort(delegate(SurfaceChoice first, SurfaceChoice second)
            {
                return string.Compare(
                    first.Name,
                    second.Name,
                    StringComparison.OrdinalIgnoreCase);
            });
            return choices;
        }

        private static ParkingValidationResult BuildParkingValidation(
            Database database,
            PromptSelectionResult selection)
        {
            var result = new ParkingValidationResult();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        result.Reject("Invalid selection entry");
                        continue;
                    }

                    Entity entity = Open<Entity>(
                        transaction,
                        selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (entity == null)
                    {
                        result.Reject("Object could not be opened");
                        continue;
                    }

                    if (IsLayerLocked(transaction, entity.LayerId))
                    {
                        result.Reject("Object layer is locked");
                        continue;
                    }

                    string groupName;
                    var blockReference = entity as BlockReference;
                    if (blockReference != null)
                    {
                        BlockTableRecord definition = ReadBlockDefinition(
                            transaction,
                            blockReference);
                        if (definition == null)
                        {
                            result.Reject("Block definition is unavailable");
                            continue;
                        }

                        if (definition.IsFromExternalReference)
                        {
                            result.Reject("Xref block is not a parking bay");
                            continue;
                        }

                        groupName = "Block: " + definition.Name;
                    }
                    else
                    {
                        var polyline = entity as Polyline;
                        if (polyline == null)
                        {
                            result.Reject("Unsupported object type: " + entity.GetType().Name);
                            continue;
                        }

                        if (!polyline.Closed)
                        {
                            result.Reject("Polyline is open");
                            continue;
                        }

                        if (polyline.NumberOfVertices < 3)
                        {
                            result.Reject("Closed polyline has fewer than three vertices");
                            continue;
                        }

                        try
                        {
                            if (Math.Abs(polyline.Area) <= GeometryTolerance)
                            {
                                result.Reject("Closed polyline has zero area");
                                continue;
                            }
                        }
                        catch
                        {
                            result.Reject("Closed polyline area is invalid");
                            continue;
                        }

                        groupName = "Closed polyline layer: " + polyline.Layer;
                    }

                    Point3d center;
                    if (!TryGetCenter(entity, out center))
                    {
                        result.Reject("Object extents are unavailable");
                        continue;
                    }

                    result.Accept(new ParkingCandidate(
                        entity.ObjectId,
                        center,
                        entity.LayerId,
                        groupName));
                }
            }

            return result;
        }

        private static BlockTableRecord ReadBlockDefinition(
            Transaction transaction,
            BlockReference blockReference)
        {
            ObjectId definitionId = blockReference.IsDynamicBlock
                ? blockReference.DynamicBlockTableRecord
                : blockReference.BlockTableRecord;
            return Open<BlockTableRecord>(
                transaction,
                definitionId,
                OpenMode.ForRead);
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

        private static CivilFeatureLine OpenOrdinaryFeatureLine(
            Transaction transaction,
            ObjectId objectId,
            OpenMode openMode)
        {
            CivilFeatureLine featureLine = Open<CivilFeatureLine>(
                transaction,
                objectId,
                openMode);
            return featureLine != null &&
                   featureLine.GetType() == typeof(CivilFeatureLine)
                ? featureLine
                : null;
        }

        private static bool IsEditableFeatureLine(
            Transaction transaction,
            CivilFeatureLine featureLine)
        {
            return featureLine != null &&
                   !featureLine.IsReferenceObject &&
                   !IsLayerLocked(transaction, featureLine.LayerId);
        }

        private static T Open<T>(
            Transaction transaction,
            ObjectId objectId,
            OpenMode openMode)
            where T : DBObject
        {
            if (objectId.IsNull || objectId.IsErased) return null;
            try
            {
                return transaction.GetObject(
                    objectId,
                    openMode,
                    false) as T;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLayerLocked(
            Transaction transaction,
            ObjectId layerId)
        {
            if (layerId.IsNull) return false;
            LayerTableRecord layer = Open<LayerTableRecord>(
                transaction,
                layerId,
                OpenMode.ForRead);
            return layer != null && layer.IsLocked;
        }

        private static bool TryBuildPlanDistances(
            Point3dCollection points,
            out List<double> distances,
            out double totalDistance)
        {
            distances = new List<double>();
            totalDistance = 0.0;
            if (points == null || points.Count < 2) return false;

            distances.Add(0.0);
            for (int index = 1; index < points.Count; index++)
            {
                totalDistance += PlanDistance(points[index - 1], points[index]);
                distances.Add(totalDistance);
            }

            return totalDistance > GeometryTolerance;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static string FormatRange(double minimum, double maximum)
        {
            if (double.IsInfinity(minimum) || double.IsInfinity(maximum))
                return "<Unavailable>";
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0:N3} to {1:N3}",
                minimum,
                maximum);
        }

        private static void AddReason(
            IDictionary<string, int> reasons,
            string reason)
        {
            int current;
            reasons.TryGetValue(reason, out current);
            reasons[reason] = current + 1;
        }

        private static void AppendRejectedRows(
            IList<KeyValuePair<string, string>> rows,
            IDictionary<string, int> reasons)
        {
            foreach (KeyValuePair<string, int> reason in reasons)
            {
                rows.Add(new KeyValuePair<string, string>(
                    "Rejected: " + reason.Key,
                    reason.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private static void WriteRejectedSummary(
            Editor editor,
            IDictionary<string, int> reasons)
        {
            foreach (KeyValuePair<string, int> reason in reasons)
            {
                editor.WriteMessage(
                    "\n  Rejected — {0}: {1}",
                    reason.Key,
                    reason.Value);
            }
        }

        private sealed class CorridorMutationPreview
        {
            public CorridorMutationPreview()
            {
                EditableIds = new List<ObjectId>();
                RejectedReasons = new SortedDictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            }

            public List<ObjectId> EditableIds { get; }
            public IDictionary<string, int> RejectedReasons { get; }
            public int OutOfDate { get; set; }
            public int UpToDate { get; set; }
            public int RejectedCount { get; private set; }

            public void Reject(string reason)
            {
                RejectedCount++;
                AddReason(RejectedReasons, reason);
            }
        }

        private class FeatureLineMutationPreview
        {
            public FeatureLineMutationPreview()
            {
                EditableIds = new List<ObjectId>();
                RejectedReasons = new SortedDictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
                MinimumElevation = double.PositiveInfinity;
                MaximumElevation = double.NegativeInfinity;
            }

            public List<ObjectId> EditableIds { get; }
            public IDictionary<string, int> RejectedReasons { get; }
            public int PointCount { get; set; }
            public int RejectedCount { get; private set; }
            public double MinimumElevation { get; set; }
            public double MaximumElevation { get; set; }

            public void Reject(string reason)
            {
                RejectedCount++;
                AddReason(RejectedReasons, reason);
            }
        }

        private sealed class ConstantGradePreview : FeatureLineMutationPreview
        {
        }

        private sealed class ParkingCandidate
        {
            public ParkingCandidate(
                ObjectId objectId,
                Point3d center,
                ObjectId layerId,
                string group)
            {
                ObjectId = objectId;
                Center = center;
                LayerId = layerId;
                Group = group;
            }

            public ObjectId ObjectId { get; }
            public Point3d Center { get; }
            public ObjectId LayerId { get; }
            public string Group { get; }
        }

        private sealed class ParkingValidationResult
        {
            public ParkingValidationResult()
            {
                Candidates = new List<ParkingCandidate>();
                Groups = new SortedDictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
                RejectedReasons = new SortedDictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            }

            public List<ParkingCandidate> Candidates { get; }
            public IDictionary<string, int> Groups { get; }
            public IDictionary<string, int> RejectedReasons { get; }
            public int RejectedCount { get; private set; }

            public void Accept(ParkingCandidate candidate)
            {
                Candidates.Add(candidate);
                AddReason(Groups, candidate.Group);
            }

            public void Reject(string reason)
            {
                RejectedCount++;
                AddReason(RejectedReasons, reason);
            }
        }
    }

    internal sealed class SurfaceChoice
    {
        public SurfaceChoice(
            ObjectId objectId,
            string name,
            string type,
            string style,
            string minimumElevation,
            string maximumElevation,
            string state)
        {
            ObjectId = objectId;
            Name = name ?? string.Empty;
            Type = type ?? string.Empty;
            Style = style ?? string.Empty;
            MinimumElevation = minimumElevation ?? string.Empty;
            MaximumElevation = maximumElevation ?? string.Empty;
            State = state ?? string.Empty;
        }

        public ObjectId ObjectId { get; }
        public string Name { get; }
        public string Type { get; }
        public string Style { get; }
        public string MinimumElevation { get; }
        public string MaximumElevation { get; }
        public string State { get; }
    }

    internal sealed class SurfaceSelectionWindow : Window
    {
        private readonly DataGrid _grid;

        public SurfaceSelectionWindow(IList<SurfaceChoice> surfaces)
        {
            Title = "CE Tools — Select Civil 3D Surface";
            Width = 920;
            Height = 540;
            MinWidth = 700;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1.0, GridUnitType.Star)
            });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var note = new TextBlock
            {
                Text = "Select the surface that must control the selected feature-line elevations. Double-clicking a row also selects it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(note, 0);
            root.Children.Add(note);

            _grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                ItemsSource = surfaces
            };
            _grid.Columns.Add(Column("Surface", "Name", 1.7));
            _grid.Columns.Add(Column("Type", "Type", 1.1));
            _grid.Columns.Add(Column("Style", "Style", 1.5));
            _grid.Columns.Add(Column("Min Elev", "MinimumElevation", 0.9));
            _grid.Columns.Add(Column("Max Elev", "MaximumElevation", 0.9));
            _grid.Columns.Add(Column("State", "State", 0.8));
            if (surfaces != null && surfaces.Count > 0)
            {
                _grid.SelectedIndex = 0;
            }
            _grid.MouseDoubleClick += OnGridDoubleClick;
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var selectButton = Button("Select Surface", 120);
            selectButton.IsDefault = true;
            selectButton.Click += delegate { AcceptSelection(); };
            buttons.Children.Add(selectButton);

            var cancelButton = Button("Cancel", 90);
            cancelButton.IsCancel = true;
            cancelButton.Click += delegate
            {
                SelectedSurface = null;
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(cancelButton);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
        }

        public SurfaceChoice SelectedSurface { get; private set; }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptSelection();
        }

        private void AcceptSelection()
        {
            SelectedSurface = _grid.SelectedItem as SurfaceChoice;
            if (SelectedSurface == null) return;
            DialogResult = true;
            Close();
        }

        private static DataGridTextColumn Column(
            string header,
            string property,
            double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(property),
                Width = new DataGridLength(width, DataGridLengthUnitType.Star),
                MinWidth = 90
            };
        }

        private static Button Button(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                MinHeight = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3)
            };
        }
    }
}
