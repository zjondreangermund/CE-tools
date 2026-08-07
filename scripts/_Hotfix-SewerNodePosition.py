from pathlib import Path

path = Path('src/CE.Tools.Civil3D/SewerProductionCommands.cs')
text = path.read_text(encoding='utf-8')

old_call = '            node = new SewerNode(id, rim);'
new_call = '            node = new SewerNode(id, rim, structure.Position);'
if old_call in text:
    text = text.replace(old_call, new_call, 1)
elif new_call not in text:
    raise SystemExit('Expected SewerNode construction marker not found')

old_ctor = '''        public SewerNode(ObjectId id, double rim)\n        {\n            Id = id;\n            Rim = rim;\n            Edges = new List<SewerEdge>();\n        }\n        public ObjectId Id { get; }\n        public double Rim { get; }\n        public IList<SewerEdge> Edges { get; }'''
new_ctor = '''        public SewerNode(ObjectId id, double rim)\n            : this(id, rim, Point3d.Origin)\n        {\n        }\n\n        public SewerNode(ObjectId id, double rim, Point3d position)\n        {\n            Id = id;\n            Rim = rim;\n            Position = position;\n            Edges = new List<SewerEdge>();\n        }\n        public ObjectId Id { get; }\n        public double Rim { get; }\n        public Point3d Position { get; }\n        public IList<SewerEdge> Edges { get; }'''
if old_ctor in text:
    text = text.replace(old_ctor, new_ctor, 1)
elif new_ctor not in text:
    raise SystemExit('Expected SewerNode class marker not found')

path.write_text(text, encoding='utf-8')

# Regression assertions for the exact Windows compiler failure.
check = path.read_text(encoding='utf-8')
assert 'new SewerNode(id, rim, structure.Position)' in check
assert 'public Point3d Position { get; }' in check
assert 'start.Position.DistanceTo(end.Position)' in check
print('SewerNode Civil 3D 2023 position hotfix applied and validated.')
