using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineWeedCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Conservatively removes redundant feature-line elevation points while
    /// preserving PI points and requiring an explicit preview confirmation.
    /// </summary>
    public sealed class FeatureLineWeedCommands
    {
        private const double PointMatchTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLWEED",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect ordinary feature lines to weed: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var verticalOptions = new PromptDoubleOptions(
                "\nMaximum vertical deviation from straight grade <0.010>: ")
            {
                AllowNegative = false,
                AllowZero = true,
                AllowNone = true,
                DefaultValue = 0.010,
                UseDefaultValue = true
            };
            PromptDoubleResult verticalResult = editor.GetDouble(verticalOptions);
            if (verticalResult.Status != PromptStatus.OK &&
                verticalResult.Status != PromptStatus.None)
            {
                return;
            }

            var spacingOptions = new PromptDoubleOptions(
                "\nMinimum point spacing to enforce; enter 0 for vertical test only <0.500>: ")
            {
                AllowNegative = false,
                AllowZero = true,
                AllowNone = true,
                DefaultValue = 0.500,
                UseDefaultValue = true
            };
            PromptDoubleResult spacingResult = editor.GetDouble(spacingOptions);
            if (spacingResult.Status != PromptStatus.OK &&
                spacingResult.Status != PromptStatus.None)
            {
                return;
            }

            double verticalTolerance = verticalResult.Status == PromptStatus.None
                ? 0.010
                : verticalResult.Value;
            double minimumSpacing = spacingResult.Status == PromptStatus.None
                ? 0.500
                : spacingResult.Value;

            PreviewResult preview = BuildPreview(
                document.Database,
                selection,
                verticalTolerance,
                minimumSpacing);

            if (preview.CandidatePoints == 0)
            {
                editor.WriteMessage(
                    "\nCE_FLWEED preview: no removable elevation points found. " +
                    "Feature lines checked: {0}; skipped: {1}.",
                    preview.FeatureLinesChecked,
                    preview.FeatureLinesSkipped);
                return;
            }

            var confirmOptions = new PromptKeywordOptions(
                string.Format(
                    "\nCE_FLWEED preview: remove {0} elevation points from {1} feature lines? [Yes/No] <No>: ",
                    preview.CandidatePoints,
                    preview.CandidateFeatureLines))
            {
                AllowNone = true
            };
            confirmOptions.Keywords.Add("Yes");
            confirmOptions.Keywords.Add("No");

            PromptResult confirmResult = editor.GetKeywords(confirmOptions);
            if (confirmResult.Status != PromptStatus.OK ||
                !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_FLWEED cancelled. No changes were made.");
                return;
            }

            ApplyWeeding(
                document,
                selection,
                verticalTolerance,
                minimumSpacing);
        }

        private static PreviewResult BuildPreview(
            Database database,
            PromptSelectionResult selection,
            double verticalTolerance,
            double minimumSpacing)
        {
            var result = new PreviewResult();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
                        transaction,
                        selectedObject,
                        false);

                    if (featureLine == null ||
                        featureLine.IsReferenceObject ||
                        featureLine.Closed ||
                        IsLayerLocked(transaction, featureLine.LayerId))
                    {
                        result.FeatureLinesSkipped++;
                        continue;
                    }

                    result.FeatureLinesChecked++;
                    Point3dCollection candidates = FindCandidates(
                        featureLine,
                        verticalTolerance,
                        minimumSpacing);

                    if (candidates.Count > 0)
                    {
                        result.CandidateFeatureLines++;
                        result.CandidatePoints += candidates.Count;
                    }
                }
            }

            return result;
        }

        private static void ApplyWeeding(
            Document document,
            PromptSelectionResult selection,
            double verticalTolerance,
            double minimumSpacing)
        {
            Editor editor = document.Editor;
            Database database = document.Database;
            int changedFeatureLines = 0;
            int deletedPoints = 0;
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
                            false);

                        if (featureLine == null ||
                            featureLine.IsReferenceObject ||
                            featureLine.Closed ||
                            IsLayerLocked(transaction, featureLine.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        Point3dCollection candidates = FindCandidates(
                            featureLine,
                            verticalTolerance,
                            minimumSpacing);
                        if (candidates.Count == 0)
                        {
                            continue;
                        }

                        featureLine.UpgradeOpen();
                        featureLine.DeleteElevationPoints(candidates);
                        changedFeatureLines++;
                        deletedPoints += candidates.Count;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLWEED complete. Feature lines changed: {0}; " +
                    "elevation points removed: {1}; skipped: {2}.",
                    changedFeatureLines,
                    deletedPoints,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLWEED cancelled. No changes were committed. {0}",
                    exception.Message);
            }
        }

        private static Point3dCollection FindCandidates(
            CivilFeatureLine featureLine,
            double verticalTolerance,
            double minimumSpacing)
        {
            Point3dCollection allPoints = featureLine.GetPoints(FeatureLinePointType.AllPoints);
            Point3dCollection elevationPoints = featureLine.GetPoints(
                FeatureLinePointType.ElevationPoint);
            var candidates = new Point3dCollection();

            if (allPoints == null ||
                elevationPoints == null ||
                allPoints.Count < 3 ||
                elevationPoints.Count == 0)
            {
                return candidates;
            }

            // This first version performs one conservative pass. Adjacent candidate
            // points are not removed in the same run, preventing an aggressive
            // cascading simplification. Running the command again can weed further.
            bool previousPointWasCandidate = false;

            for (int index = 1; index < allPoints.Count - 1; index++)
            {
                Point3d previous = allPoints[index - 1];
                Point3d current = allPoints[index];
                Point3d next = allPoints[index + 1];

                if (previousPointWasCandidate)
                {
                    previousPointWasCandidate = false;
                    continue;
                }

                if (!ContainsPoint(elevationPoints, current))
                {
                    continue;
                }

                double distanceIn = PlanDistance(previous, current);
                double distanceOut = PlanDistance(current, next);
                double totalDistance = distanceIn + distanceOut;
                if (totalDistance <= PointMatchTolerance)
                {
                    continue;
                }

                double interpolationRatio = distanceIn / totalDistance;
                double interpolatedElevation = previous.Z +
                    ((next.Z - previous.Z) * interpolationRatio);
                double verticalDeviation = Math.Abs(current.Z - interpolatedElevation);

                bool passesVerticalTest = verticalDeviation <= verticalTolerance;
                bool passesSpacingTest = minimumSpacing <= 0.0 ||
                    Math.Min(distanceIn, distanceOut) < minimumSpacing;

                if (passesVerticalTest && passesSpacingTest)
                {
                    candidates.Add(current);
                    previousPointWasCandidate = true;
                }
            }

            return candidates;
        }

        private static bool ContainsPoint(Point3dCollection points, Point3d target)
        {
            for (int index = 0; index < points.Count; index++)
            {
                if (points[index].DistanceTo(target) <= PointMatchTolerance)
                {
                    return true;
                }
            }

            return false;
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

        private static CivilFeatureLine OpenOrdinaryFeatureLine(
            Transaction transaction,
            SelectedObject selectedObject,
            bool forWrite)
        {
            if (selectedObject == null || selectedObject.ObjectId.IsNull)
            {
                return null;
            }

            var featureLine = transaction.GetObject(
                selectedObject.ObjectId,
                forWrite ? OpenMode.ForWrite : OpenMode.ForRead,
                false) as CivilFeatureLine;

            return featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine)
                ? featureLine
                : null;
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

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double deltaX = first.X - second.X;
            double deltaY = first.Y - second.Y;
            return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }

        private sealed class PreviewResult
        {
            public int FeatureLinesChecked { get; set; }
            public int FeatureLinesSkipped { get; set; }
            public int CandidateFeatureLines { get; set; }
            public int CandidatePoints { get; set; }
        }
    }
}
