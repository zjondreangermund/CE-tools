using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadDriveReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary road-drive and geometry-error review from a Civil 3D alignment
    /// and profile. Source Civil objects remain read-only. The workflow does not
    /// replace formal geometric design, sight-distance, superelevation, collision,
    /// corridor or vehicle-dynamics analysis.
    /// </summary>
    public sealed class RoadDriveReviewCommands
    {
        private const string RegAppName = "CE_ROAD_DRIVE_REVIEW";
        private const string ReviewLayer = "CE-ROAD-DRIVE-REVIEW";
        private const int MaximumSamples = 100000;
        private const int MaximumIssueLabels = 500;

        [CommandMethod("CE_TOOLS", "CE_ROADDRIVETOOLS", CommandFlags.Modal)]
        public void RoadDriveTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nRoad-drive tools [Review/Export/Info/Clear] <Review>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[] { "Review", "Export", "Info", "Clear" })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Review";
            string command = Equal(choice, "Export")
                ? "CE_ROADDRIVEEXPORT "
                : Equal(choice, "Info")
                    ? "CE_ROADDRIVEINFO "
                    : Equal(choice, "Clear")
                        ? "CE_ROADDRIVECLEAR "
                        : "CE_ROADDRIVEREVIEW ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDRIVEREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReviewRoadDrive()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            RoadDriveSource source;
            if (!PromptSource(document, out source)) return;
            RoadDriveInput input;
            if (!PromptReviewInput(editor, out input)) return;

            try
            {
                List<RoadDriveSample> samples = ReadSamples(
                    document.Database,
                    source,
                    input.SampleIntervalMetres);
                RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                    samples,
                    input.Criteria);
                List<IList<string>> rows = BuildReviewRows(source, input, analysis);
                string subtitle = string.Format(
                    CultureInfo.CurrentCulture,
                    "Alignment={0}; profile={1}; samples={2}; issues={3}; speed={4:N1} km/h. Results are preliminary design screening only.",
                    source.AlignmentName,
                    source.ProfileName,
                    analysis.Samples.Count,
                    analysis.Issues.Count,
                    input.Criteria.DesignSpeedKilometresPerHour);

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Drive and Design Review",
                    subtitle,
                    rows,
                    "CE TOOLS ROAD DRIVE REVIEW");

                if (!PromptYesNo(editor, "Create the 3D drive path and issue markers", true))
                    return;

                int created = CreateReviewGraphics(
                    document.Database,
                    source,
                    input,
                    analysis);
                editor.Regen();

                if (PromptYesNo(editor, "Export the review and camera path to Excel", true))
                {
                    string path;
                    if (PromptExcelPath(editor, "CE-Tools-Road-Drive-Review.xlsx", out path))
                    {
                        SimpleXlsxWriter.Write(path, "Road Drive", rows);
                        editor.WriteMessage(
                            "\nCE_ROADDRIVEREVIEW workbook created: {0}",
                            path);
                    }
                }

                editor.WriteMessage(
                    "\nCE_ROADDRIVEREVIEW complete. Samples={0}; issues={1}; graphics={2}; maximum grade={3:N3}%; minimum radius={4}.",
                    analysis.Samples.Count,
                    analysis.Issues.Count,
                    created,
                    analysis.MaximumAbsoluteGradePercent,
                    analysis.MinimumHorizontalRadiusMetres.HasValue
                        ? analysis.MinimumHorizontalRadiusMetres.Value.ToString("N3", CultureInfo.CurrentCulture) + " m"
                        : "Straight/undefined");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_ROADDRIVEREVIEW failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDRIVEEXPORT", CommandFlags.Modal)]
        public void ExportCameraPath()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            RoadDriveSource source;
            if (!PromptSource(document, out source)) return;
            double interval;
            if (!PromptPositiveDouble(editor, "Camera path sampling interval (m) <5>: ", 5.0, out interval))
                return;

            var saveOptions = new PromptSaveFileOptions(
                "\nChoose the road-drive camera-path CSV: ")
            {
                Filter = "Comma-separated values (*.csv)|*.csv",
                DialogCaption = "Export CE Tools Road Drive Camera Path",
                InitialFileName = "CE-Road-Drive-Camera-Path.csv"
            };
            PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
            if (fileResult.Status != PromptStatus.OK) return;
            string path = EnsureExtension(fileResult.StringResult, ".csv");
            if (File.Exists(path))
            {
                editor.WriteMessage(
                    "\nCE_ROADDRIVEEXPORT stopped. Existing files are not overwritten.");
                return;
            }

            try
            {
                List<RoadDriveSample> samples = ReadSamples(
                    document.Database,
                    source,
                    interval);
                var neutralCriteria = new RoadDriveCriteria(
                    60.0,
                    1000.0,
                    1000.0,
                    0.0,
                    10.0,
                    2.5,
                    3.4);
                RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
                    samples,
                    neutralCriteria);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("Station,X,Y,Z,HeadingDegrees,PitchDegrees,Alignment,Profile");
                    foreach (RoadCameraFrame frame in analysis.CameraFrames)
                    {
                        writer.WriteLine(string.Join(",", new[]
                        {
                            frame.Station.ToString("R", CultureInfo.InvariantCulture),
                            frame.X.ToString("R", CultureInfo.InvariantCulture),
                            frame.Y.ToString("R", CultureInfo.InvariantCulture),
                            frame.Z.ToString("R", CultureInfo.InvariantCulture),
                            frame.HeadingDegrees.ToString("R", CultureInfo.InvariantCulture),
                            frame.PitchDegrees.ToString("R", CultureInfo.InvariantCulture),
                            Csv(source.AlignmentName),
                            Csv(source.ProfileName)
                        }));
                    }
                }
                editor.WriteMessage(
                    "\nCE_ROADDRIVEEXPORT complete. Camera frames={0}; file={1}. Verify coordinate system, units, camera height/target and external-visualisation conventions before use.",
                    analysis.CameraFrames.Count,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_ROADDRIVEEXPORT failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDRIVEINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadDriveInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = document.Editor.GetEntity(
                "\nSelect a CE Tools road-drive path, marker or label: ");
            if (result.Status != PromptStatus.OK) return;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                Dictionary<string, string> values = ReadTag(entity);
                if (values == null)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADDRIVEINFO: the selected object is not a CE Tools road-drive review graphic.");
                    return;
                }

                var rows = values
                    .OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(item => (IList<string>)new List<string> { item.Key, item.Value })
                    .ToList();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Drive Review Information",
                    "Stored source and screening metadata for the selected generated review object.",
                    new List<string> { "Property", "Value" },
                    rows,
                    "CE TOOLS ROAD DRIVE REVIEW INFORMATION");
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDRIVECLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearRoadDriveReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            if (!PromptYesNo(editor, "Erase all CE Tools road-drive review graphics in the current space", false))
            {
                editor.WriteMessage("\nCE_ROADDRIVECLEAR cancelled.");
                return;
            }

            int erased = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (ReadTag(entity) == null) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                        erased++;
                    }
                }
                transaction.Commit();
            }
            editor.Regen();
            editor.WriteMessage(
                "\nCE_ROADDRIVECLEAR complete. Erased review graphics={0}. Alignments, profiles, corridors and unrelated objects were unchanged.",
                erased);
        }

        private static bool PromptSource(Document document, out RoadDriveSource source)
        {
            source = null;
            var options = new PromptEntityOptions(
                "\nSelect a Civil 3D road alignment: ");
            options.SetRejectMessage("\nSelect a Civil 3D alignment.");
            options.AddAllowedClass(typeof(CivilAlignment), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;

            List<CivilObjectChoice> profiles;
            string alignmentName;
            string alignmentHandle;
            double startStation;
            double endStation;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilAlignment alignment = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                if (alignment == null) return false;
                alignmentName = alignment.Name;
                alignmentHandle = alignment.Handle.ToString();
                startStation = alignment.StartingStation;
                endStation = alignment.EndingStation;
                profiles = alignment.GetProfileIds()
                    .Cast<ObjectId>()
                    .Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as CivilProfile)
                    .Where(profile => profile != null)
                    .Select(profile => new CivilObjectChoice(
                        profile.ObjectId,
                        profile.Name,
                        profile.GetType().Name))
                    .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            if (profiles.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nRoad-drive review stopped. The selected alignment has no Civil 3D profiles.");
                return false;
            }

            var picker = new CivilObjectPickerWindow(
                "CE Tools - Road Drive Profile",
                "Select the design profile used to build the 3D drive path.",
                profiles);
            AcApplication.ShowModalWindow(picker);
            if (!picker.Accepted || picker.SelectedChoice == null) return false;

            string profileHandle;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilProfile profile = transaction.GetObject(
                    picker.SelectedChoice.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilProfile;
                if (profile == null) return false;
                profileHandle = profile.Handle.ToString();
            }

            source = new RoadDriveSource(
                result.ObjectId,
                picker.SelectedChoice.ObjectId,
                alignmentName,
                picker.SelectedChoice.Name,
                alignmentHandle,
                profileHandle,
                startStation,
                endStation);
            return true;
        }

        private static bool PromptReviewInput(Editor editor, out RoadDriveInput input)
        {
            input = null;
            double interval;
            double speed;
            double maxGrade;
            double maxGradeChange;
            double minimumRadius;
            double lateralRatio;
            double reactionTime;
            double braking;
            double markerRadius;
            if (!PromptPositiveDouble(editor, "Sampling interval (m) <5>: ", 5.0, out interval) ||
                !PromptPositiveDouble(editor, "Design speed (km/h) <60>: ", 60.0, out speed) ||
                !PromptPositiveDouble(editor, "Maximum absolute grade (%) <8>: ", 8.0, out maxGrade) ||
                !PromptPositiveDouble(editor, "Maximum grade change (% points per 100 m) <6>: ", 6.0, out maxGradeChange) ||
                !PromptNonNegativeDouble(editor, "Minimum horizontal radius (m) <0 = speed-based only>: ", 0.0, out minimumRadius) ||
                !PromptPositiveDouble(editor, "Maximum lateral acceleration ratio (g) <0.25>: ", 0.25, out lateralRatio) ||
                !PromptPositiveDouble(editor, "Driver reaction time (s) <2.5>: ", 2.5, out reactionTime) ||
                !PromptPositiveDouble(editor, "Braking deceleration (m/s2) <3.4>: ", 3.4, out braking) ||
                !PromptPositiveDouble(editor, "Issue marker radius (drawing units) <1>: ", 1.0, out markerRadius))
                return false;

            input = new RoadDriveInput(
                interval,
                markerRadius,
                new RoadDriveCriteria(
                    speed,
                    maxGrade,
                    maxGradeChange,
                    minimumRadius,
                    lateralRatio,
                    reactionTime,
                    braking));
            return true;
        }

        private static List<RoadDriveSample> ReadSamples(
            Database database,
            RoadDriveSource source,
            double interval)
        {
            var samples = new List<RoadDriveSample>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilAlignment alignment = transaction.GetObject(
                    source.AlignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                CivilProfile profile = transaction.GetObject(
                    source.ProfileId,
                    OpenMode.ForRead,
                    false) as CivilProfile;
                if (alignment == null || profile == null)
                    throw new InvalidOperationException("The selected alignment/profile could not be reopened.");

                double length = Math.Max(0.0, source.EndStation - source.StartStation);
                int intervals = Math.Max(2, (int)Math.Ceiling(length / interval));
                if (intervals + 1 > MaximumSamples)
                    throw new InvalidOperationException(
                        "The requested path exceeds the " + MaximumSamples.ToString("N0", CultureInfo.InvariantCulture) +
                        "-sample safety limit. Increase the sampling interval.");

                for (int index = 0; index <= intervals; index++)
                {
                    double station = index == intervals
                        ? source.EndStation
                        : source.StartStation + length * index / intervals;
                    double easting = 0.0;
                    double northing = 0.0;
                    alignment.PointLocation(station, 0.0, ref easting, ref northing);
                    double elevation = profile.ElevationAt(station);
                    samples.Add(new RoadDriveSample(
                        station,
                        easting,
                        northing,
                        elevation));
                }
            }
            return samples;
        }

        private static List<IList<string>> BuildReviewRows(
            RoadDriveSource source,
            RoadDriveInput input,
            RoadDriveAnalysis analysis)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "CATEGORY", "STATION", "TYPE", "VALUE", "LIMIT", "SEVERITY", "MESSAGE" },
                new List<string> { "Summary", string.Empty, "Alignment", source.AlignmentName, string.Empty, string.Empty, string.Empty },
                new List<string> { "Summary", string.Empty, "Profile", source.ProfileName, string.Empty, string.Empty, string.Empty },
                new List<string> { "Summary", string.Empty, "Samples", analysis.Samples.Count.ToString(CultureInfo.InvariantCulture), MaximumSamples.ToString(CultureInfo.InvariantCulture), string.Empty, string.Empty },
                new List<string> { "Summary", string.Empty, "Maximum absolute grade", Format(analysis.MaximumAbsoluteGradePercent), Format(input.Criteria.MaximumAbsoluteGradePercent), string.Empty, "%" },
                new List<string> { "Summary", string.Empty, "Minimum horizontal radius", FormatOptional(analysis.MinimumHorizontalRadiusMetres), Format(analysis.RequiredHorizontalRadiusMetres), string.Empty, "m" },
                new List<string> { "Summary", string.Empty, "Maximum lateral acceleration ratio", Format(analysis.MaximumLateralAccelerationRatio), Format(input.Criteria.MaximumLateralAccelerationRatio), string.Empty, "g ratio" },
                new List<string> { "Summary", string.Empty, "Maximum grade change", Format(analysis.MaximumGradeChangePercentPer100Metres), Format(input.Criteria.MaximumGradeChangePercentPer100Metres), string.Empty, "% points / 100 m" },
                new List<string> { "Summary", string.Empty, "Stopping sight distance reference", Format(analysis.StoppingSightDistanceMetres), string.Empty, string.Empty, "m; terrain/obstruction visibility not modelled" }
            };

            if (analysis.Issues.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "Issue", string.Empty, "None", string.Empty, string.Empty, "OK",
                    "No sampled-path issues exceeded the selected screening criteria."
                });
            }
            else
            {
                foreach (RoadDriveIssue issue in analysis.Issues)
                {
                    rows.Add(new List<string>
                    {
                        "Issue",
                        Format(issue.Station),
                        issue.Type.ToString(),
                        Format(issue.Value),
                        Format(issue.Limit),
                        issue.Severity.ToString(),
                        issue.Message
                    });
                }
            }

            rows.Add(new List<string> { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty });
            rows.Add(new List<string> { "CAMERA PATH", "STATION", "X", "Y", "Z", "HEADING", "PITCH" });
            foreach (RoadCameraFrame frame in analysis.CameraFrames)
            {
                rows.Add(new List<string>
                {
                    "Frame",
                    Format(frame.Station),
                    Format(frame.X),
                    Format(frame.Y),
                    Format(frame.Z),
                    Format(frame.HeadingDegrees),
                    Format(frame.PitchDegrees)
                });
            }
            rows.Add(new List<string>
            {
                "Boundary", string.Empty, "Design assistance only", string.Empty, string.Empty, string.Empty,
                "Verify official standards, design speed, superelevation, vertical curves, sight distance, terrain/obstructions, corridor clearances and vehicle behaviour."
            });
            return rows;
        }

        private static int CreateReviewGraphics(
            Database database,
            RoadDriveSource source,
            RoadDriveInput input,
            RoadDriveAnalysis analysis)
        {
            int created = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(database, transaction);
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                var points = new Point3dCollection(
                    analysis.Samples.Select(sample => new Point3d(sample.X, sample.Y, sample.Z)).ToArray());
                var path = new Polyline3d(Poly3dType.SimplePoly, points, false);
                path.SetDatabaseDefaults(database);
                path.LayerId = layerId;
                path.ColorIndex = 256;
                space.AppendEntity(path);
                transaction.AddNewlyCreatedDBObject(path, true);
                WriteTag(path, "Path", source, input, analysis.Issues.Count, null);
                created++;

                int labelled = 0;
                foreach (RoadDriveIssue issue in analysis.Issues)
                {
                    RoadDriveSample sample = analysis.Samples
                        .OrderBy(item => Math.Abs(item.Station - issue.Station))
                        .First();
                    short colour = issue.Severity == RoadDriveSeverity.Error
                        ? (short)1
                        : issue.Severity == RoadDriveSeverity.Warning
                            ? (short)30
                            : (short)2;
                    var marker = new Circle(
                        new Point3d(sample.X, sample.Y, sample.Z),
                        Vector3d.ZAxis,
                        input.MarkerRadius);
                    marker.SetDatabaseDefaults(database);
                    marker.LayerId = layerId;
                    marker.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
                    space.AppendEntity(marker);
                    transaction.AddNewlyCreatedDBObject(marker, true);
                    WriteTag(marker, "Issue", source, input, analysis.Issues.Count, issue);
                    created++;

                    if (labelled >= MaximumIssueLabels) continue;
                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.LayerId = layerId;
                    label.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
                    label.Location = new Point3d(
                        sample.X + input.MarkerRadius * 1.5,
                        sample.Y + input.MarkerRadius * 1.5,
                        sample.Z);
                    label.TextHeight = Math.Max(0.1, input.MarkerRadius * 0.8);
                    label.Width = Math.Max(10.0, input.MarkerRadius * 20.0);
                    label.Contents = string.Format(
                        CultureInfo.CurrentCulture,
                        "STA {0:N3}\\P{1}: {2:N3} / limit {3:N3}",
                        issue.Station,
                        issue.Type,
                        issue.Value,
                        issue.Limit);
                    space.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);
                    WriteTag(label, "IssueLabel", source, input, analysis.Issues.Count, issue);
                    created++;
                    labelled++;
                }
                transaction.Commit();
            }
            return created;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            if (table.Has(ReviewLayer)) return table[ReviewLayer];
            table.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = ReviewLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 2),
                IsPlottable = true
            };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void WriteTag(
            Entity entity,
            string type,
            RoadDriveSource source,
            RoadDriveInput input,
            int issueCount,
            RoadDriveIssue issue)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Schema=1"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Type=" + type),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Alignment=" + source.AlignmentName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Profile=" + source.ProfileName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "AlignmentHandle=" + source.AlignmentHandle),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "ProfileHandle=" + source.ProfileHandle),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "SampleInterval=" + input.SampleIntervalMetres.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "DesignSpeed=" + input.Criteria.DesignSpeedKilometresPerHour.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "IssueCount=" + issueCount.ToString(CultureInfo.InvariantCulture))
            };
            if (issue != null)
            {
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "IssueType=" + issue.Type));
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Station=" + issue.Station.ToString("R", CultureInfo.InvariantCulture)));
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Value=" + issue.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Limit=" + issue.Limit.ToString("R", CultureInfo.InvariantCulture)));
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Severity=" + issue.Severity));
            }
            entity.XData = new ResultBuffer(values.ToArray());
        }

        private static Dictionary<string, string> ReadTag(Entity entity)
        {
            if (entity == null) return null;
            ResultBuffer buffer = entity.GetXDataForApplication(RegAppName);
            if (buffer == null) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            return values;
        }

        private static bool PromptPositiveDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && IsFinite(value) && value > 0.0;
        }

        private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && IsFinite(value) && value >= 0.0;
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : Equal(result.StringResult, "Yes");
        }

        private static bool PromptExcelPath(Editor editor, string initialName, out string path)
        {
            var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Road Drive Review",
                InitialFileName = initialName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            path = result.Status == PromptStatus.OK
                ? EnsureExtension(result.StringResult, ".xlsx")
                : string.Empty;
            return result.Status == PromptStatus.OK;
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static string FormatOptional(double? value)
        {
            return value.HasValue ? Format(value.Value) : string.Empty;
        }

        private static string EnsureExtension(string path, string extension)
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class RoadDriveSource
    {
        public RoadDriveSource(
            ObjectId alignmentId,
            ObjectId profileId,
            string alignmentName,
            string profileName,
            string alignmentHandle,
            string profileHandle,
            double startStation,
            double endStation)
        {
            AlignmentId = alignmentId;
            ProfileId = profileId;
            AlignmentName = alignmentName;
            ProfileName = profileName;
            AlignmentHandle = alignmentHandle;
            ProfileHandle = profileHandle;
            StartStation = startStation;
            EndStation = endStation;
        }

        public ObjectId AlignmentId { get; private set; }
        public ObjectId ProfileId { get; private set; }
        public string AlignmentName { get; private set; }
        public string ProfileName { get; private set; }
        public string AlignmentHandle { get; private set; }
        public string ProfileHandle { get; private set; }
        public double StartStation { get; private set; }
        public double EndStation { get; private set; }
    }

    internal sealed class RoadDriveInput
    {
        public RoadDriveInput(
            double sampleIntervalMetres,
            double markerRadius,
            RoadDriveCriteria criteria)
        {
            SampleIntervalMetres = sampleIntervalMetres;
            MarkerRadius = markerRadius;
            Criteria = criteria;
        }

        public double SampleIntervalMetres { get; private set; }
        public double MarkerRadius { get; private set; }
        public RoadDriveCriteria Criteria { get; private set; }
    }
}
