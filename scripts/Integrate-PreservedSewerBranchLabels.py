#!/usr/bin/env python3
"""Wire preserved repeated/offset branch labels into CE_SEWALIGN source.

This is intentionally deterministic and fails if the expected legacy midpoint
block is absent, so it cannot silently overwrite a newer implementation.
"""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "src" / "CE.Tools.Civil3D" / "SewerBranchAlignmentCommands.cs"

OLD = """                        Point3d labelPoint = GetMidpoint(branch.PlanPoints);
                        var label = new MText();
                        label.SetDatabaseDefaults(database);
                        label.LayerId = layerId;
                        label.Location = labelPoint;
                        label.Attachment = AttachmentPoint.MiddleCenter;
                        label.TextHeight = GetTextHeight(database);
                        label.Contents = branch.BranchName;
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(branchKey, \"Label\");
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        labelsCreated++;
"""

NEW = """                        IReadOnlyList<SewerBranchLabelPlacement.Placement> placements =
                            SewerBranchLabelPlacement.BuildPlacements(branch.PlanPoints);
                        double paperHeight = SewerBranchLabelPlacement.DefaultPaperHeight;
                        bool placeAbove = (branch.BranchNumber % 2) != 0;

                        foreach (SewerBranchLabelPlacement.Placement placement in placements)
                        {
                            var label = new MText();
                            label.SetDatabaseDefaults(database);
                            label.LayerId = layerId;
                            SewerBranchLabelPlacement.ConfigureLabel(
                                label,
                                database,
                                placement,
                                branch.BranchName,
                                paperHeight,
                                placeAbove);
                            label.XData = BuildTag(branchKey, \"Label\");
                            modelSpace.AppendEntity(label);
                            transaction.AddNewlyCreatedDBObject(label, true);
                            labelsCreated++;
                        }
"""


def main() -> int:
    if not TARGET.exists():
        print(f"Missing target: {TARGET.relative_to(ROOT)}", file=sys.stderr)
        return 1

    text = TARGET.read_text(encoding="utf-8")
    if NEW in text:
        print("Preserved sewer branch labels are already integrated.")
        return 0

    count = text.count(OLD)
    if count != 1:
        print(
            "Expected exactly one legacy midpoint-label block, "
            f"but found {count}. Refusing to modify source.",
            file=sys.stderr,
        )
        return 1

    TARGET.write_text(text.replace(OLD, NEW), encoding="utf-8")
    print("Integrated scale-aware repeated sewer branch labels into CE_SEWALIGN.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
