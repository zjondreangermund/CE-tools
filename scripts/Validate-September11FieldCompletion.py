from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "src/CE.Tools.Civil3D/September11FieldCompletionCommands.cs").read_text(encoding="utf-8")
september14 = (root / "src/CE.Tools.Civil3D/September14HatchOuterBoundaryCommands.cs").read_text(encoding="utf-8")
road_strict = (root / "src/CE.Tools.Civil3D/September14RoadCentreStrictCommands.cs").read_text(encoding="utf-8")
context_menu = (root / "src/CE.Tools.Civil3D/DynamicRefreshContextMenu.cs").read_text(encoding="utf-8")
menu = (root / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
front = (root / "src/CE.Tools.Civil3D/September09FieldEngineeringCommandFrontDoor.cs").read_text(encoding="utf-8")
project = (root / "src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs").read_text(encoding="utf-8")
annotation = (root / "src/CE.Tools.Civil3D/AnnotationScaleSyncCommands.cs").read_text(encoding="utf-8")
presets = (root / "src/CE.Tools.Civil3D/August11DisciplineStylePresetCommands.cs").read_text(encoding="utf-8")

required_source = [
    '"CE_ROADCENTRECLEAN"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHBOUNDARIES"',  # legacy command remains available for backwards compatibility
    '"CE_SEWRECALC"',
    '"CE_SURVEYLOCATIONNAMIBIA"',
    'Separate line strings',
    '<LineString>',
    'FeatureLinePointType.AllPoints',
    'September10SewerAuditRuntime.LinkExistingPartsToSurface',
    'document.SendStringToExecute("CE_SEWPROFILE "',
    'Surface/rule writes are committed before any profile command starts',
    'VerifyReplacements',
    'Straight intermediate vertices are removed; bend and T/X junction vertices are retained',
    # Civil 3D 2023 returns ObjectIdCollection here. It is non-generic, so the
    # collection must be explicitly Cast<ObjectId>() before passing it to the
    # IEnumerable<ObjectId> helper or the Autodesk build fails with CS1503.
    'civilDocument.GetSurfaceIds().Cast<ObjectId>()',
]
for token in required_source:
    if token not in source:
        raise SystemExit(f"September 11 source marker missing: {token}")

required_september14 = [
    '"CE_HATCHOUTERBOUNDARY"',
    'CancelSharedEdges',
    'BuildClosedChains',
    'one outside boundary was created per cluster',
    'Source hatches were not changed',
]
for token in required_september14:
    if token not in september14:
        raise SystemExit(f"September 14 hatch perimeter marker missing: {token}")

required_road_strict = [
    '"CE_ROADCENTRECLEANSTRICT"',
    'SegmentType.Arc',
    'SegmentType.Line',
    'FindRedundantVertices',
    'polyline.RemoveVertexAt(remove[index])',
    'One transaction per source polyline',
    'regardless of a T/X road junction',
    'Straight roads keep start/end only; arc roads keep BC/EC',
]
for token in required_road_strict:
    if token not in road_strict:
        raise SystemExit(f"September 14 strict road-centre marker missing: {token}")

# Strict cleanup must never remove a vertex that touches an arc. This is the
# source-level guard that preserves BC/EC while allowing straight T/X junction
# vertices to disappear from the through road.
arc_guard = 'if (previousType.Value == SegmentType.Arc || nextType.Value == SegmentType.Arc)\n                    continue;'
if arc_guard not in road_strict:
    raise SystemExit("Strict road-centre cleanup no longer protects BC/EC arc transitions.")

required_context_menu = [
    'ExtensionApplication(typeof(CETools.Civil3D.DynamicRefreshContextMenuApplication))',
    'IExtensionApplication',
    'ContextMenuExtension',
    'new MenuItem("CE Dynamic Refresh All")',
    'AddDefaultContextMenuExtension',
    'RemoveDefaultContextMenuExtension',
    'DynamicRefreshAllCommand = "CE_DYNAMIC" + "REFRESHALL"',
    'document.SendStringToExecute(DynamicRefreshAllCommand + " "',
]
for token in required_context_menu:
    if token not in context_menu:
        raise SystemExit(f"Dynamic Refresh right-click marker missing: {token}")

# The shortcut must remain an explicit/manual command handoff. Calling refresh
# manager internals directly from a menu event would bypass the sewer sequence
# safety boundary introduced by PR #151.
if 'UniversalDynamicRefreshManager.RefreshNow(' in context_menu:
    raise SystemExit("Right-click Dynamic Refresh bypasses the explicit manual command boundary.")

if 'September11FieldCompletionRuntime.RoadReserveCentrePolylines(document);' not in front:
    raise SystemExit("Road Reserve front door is not routed through the final centreline cleanup wrapper.")

required_menu = [
    '"CE_FIELDCOMPLETION"',
    'DynamicRefreshAllCommand = "CE_DYNAMIC" + "REFRESHALL"',
    'DynamicRefreshAllCommand,',
    'right-click menu',
    '"CE_ROADRESERVECENTRELINES"',
    '"CE_ROADCENTRECLEANSTRICT"',
    'Strict Road Centre Cleanup - Start / BC / EC / End',
    'Straight roads keep start/end only',
    '"CE_SEWRECALC"',
    '"CE_ANNOSCALESYNC"',
    '"CE_DISCIPLINESTYLEPRESETS"',
    '"CE_SURVEYLOCATIONNAMIBIA"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHOUTERBOUNDARY"',
    'open OR closed 2D/3D polylines',
]
for token in required_menu:
    if token not in menu:
        raise SystemExit(f"Field-completion menu marker missing: {token}")

# The field-completion front door must use the September 14 outside-perimeter
# workflow, not route users back to the old per-hatch-loop behaviour.
if '"Separate Hatch Boundaries"' in menu or '"CE_HATCHBOUNDARIES"' in menu:
    raise SystemExit("Field-completion menu still routes to the legacy separate-hatch boundary workflow.")

# The field-completion road cleanup must use the September 14 strict path. The
# legacy CE_ROADCENTRECLEAN command remains available for backwards compatibility.
if '"Clean Existing Road Centres",\n                        "CE_ROADCENTRECLEAN"' in menu:
    raise SystemExit("Field-completion menu still routes road cleanup to the legacy junction-preserving path.")

if '"CE_ANNOSCALESYNC"' not in annotation or 'CANNOSCALE' not in annotation:
    raise SystemExit("Annotation-scale synchronisation command/monitor is missing.")
if '"CE_DISCIPLINESTYLEPRESETS"' not in presets:
    raise SystemExit("Discipline style preset command is missing.")

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

print("September 11/14 field-completion regression checks passed.")
