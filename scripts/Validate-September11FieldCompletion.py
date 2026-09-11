from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "src/CE.Tools.Civil3D/September11FieldCompletionCommands.cs").read_text(encoding="utf-8")
menu = (root / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
front = (root / "src/CE.Tools.Civil3D/September09FieldEngineeringCommandFrontDoor.cs").read_text(encoding="utf-8")
project = (root / "src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs").read_text(encoding="utf-8")

required_source = [
    '"CE_ROADCENTRECLEAN"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHBOUNDARIES"',
    '"CE_SEWRECALC"',
    '"CE_SURVEYLOCATIONNAMIBIA"',
    'Separate line strings',
    '<LineString>',
    'FeatureLinePointType.AllPoints',
    'Each hatch loop will become its OWN closed boundary polyline',
    'Adjacent/touching hatches remain separate',
    'September10SewerAuditRuntime.LinkExistingPartsToSurface',
    'document.SendStringToExecute("CE_SEWPROFILE "',
    'Surface/rule writes are committed before any profile command starts',
    'VerifyReplacements',
    'Straight intermediate vertices are removed; bend and T/X junction vertices are retained',
]
for token in required_source:
    if token not in source:
        raise SystemExit(f"September 11 source marker missing: {token}")

if 'September11FieldCompletionRuntime.RoadReserveCentrePolylines(document);' not in front:
    raise SystemExit("Road Reserve front door is not routed through the final centreline cleanup wrapper.")

required_menu = [
    '"CE_FIELDCOMPLETION"',
    '"CE_ROADRESERVECENTRELINES"',
    '"CE_SEWRECALC"',
    '"CE_SURVEYLOCATIONNAMIBIA"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHBOUNDARIES"',
]
for token in required_menu:
    if token not in menu:
        raise SystemExit(f"Field-completion menu marker missing: {token}")

# The town workflow existed before this batch; keep it guarded because the field
# completion menu deliberately reuses that single canonical Namibia mapping.
for town, zone in [
    ("Windhoek", "LO17"),
    ("Walvis Bay", "LO15"),
    ("Henties Bay", "LO15"),
    ("Katima Mulilo", "LO25"),
    ("Opuwo", "LO13"),
]:
    marker = '{ "' + town + '", "' + zone + '" }'
    if marker not in project:
        raise SystemExit(f"Namibia town mapping missing: {town} -> {zone}")

# The profile handoff must stay queued after the September10 surface/rule helper;
# do not re-introduce direct/nested profile creation inside the recalculation command.
if 'new SewerProductionCommands().CreateProfiles' in source:
    raise SystemExit("Sewer profile creation was nested inside the surface/rule recalculation path.")

print("September 11 field-completion regression checks passed.")
