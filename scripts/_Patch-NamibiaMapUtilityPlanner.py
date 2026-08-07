from pathlib import Path


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit(f'Missing patch marker: {label}')
    return text.replace(old, new, 1)

project_path = Path('src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs')
project = project_path.read_text(encoding='utf-8')

old_towns = '            var towns = new[] { "Windhoek", "Swakopmund", "Walvis Bay", "Henties Bay", "Oshakati", "Rundu", "Keetmanshoop", "Custom / use Autodesk selector" };'
new_towns = '''            var towns = new[]
            {
                "Arandis", "Aranos", "Ariamsvlei", "Aus", "Bethanie", "Divundu", "Eenhana", "Gobabis",
                "Grootfontein", "Helao Nafidi", "Henties Bay", "Kalkrand", "Kamanjab", "Karasburg", "Karibib",
                "Katima Mulilo", "Keetmanshoop", "Khorixas", "Kongola", "Leonardville", "Lüderitz", "Maltahöhe",
                "Mariental", "Nkurenkuru", "Noordoewer", "Okahandja", "Okahao", "Omaruru", "Omuthiya", "Ondangwa",
                "Ongwediva", "Opuwo", "Oranjemund", "Oshakati", "Oshikuku", "Otavi", "Otjiwarongo", "Otjinene",
                "Outjo", "Rehoboth", "Rundu", "Ruacana", "Stampriet", "Swakopmund", "Tsumeb", "Uis", "Usakos",
                "Walvis Bay", "Windhoek", "Custom / use Autodesk selector"
            };'''
project = replace_once(project, old_towns, new_towns, 'expanded Namibia towns')
project = replace_once(
    project,
    '            model.AddChoice("Town", "01 Location", "Town / project area", "Windhoek", "Windhoek prefers LO17; Swakopmund/Walvis Bay/Henties Bay prefer LO15. Other towns open the Autodesk selector if a safe preset is not defined.", towns);',
    '            model.AddChoice("Town", "01 Location", "Town / project area", "Windhoek", "Major Namibian towns are mapped to their preferred LO zone. Custom opens Autodesk\'s selector. Existing geometry is never transformed.", towns);',
    'survey-town help text')
project = replace_once(
    project,
    '            model.AddDouble("Latitude", "01 WGS84", "Latitude", -22.5609, "Decimal degrees, south negative.");\n            model.AddDouble("Longitude", "01 WGS84", "Longitude", 17.0658, "Decimal degrees, east positive.");',
    '            model.AddText("Latitude", "01 WGS84", "Latitude", "-22.5609", "Decimal degrees; south is negative. Values from -90 to 90 are accepted.");\n            model.AddText("Longitude", "01 WGS84", "Longitude", "17.0658", "Decimal degrees; west is negative and east is positive. Values from -180 to 180 are accepted.");',
    'signed coordinate fields')
project = replace_once(
    project,
    '            double latitude = model.Double("Latitude", 0.0);\n            double longitude = model.Double("Longitude", 0.0);\n            if (latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)\n            {\n                document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Latitude/longitude are outside WGS84 ranges.");\n                return;\n            }',
    '            double latitude;\n            double longitude;\n            if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out latitude) ||\n                !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out longitude))\n            {\n                document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed WGS84 latitude (-90 to 90) and longitude (-180 to 180).");\n                return;\n            }',
    'signed coordinate parsing')

old_preferred = '''        private static string PreferredLo(string town)
        {
            if (string.Equals(town, "Windhoek", StringComparison.OrdinalIgnoreCase)) return "LO17";
            if (string.Equals(town, "Swakopmund", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(town, "Walvis Bay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(town, "Henties Bay", StringComparison.OrdinalIgnoreCase)) return "LO15";
            return string.Empty;
        }
'''
new_preferred = '''        private static readonly IDictionary<string, string> TownLoZones =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Arandis", "LO15" }, { "Aranos", "LO19" }, { "Ariamsvlei", "LO19" }, { "Aus", "LO17" },
                { "Bethanie", "LO17" }, { "Divundu", "LO21" }, { "Eenhana", "LO17" }, { "Gobabis", "LO19" },
                { "Grootfontein", "LO19" }, { "Helao Nafidi", "LO17" }, { "Henties Bay", "LO15" },
                { "Kalkrand", "LO17" }, { "Kamanjab", "LO15" }, { "Karasburg", "LO19" }, { "Karibib", "LO15" },
                { "Katima Mulilo", "LO25" }, { "Keetmanshoop", "LO19" }, { "Khorixas", "LO15" },
                { "Kongola", "LO23" }, { "Leonardville", "LO19" }, { "Lüderitz", "LO15" }, { "Maltahöhe", "LO17" },
                { "Mariental", "LO17" }, { "Nkurenkuru", "LO19" }, { "Noordoewer", "LO17" },
                { "Okahandja", "LO17" }, { "Okahao", "LO15" }, { "Omaruru", "LO15" }, { "Omuthiya", "LO17" },
                { "Ondangwa", "LO15" }, { "Ongwediva", "LO15" }, { "Opuwo", "LO13" }, { "Oranjemund", "LO17" },
                { "Oshakati", "LO15" }, { "Oshikuku", "LO15" }, { "Otavi", "LO17" }, { "Otjiwarongo", "LO17" },
                { "Otjinene", "LO19" }, { "Outjo", "LO17" }, { "Rehoboth", "LO17" }, { "Rundu", "LO19" },
                { "Ruacana", "LO15" }, { "Stampriet", "LO19" }, { "Swakopmund", "LO15" }, { "Tsumeb", "LO17" },
                { "Uis", "LO15" }, { "Usakos", "LO15" }, { "Walvis Bay", "LO15" }, { "Windhoek", "LO17" }
            };

        private static string PreferredLo(string town)
        {
            string lo;
            return TownLoZones.TryGetValue(town ?? string.Empty, out lo) ? lo : string.Empty;
        }

        private static bool TryParseCoordinate(string text, double minimum, double maximum, out double value)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return false;
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= minimum && value <= maximum;
        }
'''
project = replace_once(project, old_preferred, new_preferred, 'Namibia LO dictionary')
project_path.write_text(project, encoding='utf-8')

