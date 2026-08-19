from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / 'src/CE.Tools.Civil3D/August19CadastralSewerRouteCommands.cs').read_text(encoding='utf-8')
repair = (root / 'scripts/Repair-August19-CadastralSewerRouting-Civil3D2023.ps1').read_text(encoding='utf-8')
build = (root / 'scripts/Build-Install-Civil3D2023-August19.ps1').read_text(encoding='utf-8')

required_source = [
    'CE_SEWERFROMCADASTRAL',
    'CE_SEWERFROMROADRESERVE',
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

required_repair = [
    'CE-Sewer Route from Cadastral Data',
    'CE-Midblock Sewer Route',
    'CE-Road Reserve Sewer Route',
    'CE_SEWERFROMCADASTRAL',
    'CE_MIDBLOCKSEWERPRODUCTION',
    'CE_SEWERFROMROADRESERVE',
    'Create sewer route from cadastral data',
]
for marker in required_repair:
    if marker not in repair:
        raise SystemExit(f'Missing staged Sewer menu marker: {marker}')

if "CE-Midblock / Road-Reserve Sewer Route" not in repair:
    raise SystemExit('Repair no longer proves removal of the old combined Sewer action.')

if 'Repair-August19-CadastralSewerRouting-Civil3D2023.ps1' not in build:
    raise SystemExit('August 19 build does not require the cadastral Sewer repair.')
if '& $august19CadastralSewer -RepoRoot $repo' not in build:
    raise SystemExit('August 19 build does not execute the cadastral Sewer repair.')

print('August 19 cadastral sewer routing validation passed.')
