from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "src/CE.Tools.Civil3D/September11FieldCompletionCommands.cs").read_text(encoding="utf-8")
september14 = (root / "src/CE.Tools.Civil3D/September14HatchOuterBoundaryCommands.cs").read_text(encoding="utf-8")
road_strict = (root / "src/CE.Tools.Civil3D/September14RoadCentreStrictCommands.cs").read_text(encoding="utf-8")
alignment_bands = (root / "src/CE.Tools.Civil3D/September14AlignmentBandStyleCommands.cs").read_text(encoding="utf-8")
context_menu = (root / "src/CE.Tools.Civil3D/DynamicRefreshContextMenu.cs").read_text(encoding="utf-8")
menu = (root / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
front = (root / "src/CE.Tools.Civil3D/September09FieldEngineeringCommandFrontDoor.cs").read_text(encoding="utf-8")
project = (root / "src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs").read_text(encoding="utf-8")
annotation = (root / "src/CE.Tools.Civil3D/AnnotationScaleSyncCommands.cs").read_text(encoding="utf-8")
presets = (root / "src/CE.Tools.Civil3D/August11DisciplineStylePresetCommands.cs").read_text(encoding="utf-8")

required_source = [
    '"CE_ROADCENTRECLEAN"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHBOUNDARIES"',
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

arc_guard = 'if (previousType.Value == SegmentType.Arc || nextType.Value == SegmentType.Arc)\n                    continue;'
if arc_guard not in road_strict:
    raise SystemExit("Strict road-centre cleanup no longer protects BC/EC arc transitions.")

required_alignment_bands = [
    '"CE_ALIGNLABELSETMULTI"',
    'civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles',
    'alignment.ImportLabelSet(choice.Id)',
    '"CE_ROADBANDLABELS"',
    'civilDocument.Styles.ProfileViewBandSetStyles',
    'profileView.Bands.ImportBandSetStyle(choice.Id)',
    'profileView.Bands.GetTopBandItems()',
    'profileView.Bands.GetBottomBandItems()',
    'item.ShowLabels = true',
    'profileView.Bands.SetTopBandItems(top)',
    'profileView.Bands.SetBottomBandItems(bottom)',
]
for token in required_alignment_bands:
    if token not in alignment_bands:
        raise SystemExit(f"September 14 alignment/band marker missing: {token}")

for forbidden in ['new MText()', 'new DBText()', 'new Leader()', 'new MLeader()']:
    if forbidden in alignment_bands:
        raise SystemExit(f"Alignment/band fix regressed to generic AutoCAD annotation: {forbidden}")

required_context_menu = [
    'ExtensionApplication(typeof(CETools.Civil3D.DynamicRefreshContextMenuApplication))',
    'IExtensionApplication',
    'ContextMenuExtension',
    'new MenuItem("CE Dynamic Refresh All")',
    'AddDefaultContextMenuExtension',
    'RemoveDefaultContextMenuExtension',
    'DynamicRefreshAllCommand = "CE_DYNAMIC" + "REFRESHALL"',
    'document.SendStringToExecute(DynamicRefreshAllCommand + " "',
    'AnnotationScaleSyncManager.Initialize();',
    'AnnotationScaleSyncManager.Terminate();',
]
for token in required_context_menu:
    if token not in context_menu:
        raise SystemExit(f"Dynamic Refresh/startup marker missing: {token}")

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
    '"CE_ALIGNLABELSETMULTI"',
    'Alignment Label Set - Multiple Alignments',
    '"CE_ROADBANDLABELS"',
    'Road Profile Band Set - Show Labels',
    '"CE_SEWRECALC"',
    '"CE_ANNOSCALESYNC"',
    'Automatic monitor now applies each changed drawing annotation scale',
    '"CE_DISCIPLINESTYLEPRESETS"',
    '"CE_SURVEYLOCATIONNAMIBIA"',
    '"CE_GOOGLEEARTHLINEWORK"',
    '"CE_HATCHOUTERBOUNDARY"',
    'open OR closed 2D/3D polylines',
]
for token in required_menu:
    if token not in menu:
        raise SystemExit(f"Field-completion menu marker missing: {token}")

if '"Separate Hatch Boundaries"' in menu or '"CE_HATCHBOUNDARIES"' in menu:
    raise SystemExit("Field-completion menu still routes to the legacy separate-hatch boundary workflow.")

if '"Clean Existing Road Centres",\n                        "CE_ROADCENTRECLEAN"' in menu:
    raise SystemExit("Field-completion menu still routes road cleanup to the legacy junction-preserving path.")

required_annotation = [
    '"CE_ANNOSCALESYNC"',
    '"CANNOSCALE"',
    'AcApplication.Idle += OnIdle;',
    'AcApplication.Idle -= OnIdle;',
    'AcApplication.DocumentManager.DocumentToBeDestroyed +=\n                OnDocumentToBeDestroyed;',
    'AcApplication.DocumentManager.DocumentToBeDestroyed -=\n                OnDocumentToBeDestroyed;',
    'entity is Dimension',
    'entity is DBText',
    'entity is MText',
    'entity is MLeader',
    'changed = AddContext(entity, currentContext) || changed;',
    '"AddContext"',
]
for token in required_annotation:
    if token not in annotation:
        raise SystemExit(f"Automatic annotation-scale synchronisation marker missing: {token}")

if 'RemoveContext(' in annotation:
    raise SystemExit("Annotation-scale monitor removes existing entity contexts; it must only add the new current scale.")

if '"CE_DISCIPLINESTYLEPRESETS"' not in presets:
    raise SystemExit("Discipline style preset command is missing.")

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

if 'new SewerProductionCommands().CreateProfiles' in source:
    raise SystemExit("Sewer profile creation was nested inside the surface/rule recalculation path.")

print("September 11/14 field-completion regression checks passed.")
