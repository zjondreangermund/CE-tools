using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Minimum-click Civil 3D feature-line reporting and elevation utilities.
    /// </summary>
    public sealed class FeatureLineCommands
    {
        private const string ReportKeyword = "Report";
        private const string RaiseLowerKeyword = "RaiseLower";
        private const string SetElevationKeyword = "SetElevation";

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            var options = new PromptKeywordOptions(
                "\nFeature Line tool [Report/RaiseLower/SetElevation] <Report>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(ReportKeyword);
            options.Keywords.Add(RaiseLowerKeyword);
            options.Keywords.Add(SetElevationKeyword);

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? ReportKeyword
                : result.StringResult;

            if (string.Equals(mode, RaiseLowerKeyword, StringComparison.OrdinalIgnoreCase))
            {
                RaiseLower(document);
            }
            else if (string.Equals(mode, SetElevationKeyword, StringComparison.OrdinalIgnoreCase))
            {
                SetElevation(document);
            }
            else
            {
                Report(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLREPORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Report(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLRAISE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineRaiseLower()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                RaiseLower(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSETELEV",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineSetElevation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                SetElevation(document);
            }
        }

        private static void Report(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetFeatureLineSelection(
                editor,
                "\nSelect feature lines to report: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            Database database = document.Database;
            int counted = 0;
            int skipped = 0;
            int totalPoints = 0;
            int totalPiPoints = 0;
            int totalElevationPoints = 0;
            double totalLength2D = 0.0;
            double totalLength3D = 0.0;
            double minimumElevation = double.PositiveInfinity;
            double maximumElevation = double.NegativeInfinity;
            double minimumGrade = double.PositiveInfinity;
            double maximumGrade = double.NegativeInfinity;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilFeatureLine featureLine = TryOpenFeatureLine(
                        transaction,
                        selectedObject);
                    if (featureLine == null)
                    {
                        skipped++;
                        continue;
                    }

                    counted++;
                    totalPoints += featureLine.PointsCount;
                    totalPiPoints += featureLine.PIPointsCount;
                    totalElevationPoints += featureLine.ElevationPointsCount;
                    totalLength2D += featureLine.Length2D;
                    totalLength3D += featureLine.Length3D;
                    minimumElevation = Math.Min(minimumElevation, featureLine.MinElevation);
                    maximumElevation = Math.Max(maximumElevation, featureLine.MaxElevation);
                    minimumGrade = Math.Min(minimumGrade, featureLine.MinGrade);
                    maximumGrade = Math.Max(maximumGrade, featureLine.MaxGrade);

                    editor.WriteMessage(
                        "\n  {0}: L2D={1:N3}; L3D={2:N3}; Elev={3:N3} to {4:N3}; " +
                        "Grade={5:N3}% to {6:N3}%; Points={7}",
                        GetDisplayName(featureLine),
                        featureLine.Length2D,
                        featureLine.Length3D,
                        featureLine.MinElevation,
                        featureLine.MaxElevation,
                        featureLine.MinGrade * 100.0,
                        featureLine.MaxGrade * 100.0,
                        featureLine.PointsCount);
                }
            }

            if (counted == 0)
            {
                editor.WriteMessage(
                    "\nCE_FLREPORT complete. No ordinary Civil 3D feature lines were selected; skipped: {0}.",
                    skipped);
                return;
            }

            editor.WriteMessage(
                "\nCE_FLREPORT TOTAL — Count={0}; skipped={1}; L2D={2:N3}; L3D={3:N3}; " +
                "Elev={4:N3} to {5:N3}; Grade={6:N3}% to {7:N3}%; " +
                "Points={8} (PI={9}, elevation={10}).",
                counted,
                skipped,
                totalLength2D,
                totalLength3D,
                minimumElevation,
                maximumElevation,
                minimumGrade * 100.0,
                maximumGrade * 100.0,
                totalPoints,
                totalPiPoints,
                totalElevationPoints);
        }

        private static void RaiseLower(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetFeatureLineSelection(
                editor,
                "\nSelect feature lines to raise/lower: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var valueOptions = new PromptDoubleOptions(
                "\nEnter elevation difference (+ raise, - lower): ")
            {
                AllowNegative = true,
                AllowZero = false,
                AllowNone = false
            };
            PromptDoubleResult valueResult = editor.GetDouble(valueOptions);
            if (valueResult.Status != PromptStatus.OK)
            {
                return;
            }

            ModifyElevations(
                document,
                selection,
                "CE_FLRAISE",
                delegate(CivilFeatureLine featureLine, Point3dCollection points, int index)
                {
                    Point3d point = points[index];
                    if (featureLine.IsElevationRelativeToSurface(point))
                    {
                        double currentOffset = featureLine.GetPointRelativeElevation(point);
                        featureLine.SetPointRelativeElevation(
                            point,
                            true,
                            currentOffset + valueResult.Value);
                    }
                    else
                    {
                        featureLine.SetPointElevation(index, point.Z + valueResult.Value);
                    }
                });
        }

        private static void SetElevation(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetFeatureLineSelection(
                editor,
                "\nSelect feature lines to set to one elevation: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var valueOptions = new PromptDoubleOptions(
                "\nEnter absolute elevation for all feature-line points: ")
            {
                AllowNegative = true,
                AllowZero = true,
                AllowNone = false
            };
            PromptDoubleResult valueResult = editor.GetDouble(valueOptions);
            if (valueResult.Status != PromptStatus.OK)
            {
                return;
            }

            ModifyElevations(
                document,
                selection,
                "CE_FLSETELEV",
                delegate(CivilFeatureLine featureLine, Point3dCollection points, int index)
                {
                    Point3d point = points[index];
                    if (featureLine.IsElevationRelativeToSurface(point))
                    {
                        featureLine.SetPointRelativeElevation(point, false, valueResult.Value);
                    }
                    else
                    {
                        featureLine.SetPointElevation(index, valueResult.Value);
                    }
                });
        }

        private static void ModifyElevations(
            Document document,
            PromptSelectionResult selection,
            string commandName,
            Action<CivilFeatureLine, Point3dCollection, int> pointAction)
        {
            Editor editor = document.Editor;
            Database database = document.Database;
            int changedLines = 0;
            int changedPoints = 0;
            int skipped = 0;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        CivilFeatureLine featureLine = TryOpenFeatureLine(
                            transaction,
                            selectedObject);
                        if (featureLine == null ||
                            featureLine.IsReferenceObject ||
                            IsLayerLocked(transaction, featureLine.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                        if (points == null || points.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        featureLine.UpgradeOpen();

                        for (int index = 0; index < points.Count; index++)
                        {
                            pointAction(featureLine, points, index);
                            changedPoints++;
                        }

                        changedLines++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\n{0} complete. Feature lines changed: {1}; points changed: {2}; skipped: {3}.",
                    commandName,
                    changedLines,
                    changedPoints,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\n{0} cancelled. No changes were committed. {1}",
                    commandName,
                    exception.Message);
            }
        }

        private static PromptSelectionResult GetFeatureLineSelection(
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

        private static CivilFeatureLine TryOpenFeatureLine(
            Transaction transaction,
            SelectedObject selectedObject)
        {
            if (selectedObject == null || selectedObject.ObjectId.IsNull)
            {
                return null;
            }

            var featureLine = transaction.GetObject(
                selectedObject.ObjectId,
                OpenMode.ForRead,
                false) as CivilFeatureLine;

            // Keep the first MVP limited to ordinary grading feature lines.
            // Derived automatic/corridor/survey objects must not be edited accidentally.
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

        private static string GetDisplayName(CivilFeatureLine featureLine)
        {
            if (!string.IsNullOrWhiteSpace(featureLine.Name))
            {
                return featureLine.Name;
            }

            return "FeatureLine " + featureLine.Handle.ToString();
        }
    }
}
