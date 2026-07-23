using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Read-only profile reporting and station-elevation inquiry tools, plus a
    /// lightweight plan MLeader annotation workflow.
    /// </summary>
    public sealed class ProfileCommands
    {
        private const string ReportKeyword = "Report";
        private const string ElevationKeyword = "Elevation";
        private const string LabelKeyword = "Label";
        private const double StationTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PRTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfileTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nProfile tool [Report/Elevation/Label] <Report>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(ReportKeyword);
            options.Keywords.Add(ElevationKeyword);
            options.Keywords.Add(LabelKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? ReportKeyword
                : result.StringResult;

            if (string.Equals(mode, ElevationKeyword, StringComparison.OrdinalIgnoreCase))
            {
                ReportElevation(document);
            }
            else if (string.Equals(mode, LabelKeyword, StringComparison.OrdinalIgnoreCase))
            {
                PlaceProfileLabel(document);
            }
            else
            {
                ReportProfiles(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PRREPORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfileReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportProfiles(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PRELEV",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileElevation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportElevation(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PRLABEL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileLabel()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                PlaceProfileLabel(document);
            }
        }

        private static void ReportProfiles(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetProfileSelection(
                editor,
                "\nSelect Civil 3D profiles to report: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int counted = 0;
            int skipped = 0;
            double totalLength = 0.0;
            Database database = document.Database;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilProfile profile = OpenProfile(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId);
                    if (profile == null)
                    {
                        skipped++;
                        continue;
                    }

                    CivilAlignment alignment = OpenAlignment(transaction, profile.AlignmentId);
                    counted++;
                    totalLength += profile.Length;

                    string alignmentName = alignment == null ? "<Unknown>" : alignment.Name;
                    string startStation = FormatStation(alignment, profile.StartingStation);
                    string endStation = FormatStation(alignment, profile.EndingStation);
                    int pviCount = GetPviCount(profile);

                    editor.WriteMessage(
                        "\n  {0}: Type={1}; Alignment={2}; Start={3}; End={4}; " +
                        "Length={5:N3}; Style={6}; Update={7}; PVIs={8}; Reference={9}",
                        profile.Name,
                        profile.ProfileType,
                        alignmentName,
                        startStation,
                        endStation,
                        profile.Length,
                        profile.StyleName,
                        profile.UpdateMode,
                        pviCount,
                        profile.IsReferenceObject ? "Yes" : "No");
                }
            }

            editor.WriteMessage(
                "\nCE_PRREPORT complete. Profiles: {0}; skipped: {1}; total length: {2:N3}.",
                counted,
                skipped,
                totalLength);
        }

        private static void ReportElevation(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult profileResult = PromptForProfile(editor);
            if (profileResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptDoubleResult stationResult = PromptForStation(editor);
            if (stationResult.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilProfile profile = OpenProfile(transaction, profileResult.ObjectId);
                    if (profile == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D profile.");
                        return;
                    }

                    ValidateStation(profile, stationResult.Value);
                    CivilAlignment alignment = OpenAlignment(transaction, profile.AlignmentId);
                    double elevation = profile.ElevationAt(stationResult.Value);
                    double grade = profile.GradeAt(stationResult.Value);

                    editor.WriteMessage(
                        "\nCE_PRELEV — Profile={0}; Alignment={1}; Station={2}; Raw={3:N3}; " +
                        "Elevation={4:N3}; Grade={5:N3}%.",
                        profile.Name,
                        alignment == null ? "<Unknown>" : alignment.Name,
                        FormatStation(alignment, stationResult.Value),
                        stationResult.Value,
                        elevation,
                        grade * 100.0);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_PRELEV cancelled. {0}", exception.Message);
            }
        }

        private static void PlaceProfileLabel(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult profileResult = PromptForProfile(editor);
            if (profileResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptDoubleResult stationResult = PromptForStation(editor);
            if (stationResult.Status != PromptStatus.OK)
            {
                return;
            }

            Database database = document.Database;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilProfile profile = OpenProfile(transaction, profileResult.ObjectId);
                    if (profile == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D profile.");
                        return;
                    }

                    ValidateStation(profile, stationResult.Value);
                    CivilAlignment alignment = OpenAlignment(transaction, profile.AlignmentId);
                    if (alignment == null)
                    {
                        editor.WriteMessage("\nCE_PRLABEL cancelled. The profile's parent alignment could not be opened.");
                        return;
                    }

                    double easting = 0.0;
                    double northing = 0.0;
                    alignment.PointLocation(stationResult.Value, 0.0, ref easting, ref northing);
                    Point3d target = new Point3d(easting, northing, 0.0);
                    Point3d targetInUcs = target.TransformBy(editor.CurrentUserCoordinateSystem.Inverse());

                    var labelOptions = new PromptPointOptions("\nPlace profile station/elevation label: ")
                    {
                        BasePoint = targetInUcs,
                        UseBasePoint = true,
                        UseDashedLine = true
                    };
                    PromptPointResult labelResult = editor.GetPoint(labelOptions);
                    if (labelResult.Status != PromptStatus.OK)
                    {
                        return;
                    }

                    Point3d label = labelResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                    double elevation = profile.ElevationAt(stationResult.Value);
                    double grade = profile.GradeAt(stationResult.Value);
                    string contents = string.Join(
                        "\\P",
                        profile.Name,
                        "STA: " + FormatStation(alignment, stationResult.Value),
                        "ELEV: " + elevation.ToString("N3", CultureInfo.CurrentCulture),
                        "GRADE: " + (grade * 100.0).ToString("N3", CultureInfo.CurrentCulture) + "%");

                    AddMLeader(database, transaction, target, label, contents);
                    transaction.Commit();

                    editor.WriteMessage(
                        "\nCE_PRLABEL complete. Profile={0}; Station={1}; Elevation={2:N3}; Grade={3:N3}%.",
                        profile.Name,
                        FormatStation(alignment, stationResult.Value),
                        elevation,
                        grade * 100.0);
                }
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_PRLABEL cancelled. The station is outside the parent alignment range; no label was created.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PRLABEL cancelled. No label was committed. {0}",
                    exception.Message);
            }
        }

        private static PromptDoubleResult PromptForStation(Editor editor)
        {
            return editor.GetDouble(
                new PromptDoubleOptions("\nEnter raw profile station: ")
                {
                    AllowNegative = true,
                    AllowZero = true,
                    AllowNone = false
                });
        }

        private static PromptEntityResult PromptForProfile(Editor editor)
        {
            var options = new PromptEntityOptions("\nSelect Civil 3D profile: ");
            options.SetRejectMessage("\nSelect a Civil 3D profile.");
            options.AddAllowedClass(typeof(CivilProfile), false);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetProfileSelection(Editor editor, string message)
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

        private static CivilProfile OpenProfile(Transaction transaction, ObjectId objectId)
        {
            if (objectId.IsNull)
            {
                return null;
            }

            return transaction.GetObject(
                objectId,
                OpenMode.ForRead,
                false) as CivilProfile;
        }

        private static CivilAlignment OpenAlignment(Transaction transaction, ObjectId objectId)
        {
            if (objectId.IsNull)
            {
                return null;
            }

            return transaction.GetObject(
                objectId,
                OpenMode.ForRead,
                false) as CivilAlignment;
        }

        private static void ValidateStation(CivilProfile profile, double station)
        {
            if (station < profile.StartingStation - StationTolerance ||
                station > profile.EndingStation + StationTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(station),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Station {0:N3} is outside profile range {1:N3} to {2:N3}.",
                        station,
                        profile.StartingStation,
                        profile.EndingStation));
            }
        }

        private static int GetPviCount(CivilProfile profile)
        {
            try
            {
                return profile.PVIs == null ? 0 : profile.PVIs.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatStation(CivilAlignment alignment, double rawStation)
        {
            if (alignment == null)
            {
                return rawStation.ToString("N3", CultureInfo.CurrentCulture);
            }

            try
            {
                return alignment.GetStationStringWithEquations(rawStation);
            }
            catch
            {
                return rawStation.ToString("N3", CultureInfo.CurrentCulture);
            }
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
    }
}
