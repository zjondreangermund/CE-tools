using CETools.Civil3D;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS: " + message); }
CorridorRegionSnapshot S(string handle, double start, double end, bool enabled = false, string name = "") =>
    new() { CorridorHandle = handle, Start = start, End = end, Enabled = enabled, CorridorName = name, BaselineEnabled = false };
var saved = new List<CorridorRegionSnapshot> { S("OLD", 0, 100), S("OLD", 100, 200, true) };
var live = new List<CorridorRegionSnapshot> { S("NEW", 0, 100), S("NEW", 100, 200) };
Check(CorridorRegionSnapshot.Find(live[0], saved, live).Single() == saved[0], "Legacy recreated handle recovers by complete unique signature");
Check(CorridorRegionSnapshot.Find(live[1], saved, live).Single().Enabled, "Mixed enabled flags remain distinct");
live[0].Region = 9;
Check(CorridorRegionSnapshot.Find(live[0], saved, live).Count == 1, "Reordered region index does not lose state");
var ambiguous = live.Concat(new[] { S("OTHER", 0, 100), S("OTHER", 100, 200) }).ToList();
Check(CorridorRegionSnapshot.Find(live[0], saved, ambiguous).Count == 0, "Ambiguous legacy corridor signatures are not guessed");
var named = new List<CorridorRegionSnapshot> { S("OLD", 0, 200, false, "Road 1") };
var split = new List<CorridorRegionSnapshot> { S("NEW", 0, 100, true, "Road 1"), S("NEW", 100, 200, true, "Road 1") };
Check(CorridorRegionSnapshot.Find(split[0], named, split).Single() == named[0], "Split region inherits original saved state");
var mixed = new List<CorridorRegionSnapshot> { S("OLD", 0, 100, false, "Road 1"), S("OLD", 100, 200, true, "Road 1") };
var merged = new List<CorridorRegionSnapshot> { S("NEW", 0, 200, true, "Road 1") };
Check(CorridorRegionSnapshot.Find(merged[0], mixed, merged).Count == 0, "Merged regions with conflicting flags remain unresolved");
var gap = new List<CorridorRegionSnapshot> { S("OLD", 0, 90, false, "Road 1"), S("OLD", 100, 200, false, "Road 1") };
Check(CorridorRegionSnapshot.Find(merged[0], gap, merged).Count == 0, "Gap in saved coverage is not restored");
var encoded = S("A", 1.25, 2.75, true, "Road | A"); encoded.BaselineName = "BL-1"; encoded.RegionName = "Region|1";
var decoded = CorridorRegionSnapshot.Parse(encoded.Encode());
Check(decoded.CorridorName == encoded.CorridorName && decoded.RegionName == encoded.RegionName && decoded.Enabled, "Named snapshot round trip escapes delimiters");
Check(CorridorRegionSnapshot.Parse("A|0|0|1.25|2.75|0") != null, "Six-field legacy snapshots still load");
var centres = new List<double> { 10, 40, 80, 120, 190 };
Check(BranchLabelRepeatPlan.Select(centres, 0, 200, false, 300, "Every pipe").SequenceEqual(centres), "Threshold OFF places every pipe even below 300m");
Check(BranchLabelRepeatPlan.Select(centres, 0, 200, false, 300, "Every second pipe").SequenceEqual(new[] { 10d, 80d, 190d }), "Every second pipe has no extra run-centre label");
var spaced = BranchLabelRepeatPlan.Select(centres, 0, 200, false, 300, "Consistent 50 m");
Check(spaced.All(centres.Contains) && spaced.Count == spaced.Distinct().Count(), "50m interval anchors snap to unique pipe centres");
Check(BranchLabelRepeatPlan.Select(centres, 0, 200, true, 300, "Every pipe").Count == 1, "Threshold ON retains one central pipe label for short run");
Check(BranchLabelRepeatPlan.Select(new[] { 5d }, 0, 10, false, 300, "Consistent 50 m").Single() == 5, "Short pipes still receive a 50m-mode label");
Check(new[] { "BRANCH-10", "BRANCH-2", "BRANCH-1" }.OrderBy(BranchLabelRepeatPlan.BranchNumber).SequenceEqual(new[] { "BRANCH-1", "BRANCH-2", "BRANCH-10" }), "Numeric branch tree ordering");

mixed[0].RegionName = merged[0].RegionName = "Retained name";
Check(CorridorRegionSnapshot.Find(merged[0], mixed, merged).Count == 0, "Retained region name cannot override conflicting merged flags");
