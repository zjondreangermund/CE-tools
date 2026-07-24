using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.AnnotationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Shared CE Tools annotation settings and annotation workflows. The settings
    /// are stored inside the current DWG so text height, marker and output choices
    /// remain consistent across disciplines.
    /// </summary>
    public sealed class AnnotationCommands
    {
        private const double OffsetTolerance = 0.000001;
        private const double StationTolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_ANNOTSETTINGS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AnnotationSettings()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions updated;
            if (AnnotationSettingsStore.EditAndSave(document, true, out updated))
            {
                document.Editor.WriteMessage(
                    "\nCE_ANNOTSETTINGS complete. Height={0:N1}; marker={1}; output={2}.",
                    updated.TextHeight,
                    updated.DrawMarker ? "Yes" : "No",
                    updated.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ALLABELX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AlignmentLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptEntityResult entityResult = PromptForEntity<CivilAlignment>(
                editor,
                "\nSelect Civil 3D alignment: ",
                "\nSelect a Civil 3D alignment.");
            if (entityResult.Status != PromptStatus.OK) return;

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick point for station/offset annotation: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d pickedPoint = ToWorld(editor, targetResult.Value);
            Point3d target = new Point3d(pickedPoint.X, pickedPoint.Y, 0.0);
            string contents;
            string plainDescription;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilAlignment alignment = Open<CivilAlignment>(
                        transaction,
                        entityResult.ObjectId);
                    if (alignment == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D alignment.");
                        return;
                    }

                    double station = 0.0;
                    double offset = 0.0;
                    alignment.StationOffset(target.X, target.Y, ref station, ref offset);
                    string side = ClassifySide(offset);
                    string stationText = FormatStation(alignment, station);
                    contents = string.Join(
                        "\\P",
                        alignment.Name,
                        "STA: " + stationText,
                        "OFF: " + Math.Abs(offset).ToString("N3", CultureInfo.CurrentCulture) +
                            " " + side);
                    plainDescription = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}; STA {1}; OFF {2:N3} {3}",
                        alignment.Name,
                        stationText,
                        Math.Abs(offset),
                        side);
                }
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_ALLABELX cancelled. The point falls beyond the alignment range.");
                return;
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_ALLABELX cancelled. {0}", exception.Message);
                return;
            }

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_ALLABELX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PRLABELX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptEntityResult profileResult = PromptForEntity<CivilProfile>(
                editor,
                "\nSelect Civil 3D profile: ",
                "\nSelect a Civil 3D profile.");
            if (profileResult.Status != PromptStatus.OK) return;

            PromptDoubleResult stationResult = editor.GetDouble(
                new PromptDoubleOptions("\nEnter raw profile station: ")
                {
                    AllowNegative = true,
                    AllowZero = true,
                    AllowNone = false
                });
            if (stationResult.Status != PromptStatus.OK) return;

            Point3d target;
            string contents;
            string plainDescription;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilProfile profile = Open<CivilProfile>(transaction, profileResult.ObjectId);
                    if (profile == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D profile.");
                        return;
                    }

                    ValidateProfileStation(profile, stationResult.Value);
                    CivilAlignment alignment = Open<CivilAlignment>(transaction, profile.AlignmentId);
                    if (alignment == null)
                    {
                        editor.WriteMessage(
                            "\nCE_PRLABELX cancelled. The parent alignment could not be opened.");
                        return;
                    }

                    double easting = 0.0;
                    double northing = 0.0;
                    alignment.PointLocation(
                        stationResult.Value,
                        0.0,
                        ref easting,
                        ref northing);
                    double elevation = profile.ElevationAt(stationResult.Value);
                    double grade = profile.GradeAt(stationResult.Value) * 100.0;
                    string stationText = FormatStation(alignment, stationResult.Value);
                    target = new Point3d(easting, northing, elevation);
                    contents = string.Join(
                        "\\P",
                        profile.Name,
                        "STA: " + stationText,
                        "ELEV: " + elevation.ToString("N3", CultureInfo.CurrentCulture),
                        "GRADE: " + grade.ToString("N3", CultureInfo.CurrentCulture) + "%");
                    plainDescription = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}; STA {1}; ELEV {2:N3}; GRADE {3:N3}%",
                        profile.Name,
                        stationText,
                        elevation,
                        grade);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_PRLABELX cancelled. {0}", exception.Message);
                return;
            }

            Point3d targetInUcs = target.TransformBy(editor.CurrentUserCoordinateSystem.Inverse());
            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetInUcs, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_PRLABELX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SFLABELX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptEntityResult surfaceResult = PromptForEntity<CivilSurface>(
                editor,
                "\nSelect Civil 3D surface: ",
                "\nSelect a Civil 3D surface.");
            if (surfaceResult.Status != PromptStatus.OK) return;

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick point for surface annotation: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d pickedPoint = ToWorld(editor, targetResult.Value);
            Point3d target;
            string contents;
            string plainDescription;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = Open<CivilSurface>(transaction, surfaceResult.ObjectId);
                    if (surface == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D surface.");
                        return;
                    }

                    double elevation = surface.FindElevationAtXY(pickedPoint.X, pickedPoint.Y);
                    target = new Point3d(pickedPoint.X, pickedPoint.Y, elevation);
                    contents = string.Join(
                        "\\P",
                        surface.Name,
                        "E: " + pickedPoint.X.ToString("N3", CultureInfo.CurrentCulture),
                        "N: " + pickedPoint.Y.ToString("N3", CultureInfo.CurrentCulture),
                        "Z: " + elevation.ToString("N3", CultureInfo.CurrentCulture));
                    plainDescription = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}; E {1:N3}; N {2:N3}; Z {3:N3}",
                        surface.Name,
                        pickedPoint.X,
                        pickedPoint.Y,
                        elevation);
                }
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_SFLABELX cancelled. The point is outside the selected surface.");
                return;
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SFLABELX cancelled. {0}", exception.Message);
                return;
            }

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_SFLABELX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDPICKX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinatePickLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptPointResult targetResult = editor.GetPoint("\nPick coordinate point: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d target = ToWorld(editor, targetResult.Value);
            string contents = FormatCoordinate(target);
            string plainDescription = string.Format(
                CultureInfo.CurrentCulture,
                "E {0:N3}; N {1:N3}; Z {2:N3}",
                target.X,
                target.Y,
                target.Z);

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_COORDPICKX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDCROSSX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateCrossLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptPointResult targetResult = editor.GetPoint("\nPick coordinate-cross point: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d target = ToWorld(editor, targetResult.Value);
            string contents = FormatCoordinate(target);
            string plainDescription = string.Format(
                CultureInfo.CurrentCulture,
                "Coordinate cross; E {0:N3}; N {1:N3}; Z {2:N3}",
                target.X,
                target.Y,
                target.Z);

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (!AddCoordinateCross(document, target, settings.TextHeight))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_COORDCROSSX complete. Cross and annotation created.");
            }
        }

        [CommandMethod("CE_TOOLS", "CE_FLLABELX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FeatureLineLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptEntityResult featureLineResult = PromptForEntity<CivilFeatureLine>(
                editor,
                "\nSelect Civil 3D feature line: ",
                "\nSelect a Civil 3D feature line.");
            if (featureLineResult.Status != PromptStatus.OK) return;

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick annotation reference point near the feature line: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d target = ToWorld(editor, targetResult.Value);
            string contents;
            string plainDescription;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = Open<CivilFeatureLine>(
                        transaction,
                        featureLineResult.ObjectId);
                    if (featureLine == null || featureLine.GetType() != typeof(CivilFeatureLine))
                    {
                        editor.WriteMessage(
                            "\nCE_FLLABELX cancelled. Select an ordinary grading feature line.");
                        return;
                    }

                    string name = string.IsNullOrWhiteSpace(featureLine.Name)
                        ? "FeatureLine " + featureLine.Handle
                        : featureLine.Name;
                    contents = string.Join(
                        "\\P",
                        name,
                        "L2D: " + featureLine.Length2D.ToString("N3", CultureInfo.CurrentCulture),
                        "L3D: " + featureLine.Length3D.ToString("N3", CultureInfo.CurrentCulture),
                        "ELEV: " + featureLine.MinElevation.ToString("N3", CultureInfo.CurrentCulture) +
                            " to " + featureLine.MaxElevation.ToString("N3", CultureInfo.CurrentCulture),
                        "GRADE: " + (featureLine.MinGrade * 100.0).ToString("N3", CultureInfo.CurrentCulture) +
                            "% to " + (featureLine.MaxGrade * 100.0).ToString("N3", CultureInfo.CurrentCulture) + "%");
                    plainDescription = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}; L2D {1:N3}; ELEV {2:N3}-{3:N3}",
                        name,
                        featureLine.Length2D,
                        featureLine.MinElevation,
                        featureLine.MaxElevation);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLLABELX cancelled. {0}", exception.Message);
                return;
            }

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                plainDescription,
                settings,
                true))
            {
                editor.WriteMessage("\nCE_FLLABELX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_CORLABELX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CorridorLabel()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            Editor editor = document.Editor;
            PromptEntityResult corridorResult = PromptForEntity<CivilCorridor>(
                editor,
                "\nSelect Civil 3D corridor: ",
                "\nSelect a Civil 3D corridor.");
            if (corridorResult.Status != PromptStatus.OK) return;

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick corridor annotation reference point: ");
            if (targetResult.Status != PromptStatus.OK) return;

            Point3d target = ToWorld(editor, targetResult.Value);
            string contents;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilCorridor corridor = Open<CivilCorridor>(transaction, corridorResult.ObjectId);
                    if (corridor == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D corridor.");
                        return;
                    }

                    int regions = 0;
                    foreach (Baseline baseline in corridor.Baselines)
                    {
                        regions += baseline.BaselineRegions.Count;
                    }

                    contents = string.Join(
                        "\\P",
                        corridor.Name,
                        "BASELINES: " + corridor.Baselines.Count.ToString(CultureInfo.InvariantCulture),
                        "REGIONS: " + regions.ToString(CultureInfo.InvariantCulture),
                        "SURFACES: " + corridor.CorridorSurfaces.Count.ToString(CultureInfo.InvariantCulture),
                        "OUT OF DATE: " + (corridor.IsOutOfDate ? "Yes" : "No"));
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_CORLABELX cancelled. {0}", exception.Message);
                return;
            }

            Point3d labelPoint;
            if (!AnnotationWriter.PromptLabelPoint(editor, targetResult.Value, settings, out labelPoint))
            {
                return;
            }

            if (AnnotationWriter.Create(
                document,
                target,
                labelPoint,
                contents,
                "Corridor annotation",
                settings,
                false))
            {
                editor.WriteMessage("\nCE_CORLABELX complete. Annotation created using {0}.", settings.Output);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKNUMBERX",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ParkingNumbering()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines to number: ");
            if (selection.Status != PromptStatus.OK) return;

            PromptResult prefixResult = editor.GetString(
                new PromptStringOptions("\nEnter bay number prefix <P>: ")
                {
                    AllowSpaces = false,
                    DefaultValue = "P",
                    UseDefaultValue = true
                });
            if (prefixResult.Status != PromptStatus.OK) return;

            PromptIntegerResult startResult = editor.GetInteger(
                new PromptIntegerOptions("\nEnter starting number <1>: ")
                {
                    AllowNone = true,
                    DefaultValue = 1,
                    UseDefaultValue = true
                });
            if (startResult.Status != PromptStatus.OK) return;

            PromptIntegerResult incrementResult = editor.GetInteger(
                new PromptIntegerOptions("\nEnter numbering increment <1>: ")
                {
                    AllowNone = true,
                    DefaultValue = 1,
                    UseDefaultValue = true
                });
            if (incrementResult.Status != PromptStatus.OK) return;
            if (incrementResult.Value == 0)
            {
                editor.WriteMessage("\nCE_PKNUMBERX cancelled. Increment cannot be zero.");
                return;
            }

            int accepted = 0;
            int skipped = 0;
            using (Transaction previewTransaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    Entity entity = OpenNumberableEntity(previewTransaction, selectedObject);
                    Point3d center;
                    if (entity == null ||
                        IsLayerLocked(previewTransaction, entity.LayerId) ||
                        !TryGetCenter(entity, out center))
                    {
                        skipped++;
                    }
                    else
                    {
                        accepted++;
                    }
                }
            }

            if (accepted == 0)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBERX cancelled. No editable blocks or closed polylines were found. Skipped={0}.",
                    skipped);
                return;
            }

            editor.WriteMessage(
                "\nCE_PKNUMBERX preview: labels={0}; skipped={1}; height={2:N1}; marker={3}.",
                accepted,
                skipped,
                settings.TextHeight,
                settings.DrawMarker ? "Yes" : "No");
            if (!Confirm(editor, "Create these parking bay numbers")) return;

            int placed = 0;
            int number = startResult.Value;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);

                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        Entity entity = OpenNumberableEntity(transaction, selectedObject);
                        Point3d center;
                        if (entity == null ||
                            IsLayerLocked(transaction, entity.LayerId) ||
                            !TryGetCenter(entity, out center))
                        {
                            continue;
                        }

                        var text = new MText();
                        text.SetDatabaseDefaults(document.Database);
                        text.LayerId = entity.LayerId;
                        text.Location = center;
                        text.Attachment = AttachmentPoint.MiddleCenter;
                        text.TextHeight = settings.TextHeight;
                        text.Contents = prefixResult.StringResult +
                            number.ToString(CultureInfo.InvariantCulture);
                        currentSpace.AppendEntity(text);
                        transaction.AddNewlyCreatedDBObject(text, true);

                        if (settings.DrawMarker)
                        {
                            AnnotationWriter.AddMarker(
                                currentSpace,
                                transaction,
                                center,
                                settings.TextHeight,
                                entity.LayerId);
                        }

                        placed++;
                        number += incrementResult.Value;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_PKNUMBERX complete. Labels placed={0}; rejected={1}.",
                    placed,
                    selection.Value.Count - placed);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PKNUMBERX cancelled. No labels were committed. {0}",
                    exception.Message);
            }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static PromptEntityResult PromptForEntity<T>(
            Editor editor,
            string message,
            string rejectMessage)
            where T : Entity
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage(rejectMessage);
            options.AddAllowedClass(typeof(T), false);
            return editor.GetEntity(options);
        }

        private static T Open<T>(Transaction transaction, ObjectId objectId)
            where T : DBObject
        {
            if (objectId.IsNull || objectId.IsErased) return null;
            return transaction.GetObject(objectId, OpenMode.ForRead, false) as T;
        }

        private static Point3d ToWorld(Editor editor, Point3d pointInCurrentUcs)
        {
            return pointInCurrentUcs.TransformBy(editor.CurrentUserCoordinateSystem);
        }

        private static string ClassifySide(double offset)
        {
            if (offset > OffsetTolerance) return "Right";
            if (offset < -OffsetTolerance) return "Left";
            return "On alignment";
        }

        private static string FormatStation(CivilAlignment alignment, double station)
        {
            try
            {
                return alignment.GetStationStringWithEquations(station);
            }
            catch
            {
                return station.ToString("N3", CultureInfo.CurrentCulture);
            }
        }

        private static void ValidateProfileStation(CivilProfile profile, double station)
        {
            if (station < profile.StartingStation - StationTolerance ||
                station > profile.EndingStation + StationTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    "station",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Station {0:N3} is outside profile range {1:N3} to {2:N3}.",
                        station,
                        profile.StartingStation,
                        profile.EndingStation));
            }
        }

        private static string FormatCoordinate(Point3d point)
        {
            return string.Join(
                "\\P",
                "E: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "N: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static bool AddCoordinateCross(
            Document document,
            Point3d target,
            double textHeight)
        {
            double halfSize = Math.Max(textHeight * 1.5, 0.001);
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);
                    AddLine(
                        currentSpace,
                        transaction,
                        new Point3d(target.X - halfSize, target.Y, target.Z),
                        new Point3d(target.X + halfSize, target.Y, target.Z));
                    AddLine(
                        currentSpace,
                        transaction,
                        new Point3d(target.X, target.Y - halfSize, target.Z),
                        new Point3d(target.X, target.Y + halfSize, target.Z));
                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_COORDCROSSX cancelled. The cross was not created. {0}",
                    exception.Message);
                return false;
            }
        }

        private static void AddLine(
            BlockTableRecord currentSpace,
            Transaction transaction,
            Point3d start,
            Point3d end)
        {
            var line = new Line(start, end);
            line.SetDatabaseDefaults();
            currentSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
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

        private static Entity OpenNumberableEntity(
            Transaction transaction,
            SelectedObject selectedObject)
        {
            if (selectedObject == null ||
                selectedObject.ObjectId.IsNull ||
                selectedObject.ObjectId.IsErased)
            {
                return null;
            }

            Entity entity = transaction.GetObject(
                selectedObject.ObjectId,
                OpenMode.ForRead,
                false) as Entity;
            if (entity is BlockReference) return entity;

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
            if (layerId.IsNull) return false;
            LayerTableRecord layer = transaction.GetObject(
                layerId,
                OpenMode.ForRead,
                false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
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
                string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal enum AnnotationOutput
    {
        MLeader,
        MText,
        Cogo
    }

    internal sealed class AnnotationOptions
    {
        public AnnotationOptions(double textHeight, bool drawMarker, AnnotationOutput output)
        {
            TextHeight = textHeight;
            DrawMarker = drawMarker;
            Output = output;
        }

        public double TextHeight { get; }
        public bool DrawMarker { get; }
        public AnnotationOutput Output { get; }
    }

    internal static class AnnotationSettingsStore
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string RecordName = "ANNOTATION_SETTINGS";
        private const string SchemaVersion = "1";

        public static bool Prepare(
            Document document,
            bool allowCogo,
            out AnnotationOptions options)
        {
            options = Read(document.Database);
            if (!allowCogo && options.Output == AnnotationOutput.Cogo)
            {
                options = new AnnotationOptions(
                    options.TextHeight,
                    options.DrawMarker,
                    AnnotationOutput.MLeader);
            }

            Editor editor = document.Editor;
            editor.WriteMessage(
                "\nCE annotation settings: height={0:N1}; marker={1}; output={2}.",
                options.TextHeight,
                options.DrawMarker ? "Yes" : "No",
                options.Output);

            var prompt = new PromptKeywordOptions(
                "\nUse these settings or edit them [Continue/Settings] <Continue>: ")
            {
                AllowNone = true
            };
            prompt.Keywords.Add("Continue");
            prompt.Keywords.Add("Settings");
            PromptResult result = editor.GetKeywords(prompt);
            if (result.Status == PromptStatus.Cancel) return false;

            if (result.Status == PromptStatus.OK &&
                string.Equals(result.StringResult, "Settings", StringComparison.OrdinalIgnoreCase))
            {
                return EditAndSave(document, allowCogo, out options);
            }

            return true;
        }

        public static bool EditAndSave(
            Document document,
            bool allowCogo,
            out AnnotationOptions options)
        {
            AnnotationOptions existing = Read(document.Database);
            Editor editor = document.Editor;

            var heightPrompt = new PromptKeywordOptions(
                "\nAnnotation text height [Small1.8/Standard2.0/Large5.0] <" +
                HeightKeyword(existing.TextHeight) + ">: ")
            {
                AllowNone = true
            };
            heightPrompt.Keywords.Add("Small");
            heightPrompt.Keywords.Add("Standard");
            heightPrompt.Keywords.Add("Large");
            PromptResult heightResult = editor.GetKeywords(heightPrompt);
            if (heightResult.Status == PromptStatus.Cancel)
            {
                options = existing;
                return false;
            }

            double height = heightResult.Status == PromptStatus.None
                ? NormalizeHeight(existing.TextHeight)
                : HeightFromKeyword(heightResult.StringResult);

            var markerPrompt = new PromptKeywordOptions(
                "\nDraw a marker circle at the reference point [Yes/No] <" +
                (existing.DrawMarker ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            markerPrompt.Keywords.Add("Yes");
            markerPrompt.Keywords.Add("No");
            PromptResult markerResult = editor.GetKeywords(markerPrompt);
            if (markerResult.Status == PromptStatus.Cancel)
            {
                options = existing;
                return false;
            }

            bool marker = markerResult.Status == PromptStatus.None
                ? existing.DrawMarker
                : string.Equals(markerResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);

            AnnotationOutput output = existing.Output;
            if (!allowCogo && output == AnnotationOutput.Cogo)
            {
                output = AnnotationOutput.MLeader;
            }

            var outputPrompt = new PromptKeywordOptions(
                allowCogo
                    ? "\nAnnotation output [MLeader/MText/COGO] <" + output + ">: "
                    : "\nAnnotation output [MLeader/MText] <" + output + ">: ")
            {
                AllowNone = true
            };
            outputPrompt.Keywords.Add("MLeader");
            outputPrompt.Keywords.Add("MText");
            if (allowCogo) outputPrompt.Keywords.Add("COGO");
            PromptResult outputResult = editor.GetKeywords(outputPrompt);
            if (outputResult.Status == PromptStatus.Cancel)
            {
                options = existing;
                return false;
            }

            if (outputResult.Status == PromptStatus.OK)
            {
                output = ParseOutput(outputResult.StringResult);
            }

            options = new AnnotationOptions(height, marker, output);
            try
            {
                Write(document.Database, options);
                return true;
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE annotation settings were not saved. {0}",
                    exception.Message);
                return false;
            }
        }

        private static AnnotationOptions Read(Database database)
        {
            AnnotationOptions defaults = new AnnotationOptions(
                NormalizeHeight(database == null ? 2.0 : database.Textsize),
                true,
                AnnotationOutput.MLeader);

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary namedObjects = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
                    {
                        return defaults;
                    }

                    DBDictionary root = transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains(RecordName)) return defaults;

                    Xrecord record = transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null) return defaults;

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string pendingKey = null;
                    foreach (TypedValue typedValue in record.Data)
                    {
                        string text = typedValue.Value as string;
                        if (text == null) continue;
                        if (pendingKey == null)
                        {
                            pendingKey = text;
                        }
                        else
                        {
                            values[pendingKey] = text;
                            pendingKey = null;
                        }
                    }

                    double height;
                    string heightText;
                    if (!values.TryGetValue("Height", out heightText) ||
                        !double.TryParse(
                            heightText,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out height))
                    {
                        height = defaults.TextHeight;
                    }

                    string markerText;
                    bool marker = !values.TryGetValue("Marker", out markerText)
                        ? defaults.DrawMarker
                        : string.Equals(markerText, "Yes", StringComparison.OrdinalIgnoreCase);

                    string outputText;
                    AnnotationOutput output = values.TryGetValue("Output", out outputText)
                        ? ParseOutput(outputText)
                        : defaults.Output;
                    return new AnnotationOptions(NormalizeHeight(height), marker, output);
                }
            }
            catch
            {
                return defaults;
            }
        }

        private static void Write(Database database, AnnotationOptions options)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);
                DBDictionary root;
                if (namedObjects.Contains(RootDictionaryName))
                {
                    root = (DBDictionary)transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    root = new DBDictionary();
                    namedObjects.SetAt(RootDictionaryName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }

                Xrecord record;
                if (root.Contains(RecordName))
                {
                    record = (Xrecord)transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    record = new Xrecord();
                    root.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, "Schema"),
                    new TypedValue((int)DxfCode.Text, SchemaVersion),
                    new TypedValue((int)DxfCode.Text, "Height"),
                    new TypedValue(
                        (int)DxfCode.Text,
                        options.TextHeight.ToString("0.0", CultureInfo.InvariantCulture)),
                    new TypedValue((int)DxfCode.Text, "Marker"),
                    new TypedValue((int)DxfCode.Text, options.DrawMarker ? "Yes" : "No"),
                    new TypedValue((int)DxfCode.Text, "Output"),
                    new TypedValue((int)DxfCode.Text, options.Output.ToString()));
                transaction.Commit();
            }
        }

        private static double NormalizeHeight(double height)
        {
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string HeightKeyword(double height)
        {
            double normalized = NormalizeHeight(height);
            if (Math.Abs(normalized - 1.8) < 0.01) return "Small";
            if (Math.Abs(normalized - 5.0) < 0.01) return "Large";
            return "Standard";
        }

        private static double HeightFromKeyword(string keyword)
        {
            if (string.Equals(keyword, "Small", StringComparison.OrdinalIgnoreCase)) return 1.8;
            if (string.Equals(keyword, "Large", StringComparison.OrdinalIgnoreCase)) return 5.0;
            return 2.0;
        }

        private static AnnotationOutput ParseOutput(string value)
        {
            if (string.Equals(value, "MText", StringComparison.OrdinalIgnoreCase))
                return AnnotationOutput.MText;
            if (string.Equals(value, "COGO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Cogo", StringComparison.OrdinalIgnoreCase))
                return AnnotationOutput.Cogo;
            return AnnotationOutput.MLeader;
        }
    }

    internal static class AnnotationWriter
    {
        public static bool PromptLabelPoint(
            Editor editor,
            Point3d targetInCurrentUcs,
            AnnotationOptions options,
            out Point3d labelPoint)
        {
            if (options.Output == AnnotationOutput.Cogo)
            {
                labelPoint = Point3d.Origin;
                return true;
            }

            var prompt = new PromptPointOptions(
                options.Output == AnnotationOutput.MText
                    ? "\nPlace annotation text: "
                    : "\nPlace annotation leader text: ")
            {
                BasePoint = targetInCurrentUcs,
                UseBasePoint = true,
                UseDashedLine = true
            };
            PromptPointResult result = editor.GetPoint(prompt);
            if (result.Status != PromptStatus.OK)
            {
                labelPoint = Point3d.Origin;
                return false;
            }

            labelPoint = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            return true;
        }

        public static bool Create(
            Document document,
            Point3d target,
            Point3d labelPoint,
            string contents,
            string plainDescription,
            AnnotationOptions options,
            bool allowCogo)
        {
            if (options.Output == AnnotationOutput.Cogo && allowCogo)
            {
                return CreateCogoPoint(
                    document,
                    target,
                    plainDescription,
                    options);
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);

                    if (options.DrawMarker)
                    {
                        AddMarker(
                            currentSpace,
                            transaction,
                            target,
                            options.TextHeight,
                            ObjectId.Null);
                    }

                    if (options.Output == AnnotationOutput.MText)
                    {
                        AddMText(
                            document.Database,
                            currentSpace,
                            transaction,
                            labelPoint,
                            contents,
                            options.TextHeight);
                    }
                    else
                    {
                        AddMLeader(
                            document.Database,
                            currentSpace,
                            transaction,
                            target,
                            labelPoint,
                            contents,
                            options.TextHeight);
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE annotation creation failed. No annotation was committed. {0}",
                    exception.Message);
                return false;
            }
        }

        public static void AddMarker(
            BlockTableRecord currentSpace,
            Transaction transaction,
            Point3d target,
            double textHeight,
            ObjectId layerId)
        {
            var marker = new Circle(
                target,
                Vector3d.ZAxis,
                Math.Max(textHeight * 0.75, 0.001));
            marker.SetDatabaseDefaults();
            if (!layerId.IsNull) marker.LayerId = layerId;
            currentSpace.AppendEntity(marker);
            transaction.AddNewlyCreatedDBObject(marker, true);
        }

        private static void AddMText(
            Database database,
            BlockTableRecord currentSpace,
            Transaction transaction,
            Point3d labelPoint,
            string contents,
            double textHeight)
        {
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.Location = labelPoint;
            text.Attachment = AttachmentPoint.MiddleLeft;
            text.TextHeight = textHeight;
            text.Contents = contents ?? string.Empty;
            currentSpace.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static void AddMLeader(
            Database database,
            BlockTableRecord currentSpace,
            Transaction transaction,
            Point3d target,
            Point3d labelPoint,
            string contents,
            double textHeight)
        {
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.Location = labelPoint;
            text.TextHeight = textHeight;
            text.Contents = contents ?? string.Empty;

            var leader = new MLeader();
            leader.SetDatabaseDefaults(database);
            leader.ContentType = ContentType.MTextContent;
            leader.MText = text;
            int leaderIndex = leader.AddLeader();
            int leaderLineIndex = leader.AddLeaderLine(leaderIndex);
            leader.AddFirstVertex(leaderLineIndex, target);
            leader.AddLastVertex(leaderLineIndex, labelPoint);
            currentSpace.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
        }

        private static bool CreateCogoPoint(
            Document document,
            Point3d target,
            string description,
            AnnotationOptions options)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage(
                    "\nCOGO output cancelled. No active Civil 3D document is available.");
                return false;
            }

            var createdIds = new List<ObjectId>();
            try
            {
                var locations = new Point3dCollection { target };
                ObjectIdCollection added = civilDocument.CogoPoints.Add(locations, true);
                foreach (ObjectId id in added) createdIds.Add(id);
                if (createdIds.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Civil 3D did not create the expected COGO point.");
                }

                civilDocument.CogoPoints.SetRawDescription(
                    createdIds,
                    new List<string>
                    {
                        string.IsNullOrWhiteSpace(description)
                            ? "CE Tools annotation"
                            : description
                    });

                if (options.DrawMarker)
                {
                    using (Transaction transaction =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                            document.Database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false);
                        AddMarker(
                            currentSpace,
                            transaction,
                            target,
                            options.TextHeight,
                            ObjectId.Null);
                        transaction.Commit();
                    }
                }

                document.Editor.WriteMessage(
                    "\nCOGO point created. Its visible label follows the drawing's current point label style.");
                return true;
            }
            catch (System.Exception exception)
            {
                TryErase(document.Database, createdIds);
                document.Editor.WriteMessage(
                    "\nCOGO output failed. Created points were removed where possible. {0}",
                    exception.Message);
                return false;
            }
        }

        private static void TryErase(Database database, IList<ObjectId> objectIds)
        {
            if (objectIds == null || objectIds.Count == 0) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in objectIds)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        DBObject databaseObject = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false);
                        if (databaseObject != null) databaseObject.Erase();
                    }
                    transaction.Commit();
                }
            }
            catch
            {
                // Preserve the original creation error.
            }
        }
    }
}
