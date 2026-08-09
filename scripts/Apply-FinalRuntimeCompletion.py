from pathlib import Path

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

# 1. Dynamic feature-line report is part of the universal refresh cycle.
name = "UniversalDynamicRefreshCommands.cs"
text = read(name)
text = replace_once(text,
'''                try { CeTablePresentationManager.CenterCeTables(document); }
                catch { result.Warnings++; }''',
'''                try { FinalFeatureLineReportCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { CeTablePresentationManager.CenterCeTables(document); }
                catch { result.Warnings++; }''',
"feature-line report universal refresh")
write(name, text)

# 2. Closed polyline vertices: remove duplicate closing VERTEX at the same XYZ.
# Also bulk-sync COGO styles once per refresh instead of resolving styles per point.
name = "VertexSettingOutCommands.cs"
text = read(name)
text = replace_once(text,
'''            var records = source == null || source.Records == null
                ? new List<VertexSettingRecord>()
                : source.Records.ToList();''',
'''            var records = source == null || source.Records == null
                ? new List<VertexSettingRecord>()
                : RemoveDuplicateClosingVertices(source.Records);''',
"closed polyline point dedupe")
marker = '''        private static string FindNearestRecordKey(
            IEnumerable<VertexSettingSource> sources,'''
helper = '''        private static List<VertexSettingRecord> RemoveDuplicateClosingVertices(
            IEnumerable<VertexSettingRecord> values)
        {
            var result = new List<VertexSettingRecord>();
            foreach (VertexSettingRecord record in values ?? Enumerable.Empty<VertexSettingRecord>())
            {
                if (string.Equals(record.Kind, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                    result.Any(existing =>
                        string.Equals(existing.Kind, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                        existing.Point.DistanceTo(record.Point) <= 1e-7))
                    continue;
                result.Add(record);
            }
            return result;
        }

'''
if marker not in text:
    raise RuntimeError("marker not found: vertex dedupe helper insertion")
text = text.replace(marker, helper + marker, 1)
# Remove expensive per-point style resolution in create/update; one sync after transaction.
text = text.replace('''                CogoPointProjectStyleCommands.ApplyPointStyles(
                    database,
                    civilDocument,
                    transaction,
                    point);
''', '', 1)
text = text.replace('''                CogoPointProjectStyleCommands.ApplyPointStyles(
                    cogo.Database,
                    CivilApplication.ActiveDocument,
                    transaction,
                    cogo);
''', '', 1)
# MLeader text should sit above its attachment/leader rather than below it.
text = replace_once(text,
'''                text.TextHeight = textHeight;
                text.Contents = LabelText(record, link);''',
'''                text.TextHeight = textHeight;
                text.Attachment = AttachmentPoint.BottomLeft;
                text.Contents = LabelText(record, link);''',
"MLeader text above leader")
# After the main transaction closes, sync all COGO styles once.
text = replace_once(text,
'''                pointCount = records.Count;
                dimensionCount = liveDimensionKeys.Count;
            }
        }

        private static List<VertexSettingRecord> FlattenAndName''',
'''                pointCount = records.Count;
                dimensionCount = liveDimensionKeys.Count;
            }
            if (string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))
            {
                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, false); }
                catch { }
            }
        }

        private static List<VertexSettingRecord> FlattenAndName''',
"bulk COGO style sync")
write(name, text)

# 3. Road returns must use the inside quarter of each corner, not the outside arc.
name = "RoadJunctionCompletionCommands.cs"
text = read(name)
old = '''                    double angle = quadrants[index];
                    Point3d arcCentre = centre + new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0) * (width + radius);
                    double startAngle = angle - Math.PI * 0.25;
                    double endAngle = angle + Math.PI * 0.25;
                    var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, startAngle, endAngle);'''
new = '''                    double angle = quadrants[index];
                    double sx = Math.Cos(angle) >= 0.0 ? 1.0 : -1.0;
                    double sy = Math.Sin(angle) >= 0.0 ? 1.0 : -1.0;
                    Point3d arcCentre = centre + new Vector3d(sx * (width + radius), sy * (width + radius), 0.0);
                    double startAngle;
                    double endAngle;
                    if (sx > 0.0 && sy > 0.0) { startAngle = Math.PI; endAngle = Math.PI * 1.5; }
                    else if (sx < 0.0 && sy > 0.0) { startAngle = Math.PI * 1.5; endAngle = Math.PI * 2.0; }
                    else if (sx < 0.0 && sy < 0.0) { startAngle = 0.0; endAngle = Math.PI * 0.5; }
                    else { startAngle = Math.PI * 0.5; endAngle = Math.PI; }
                    var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, startAngle, endAngle);'''
