using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Read-only Civil 3D surface reporting, elevation inquiry, annotation and
    /// two-surface point comparison tools.
    /// </summary>
    public sealed class SurfaceCommands
    {
        private const string ReportKeyword = "Report";
        private const string ElevationKeyword = "Elevation";
        private const string LabelKeyword = "Label";
        private const string CompareKeyword = "Compare";
        private const double DifferenceTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nSurface tool [Report/Elevation/Label/Compare] <Elevation>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(ReportKeyword);
            options.Keywords.Add(ElevationKeyword);
            options.Keywords.Add(LabelKeyword);
            options.Keywords.Add(CompareKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? ElevationKeyword
                : result.StringResult;

            if (string.Equals(mode, ReportKeyword, StringComparison.OrdinalIgnoreCase))
            {
                ReportSurfaces(document);
            }
            else if (string.Equals(mode, LabelKeyword, StringComparison.OrdinalIgnoreCase))
            {
                PlaceElevationLabel(document);
            }
            else if (string.Equals(mode, CompareKeyword, StringComparison.OrdinalIgnoreCase))
            {
                CompareSurfaces(document);
            }
            else
            {
                ReportElevation(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFREPORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportSurfaces(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFELEV",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceElevation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportElevation(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFLABEL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceElevationLabel()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                PlaceElevationLabel(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFCOMPARE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceComparison()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                CompareSurfaces(document);
            }
        }

        private static void ReportSurfaces(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSurfaceSelection(
                editor,
                "\nSelect Civil 3D surfaces to report: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int counted = 0;
            int skipped = 0;
            Database database = document.Database;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilSurface surface = OpenSurface(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId);
                    if (surface == null)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        var properties = surface.GetGeneralProperties();
                        counted++;

                        editor.WriteMessage(
                            "\n  {0}: Type={1}; Style={2}; Points={3}; " +
                            "Elev={4:N3} to {5:N3}; Mean={6:N3}; " +
                            "X={7:N3} to {8:N3}; Y={9:N3} to {10:N3}; " +
                            "Volume={11}; Reference={12}; OutOfDate={13}; Locked={14}",
                            surface.Name,
                            surface.GetType().Name,
                            surface.StyleName,
                            properties.NumberOfPoints,
                            properties.MinimumElevation,
                            properties.MaximumElevation,
                            properties.MeanElevation,
                            properties.MinimumCoordinateX,
                            properties.MaximumCoordinateX,
                            properties.MinimumCoordinateY,
                            properties.MaximumCoordinateY,
                            surface.IsVolumeSurface ? "Yes" : "No",
                            surface.IsReferenceObject ? "Yes" : "No",
                            surface.IsOutOfDate ? "Yes" : "No",
                            surface.Lock ? "Yes" : "No");
                    }
                    catch (System.Exception exception)
                    {
                        skipped++;
                        editor.WriteMessage(
                            "\n  Surface {0} skipped: {1}",
                            surface.Name,
                            exception.Message);
                    }
                }
            }

            editor.WriteMessage(
                "\nCE_SFREPORT complete. Surfaces: {0}; skipped: {1}.",
                counted,
                skipped);
        }

        private static void ReportElevation(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult surfaceResult = PromptForSurface(
                editor,
                "\nSelect Civil 3D surface: ");
            if (surfaceResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick point for surface elevation: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d point = ToWorld(editor, pointResult.Value);

            try
            {
                SurfacePointResult result = ReadSurfacePoint(
                    document.Database,
                    surfaceResult.ObjectId,
                    point);

                editor.WriteMessage(
                    "\nCE_SFELEV — Surface={0}; X={1:N3}; Y={2:N3}; Elevation={3:N3}.",
                    result.SurfaceName,
                    point.X,
                    point.Y,
                    result.Elevation);
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFELEV cancelled. The picked point is outside the selected surface boundary.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFELEV cancelled. {0}", exception.Message);
            }
        }

        private static void PlaceElevationLabel(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult surfaceResult = PromptForSurface(
                editor,
                "\nSelect Civil 3D surface: ");
            if (surfaceResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick point for surface elevation label: ");
            if (targetResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d pickedPoint = ToWorld(editor, targetResult.Value);
            SurfacePointResult result;

            try
            {
                result = ReadSurfacePoint(
                    document.Database,
                    surfaceResult.ObjectId,
                    pickedPoint);
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFLABEL cancelled. The picked point is outside the selected surface boundary; no label was created.");
                return;
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFLABEL cancelled. {0}", exception.Message);
                return;
            }

            var labelOptions = new PromptPointOptions("\nPlace surface elevation label: ")
            {
                BasePoint = targetResult.Value,
                UseBasePoint = true,
                UseDashedLine = true
            };
            PromptPointResult labelResult = editor.GetPoint(labelOptions);
            if (labelResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d target = new Point3d(pickedPoint.X, pickedPoint.Y, 0.0);
            Point3d label = ToWorld(editor, labelResult.Value);
            string contents = string.Join(
                "\\P",
                result.SurfaceName,
                "E: " + pickedPoint.X.ToString("N3", CultureInfo.CurrentCulture),
                "N: " + pickedPoint.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + result.Elevation.ToString("N3", CultureInfo.CurrentCulture));

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    AddMLeader(document.Database, transaction, target, label, contents);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_SFLABEL complete. Surface={0}; X={1:N3}; Y={2:N3}; Elevation={3:N3}.",
                    result.SurfaceName,
                    pickedPoint.X,
                    pickedPoint.Y,
                    result.Elevation);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SFLABEL cancelled. No label was committed. {0}",
                    exception.Message);
            }
        }

        private static void CompareSurfaces(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult existingResult = PromptForSurface(
                editor,
                "\nSelect existing/base surface: ");
            if (existingResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptEntityResult proposedResult = PromptForSurface(
                editor,
                "\nSelect proposed/comparison surface: ");
            if (proposedResult.Status != PromptStatus.OK)
            {
                return;
            }

            if (existingResult.ObjectId == proposedResult.ObjectId)
            {
                editor.WriteMessage("\nSelect two different surfaces for comparison.");
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick comparison point: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d point = ToWorld(editor, pointResult.Value);

            try
            {
                SurfacePointResult existing = ReadSurfacePoint(
                    document.Database,
                    existingResult.ObjectId,
                    point);
                SurfacePointResult proposed = ReadSurfacePoint(
                    document.Database,
                    proposedResult.ObjectId,
                    point);

                double difference = proposed.Elevation - existing.Elevation;
                string classification = ClassifyDifference(difference);

                editor.WriteMessage(
                    "\nCE_SFCOMPARE — X={0:N3}; Y={1:N3}; Existing {2}={3:N3}; " +
                    "Proposed {4}={5:N3}; Difference (Proposed-Existing)={6:N3}; Result={7}.",
                    point.X,
                    point.Y,
                    existing.SurfaceName,
                    existing.Elevation,
                    proposed.SurfaceName,
                    proposed.Elevation,
                    difference,
                    classification);
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFCOMPARE cancelled. The picked point is outside one or both selected surface boundaries.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFCOMPARE cancelled. {0}", exception.Message);
            }
        }

        private static string ClassifyDifference(double difference)
        {
            if (difference > DifferenceTolerance)
            {
                return "Fill";
            }

            if (difference < -DifferenceTolerance)
            {
                return "Cut";
            }

            return "Level";
        }

        private static SurfacePointResult ReadSurfacePoint(
            Database database,
            ObjectId surfaceId,
            Point3d point)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = OpenSurface(transaction, surfaceId);
                if (surface == null)
                {
                    throw new InvalidOperationException("The selected object is not a Civil 3D surface.");
                }

                double elevation = surface.FindElevationAtXY(point.X, point.Y);
                return new SurfacePointResult(surface.Name, elevation);
            }
        }

        private static PromptEntityResult PromptForSurface(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetSurfaceSelection(Editor editor, string message)
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

        private static CivilSurface OpenSurface(Transaction transaction, ObjectId objectId)
        {
            if (objectId.IsNull)
            {
                return null;
            }

            return transaction.GetObject(
                objectId,
                OpenMode.ForRead,
                false) as CivilSurface;
        }

        private static Point3d ToWorld(Editor editor, Point3d pointInCurrentUcs)
        {
            return pointInCurrentUcs.TransformBy(editor.CurrentUserCoordinateSystem);
        }

        private static void AddMLeader(
            Database database,
            Transaction transaction,
            Point3d target,
            Point3d label,
            string contents)
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite,
                false);

            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.TextHeight = GetTextHeight(database);
            mtext.Contents = contents;
            mtext.Location = label;

            var leader = new MLeader();
            leader.SetDatabaseDefaults(database);
            leader.ContentType = ContentType.MTextContent;
            leader.MText = mtext;

            int leaderIndex = leader.AddLeader();
            int leaderLineIndex = leader.AddLeaderLine(leaderIndex);
            leader.AddFirstVertex(leaderLineIndex, target);
            leader.AddLastVertex(leaderLineIndex, label);

            currentSpace.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
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

        private sealed class SurfacePointResult
        {
            public SurfacePointResult(string surfaceName, double elevation)
            {
                SurfaceName = surfaceName;
                Elevation = elevation;
            }

            public string SurfaceName { get; }

            public double Elevation { get; }
        }
    }
}
