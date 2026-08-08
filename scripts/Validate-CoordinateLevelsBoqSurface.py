from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
files = {
    'map': ROOT/'src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs',
    'vertex': ROOT/'src/CE.Tools.Civil3D/VertexSettingOutCommands.cs',
    'geometry': ROOT/'src/CE.Tools.Civil3D/VertexSettingOutGeometry.cs',
    'survey': ROOT/'src/CE.Tools.Civil3D/SurveyCoordinateWorkflowCommands.cs',
    'feature': ROOT/'src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs',
    'runtime': ROOT/'src/CE.Tools.Civil3D/PreBuildRuntimeCompletionCommands.cs',
    'boq': ROOT/'src/CE.Tools.Civil3D/BillOfQuantitiesCommands.cs',
    'schedule': ROOT/'src/CE.Tools.Civil3D/NetworkAssetScheduleCommands.cs',
    'surface': ROOT/'src/CE.Tools.Civil3D/SurfaceSpikeHoleRepairCommands.cs',
    'tables': ROOT/'src/CE.Tools.Civil3D/CeTablePresentationCommands.cs',
    'refresh': ROOT/'src/CE.Tools.Civil3D/UniversalDynamicRefreshCommands.cs',
}
for name, path in files.items():
    if not path.exists(): raise SystemExit(f'Missing {name}: {path}')
text = {k:p.read_text(encoding='utf-8') for k,p in files.items()}
checks = {
 'map': ['"Northing (N / Y)"','"Easting (E / X)"','"Drawing X / Easting"','"Drawing Y / Northing"','"Northing / Easting -> Y / X"','"Y / X -> Northing / Easting"','TryParseSignedNumber'],
 'geometry': ['public double? NgLevel','public double? DesignLevel'],
 'vertex': ['"NGSurface"','"NG LEVEL"','"DESIGN LEVEL"','"DIFFERENCE"','ApplyLevelReferences','NgSurfaceHandle','mtext.Location = record.Point','AnchoredMText'],
 'survey': ['text.Location = target','CoordinateAttachment','CreateLinkedLevelTable','"NG LEVEL"','"DESIGN LEVEL"','"DIFFERENCE"','PromptOptionalNgSurface','NgSurface='],
 'feature': ['PromptOptionalNgSurface','CreateLinkedLevelTable'],
 'runtime': ['MText anchoredText = entity as MText','anchoredText.Location = anchor'],
 'boq': ['TryGetLength(DBObject databaseObject, Transaction transaction','"StartPoint"','"EndPoint"','TryReadConnectedStructureLength','SnapNominalDiameter','CellAlignment.MiddleCenter'],
 'schedule': ['ReadGeometricLength(value, transaction)','ToNominalMillimetres','"StartPoint"','"EndPoint"'],
 'surface': ['CivilTinSurface','tin.GetTriangles(false)','tin.AddVertices','UniqueTriangleVertices'],
 'tables': ['CE_TABLECENTERALL','CenterCeTables','CellAlignment.MiddleCenter','RecordGraphicsModified(true)'],
 'refresh': ['CeTablePresentationManager.CenterCeTables(document)'],
}
for key, markers in checks.items():
    for marker in markers:
        if marker not in text[key]: raise SystemExit(f'Missing {key} marker: {marker}')
if 'column == 3 || column == 9' in text['boq']:
    raise SystemExit('BOQ still left-aligns description/source cells.')
if 'ReadGeometricLength(value);' in text['schedule']:
    raise SystemExit('Network schedule still uses old geometry-length call.')
if 'mtext.Location = OutputLocation(record, link.LabelOffset);' in text['vertex']:
    raise SystemExit('Vertex MText still uses an offset insertion point rather than the source anchor.')
if 'text.Location = labelPoint;' in text['survey'].split('if (settings.Output == AnnotationOutput.MText)',1)[1].split('else',1)[0]:
    raise SystemExit('Coordinate MText still uses the label point as its insertion/base point.')
print('Coordinate/levels/BOQ/surface runtime regression checks passed.')
