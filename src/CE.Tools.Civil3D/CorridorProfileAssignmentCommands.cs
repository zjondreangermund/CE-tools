using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.CorridorProfileAssignmentCommands))]

namespace CETools.Civil3D
{
    public sealed class CorridorProfileAssignmentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CORRIDORPROFILESMULTI", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AssignDesignProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            var profiles = new List<ProfileEntry>();
            var assemblies = new List<CivilChoice>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        var profile = tr.GetObject(profileId, OpenMode.ForRead, false) as CivilProfile;
                        if (profile == null || profile.ProfileType != ProfileType.FG ||
                            profile.Name.StartsWith("CE_BAND_SRC_", StringComparison.OrdinalIgnoreCase)) continue;
                        profiles.Add(new ProfileEntry
                        {
                            Id = profileId, AlignmentId = alignmentId,
                            Display = alignment.Name + " | " + profile.Name + " [" + profileId.Handle + "]"
                        });
                    }
                }
                foreach (ObjectId id in CivilAssemblyResolver.GetAssemblyIds(civil, document.Database))
                {
                    var assembly = tr.GetObject(id, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Assembly;
                    if (assembly != null) assemblies.Add(new CivilChoice(id, assembly.Name));
                }
            }
            if (profiles.Count == 0)
            {
                document.Editor.WriteMessage("\nNo design (FG) profiles were found.");
                return;
            }
            IList<CivilChoice> picked = FieldCompletionBatchUi.PickMultiple("CE Tools - Select Design Profiles",
                "Select the design profiles to assign. Each profile keeps its parent alignment.",
                profiles.Select(item => new CivilChoice(item.Id, item.Display)));
            if (picked == null) return;
            var ids = new HashSet<ObjectId>(picked.Select(item => item.Id));
            profiles = profiles.Where(item => ids.Contains(item.Id)).ToList();
            IList<CivilChoice> corridors = FieldCompletionBatchUi.PickMultiple("CE Tools - Select Corridors",
                "Select the existing corridors to update or add baselines to.",
                FieldCompletionBatchUi.ReadCorridorChoices(document, civil));
            if (corridors == null || corridors.Count == 0) return;

            const string update = "Update matching existing baselines";
            const string add = "Add selected profiles as new baselines";
            const string skip = "<Skip this baseline>";
            var settings = new ProductionSettingsDialogModel("CE Tools - Assign Design Profiles to Corridors",
                "Update mode matches the baseline alignment. Add mode adds every selected profile as a new baseline to every selected corridor, with one assembly region over the profile's station range.");
            settings.AddChoice("Mode", "01 Operation", "Operation", update,
                "Existing corridor geometry is retained. Existing alignment/profile pairs are not duplicated in Add mode.", new[] { update, add });
            settings.AddChoice("Assembly", "02 New baselines only", "Assembly for new regions",
                assemblies.Count == 0 ? "<No assembly available>" : assemblies[0].Name,
                "Used only in Add mode. Configure required surface targets with Assign Corridor Settings afterwards.",
                assemblies.Count == 0 ? new[] { "<No assembly available>" } : assemblies.Select(item => item.Name));
            settings.AddChoice("Rebuild", "03 Rebuild", "Rebuild changed corridors", "Yes",
                "Rebuild each changed corridor once after all its profile assignments.", new[] { "Yes", "No" });

            var assignments = new List<Assignment>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (CivilChoice choice in corridors)
                {
                    var corridor = tr.GetObject(choice.Id, OpenMode.ForRead, false) as Corridor;
                    if (corridor == null) continue;
                    for (int index = 0; index < corridor.Baselines.Count; index++)
                    {
                        Baseline baseline = corridor.Baselines[index];
                        ObjectId alignmentId = AlignmentId(baseline);
                        List<ProfileEntry> candidates = profiles.Where(item => item.AlignmentId == alignmentId).ToList();
                        if (candidates.Count == 0) continue;
                        string key = "Baseline" + assignments.Count.ToString(CultureInfo.InvariantCulture);
                        string initial = candidates.Count == 1 ? candidates[0].Display : skip;
                        settings.AddChoice(key, "04 Existing baseline assignments", choice.Name + " / " + baseline.Name,
                            initial, "Select a profile belonging to this baseline alignment. Used only in Update mode.",
                            new[] { skip }.Concat(candidates.Select(item => item.Display)));
                        assignments.Add(new Assignment { CorridorId = choice.Id, Index = index, Key = key });
                    }
                }
            }
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            bool addMode = settings.Text("Mode") == add;
            CivilChoice selectedAssembly = assemblies.FirstOrDefault(item => item.Name == settings.Text("Assembly"));
            if (addMode && selectedAssembly == null)
            {
                document.Editor.WriteMessage("\nCreate or import an assembly before adding new baseline regions.");
                return;
            }

            var rows = new List<IList<string>>();
            using (DocumentLock documentLock = document.LockDocument())
            foreach (CivilChoice choice in corridors)
            {
                int changed = 0;
                int unchanged = 0;
                try
                {
                    using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                    {
                        var corridor = tr.GetObject(choice.Id, OpenMode.ForWrite, false) as Corridor;
                        if (corridor == null || corridor.IsReferenceObject)
                            throw new InvalidOperationException("Corridor is unavailable or read-only.");
                        if (addMode)
                        {
                            foreach (ProfileEntry entry in profiles)
                            {
                                bool exists = corridor.Baselines.Cast<Baseline>().Any(existing =>
                                    AlignmentId(existing) == entry.AlignmentId && existing.ProfileId == entry.Id);
                                if (exists) { unchanged++; continue; }
                                var profile = (CivilProfile)tr.GetObject(entry.Id, OpenMode.ForRead);
                                if (profile.EndingStation <= profile.StartingStation)
                                    throw new InvalidOperationException("Profile has no usable station range: " + entry.Display);
                                string name = "CE-" + entry.Id.Handle;
                                var names = new HashSet<string>(corridor.Baselines.Cast<Baseline>().Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
                                string root = name;
                                int suffix = 2;
                                while (names.Contains(name)) name = root + "-" + suffix++;
                                Baseline baseline = corridor.Baselines.Add(name, entry.AlignmentId, entry.Id);
                                baseline.BaselineRegions.Add(name + "-Region", selectedAssembly.Name,
                                    profile.StartingStation, profile.EndingStation);
                                changed++;
                            }
                        }
                        else
                        {
                            foreach (Assignment assignment in assignments.Where(item => item.CorridorId == choice.Id))
                            {
                                ProfileEntry entry = profiles.FirstOrDefault(item => item.Display == settings.Text(assignment.Key));
                                if (entry == null) { unchanged++; continue; }
                                Baseline baseline = corridor.Baselines[assignment.Index];
                                if (baseline.ProfileId == entry.Id) { unchanged++; continue; }
                                var profile = (CivilProfile)tr.GetObject(entry.Id, OpenMode.ForRead);
                                foreach (BaselineRegion region in baseline.BaselineRegions)
                                    if (region.StartStation < profile.StartingStation - 0.001 || region.EndStation > profile.EndingStation + 0.001)
                                        throw new InvalidOperationException("Profile does not cover all existing regions: " + entry.Display);
                                baseline.SetAlignmentAndProfile(entry.AlignmentId, entry.Id);
                                if (baseline.ProfileId != entry.Id)
                                    throw new InvalidOperationException("Civil 3D did not retain the selected profile.");
                                baseline.NeedsProcessing = true;
                                changed++;
                            }
                        }
                        if (changed > 0 && settings.Text("Rebuild") == "Yes") corridor.Rebuild();
                        tr.Commit();
                    }
                    rows.Add(new[] { choice.Name, changed.ToString(), unchanged.ToString(), changed == 0 ? "No matching assignment changed" : "Applied" });
                }
                catch (System.Exception exception)
                {
                    rows.Add(new[] { choice.Name, "0", "0", "Rolled back: " + exception.Message });
                }
            }
            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(document, "CE Tools - Corridor Profile Assignments",
                "Assignments are committed independently per corridor. Review skipped or rolled-back corridors below.",
                new[] { "CORRIDOR", "CHANGED", "UNCHANGED / SKIPPED", "STATUS" }, rows, "CE CORRIDOR PROFILE ASSIGNMENTS");
        }

        private static ObjectId AlignmentId(Baseline baseline)
        {
            try { return baseline.AlignmentId; }
            catch { return ObjectId.Null; }
        }
        private sealed class ProfileEntry { internal ObjectId Id; internal ObjectId AlignmentId; internal string Display; }
        private sealed class Assignment { internal ObjectId CorridorId; internal int Index; internal string Key; }
    }
}
