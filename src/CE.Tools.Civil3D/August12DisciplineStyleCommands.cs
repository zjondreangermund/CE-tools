using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August12DisciplineStyleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Dedicated style centres. Each production discipline reads and writes only
    /// its own preset record and only exposes style categories used by that
    /// discipline. Saving one discipline can never overwrite another preset.
    /// </summary>
    public sealed class August12DisciplineStyleCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURVEYSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurveyStyles() { Open("Survey", SurveyKeys); }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PlatformStyles() { Open("Platforms", PlatformKeys); }

        [CommandMethod("CE_TOOLS", "CE_ROADSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadStyles() { Open("Roads", RoadKeys); }

        [CommandMethod("CE_TOOLS", "CE_SWSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StormwaterStyles() { Open("Stormwater", GravityNetworkKeys); }

        [CommandMethod("CE_TOOLS", "CE_SEWERSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SewerStyles() { Open("Sewer", GravityNetworkKeys); }

        [CommandMethod("CE_TOOLS", "CE_WATERSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void WaterStyles() { Open("Water", PressureNetworkKeys); }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BulkWaterStyles() { Open("Bulk Water", PressureNetworkKeys); }

        [CommandMethod("CE_TOOLS", "CE_PARKINGSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingStyles() { Open("Parking", ParkingKeys); }

        [CommandMethod("CE_TOOLS", "CE_FLOODSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FloodStyles() { Open("Flood", FloodKeys); }

        private static readonly string[] SurveyKeys =
        {
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Feature Line Style", "Feature Line Label Style", "Point Table Style", "Surface Table Style"
        };

        private static readonly string[] PlatformKeys =
        {
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Feature Line Style", "Feature Line Label Style", "Grading Style",
            "Section Style", "Section Label Set Style", "Section Label Style",
            "Section View Style", "Section View Band Set Style", "Section View Label Style",
            "Sample Line Style", "Sample Line Label Style", "Point Table Style", "Surface Table Style"
        };

        private static readonly string[] RoadKeys =
        {
            "Alignment Style", "Alignment Label Set Style", "Alignment Label Style",
            "Profile Style", "Profile Label Set Style", "Profile Label Style",
            "Profile View Style", "Profile View Band Set Style", "Profile View Label Style",
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Feature Line Style", "Feature Line Label Style", "Corridor Style", "Code Set Style",
            "Assembly Style", "Section Style", "Section Label Set Style", "Section Label Style",
            "Section View Style", "Section View Band Set Style", "Section View Label Style",
            "Sample Line Style", "Sample Line Label Style", "Mass Haul View Style", "Mass Haul Line Style",
            "Marker Style", "Alignment Table Style", "Point Table Style", "Surface Table Style"
        };

        private static readonly string[] GravityNetworkKeys =
        {
            "Alignment Style", "Alignment Label Set Style", "Alignment Label Style",
            "Profile Style", "Profile Label Set Style", "Profile Label Style",
            "Profile View Style", "Profile View Band Set Style", "Profile View Label Style",
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Pipe Style", "Pipe Label Style", "Structure Style", "Structure Label Style",
            "Pipe Rule Set", "Structure Rule Set", "Pipe Table Style", "Structure Table Style"
        };

        private static readonly string[] PressureNetworkKeys =
        {
            "Alignment Style", "Alignment Label Set Style", "Alignment Label Style",
            "Profile Style", "Profile Label Set Style", "Profile Label Style",
            "Profile View Style", "Profile View Band Set Style", "Profile View Label Style",
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Pressure Pipe Style", "Pressure Pipe Label Style", "Fitting Style", "Fitting Label Style",
            "Appurtenance Style", "Appurtenance Label Style", "Point Table Style", "Surface Table Style"
        };

        private static readonly string[] ParkingKeys =
        {
            "Surface Style", "Surface Label Style", "Point Style", "Point Label Style",
            "Feature Line Style", "Feature Line Label Style", "Parcel Style", "Parcel Label Style",
            "Grading Style", "Parcel Table Style", "Point Table Style", "Surface Table Style"
        };

        private static readonly string[] FloodKeys =
        {
            "Surface Style", "Surface Label Style", "Catchment Style", "Catchment Label Style",
            "Point Style", "Point Label Style", "Section Style", "Section Label Set Style",
            "Section View Style", "Section View Band Set Style", "Point Table Style", "Surface Table Style"
        };

        private static void Open(string discipline, IEnumerable<string> keys)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<string> styleKeys = (keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, List<string>> catalogue = CivilStyleCatalogV2.ReadProjectCatalogue(document, styleKeys);
            ProjectStyleSelection existing = August11DisciplineStylePresetManager.ReadPreset(document.Database, discipline);
            if (!existing.Exists)
                existing = CeGlobalDisciplineStyleDefaults.Read(discipline);
            if (!existing.Exists)
                existing = new ProjectStyleSelection { Exists = true, Discipline = discipline };
            existing.Discipline = discipline;

            var window = new ProjectStyleCenterWindow(
                new[] { discipline },
                styleKeys,
                catalogue,
                existing);
            window.Title = "CE Tools - " + discipline + " Style Centre";
            AcApplication.ShowModalWindow(window);

            if (window.ImportRequested)
            {
                document.SendStringToExecute("CE_PROJECTSTYLEIMPORT ", true, false, true);
                return;
            }
            if (!window.Accepted) return;

            ProjectStyleSelection selection = window.BuildSelection();
            selection.Discipline = discipline;
            August11DisciplineStylePresetManager.SavePreset(document.Database, selection);
            CeGlobalDisciplineStyleDefaults.Save(selection);
            August11DisciplineStylePresetManager.ActivateForProduction(document.Database, discipline);

            document.Editor.WriteMessage(
                "\nCE Tools {0} styles saved independently. Stored choices={1}.",
                discipline,
                selection.Values.Count.ToString(CultureInfo.InvariantCulture));
        }
    }
}
