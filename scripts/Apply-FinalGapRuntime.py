from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

def read(name):
    return (SRC / name).read_text(encoding="utf-8")

def write(name, text):
    (SRC / name).write_text(text, encoding="utf-8", newline="\n")

def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError("marker not found: " + label)
    return text.replace(old, new, 1)

# Selective COGO overlap repair and tighter bounded offsets.
name = "CogoPointProjectStyleCommands.cs"
text = read(name)
old = '''        [CommandMethod("CE_TOOLS", "CE_COGOOVERLAPFIX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResolvePointLabelOverlaps()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CogoPointStyleResult result = ApplySelectedStyles(document, true);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\\nCE_COGOOVERLAPFIX complete. COGO labels moved={0}; point coordinates unchanged.",
                result.OverlapsMoved);
        }'''
new = '''        [CommandMethod("CE_TOOLS", "CE_COGOOVERLAPFIX", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ResolvePointLabelOverlaps()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Resolve COGO Label Overlaps",
                "Keep all survey point coordinates fixed. Choose all COGO labels or only selected points; only labels that actually conflict are repositioned and they remain close to their reference point.");
            settings.AddChoice("Scope", "Overlap", "COGO points", "All overlapping COGO labels", "Choose all COGO labels or manually select only the points you want CE Tools to resolve.", new[] { "All overlapping COGO labels", "Select COGO points" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            ISet<ObjectId> restricted = null;
            if (string.Equals(settings.Text("Scope"), "Select COGO points", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\\nSelect COGO points whose labels may move: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                restricted = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }

            CogoPointStyleResult result = ApplySelectedStyles(document, false);
            result.OverlapsMoved = ResolveOverlaps(document, restricted);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\\nCE_COGOOVERLAPFIX complete. COGO labels moved={0}; point coordinates unchanged; scope={1}.",
                result.OverlapsMoved,
                restricted == null ? "all" : "selected");
        }'''
text = replace_once(text, old, new, "selective COGO overlap command")
text = replace_once(text,
'''        internal static int ResolveOverlaps(Document document)
        {''',
'''        internal static int ResolveOverlaps(Document document, ISet<ObjectId> restrictedPointIds = null)
        {''',
"selective overlap signature")
old = '''                    items.Add(new CogoLabelItem(
                        point,
                        anchor,
                        location,
                        width,
                        height));'''
new = '''                    if (restrictedPointIds != null && !restrictedPointIds.Contains(pointId))
                    {
                        occupied.Add(LabelBox(location, width, height, gap));
                        continue;
                    }
                    items.Add(new CogoLabelItem(
                        point,
                        anchor,
                        location,
                        width,
                        height));'''
text = replace_once(text, old, new, "selected COGO overlap items")
text = replace_once(text,
'''                PaperAnnotationScale.ModelDistance(database, 8.0),''',
'''                PaperAnnotationScale.ModelDistance(database, 6.0),''',
"closer COGO maximum offset")
write(name, text)

# Cost estimates: use user-selected XLSX/XLSM template first and preserve the
# template extension so macro-enabled Annexure-A structures remain intact.
name = "WaterSewerCostEstimateCommands.cs"
text = read(name)
old = '''            var save = new SaveFileDialog(
                "Create linked water and sewer cost estimate",
                DefaultOutputPath(document),
                "xlsx",
                "CE_WSCOSTCREATE",
                SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);'''
new = '''            string outputExtension = string.Equals(
                Path.GetExtension(template),
                ".xlsm",
                StringComparison.OrdinalIgnoreCase) ? "xlsm" : "xlsx";
            var save = new SaveFileDialog(
                "Create linked water and sewer cost estimate",
                DefaultOutputPath(document, outputExtension),
                outputExtension,
                "CE_WSCOSTCREATE",
                SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);'''
text = replace_once(text, old, new, "cost output extension")
old = '''        private static string FindInstalledTemplate()
        {
            string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);'''
new = '''        private static string FindInstalledTemplate()
        {
            string selectedTemplate = CostEstimateTemplateStore.Read();
            if (!string.IsNullOrWhiteSpace(selectedTemplate) && File.Exists(selectedTemplate))
                return selectedTemplate;
            string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);'''
