using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DrawingLegendCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Creates paper-space legends from selected road, stormwater, water and sewer objects.
    /// Quantities are derived only from selected drawing geometry.
    /// </summary>
    public sealed class DrawingLegendCommands
    {
        private sealed class LegendStyleOptions
        {
            public Autodesk.AutoCAD.Colors.Color Color;
            public string Linetype;
            public double LinetypeScale;
        }

        private sealed class LegendRow
        {
            public string Category;
            public string Layer;
            public string Description;
            public string ObjectType;
            public string PartFamily;
            public string PartSize;
            public string Linetype;
            public string SymbolKind;
            public string SignCode;
            public Autodesk.AutoCAD.Colors.Color DisplayColor;
            public double LinetypeScale = 1.0;
            public List<ObjectId> Objects = new List<ObjectId>();
            public double Length;
            public double Area;
            public int Count;
            public bool HasLength;
            public bool HasArea;
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWINGLEGEND", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateLegend()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drawing Legend",
                "Select roads, stormwater, water or sewer objects. Selecting a Civil 3D pipe network (or one of its parts) expands to all pipes, structures and supported appurtenances in that network. Part family, size, linetype, lengths and quantities are grouped into legend rows.");
            List<string> availableLayouts = GetPaperLayoutNames(doc.Database);
            var layoutChoices = new List<string> { "All layouts" };
            layoutChoices.AddRange(availableLayouts);
            settings.AddChoice("TargetLayout", "01 Placement", "Paper-space layout", "All layouts",
                "Choose All layouts or a specific paper-space layout from this drawing.",
                layoutChoices);
            settings.AddChoice("InsertionMode", "01 Placement", "Insertion point", "Pick point",
                "Pick the legend insertion point in the drawing, or enter paper-space coordinates.",
                new List<string> { "Pick point", "Specify coordinates" });
            settings.AddText("InsertionX", "01 Placement", "Insertion X", "20",
                "Used only when Specify coordinates is selected.");
            settings.AddText("InsertionY", "01 Placement", "Insertion Y", "250",
                "Used only when Specify coordinates is selected.");
            settings.AddPositiveDouble("ObjectScale", "02 Appearance", "Object sample scale", 1.0,
                "Scale multiplier applied to general representative objects in the legend.");
            settings.AddPositiveDouble("StructureScale", "02 Appearance", "Water / sewer structure scale", 1.0,
                "Independent scale multiplier applied only to water and sewer structure symbols.");
            settings.AddPositiveDouble("RoadSignImageScale", "02 Appearance", "Road sign image scale", 1.0,
                "Independent scale multiplier for road-sign block/image symbols only.");
            List<string> colorChoices = GetLegendColorChoices();
            settings.AddChoice("LegendTextColor", "02 Appearance", "Legend text color",
                "White (#FFFFFF, 7)", "Color applied to the main heading, discipline headings and all legend descriptions.",
                colorChoices);
            List<string> linetypeChoices = GetLegendLinetypeChoices(doc.Database);
            settings.AddChoice("StructureLinetype", "02 Appearance", "Structure linetype",
                "Use source", "Choose the linetype used by exploded water and sewer structure symbols.", linetypeChoices);
            foreach (string discipline in new[] { "Roads", "Stormwater", "Water", "Sewer" })
            {
                string prefix = discipline + "Style";
                settings.AddChoice(prefix + "Color", "02 Appearance", discipline + " line / symbol color",
                    "Use source", "Choose the displayed color for this discipline.", colorChoices);
                settings.AddChoice(prefix + "Linetype", "02 Appearance", discipline + " linetype",
                    "Use source", "Choose the displayed linetype for this discipline.", linetypeChoices);
                settings.AddPositiveDouble(prefix + "LinetypeScale", "02 Appearance", discipline + " linetype scale",
                    1.0, "Linetype scale multiplier for this discipline.");
            }
            settings.AddChoice("AnnotativeText", "02 Appearance", "Use annotation scale for text", "NO",
                "YES makes legend text annotative; NO keeps the entered paper-space text height.",
                new List<string> { "NO", "YES" });
            settings.AddPositiveDouble("HeadingHeight", "02 Appearance", "Legend heading text size", 5.0,
                "Paper-space text height for the LEGEND heading and discipline headings.");
            settings.AddPositiveDouble("TextHeight", "02 Appearance", "Object text size", 2.5,
                "Paper-space text height for descriptions and quantities.");
            settings.AddPositiveDouble("SampleWidth", "02 Appearance", "Object sample width", 18.0,
                "Target paper-space width for each representative object sample.");
            settings.AddText("LengthUnit", "02 Appearance", "Length unit label", "m",
                "Unit label used for calculated curve lengths, for example m or mm.");
            settings.AddText("LegendHeading", "03 Headings", "Main legend heading", "LEGEND:",
                "Main heading text.");
            settings.AddText("RoadHeading", "03 Headings", "Road heading", "ROADS:",
                "Discipline heading for road objects.");
            settings.AddText("SwHeading", "03 Headings", "Stormwater heading", "STORMWATER:",
                "Discipline heading for stormwater objects.");
            settings.AddText("WaterHeading", "03 Headings", "Water heading", "NEW WATER:",
                "Discipline heading for water objects.");
            settings.AddText("SewerHeading", "03 Headings", "Sewer heading", "NEW SEWER:",
                "Discipline heading for sewer objects.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double objectScale = ReadPositive(settings.Text("ObjectScale"), 1.0);
            double structureScale = ReadPositive(settings.Text("StructureScale"), 1.0);
            double roadSignImageScale = ReadPositive(settings.Text("RoadSignImageScale"), 1.0);
            Autodesk.AutoCAD.Colors.Color legendTextColor = ParseLegendColor(settings.Text("LegendTextColor"));
            bool annotativeText = string.Equals(settings.Text("AnnotativeText"), "YES", StringComparison.OrdinalIgnoreCase);
            var disciplineStyles = new Dictionary<string, LegendStyleOptions>(StringComparer.OrdinalIgnoreCase);
            foreach (string discipline in new[] { "ROADS", "STORMWATER", "WATER", "SEWER" })
            {
                string prefix = char.ToUpper(discipline[0]) + discipline.Substring(1).ToLowerInvariant() + "Style";
                disciplineStyles[discipline] = new LegendStyleOptions {
                    Color = ParseLegendColor(settings.Text(prefix + "Color")),
                    Linetype = settings.Text(prefix + "Linetype"),
                    LinetypeScale = ReadPositive(settings.Text(prefix + "LinetypeScale"), 1.0)
                };
            }
            double headingHeight = ReadPositive(settings.Text("HeadingHeight"), 5.0);
            double textHeight = ReadPositive(settings.Text("TextHeight"), 2.5);
            double sampleWidth = ReadPositive(settings.Text("SampleWidth"), 18.0);
            string mainHeading = settings.Text("LegendHeading");
            string[] categories = { "ROADS", "STORMWATER", "WATER", "SEWER" };
            string[] headings = {
                settings.Text("RoadHeading"), settings.Text("SwHeading"),
                settings.Text("WaterHeading"), settings.Text("SewerHeading")
            };

            var rows = new List<LegendRow>();
            for (int i = 0; i < categories.Length; i++)
            {
                PromptSelectionResult selection = ed.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nSelect " + categories[i] + " objects for the legend: " });
                if (selection.Status != PromptStatus.OK) continue;
                rows.AddRange(BuildRows(doc.Database, selection.Value, categories[i]));
            }
            if (rows.Count == 0)
            {
                ed.WriteMessage("\nCE_DRAWINGLEGEND: no supported objects were selected.");
                return;
            }

            foreach (LegendRow row in rows)
            {
                LegendStyleOptions style;
                if (disciplineStyles.TryGetValue(row.Category ?? string.Empty, out style) && style != null)
                {
                    if (style.Color != null) row.DisplayColor = style.Color;
                    if (!string.IsNullOrWhiteSpace(style.Linetype) &&
                        !string.Equals(style.Linetype, "Use source", StringComparison.OrdinalIgnoreCase))
                        row.Linetype = style.Linetype;
                    if (style.LinetypeScale > 0) row.LinetypeScale *= style.LinetypeScale;
                }
            }

            string structureLinetype = settings.Text("StructureLinetype");
            if (!string.IsNullOrWhiteSpace(structureLinetype) &&
                !string.Equals(structureLinetype, "Use source", StringComparison.OrdinalIgnoreCase))
            {
                foreach (LegendRow row in rows.Where(r => string.Equals(r.SymbolKind, "STRUCTURE", StringComparison.OrdinalIgnoreCase)))
                    row.Linetype = structureLinetype;
            }

            // Descriptions are derived from selected object data; no command-line description prompts.
            List<string> layouts = GetTargetLayouts(doc.Database, settings);
            if (layouts.Count == 0)
            {
                ed.WriteMessage("\nCE_DRAWINGLEGEND: no paper-space layouts were selected.");
                return;
            }

            Point3d insertion;
            if (string.Equals(settings.Text("InsertionMode"), "Pick point", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult picked = ed.GetPoint("\\nPick the legend insertion point in paper space: ");
                if (picked.Status != PromptStatus.OK) return;
                insertion = picked.Value;
            }
            else
            {
                double insertionX = ReadNumber(settings.Text("InsertionX"), 20.0);
                double insertionY = ReadNumber(settings.Text("InsertionY"), 250.0);
                insertion = new Point3d(insertionX, insertionY, 0.0);
            }
            int created = 0;
            foreach (string layoutName in layouts)
            {
                if (CreateOnLayout(doc.Database, layoutName, insertion, rows,
                    categories, headings, mainHeading, headingHeight, textHeight,
                    sampleWidth, objectScale, structureScale, roadSignImageScale, annotativeText,
                    legendTextColor, settings.Text("LengthUnit")))
                    created++;
            }

            ed.WriteMessage("\nCE_DRAWINGLEGEND: created {0} legend(s), {1} grouped rows from selected objects.",
                created, rows.Count);
        }

        private static List<string> GetLegendColorChoices()
        {
            var choices = new List<string> { "Use source" };
            for (short index = 1; index <= 255; index++)
            {
                try
                {
                    var color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index);
                    System.Drawing.Color rgb = color.ColorValue;
                    string name = index == 1 ? "Red" : index == 2 ? "Yellow" :
                        index == 3 ? "Green" : index == 4 ? "Cyan" :
                        index == 5 ? "Blue" : index == 6 ? "Magenta" :
                        index == 7 ? "White" : index == 8 ? "Dark grey" :
                        index == 9 ? "Light grey" : "ACI " + index.ToString(CultureInfo.InvariantCulture);
                    choices.Add(name + " (#" + rgb.R.ToString("X2") + rgb.G.ToString("X2") +
                        rgb.B.ToString("X2") + ", " + index.ToString(CultureInfo.InvariantCulture) + ")");
                }
                catch { choices.Add("ACI " + index.ToString(CultureInfo.InvariantCulture)); }
            }
            return choices;
        }

        private static List<string> GetLegendLinetypeChoices(Database database)
        {
            var choices = new List<string> { "Use source" };
            try
            {
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    LinetypeTable table = (LinetypeTable)tr.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        LinetypeTableRecord record = tr.GetObject(id, OpenMode.ForRead) as LinetypeTableRecord;
                        if (record != null && !choices.Contains(record.Name, StringComparer.OrdinalIgnoreCase))
                            choices.Add(record.Name);
                    }
                    tr.Commit();
                }
            }
            catch { }
            return choices;
        }

        private static Autodesk.AutoCAD.Colors.Color ParseLegendColor(string choice)
        {
            if (string.IsNullOrWhiteSpace(choice) ||
                choice.StartsWith("Use source", StringComparison.OrdinalIgnoreCase)) return null;
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(choice, @",\s*(\d+)\)");
            short index = 0;
            if (!match.Success || !short.TryParse(match.Groups[1].Value, out index)) return null;
            try { return Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index); }
            catch { return null; }
        }

        private static double ReadPositive(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                parsed > 0 && !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? parsed : fallback;
        }

        private static List<LegendRow> BuildRows(Database database, SelectionSet selection, string category)
        {
            var grouped = new Dictionary<string, LegendRow>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                List<ObjectId> selectedIds = selection.Cast<SelectedObject>()
                    .Where(x => x != null && !x.ObjectId.IsNull)
                    .Select(x => x.ObjectId).Distinct().ToList();
                List<ObjectId> expandedIds = ExpandNetworkSelection(selectedIds, tr);

                foreach (ObjectId id in expandedIds)
                {
                    Entity entity = null;
                    try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity == null) continue;

                    string runtimeType = entity.GetType().Name;
                    string blockName = GetBlockName(entity, tr);
                    string symbolSourceName = GetSymbolSourceName(entity, tr, blockName);
                    string signDescriptor = GetSignDescriptorText(entity, tr, blockName, symbolSourceName);
                    string searchableName = runtimeType + " " + blockName + " " + symbolSourceName + " " +
                        (entity.Layer ?? string.Empty) + " " + signDescriptor;
                    string upperName = searchableName.ToUpperInvariant();
                    bool isPipe = runtimeType.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isStructure = runtimeType.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isCogoPoint = runtimeType.IndexOf("CogoPoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        runtimeType.IndexOf("SurveyPoint", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDrift = upperName.Contains("DRIFT");
                    string detectedSignCode = isDrift ? string.Empty : GetRoadSignCode(entity, symbolSourceName, tr);
                    bool isRoadSign = !isDrift && (upperName.Contains("SIGN") || upperName.Contains("R1") ||
                        upperName.Contains("W2") || upperName.Contains("W3") ||
                        upperName.Contains("SPEED") || upperName.Contains("GENTLE") ||
                        upperName.Contains("SHARP") || upperName.Contains("WARNING") ||
                        !string.IsNullOrWhiteSpace(detectedSignCode));
                    bool isRoadObject = !isRoadSign && !isDrift && !isCogoPoint &&
                        (upperName.Contains("ALIGNMENT") || upperName.Contains("CORRIDOR") ||
                         upperName.Contains("FEATURELINE") || upperName.Contains("ROAD") ||
                         runtimeType.IndexOf("Alignment", StringComparison.OrdinalIgnoreCase) >= 0);
                    string partFamily = ReadPartText(entity, "PartFamilyName", "FamilyName");
                    string partSize = ReadPartText(entity, "PartSizeName", "SizeName", "PartSize");
                    string signCode = isRoadSign ? detectedSignCode : string.Empty;
                    if (isRoadSign && string.IsNullOrWhiteSpace(signCode) &&
                        searchableName.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0)
                        signCode = "R1";
                    string objectType = isPipe ? "Pipe" : isStructure ? "Structure / fitting" :
                        isRoadSign ? "Road Sign Image" : isDrift ? "Drift" :
                        isCogoPoint ? "COGO Point" : isRoadObject ? "Road Object" : runtimeType;
                    string symbolKind = isRoadSign ? "SIGN" : isDrift ? "DRIFT" :
                        isCogoPoint ? "COGO" : isRoadObject ? "ROAD" :
                        isPipe ? "PIPE" : isStructure ? "STRUCTURE" : "GENERIC";
                    string linetype = ResolveLinetype(entity, tr);
                    string description = isRoadSign ? BuildRoadSignDescription(signDescriptor, signCode) :
                        isDrift ? (string.Equals(category, "STORMWATER", StringComparison.OrdinalIgnoreCase) ? "SW Drift" : "Drift") :
                        isCogoPoint ? BuildCogoDescription(entity) :
                        isRoadObject ? "Road" :
                        BuildObjectDescription(category, entity, objectType, partFamily, partSize);
                    string key = string.Join("|", new[] {
                        entity.Layer ?? string.Empty, runtimeType, partFamily, partSize,
                        linetype, description, symbolKind, signCode,
                        isRoadSign ? symbolSourceName : string.Empty,
                        entity.Color == null ? string.Empty :
                            entity.Color.ColorMethod.ToString() + ":" +
                            entity.Color.ColorIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                            entity.Color.ColorValue.ToArgb().ToString(CultureInfo.InvariantCulture),
                        entity.LinetypeScale.ToString("R", CultureInfo.InvariantCulture)
                    });

                    LegendRow row;
                    if (!grouped.TryGetValue(key, out row))
                    {
                        row = new LegendRow {
                            Category = category,
                            Layer = entity.Layer,
                            Description = description,
                            ObjectType = objectType,
                            PartFamily = partFamily,
                            PartSize = partSize,
                            Linetype = linetype,
                            SymbolKind = symbolKind,
                            SignCode = signCode,
                            DisplayColor = ResolveDisplayColor(entity, tr),
                            LinetypeScale = ReadPositive(Convert.ToString(entity.LinetypeScale, CultureInfo.InvariantCulture), 1.0),
                            HasLength = isPipe || (!isStructure && entity is Curve),
                            HasArea = entity is Hatch || entity is Region
                        };
                        grouped.Add(key, row);
                    }

                    row.Objects.Add(id);
                    row.Count++;

                    double measuredLength;
                    if (!isStructure && TryReadNetworkLength(entity, out measuredLength))
                    {
                        row.Length += measuredLength;
                        row.HasLength = true;
                    }
                    else
                    {
                        Curve curve = isStructure ? null : entity as Curve;
                        if (curve != null)
                        {
                            try
                            {
                                double length = curve.GetDistanceAtParameter(curve.EndParam) -
                                    curve.GetDistanceAtParameter(curve.StartParam);
                                if (length > 0 && !double.IsInfinity(length)) row.Length += length;
                            }
                            catch { }
                        }
                    }

                    Polyline polyline = entity as Polyline;
                    if (polyline != null && polyline.Closed)
                    {
                        try { row.Area += Math.Abs(polyline.Area); row.HasArea = true; }
                        catch { }
                    }
                }
                tr.Commit();
            }
            return grouped.Values
                .OrderBy(x => LegendObjectOrder(x.ObjectType))
                .ThenBy(x => ExtractNominalSize(x.Description))
                .ThenBy(x => x.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Layer, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int LegendDisplayOrder(string symbolKind)
        {
            if (string.Equals(symbolKind, "ROAD", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(symbolKind, "SIGN", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(symbolKind, "COGO", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(symbolKind, "DRIFT", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(symbolKind, "PIPE", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(symbolKind, "STRUCTURE", StringComparison.OrdinalIgnoreCase)) return 5;
            return 6;
        }

        private static int LegendObjectOrder(string objectType)
        {
            if (!string.IsNullOrWhiteSpace(objectType) && objectType.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (!string.IsNullOrWhiteSpace(objectType) &&
                (objectType.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 objectType.IndexOf("Appurtenance", StringComparison.OrdinalIgnoreCase) >= 0)) return 1;
            return 2;
        }

        private static double ExtractNominalSize(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return double.MaxValue;
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(description, @"(?<!\d)(\d+(?:[.,]\d+)?)\s*mm",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            double value = 0.0;
            return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : double.MaxValue;
        }

        private static List<ObjectId> ExpandNetworkSelection(IEnumerable<ObjectId> selectedIds, Transaction tr)
        {
            var result = new HashSet<ObjectId>();
            foreach (ObjectId selectedId in selectedIds)
            {
                if (selectedId.IsNull) continue;
                result.Add(selectedId);
                DBObject selected = null;
                try { selected = tr.GetObject(selectedId, OpenMode.ForRead, false); }
                catch { }
                if (selected == null) continue;

                // Selecting a PipeNetwork expands to every pipe and structure in that network.
                bool expanded = AddNetworkMembers(selected, tr, result);
                if (!expanded)
                {
                    // Also support selecting an individual pipe/structure: use its NetworkId
                    // to expand the whole parent network, so the legend remains complete.
                    object networkValue = ReadProperty(selected, "NetworkId");
                    if (networkValue is ObjectId)
                    {
                        ObjectId networkId = (ObjectId)networkValue;
                        if (!networkId.IsNull && networkId != selectedId)
                        {
                            try
                            {
                                DBObject network = tr.GetObject(networkId, OpenMode.ForRead, false);
                                if (network != null) AddNetworkMembers(network, tr, result);
                            }
                            catch { }
                        }
                    }
                }
            }
            return result.ToList();
        }

        private static bool AddNetworkMembers(DBObject network, Transaction tr, HashSet<ObjectId> result)
        {
            bool found = false;
            foreach (string methodName in new[] { "GetPipeIds", "GetStructureIds", "GetAppurtenanceIds" })
            {
                try
                {
                    MethodInfo method = network.GetType().GetMethod(methodName,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    object raw = method.Invoke(network, null);
                    System.Collections.IEnumerable ids = raw as System.Collections.IEnumerable;
                    if (ids == null) continue;
                    foreach (object value in ids)
                    {
                        if (value is ObjectId && !((ObjectId)value).IsNull)
                        {
                            result.Add((ObjectId)value);
                            found = true;
                        }
                    }
                }
                catch { }
            }
            return found;
        }

        private static object ReadProperty(object instance, string propertyName)
        {
            if (instance == null) return null;
            try
            {
                var property = instance.GetType().GetProperty(propertyName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                return property != null && property.CanRead ? property.GetValue(instance, null) : null;
            }
            catch { return null; }
        }

        private static string ReadPartText(object instance, params string[] names)
        {
            foreach (string name in names)
            {
                object value = ReadProperty(instance, name);
                if (value == null) continue;
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            return string.Empty;
        }

        private static Autodesk.AutoCAD.Colors.Color ResolveDisplayColor(Entity entity, Transaction tr)
        {
            try
            {
                if (entity.Color != null &&
                    entity.Color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByLayer)
                {
                    LayerTable layers = (LayerTable)tr.GetObject(entity.Database.LayerTableId, OpenMode.ForRead);
                    if (layers.Has(entity.Layer))
                    {
                        LayerTableRecord layer = (LayerTableRecord)tr.GetObject(layers[entity.Layer], OpenMode.ForRead);
                        if (layer.Color != null) return layer.Color;
                    }
                }
                return entity.Color;
            }
            catch { return entity.Color; }
        }

        private static string ResolveLinetype(Entity entity, Transaction tr)
        {
            try
            {
                if (entity.Linetype == "BYLAYER")
                {
                    LayerTable layers = (LayerTable)tr.GetObject(entity.Database.LayerTableId, OpenMode.ForRead);
                    if (layers.Has(entity.Layer))
                    {
                        LayerTableRecord layer = (LayerTableRecord)tr.GetObject(layers[entity.Layer], OpenMode.ForRead);
                        return layer.LinetypeObjectId.IsNull ? "BYLAYER" :
                            ((LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead)).Name;
                    }
                }
                if (entity.Linetype == "BYBLOCK") return "BYBLOCK";
                return entity.Linetype;
            }
            catch { return entity.Linetype ?? "CONTINUOUS"; }
        }

        private static bool TryReadNetworkLength(object entity, out double length)
        {
            length = 0;
            foreach (string propertyName in new[] { "Length2D", "Length3D", "Length" })
            {
                object value = ReadProperty(entity, propertyName);
                if (value == null) continue;
                try
                {
                    double parsed = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (parsed > 0 && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                    {
                        length = parsed;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static string GetBlockName(Entity entity, Transaction tr)
        {
            BlockReference block = entity as BlockReference;
            if (block == null) return string.Empty;
            try
            {
                BlockTableRecord record = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                return record.Name ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string GetSymbolSourceName(Entity entity, Transaction tr, string blockName)
        {
            string name = blockName ?? string.Empty;
            object effective = ReadProperty(entity, "EffectiveName");
            if (effective != null && !string.IsNullOrWhiteSpace(Convert.ToString(effective, CultureInfo.InvariantCulture)))
                name = Convert.ToString(effective, CultureInfo.InvariantCulture);
            // RasterImage objects store the referenced file on their RasterImageDef.
            // Read ImageDefId first so SANRAL codes embedded in image filenames are retained.
            object imageDefId = ReadProperty(entity, "ImageDefId");
            if (imageDefId is ObjectId && !((ObjectId)imageDefId).IsNull)
            {
                try
                {
                    DBObject imageDefinition = tr.GetObject((ObjectId)imageDefId, OpenMode.ForRead, false);
                    object imageSourceFile = ReadProperty(imageDefinition, "SourceFileName");
                    object imageDefinitionName = ReadProperty(imageDefinition, "Name");
                    if (imageSourceFile != null && !string.IsNullOrWhiteSpace(Convert.ToString(imageSourceFile, CultureInfo.InvariantCulture)))
                        name = System.IO.Path.GetFileNameWithoutExtension(Convert.ToString(imageSourceFile, CultureInfo.InvariantCulture));
                    else if (imageDefinitionName != null)
                        name = Convert.ToString(imageDefinitionName, CultureInfo.InvariantCulture);
                }
                catch { }
            }

            object definitionId = ReadProperty(entity, "DefinitionId");
            if (definitionId is ObjectId && !((ObjectId)definitionId).IsNull)
            {
                try
                {
                    DBObject definition = tr.GetObject((ObjectId)definitionId, OpenMode.ForRead, false);
                    object sourceFile = ReadProperty(definition, "SourceFileName");
                    object definitionName = ReadProperty(definition, "Name");
                    if (sourceFile != null && !string.IsNullOrWhiteSpace(Convert.ToString(sourceFile, CultureInfo.InvariantCulture)))
                        name = System.IO.Path.GetFileNameWithoutExtension(Convert.ToString(sourceFile, CultureInfo.InvariantCulture));
                    else if (definitionName != null && string.IsNullOrWhiteSpace(name))
                        name = Convert.ToString(definitionName, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(name))
                name = entity.Layer ?? entity.GetType().Name;
            return name.Trim();
        }

        private static string GetRoadSignCode(Entity entity, string blockName, Transaction tr)
        {
            string search = (blockName ?? string.Empty) + " " + (entity.Layer ?? string.Empty) + " " +
                ReadPartText(entity, "EffectiveName", "Name", "Description", "RawDescription", "FullDescription");
            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                try
                {
                    foreach (ObjectId attributeId in block.AttributeCollection)
                    {
                        AttributeReference attribute = tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute == null) continue;
                        search += " " + attribute.Tag + " " + attribute.TextString;
                        if (!string.IsNullOrWhiteSpace(attribute.TextString) &&
                            (attribute.Tag.IndexOf("CODE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             attribute.Tag.IndexOf("SIGN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             attribute.Tag.IndexOf("NUMBER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             attribute.Tag.IndexOf("ID", StringComparison.OrdinalIgnoreCase) >= 0))
                            search += " " + attribute.TextString;
                    }
                    foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                    {
                        if (property != null && property.Value != null)
                            search += " " + property.PropertyName + " " +
                                Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                    }
                }
                catch { }
            }

            System.Text.RegularExpressions.Match codeMatch = System.Text.RegularExpressions.Regex.Match(search,
                @"\b[RW]\s*[-_]?\s*\d+(?:\.\d+)?[A-Z]?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (codeMatch.Success)
                return System.Text.RegularExpressions.Regex.Replace(codeMatch.Value, @"\s+", "")
                    .Replace("-", "").ToUpperInvariant();

            string upper = search.ToUpperInvariant();
            if (upper.Contains("FOUR WAY") || upper.Contains("4 WAY STOP")) return "R1.4";
            if (upper.Contains("STOP")) return "R1";
            if (upper.Contains("GENTLE") && upper.Contains("CURVE"))
                return upper.Contains("LEFT") ? "W203" : upper.Contains("RIGHT") ? "W202" : string.Empty;
            if (upper.Contains("SHARP") && upper.Contains("CURVE"))
                return upper.Contains("LEFT") ? "W205" : upper.Contains("RIGHT") ? "W204" : string.Empty;
            if (upper.Contains("DRIFT")) return string.Empty;
            if (upper.Contains("MINIMUM SPEED")) return "R101";
            if (upper.Contains("SPEED") || System.Text.RegularExpressions.Regex.IsMatch(upper, @"\bCE\s*\d{2,3}\b"))
                return "R201";
            return string.Empty;
        }

        private static string GetSignDescriptorText(Entity entity, Transaction tr,
            string blockName, string symbolSourceName)
        {
            var values = new List<string>();
            foreach (string value in new[] { blockName, symbolSourceName, entity.Layer,
                Convert.ToString(ReadProperty(entity, "EffectiveName"), CultureInfo.InvariantCulture),
                Convert.ToString(ReadProperty(entity, "Name"), CultureInfo.InvariantCulture),
                Convert.ToString(ReadProperty(entity, "Description"), CultureInfo.InvariantCulture),
                Convert.ToString(ReadProperty(entity, "RawDescription"), CultureInfo.InvariantCulture) })
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Replace("_", " ").Replace("-", " "));

            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                try
                {
                    foreach (ObjectId attributeId in block.AttributeCollection)
                    {
                        AttributeReference attribute = tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute != null)
                        {
                            if (!string.IsNullOrWhiteSpace(attribute.Tag)) values.Add(attribute.Tag);
                            if (!string.IsNullOrWhiteSpace(attribute.TextString)) values.Add(attribute.TextString);
                        }
                    }
                }
                catch { }
                try
                {
                    foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                        if (property != null && property.Value != null)
                            values.Add(property.PropertyName + " " + Convert.ToString(property.Value, CultureInfo.InvariantCulture));
                }
                catch { }
                try
                {
                    BlockTableRecord definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                    foreach (ObjectId childId in definition)
                    {
                        Entity child = tr.GetObject(childId, OpenMode.ForRead, false) as Entity;
                        if (child is DBText) values.Add(((DBText)child).TextString);
                        else if (child is MText) values.Add(((MText)child).Contents);
                        else if (child is AttributeDefinition) values.Add(((AttributeDefinition)child).TextString);
                        else if (child is BlockReference) values.Add(GetBlockName(child, tr));
                    }
                }
                catch { }
            }
            return string.Join(" ", values.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string BuildRoadSignDescription(string descriptor, string code)
        {
            string upper = (descriptor ?? string.Empty).ToUpperInvariant();
            string mapped = (code ?? string.Empty).ToUpperInvariant();
            string label = string.Empty;

            if (mapped == "R1.4") label = "4 Way Stop";
            else if (mapped == "R1" || upper.Contains("STOP")) label = "Stop Sign";
            else if (mapped == "W202") label = "Gentle Curve - Right";
            else if (mapped == "W203") label = "Gentle Curve - Left";
            else if (mapped == "W204") label = "Sharp Curve - Right";
            else if (mapped == "W205") label = "Sharp Curve - Left";
            else if (mapped == "W350") label = "Drift Warning Sign";
            else if (mapped == "R101") label = "Minimum Speed Sign";
            else if (mapped == "R201") label = "Speed Sign";
            else if (mapped == "W401") label = "Danger Warning Sign";
            else if (mapped == "W339") label = "General Warning Sign";
            else if (upper.Contains("GENTLE") && upper.Contains("CURVE"))
                label = upper.Contains("LEFT") ? "Gentle Curve - Left" :
                    upper.Contains("RIGHT") ? "Gentle Curve - Right" : "Gentle Curve";
            else if (upper.Contains("SHARP") && upper.Contains("CURVE"))
                label = upper.Contains("LEFT") ? "Sharp Curve - Left" :
                    upper.Contains("RIGHT") ? "Sharp Curve - Right" : "Sharp Curve";
            else if (upper.Contains("MINIMUM") && upper.Contains("SPEED")) label = "Minimum Speed Sign";
            else if (upper.Contains("SPEED") || System.Text.RegularExpressions.Regex.IsMatch(upper, @"\bCE\s*\d{2,3}\b"))
                label = "Speed Limit Sign";
            else if (upper.Contains("DANGER")) label = "Danger Warning Sign";
            else if (upper.Contains("WARNING")) label = "Warning Sign";
            else
            {
                label = CleanLegendDescription(descriptor);
                label = System.Text.RegularExpressions.Regex.Replace(label,
                    @"\b(CE\s+ROAD\s+SIGNS?|ROAD\s+SIGNS?|SIGN)\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                label = System.Text.RegularExpressions.Regex.Replace(label,
                    @"\b[RW]\s*[-_]?\s*\d+(?:\.\d+)?[A-Z]?\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                label = System.Text.RegularExpressions.Regex.Replace(label, @"\b\d+\s*px\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                label = System.Text.RegularExpressions.Regex.Replace(label, @"\s+", " ").Trim(' ', '-', '_');
            }

            if (string.IsNullOrWhiteSpace(label)) label = "Road Sign";
            return string.IsNullOrWhiteSpace(mapped) ? label : label + " - " + mapped;
        }

        private static string BuildCogoDescription(Entity entity)
        {
            string description = ReadPartText(entity, "RawDescription", "FullDescription", "Description", "PointName");
            return string.IsNullOrWhiteSpace(description) ? "COGO Point" : CleanLegendDescription(description);
        }

        private static string BuildObjectDescription(string category, Entity entity,
            string objectType, string partFamily, string partSize)
        {
            string raw = string.Join(" ", new[] { partFamily, partSize, DescribeLayer(entity.Layer) }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            raw = System.Text.RegularExpressions.Regex.Replace(raw,                @"\b(Duroflo|SABS\s*966\s*Part\s*1|C3d|Civil\s*3D|Pipe|Structure(?:\s*/\s*fitting)?)\b",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"[<>]", " ");
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim(' ', '-', '|');
            string familyOnly = (partFamily ?? string.Empty).Trim();
            string sizeOnly = (partSize ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(familyOnly) &&
                !string.IsNullOrWhiteSpace(sizeOnly) &&
                string.Equals(familyOnly, sizeOnly, StringComparison.OrdinalIgnoreCase))
                raw = familyOnly;

            bool isPipe = objectType.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isStructure = objectType.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isPipe)
            {
                string combined = string.Join(" ", new[] { partFamily, partSize, DescribeLayer(entity.Layer) });
                System.Text.RegularExpressions.Match classMatch = System.Text.RegularExpressions.Regex.Match(combined,
                    @"\bClass\s*(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                System.Text.RegularExpressions.Match diameterMatch = System.Text.RegularExpressions.Regex.Match(combined,
                    @"\b(\d{2,4})\s*mm\b|\b(\d{2,4})\s*mm\s*Ø\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string pipeClass = classMatch.Success ? "Class " + classMatch.Groups[1].Value : "";
                string diameter = "";
                if (diameterMatch.Success)
                    diameter = (diameterMatch.Groups[1].Success ? diameterMatch.Groups[1].Value :
                        diameterMatch.Groups[2].Value) + "mm Ø";

                string material = combined.IndexOf("uPVC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    combined.IndexOf("PVC", StringComparison.OrdinalIgnoreCase) >= 0 ? "uPVC" : "";
                var pipeParts = new List<string>();
                if (!string.IsNullOrEmpty(material)) pipeParts.Add(material);
                if (!string.IsNullOrEmpty(pipeClass)) pipeParts.Add(pipeClass);
                if (!string.IsNullOrEmpty(diameter)) pipeParts.Add(diameter);
                if (pipeParts.Count > 0) return pipeParts.Count >= 3 ? pipeParts[0] + " " + pipeParts[1] + " - " + pipeParts[2] : string.Join(" ", pipeParts);
            }

            if (isStructure)
            {
                string combinedStructure = string.Join(" ", new[] { partFamily, partSize, DescribeLayer(entity.Layer) });
                bool sewerCategory = string.Equals(category, "SEWER", StringComparison.OrdinalIgnoreCase);
                bool concreteRing = combinedStructure.IndexOf("CONCRETE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    combinedStructure.IndexOf("CONCENTRIC CYLINDRICAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    combinedStructure.IndexOf("MANHOLE", StringComparison.OrdinalIgnoreCase) >= 0;
                if (sewerCategory && concreteRing)
                {
                    System.Text.RegularExpressions.Match diameter = System.Text.RegularExpressions.Regex.Match(
                        combinedStructure, @"(?<!\d)(\d[\d,]*(?:\.\d+)?)\s*(?:mm|dia)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    string diameterText = string.Empty;
                    if (diameter.Success)
                    {
                        double diameterValue;
                        if (double.TryParse(diameter.Groups[1].Value.Replace(",", ""),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out diameterValue))
                            diameterText = diameterValue.ToString("0.##", CultureInfo.InvariantCulture) + "mm Ø ";
                    }
                    return diameterText + "Concrete Ring Manholes";
                }

                raw = System.Text.RegularExpressions.Regex.Replace(raw,
                    @"\b(Structure|C3d|Civil\s*3D|SI)\b", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim(' ', '-', '|');
                raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\b(\w+)(?:\s+\1\b)+",
                    "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            return string.IsNullOrWhiteSpace(raw) ? DescribeLayer(entity.Layer) : raw;
        }

        private static string DescribeLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return "Selected objects";
            string value = layer.Replace("_", " ").Replace("-", " ").Replace("|", " ");
            value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
        }

        private static List<string> GetPaperLayoutNames(Database database)
        {
            var result = new List<string>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout != null && !layout.ModelType) result.Add(layout.LayoutName);
                }
                tr.Commit();
            }
            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> GetTargetLayouts(Database database, ProductionSettingsDialogModel settings)
        {
            string selected = settings.Text("TargetLayout");
            List<string> available = GetPaperLayoutNames(database);
            if (string.Equals(selected, "All layouts", StringComparison.OrdinalIgnoreCase)) return available;
            return available.Where(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        }

        private static double ReadNumber(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? parsed : fallback;
        }

        private static bool CreateOnLayout(Database database, string layoutName, Point3d insertion,
            IList<LegendRow> rows, string[] categories, string[] headings, string mainHeading,
            double headingHeight, double textHeight, double sampleWidth, double objectScale,
            double structureScale, double roadSignImageScale, bool annotativeText,
            Autodesk.AutoCAD.Colors.Color legendTextColor, string lengthUnit)
        {
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                ObjectId layoutId = ObjectId.Null;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout != null && string.Equals(layout.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
                    { layoutId = entry.Value; break; }
                }
                if (layoutId.IsNull) return false;
                Layout targetLayout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                BlockTableRecord paper = (BlockTableRecord)tr.GetObject(targetLayout.BlockTableRecordId, OpenMode.ForWrite);
                double y = insertion.Y;
                double sampleX = insertion.X;
                double textX = insertion.X + sampleWidth + textHeight * 2.0;

                AddText(paper, tr, mainHeading, new Point3d(insertion.X, y, insertion.Z), headingHeight, annotativeText, legendTextColor);
                y -= headingHeight * 2.0;

                for (int c = 0; c < categories.Length; c++)
                {
                    List<LegendRow> categoryRows = rows.Where(r => r.Category == categories[c])
                        .OrderBy(r => LegendDisplayOrder(r.SymbolKind))
                        .ThenBy(r => ExtractNominalSize(r.Description))
                        .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase).ToList();
                    if (categoryRows.Count == 0) continue;
                    AddText(paper, tr, headings[c], new Point3d(insertion.X, y, insertion.Z), headingHeight, annotativeText, legendTextColor);
                    y -= headingHeight * 1.8;

                    foreach (LegendRow row in categoryRows)
                    {
                        // Legend samples are deliberately rebuilt from native AutoCAD entities.
                        // Do not clone Aecc pipe/structure objects into paper space.
                        double symbolY = y - textHeight * 0.2;
                        double symbolCenterX = sampleX + sampleWidth / 2.0;
                        if (row.SymbolKind == "SIGN")
                        {
                            bool placed = TryPlaceNativeSymbol(row, tr, paper, sampleX, symbolY,
                                sampleWidth, insertion.Z, roadSignImageScale);
                            if (!placed) AddFallbackSymbol(paper, tr, symbolCenterX, symbolY, insertion.Z, textHeight, row.SymbolKind);
                        }
                        else if (row.SymbolKind == "DRIFT")
                        {
                            // Stormwater drift symbols are custom blocks in the drawing.
                            // Clone the original block reference so its hatch, borders and
                            // internal linework remain identical to model space, then orient
                            // the block horizontally for the legend.
                            bool placedDriftBlock = TryPlaceNativeSymbol(row, tr, paper,
                                sampleX, symbolY, sampleWidth, insertion.Z, 1.0, true);
                            if (!placedDriftBlock)
                            {
                                AddDriftSample(paper, tr, sampleX, symbolY, sampleWidth, insertion.Z,
                                    row.DisplayColor, row.Linetype, row.LinetypeScale);
                            }
                        }
                        else if (row.SymbolKind == "ROAD")
                        {
                            AddRoadSample(paper, tr, sampleX, symbolY, sampleWidth, textHeight, insertion.Z,
                                row.DisplayColor, row.Linetype, row.LinetypeScale);
                        }
                        else if (row.SymbolKind == "COGO")
                        {
                            AddCogoSample(paper, tr, symbolCenterX, symbolY, insertion.Z, textHeight,
                                row.DisplayColor, row.Linetype, row.LinetypeScale);
                        }
                        else if (row.ObjectType != null &&
                            row.ObjectType.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Line symbol = new Line(
                                new Point3d(sampleX, symbolY, insertion.Z),
                                new Point3d(sampleX + sampleWidth, symbolY, insertion.Z));
                            symbol.SetDatabaseDefaults();
                            try
                            {
                                if (row.DisplayColor != null) symbol.Color = row.DisplayColor;
                                symbol.LinetypeScale = row.LinetypeScale > 0 ? row.LinetypeScale : 1.0;
                                if (!string.IsNullOrWhiteSpace(row.Linetype) &&
                                    !string.Equals(row.Linetype, "BYLAYER", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(row.Linetype, "BYBLOCK", StringComparison.OrdinalIgnoreCase))
                                    symbol.Linetype = row.Linetype;
                                // Preserve the selected pipe's linetype scale. Do not reset to 1.0.
                            }
                            catch { }
                            paper.AppendEntity(symbol);
                            tr.AddNewlyCreatedDBObject(symbol, true);
                        }
                        else
                        {
                            bool placedStructureBlock = row.SymbolKind == "STRUCTURE" &&
                                TryPlaceExplodedStructure(row, tr, paper, sampleX, symbolY,
                                    sampleWidth, insertion.Z, structureScale);
                            if (!placedStructureBlock)
                            {
                                double radius = Math.Max(textHeight * 0.45, sampleWidth * 0.10);
                                Circle symbol = new Circle(
                                    new Point3d(symbolCenterX, symbolY, insertion.Z),
                                    Vector3d.ZAxis, radius);
                                symbol.SetDatabaseDefaults();
                                if (row.DisplayColor != null) symbol.Color = row.DisplayColor;
                                paper.AppendEntity(symbol);
                                tr.AddNewlyCreatedDBObject(symbol, true);
                            }
                        }

                        string quantity = FormatQuantity(row, lengthUnit);
                        string cleanDescription = CleanLegendDescription(row.Description);
                        string rowLabel = string.IsNullOrWhiteSpace(quantity)
                            ? cleanDescription + " - ITEMS = " + row.Count.ToString(CultureInfo.InvariantCulture)
                            : cleanDescription + " - " + quantity +
                                " - ITEMS = " + row.Count.ToString(CultureInfo.InvariantCulture);
                        AddText(paper, tr, rowLabel,
                            new Point3d(textX, y - textHeight * 0.35, insertion.Z), textHeight, annotativeText, legendTextColor);
                        y -= Math.Max(textHeight * 2.0, sampleWidth * 0.65);
                    }
                    y -= headingHeight * 0.8;
                }
                tr.Commit();
                return true;
            }
        }

        private static bool TryPlaceExplodedStructure(LegendRow row, Transaction tr,
            BlockTableRecord paper, double sampleX, double symbolY, double sampleWidth,
            double z, double scaleMultiplier)
        {
            if (row == null || row.Objects == null || row.Objects.Count == 0) return false;
            DBObjectCollection parts = new DBObjectCollection();
            var graphics = new List<Entity>();
            try
            {
                Entity source = tr.GetObject(row.Objects[0], OpenMode.ForRead, false) as Entity;
                if (source == null) return false;
                source.Explode(parts);
                var pending = new Queue<DBObject>();
                foreach (DBObject item in parts) pending.Enqueue(item);
                int safety = 0;
                while (pending.Count > 0 && safety++ < 2000)
                {
                    DBObject item = pending.Dequeue();
                    BlockReference nestedBlock = item as BlockReference;
                    if (nestedBlock != null)
                    {
                        DBObjectCollection nestedParts = new DBObjectCollection();
                        nestedBlock.Explode(nestedParts);
                        nestedBlock.Dispose();
                        foreach (DBObject nested in nestedParts)
                        {
                            parts.Add(nested);
                            pending.Enqueue(nested);
                        }
                        continue;
                    }
                    Entity primitive = item as Entity;
                    if (primitive != null) graphics.Add(primitive);
                }
                if (graphics.Count == 0) return false;

                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                foreach (Entity graphic in graphics)
                {
                    Extents3d e = graphic.GeometricExtents;
                    minX = Math.Min(minX, e.MinPoint.X); minY = Math.Min(minY, e.MinPoint.Y); minZ = Math.Min(minZ, e.MinPoint.Z);
                    maxX = Math.Max(maxX, e.MaxPoint.X); maxY = Math.Max(maxY, e.MaxPoint.Y); maxZ = Math.Max(maxZ, e.MaxPoint.Z);
                }
                double width = maxX - minX;
                double height = maxY - minY;
                double dimension = Math.Max(width, height);
                if (dimension <= 1e-9) return false;
                Point3d center = new Point3d((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);

                // Standard legend orientation: air valves vertical; isolating valves horizontal.
                // Rotate the exploded AutoCAD primitives as a group about their own centre.
                string structureName = ((row.Description ?? string.Empty) + " " +
                    (row.PartFamily ?? string.Empty) + " " + (row.Layer ?? string.Empty)).ToUpperInvariant();
                bool isAirValve = structureName.Contains("AIR VALVE") || structureName.Contains("AIRVALVE");
                bool isIsolatingValve = structureName.Contains("ISOLAT") || structureName.Contains("GATE VALVE");
                bool rotateQuarterTurn = (isAirValve && width > height) ||
                    (isIsolatingValve && height > width);
                if (rotateQuarterTurn)
                {
                    Matrix3d orientation = Matrix3d.Rotation(Math.PI / 2.0, Vector3d.ZAxis, center);
                    foreach (Entity graphic in graphics) graphic.TransformBy(orientation);
                    minX = double.MaxValue; minY = double.MaxValue; minZ = double.MaxValue;
                    maxX = double.MinValue; maxY = double.MinValue; maxZ = double.MinValue;
                    foreach (Entity graphic in graphics)
                    {
                        Extents3d e = graphic.GeometricExtents;
                        minX = Math.Min(minX, e.MinPoint.X); minY = Math.Min(minY, e.MinPoint.Y); minZ = Math.Min(minZ, e.MinPoint.Z);
                        maxX = Math.Max(maxX, e.MaxPoint.X); maxY = Math.Max(maxY, e.MaxPoint.Y); maxZ = Math.Max(maxZ, e.MaxPoint.Z);
                    }
                    width = maxX - minX; height = maxY - minY;
                    dimension = Math.Max(width, height);
                    center = new Point3d((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
                }
                Matrix3d scale = Matrix3d.Scaling(sampleWidth / dimension * Math.Max(0.01, scaleMultiplier), center);
                foreach (Entity graphic in graphics)
                {
                    if (row.DisplayColor != null) graphic.Color = row.DisplayColor;
                    if (!string.IsNullOrWhiteSpace(row.Linetype) &&
                        !string.Equals(row.Linetype, "Use source", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(row.Linetype, "BYLAYER", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(row.Linetype, "BYBLOCK", StringComparison.OrdinalIgnoreCase))
                        graphic.Linetype = row.Linetype;
                    graphic.LinetypeScale = row.LinetypeScale > 0 ? row.LinetypeScale : 1.0;
                    graphic.TransformBy(scale);
                }
                double nMinX = double.MaxValue, nMinY = double.MaxValue, nMinZ = double.MaxValue;
                double nMaxX = double.MinValue, nMaxY = double.MinValue, nMaxZ = double.MinValue;
                foreach (Entity graphic in graphics)
                {
                    Extents3d e = graphic.GeometricExtents;
                    nMinX = Math.Min(nMinX, e.MinPoint.X); nMinY = Math.Min(nMinY, e.MinPoint.Y); nMinZ = Math.Min(nMinZ, e.MinPoint.Z);
                    nMaxX = Math.Max(nMaxX, e.MaxPoint.X); nMaxY = Math.Max(nMaxY, e.MaxPoint.Y); nMaxZ = Math.Max(nMaxZ, e.MaxPoint.Z);
                }
                Point3d newCenter = new Point3d((nMinX + nMaxX) / 2, (nMinY + nMaxY) / 2, (nMinZ + nMaxZ) / 2);
                Matrix3d move = Matrix3d.Displacement(new Vector3d(sampleX + sampleWidth / 2 - newCenter.X,
                    symbolY - newCenter.Y, z - newCenter.Z));
                foreach (Entity graphic in graphics)
                {
                    graphic.TransformBy(move);
                    paper.AppendEntity(graphic);
                    tr.AddNewlyCreatedDBObject(graphic, true);
                }
                return true;
            }
            catch { return false; }
            finally
            {
                // Explode returns transient entities. Once appended they are database-owned;
                // any unappended items must be disposed even if an earlier operation failed.
                foreach (DBObject item in parts)
                {
                    if (item == null || item.IsDisposed) continue;
                    Entity graphic = item as Entity;
                    bool databaseOwned = false;
                    try { databaseOwned = graphic != null && !graphic.ObjectId.IsNull; } catch { }
                    if (!databaseOwned)
                    {
                        try { item.Dispose(); } catch { }
                    }
                }
            }
        }

        private static bool IsNativeBlockReference(LegendRow row, Transaction tr)
        {
            if (row == null || row.Objects == null || row.Objects.Count == 0) return false;
            try { return tr.GetObject(row.Objects[0], OpenMode.ForRead, false) is BlockReference; }
            catch { return false; }
        }

        private static bool TryPlaceNativeSymbol(LegendRow row, Transaction tr,
            BlockTableRecord paper, double sampleX, double symbolY, double sampleWidth,
            double z, double scaleMultiplier, bool forceHorizontal = false)
        {
            if (row == null || row.Objects == null || row.Objects.Count == 0) return false;
            Entity clone = null;
            try
            {
                Entity source = tr.GetObject(row.Objects[0], OpenMode.ForRead, false) as Entity;
                if (source == null) return false;
                clone = source.Clone() as Entity;
                if (clone == null) return false;
                if (row.DisplayColor != null) clone.Color = row.DisplayColor;
                if (!string.IsNullOrWhiteSpace(row.Linetype) &&
                    !string.Equals(row.Linetype, "Use source", StringComparison.OrdinalIgnoreCase))
                    clone.Linetype = row.Linetype;
                clone.LinetypeScale = row.LinetypeScale > 0 ? row.LinetypeScale : 1.0;
                if (forceHorizontal)
                {
                    BlockReference rotatedBlock = clone as BlockReference;
                    if (rotatedBlock != null && Math.Abs(rotatedBlock.Rotation) > 1e-9)
                    {
                        clone.TransformBy(Matrix3d.Rotation(-rotatedBlock.Rotation,
                            Vector3d.ZAxis, rotatedBlock.Position));
                        rotatedBlock.Rotation = 0.0;
                    }
                }
                Extents3d extents = clone.GeometricExtents;
                double width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
                double height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
                double maxDimension = Math.Max(width, height);
                if (maxDimension <= 1e-9) { clone.Dispose(); return false; }
                Point3d center = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0);
                double factor = sampleWidth / maxDimension * Math.Max(0.01, scaleMultiplier);
                clone.TransformBy(Matrix3d.Scaling(factor, center));
                Extents3d scaled = clone.GeometricExtents;
                Point3d scaledCenter = new Point3d(
                    (scaled.MinPoint.X + scaled.MaxPoint.X) / 2.0,
                    (scaled.MinPoint.Y + scaled.MaxPoint.Y) / 2.0,
                    (scaled.MinPoint.Z + scaled.MaxPoint.Z) / 2.0);
                clone.TransformBy(Matrix3d.Displacement(new Vector3d(
                    sampleX + sampleWidth / 2.0 - scaledCenter.X,
                    symbolY - scaledCenter.Y, z - scaledCenter.Z)));
                paper.AppendEntity(clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                return true;
            }
            catch
            {
                if (clone != null) { try { clone.Dispose(); } catch { } }
                return false;
            }
        }

        private static void AddRoadSample(BlockTableRecord paper, Transaction tr,
            double x, double y, double width, double textHeight, double z,
            Autodesk.AutoCAD.Colors.Color displayColor, string linetype, double linetypeScale)
        {
            double h = Math.Max(textHeight * 0.9, width * 0.28);
            double inset = width * 0.04;
            Polyline road = new Polyline(4);
            road.AddVertexAt(0, new Point2d(x, y - h / 2.0), 0, 0, 0);
            road.AddVertexAt(1, new Point2d(x + width, y - h / 2.0), 0, 0, 0);
            road.AddVertexAt(2, new Point2d(x + width, y + h / 2.0), 0, 0, 0);
            road.AddVertexAt(3, new Point2d(x, y + h / 2.0), 0, 0, 0);
            road.Closed = true;
            road.SetDatabaseDefaults();
            if (displayColor != null) road.Color = displayColor;
            road.LinetypeScale = linetypeScale > 0 ? linetypeScale : 1.0;
            if (!string.IsNullOrWhiteSpace(linetype) && !string.Equals(linetype, "Use source", StringComparison.OrdinalIgnoreCase))
                road.Linetype = linetype;
            paper.AppendEntity(road);
            tr.AddNewlyCreatedDBObject(road, true);
            Line edge1 = new Line(new Point3d(x + inset, y - h * 0.28, z),
                new Point3d(x + width - inset, y - h * 0.28, z));
            Line edge2 = new Line(new Point3d(x + inset, y + h * 0.28, z),
                new Point3d(x + width - inset, y + h * 0.28, z));
            Line center = new Line(new Point3d(x + width * 0.35, y, z),
                new Point3d(x + width * 0.65, y, z));
            foreach (Line line in new[] { edge1, edge2, center })
            {
                line.SetDatabaseDefaults();
                if (displayColor != null) line.Color = displayColor;
                line.LinetypeScale = linetypeScale > 0 ? linetypeScale : 1.0;
                if (!string.IsNullOrWhiteSpace(linetype) && !string.Equals(linetype, "Use source", StringComparison.OrdinalIgnoreCase))
                    line.Linetype = linetype;
                paper.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
            try { center.Linetype = "HIDDEN"; } catch { }
        }

        private static void AddCogoSample(BlockTableRecord paper, Transaction tr,
            double x, double y, double z, double textHeight,
            Autodesk.AutoCAD.Colors.Color displayColor, string linetype, double linetypeScale)
        {
            DBPoint point = new DBPoint(new Point3d(x, y, z));
            point.SetDatabaseDefaults();
            if (displayColor != null) point.Color = displayColor;
            paper.AppendEntity(point);
            tr.AddNewlyCreatedDBObject(point, true);
            Circle ring = new Circle(new Point3d(x, y, z), Vector3d.ZAxis, Math.Max(textHeight * 0.28, 0.5));
            ring.SetDatabaseDefaults();
            if (displayColor != null) ring.Color = displayColor;
            ring.LinetypeScale = linetypeScale > 0 ? linetypeScale : 1.0;
            if (!string.IsNullOrWhiteSpace(linetype) && !string.Equals(linetype, "Use source", StringComparison.OrdinalIgnoreCase))
                ring.Linetype = linetype;
            paper.AppendEntity(ring);
            tr.AddNewlyCreatedDBObject(ring, true);
        }

        private static void AddDriftSample(BlockTableRecord paper, Transaction tr,
            double x, double y, double width, double z,
            Autodesk.AutoCAD.Colors.Color displayColor, string linetype, double linetypeScale)
        {
            // A drift is represented by a horizontal plan-symbol sample in the legend,
            // independent of the rotation of the source object in the drawing.
            double inset = width * 0.08;
            Line top = new Line(new Point3d(x + inset, y + width * 0.12, z),
                new Point3d(x + width - inset, y + width * 0.12, z));
            Line bottom = new Line(new Point3d(x + inset, y - width * 0.12, z),
                new Point3d(x + width - inset, y - width * 0.12, z));
            foreach (Line line in new[] { top, bottom })
            {
                line.SetDatabaseDefaults();
                if (displayColor != null) line.Color = displayColor;
                if (!string.IsNullOrWhiteSpace(linetype) &&
                    !string.Equals(linetype, "Use source", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(linetype, "BYLAYER", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(linetype, "BYBLOCK", StringComparison.OrdinalIgnoreCase))
                    line.Linetype = linetype;
                line.LinetypeScale = linetypeScale > 0 ? linetypeScale : 1.0;
                paper.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
        }

        private static void AddFallbackSymbol(BlockTableRecord paper, Transaction tr,
            double x, double y, double z, double textHeight, string kind)
        {
            if (kind == "DRIFT")
            {
                Line a = new Line(new Point3d(x - textHeight * 0.5, y - textHeight * 0.5, z),
                    new Point3d(x + textHeight * 0.5, y + textHeight * 0.5, z));
                Line b = new Line(new Point3d(x - textHeight * 0.5, y + textHeight * 0.5, z),
                    new Point3d(x + textHeight * 0.5, y - textHeight * 0.5, z));
                foreach (Entity line in new Entity[] { a, b })
                {
                    line.SetDatabaseDefaults();
                    paper.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                }
            }
            else
            {
                Circle circle = new Circle(new Point3d(x, y, z), Vector3d.ZAxis, Math.Max(textHeight * 0.45, 0.5));
                circle.SetDatabaseDefaults();
                paper.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
            }
        }

        private static string FormatQuantity(LegendRow row, string lengthUnit)
        {
            if (row.HasArea && row.Area > 0)
                return "AREA = " + row.Area.ToString("0.00", CultureInfo.InvariantCulture) + " m²";
            if (row.HasLength && row.Length > 0)
                return "TOTAL LENGTH = " + row.Length.ToString("0.00", CultureInfo.InvariantCulture) +
                    (string.IsNullOrWhiteSpace(lengthUnit) ? " drawing units" : lengthUnit.Trim());
            return string.Empty;
        }

        private static string CleanLegendDescription(string value)
        {
            string text = value ?? string.Empty;
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"\b(C3d|Civil\s*3D|Pipe|Structure(?:\s*/\s*fitting)?|Duroflo|SABS\s*966\s*Part\s*1)\b",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b\d+\s*px\b", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '|');
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(\w+)(?:\s+\1\b)+",
                "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text;
        }

        private static void AddText(BlockTableRecord paper, Transaction tr, string value, Point3d position,
            double height, bool annotative, Autodesk.AutoCAD.Colors.Color textColor)
        {
            DBText text = new DBText();
            text.SetDatabaseDefaults();
            if (textColor != null) text.Color = textColor;
            text.TextString = value ?? string.Empty;
            text.Position = position;
            text.Height = height;
            if (annotative)
            {
                try { PaperAnnotationScale.SetAnnotative(text); }
                catch { }
            }
            paper.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }
    }
}