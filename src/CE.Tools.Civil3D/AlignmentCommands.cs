using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.AlignmentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Read-only alignment reporting and station/offset lookup tools, plus a
    /// lightweight MLeader annotation workflow.
    /// </summary>
    public sealed class AlignmentCommands
    {
        private const string ReportKeyword = "Report";
        private const string StationOffsetKeyword = "StationOffset";
        private const string LabelKeyword = "Label";
        private const double OffsetZeroTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_ALTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AlignmentTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nAlignment tool [Report/StationOffset/Label] <Report>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(ReportKeyword);
            options.Keywords.Add(StationOffsetKeyword);
            options.Keywords.Add(LabelKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? ReportKeyword
                : result.StringResult;

            if (string.Equals(mode, StationOffsetKeyword, StringComparison.OrdinalIgnoreCase))
            {
                ReportStationOffset(document);
            }
            else if (string.Equals(mode, LabelKeyword, StringComparison.OrdinalIgnoreCase))
            {
                PlaceStationOffsetLabel(document);
            }
            else
            {
                ReportAlignments(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_ALREPORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AlignmentReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportAlignments(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_ALSTOFF",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void AlignmentStationOffset()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportStationOffset(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_ALLABEL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void AlignmentStationOffsetLabel()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                PlaceStationOffsetLabel(document);
            }
        }

        private static void ReportAlignments(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetAlignmentSelection(
                editor,
                "\nSelect Civil 3D alignments to report: ");
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
                    CivilAlignment alignment = OpenAlignment(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId);
                    if (alignment == null)
                    {
                        skipped++;
                        continue;
                    }

                    counted++;
                    totalLength += alignment.Length;

                    string startStation = FormatStation(alignment, alignment.StartingStation);
                    string endStation = FormatStation(alignment, alignment.EndingStation);
                    string site = string.IsNullOrWhiteSpace(alignment.SiteName)
                        ? "<Siteless>"
                        : alignment.SiteName;
                    int profileCount = alignment.GetProfileIds().Count;

                    editor.WriteMessage(
                        "\n  {0}: Type={1}; Start={2}; End={3}; Length={4:N3}; " +
                        "Site={5}; Style={6}; Profiles={7}; Reference={8}",
                        alignment.Name,
                        alignment.AlignmentType,
                        startStation,
                        endStation,
                        alignment.Length,
                        site,
                        alignment.StyleName,
                        profileCount,
                        alignment.IsReferenceObject ? "Yes" : "No");
                }
            }

            editor.WriteMessage(
                "\nCE_ALREPORT complete. Alignments: {0}; skipped: {1}; total length: {2:N3}.",
                counted,
                skipped,
                totalLength);
        }

        private static void ReportStationOffset(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult alignmentResult = PromptForAlignment(editor);
            if (alignmentResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nPick point for station and offset: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            Point3d point = ToWorld(editor, pointResult.Value);
            Database database = document.Database;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilAlignment alignment = OpenAlignment(transaction, alignmentResult.ObjectId);
                    if (alignment == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D alignment.");
                        return;
                    }

                    StationOffsetResult result = CalculateStationOffset(alignment, point);
                    editor.WriteMessage(
                        "\nCE_ALSTOFF — Alignment={0}; Station={1}; Raw={2:N3}; " +
                        "Offset={3:N3}; Side={4}; X={5:N3}; Y={6:N3}.",
                        alignment.Name,
                        result.DisplayStation,
                        result.RawStation,
                        result.SignedOffset,
                        result.Side,
                        point.X,
                        point.Y);
                }
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nThe picked point falls beyond the start or end range of the selected alignment.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_ALSTOFF cancelled. {0}", exception.Message);
            }
        }

        private static void PlaceStationOffsetLabel(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult alignmentResult = PromptForAlignment(editor);
            if (alignmentResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptPointResult targetResult = editor.GetPoint(
                "\nPick point for station/offset label: ");
            if (targetResult.Status != PromptStatus.OK)
            {
                return;
            }

            var labelOptions = new PromptPointOptions("\nPlace station/offset label: ")
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

            Point3d target = ToWorld(editor, targetResult.Value);
            Point3d label = ToWorld(editor, labelResult.Value);
            Database database = document.Database;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilAlignment alignment = OpenAlignment(transaction, alignmentResult.ObjectId);
                    if (alignment == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a Civil 3D alignment.");
                        return;
                    }

                    StationOffsetResult result = CalculateStationOffset(alignment, target);
                    string contents = string.Join(
                        "\\P",
                        alignment.Name,
                        "STA: " + result.DisplayStation,
                        "OFF: " + Math.Abs(result.SignedOffset).ToString("N3", CultureInfo.CurrentCulture) +
                            " " + result.Side);

                    AddMLeader(database, transaction, target, label, contents);
                    transaction.Commit();

                    editor.WriteMessage(
                        "\nCE_ALLABEL complete. Alignment={0}; Station={1}; Offset={2:N3} {3}.",
                        alignment.Name,
                        result.DisplayStation,
                        Math.Abs(result.SignedOffset),
                        result.Side);
                }
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                editor.WriteMessage(
                    "\nCE_ALLABEL cancelled. The picked point falls beyond the alignment range; no label was created.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_ALLABEL cancelled. No label was committed. {0}",
                    exception.Message);
            }
        }

        private static StationOffsetResult CalculateStationOffset(
            CivilAlignment alignment,
            Point3d point)
        {
            double station = 0.0;
            double offset = 0.0;
            alignment.StationOffset(point.X, point.Y, ref station, ref offset);

            string side;
            if (offset > OffsetZeroTolerance)
            {
                side = "Right";
            }
            else if (offset < -OffsetZeroTolerance)
            {
                side = "Left";
            }
            else
            {
                side = "On alignment";
            }

            return new StationOffsetResult(
                station,
                offset,
                side,
                FormatStation(alignment, station));
        }

        private static string FormatStation(CivilAlignment alignment, double rawStation)
        {
            try
            {
                return alignment.GetStationStringWithEquations(rawStation);
            }
            catch
            {
                return rawStation.ToString("N3", CultureInfo.CurrentCulture);
            }
        }

        private static PromptEntityResult PromptForAlignment(Editor editor)
        {
            var options = new PromptEntityOptions("\nSelect Civil 3D alignment: ");
            options.SetRejectMessage("\nSelect a Civil 3D alignment.");
            options.AddAllowedClass(typeof(CivilAlignment), false);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetAlignmentSelection(
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

        private static CivilAlignment OpenAlignment(
            Transaction transaction,
            ObjectId objectId)
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

        private sealed class StationOffsetResult
        {
            public StationOffsetResult(
                double rawStation,
                double signedOffset,
                string side,
                string displayStation)
            {
                RawStation = rawStation;
                SignedOffset = signedOffset;
                Side = side;
                DisplayStation = displayStation;
            }

            public double RawStation { get; }
            public double SignedOffset { get; }
            public string Side { get; }
            public string DisplayStation { get; }
        }
    }
}
