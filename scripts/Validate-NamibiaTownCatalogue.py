from pathlib import Path

project = Path('src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs').read_text(encoding='utf-8')

required = [
    '"Aminuis"', '"Aroab"', '"Aussenkehr"', '"Berseba"', '"Buitepos"', '"Bukalo"',
    '"Dordabis"', '"Epukiro"', '"Gibeon"', '"Gochas"', '"Grünau"', '"Helmeringhausen"',
    '"Hoachanas"', '"Kalkfeld"', '"Klein Aub"', '"Koës"', '"Kombat"', '"Linyanti"',
    '"Mpungu"', '"Ndiyona"', '"Okakarara"', '"Okanguati"', '"Okombahe"', '"Okongo"',
    '"Onayena"', '"Ongenga"', '"Oshifo"', '"Oshikango"', '"Oshivelo"', '"Otjimbingwe"',
    '"Outapi"', '"Rietoog"', '"Rosh Pinah"', '"Sesfontein"', '"Steinhausen"', '"Summerdown"',
    '"Tsandi"', '"Tses"', '"Tsintsabis"', '"Tsumkwe"', '"Witvlei"',
    'Most Namibian towns and common project centres are listed.',
    'TownLoZones'
]
for marker in required:
    if marker not in project:
        raise SystemExit(f'Missing Namibia town catalogue marker: {marker}')

print('Expanded Namibia town catalogue regression checks passed.')
