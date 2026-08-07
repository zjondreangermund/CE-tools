from pathlib import Path

path = Path('src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs')
text = path.read_text(encoding='utf-8')

old_towns = '''            var towns = new[]
            {
                "Arandis", "Aranos", "Ariamsvlei", "Aus", "Bethanie", "Divundu", "Eenhana", "Gobabis",
                "Grootfontein", "Helao Nafidi", "Henties Bay", "Kalkrand", "Kamanjab", "Karasburg", "Karibib",
                "Katima Mulilo", "Keetmanshoop", "Khorixas", "Kongola", "Leonardville", "Lüderitz", "Maltahöhe",
                "Mariental", "Nkurenkuru", "Noordoewer", "Okahandja", "Okahao", "Omaruru", "Omuthiya", "Ondangwa",
                "Ongwediva", "Opuwo", "Oranjemund", "Oshakati", "Oshikuku", "Otavi", "Otjiwarongo", "Otjinene",
                "Outjo", "Rehoboth", "Rundu", "Ruacana", "Stampriet", "Swakopmund", "Tsumeb", "Uis", "Usakos",
                "Walvis Bay", "Windhoek", "Custom / use Autodesk selector"
            };'''

new_towns = '''            var towns = new[]
            {
                "Aminuis", "Aroab", "Arandis", "Aranos", "Ariamsvlei", "Aus", "Aussenkehr",
                "Bagani", "Berseba", "Bethanie", "Buitepos", "Bukalo",
                "Dordabis", "Divundu",
                "Eenhana", "Epupa", "Epukiro",
                "Fransfontein",
                "Gibeon", "Gobabis", "Gochas", "Grootfontein", "Grünau",
                "Helao Nafidi", "Helmeringhausen", "Henties Bay", "Hoachanas",
                "Kalkfeld", "Kalkrand", "Kamanjab", "Karasburg", "Karibib", "Katima Mulilo", "Katwitwi",
                "Keetmanshoop", "Khorixas", "Klein Aub", "Koës", "Kombat", "Kongola",
                "Leonardville", "Linyanti", "Lüderitz",
                "Maltahöhe", "Mariental", "Mpungu",
                "Ndiyona", "Nkurenkuru", "Noordoewer",
                "Okahandja", "Okahao", "Okakarara", "Okanguati", "Okombahe", "Okongo", "Omaruru", "Omuthiya",
                "Onandjokwe", "Onayena", "Ondangwa", "Ongenga", "Ongwediva", "Opuwo", "Oranjemund",
                "Oshakati", "Oshifo", "Oshikango", "Oshikuku", "Oshivelo",
                "Otavi", "Otjimbingwe", "Otjinene", "Otjiwarongo", "Outapi", "Outjo",
                "Rehoboth", "Rietoog", "Rosh Pinah", "Rundu", "Ruacana",
                "Sesfontein", "Stampriet", "Steinhausen", "Summerdown", "Swakopmund",
                "Tsandi", "Tses", "Tsintsabis", "Tsumeb", "Tsumkwe",
                "Uis", "Usakos",
                "Walvis Bay", "Warmbad", "Windhoek", "Witvlei",
                "Custom / use Autodesk selector"
            };'''

if old_towns not in text:
    raise SystemExit('Town list block not found; source changed.')
text = text.replace(old_towns, new_towns, 1)

old_help = '"Major Namibian towns are mapped to their preferred LO zone. Custom opens Autodesk\'s selector. Existing geometry is never transformed."'
new_help = '"Most Namibian towns and common project centres are listed. Known locations are mapped to a preferred LO zone; Custom or unmapped locations open Autodesk\'s selector. Existing geometry is never transformed."'
if old_help not in text:
    raise SystemExit('Town help marker not found; source changed.')
text = text.replace(old_help, new_help, 1)

old_dict_end = '''                { "Uis", "LO15" }, { "Usakos", "LO15" }, { "Walvis Bay", "LO15" }, { "Windhoek", "LO17" }
            };'''
new_dict_end = '''                { "Uis", "LO15" }, { "Usakos", "LO15" }, { "Walvis Bay", "LO15" }, { "Windhoek", "LO17" },
                { "Aminuis", "LO19" }, { "Aroab", "LO19" }, { "Aussenkehr", "LO17" }, { "Bagani", "LO21" },
                { "Berseba", "LO17" }, { "Bukalo", "LO25" }, { "Dordabis", "LO17" }, { "Epukiro", "LO19" },
                { "Fransfontein", "LO15" }, { "Gibeon", "LO17" }, { "Gochas", "LO19" }, { "Grünau", "LO19" },
                { "Helmeringhausen", "LO17" }, { "Hoachanas", "LO19" }, { "Kalkfeld", "LO17" },
                { "Klein Aub", "LO17" }, { "Koës", "LO19" }, { "Kombat", "LO17" }, { "Linyanti", "LO23" },
                { "Mpungu", "LO19" }, { "Ndiyona", "LO21" }, { "Okakarara", "LO17" }, { "Okanguati", "LO15" },
                { "Okombahe", "LO15" }, { "Okongo", "LO17" }, { "Onayena", "LO17" }, { "Ongenga", "LO15" },
                { "Oshifo", "LO15" }, { "Oshikango", "LO15" }, { "Oshivelo", "LO17" },
                { "Otjimbingwe", "LO17" }, { "Outapi", "LO15" }, { "Rietoog", "LO17" }, { "Rosh Pinah", "LO17" },
                { "Sesfontein", "LO13" }, { "Steinhausen", "LO19" }, { "Summerdown", "LO19" },
                { "Tsandi", "LO15" }, { "Tses", "LO19" }, { "Tsintsabis", "LO17" }, { "Tsumkwe", "LO19" },
                { "Witvlei", "LO19" }
            };'''
if old_dict_end not in text:
    raise SystemExit('Town LO dictionary tail not found; source changed.')
text = text.replace(old_dict_end, new_dict_end, 1)

path.write_text(text, encoding='utf-8')
print('Expanded Namibia town catalogue patch applied.')
