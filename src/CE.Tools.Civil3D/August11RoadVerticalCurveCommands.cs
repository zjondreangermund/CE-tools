using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.August11RoadVerticalCurveCommands))]

namespace CETools.Civil3D
{
    public sealed class August11RoadVerticalCurveCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEBESTFIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BestFitFinalProfile()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADVERTICALCURVES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AddVerticalCurves()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Final Road Vertical Curves",
                "Add free symmetric parabolic vertical curves to eligible internal PVIs on CE final-design road profiles. Existing end tangents remain; PVIs that already carry a vertical curve are skipped safely.");
            model.AddChoice("Scope", "01 Profiles", "Final road profiles", "All CE final profiles", "Process every CE road -FG/final profile or only profiles belonging to selected road alignments.", new[] { "All CE final profiles", "Selected road alignments" });
            model.AddPositiveDouble("Length", "02 Vertical Curves", "Preferred vertical curve length", 30.0, "Preferred parabolic curve length. CE Tools shortens it automatically where adjacent PVI spacing is insufficient.");
            model.AddPositiveDouble("Minimum", "02 Vertical Curves", "Minimum curve length", 6.0, "Do not add a curve if the available tangent geometry cannot support at least this length.");
            model.AddPositiveDouble("Fit", "02 Vertical Curves", "Maximum share of adjacent tangent spacing (%)", 80.0, "Limits curve length to this percentage of the smaller adjacent PVI spacing.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            HashSet<ObjectId> selectedAlignments = null;
            if (string.Equals(model.Text("Scope"), "Selected road alignments", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect CE road alignments whose final profiles should receive vertical curves: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                selectedAlignments = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }

            double preferred = Math.Max(0.1, model.Double("Length", 30.0));
            double minimum = Math.Max(0.1, model.Double("Minimum", 6.0));
            double share = Math.Max(10.0, Math.Min(95.0, model.Double("Fit", 80.0))) / 100.0;
            int profiles = 0;
            int curves = 0;
            int skipped = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    if (selectedAlignments != null && !selectedAlignments.Contains(alignmentId)) continue;
                    CivilAlignment alignment;
                    try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { continue; }
                    if (alignment == null || !IsRoadAlignment(alignment)) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile;
                        try { profile = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile; }
                        catch { continue; }
                        if (profile == null || !IsFinalProfile(profile)) continue;
                        profiles++;
                        int before = curves;
                        List<ProfilePVI> pvis = CivilStyleDiscovery.Enumerate(profile.PVIs)
                            .OfType<ProfilePVI>()
                            .OrderBy(item => SafeStation(item))
                            .ToList();
                        if (pvis.Count < 3)
                        {
                            skipped++;
                            rows.Add(new List<string> { alignment.Name, profile.Name, "0", "Fewer than 3 PVIs" });
                            continue;
                        }
                        for (int index = 1; index < pvis.Count - 1; index++)
                        {
                            ProfilePVI previousPvi = pvis[index - 1];
                            ProfilePVI currentPvi = pvis[index];
                            ProfilePVI nextPvi = pvis[index + 1];
                            double previousSpacing = SafeStation(currentPvi) - SafeStation(previousPvi);
                            double nextSpacing = SafeStation(nextPvi) - SafeStation(currentPvi);
                            double available = Math.Max(0.0, Math.Min(previousSpacing, nextSpacing) * share);
                            double length = Math.Min(preferred, available);
                            if (length < minimum)
                            {
                                skipped++;
                                continue;
                            }
                            try
                            {
                                profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(currentPvi, length);
                                curves++;
                            }
                            catch (System.InvalidOperationException)
                            {
                                // End PVI or curve already attached: keep existing design.
                                skipped++;
                            }
                            catch
                            {
                                skipped++;
                            }
                        }
                        rows.Add(new List<string>
                        {
                            alignment.Name,
                            profile.Name,
                            (curves - before).ToString(CultureInfo.CurrentCulture),
                            "Tangents + PVI parabolas"
                        });
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Final Road Vertical Curves",
                string.Format(CultureInfo.CurrentCulture, "Final road profiles={0}; vertical curves added={1}; skipped/already curved={2}.", profiles, curves, skipped),
                new List<string> { "Road", "Profile", "Curves Added", "Geometry" },
                rows,
                "CE TOOLS FINAL ROAD VERTICAL CURVE REGISTER");
        }

        private static bool IsRoadAlignment(CivilAlignment alignment)
        {
            string name = alignment.Name ?? string.Empty;
            string description = alignment.Description ?? string.Empty;
            return name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("ROAD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFinalProfile(CivilProfile profile)
        {
            string name = profile.Name ?? string.Empty;
            string description = profile.Description ?? string.Empty;
            return name.EndsWith("-FG", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("FINAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   description.IndexOf("CE final road profile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static double SafeStation(ProfilePVI pvi)
        {
            try { return pvi.Station; }
            catch { return double.NaN; }
        }
    }
}
