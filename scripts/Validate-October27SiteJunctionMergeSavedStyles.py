#!/usr/bin/env python3
"""Source regressions: endpoint connector Sites, junction merge, style preservation.

This is not an Autodesk Civil 3D runtime/in-drawing verification.
"""
from pathlib import Path
root = Path(__file__).resolve().parents[1]
src = root / "src/CE.Tools.Civil3D"
read = lambda name: (src / name).read_text(encoding="utf-8")
connect = read("September04FieldGeometryCompletionCommands.cs")
merge = read("SurfaceMergeProductionCommands.cs")
preset = read("ProjectStylePresetManager.cs")
discipline = read("August11DisciplineStylePresetCommands.cs")

checks = {
    "connector popup Site selector": '"Site", "02 Output", "Connector feature-line Site"' in connect,
    "connector supports existing / new sites":
        "ReadConnectorSiteNames(document)" in connect and
        'settings.Text("NewSite")' in connect and "ResolveConnectorSite(" in connect,
    "connector feature line uses selected site":
        "CivilFeatureLine.Create(name, temporary.ObjectId, connectorSite)" in connect,
    "connector sources remain unchanged":
        'string name = "CE-ENDPOINT-CONNECTOR-"' in connect and
        "Source Lines/Polylines/FeatureLines were not changed" in connect,
    "junction merge command registered":
        '"CE_JUNCTIONSURFACEMERGE"' in merge,
    "junction source selectors include kerb/gutter":
        all(t in merge for t in (
            '"CE-JUNCTION-GRADE"', '"CE-JUNCTION-INFILL"',
            '"CE-KERB-INFILL"', '"CE-GUTTER-INFILL"')),
    "junction merged output is stable":
        'private const string JunctionOutput = "CE Junctions All"' in merge,
    "source TIN paste / surface rebuild retained":
        "TryMergeTinSurface(output, source, item.Id)" in merge and
        "output.Rebuild();" in merge,
    "junction merge uses transactional old output replacement":
        'existing.Name = "CE-JUNCTION-MERGE-REPLACED-"' in merge and
        "output.Name = JunctionOutput" in merge and
        "transaction.Commit();" in merge,
    "saved styles migrated into production presets":
        "August11DisciplineStylePresetManager.SavePreset(document.Database, preset)" in preset,
    "older drawing styles preserved before saved preset":
        "August11DisciplineStylePresetManager.SavePreset(document.Database, existing)" in preset,
    "configured production discipline does not lose its saved choices":
        "if (preset.Exists)" in discipline and
        "WriteRecord(database, ActiveRecordName, preset)" in discipline,
    "unconfigured disciplines retain isolated drawing defaults":
        "var clean = new ProjectStyleSelection" in discipline and
        "WriteRecord(database, ActiveRecordName, clean)" in discipline,
}
for name, passed in checks.items():
    if not passed: raise AssertionError(name)
    print("PASS:", name)
menus = [
    "August11ProductionCentreCommands.cs",
    "August13RoadProductionCentres.cs",
    "August14StructuredDisciplineProductionCentres.cs",
    "ProductionWorkflowCommands.cs",
]
for menu in menus:
    assert '"CE_JUNCTIONSURFACEMERGE"' in read(menu), menu
    print("PASS: junction merge present in", menu)
