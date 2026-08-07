from pathlib import Path

project = Path('src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs').read_text(encoding='utf-8')
utility = Path('src/CE.Tools.Civil3D/UtilityPlanningCommands.cs').read_text(encoding='utf-8')

required_project = [
    'model.AddText("Latitude"',
    'model.AddText("Longitude"',
    'TryParseCoordinate(model.Text("Latitude")',
    'TryParseCoordinate(model.Text("Longitude")',
    '"Katima Mulilo"',
    '"Opuwo"',
    '"Lüderitz"',
    '"Noordoewer"',
    '"LO25"',
    '"LO13"',
    'TownLoZones',
]
for marker in required_project:
    if marker not in project:
        raise SystemExit(f'Missing ProjectCoordination marker: {marker}')

if 'model.AddDouble("Latitude"' in project or 'model.AddDouble("Longitude"' in project:
    raise SystemExit('WGS84 latitude/longitude still use the positive-only AddDouble compatibility field.')

required_utility = [
    '"Midblock sewer centreline"',
    'CreatePlanningRoute(source, settings)',
    'CreateMidblockRoute',
    '"Route mode", settings.RouteMode',
]
for marker in required_utility:
    if marker not in utility:
        raise SystemExit(f'Missing UtilityPlanning marker: {marker}')

print('Namibia map/location and utility-planner regression checks passed.')