text = replace_once(text, old, new, "cost user template priority")
old = '''        private static string DefaultOutputPath(Document document)
        {'''
new = '''        private static string DefaultOutputPath(Document document, string extension)
        {'''
text = replace_once(text, old, new, "cost output path signature")
old = '''            string name = string.IsNullOrWhiteSpace(drawing)
                ? "CE Tools - Water & Sewer Cost Estimate.xlsx"
                : Path.GetFileNameWithoutExtension(drawing) + " - Water & Sewer Cost Estimate.xlsx";'''
new = '''            string suffix = string.Equals(extension, "xlsm", StringComparison.OrdinalIgnoreCase) ? ".xlsm" : ".xlsx";
            string name = string.IsNullOrWhiteSpace(drawing)
                ? "CE Tools - Water & Sewer Cost Estimate" + suffix
                : Path.GetFileNameWithoutExtension(drawing) + " - Water & Sewer Cost Estimate" + suffix;'''
text = replace_once(text, old, new, "cost output suffix")
write(name, text)

# Accept XLSM as a normal OOXML workbook in the shared reader.
name = "FinalAllCommentsCompletionCommands.cs"
text = read(name)
text = replace_once(text,
'''            return extension == ".xlsx" ? ReadXlsx(path) : ReadDelimited(path);''',
'''            return extension == ".xlsx" || extension == ".xlsm" ? ReadXlsx(path) : ReadDelimited(path);''',
"xlsx/xlsm reader")
write(name, text)

# Ribbon access + CE- visible command prefixes.
name = "PluginEntry.cs"
text = read(name)
text = replace_once(text,
'''                        Cmd("Create from Object", "CE_FLCREATE ", "Create feature lines from supported curves."),''',
'''                        Cmd("Create from Object", "CE_FLCREATE ", "Create feature lines from supported curves."),
                        Cmd("Add Feature Lines as Surface Breaklines", "CE_FLBREAKLINE ", "Add selected feature lines/3D curves to a Civil 3D TIN surface as standard breaklines."),''',
"feature breakline ribbon")
text = replace_once(text,
'''                        Cmd("Prepare Crossings and Junctions", "CE_PLBREAKJUNCTIONS ", "Break prepared utility polylines at true crossings and T-junctions.")),''',
'''                        Cmd("Prepare Crossings and Junctions", "CE_PLBREAKJUNCTIONS ", "Break prepared utility polylines at true crossings and T-junctions."),
                        Cmd("Network Creation Hub", "CE_NETWORKCREATEHUB ", "Create Sewer/SW/Water/Bulk Water networks from line/polyline/feature-line objects and connect parts."),
                        Cmd("Create Network from Object", "CE_NETWORKFROMPOLYLINES ", "Choose discipline and launch the Civil 3D native network-from-object workflow."),
                        Cmd("Connect Pipe / Structure Parts", "CE_NETWORKCONNECT ", "Launch Civil 3D Connect Network Part To for selected network parts.")),''',
"network creation ribbon")
text = replace_once(text,
'''                        Cmd("Automatic Cost Refresh", "CE_WSCOSTAUTO ", "Turn deferred refresh after drawing commands on or off.")),''',
'''                        Cmd("Automatic Cost Refresh", "CE_WSCOSTAUTO ", "Turn deferred refresh after drawing commands on or off."),
                        Cmd("Select Approved Cost Template", "CE_COSTTEMPLATESELECT ", "Select an XLSX/XLSM template such as the approved Annexure-A structure for future linked estimates."),
                        Cmd("Cost Template Information", "CE_COSTTEMPLATEINFO ", "Review the active cross-drawing cost-estimate template.")),''',
"cost template ribbon")
text = replace_once(text,
'''                Text = definition.Text,
                ShowText = true,''',
'''                Text = definition.Text.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)
                    ? definition.Text
                    : "CE-" + definition.Text,
                ShowText = true,''',
"CE prefix ribbon command labels")
write(name, text)

print("Final runtime gap patch applied.")
