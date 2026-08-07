from pathlib import Path

path = Path('src/CE.Tools.Civil3D/SewerProductionCommands.cs')
text = path.read_text(encoding='utf-8')

required = [
    'new SewerNode(id, rim, structure.Position)',
    'public Point3d Position { get; }',
    'start.Position.DistanceTo(end.Position)',
]
for marker in required:
    if marker not in text:
        raise SystemExit('Missing Civil 3D 2023 SewerNode compatibility marker: ' + marker)

if 'node = new SewerNode(id, rim);' in text:
    raise SystemExit('Legacy SewerNode construction without a position is still present.')

print('SewerNode Civil 3D 2023 compiler regression validated.')
