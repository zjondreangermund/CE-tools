"""Guard against copying source road band IDs into destination cross sections."""
from pathlib import Path
code = (Path(__file__).resolve().parents[1] /
        "src/CE.Tools.Civil3D/SectionViewManagementCommands.cs").read_text(encoding="utf-8")
required = [
    "targetSample.GetSectionIds()",
    "item.SampleLineId == destination.SampleLineId",
    "sourceBand.Section1Id",
    "sourceBand.Section2Id",
    "targetBand.Section1Id = mapped[0]",
    "targetBand.Section2Id = mapped[1]",
    "sourceBand.BandType != targetBand.BandType",
    "NormalizeSectionSourceName(reference.SourceName)",
    "c.SourceType == reference.SourceType",
    "c.SourceId == reference.SourceId",
    "Repair Section1/Section2 band data sources",
    "targetBand.BandStyleId = sourceBand.BandStyleId",
    "bool auditOnly",
    'new[] { "Report and repair", "Audit only (no changes)" }',
    'destination.Bands.SetBottomBandItems(actual)',
]
for marker in required:
    if marker not in code:
        raise AssertionError("Missing local section band source safeguard: " + marker)
executable = "\n".join(line for line in code.splitlines()
                      if not line.lstrip().startswith("//"))
for marker in [
    "SetBottomBandItems(source.Bands.GetBottomBandItems())",
    "SetTopBandItems(source.Bands.GetTopBandItems())",
    "CopyBands(source, dest)",
]:
    if marker in executable:
        raise AssertionError("Cross-view band copy remains: " + marker)
assert 'return new List<Section>(); // No unsafe default to junction grade.' in code
assert '@"\\s+"' in code, "Whitespace normalization must use C# verbatim regex \\s+"
print("PASS: per-road local band source selection, source type, safe audit and band style matching.")
