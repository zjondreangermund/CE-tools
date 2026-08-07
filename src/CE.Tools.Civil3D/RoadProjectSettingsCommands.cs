using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadProjectSettingsCommands))]

namespace CETools.Civil3D
{
    public sealed class RoadProjectSettingsCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADSETTINGS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ConfigureRoadSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            RoadProductionSettings settings = RoadProductionSettings.Read(document.Database);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Settings",
                "Road-only Civil 3D production styles. These values are stored in this DWG and are used by road alignments, profiles, profile views, corridors and assemblies instead of sewer or other discipline selections.");
            model.AddChoice("AlignmentStyle", "01 Alignments", "Alignment style", settings.AlignmentStyle,
                "Style applied to generated road alignments.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Alignment Style"));
            model.AddChoice("AlignmentLabelSetStyle", "01 Alignments", "Alignment label-set style", settings.AlignmentLabelSetStyle,
                "Label set applied automatically to road alignments.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Alignment Label Set Style"));
            model.AddChoice("ProfileStyle", "02 Profiles", "Profile style", settings.ProfileStyle,
                "Style applied to generated road NGL/final profiles.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Style"));
            model.AddChoice("ProfileLabelSetStyle", "02 Profiles", "Profile label-set style", settings.ProfileLabelSetStyle,
                "Label set applied automatically to generated road profiles.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Label Set Style"));
            model.AddChoice("ProfileViewStyle", "03 Profile Views", "Profile-view style", settings.ProfileViewStyle,
                "Profile-view style used for road long sections.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile View Style"));
            model.AddChoice("ProfileViewBandSetStyle", "03 Profile Views", "Profile-view band-set style", settings.ProfileViewBandSetStyle,
                "Road band set. CE Tools prefers Road-Single-Band Set 1-Full Grid when it is installed.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile View Band Set Style"));
            model.AddChoice("CorridorStyle", "04 Corridors", "Corridor style", settings.CorridorStyle,
                "Style applied to generated and repaired road corridors.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Corridor Style"));
            model.AddChoice("CodeSetStyle", "04 Corridors", "Code-set style", settings.CodeSetStyle,
                "Code-set style applied to corridor and region display.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Code Set Style"));
            model.AddChoice("AssemblyStyle", "05 Assemblies", "Assembly style", settings.AssemblyStyle,
                "Preferred Civil 3D assembly style for road production.", CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Assembly Style"));
            model.AddText("ProfileLayer", "06 Layers", "Road profile layer", settings.ProfileLayer,
                "Layer used for generated road profile output.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            settings.AlignmentStyle = model.Text("AlignmentStyle");
            settings.AlignmentLabelSetStyle = model.Text("AlignmentLabelSetStyle");
            settings.ProfileStyle = model.Text("ProfileStyle");
            settings.ProfileLabelSetStyle = model.Text("ProfileLabelSetStyle");
            settings.ProfileViewStyle = model.Text("ProfileViewStyle");
            settings.ProfileViewBandSetStyle = model.Text("ProfileViewBandSetStyle");
            settings.CorridorStyle = model.Text("CorridorStyle");
            settings.CodeSetStyle = model.Text("CodeSetStyle");
            settings.AssemblyStyle = model.Text("AssemblyStyle");
            settings.ProfileLayer = string.IsNullOrWhiteSpace(model.Text("ProfileLayer"))
                ? "CE-ROAD-PROFILE"
                : model.Text("ProfileLayer").Trim();
            settings.Write(document.Database);
            document.Editor.WriteMessage(
                "\nCE_ROADSETTINGS complete. Road-only styles saved. Band set={0}.",
                string.IsNullOrWhiteSpace(settings.ProfileViewBandSetStyle)
                    ? "<drawing default>"
                    : settings.ProfileViewBandSetStyle);
        }
    }

    internal sealed class RoadProductionSettings
    {
        private const string RootName = "CE_TOOLS";
        private const string RecordName = "ROAD_PRODUCTION_SETTINGS";
        internal const string PreferredRoadBandSet = "Road-Single-Band Set 1-Full Grid";

        public string AlignmentStyle { get; set; } = string.Empty;
        public string AlignmentLabelSetStyle { get; set; } = string.Empty;
        public string ProfileStyle { get; set; } = string.Empty;
        public string ProfileLabelSetStyle { get; set; } = string.Empty;
        public string ProfileViewStyle { get; set; } = string.Empty;
        public string ProfileViewBandSetStyle { get; set; } = PreferredRoadBandSet;
        public string CorridorStyle { get; set; } = string.Empty;
        public string CodeSetStyle { get; set; } = string.Empty;
        public string AssemblyStyle { get; set; } = string.Empty;
        public string ProfileLayer { get; set; } = "CE-ROAD-PROFILE";

        public string Value(string category)
        {
            if (string.Equals(category, "Alignment Style", StringComparison.OrdinalIgnoreCase)) return AlignmentStyle;
            if (string.Equals(category, "Alignment Label Set Style", StringComparison.OrdinalIgnoreCase)) return AlignmentLabelSetStyle;
            if (string.Equals(category, "Profile Style", StringComparison.OrdinalIgnoreCase)) return ProfileStyle;
            if (string.Equals(category, "Profile Label Set Style", StringComparison.OrdinalIgnoreCase)) return ProfileLabelSetStyle;
            if (string.Equals(category, "Profile View Style", StringComparison.OrdinalIgnoreCase)) return ProfileViewStyle;
            if (string.Equals(category, "Profile View Band Set Style", StringComparison.OrdinalIgnoreCase)) return ProfileViewBandSetStyle;
            if (string.Equals(category, "Corridor Style", StringComparison.OrdinalIgnoreCase)) return CorridorStyle;
            if (string.Equals(category, "Code Set Style", StringComparison.OrdinalIgnoreCase)) return CodeSetStyle;
            if (string.Equals(category, "Assembly Style", StringComparison.OrdinalIgnoreCase)) return AssemblyStyle;
            return string.Empty;
        }

        public static RoadProductionSettings Read(Database database)
        {
            var settings = new RoadProductionSettings();
            if (database == null) return settings;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                if (named == null || !named.Contains(RootName)) return settings;
                DBDictionary root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForRead, false) as DBDictionary;
                if (root == null || !root.Contains(RecordName)) return settings;
                Xrecord record = transaction.GetObject(root.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return settings;
                foreach (TypedValue value in record.Data)
                {
                    if (value.TypeCode != (int)DxfCode.Text) continue;
                    string text = value.Value as string;
                    int split = string.IsNullOrEmpty(text) ? -1 : text.IndexOf('=');
                    if (split <= 0) continue;
                    Apply(settings, text.Substring(0, split), text.Substring(split + 1));
                }
            }
            return settings;
        }

        public void Write(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                DBDictionary root;
                if (named.Contains(RootName))
                    root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForWrite, false) as DBDictionary;
                else
                {
                    root = new DBDictionary();
                    named.SetAt(RootName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }
                Xrecord record;
                if (root.Contains(RecordName))
                    record = transaction.GetObject(root.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    root.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                record.Data = new ResultBuffer(
                    Pair("AlignmentStyle", AlignmentStyle),
                    Pair("AlignmentLabelSetStyle", AlignmentLabelSetStyle),
                    Pair("ProfileStyle", ProfileStyle),
                    Pair("ProfileLabelSetStyle", ProfileLabelSetStyle),
                    Pair("ProfileViewStyle", ProfileViewStyle),
                    Pair("ProfileViewBandSetStyle", ProfileViewBandSetStyle),
                    Pair("CorridorStyle", CorridorStyle),
                    Pair("CodeSetStyle", CodeSetStyle),
                    Pair("AssemblyStyle", AssemblyStyle),
                    Pair("ProfileLayer", ProfileLayer));
                transaction.Commit();
            }
        }

        public static string SelectPreferredBandSet(IEnumerable<string> names, string current)
        {
            List<string> values = names == null
                ? new List<string>()
                : names.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (!string.IsNullOrWhiteSpace(current) && values.Any(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase)))
                return values.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
            string exact = values.FirstOrDefault(item => string.Equals(item, PreferredRoadBandSet, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;
            string close = values.FirstOrDefault(item =>
                item.IndexOf("Road", StringComparison.OrdinalIgnoreCase) >= 0 &&
                item.IndexOf("Single", StringComparison.OrdinalIgnoreCase) >= 0 &&
                item.IndexOf("Band Set 1", StringComparison.OrdinalIgnoreCase) >= 0 &&
                item.IndexOf("Full Grid", StringComparison.OrdinalIgnoreCase) >= 0);
            return string.IsNullOrWhiteSpace(close) ? current ?? string.Empty : close;
        }

        private static TypedValue Pair(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + (value ?? string.Empty));
        }

        private static void Apply(RoadProductionSettings settings, string key, string value)
        {
            if (key == "AlignmentStyle") settings.AlignmentStyle = value;
            else if (key == "AlignmentLabelSetStyle") settings.AlignmentLabelSetStyle = value;
            else if (key == "ProfileStyle") settings.ProfileStyle = value;
            else if (key == "ProfileLabelSetStyle") settings.ProfileLabelSetStyle = value;
            else if (key == "ProfileViewStyle") settings.ProfileViewStyle = value;
            else if (key == "ProfileViewBandSetStyle") settings.ProfileViewBandSetStyle = value;
            else if (key == "CorridorStyle") settings.CorridorStyle = value;
            else if (key == "CodeSetStyle") settings.CodeSetStyle = value;
            else if (key == "AssemblyStyle") settings.AssemblyStyle = value;
            else if (key == "ProfileLayer") settings.ProfileLayer = value;
        }
    }
}
