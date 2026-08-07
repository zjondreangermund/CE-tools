#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'src' / 'CE.Tools.Civil3D'
errors=[]

def read(name): return (SRC/name).read_text(encoding='utf-8')

vertex=read('VertexSettingOutCommands.cs')
pre=read('PreBuildRuntimeCompletionCommands.cs')
cogo=read('CogoPointProjectStyleCommands.cs')
grid=read('GridReportPresenter.cs')
sewer=read('SewerProductionCommands.cs')
curve=read('CurveConversionCommands.cs')
plugin=read('PluginEntry.cs')
universal=read('UniversalDynamicRefreshCommands.cs')
road_settings=read('RoadProjectSettingsCommands.cs')
road_styles=read('RoadProductionCommentCommands.cs')
road_corridor=read('RoadCorridorCompletionCommands.cs')
feature=read('FeatureProfileSurfaceCommentCommands.cs')
coordination=read('ProjectCoordinationCommands.cs')
utility=read('UtilityPlanningCommands.cs')
surface=read('SurfaceSpikeHoleRepairCommands.cs')
network_schedule=read('NetworkAssetScheduleCommands.cs')

checks=[
    ('vertex closed-filled arrow', 'leader.ArrowSymbolId = ObjectId.Null;' in vertex),
    ('radial closed-filled arrow', 'ObjectId arrow = ObjectId.Null;' in vertex),
    ('runtime closed-filled arrow', 'leader.ArrowSymbolId = ObjectId.Null;' in pre),
    ('bounded runtime annotations', 'ModelDistance(database, 8.0)' in pre),
    ('bounded COGO labels', 'ModelDistance(database, 8.0)' in cogo),
    ('COGO prefers anchor distance', 'candidate.DistanceTo(item.Anchor)' in cogo),
    ('vertex table immediate graphics', 'RecomputeTableBlock' in vertex),
    ('global tables immediate graphics', 'RecomputeTableBlock' in grid),
    ('global table data centred', 'CellAlignment.MiddleLeft' not in grid),
    ('coordinate numbers stay in fixed columns', 'table.Cells[row, 4].TextString = displayX' in vertex and 'table.Cells[row, 5].TextString = displayY' in vertex),
    ('sewer 1m fallback removed', 'length = 1.0;' not in sewer),
    ('sewer geometric fallback', 'pipe.GetPointAtParam(0.0).DistanceTo' in sewer),
    ('exact arc polyline', 'CreateExactArcPolyline' in curve and 'Math.Tan((sweep / segments) / 4.0)' in curve),
    ('exact circle polyline', 'CreateExactCirclePolyline' in curve and 'Math.Tan(Math.PI / 8.0)' in curve),
    ('ribbon display names normalized', 'NormalizeDisplayText' in plugin),
    ('single command-start subscription', universal.count('_document.CommandWillStart += OnCommandWillStart;') == 1),
    ('road settings command', 'CE_ROADSETTINGS' in road_settings),
    ('road settings stored per DWG', 'ROAD_PRODUCTION_SETTINGS' in road_settings),
    ('preferred road band set', 'Road-Single-Band Set 1-Full Grid' in road_settings),
    ('road alignment styles use road settings', 'RoadProductionSettings road = RoadProductionSettings.Read' in road_styles and 'RoadValue(road, selection' in road_styles),
    ('corridor can create missing baseline and region', 'TryCreateMissingBaselineAndRegion' in road_corridor and 'InvokeAddBaseline' in road_corridor and 'InvokeAddRegion' in road_corridor),
    ('corridor uses bottom surface naming', 'CE-BOTTOM' in road_corridor),
    ('feature-line vertex points resync COGO style', 'CogoPointProjectStyleCommands.ApplySelectedStyles(document, true)' in feature),
    ('project coordination centre', 'CE_PROJECTCOORDINATION' in coordination),
    ('non-destructive master xref command', 'CE_MASTERXREF' in coordination and 'AttachXref' in coordination and 'Source DWGs were not modified' in coordination),
    ('multi-layout page setup manager', 'CE_PAGESETUPMANAGER' in coordination and 'PlotSettings(false)' in coordination),
    ('survey town coordinate-system command', 'CE_SURVEYLOCATION' in coordination and 'LO17' in coordination and 'LO15' in coordination),
    ('map location command', 'CE_MAPLOCATION' in coordination and 'Google Maps' in coordination and 'Google Earth' in coordination),
    ('utility planning workflow', 'CE_UTILITYPLANNER' in utility and 'CE_UTILITYROUTES' in utility and 'CE_UTILITYROUTESREFRESH' in utility),
    ('utility default boundary offset', '"Boundary offset (m)", 1.5' in utility),
    ('utility constraints', 'Minimum pipe slope (%)' in utility and 'Maximum pipe cover (m)' in utility and 'Warn when included pipe angle is below' in utility),
    ('adaptive surface repair retry', 'Adaptive neighbour retry' in surface and 'neighbourRadius * 4.0' in surface),
    ('surface refuses unchanged repair copies', 'No unchanged repair surface was created' in surface),
    ('surface output rebuild verification', 'generatedVertexCount' in surface and 'Rebuild(generated)' in surface),
    ('network suspicious 1m length geometry fallback', 'ReadGeometricLength' in network_schedule and 'Math.Abs(length.Value - 1.0)' in network_schedule),
    ('network nominal millimetre fallback', 'ToNominalMillimetres' in network_schedule and ' + " mm"' in network_schedule),
]
for name, ok in checks:
    if not ok: errors.append(name)

for command in [
    'CE_ROADSETTINGS', 'CE_PROJECTCOORDINATION', 'CE_MASTERXREF',
    'CE_PAGESETUPMANAGER', 'CE_SURVEYLOCATION', 'CE_MAPLOCATION',
    'CE_UTILITYPLANNER', 'CE_UTILITYROUTES', 'CE_UTILITYROUTESREFRESH'
]:
    if command not in plugin:
        errors.append('ribbon is missing ' + command)

for name,text,marker in [
    ('vertex',vertex,'database.Dimblk.IsNull'),
    ('runtime',pre,'database.Dimblk.IsNull'),
]:
    if marker in text: errors.append(f'{name} still inherits drawing DIMBLK')

if errors:
    raise SystemExit('Latest runtime/design comment validation failed:\n- ' + '\n- '.join(errors))
print('Latest runtime/design comment validation passed: runtime annotation/table fixes, exact curve conversion, road production, project coordination, cadastral utility planning, adaptive surface repair and verified network length/diameter presentation are present.')
