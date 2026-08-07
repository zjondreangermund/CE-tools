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
]
for name, ok in checks:
    if not ok: errors.append(name)

# Old failure markers must not return.
for name,text,marker in [
    ('vertex',vertex,'database.Dimblk.IsNull'),
    ('runtime',pre,'database.Dimblk.IsNull'),
]:
    if marker in text: errors.append(f'{name} still inherits drawing DIMBLK')

if errors:
    raise SystemExit('Latest runtime/design comment validation failed:\n- ' + '\n- '.join(errors))
print('Latest runtime/design comment validation passed: arrows, tables, coordinate labels, COGO spacing, sewer lengths, exact curve conversion and refresh subscriptions are corrected.')
