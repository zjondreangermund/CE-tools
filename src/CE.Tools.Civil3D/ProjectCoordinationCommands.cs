using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProjectCoordinationCommands))]

namespace CETools.Civil3D
{
    public sealed class ProjectCoordinationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROJECTCOORDINATION", CommandFlags.Modal)]
        public void ProjectCoordination()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Project Coordination",
                "Coordinate discipline drawings and paper-space page setups without merging or exploding live Civil 3D design objects.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create master drawing from discipline XREFs", "CE_MASTERXREF", "Select Roads, Stormwater, Sewer and Water DWGs and create a new non-destructive master DWG containing them as XREFs at the same origin.", "01 Master Drawing"),
                    new DisciplineWorkflowAction("Multi-layout page setup manager", "CE_PAGESETUPMANAGER", "Copy the page/plot setup of one paper-space layout to multiple layouts in one popup workflow.", "02 Drawing Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_MASTERXREF", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateMasterXrefDrawing()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            var save = new PromptSaveFileOptions("\nChoose the new coordinated master drawing path: ")
            {
                DialogCaption = "CE Tools - Save Master Drawing",
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                InitialFileName = "CE-PROJECT-MASTER.dwg"
            };
            PromptFileNameResult destination = editor.GetFileNameForSave(save);
            if (destination.Status != PromptStatus.OK) return;
            string output = EnsureDwg(destination.StringResult);
            if (File.Exists(output))
            {
                editor.WriteMessage("\nCE_MASTERXREF stopped. Existing master drawings are never overwritten: {0}", output);
                return;
            }

            string[] disciplines = { "ROADS", "STORMWATER", "SEWER", "WATER" };
            var files = new List<MasterXrefInput>();
            foreach (string discipline in disciplines)
            {
                var open = new PromptOpenFileOptions("\nSelect the " + discipline + " design DWG: ")
                {
                    DialogCaption = "CE Tools - Select " + discipline + " Drawing",
                    Filter = "AutoCAD Drawing (*.dwg)|*.dwg"
                };
                PromptFileNameResult selected = editor.GetFileNameForOpen(open);
                if (selected.Status != PromptStatus.OK) return;
                string path = Path.GetFullPath(selected.StringResult);
                if (!File.Exists(path))
                {
                    editor.WriteMessage("\nCE_MASTERXREF stopped. File not found: {0}", path);
                    return;
                }
                files.Add(new MasterXrefInput(discipline, path));
            }

            string reviewText =
                "Create a new coordinated master drawing?\n\n" +
                "Master: " + output + "\n" +
                string.Join("\n", files.Select(item => item.Discipline + ": " + item.Path)) +
                "\n\nInsertion: 0,0,0 as XREFs. Source discipline DWGs are not modified.";
            if (MessageBox.Show(
                    reviewText,
                    "CE Tools - Coordinated Master Drawing",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
                using (var master = new Database(true, true))
                {
                    using (Transaction transaction = master.TransactionManager.StartTransaction())
                    {
                        BlockTable table = transaction.GetObject(master.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                        BlockTableRecord model = transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite, false) as BlockTableRecord;
                        foreach (MasterXrefInput input in files)
                        {
                            string blockName = "CE-XREF-" + input.Discipline;
                            ObjectId definition = master.AttachXref(input.Path, blockName);
                            if (definition.IsNull) throw new InvalidOperationException("AutoCAD could not attach " + input.Path);
                            ObjectId layerId = GetOrCreateLayer(master, transaction, "XREF-" + input.Discipline);
                            var reference = new BlockReference(Point3d.Origin, definition);
                            reference.SetDatabaseDefaults(master);
                            reference.LayerId = layerId;
                            model.AppendEntity(reference);
                            transaction.AddNewlyCreatedDBObject(reference, true);
                        }
                        transaction.Commit();
                    }
                    master.SaveAs(output, DwgVersion.Current);
                }
                editor.WriteMessage("\nCE_MASTERXREF complete. Master drawing created: {0}", output);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_MASTERXREF failed. Source DWGs were not modified. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PAGESETUPMANAGER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PageSetupManager()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<string> layouts = ReadPaperLayouts(document.Database);
            if (layouts.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PAGESETUPMANAGER: no paper-space layouts were found.");
                return;
            }
            var targetChoices = new List<string> { "All paper-space layouts" };
            targetChoices.AddRange(layouts);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Multi-Layout Page Setup Manager",
                "Choose one paper-space layout as the page/plot setup source and apply its plot settings to all layouts or one target layout. Viewports and drawing objects are not changed.");
            model.AddChoice("Source", "01 Source", "Source layout", layouts[0], "Page setup/plot settings are copied from this layout.", layouts);
            model.AddChoice("Target", "02 Target", "Target layouts", "All paper-space layouts", "Apply to all paper layouts or one selected target.", targetChoices);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string sourceName = model.Text("Source");
            string targetName = model.Text("Target");

            int updated = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary dictionary = transaction.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary == null || !dictionary.Contains(sourceName)) return;
                    Layout source = transaction.GetObject(dictionary.GetAt(sourceName), OpenMode.ForRead, false) as Layout;
                    if (source == null || source.ModelType) return;
                    using (var snapshot = new PlotSettings(false))
                    {
                        snapshot.CopyFrom(source);
                        foreach (DBDictionaryEntry entry in dictionary)
                        {
                            Layout target = transaction.GetObject(entry.Value, OpenMode.ForWrite, false) as Layout;
                            if (target == null || target.ModelType || string.Equals(target.LayoutName, sourceName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.Equals(targetName, "All paper-space layouts", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(target.LayoutName, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                            target.CopyFrom(snapshot);
                            updated++;
                        }
                    }
                    transaction.Commit();
                }
                document.Editor.WriteMessage("\nCE_PAGESETUPMANAGER complete. Layout page setups updated={0}; source={1}.", updated, sourceName);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PAGESETUPMANAGER failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYLOCATION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurveyLocation()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            var towns = new[]
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
            };
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Location and Coordinate System",
                "Choose a town. CE Tools searches the coordinate systems installed with Autodesk and assigns the matching LO definition when one can be identified. Existing geometry is never transformed by this command.");
            model.AddChoice("Town", "01 Location", "Town / project area", "Windhoek", "Most Namibian towns and common project centres are listed. Known locations are mapped to a preferred LO zone; Custom or unmapped locations open Autodesk's selector. Existing geometry is never transformed.", towns);
            model.AddChoice("Action", "02 Action", "Coordinate-system action", "Assign matching installed LO system", "Assign the installed matching definition or open Autodesk's native selector.", new[] { "Assign matching installed LO system", "Open Autodesk coordinate-system selector only" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string town = model.Text("Town");
            if (model.Text("Action").StartsWith("Open", StringComparison.OrdinalIgnoreCase) || town.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
            {
                document.SendStringToExecute("_.MAPCSASSIGN ", true, false, true);
                return;
            }
            string lo = PreferredLo(town);
            if (string.IsNullOrWhiteSpace(lo))
            {
                document.Editor.WriteMessage("\nCE_SURVEYLOCATION: no conservative LO preset is stored for {0}; opening Autodesk's selector.", town);
                document.SendStringToExecute("_.MAPCSASSIGN ", true, false, true);
                return;
            }
            string code = FindInstalledCoordinateSystem(lo);
            if (string.IsNullOrWhiteSpace(code))
            {
                document.Editor.WriteMessage("\nCE_SURVEYLOCATION: an installed coordinate-system definition matching {0} was not found; opening Autodesk's selector.", lo);
                document.SendStringToExecute("_.MAPCSASSIGN ", true, false, true);
                return;
            }
            try
            {
                civilDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode = code;
                document.Editor.WriteMessage("\nCE_SURVEYLOCATION complete. {0} -> installed Autodesk coordinate system {1}. Existing geometry was not transformed.", town, code);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SURVEYLOCATION could not assign {0}. {1}", code, exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_MAPLOCATION", CommandFlags.Modal)]
        public void MapLocation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - WGS84 Latitude / Longitude Map Tools",
                "Enter WGS84 decimal-degree latitude and longitude. CE Tools opens the point in the selected web map. This command does not alter drawing coordinates.");
            model.AddText("Latitude", "01 WGS84", "Latitude", "-22.5609", "Decimal degrees; south is negative. Values from -90 to 90 are accepted.");
            model.AddText("Longitude", "01 WGS84", "Longitude", "17.0658", "Decimal degrees; west is negative and east is positive. Values from -180 to 180 are accepted.");
            model.AddText("Northing", "02 NE / YX", "Northing (N / Y)", "0.000", "Survey Northing maps directly to drawing Y. Signed values are accepted.");
            model.AddText("Easting", "02 NE / YX", "Easting (E / X)", "0.000", "Survey Easting maps directly to drawing X. Signed values are accepted.");
            model.AddText("X", "02 NE / YX", "Drawing X / Easting", "0.000", "Drawing X is the Easting convention used by CE Tools.");
            model.AddText("Y", "02 NE / YX", "Drawing Y / Northing", "0.000", "Drawing Y is the Northing convention used by CE Tools.");
            model.AddChoice("Action", "03 Action", "Action", "Open WGS84 in map", "Open the WGS84 point, convert survey/drawing labels, or transform between drawing XY and WGS84 using the GeoLocationData stored in this DWG.", new[] { "Open WGS84 in map", "Northing / Easting -> Y / X", "Y / X -> Northing / Easting", "Drawing X / Y -> WGS84 Lat / Long", "WGS84 Lat / Long -> Drawing X / Y" });
            model.AddChoice("Provider", "03 Action", "Open in", "Google Maps", "Choose the web map opened for the WGS84 action.", new[] { "Google Maps", "Google Earth" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string action = model.Text("Action");
            if (string.Equals(action, "Drawing X / Y -> WGS84 Lat / Long", StringComparison.OrdinalIgnoreCase))
            {
                double xValue;
                double yValue;
                if (!TryParseSignedNumber(model.Text("X"), out xValue) ||
                    !TryParseSignedNumber(model.Text("Y"), out yValue))
                {
                    document.Editor.WriteMessage("\nCE_MAPLOCATION stopped. Enter valid Drawing X and Y values.");
                    return;
                }
                Point3d geo;
                string transformError;
                if (!GeoCoordinateTransform.TryDrawingToWgs84(
                        document.Database,
                        new Point3d(xValue, yValue, 0.0),
                        out geo,
                        out transformError))
                {
                    document.Editor.WriteMessage("\nCE_MAPLOCATION transformation stopped. {0}", transformError);
                    return;
                }
                string result = string.Format(
                    CultureInfo.CurrentCulture,
                    "Drawing X {0:N3} / Y {1:N3}\nLatitude {2:0.00000000}\nLongitude {3:0.00000000}",
                    xValue,
                    yValue,
                    geo.Y,
                    geo.X);
                MessageBox.Show(result, "CE Tools - Drawing XY / WGS84", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.Equals(action, "WGS84 Lat / Long -> Drawing X / Y", StringComparison.OrdinalIgnoreCase))
            {
                double transformLatitude;
                double transformLongitude;
                if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out transformLatitude) ||
                    !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out transformLongitude))
                {
                    document.Editor.WriteMessage("\nCE_MAPLOCATION stopped. Enter valid WGS84 latitude/longitude.");
                    return;
                }
                Point3d drawingPoint;
                string transformError;
                if (!GeoCoordinateTransform.TryWgs84ToDrawing(
                        document.Database,
                        transformLatitude,
                        transformLongitude,
                        0.0,
                        out drawingPoint,
                        out transformError))
                {
                    document.Editor.WriteMessage("\nCE_MAPLOCATION transformation stopped. {0}", transformError);
                    return;
                }
                string result = string.Format(
                    CultureInfo.CurrentCulture,
                    "Latitude {0:0.00000000} / Longitude {1:0.00000000}\nDrawing X {2:N3}\nDrawing Y {3:N3}",
                    transformLatitude,
                    transformLongitude,
                    drawingPoint.X,
                    drawingPoint.Y);
                MessageBox.Show(result, "CE Tools - WGS84 / Drawing XY", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!string.Equals(action, "Open WGS84 in map", StringComparison.OrdinalIgnoreCase))
            {
                double first;
                double second;
                bool neToYx = action.StartsWith("Northing", StringComparison.OrdinalIgnoreCase);
                string firstKey = neToYx ? "Northing" : "Y";
                string secondKey = neToYx ? "Easting" : "X";
                if (!TryParseSignedNumber(model.Text(firstKey), out first) || !TryParseSignedNumber(model.Text(secondKey), out second))
                {
                    document.Editor.WriteMessage("\nCE_MAPLOCATION stopped. Enter valid signed coordinate values for the selected conversion.");
                    return;
                }
                string result = neToYx
                    ? string.Format(CultureInfo.CurrentCulture, "Northing {0:N3} -> Y {0:N3}\nEasting {1:N3} -> X {1:N3}", first, second)
                    : string.Format(CultureInfo.CurrentCulture, "Y {0:N3} -> Northing {0:N3}\nX {1:N3} -> Easting {1:N3}", first, second);
                MessageBox.Show(result, "CE Tools - NE / YX Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
                document.Editor.WriteMessage("\nCE_MAPLOCATION coordinate conversion: {0}", result.Replace("\n", "; "));
                return;
            }
            double latitude;
            double longitude;
            if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out latitude) ||
                !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out longitude))
            {
                document.Editor.WriteMessage("\nCE_MAPLOCATION stopped. Enter valid signed WGS84 latitude (-90 to 90) and longitude (-180 to 180).");
                return;
            }
            string lat = latitude.ToString("0.########", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("0.########", CultureInfo.InvariantCulture);
            string url = string.Equals(model.Text("Provider"), "Google Earth", StringComparison.OrdinalIgnoreCase)
                ? "https://earth.google.com/web/@" + lat + "," + lon + ",1000a,1000d,35y,0h,0t,0r"
                : "https://www.google.com/maps/search/?api=1&query=" + lat + "%2C" + lon;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                document.Editor.WriteMessage("\nCE_MAPLOCATION opened {0}: {1}, {2}.", model.Text("Provider"), lat, lon);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_MAPLOCATION could not open the browser. {0}", exception.Message);
            }
        }

        private static readonly IDictionary<string, string> TownLoZones =
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
                { "Uis", "LO15" }, { "Usakos", "LO15" }, { "Walvis Bay", "LO15" }, { "Windhoek", "LO17" },
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
            };

        private static string PreferredLo(string town)
        {
            string lo;
            return TownLoZones.TryGetValue(town ?? string.Empty, out lo) ? lo : string.Empty;
        }

        private static bool TryParseSignedNumber(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool TryParseCoordinate(string text, double minimum, double maximum, out double value)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return false;
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= minimum && value <= maximum;
        }

        private static string FindInstalledCoordinateSystem(string search)
        {
            string[] codes;
            try { codes = SettingsUnitZone.GetAllCodes(); }
            catch { return string.Empty; }
            foreach (string code in codes)
            {
                try
                {
                    SettingsCoordinateSystem current = SettingsUnitZone.GetCoordinateSystemByCode(code);
                    string combined = string.Join(" ", code, current.Description, current.Category, current.Projection, current.Datum);
                    string compact = combined.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
                    string wanted = search.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
                    if (compact.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return code;
                }
                catch { }
            }
            return string.Empty;
        }

        private static List<string> ReadPaperLayouts(Database database)
        {
            var result = new List<string>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout != null && !layout.ModelType) result.Add(layout.LayoutName);
                }
            }
            return result.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string EnsureDwg(string path)
        {
            string value = Path.GetFullPath(path ?? string.Empty);
            return value.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ? value : value + ".dwg";
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class MasterXrefInput
        {
            internal MasterXrefInput(string discipline, string path)
            {
                Discipline = discipline;
                Path = path;
            }
            internal string Discipline { get; private set; }
            internal string Path { get; private set; }
        }
    }
}