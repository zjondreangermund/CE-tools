using CETools.Civil3D;
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
const string diameter = "Larger pipes, then longer branches";
Check(WaterBranchPriority.Compare(250, 50, 10, "A", 110, 500, 50, "B", diameter) < 0, "Large trunk precedes longer small branch");
Check(WaterBranchPriority.Compare(160, 500, 10, "A", 160, 50, 50, "B", diameter) < 0, "Equal diameter uses longest branch");
Check(WaterBranchPriority.Compare(160, 500, 20, "A", 160, 500, 10, "B", diameter) < 0, "Equal size and length uses highest elevation");
Check(WaterBranchPriority.Compare(110, 500, 10, "A", 250, 50, 50, "B", "Longer branches, then larger pipes") < 0, "Explicit length priority overrides size");
Check(WaterBranchPriority.Compare(110, 50, 100, "A", 250, 500, 50, "B", "Highest elevation first") < 0, "Explicit elevation priority");
Check(WaterBranchPriority.Compare(160, 500, 20, "A", 160, 500, 20, "B", diameter) < 0, "Stable identity resolves equal priorities");
Check(!WaterBranchPriority.PreferCandidate(0, 0, true), "Shared junction retains priority trunk");
Check(WaterBranchPriority.PreferCandidate(0, 1, true), "Geometrically closer branch wins away from junction");
Check(!WaterBranchPriority.PreferCandidate(6, 5, false), "Out-of-tolerance part is unmatched");
Check(WaterBranchPriority.PreferCandidate(5, 5, false), "Inclusive tolerance accepts first route");
