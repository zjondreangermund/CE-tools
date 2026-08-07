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
                "Coordinate discipline drawings, paper-space page setups and survey/location information without merging or exploding live Civil 3D design objects.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create master drawing from discipline XREFs", "CE_MASTERXREF", "Select Roads, Stormwater, Sewer and Water DWGs and create a new non-destructive master DWG containing them as XREFs at the same origin.", "01 Master Drawing"),
                    new DisciplineWorkflowAction("Multi-layout page setup manager", "CE_PAGESETUPMANAGER", "Copy the page/plot setup of one paper-space layout to multiple layouts in one popup workflow.", "02 Drawing Production"),
                    new DisciplineWorkflowAction("Survey town / coordinate system", "CE_SURVEYLOCATION", "Choose a Namibian town and assign the best installed Autodesk LO coordinate-system definition.", "03 Survey and Maps"),
                    new DisciplineWorkflowAction("Latitude / longitude map tools", "CE_MAPLOCATION", "Open a WGS84 latitude/longitude position in Google Maps or Google Earth web.", "03 Survey and Maps")
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
            var towns = new[] { "Windhoek", "Swakopmund", "Walvis Bay", "Henties Bay", "Oshakati", "Rundu", "Keetmanshoop", "Custom / use Autodesk selector" };
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Location and Coordinate System",
                "Choose a town. CE Tools searches the coordinate systems installed with Autodesk and assigns the matching LO definition when one can be identified. Existing geometry is never transformed by this command.");
            model.AddChoice("Town", "01 Location", "Town / project area", "Windhoek", "Windhoek prefers LO17; Swakopmund/Walvis Bay/Henties Bay prefer LO15. Other towns open the Autodesk selector if a safe preset is not defined.", towns);
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
            model.AddDouble("Latitude", "01 WGS84", "Latitude", -22.5609, "Decimal degrees, south negative.");
            model.AddDouble("Longitude", "01 WGS84", "Longitude", 17.0658, "Decimal degrees, east positive.");
            model.AddChoice("Provider", "02 Map", "Open in", "Google Maps", "Choose the web map opened after confirmation.", new[] { "Google Maps", "Google Earth" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double latitude = model.Double("Latitude", 0.0);
            double longitude = model.Double("Longitude", 0.0);
            if (latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)
            {
                document.Editor.WriteMessage("\nCE_MAPLOCATION stopped. Latitude/longitude are outside WGS84 ranges.");
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

        private static string PreferredLo(string town)
        {
            if (string.Equals(town, "Windhoek", StringComparison.OrdinalIgnoreCase)) return "LO17";
            if (string.Equals(town, "Swakopmund", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(town, "Walvis Bay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(town, "Henties Bay", StringComparison.OrdinalIgnoreCase)) return "LO15";
            return string.Empty;
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
