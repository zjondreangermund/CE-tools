from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / 'src/CE.Tools.Civil3D/August19CadastralSewerRouteCommands.cs').read_text(encoding='utf-8')
road_reserve = (root / 'src/CE.Tools.Civil3D/August19RoadReserveSewerAndSafetyCommands.cs').read_text(encoding='utf-8')
repair = (root / 'scripts/Repair-August19-CadastralSewerRouting-Civil3D2023.ps1').read_text(encoding='utf-8')
build = (root / 'scripts/Build-Install-Civil3D2023-August19.ps1').read_text(encoding='utf-8')
late_repair = (root / 'scripts/Repair-August21-StateGraphicsSurfaceSafety-Civil3D2023.ps1').read_text(encoding='utf-8')

# The later stability repair replaces the whole command body during a local
# build. Validate that the code actually compiled keeps the surface dropdown.
late_body = late_repair.split("$cadastralBody = @'", 1)[1].split("'@", 1)[0]
if 'model.AddChoice("Surface", "01 Cadastral"' not in late_body or \
        'ObjectId surfaceId = selectedSurface == null ? ObjectId.Null : selectedSurface.Id;' not in late_body or \
        'ObjectId surfaceId = PromptSurface(document);' in late_body:
    raise SystemExit('Late Civil 3D build repair restores a click-only surface prompt.')

required_source = [
    'CE_SEWERFROMCADASTRAL',
    'Offset from shared erf boundary',
    'Offset from outer erf boundary',
    'Select Civil 3D surface for cadastral sewer slope / low-point analysis',
    'Shortest practical route',
    'SAMPLED SITE LOW POINT',
    'NETWORK OUTLET',
    'MIDBLOCK',
    'ROAD_RESERVE',
    'FindElevationAtXY',
    'NextTowardOutlet',
    'adverse * 1000.0',
]
for marker in required_source:
    if marker not in source:
        raise SystemExit(f'Missing cadastral sewer source marker: {marker}')

required_road_reserve = [
    'CE_SEWERROADRESERVE',
    'CE_ROADRESERVECENTERLINESSAFE',
    'Offset from erf boundary into road reserve',
    'Minimum road reserve width',
    'Maximum road reserve width',
    'Maximum opposing-edge angle difference',
    'Minimum overlapping edge length (%)',
    'Minimum usable reserve-edge length',
    'Starting manhole setback from erf boundary',
    'Select Civil 3D surface for Road Reserve sewer slope / site-low-point analysis',
    'FindElevationAtXY',
    'SelfIntersects',
    'SplitAtJunctionsAndSpacing',
    'TryOffsetIntoReserve',
    'ROAD RESERVE SEWER - SITE LOW POINT',
]
for marker in required_road_reserve:
    if marker not in road_reserve:
        raise SystemExit(f'Missing Road Reserve Sewer/safety marker: {marker}')

required_repair = [
    'CE-Sewer Route from Cadastral Data',
    'CE-Midblock Sewer Route',
    'CE-Road Reserve Sewer Route',
    'CE_SEWERFROMCADASTRAL',
    'CE_MIDBLOCKSEWERPRODUCTION',
    'CE_SEWERROADRESERVE',
    'CE_ROADRESERVECENTERLINESSAFE',
    'Create sewer route from cadastral data',
    'Create Sewer route in road reserves',
]
for marker in required_repair:
    if marker not in repair:
        raise SystemExit(f'Missing staged Sewer/Road Reserve menu marker: {marker}')

if 'CE-Midblock / Road-Reserve Sewer Route' not in repair:
    raise SystemExit('Repair no longer proves removal of the old combined Sewer action.')

if 'Repair-August19-CadastralSewerRouting-Civil3D2023.ps1' not in build:
    raise SystemExit('August 19 build does not require the cadastral Sewer repair.')
if '& $august19CadastralSewer -RepoRoot $repo' not in build:
    raise SystemExit('August 19 build does not execute the cadastral Sewer repair.')

print('August 19 cadastral, Midblock separation and Road Reserve sewer/safety validation passed.')