text = replace_once(text, old, new, "inside road bellmouth arcs")
write(name, text)

# 4. Ctrl+Shift+M opens overall most-used directly; Ctrl+F remains Workflow Centre.
name = "FloatingToolsWindow.cs"
text = read(name)
old = '''        private static void OnGlobalPreProcessInput(object sender, PreProcessInputEventArgs args)
        {
            KeyEventArgs key = args == null ? null : args.StagingItem.Input as KeyEventArgs;
            if (key == null || !key.IsDown || key.Key != Key.F ||
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;
            key.Handled = true;
            ShowWindow();
        }'''
new = '''        private static void OnGlobalPreProcessInput(object sender, PreProcessInputEventArgs args)
        {
            KeyEventArgs key = args == null ? null : args.StagingItem.Input as KeyEventArgs;
            if (key == null || !key.IsDown) return;
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (key.Key == Key.M &&
                (modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                key.Handled = true;
                AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute("CE_MOSTUSEDOVERALL ", true, false, true);
                return;
            }
            if (key.Key != Key.F || (modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            key.Handled = true;
            ShowWindow();
        }'''
text = replace_once(text, old, new, "overall most-used shortcut")
write(name, text)

# 5. Ribbon/workflow commands: CE-prefix panel/menu names and route new workflows.
name = "PluginEntry.cs"
text = read(name)
text = replace_once(text,
'''                Title = title.ToUpperInvariant()''',
'''                Title = title.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)
                    ? title.ToUpperInvariant()
                    : "CE-" + title.ToUpperInvariant()''',
"CE panel names")
text = replace_once(text,
'''                Text = text,
                ShowText = true,''',
'''                Text = text.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : "CE-" + text,
                ShowText = true,''',
"CE menu names")
# Feature-line static report -> dynamic linked report, preserving legacy command elsewhere.
text = text.replace('Cmd("Feature Line Report", "CE_FLREPORTUI ",', 'Cmd("Dynamic Feature Line Report", "CE_FLDYNAMICREPORT ",', 1)
# Sewer sequence commands use wrapper with auto-align/layer/freeze options.
text = text.replace('Cmd("Sequence Network", "CE_SEWSEQ ",', 'Cmd("Sequence Network + Production Options", "CE_SEWSEQWORKFLOW ",', 1)
text = text.replace('Cmd("Sequence with Selected Main", "CE_SEWSEQMAIN ",', 'Cmd("Sequence Selected Main + Production Options", "CE_SEWSEQMAINWORKFLOW ",', 1)
# Network creation menu includes auto-connect-all.
needle = '''                        Cmd("Connect Pipe / Structure Parts", "CE_NETWORKCONNECT ", "Launch Civil 3D Connect Network Part To for selected network parts.")),'''
replacement = '''                        Cmd("Connect Pipe / Structure Parts", "CE_NETWORKCONNECT ", "Launch Civil 3D Connect Network Part To for selected network parts."),
                        Cmd("Auto Connect Open Pipe Ends", "CE_NETWORKCONNECTALL ", "Connect open gravity-pipe ends to nearby structures in the same network within a chosen tolerance.")),'''
text = replace_once(text, needle, replacement, "network connect all ribbon")
# Junction tools: explicit single/all setting-out handoff.
needle = '''                        Cmd("Refresh Linked Junctions", "CE_JUNCTIONREFRESH ", "Refresh junction annotations from linked sources."),'''
if needle in text:
    text = text.replace(needle, needle + '\n                        Cmd("Junction Setting-Out", "CE_JUNCTIONSETTINGOUT ", "Set out one picked junction or all selected T/cross-junction returns with the multi-source vertex workflow."),', 1)
write(name, text)

# 6. Sewer popup workflow itself routes sequence actions through options wrappers.
name = "SewerProductionCommands.cs"
text = read(name)
text = text.replace('new DisciplineWorkflowAction("Automatic network sequence", "CE_SEWSEQ",', 'new DisciplineWorkflowAction("Automatic network sequence + options", "CE_SEWSEQWORKFLOW",', 1)
text = text.replace('new DisciplineWorkflowAction("Sequence with selected main", "CE_SEWSEQMAIN",', 'new DisciplineWorkflowAction("Sequence with selected main + options", "CE_SEWSEQMAINWORKFLOW",', 1)
write(name, text)

print("Final runtime completion patch applied.")
