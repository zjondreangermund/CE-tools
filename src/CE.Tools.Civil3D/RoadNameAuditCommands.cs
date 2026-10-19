using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadNameAuditCommands))]
namespace CETools.Civil3D
{
    /// <summary>Audit authoritative alignment identity against actual dependent object IDs.
    /// Never infer ownership from a road number or rename by selection order.</summary>
    public sealed class RoadNameAuditCommands
    {
        private static readonly Regex RoadToken =
            new Regex(@"(?<![A-Za-z0-9])(?:RD|ROAD)[-_ ]*0*([0-9]+)(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [CommandMethod("CE_TOOLS", "CE_ROADNAMEAUDIT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Audit() { AuditActive(false); }

        internal static void AuditActive(bool quiet)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return;
            int alignments = 0, profiles = 0, views = 0, corridors = 0, warnings = 0;
            var lines = new List<string>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var alignmentById = new Dictionary<ObjectId, CivilAlignment>();
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment a = null;
                    try { a = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { }
                    if (a == null || !RoadToken.IsMatch(a.Name ?? "")) continue;
                    alignments++;
                    alignmentById[id] = a;
                }
                // Civil 3D treats ROAD-10 and RD-10 as different names, but
                // both represent road number 10. Report the collision before
                // anyone runs a synchronization or renumbering command.
                foreach (var collision in alignmentById.Values
                    .GroupBy(a => Number(a.Name))
                    .Where(g => g.Key >= 0 && g.Count() > 1))
                {
                    warnings++;
                    lines.Add("DUPLICATE ROAD NUMBER " + collision.Key.ToString("00") +
                        ": " + string.Join(", ", collision.Select(a => a.Name)) +
                        ". Resolve conflicting alignments before renumbering.");
                }
                foreach (CivilAlignment a in alignmentById.Values)
                {
                    var number = Number(a.Name);
                    foreach (ObjectId id in a.GetProfileIds())
                    {
                        CivilProfile p = null;
                        try { p = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfile; }
                        catch { }
                        if (p == null) continue;
                        profiles++;
                        if (!MatchesNumber(p.Name, number))
                        {
                            warnings++;
                            lines.Add("PROFILE: " + p.Name + " belongs to " + a.Name +
                                " (number mismatch or name contains no road number)");
                        }
                    }
                    foreach (ObjectId id in a.GetProfileViewIds())
                    {
                        CivilProfileView v = null;
                        try { v = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView; }
                        catch { }
                        if (v == null) continue;
                        views++;
                        if (!MatchesNumber(v.Name, number))
                        {
                            warnings++;
                            lines.Add("PROFILE VIEW: " + v.Name + " belongs to " + a.Name +
                                " (number mismatch or name contains no road number)");
                        }
                    }
                }
                foreach (ObjectId id in civil.CorridorCollection)
                {
                    Corridor c = null;
                    try { c = tr.GetObject(id, OpenMode.ForRead, false) as Corridor; }
                    catch { }
                    if (c == null) continue;
                    corridors++;
                    var owners = new HashSet<int>();
                    foreach (Baseline b in c.Baselines)
                    {
                        CivilAlignment a;
                        if (!alignmentById.TryGetValue(b.AlignmentId, out a)) continue;
                        owners.Add(Number(a.Name));
                    }
                    if (owners.Count > 1)
                    {
                        warnings++;
                        lines.Add("SHARED CORRIDOR: " + c.Name +
                            " contains multiple road numbers (" + string.Join(", ", owners.OrderBy(n => n)) +
                            "); cannot safely rename the corridor by one road");
                    }
                    else if (owners.Count == 1 && !MatchesNumber(c.Name, owners.First()))
                    {
                        warnings++;
                        lines.Add("CORRIDOR: " + c.Name + " belongs to RD-" +
                            owners.First().ToString("00") + " (name mismatch)");
                    }
                }
            }
            if (!quiet || warnings > 0)
            {
                doc.Editor.WriteMessage(
                    "\nCE_ROADNAMEAUDIT: road alignments={0}, profiles={1}, profile views={2}, corridors={3}, naming warnings={4}.",
                    alignments, profiles, views, corridors, warnings);
                foreach (string line in lines.Take(100))
                    doc.Editor.WriteMessage("\n  " + line);
                if (warnings > 100)
                    doc.Editor.WriteMessage("\n  {0} more warnings omitted.", warnings - 100);
                if (warnings > 0)
                    doc.Editor.WriteMessage(
                        "\nNo identities were changed. Review mismatches before renaming: objects are linked by Civil 3D IDs, not by their displayed names.");
            }
        }

        private static int Number(string value)
        {
            Match m = RoadToken.Match(value ?? "");
            int number;
            return m.Success && int.TryParse(m.Groups[1].Value, out number) ? number : -1;
        }
        private static bool MatchesNumber(string name, int expected)
        {
            var matches = RoadToken.Matches(name ?? "");
            return expected >= 0 && matches.Count > 0 &&
                matches.Cast<Match>().All(m => {
                    int number;
                    return int.TryParse(m.Groups[1].Value, out number) && number == expected;
                });
        }
    }
}