utility_path = Path('src/CE.Tools.Civil3D/UtilityPlanningCommands.cs')
utility = utility_path.read_text(encoding='utf-8')
utility = replace_once(
    utility,
    '            model.AddChoice("RouteMode", "01 Route", "Route option", "Inside road reserve", "Use the inward cadastral offset or keep a midblock planning option for later review.", new[] { "Inside road reserve", "Midblock" });',
    '            model.AddChoice("RouteMode", "01 Route", "Route option", "Inside road reserve", "Inside road reserve offsets the cadastral boundary. Midblock sewer centreline creates an open centreline through the selected block/erf footprint for sewer planning.", new[] { "Inside road reserve", "Midblock sewer centreline" });',
    'midblock route choice')
utility = utility.replace('                    Polyline route = CreateInwardOffset(source, settings.Offset);', '                    Polyline route = CreatePlanningRoute(source, settings);')
utility = utility.replace('                    Polyline rebuilt = CreateInwardOffset(source, link.Settings.Offset);', '                    Polyline rebuilt = CreatePlanningRoute(source, link.Settings);')

insert_before = '        private static Polyline CreateInwardOffset(Polyline source, double distance)\n'
helper = '''        private static Polyline CreatePlanningRoute(Polyline source, UtilityRouteSettings settings)
        {
            if (settings != null && string.Equals(settings.RouteMode, "Midblock sewer centreline", StringComparison.OrdinalIgnoreCase))
                return CreateMidblockRoute(source, settings.Offset);
            return CreateInwardOffset(source, settings == null ? 0.0 : settings.Offset);
        }

        private static Polyline CreateMidblockRoute(Polyline source, double endInset)
        {
            if (source == null || source.NumberOfVertices < 3) return null;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            for (int index = 0; index < source.NumberOfVertices; index++)
            {
                Point2d point = source.GetPoint2dAt(index);
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
            if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY)) return null;
            double width = maxX - minX;
            double height = maxY - minY;
            if (width <= 1e-6 || height <= 1e-6) return null;
            double inset = Math.Max(0.0, Math.Min(Math.Abs(endInset), 0.25 * Math.Max(width, height)));
            var route = new Polyline(2);
            if (width >= height)
            {
                double y = 0.5 * (minY + maxY);
                route.AddVertexAt(0, new Point2d(minX + inset, y), 0.0, 0.0, 0.0);
                route.AddVertexAt(1, new Point2d(maxX - inset, y), 0.0, 0.0, 0.0);
            }
            else
            {
                double x = 0.5 * (minX + maxX);
                route.AddVertexAt(0, new Point2d(x, minY + inset), 0.0, 0.0, 0.0);
                route.AddVertexAt(1, new Point2d(x, maxY - inset), 0.0, 0.0, 0.0);
            }
            route.Closed = false;
            route.Elevation = source.Elevation;
            return route;
        }

'''
if insert_before not in utility:
    raise SystemExit('Missing patch marker: CreateInwardOffset helper insertion')
utility = utility.replace(insert_before, helper + insert_before, 1)
utility = replace_once(
    utility,
    '                new List<string> { "Boundary offset", settings.Offset.ToString("N2", CultureInfo.CurrentCulture) + " m" },',
    '                new List<string> { "Boundary offset", settings.Offset.ToString("N2", CultureInfo.CurrentCulture) + " m" },\n                new List<string> { "Route mode", settings.RouteMode ?? string.Empty },',
    'route mode report')
utility_path.write_text(utility, encoding='utf-8')

print('Applied Namibia map/location and utility planner patch.')
