using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProfileSectionCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadProfileSectionCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADPROFILEVIEWSPLIT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SplitProfileViews()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument =
                CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            var options = new PromptEntityOptions(
                "\nSelect an existing road profile view as template: ");
            options.SetRejectMessage(
                "\nSelect a Civil 3D profile view.");
            options.AddAllowedClass(
                typeof(CivilProfileView),
                false);
            PromptEntityResult picked =
                document.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK)
                return;

            ObjectId alignmentId;
            ObjectId styleId;
            double alignmentStart;
            double alignmentEnd;
            string alignmentName;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                CivilProfileView template = transaction.GetObject(
                    picked.ObjectId,
                    OpenMode.ForRead,
                    false) as CivilProfileView;
                if (template == null)
                    return;

                alignmentId = template.AlignmentId;
                styleId = template.StyleId;
                CivilAlignment alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                if (alignment == null)
                    return;

                alignmentStart = alignment.StartingStation;
                alignmentEnd = alignment.EndingStation;
                alignmentName = alignment.Name;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Split Road Profile Views",
                "Create consecutive profile views such as 0.000-750.000, " +
                "750.000-1500.000 and continue to alignment end. " +
                "Zero chainage is valid and saved values become the default " +
                "for the next drawing/session.");
            // Start/end are intentionally Text fields. PositiveDouble rejects
            // zero, but 0+000 is a valid Civil 3D station and must be accepted.
            settings.AddText(
                "Start",
                "01 Stations",
                "Start station",
                alignmentStart.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture),
                "First station. Zero is valid.");
            settings.AddText(
                "End",
                "01 Stations",
                "End station",
                alignmentEnd.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture),
                "Last station.");
            settings.AddPositiveDouble(
                "Length",
                "01 Stations",
                "Section length",
                750.0,
                "Length per view.");
            settings.AddPositiveDouble(
                "Spacing",
                "02 Placement",
                "Horizontal spacing",
                250.0,
                "Drawing-unit spacing.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            double requestedStart;
            double requestedEnd;
            if (!ProductionSettingsDialogModel.TryDouble(
                    settings.Text("Start"),
                    out requestedStart) ||
                !ProductionSettingsDialogModel.TryDouble(
                    settings.Text("End"),
                    out requestedEnd))
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADPROFILEVIEWSPLIT cancelled. " +
                    "Enter valid numeric start/end stations; zero is allowed.");
                return;
            }

            double start =
                Math.Max(alignmentStart, requestedStart);
            double end =
                Math.Min(alignmentEnd, requestedEnd);
            double length =
                Math.Max(
                    0.001,
                    settings.Double("Length", 750.0));
            double spacing =
                Math.Max(
                    0.001,
                    settings.Double("Spacing", 250.0));

            if (end <= start + 1e-9)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADPROFILEVIEWSPLIT cancelled. " +
                    "End station must be greater than start station.");
                return;
            }

            PromptPointResult insertion =
                document.Editor.GetPoint(
                    "\nPick insertion point for first section profile view: ");
            if (insertion.Status != PromptStatus.OK)
                return;

            int created = 0;
            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                int index = 0;
                for (double station = start;
                     station < end - 0.000001;
                     station += length)
                {
                    double sectionEnd =
                        Math.Min(station + length, end);
                    ObjectId viewId =
                        CivilProfileView.Create(
                            alignmentId,
                            new Point3d(
                                insertion.Value.X +
                                    index * spacing,
                                insertion.Value.Y,
                                insertion.Value.Z));
                    CivilProfileView view = transaction.GetObject(
                        viewId,
                        OpenMode.ForWrite,
                        false) as CivilProfileView;
                    if (view == null)
                        continue;

                    view.StyleId = styleId;
                    view.StationRangeMode =
                        StationRangeType.UserSpecified;
                    view.StationStart = station;
                    view.StationEnd = sectionEnd;
                    try
                    {
                        view.Name = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}-STA-{1:0.000}-{2:0.000}",
                            alignmentName,
                            station,
                            sectionEnd);
                    }
                    catch { }

                    created++;
                    index++;
                }

                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADPROFILEVIEWSPLIT complete. " +
                "Section profile views={0}; start={1:0.###}; " +
                "end={2:0.###}; length={3:0.###}.",
                created,
                start,
                end,
                length);
        }
    }
}
