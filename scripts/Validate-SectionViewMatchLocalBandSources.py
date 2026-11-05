"""Static regression checks for preserving per-sample-line section-view band sources."""
from pathlib import Path

code = (Path(__file__).resolve().parents[1] /
        "src/CE.Tools.Civil3D/SectionViewManagementCommands.cs").read_text(encoding="utf-8")
required = [
    "destination.SampleLineId",
    "targetSample.GetSectionIds()",
    "section.SampleLineId == destination.SampleLineId",
    "sourceBand.Section1Id",
    "sourceBand.Section2Id",
    "destinationBand.Section1Id = match.ObjectId",
    "destinationBand.Section2Id = match.ObjectId",
    "sourceBand.BandType != destinationBand.BandType",
    "NormalizeSectionSourceName(reference.SourceName)",
    "unresolvedBandSources",
    "Repair Section1/Section2 band data sources",
    'new[] { "ON", "OFF" }',
]
for marker in required:
    if marker not in code:
        raise AssertionError("Missing local Section1/Section2 data-binding safety: " + marker)
unsafe = [
    "SetBottomBandItems(source.Bands.GetBottomBandItems())",
    "SetTopBandItems(source.Bands.GetTopBandItems())",
    "CopyBands(source, dest)",
]
# Exclude comments from unsafe call checks; documentation may include examples.
executable = "\n".join(line for line in code.splitlines()
                      if not line.lstrip().startswith("//"))
for marker in unsafe:
    if marker in executable:
        raise AssertionError("Cross-view data band copy remains: " + marker)
print("PASS: SectionView Match rebinds only source IDs from each destination's own sample line.")
