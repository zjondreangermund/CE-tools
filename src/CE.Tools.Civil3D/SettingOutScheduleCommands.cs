using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SettingOutScheduleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked platform/road/junction setting-out schedules. Point coordinates are
    /// read from COGO or AutoCAD points and optional ground/design elevations are
    /// sampled from selected Civil 3D surfaces. Tables store source handles and can
    /// be refreshed or exported to Excel without COM automation.
    /// </summary>
    public sealed class SettingOutScheduleCommands
    {
        private const string LinkRecordName = "CE_SETTING_OUT_LINKS";
        private const string SchemaVersion = "1";
        private static string _exportGroundSurfaceName = string.Empty;
        private static string _exportFinishedSurfaceName = string.Empty;

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTTOOLS", CommandFlags.Modal)]
        public void SettingOutTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Setting-Out Schedules",
                "Create and maintain linked coordinate/elevation schedules for COGO and AutoCAD points.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create setting-out schedule", "CE_SETTINGOUTPOINTS", "Create a linked schedule from selected COGO or AutoCAD points.", "01 Schedule"),
                    new DisciplineWorkflowAction("Refresh schedule", "CE_SETTINGOUTREFRESH", "Rebuild coordinates and surface elevations from current sources.", "01 Schedule"),
                    new DisciplineWorkflowAction("Export schedule", "CE_SETTINGOUTEXPORT", "Export a linked schedule to Excel.", "02 Export"),
                    new DisciplineWorkflowAction("Schedule information", "CE_SETTINGOUTINFO", "Inspect stored sources, surfaces and refresh state.", "03 Review")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SETTINGOUTPOINTS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D COGO points and/or AutoCAD points for the setting-out schedule: ");
            if (selection.Status != PromptStatus.OK) return;

            var sourceIds = new List<ObjectId>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        rejected++;
                        continue;
                    }
                    DBObject value = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false);
                    if (value is CivilCogoPoint || value is DBPoint)
                        sourceIds.Add(selected.ObjectId);
                    else
                        rejected++;
                }
            }
            if (sourceIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No supported COGO or AutoCAD points were selected.");
                return;
            }

            List<SettingOutSurfaceChoice> surfaces = ReadSurfaceChoices(document);
            var window = new SettingOutConfigurationWindow(surfaces);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                editor.WriteMessage("\nCE_SETTINGOUTPOINTS cancelled.");
                return;
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the linked setting-out table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
                return;

            var link = new SettingOutLink(
                window.ScheduleType,
                window.GroundChoice,
                window.DesignChoice,
                sourceIds.Select(id => id.Handle.ToString()));
            List<SettingOutRow> rows;
            int missing;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                rows = ReadRows(document.Database, transaction, link, out missing);
            }
            if (rows.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No usable setting-out rows were produced.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Schedule type", link.ScheduleType),
                Pair("Accepted points", sourceIds.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Rejected selections", rejected.ToString(CultureInfo.InvariantCulture)),
                Pair("Ground elevation source", link.Ground.DisplayName),
                Pair("Design elevation source", link.Design.DisplayName),
                Pair("Table columns", "Description, X, Y, Ground, Design, Difference"),
                Pair("Linked refresh", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Setting-Out Schedule",
                    "The table stores source point and surface handles so it can be refreshed when points or surfaces change.",
                    review,
                    "Create Schedule"))
            {
                editor.WriteMessage("\nCE_SETTINGOUTPOINTS cancelled.");
                return;
            }

            try
            {
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    insertion.Value,
                    rows,
                    link,
                    annotation.TextHeight);
                editor.SetImpliedSelection(new[] { tableId });
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS complete. Rows={0}; missing sources={1}; linked table created.",
                    rows.Count,
                    missing);

                if (MessageBox.Show("Export this setting-out schedule to Excel now?",
                    "CE Tools - Setting-Out Schedule", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                    ExportTable(document, tableId);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No table was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE setting-out table to refresh: ");
            if (result.Status != PromptStatus.OK) return;

            int rows;
            int missing;
            try
            {
                RefreshTable(document.Database, result.ObjectId, out rows, out missing);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTREFRESH complete. Rows={0}; missing/unavailable sources={1}.",
                    rows,
                    missing);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTREFRESH stopped. {0}",
                    exception.Message);
            }
        }

        // Compatibility alias requested for Data Manager / design-data setting-out export.
        // Reuses the established linked setting-out table export workflow.
        [CommandMethod("CE_TOOLS", "CE_DESIGNDATAEXPORT", CommandFlags.Modal)]
        public void ExportDesignData()
        {
            ExportSettingOutSchedule();
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTEXPORT", CommandFlags.Modal)]
        public void ExportSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\\nSelect a linked CE setting-out table to export (press Esc or cancel for design elements): ");
            if (result.Status == PromptStatus.OK)
            {
                ExportTable(document, result.ObjectId);
                return;
            }

            MessageBoxResult choice = MessageBox.Show(
                "No linked setting-out table was selected. Export all design elements in the drawing instead?",
                "CE Tools - Design Data Export",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) return;

            ExportAllDesignElements(document);
        }

        private static void ExportAllDesignElements(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(editor, "\nSelect multiple design elements to include in the export: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                editor.WriteMessage("\\nNo design elements were selected for export.");
                return;
            }

            var rows = new List<IList<string>>
            {
                new List<string> { "Handle", "Object Type", "Layer", "Point Type", "X", "Y", "Z" }
            };
            var junctionRows = new List<IList<string>>
            {
                new List<string> { "ROAD JUNCTIONS - SETTING-OUT CO-ORDINATES" },
                new List<string> { "POINT", "Y", "X", "EXISTING GROUND LEVEL (m)", "FINAL LEVEL (m)", "HEIGHT DIFFERENCE (m)" }
            };
            int exported = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull) continue;
                    Entity entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;

                    var points = new List<KeyValuePair<string, Point3d>>();
                    Circle circle = entity as Circle;
                    DBPoint dbPoint = entity as DBPoint;
                    Curve curve = entity as Curve;
                    if (circle != null)
                    {
                        points.Add(new KeyValuePair<string, Point3d>("CIRCLE CENTRE", circle.Center));
                    }
                    else if (entity is CivilCogoPoint)
                    {
                        CivilCogoPoint cogo = (CivilCogoPoint)entity;
                        Point3d location = cogo.Location;
                        points.Add(new KeyValuePair<string, Point3d>("COGO POINT", location));
                        string pointName = !string.IsNullOrWhiteSpace(cogo.RawDescription)
                            ? cogo.RawDescription.Trim()
                            : (!string.IsNullOrWhiteSpace(cogo.PointName)
                                ? cogo.PointName.Trim()
                                : cogo.PointNumber.ToString(CultureInfo.InvariantCulture));
                        junctionRows.Add(new List<string>
                        {
                            pointName,
                            location.Y.ToString("0.000", CultureInfo.InvariantCulture),
                            location.X.ToString("0.000", CultureInfo.InvariantCulture),
                            string.Empty,
                            location.Z.ToString("0.000", CultureInfo.InvariantCulture),
                            string.Empty
                        });
                    }
                    else if (dbPoint != null)
                    {
                        points.Add(new KeyValuePair<string, Point3d>("POINT", dbPoint.Position));
                    }
                    else if (curve != null)
                    {
                        try { points.Add(new KeyValuePair<string, Point3d>("START", curve.StartPoint)); } catch { }
                        try { points.Add(new KeyValuePair<string, Point3d>("END", curve.EndPoint)); } catch { }
                    }
                    else
                    {
                        try
                        {
                            Extents3d ext = entity.GeometricExtents;
                            points.Add(new KeyValuePair<string, Point3d>("EXTENTS CENTRE",
                                new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                                            (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5,
                                            (ext.MinPoint.Z + ext.MaxPoint.Z) * 0.5)));
                        }
                        catch { }
                    }

                    foreach (KeyValuePair<string, Point3d> item in points)
                    {
                        rows.Add(new List<string>
                        {
                            entity.Handle.ToString(),
                            entity.GetType().Name,
                            entity.Layer ?? string.Empty,
                            item.Key,
                            item.Value.X.ToString("0.########", CultureInfo.InvariantCulture),
                            item.Value.Y.ToString("0.########", CultureInfo.InvariantCulture),
                            item.Value.Z.ToString("0.########", CultureInfo.InvariantCulture)
                        });
                        exported++;
                    }
                }
                transaction.Commit();
            }

            if (exported == 0)
            {
                editor.WriteMessage("\\nNo exportable design geometry was found.");
                return;
            }

            var exportOptions = new DesignDataExportOptionsWindow(document);
            AcApplication.ShowModalWindow(exportOptions);
            if (!exportOptions.Accepted) return;
            _exportGroundSurfaceName = exportOptions.GroundSurfaceName;
            _exportFinishedSurfaceName = exportOptions.FinishedSurfaceName;
            bool swapCoordinates = exportOptions.SwapCoordinates;
            bool reverseCoordinateSigns = exportOptions.ReverseCoordinateSigns;
            double chainageInterval = exportOptions.ChainageInterval;
            double leftOffset = exportOptions.LeftOffset;
            double centreOffset = exportOptions.CentreOffset;
            double rightOffset = exportOptions.RightOffset;

            string path;
            var save = new SaveFileDialog();
            {
                string initialPath = GetDefaultExportPath("CE-Design-Elements-Setting-Out.xlsx");
                save.Filter = "Excel Workbook (*.xlsx)|*.xlsx";
                save.Title = "Export All CE Tools Design Elements";
                save.DefaultExt = ".xlsx";
                save.AddExtension = true;
                save.FileName = Path.GetFileName(initialPath);
                if (Directory.Exists(Path.GetDirectoryName(initialPath)))
                    save.InitialDirectory = Path.GetDirectoryName(initialPath);
                if (save.ShowDialog() != true) return;
                path = save.FileName;
            }
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
            try
            {
                var sheets = BuildDesignDataReportLayouts();
                SetConstructionLevelHeaders(sheets, leftOffset, centreOffset, rightOffset);
                PopulateWaterStructureAndPipeReports(document, sheets);
                PopulateSewerStructureAndPipeReports(document, sheets);
                PopulateHorizontalAlignmentReport(document, sheets);
                PopulateVerticalAlignmentReport(document, sheets);
                PopulateConstructionLevelsReport(document, sheets, chainageInterval, leftOffset, centreOffset, rightOffset);
                PopulateJunctionSurfaceLevels(document, junctionRows);

                // Keep water/sewer branch, structure and pipe rows in natural numeric
                // order (e.g. W2.9 before W2.10, MH2.9 before MH2.10, P2.9 before P2.10).
                // Preserve each worksheet's title and header rows.
                for (int reportIndex = 1; reportIndex <= 4 && reportIndex < sheets.Count; reportIndex++)
                    sheets[reportIndex] = SortReportDataRowsNaturally(sheets[reportIndex]);

                // Populate junction setting-out rows from selected COGO points and sample
                // uniquely identified EG/FG surfaces when available.
                if (junctionRows.Count > 2)
                {
                    // Sort setting-out names naturally so J1.1, J1.2 ... J2.1,
                    // J2.2 appear in numeric order rather than alphabetically.
                    List<IList<string>> sortedJunctionRows = junctionRows
                        .Take(2)
                        .Concat(junctionRows.Skip(2).OrderBy(row => row.Count > 0 ? row[0] : string.Empty,
                            Comparer<string>.Create(CompareNaturalPointNames)))
                        .ToList();
                    sheets[0] = new XlsxSheet("Road Junction Setting-Out", sortedJunctionRows);
                }
                sheets.Add(new XlsxSheet("Selected Elements", rows));
                ApplyCoordinateOptions(sheets, swapCoordinates, reverseCoordinateSigns);
                SimpleXlsxWriter.Write(path, sheets);
                SaveLastExportPath(path);
                TryOpenExportedWorkbook(path);
                editor.WriteMessage("\\nCE_DESIGNDATAEXPORT complete. Design points exported={0}; file={1}", exported, path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\\nCE_DESIGNDATAEXPORT failed: {0}", exception.Message);
            }
        }


        private static void ApplyCoordinateOptions(IList<XlsxSheet> sheets, bool swap, bool reverseSigns)
        {
            if (sheets == null || (!swap && !reverseSigns)) return;
            for (int si = 0; si < sheets.Count; si++)
            {
                XlsxSheet sheet = sheets[si];
                if (sheet == null || sheet.Rows == null) continue;
                string name = sheet.Name ?? string.Empty;
                int firstDataRow = name == "Selected Elements" ? 1 : 2;
                for (int ri = firstDataRow; ri < sheet.Rows.Count; ri++)
                {
                    IList<string> row = sheet.Rows[ri];
                    if (row == null) continue;
                    int first = -1, second = -1;
                    if (name == "Road Junction Setting-Out") { first = 1; second = 2; }
                    else if (name == "Water Structure Data") { first = 2; second = 3; }
                    else if (name == "Sewer Structure Data") { first = 1; second = 2; }
                    else if (name == "Construction Levels") { first = 2; second = 3; }
                    else if (name == "Selected Elements") { first = 4; second = 5; }
                    if (first >= 0 && row.Count > Math.Max(first, second))
                    {
                        double a, b;
                        if (double.TryParse(row[first], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                            double.TryParse(row[second], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                        {
                            if (swap) { double t = a; a = b; b = t; }
                            if (reverseSigns) { a = -a; b = -b; }
                            row[first] = a.ToString("0.000", CultureInfo.InvariantCulture);
                            row[second] = b.ToString("0.000", CultureInfo.InvariantCulture);
                        }
                    }
                    if (name == "Horizontal Alignment Data")
                    {
                        for (int ci = 11; ci <= 14 && ci < row.Count; ci++)
                            row[ci] = TransformPointText(row[ci], swap, reverseSigns);
                    }
                }
            }
        }

        private static string TransformPointText(string value, bool swap, bool reverseSigns)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var matches = Regex.Matches(value, @"-?\d+(?:\.\d+)?");
            if (matches.Count < 2) return value;
            double a, b;
            if (!double.TryParse(matches[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out a) ||
                !double.TryParse(matches[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return value;
            if (swap) { double t = a; a = b; b = t; }
            if (reverseSigns) { a = -a; b = -b; }
            return string.Format(CultureInfo.InvariantCulture, "({0:0.000}m,{1:0.000}m)", a, b);
        }

        // Prefer the pipe's explicit Civil 3D reference alignment. When absent,
        // resolve numbered P<n>.<m> pipes only to an unambiguous matching branch.
        // Never substitute a common network name for the branch identity.
        private static string ResolvePipeBranchName(CivilPipe pipe, CivilNetwork network,
            Transaction transaction, IDictionary<int, string> alignmentNames)
        {
            try
            {
                ObjectId reference = pipe.RefAlignmentId;
                if (!reference.IsNull && !reference.IsErased)
                {
                    CivilAlignment alignment = transaction.GetObject(reference, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment != null && !string.IsNullOrWhiteSpace(alignment.Name)) return alignment.Name;
                }
            }
            catch { }
            string description = ReadDesignText(pipe, "Description");
            Match match = Regex.Match(pipe.Name ?? string.Empty, @"^P(?<number>\\d+)\\.", RegexOptions.IgnoreCase);
            if (!match.Success) match = Regex.Match(description ?? string.Empty, @"Branch\\s*-\\s*(?<number>\\d+)", RegexOptions.IgnoreCase);
            int number;
            string name;
            if (match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out number) && alignmentNames.TryGetValue(number, out name))
                return name;
            return string.Empty;
        }

        private static Dictionary<int, string> ReadBranchAlignmentNames(Transaction transaction)
        {
            var result = new Dictionary<int, string>();
            var ambiguous = new HashSet<int>();
            foreach (ObjectId id in CivilApplication.ActiveDocument.GetAlignmentIds())
            {
                CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment == null) continue;
                Match match = Regex.Match(alignment.Name ?? string.Empty,
                    @"(?:^| - )Branch\\s*-\\s*(?<number>\\d+)$", RegexOptions.IgnoreCase);
                int number;
                if (!match.Success || !int.TryParse(match.Groups["number"].Value, out number)) continue;
                if (result.ContainsKey(number)) ambiguous.Add(number);
                else result[number] = alignment.Name;
            }
            foreach (int number in ambiguous) result.Remove(number);
            return result;
        }

        private static void PopulateWaterStructureAndPipeReports(Document document, IList<XlsxSheet> sheets)
        {
            if (document == null || sheets == null || sheets.Count < 5) return;

            var structureRows = new List<IList<string>>();
            var pipeRows = new List<IList<string>>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface groundSurface = FindExportSurface(document, transaction, _exportGroundSurfaceName, false);
                Dictionary<int, string> branchAlignments = ReadBranchAlignmentNames(transaction);
                foreach (ObjectId networkId in civilDocument.GetPipeNetworkIds())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;

                    string networkName = network.Name ?? string.Empty;
                    if (!IsWaterNetworkName(networkName)) continue;

                    var invertsByStructure = new Dictionary<ObjectId, List<double>>();
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;

                        double diameter = ReadDesignDouble(pipe, "InnerDiameterOrWidth", "InnerDiameter", "Diameter", "NominalDiameter");
                        double radius = diameter > 0.0 ? diameter * 0.5 : 0.0;
                        Point3d start = pipe.StartPoint;
                        Point3d end = pipe.EndPoint;
                        double startInvert = GetPipeInvertElevation(pipe, true, start, radius);
                        double endInvert = GetPipeInvertElevation(pipe, false, end, radius);
                        AddDesignInvert(invertsByStructure, pipe.StartStructureId, startInvert);
                        AddDesignInvert(invertsByStructure, pipe.EndStructureId, endInvert);

                        double length = Math.Sqrt(
                            Math.Pow(end.X - start.X, 2.0) +
                            Math.Pow(end.Y - start.Y, 2.0));
                        if (length <= 1e-9)
                            length = ReadDesignDouble(pipe, "Length2DCenterToCenter", "Length2D", "Length3DCenterToCenter", "Length3D");

                        double slopePercent = length > 1e-9 && !double.IsNaN(startInvert) && !double.IsNaN(endInvert)
                            ? ((startInvert - endInvert) / length) * 100.0 : double.NaN;
                        string pipeName = ReadDesignText(pipe, "Name", "PartName");
                        if (string.IsNullOrWhiteSpace(pipeName)) pipeName = pipe.Handle.ToString();
                        string description = ReadDesignText(pipe, "Description", "PartDescription", "PartFamilyName");
                        string size = ReadDesignText(pipe, "PartSizeName", "NominalDiameter", "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
                        if (string.IsNullOrWhiteSpace(size) && diameter > 0.0)
                            size = diameter.ToString("0.###", CultureInfo.InvariantCulture) + " drawing units";

                        pipeRows.Add(new List<string>
                        {
                            pipeName,
                            description,
                            size,
                            length > 0.0 && !double.IsNaN(length) && !double.IsInfinity(length)
                                ? length.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            !double.IsNaN(slopePercent) && !double.IsInfinity(slopePercent)
                                ? slopePercent.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            FormatMaybe(startInvert, "0.000"),
                            FormatMaybe(endInvert, "0.000"),
                            ResolvePipeBranchName(pipe, network, transaction, branchAlignments)
                        });
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;

                        Point3d position;
                        try { position = structure.Position; }
                        catch { continue; }

                        double invert = double.NaN;
                        List<double> invertValues;
                        if (invertsByStructure.TryGetValue(structureId, out invertValues) && invertValues.Count > 0)
                            invert = invertValues.Min();

                        string nodeName = ReadDesignText(structure, "Name", "Description");
                        if (string.IsNullOrWhiteSpace(nodeName)) nodeName = structure.Handle.ToString();
                        string nodeType = ReadDesignText(structure, "PartFamilyName", "Description", "PartSizeName");
                        if (string.IsNullOrWhiteSpace(nodeType)) nodeType = "Structure";

                        double surfaceElevation = TryFindSurfaceElevation(groundSurface, position.X, position.Y);
                        double rim = ReadDesignDouble(structure, "RimElevation", "RimElevationOverride", "TopElevation");
                        if (double.IsNaN(rim)) rim = position.Z;
                        double depthBelowSurface = !double.IsNaN(surfaceElevation) && !double.IsNaN(invert)
                            ? surfaceElevation - invert : double.NaN;
                        structureRows.Add(new List<string>
                        {
                            nodeName,
                            nodeType,
                            position.Y.ToString("0.000", CultureInfo.InvariantCulture),
                            position.X.ToString("0.000", CultureInfo.InvariantCulture),
                            FormatMaybe(surfaceElevation, "0.000"),
                            FormatMaybe(rim, "0.000"),
                            FormatMaybe(invert, "0.000"),
                            FormatMaybe(depthBelowSurface, "0.000")
                        });
                    }
                }
                transaction.Commit();
            }

            sheets[1] = ReplaceReportRows(sheets[1], structureRows);
            sheets[2] = ReplaceReportRows(sheets[2], pipeRows);
        }

        private static CivilSurface FindExportSurface(Document document, Transaction transaction, string selectedName, bool finished)
        {
            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                foreach (ObjectId id in CivilApplication.ActiveDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null && string.Equals(surface.Name, selectedName, StringComparison.OrdinalIgnoreCase)) return surface;
                }
                // Never silently substitute another surface for a missing explicitly selected one.
                return null;
            }
            return finished ? FindUniqueFinishedGroundSurface(CivilApplication.ActiveDocument, transaction)
                            : FindUniqueExistingGroundSurface(document, transaction);
        }

        private static CivilSurface FindUniqueExistingGroundSurface(Document document, Transaction transaction)
        {
            if (document == null || transaction == null) return null;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            var matches = new List<CivilSurface>();
            foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) continue;
                string name = (surface.Name ?? string.Empty).ToUpperInvariant();
                bool looksFinished = name.Contains("FINISHED") || name.Contains("PROPOSED") || name.Contains("DESIGN") ||
                                     name == "FG" || name.StartsWith("FG-") || name.StartsWith("FG_");
                if (!looksFinished && (name.Contains("EXISTING") || name == "EG" || name.StartsWith("EG-") ||
                    name.StartsWith("EG_") || name.Contains("GROUND") || name.Contains("NATURAL") || name.Contains("TERRAIN")))
                    matches.Add(surface);
            }
            // Prefer an explicitly named EG/terrain surface. If the drawing uses a
            // non-standard surface name, fall back deterministically to a non-FG surface
            // rather than returning blank elevations for every structure and junction.
            if (matches.Count == 0)
            {
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null) continue;
                    string name = (surface.Name ?? string.Empty).ToUpperInvariant();
                    if (name.Contains("FINISHED") || name.Contains("PROPOSED") || name.Contains("DESIGN") ||
                        name == "FG" || name.StartsWith("FG-") || name.StartsWith("FG_")) continue;
                    matches.Add(surface);
                }
            }
            return matches.OrderByDescending(surface => SurfaceNameScore(surface.Name, false)).FirstOrDefault();
        }

        private static double TryFindSurfaceElevation(CivilSurface surface, double x, double y)
        {
            if (surface == null) return double.NaN;
            try
            {
                double elevation = surface.FindElevationAtXY(x, y);
                return double.IsNaN(elevation) || double.IsInfinity(elevation) ? double.NaN : elevation;
            }
            catch { return double.NaN; }
        }

        private static bool IsWaterNetworkName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string value = name.ToUpperInvariant();
            // Exclude sewer-like networks even when a project name contains both disciplines.
            if (value.Contains("SEWER") || value.Contains("FOUL") || value.Contains("SANITARY") || value.Contains("WASTEWATER"))
                return false;
            return value.Contains("WATER") || value.Contains("POTABLE") || value.Contains("WATERLINE") ||
                   value.Contains("WATER LINE") || value.Contains("RETICULATION") || value.Contains("HYDRANT");
        }

        private static void PopulateSewerStructureAndPipeReports(Document document, IList<XlsxSheet> sheets)
        {
            if (document == null || sheets == null || sheets.Count < 5) return;

            var structureRows = new List<IList<string>>();
            var pipeRows = new List<IList<string>>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface groundSurface = FindExportSurface(document, transaction, _exportGroundSurfaceName, false);
                Dictionary<int, string> branchAlignments = ReadBranchAlignmentNames(transaction);
                foreach (ObjectId networkId in civilDocument.GetPipeNetworkIds())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;

                    string networkName = network.Name ?? string.Empty;
                    if (!IsSewerNetworkName(networkName)) continue;

                    var invertsByStructure = new Dictionary<ObjectId, List<double>>();
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;

                        double diameter = ReadDesignDouble(pipe, "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
                        double radius = diameter > 0.0 ? diameter * 0.5 : 0.0;
                        Point3d start = pipe.StartPoint;
                        Point3d end = pipe.EndPoint;
                        double startInvert = GetPipeInvertElevation(pipe, true, start, radius);
                        double endInvert = GetPipeInvertElevation(pipe, false, end, radius);
                        AddDesignInvert(invertsByStructure, pipe.StartStructureId, startInvert);
                        AddDesignInvert(invertsByStructure, pipe.EndStructureId, endInvert);

                        double length = Math.Sqrt(
                            Math.Pow(end.X - start.X, 2.0) +
                            Math.Pow(end.Y - start.Y, 2.0));
                        if (length <= 1e-9)
                            length = ReadDesignDouble(pipe, "Length2DCenterToCenter", "Length2D", "Length3DCenterToCenter", "Length3D");

                        // Export gravity pipes in their downhill reporting direction; do not alter Civil 3D geometry.
                        if (!double.IsNaN(startInvert) && !double.IsNaN(endInvert) && endInvert > startInvert)
                        {
                            double temporary = startInvert; startInvert = endInvert; endInvert = temporary;
                        }
                        double slopePercent = length > 1e-9 && !double.IsNaN(startInvert) && !double.IsNaN(endInvert)
                            ? ((startInvert - endInvert) / length) * 100.0 : double.NaN;
                        string pipeName = ReadDesignText(pipe, "Name", "PartName");
                        if (string.IsNullOrWhiteSpace(pipeName)) pipeName = pipe.Handle.ToString();
                        string description = ReadDesignText(pipe, "Description", "PartDescription", "PartFamilyName");
                        string size = ReadDesignText(pipe, "PartSizeName", "NominalDiameter", "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
                        if (string.IsNullOrWhiteSpace(size) && diameter > 0.0)
                            size = diameter.ToString("0.###", CultureInfo.InvariantCulture) + " drawing units";

                        pipeRows.Add(new List<string>
                        {
                            pipeName,
                            description,
                            size,
                            length > 0.0 ? length.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            !double.IsNaN(slopePercent) && !double.IsInfinity(slopePercent)
                                ? slopePercent.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            FormatMaybe(startInvert, "0.000"),
                            FormatMaybe(endInvert, "0.000"),
                            ResolvePipeBranchName(pipe, network, transaction, branchAlignments)
                        });
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;

                        Point3d position;
                        try { position = structure.Position; }
                        catch { continue; }

                        double rim = ReadDesignDouble(structure, "RimElevation");
                        if (double.IsNaN(rim) || double.IsInfinity(rim)) rim = position.Z;

                        double invert = double.NaN;
                        List<double> invertValues;
                        if (invertsByStructure.TryGetValue(structureId, out invertValues) && invertValues.Count > 0)
                            invert = invertValues.Min();

                        double depth = !double.IsNaN(invert) ? rim - invert : double.NaN;
                        string nodeName = ReadDesignText(structure, "Name", "Description");
                        if (string.IsNullOrWhiteSpace(nodeName)) nodeName = structure.Handle.ToString();
                        string nodeType = ReadDesignText(structure, "PartFamilyName", "Description", "PartSizeName");
                        if (string.IsNullOrWhiteSpace(nodeType)) nodeType = "Structure";

                        double surfaceElevation = TryFindSurfaceElevation(groundSurface, position.X, position.Y);
                        structureRows.Add(new List<string>
                        {
                            nodeName,
                            position.X.ToString("0.000", CultureInfo.InvariantCulture),
                            position.Y.ToString("0.000", CultureInfo.InvariantCulture),
                            !double.IsNaN(surfaceElevation) ? surfaceElevation.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            rim.ToString("0.000", CultureInfo.InvariantCulture),
                            !double.IsNaN(invert) && !double.IsInfinity(invert)
                                ? invert.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            !double.IsNaN(depth) && !double.IsInfinity(depth)
                                ? depth.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                            nodeType
                        });
                    }
                }
                transaction.Commit();
            }

            sheets[3] = ReplaceReportRows(sheets[3], structureRows);
            sheets[4] = ReplaceReportRows(sheets[4], pipeRows);
        }

        private static void PopulateHorizontalAlignmentReport(Document document, IList<XlsxSheet> sheets)
        {
            if (document == null || sheets == null || sheets.Count < 8) return;
            var rows = new List<IList<string>>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    object entities = ReadDesignObject(alignment, "Entities");
                    if (!(entities is IEnumerable)) continue;
                    double alignmentStart = ReadDesignDouble(alignment, "StartingStation", "StartStation");
                    double alignmentEnd = ReadDesignDouble(alignment, "EndingStation", "EndStation");
                    double roadLength = ReadDesignDouble(alignment, "Length", "Length2D");
                    if (double.IsNaN(roadLength) && !double.IsNaN(alignmentStart) && !double.IsNaN(alignmentEnd))
                        roadLength = alignmentEnd - alignmentStart;
                    double alignmentDesignSpeed = ReadDesignDouble(alignment, "DesignSpeed", "DesignSpeedValue", "Speed");
                    var entityList = ((IEnumerable)entities).Cast<object>().Where(e => e != null).ToList();
                    int number = 0;
                    double derivedStation = alignmentStart;
                    foreach (object entity in entityList)
                    {
                        if (entity == null) continue;
                        number++;
                        string type = ReadDesignText(entity, "EntityType", "Type");
                        if (string.IsNullOrWhiteSpace(type)) type = entity.GetType().Name;
                        double length = ReadDesignDouble(entity, "Length");
                        double radius = ReadDesignDouble(entity, "Radius");
                        double startStation = ReadDesignDouble(entity, "StartingStation", "StartStation");
                        double endStation = ReadDesignDouble(entity, "EndingStation", "EndStation");
                        // Civil 3D alignment entities expose their own station limits. Keep
                        // these authoritative values whenever valid: projecting a curve's
                        // reported StartPoint/EndPoint can return the wrong point for some
                        // entity wrappers and previously stretched ROAD-03/04/05 entities
                        // across the entire parent alignment. Geometry projection is only a
                        // fallback when the entity does not provide a usable station range.
                        object entityStartPoint = ReadDesignObject(entity, "StartPoint", "StartPointLocation", "StartPoint3d");
                        object entityEndPoint = ReadDesignObject(entity, "EndPoint", "EndPointLocation", "EndPoint3d");
                        double ex, ey, resolvedStart, resolvedEnd;
                        bool validEntityRange = !double.IsNaN(startStation) && !double.IsNaN(endStation) &&
                                                !double.IsInfinity(startStation) && !double.IsInfinity(endStation) &&
                                                endStation >= startStation &&
                                                (double.IsNaN(alignmentStart) || startStation >= alignmentStart - 0.01) &&
                                                (double.IsNaN(alignmentEnd) || endStation <= alignmentEnd + 0.01) &&
                                                (double.IsNaN(length) || Math.Abs((endStation - startStation) - length) <= Math.Max(1.0, length * 0.05));
                        if (!validEntityRange)
                        {
                            if (TryPointXY(entityStartPoint, out ex, out ey) && TryAlignmentStation(alignment, ex, ey, out resolvedStart))
                                startStation = resolvedStart;
                            if (TryPointXY(entityEndPoint, out ex, out ey) && TryAlignmentStation(alignment, ex, ey, out resolvedEnd))
                                endStation = resolvedEnd;
                        }
                        // Fall back to a continuous station chain only when the entity API
                        // does not expose usable geometry/stations. Never reset an entity to 0
                        // merely because the parent alignment begins at a non-zero station.
                        if (double.IsNaN(startStation) && !double.IsNaN(derivedStation)) startStation = derivedStation;
                        if (double.IsNaN(endStation) && !double.IsNaN(startStation) && !double.IsNaN(length))
                            endStation = startStation + length;
                        if (!double.IsNaN(alignmentStart) && !double.IsNaN(startStation) && Math.Abs(startStation) < 1e-8 && alignmentStart > 1e-6 && number == 1)
                            startStation = alignmentStart;
                        if (!double.IsNaN(endStation)) derivedStation = endStation;
                        double piStation = ReadDesignDouble(entity, "PIStation", "PIPointStation");

                        double designSpeed = ReadDesignDouble(entity, "DesignSpeed", "DesignSpeedValue", "Speed");
                        if (double.IsNaN(designSpeed)) designSpeed = alignmentDesignSpeed;
                        string direction = ReadDesignText(entity, "Direction");
                        object piObject = ReadDesignObject(entity, "PI", "PIPoint", "PIPosition");
                        object startObject = ReadDesignObject(entity, "StartPoint", "StartPointLocation");
                        object endObject = ReadDesignObject(entity, "EndPoint", "EndPointLocation");
                        object centerObject = ReadDesignObject(entity, "CenterPoint", "Center");
                        if (double.IsNaN(piStation) && piObject != null)
                        {
                            double piX, piY, resolvedStation;
                            if (TryPointXY(piObject, out piX, out piY) &&
                                TryAlignmentStation(alignment, piX, piY, out resolvedStation))
                                piStation = resolvedStation;
                        }
                        if (piObject == null && !double.IsNaN(piStation))
                        {
                            double px, py;
                            if (TryAlignmentPoint(alignment, piStation, 0.0, out px, out py))
                                piObject = new Point3d(px, py, 0.0);
                        }
                        if (startObject == null && !double.IsNaN(startStation))
                        {
                            double px, py;
                            if (TryAlignmentPoint(alignment, startStation, 0.0, out px, out py))
                                startObject = new Point3d(px, py, 0.0);
                        }
                        if (endObject == null && !double.IsNaN(endStation))
                        {
                            double px, py;
                            if (TryAlignmentPoint(alignment, endStation, 0.0, out px, out py))
                                endObject = new Point3d(px, py, 0.0);
                        }
                        string pi = FormatDesignPoint(piObject);
                        string start = FormatDesignPoint(startObject);
                        string end = FormatDesignPoint(endObject);
                        string center = FormatDesignPoint(centerObject);
                        rows.Add(new List<string> {
                            alignment.Name ?? string.Empty,
                            FormatMaybe(roadLength, "0.000"),
                            number.ToString(CultureInfo.InvariantCulture), type,
                            FormatMaybe(length, "0.000"), FormatMaybe(radius, "0.000"),
                            FormatMaybe(designSpeed, "0.###"), direction,
                            FormatStation(startStation), FormatStation(endStation), FormatStation(piStation),
                            pi, start, end, center
                        });
                    }
                }
                tr.Commit();
            }
            rows = rows.OrderBy(r => r.Count > 0 ? r[0] : string.Empty,
                Comparer<string>.Create(CompareNaturalPointNames))
                .ToList();
            sheets[5] = ReplaceReportRows(sheets[5], rows);
        }

        private static void PopulateVerticalAlignmentReport(Document document, IList<XlsxSheet> sheets)
        {
            if (document == null || sheets == null || sheets.Count < 8) return;
            var rows = new List<IList<string>>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    // Export one best-fit design profile per alignment. Exporting every
                    // profile was duplicating each PVI (EG plus FG/design profiles) and
                    // produced thousands of irrelevant 5 m sample rows.
                    var candidates = new List<CivilProfile>();
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile = tr.GetObject(profileId, OpenMode.ForRead, false) as CivilProfile;
                        if (profile == null) continue;
                        string name = (profile.Name ?? string.Empty).Trim().ToUpperInvariant();
                        if (name.Contains("EXIST") || name == "EG" || name.StartsWith("EG-") || name.StartsWith("EG_") ||
                            name.Contains("NATURAL GROUND") || name.Contains("SURFACE PROFILE")) continue;
                        candidates.Add(profile);
                    }
                    // Prefer an explicitly named FG/design profile, but use PVI count to
                    // break ties so a short/placeholder profile cannot win over the real road profile.
                    CivilProfile selectedProfile = candidates
                        // Prefer an actual design profile over sampled surface/profile
                        // representations. A regular sequence at a fixed interval (often
                        // 5 m) is characteristic of sampled data, not manually defined PVIs.
                        .OrderBy(p => IsLikelySampledProfile(p) ? 1 : 0)
                        .ThenByDescending(p => ProfileDesignNameScore(p.Name))
                        .ThenBy(p =>
                        {
                            object values = ReadDesignObject(p, "PVIs");
                            if (!(values is IEnumerable)) return int.MaxValue;
                            try { return ((IEnumerable)values).Cast<object>().Count(v => v != null); }
                            catch { return int.MaxValue; }
                        })
                        .FirstOrDefault();
                    if (selectedProfile == null) continue;
                    object pvis = ReadDesignObject(selectedProfile, "PVIs");
                    if (!(pvis is IEnumerable)) continue;
                    var pviList = ((IEnumerable)pvis).Cast<object>().Where(v => v != null)
                        .Select(v => new { Pvi = v, Station = ReadDesignDouble(v, "Station"), Elevation = ReadDesignDouble(v, "Elevation") })
                        .Where(v => !double.IsNaN(v.Station) && !double.IsNaN(v.Elevation))
                        .GroupBy(v => Math.Round(v.Station, 4).ToString("0.0000", CultureInfo.InvariantCulture) + "|" +
                                      Math.Round(v.Elevation, 4).ToString("0.0000", CultureInfo.InvariantCulture))
                        .Select(g => g.First()).OrderBy(v => v.Station).ToList();
                    double alignmentStart = ReadDesignDouble(alignment, "StartingStation", "StartStation");
                    double alignmentEnd = ReadDesignDouble(alignment, "EndingStation", "EndStation");
                    // Some Civil 3D profile APIs expose interior PVIs only. Add actual
                    // profile start/end points when absent; do not add fixed interval samples.
                    if (!double.IsNaN(alignmentStart) &&
                        (pviList.Count == 0 || Math.Abs(pviList[0].Station - alignmentStart) > 0.001))
                    {
                        double z = TryProfileElevationAt(selectedProfile, alignmentStart);
                        if (!double.IsNaN(z))
                            pviList.Insert(0, new { Pvi = (object)new ProfileEndpointData(alignmentStart, z), Station = alignmentStart, Elevation = z });
                    }
                    if (!double.IsNaN(alignmentEnd) &&
                        (pviList.Count == 0 || Math.Abs(pviList[pviList.Count - 1].Station - alignmentEnd) > 0.001))
                    {
                        double z = TryProfileElevationAt(selectedProfile, alignmentEnd);
                        if (!double.IsNaN(z))
                            pviList.Add(new { Pvi = (object)new ProfileEndpointData(alignmentEnd, z), Station = alignmentEnd, Elevation = z });
                    }
                    if (pviList.Count == 0) continue;
                    // Keep only actual profile start, PVI points and profile end; never
                    // sample the profile at a fixed interval for this report.
                    for (int i = 0; i < pviList.Count; i++)
                    {
                        object pvi = pviList[i].Pvi;
                        double station = pviList[i].Station;
                        double elevation = pviList[i].Elevation;
                        double gradeIn = ReadDesignDouble(pvi, "GradeIn", "IncomingGrade", "GradeInPercent");
                        // Civil 3D GradeIn/GradeOut are decimal gradients (0.005 = 0.5%).
                        if (!double.IsNaN(gradeIn)) gradeIn *= 100.0;
                        double gradeOut = ReadDesignDouble(pvi, "GradeOut", "OutgoingGrade", "GradeOutPercent");
                        if (!double.IsNaN(gradeOut)) gradeOut *= 100.0;
                        if (double.IsNaN(gradeIn) && i > 0) gradeIn = GradeBetween(pviList[i - 1].Pvi, pvi);
                        if (double.IsNaN(gradeOut) && i + 1 < pviList.Count) gradeOut = GradeBetween(pvi, pviList[i + 1].Pvi);
                        object curve = ReadDesignObject(pvi, "VerticalCurve", "Curve");
                        string curveType = ReadDesignText(curve, "CurveType", "Type");
                        if (string.IsNullOrWhiteSpace(curveType)) curveType = ReadDesignText(pvi, "CurveType", "VerticalCurveType");
                        double curveLength = ReadDesignDouble(curve, "Length", "CurveLength");
                        if (double.IsNaN(curveLength)) curveLength = ReadDesignDouble(pvi, "CurveLength", "VerticalCurveLength");
                        double k = ReadDesignDouble(curve, "K", "KValue");
                        if (double.IsNaN(k)) k = ReadDesignDouble(pvi, "K", "KValue");
                        double radius = ReadDesignDouble(curve, "Radius", "CurveRadius");
                        if (double.IsNaN(radius)) radius = ReadDesignDouble(pvi, "Radius", "CurveRadius");
                        // Calculate parabolic curve parameters from corrected percentage grades.
                        // Trust the profile's curve length, not a K-value computed with decimal gradients.
                        if (!double.IsNaN(curveLength) && curveLength > 0 &&
                            !double.IsNaN(gradeIn) && !double.IsNaN(gradeOut) &&
                            Math.Abs(gradeOut - gradeIn) > 1e-8)
                        {
                            k = curveLength / Math.Abs(gradeOut - gradeIn);
                            radius = k * 100.0;
                        }
                        double designSpeed = ReadDesignDouble(curve, "DesignSpeed", "DesignSpeedValue", "Speed");
                        if (double.IsNaN(designSpeed)) designSpeed = ReadDesignDouble(pvi, "DesignSpeed", "DesignSpeedValue", "Speed");
                        if (double.IsNaN(designSpeed)) designSpeed = ReadDesignDouble(selectedProfile, "DesignSpeed", "DesignSpeedValue", "Speed");
                        if (double.IsNaN(designSpeed)) designSpeed = ReadDesignDouble(alignment, "DesignSpeed", "DesignSpeedValue", "Speed");
                        if (string.IsNullOrWhiteSpace(curveType) && !double.IsNaN(gradeIn) && !double.IsNaN(gradeOut) && Math.Abs(gradeOut - gradeIn) > 1e-8)
                            curveType = gradeOut < gradeIn ? "Crest" : "Sag";
                        rows.Add(new List<string> {
                            alignment.Name ?? string.Empty, (i + 1).ToString(CultureInfo.InvariantCulture),
                            FormatStation(station), FormatMaybe(elevation, "0.000"),
                            double.IsNaN(gradeIn) ? string.Empty : FormatMaybe(gradeIn, "0.000") + "%",
                            double.IsNaN(gradeOut) ? string.Empty : FormatMaybe(gradeOut, "0.000") + "%",
                            curveType, FormatMaybe(curveLength, "0.000"), FormatMaybe(k, "0.000"),
                            FormatMaybe(radius, "0.000"), FormatMaybe(designSpeed, "0.###")
                        });
                    }
                }
                tr.Commit();
            }
            rows = rows.OrderBy(r => r.Count > 0 ? r[0] : string.Empty,
                Comparer<string>.Create(CompareNaturalPointNames))
                .ThenBy(r => r.Count > 2 ? ParseStationForSort(r[2]) : double.MaxValue).ToList();
            sheets[6] = ReplaceReportRows(sheets[6], rows);
        }

        private sealed class ProfileEndpointData
        {
            public double Station { get; private set; }
            public double Elevation { get; private set; }
            public ProfileEndpointData(double station, double elevation)
            {
                Station = station;
                Elevation = elevation;
            }
        }

        private static double TryProfileElevationAt(CivilProfile profile, double station)
        {
            if (profile == null || double.IsNaN(station) || double.IsInfinity(station)) return double.NaN;
            string[] names = { "ElevationAt", "ElevationAtStation", "FindElevationAtXY" };
            foreach (string name in names)
            {
                try
                {
                    MethodInfo method = profile.GetType().GetMethod(name, new[] { typeof(double) });
                    if (method == null) continue;
                    object value = method.Invoke(profile, new object[] { station });
                    if (value == null) continue;
                    double elevation = Convert.ToDouble(value, CultureInfo.InvariantCulture);                    if (!double.IsNaN(elevation) && !double.IsInfinity(elevation)) return elevation;
                }
                catch { }
            }
            return double.NaN;
        }


        private static bool IsLikelySampledProfile(CivilProfile profile)
        {
            if (profile == null) return true;
            string name = (profile.Name ?? string.Empty).Trim().ToUpperInvariant();
            if (name.Contains("EXIST") || name == "EG" || name.StartsWith("EG-") || name.StartsWith("EG_") ||
                name.Contains("NATURAL") || name.Contains("SURFACE") || name.Contains("SAMPLED") || name.Contains("GROUND PROFILE"))
                return true;
            object raw = ReadDesignObject(profile, "PVIs");
            if (!(raw is IEnumerable)) return false;
            var stations = new List<double>();
            try
            {
                foreach (object pvi in (IEnumerable)raw)
                {
                    if (pvi == null) continue;
                    double st = ReadDesignDouble(pvi, "Station");
                    if (!double.IsNaN(st) && !double.IsInfinity(st)) stations.Add(st);
                }
            }
            catch { return false; }
            stations = stations.Distinct().OrderBy(v => v).ToList();
            if (stations.Count < 8) return false;
            var gaps = new List<double>();
            for (int i = 1; i < stations.Count; i++)
            {
                double gap = stations[i] - stations[i - 1];
                if (gap > 1e-4) gaps.Add(gap);
            }
            if (gaps.Count < 7) return false;
            double median = gaps.OrderBy(v => v).ElementAt(gaps.Count / 2);
            if (median <= 0.0) return false;
            int regular = gaps.Count(g => Math.Abs(g - median) <= Math.Max(0.01, median * 0.002));
            return regular >= Math.Ceiling(gaps.Count * 0.80) && median <= 10.001;
        }

        private static int ProfileDesignNameScore(string profileName)
        {
            string name = (profileName ?? string.Empty).Trim().ToUpperInvariant();
            if (name == "FG" || name.StartsWith("FG-") || name.StartsWith("FG_")) return 100;
            if (name.Contains("DESIGN") || name.Contains("PROPOSED") || name.Contains("FINISHED")) return 90;
            if (name.Contains("ROAD") || name.Contains("FINAL")) return 80;
            return 10;
        }

        private static void PopulateConstructionLevelsReport(Document document, IList<XlsxSheet> sheets,
            double interval, double leftOffset, double centreOffset, double rightOffset)
        {
            if (document == null || sheets == null || sheets.Count < 8 || interval <= 0.0) return;
            var rows = new List<IList<string>>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface eg = FindExportSurface(document, tr, _exportGroundSurfaceName, false);
                CivilSurface fg = FindExportSurface(document, tr, _exportFinishedSurfaceName, true);
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    var designProfiles = new List<CivilProfile>();
                    foreach (ObjectId pid in alignment.GetProfileIds())
                    {
                        CivilProfile profile = tr.GetObject(pid, OpenMode.ForRead, false) as CivilProfile;
                        if (profile == null) continue;
                        string n = (profile.Name ?? string.Empty).Trim().ToUpperInvariant();
                        bool candidateEg = n == "EG" || n.StartsWith("EG-") || n.StartsWith("EG_") ||
                            n.Contains("EXIST") || n.Contains("NATURAL GROUND") || n.Contains("SURFACE PROFILE") ||
                            n.Contains("GROUND PROFILE");
                        if (!candidateEg) designProfiles.Add(profile);
                    }
                    CivilProfile designProfile = designProfiles
                        .OrderByDescending(p => ProfileDesignNameScore(p.Name))
                        .FirstOrDefault();
                    double startStation = ReadDesignDouble(alignment, "StartingStation");
                    double endStation = ReadDesignDouble(alignment, "EndingStation");
                    if (double.IsNaN(startStation) || double.IsNaN(endStation) || endStation < startStation) continue;
                    int guard = 0;
                    for (double station = startStation; station <= endStation + 1e-6 && guard < 200000; station += interval, guard++)
                    {
                        double x = double.NaN, y = double.NaN;
                        if (!TryAlignmentPoint(alignment, station, centreOffset, out x, out y)) continue;
                        double egLeft = SampleAtAlignment(alignment, eg, station, leftOffset);
                        double egCentre = SampleAtAlignment(alignment, eg, station, centreOffset);
                        double egRight = SampleAtAlignment(alignment, eg, station, rightOffset);
                        double fgLeft = SampleProfileOrSurface(alignment, designProfile, fg, station, leftOffset);
                        double fgCentre = SampleProfileOrSurface(alignment, designProfile, fg, station, centreOffset);
                        double fgRight = SampleProfileOrSurface(alignment, designProfile, fg, station, rightOffset);
                        rows.Add(new List<string> {
                            alignment.Name ?? string.Empty, FormatStation(station),
                            FormatMaybe(-y, "0.000"), FormatMaybe(x, "0.000"),
                            FormatMaybe(egLeft, "0.000"), FormatMaybe(egCentre, "0.000"), FormatMaybe(egRight, "0.000"),
                            FormatMaybe(fgLeft, "0.000"), FormatMaybe(fgCentre, "0.000"), FormatMaybe(fgRight, "0.000"),
                            Difference(fgLeft, egLeft), Difference(fgCentre, egCentre), Difference(fgRight, egRight)
                        });
                    }
                }
                tr.Commit();
            }
            rows = rows.OrderBy(r => r.Count > 0 ? r[0] : string.Empty,
                Comparer<string>.Create(CompareNaturalPointNames))
                .ThenBy(r => r.Count > 1 ? ParseStationForSort(r[1]) : double.NaN)
                .ToList();
            sheets[7] = ReplaceReportRows(sheets[7], rows);
        }

        private static CivilSurface FindUniqueFinishedGroundSurface(CivilDocument civil, Transaction tr)
        {
            var matches = new List<CivilSurface>();
            if (civil == null) return null;
            foreach (ObjectId id in civil.GetSurfaceIds())
            {
                CivilSurface surface = tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) continue;
                string name = (surface.Name ?? string.Empty).ToUpperInvariant();
                if (name.Contains("FINISHED") || name.Contains("DESIGN") || name == "FG" || name.StartsWith("FG-") || name.StartsWith("FG_"))
                    matches.Add(surface);
            }
            return matches.OrderByDescending(surface => SurfaceNameScore(surface.Name, true)).FirstOrDefault();
        }

        private static int SurfaceNameScore(string surfaceName, bool finished)
        {
            string name = (surfaceName ?? string.Empty).Trim().ToUpperInvariant();
            if (finished)
            {
                if (name == "FG") return 100;
                if (name.StartsWith("FG-") || name.StartsWith("FG_")) return 95;
                if (name.Contains("FINISHED GROUND")) return 90;
                if (name.Contains("DESIGN")) return 85;
                if (name.Contains("FINISHED")) return 80;
                return 50;
            }
            if (name == "EG") return 100;
            if (name.StartsWith("EG-") || name.StartsWith("EG_")) return 95;
            if (name.Contains("EXISTING GROUND")) return 90;
            if (name.Contains("NATURAL GROUND")) return 85;
            if (name.Contains("EXISTING")) return 80;
            if (name.Contains("GROUND")) return 70;
            return 50;
        }

        private static double GetPipeInvertElevation(object pipe, bool atStart, Point3d endpoint, double radius)
        {
            string[] candidates = atStart
                ? new[] { "StartInvertElevation", "InvertElevationAtStart", "StartInnerBottomElevation", "StartInvert", "StartSumpElevation" }
                : new[] { "EndInvertElevation", "InvertElevationAtEnd", "EndInnerBottomElevation", "EndInvert", "EndSumpElevation" };
            double value = ReadDesignDouble(pipe, candidates);
            if (!double.IsNaN(value)) return value;
            // Civil 3D pipe endpoints are commonly on the pipe centreline; subtract the
            // internal radius only as a fallback when no explicit invert property exists.
            return radius > 0.0 ? endpoint.Z - radius : endpoint.Z;
        }

        private static double SampleAtAlignment(CivilAlignment alignment, CivilSurface surface, double station, double offset)
        {
            double x, y;
            return surface != null && TryAlignmentPoint(alignment, station, offset, out x, out y)
                ? TryFindSurfaceElevation(surface, x, y) : double.NaN;
        }

        private static double SampleProfileOrSurface(CivilAlignment alignment, CivilProfile profile, CivilSurface surface, double station, double offset)
        {
            // Prefer the selected/uniquely identified finished-ground surface at every offset.
            // A design profile is only a fallback for centreline levels when no FG surface exists.
            double sampled = SampleAtAlignment(alignment, surface, station, offset);
            if (!double.IsNaN(sampled)) return sampled;
            if (Math.Abs(offset) < 1e-8 && profile != null)
            {
                try
                {
                    MethodInfo method = profile.GetType().GetMethod("ElevationAt", new[] { typeof(double) });
                    if (method != null) return Convert.ToDouble(method.Invoke(profile, new object[] { station }), CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return double.NaN;
        }

        private static void PopulateJunctionSurfaceLevels(Document document, IList<IList<string>> junctionRows)
        {
            if (document == null || junctionRows == null || junctionRows.Count <= 2) return;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface eg = FindExportSurface(document, tr, _exportGroundSurfaceName, false);
                CivilSurface fg = FindExportSurface(document, tr, _exportFinishedSurfaceName, true);
                for (int i = 2; i < junctionRows.Count; i++)
                {
                    IList<string> row = junctionRows[i];
                    if (row == null || row.Count < 6) continue;
                    double y, x;
                    if (!double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                        !double.TryParse(row[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                    double egLevel = TryFindSurfaceElevation(eg, x, y);
                    double fgLevel = TryFindSurfaceElevation(fg, x, y);
                    if (!double.IsNaN(egLevel)) row[3] = egLevel.ToString("0.000", CultureInfo.InvariantCulture);
                    if (!double.IsNaN(fgLevel)) row[4] = fgLevel.ToString("0.000", CultureInfo.InvariantCulture);
                    else if (string.IsNullOrWhiteSpace(row[4])) row[4] = string.Empty;
                    double finalLevel;
                    if (double.TryParse(row[4], NumberStyles.Float, CultureInfo.InvariantCulture, out finalLevel) && !double.IsNaN(egLevel))
                        row[5] = (finalLevel - egLevel).ToString("0.000", CultureInfo.InvariantCulture);
                }
                tr.Commit();
            }
        }

        private static bool TryPointXY(object point, out double x, out double y)
        {
            x = y = double.NaN;
            if (point == null) return false;
            x = ReadDesignDouble(point, "X", "Easting");
            y = ReadDesignDouble(point, "Y", "Northing");
            return !double.IsNaN(x) && !double.IsNaN(y) &&
                   !double.IsInfinity(x) && !double.IsInfinity(y);
        }

        private static bool TryAlignmentStation(object alignment, double x, double y, out double station)
        {
            station = double.NaN;
            if (alignment == null) return false;
            try
            {
                MethodInfo method = alignment.GetType().GetMethod("StationOffset",
                    new[] { typeof(double), typeof(double), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() });
                if (method == null) return false;
                object[] args = { x, y, 0.0, 0.0 };
                method.Invoke(alignment, args);
                station = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                return !double.IsNaN(station) && !double.IsInfinity(station);
            }
            catch { return false; }
        }

        private static bool TryAlignmentPoint(object alignment, double station, double offset, out double x, out double y)
        {
            x = y = double.NaN;
            try
            {
                MethodInfo method = alignment.GetType().GetMethod("PointLocation", new[] { typeof(double), typeof(double), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() });
                if (method == null) return false;
                object[] args = { station, offset, 0.0, 0.0 };
                method.Invoke(alignment, args);
                x = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                y = Convert.ToDouble(args[3], CultureInfo.InvariantCulture);
                return !(double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y));
            }
            catch { return false; }
        }

        private static object ReadDesignObject(object instance, params string[] propertyNames)
        {
            if (instance == null || propertyNames == null) return null;
            foreach (string name in propertyNames)
            {
                try { PropertyInfo p = instance.GetType().GetProperty(name); if (p != null && p.CanRead) { object value = p.GetValue(instance, null); if (value != null) return value; } } catch { }
                try { FieldInfo f = instance.GetType().GetField(name); if (f != null) { object value = f.GetValue(instance); if (value != null) return value; } } catch { }
            }
            return null;
        }

        private static string FormatDesignPoint(object point)
        {
            if (point == null) return string.Empty;
            double x = ReadDesignDouble(point, "X", "Easting");
            double y = ReadDesignDouble(point, "Y", "Northing");
            if (double.IsNaN(x) || double.IsNaN(y)) return string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "({0:0.000}m,{1:0.000}m)", -y, -x);
        }

        private static string FormatStation(double station)
        {
            return double.IsNaN(station) || double.IsInfinity(station) ? string.Empty : station.ToString("0.000m", CultureInfo.InvariantCulture);
        }

        private static string FormatMaybe(double value, string format)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? string.Empty : value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string Difference(double finalLevel, double existingLevel)
        {
            return double.IsNaN(finalLevel) || double.IsNaN(existingLevel) ? string.Empty : (finalLevel - existingLevel).ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static double GradeBetween(object a, object b)
        {
            double s1 = ReadDesignDouble(a, "Station"), e1 = ReadDesignDouble(a, "Elevation");
            double s2 = ReadDesignDouble(b, "Station"), e2 = ReadDesignDouble(b, "Elevation");
            return Math.Abs(s2 - s1) < 1e-9 ? double.NaN : ((e2 - e1) / (s2 - s1)) * 100.0;
        }

        private static bool IsSewerNetworkName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string value = name.ToUpperInvariant();
            return value.Contains("SEWER") || value.Contains("FOUL") ||
                   value.Contains("SANITARY") || value.Contains("WASTEWATER");
        }

        private static void AddDesignInvert(Dictionary<ObjectId, List<double>> values, ObjectId structureId, double invert)
        {
            if (structureId.IsNull || double.IsNaN(invert) || double.IsInfinity(invert)) return;
            List<double> list;
            if (!values.TryGetValue(structureId, out list))
            {
                list = new List<double>();
                values.Add(structureId, list);
            }
            list.Add(invert);
        }

        private static XlsxSheet ReplaceReportRows(XlsxSheet sheet, IList<IList<string>> dataRows)
        {
            var rows = new List<IList<string>>();
            if (sheet != null && sheet.Rows != null)
            {
                rows.AddRange(sheet.Rows.Take(2));
            }
            if (dataRows != null && dataRows.Count > 0)
                rows.AddRange(dataRows);
            else
                rows.Add(new List<string> { "No matching Civil 3D data found for this report. Check the selected objects, surface names, network names and profile availability." });
            return new XlsxSheet(sheet == null ? "Report" : sheet.Name, rows);
        }

        private static string ReadDesignText(object instance, params string[] propertyNames)
        {
            if (instance == null || propertyNames == null) return string.Empty;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = instance.GetType().GetProperty(propertyName);
                    if (property == null || !property.CanRead) continue;
                    object value = property.GetValue(instance, null);
                    if (value == null) continue;
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
                catch { }
            }
            return string.Empty;
        }

        private static double ReadDesignDouble(object instance, params string[] propertyNames)
        {
            if (instance == null || propertyNames == null) return double.NaN;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = instance.GetType().GetProperty(propertyName);
                    if (property == null || !property.CanRead) continue;
                    object value = property.GetValue(instance, null);
                    if (value == null) continue;
                    double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(number) && !double.IsInfinity(number)) return number;
                }
                catch { }
            }
            return double.NaN;
        }

        // These report schemas mirror the screenshots. Source rows are intentionally not
        // fabricated from generic drawing extents; each report needs matching Civil 3D objects.
        private static int CompareNaturalPointNames(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            int i = 0;
            int j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    int leftEnd = i;
                    int rightEnd = j;
                    while (leftEnd < left.Length && char.IsDigit(left[leftEnd])) leftEnd++;
                    while (rightEnd < right.Length && char.IsDigit(right[rightEnd])) rightEnd++;

                    int leftSignificant = i;
                    int rightSignificant = j;
                    while (leftSignificant < leftEnd && left[leftSignificant] == '0') leftSignificant++;
                    while (rightSignificant < rightEnd && right[rightSignificant] == '0') rightSignificant++;

                    int leftDigits = leftEnd - leftSignificant;
                    int rightDigits = rightEnd - rightSignificant;
                    if (leftDigits != rightDigits) return leftDigits.CompareTo(rightDigits);

                    int numericCompare = string.Compare(left, leftSignificant, right, rightSignificant,
                        leftDigits, StringComparison.Ordinal);
                    if (numericCompare != 0) return numericCompare;

                    i = leftEnd;
                    j = rightEnd;
                }
                else
                {
                    int textCompare = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                    if (textCompare != 0) return textCompare;
                    i++;
                    j++;
                }
            }
            return (left.Length - i).CompareTo(right.Length - j);
        }

        private static XlsxSheet SortReportDataRowsNaturally(XlsxSheet sheet)
        {
            if (sheet == null || sheet.Rows == null || sheet.Rows.Count <= 2)
                return sheet;

            var sortedRows = sheet.Rows.Take(2)
                .Concat(sheet.Rows.Skip(2).OrderBy(
                    row => row != null && row.Count > 0 ? row[0] : string.Empty,
                    Comparer<string>.Create(CompareNaturalPointNames)))
                .ToList();

            return new XlsxSheet(sheet.Name, sortedRows);
        }

        private static void SetConstructionLevelHeaders(IList<XlsxSheet> sheets, double leftOffset, double centreOffset, double rightOffset)
        {
            if (sheets == null || sheets.Count <= 7 || sheets[7] == null || sheets[7].Rows == null || sheets[7].Rows.Count < 2)
                return;
            IList<string> headers = sheets[7].Rows[1];
            if (headers == null || headers.Count < 13) return;
            headers[4] = "NG Left (" + FormatOffset(leftOffset) + " m)";
            headers[5] = "NG CL (" + FormatOffset(centreOffset) + " m)";
            headers[6] = "NG Right (" + FormatOffset(rightOffset) + " m)";
            headers[7] = "Final Road Left (" + FormatOffset(leftOffset) + " m)";
            headers[8] = "Final Road CL (" + FormatOffset(centreOffset) + " m)";
            headers[9] = "Final Road Right (" + FormatOffset(rightOffset) + " m)";
            headers[10] = "Difference Left (" + FormatOffset(leftOffset) + " m)";
            headers[11] = "Difference CL (" + FormatOffset(centreOffset) + " m)";
            headers[12] = "Difference Right (" + FormatOffset(rightOffset) + " m)";
        }

        private static string FormatOffset(double value)
        {
            return value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
        }

        private static double ParseStationForSort(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return double.MaxValue;
            string cleaned = value.Trim();
            if (cleaned.EndsWith("m", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(0, cleaned.Length - 1);
            double result;
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                ? result : double.MaxValue;
        }

        private static List<XlsxSheet> BuildDesignDataReportLayouts()
        {
            return new List<XlsxSheet>
            {
                ReportSheet("Road Junction Setting-Out", "ROAD JUNCTIONS - SETTING-OUT CO-ORDINATES", new[] { "POINT", "Y", "X", "EXISTING GROUND LEVEL (m)", "FINAL LEVEL (m)", "HEIGHT DIFFERENCE (m)" }),
                ReportSheet("Water Structure Data", "WATER - STRUCTURE DATA", new[] { "Node Name", "Type", "Y Coordinate", "X Coordinate", "Surface Elevation (m)", "Rim Elevation (m)", "Invert Elevation (m)", "Depth Below Surface (m)" }),
                ReportSheet("Water Pipe Data", "WATER - PIPE DATA", new[] { "Pipe Name", "Pipe Description", "Pipe Size", "Length (m)", "Slope (%)", "Start Invert Elevation (m)", "End Invert Elevation (m)", "Branch Name" }),
                ReportSheet("Sewer Structure Data", "SEWER - STRUCTURE DATA", new[] { "Node Name", "X Coordinate", "Y Coordinate", "Surface Elevation (m)", "Rim Elevation (m)", "Invert Elevation (m)", "Depth (m)", "Node Type" }),
                ReportSheet("Sewer Pipe Data", "SEWER - PIPE DATA", new[] { "Pipe Name", "Pipe Description", "Pipe Size", "Length (m)", "Slope (%)", "Start Invert Elevation (m)", "End Invert Elevation (m)", "Branch Name" }),
                ReportSheet("Horizontal Alignment Data", "ROAD - HORIZONTAL ALIGNMENT DATA", new[] { "Alignment", "Road Length (m)", "No.", "Type", "Entity Length (m)", "Radius (m)", "Design Speed", "Direction", "Start Station", "End Station", "PI Station", "PI Point (-Y,-X)", "Start Point (-Y,-X)", "End Point (-Y,-X)", "Center Point (-Y,-X)" }),
                ReportSheet("Vertical Alignment Data", "ROAD - VERTICAL ALIGNMENT DATA", new[] { "Alignment", "No.", "PVI Station", "PVI Elevation", "Grade In (%)", "Grade Out (%)", "Profile Curve Type", "Profile Curve Length (m)", "K Value", "Curve Radius (m)", "Design Speed" }),
                ReportSheet("Construction Levels", "ROAD - LEVELS FOR CONSTRUCTION", new[] { "Alignment", "Chainage (m)", "Y(-)", "X(+)", "NG - Left - 3.000 (m)", "NG - CL Levels (m)", "NG - Right - 3.000 (m)", "Final Road - 3.000m - Left (m)", "Final Road CL Levels (m)", "Final Road - 3.000m - Right (m)", "Left - 3.000 (m) - Diff", "CL - 0.000 (m) - Diff", "Right - 3.000 (m) - Diff" })
            };
        }

        private static XlsxSheet ReportSheet(string name, string title, string[] headers)
        {
            var rows = new List<IList<string>>();
            rows.Add(new List<string> { title });
            rows.Add(new List<string>(headers));
            rows.Add(new List<string> { "No rows populated: matching Civil 3D data extraction is not yet implemented for this report." });
            return new XlsxSheet(name, rows);
        }

        private static string ExportPreferenceFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CE Tools");
            }
        }

        private static string LastExportPathFile
        {
            get { return Path.Combine(ExportPreferenceFolder, "DesignDataExport.last-path"); }
        }

        private static string GetDefaultExportPath(string fileName)
        {
            try
            {
                if (File.Exists(LastExportPathFile))
                {
                    string lastPath = File.ReadAllText(LastExportPathFile).Trim();
                    if (!string.IsNullOrWhiteSpace(lastPath))
                    {
                        string directory = Directory.Exists(lastPath) ? lastPath : Path.GetDirectoryName(lastPath);
                        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                            return Path.Combine(directory, fileName);
                    }
                }
            }
            catch { }
            return fileName;
        }

        private static void SaveLastExportPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                Directory.CreateDirectory(ExportPreferenceFolder);
                File.WriteAllText(LastExportPathFile, Path.GetFullPath(path));
            }
            catch { }
        }

        private static void TryOpenExportedWorkbook(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SettingOutInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE setting-out table for information: ");
            if (result.Status != PromptStatus.OK) return;

            try
            {
                SettingOutLink link;
                int existingRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(document.Database, table, transaction);
                    existingRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }

                int missing;
                List<SettingOutRow> current;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    current = ReadRows(document.Database, transaction, link, out missing);
                }
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Schedule type", link.ScheduleType),
                    Pair("Linked point handles", link.SourceHandles.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Existing table rows", existingRows.ToString(CultureInfo.InvariantCulture)),
                    Pair("Current usable rows", current.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Missing/unavailable sources", missing.ToString(CultureInfo.InvariantCulture)),
                    Pair("Ground elevation source", link.Ground.DisplayName),
                    Pair("Design elevation source", link.Design.DisplayName)
                };
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Setting-Out Information",
                    "The selected table is linked to point and surface handles in the current DWG.",
                    rows,
                    "CE TOOLS SETTING-OUT INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTINFO stopped. {0}",
                    exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<ObjectId> tables = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tables)
            {
                try
                {
                    int rows;
                    int missing;
                    RefreshTable(document.Database, tableId, out rows, out missing);
                    refreshed++;
                }
                catch
                {
                    // One stale table must not block other linked schedules.
                }
            }
            return refreshed;
        }

        internal static int CountLinkedTables(Database database)
        {
            return database == null ? 0 : FindLinkedTables(database).Count;
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertionPoint,
            IList<SettingOutRow> rows,
            SettingOutLink link,
            double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertionPoint;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, link);
                PopulateTable(table, rows, textHeight, link.ScheduleType);
                transaction.Commit();
                return tableId;
            }
        }

        private static void RefreshTable(
            Database database,
            ObjectId tableId,
            out int rowCount,
            out int missing)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                    throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                SettingOutLink link = ReadLink(database, table, transaction);
                List<SettingOutRow> rows = ReadRows(database, transaction, link, out missing);
                if (rows.Count == 0)
                    throw new InvalidOperationException("The linked schedule has no usable current point sources.");
                AnnotationOptions settings = AnnotationSettingsStore.Read(database);
                double height = PaperAnnotationScale.ModelTextHeight(
                    database,
                    settings == null ? 2.0 : settings.TextHeight);
                PopulateTable(table, rows, height, link.ScheduleType);
                rowCount = rows.Count;
                transaction.Commit();
            }
        }

        private static void ExportTable(Document document, ObjectId tableId)
        {
            Editor editor = document.Editor;
            try
            {
                int rows;
                int missing;
                RefreshTable(document.Database, tableId, out rows, out missing);
                IList<IList<string>> cells;
                string type;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableId,
                        OpenMode.ForRead,
                        false) as Table;
                    SettingOutLink link = ReadLink(document.Database, table, transaction);
                    type = link.ScheduleType;
                    cells = ReadTableCells(table);
                }

                string path;
                var save = new SaveFileDialog();
                {
                    string initialPath = GetDefaultExportPath("CE-" + SanitizeFileName(type) + "-Setting-Out.xlsx");
                    save.Filter = "Excel Workbook (*.xlsx)|*.xlsx";
                    save.Title = "Export CE Tools Setting-Out Schedule";
                    save.DefaultExt = ".xlsx";
                    save.AddExtension = true;
                    save.FileName = Path.GetFileName(initialPath);
                    if (Directory.Exists(Path.GetDirectoryName(initialPath)))
                        save.InitialDirectory = Path.GetDirectoryName(initialPath);
                    if (save.ShowDialog() != true) return;
                    path = save.FileName;
                }
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Setting Out", cells);
                SaveLastExportPath(path);
                TryOpenExportedWorkbook(path);
                editor.WriteMessage(
                    "\nCE_SETTINGOUTEXPORT complete. Rows={0}; missing sources={1}; file={2}",
                    rows,
                    missing,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTEXPORT stopped. {0}",
                    exception.Message);
            }
        }

        private static List<SettingOutRow> ReadRows(
            Database database,
            Transaction transaction,
            SettingOutLink link,
            out int missing)
        {
            var rows = new List<SettingOutRow>();
            missing = 0;
            CivilSurface ground = OpenSurface(database, transaction, link.Ground);
            CivilSurface design = OpenSurface(database, transaction, link.Design);
            int fallback = 1;
            foreach (string handleText in link.SourceHandles)
            {
                ObjectId id;
                if (!TryResolveHandle(database, handleText, out id))
                {
                    missing++;
                    continue;
                }
                DBObject value;
                try
                {
                    value = transaction.GetObject(id, OpenMode.ForRead, false);
                }
                catch
                {
                    missing++;
                    continue;
                }

                string description;
                Point3d point;
                CivilCogoPoint cogo = value as CivilCogoPoint;
                if (cogo != null)
                {
                    description = string.IsNullOrWhiteSpace(cogo.PointName)
                        ? cogo.RawDescription
                        : cogo.PointName;
                    if (string.IsNullOrWhiteSpace(description))
                        description = "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture);
                    point = new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
                }
                else
                {
                    DBPoint dbPoint = value as DBPoint;
                    if (dbPoint == null)
                    {
                        missing++;
                        continue;
                    }
                    description = "P" + fallback.ToString(CultureInfo.InvariantCulture);
                    point = dbPoint.Position;
                    fallback++;
                }

                double? groundElevation = ResolveElevation(link.Ground, ground, point);
                double? designElevation = ResolveElevation(link.Design, design, point);
                double? difference = groundElevation.HasValue && designElevation.HasValue
                    ? designElevation.Value - groundElevation.Value
                    : (double?)null;
                rows.Add(new SettingOutRow(
                    description,
                    point.X,
                    point.Y,
                    groundElevation,
                    designElevation,
                    difference));
            }
            return rows.OrderBy(item => item.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static CivilSurface OpenSurface(
            Database database,
            Transaction transaction,
            SettingOutSurfaceChoice choice)
        {
            if (choice == null || choice.Mode != SettingOutElevationMode.Surface)
                return null;
            ObjectId id;
            if (!TryResolveHandle(database, choice.HandleText, out id))
                return null;
            return transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
        }

        private static double? ResolveElevation(
            SettingOutSurfaceChoice choice,
            CivilSurface surface,
            Point3d point)
        {
            if (choice == null || choice.Mode == SettingOutElevationMode.Blank)
                return null;
            if (choice.Mode == SettingOutElevationMode.Point)
                return point.Z;
            if (surface == null) return null;
            try
            {
                return surface.FindElevationAtXY(point.X, point.Y);
            }
            catch
            {
                return null;
            }
        }

        private static void PopulateTable(
            Table table,
            IList<SettingOutRow> rows,
            double textHeight,
            string scheduleType)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("A setting-out table cannot be populated with zero rows.");
            const int columns = 6;
            double height = NormalizeHeight(textHeight);
            table.SetSize(rows.Count + 2, columns);
            table.SetRowHeight(Math.Max(height * 2.5, 0.001));
            table.SetColumnWidth(Math.Max(height * 12.0, 0.001));
            table.Columns[0].Width = Math.Max(height * 14.0, 0.001);
            table.Cells[0, 0].TextString = "CE TOOLS " + scheduleType.ToUpperInvariant() + " SETTING-OUT SCHEDULE";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            string[] headings =
            {
                "POINT DESCRIPTION",
                "X COORDINATE",
                "Y COORDINATE",
                "GROUND ELEVATION",
                "DESIGN ELEVATION",
                "DIFFERENCE"
            };
            for (int column = 0; column < columns; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
            }
            table.Cells[0, 0].TextHeight = height * 1.2;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            for (int index = 0; index < rows.Count; index++)
            {
                SettingOutRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.Description;
                table.Cells[tableRow, 1].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 2].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = FormatNullable(row.GroundElevation);
                table.Cells[tableRow, 4].TextString = FormatNullable(row.DesignElevation);
                table.Cells[tableRow, 5].TextString = FormatNullable(row.Difference);
                for (int column = 0; column < columns; column++)
                {
                    table.Cells[tableRow, column].TextHeight = height;
                    table.Cells[tableRow, column].Alignment = CellAlignment.MiddleCenter;
                }
            }
            table.GenerateLayout();
        }

        private static void WriteLink(
            Table table,
            Transaction transaction,
            SettingOutLink link)
        {
            if (table.ExtensionDictionary.IsNull)
                table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null)
                throw new InvalidOperationException("The table extension dictionary could not be opened.");
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(LinkRecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Type=" + link.ScheduleType),
                new TypedValue((int)DxfCode.Text, "Ground=" + link.Ground.Serialize()),
                new TypedValue((int)DxfCode.Text, "Design=" + link.Design.Serialize())
            };
            foreach (string handle in link.SourceHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Handle=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SettingOutLink ReadLink(
            Database database,
            Table table,
            Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not a linked CE setting-out schedule.");
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE setting-out link record.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE setting-out link record is empty.");

            string type = "General";
            SettingOutSurfaceChoice ground = SettingOutSurfaceChoice.PointElevation();
            SettingOutSurfaceChoice design = SettingOutSurfaceChoice.PointElevation();
            var handles = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Type=", StringComparison.OrdinalIgnoreCase))
                    type = text.Substring("Type=".Length);
                else if (text.StartsWith("Ground=", StringComparison.OrdinalIgnoreCase))
                    ground = SettingOutSurfaceChoice.Parse(text.Substring("Ground=".Length));
                else if (text.StartsWith("Design=", StringComparison.OrdinalIgnoreCase))
                    design = SettingOutSurfaceChoice.Parse(text.Substring("Design=".Length));
                else if (text.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase))
                    handles.Add(text.Substring("Handle=".Length));
            }
            if (handles.Count == 0)
                throw new InvalidOperationException("The linked setting-out schedule contains no point handles.");
            return new SettingOutLink(type, ground, design, handles);
        }

        private static List<SettingOutSurfaceChoice> ReadSurfaceChoices(Document document)
        {
            var choices = new List<SettingOutSurfaceChoice>
            {
                SettingOutSurfaceChoice.PointElevation(),
                SettingOutSurfaceChoice.Blank()
            };
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return choices;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(
                            surfaceId,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                        if (surface == null) continue;
                        choices.Add(SettingOutSurfaceChoice.Surface(
                            surface.Name,
                            surfaceId.Handle.ToString()));
                    }
                }
            }
            catch
            {
                // Keep point-elevation and blank options when the Civil API cannot enumerate surfaces.
            }
            return choices;        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId objectId in currentSpace)
                {
                    Table table = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Table;
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(
                        table.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkRecordName))
                        result.Add(objectId);
                }
            }
            return result;
        }

        private static IList<IList<string>> ReadTableCells(Table table)
        {
            var rows = new List<IList<string>>();
            if (table == null) return rows;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                    values.Add(table.Cells[row, column].TextString ?? string.Empty);
                rows.Add(values);
            }
            return rows;
        }

        private static PromptEntityResult PromptForLinkedTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static bool TryResolveHandle(Database database, string handleText, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static double NormalizeHeight(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
                ? value
                : 0.002;
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("N3", CultureInfo.CurrentCulture)
                : string.Empty;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "General";
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '-');
            return value.Replace(' ', '-');
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal enum SettingOutElevationMode
    {
        Point,
        Blank,
        Surface
    }

    internal sealed class SettingOutSurfaceChoice
    {
        private SettingOutSurfaceChoice(
            SettingOutElevationMode mode,
            string displayName,
            string handleText)
        {
            Mode = mode;
            DisplayName = displayName;
            HandleText = handleText ?? string.Empty;
        }

        public SettingOutElevationMode Mode { get; private set; }
        public string DisplayName { get; private set; }
        public string HandleText { get; private set; }

        public static SettingOutSurfaceChoice PointElevation()
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Point,
                "<Use point elevation>",
                string.Empty);
        }

        public static SettingOutSurfaceChoice Blank()
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Blank,
                "<Leave blank>",
                string.Empty);
        }

        public static SettingOutSurfaceChoice Surface(string name, string handle)
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Surface,
                name,
                handle);
        }

        public string Serialize()
        {
            return Mode + "|" + HandleText + "|" + DisplayName;
        }

        public static SettingOutSurfaceChoice Parse(string value)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { '|' }, 3);
            SettingOutElevationMode mode;
            if (parts.Length == 0 || !Enum.TryParse(parts[0], true, out mode))
                return PointElevation();
            string handle = parts.Length > 1 ? parts[1] : string.Empty;
            string name = parts.Length > 2 ? parts[2] : string.Empty;
            if (mode == SettingOutElevationMode.Blank) return Blank();
            if (mode == SettingOutElevationMode.Point) return PointElevation();
            return Surface(string.IsNullOrWhiteSpace(name) ? "<Surface>" : name, handle);
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class SettingOutLink
    {
        public SettingOutLink(
            string scheduleType,
            SettingOutSurfaceChoice ground,
            SettingOutSurfaceChoice design,
            IEnumerable<string> sourceHandles)
        {
            ScheduleType = string.IsNullOrWhiteSpace(scheduleType)
                ? "General"
                : scheduleType;
            Ground = ground ?? SettingOutSurfaceChoice.PointElevation();
            Design = design ?? SettingOutSurfaceChoice.PointElevation();
            SourceHandles = sourceHandles == null
                ? new List<string>()
                : sourceHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string ScheduleType { get; private set; }
        public SettingOutSurfaceChoice Ground { get; private set; }
        public SettingOutSurfaceChoice Design { get; private set; }
        public List<string> SourceHandles { get; private set; }
    }

    internal sealed class SettingOutRow
    {
        public SettingOutRow(
            string description,
            double x,
            double y,
            double? groundElevation,
            double? designElevation,
            double? difference)
        {
            Description = description;
            X = x;
            Y = y;
            GroundElevation = groundElevation;
            DesignElevation = designElevation;
            Difference = difference;
        }

        public string Description { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double? GroundElevation { get; private set; }
        public double? DesignElevation { get; private set; }
        public double? Difference { get; private set; }
    }

    internal static class DesignDataExportDefaultsStore
    {
        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools", "DesignDataExport.defaults"); }
        }
        public static string[] Read()
        {
            string[] values = { "true", "true", "10", "-3", "0", "3" };
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] saved = File.ReadAllLines(FilePath);
                    for (int i = 0; i < values.Length && i < saved.Length; i++)
                        if (!string.IsNullOrWhiteSpace(saved[i])) values[i] = saved[i].Trim();
                }
            }
            catch { }
            return values;
        }
        public static void Save(bool swap, bool reverse, double interval, double left, double centre, double right)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(FilePath, new[] { swap.ToString().ToLowerInvariant(), reverse.ToString().ToLowerInvariant(),
                    interval.ToString("0.########", CultureInfo.InvariantCulture), left.ToString("0.########", CultureInfo.InvariantCulture),
                    centre.ToString("0.########", CultureInfo.InvariantCulture), right.ToString("0.########", CultureInfo.InvariantCulture) });
            }
            catch { }
        }
    }

    internal static class DesignDataExportSurfaceStore
    {
        private static string FileName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools", "DesignDataExport.surfaces"); } }
        public static string Read(bool finished)
        {
            try { string[] names = File.ReadAllLines(FileName); return names.Length > (finished ? 1 : 0) ? names[finished ? 1 : 0] : string.Empty; }
            catch { return string.Empty; }
        }
        public static void Save(string ground, string finished)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FileName)); File.WriteAllLines(FileName, new[] { ground ?? string.Empty, finished ?? string.Empty }); }
            catch { }
        }
    }

    internal sealed class DesignDataExportOptionsWindow : Window
    {
        private readonly CheckBox _swapCoordinates;
        private readonly CheckBox _reverseSigns;
        private readonly TextBox _interval;
        private readonly TextBox _leftOffset;
        private readonly TextBox _centreOffset;
        private readonly TextBox _rightOffset;
        private readonly ComboBox _groundSurface;
        private readonly ComboBox _finishedSurface;

        public DesignDataExportOptionsWindow(Document document)
        {
            Title = "CE Tools - Design Data Export Options";
            Width = 520;
            Height = 475;
            MinWidth = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Accepted = false;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Design data export settings",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Set coordinate formatting and construction-level sampling. No settings are entered at the command line.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = new Button { Content = "Export", MinWidth = 100, Padding = new Thickness(10, 6, 10, 6) };
            ok.Click += delegate
            {
                double interval, left, centre, right;
                if (!double.TryParse(_interval.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out interval) ||
                    !double.TryParse(_leftOffset.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out left) ||
                    !double.TryParse(_centreOffset.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out centre) ||
                    !double.TryParse(_rightOffset.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out right) ||
                    interval <= 0.0)
                {
                    MessageBox.Show(this, "Enter a positive chainage interval and valid numeric offsets.",
                        "CE Tools - Invalid Export Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ChainageInterval = interval;
                LeftOffset = left;
                CentreOffset = centre;
                RightOffset = right;
                SaveDesignExportDefaults(SwapCoordinates, ReverseCoordinateSigns, interval, left, centre, right);
                DesignDataExportSurfaceStore.Save(GroundSurfaceName, FinishedSurfaceName);
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
            cancel.Click += delegate { Accepted = false; DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 8; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _swapCoordinates = new CheckBox { Content = "Swap X and Y coordinates", Margin = new Thickness(0, 5, 0, 5) };
            _reverseSigns = new CheckBox { Content = "Reverse coordinate signs", Margin = new Thickness(0, 5, 0, 5) };
            string[] defaults = ReadDesignExportDefaults();
            _swapCoordinates.IsChecked = defaults[0] == "true";
            _reverseSigns.IsChecked = defaults[1] == "true";
            _interval = CreateTextBox(defaults[2]);
            _leftOffset = CreateTextBox(defaults[3]);
            _centreOffset = CreateTextBox(defaults[4]);
            _rightOffset = CreateTextBox(defaults[5]);
            AddControlRow(grid, 0, _swapCoordinates, null);
            AddControlRow(grid, 1, _reverseSigns, null);
            AddControlRow(grid, 2, "Chainage interval (drawing units)", _interval);
            AddControlRow(grid, 3, "Left offset from alignment", _leftOffset);
            AddControlRow(grid, 4, "Centre offset from alignment", _centreOffset);
            AddControlRow(grid, 5, "Right offset from alignment", _rightOffset);
            _groundSurface = SurfaceSelector(document, false);
            _finishedSurface = SurfaceSelector(document, true);
            AddControlRow(grid, 6, "Existing / natural ground surface", _groundSurface);
            AddControlRow(grid, 7, "Final / design surface", _finishedSurface);
            root.Children.Add(grid);
            Loaded += delegate { CeInterfaceTheme.Apply(this); };
        }

        public bool Accepted { get; private set; }
        public string GroundSurfaceName { get { return Convert.ToString(_groundSurface.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty; } }
        public string FinishedSurfaceName { get { return Convert.ToString(_finishedSurface.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty; } }
        private static ComboBox SurfaceSelector(Document document, bool finished)
        {
            var combo = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 3, 0, 3) };
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilApplication.ActiveDocument.GetSurfaceIds())
                {
                    CivilSurface surface = tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null) combo.Items.Add(surface.Name);
                }
            }
            string last = DesignDataExportSurfaceStore.Read(finished);
            foreach (object item in combo.Items)
                if (string.Equals(Convert.ToString(item), last, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; break; }
            if (combo.SelectedItem == null && combo.Items.Count > 0)
            {
                string[] words = finished ? new[] { "FG", "FINAL", "DESIGN", "FINISHED" } : new[] { "EG", "NG", "EXIST", "NATURAL", "GROUND" };
                foreach (object item in combo.Items)
                    if (words.Any(word => Convert.ToString(item).ToUpperInvariant().Contains(word))) { combo.SelectedItem = item; break; }
                if (combo.SelectedItem == null) combo.SelectedIndex = 0;
            }
            return combo;
        }
        public bool SwapCoordinates { get { return _swapCoordinates.IsChecked == true; } }
        public bool ReverseCoordinateSigns { get { return _reverseSigns.IsChecked == true; } }
        public double ChainageInterval { get; private set; }
        public double LeftOffset { get; private set; }
        public double CentreOffset { get; private set; }
        public double RightOffset { get; private set; }

        private static string[] ReadDesignExportDefaults() { return DesignDataExportDefaultsStore.Read(); }
        private static void SaveDesignExportDefaults(bool swap, bool reverse, double interval, double left, double centre, double right)
        { DesignDataExportDefaultsStore.Save(swap, reverse, interval, left, centre, right); }

        private static TextBox CreateTextBox(string value)
        {
            return new TextBox { Text = value, MinWidth = 120, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(4, 3, 4, 3) };
        }

        private static void AddControlRow(Grid grid, int row, object labelOrControl, Control control)
        {
            if (labelOrControl is Control && control == null)
            {
                Control onlyControl = (Control)labelOrControl;
                Grid.SetRow(onlyControl, row);
                Grid.SetColumn(onlyControl, 0);
                Grid.SetColumnSpan(onlyControl, 2);
                grid.Children.Add(onlyControl);
                return;
            }
            var label = new TextBlock
            {
                Text = Convert.ToString(labelOrControl, CultureInfo.CurrentCulture),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 10, 3)
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
    }

    internal sealed class SettingOutConfigurationWindow : Window
    {
        private readonly ComboBox _type;
        private readonly ComboBox _ground;
        private readonly ComboBox _design;

        public SettingOutConfigurationWindow(IList<SettingOutSurfaceChoice> choices)
        {
            Accepted = false;
            Title = "CE Tools - Setting-Out Schedule";
            Width = 650;
            Height = 390;
            MinWidth = 540;
            MinHeight = 330;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Setting-out schedule configuration",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Choose the schedule purpose and the elevation sources. Surface samples outside a surface boundary remain blank rather than inventing a value.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var accept = new Button
            {
                Content = "Continue",
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6)
            };
            accept.Click += delegate
            {
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            cancel.Click += delegate
            {
                Accepted = false;
                DialogResult = false;
            };
            buttons.Children.Add(accept);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int index = 0; index < 3; index++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _type = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Platform",
                    "Road Horizontal",
                    "Road Vertical",
                    "Junction",
                    "General"
                },
                SelectedIndex = 0,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _ground = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = 0,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _design = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = choices != null && choices.Count > 2 ? 2 : 0,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AddRow(grid, 0, "Schedule type", _type);
            AddRow(grid, 1, "Ground elevation source", _ground);
            AddRow(grid, 2, "Design elevation source", _design);
            root.Children.Add(grid);
        }

        public bool Accepted { get; private set; }
        public string ScheduleType
        {
            get { return Convert.ToString(_type.SelectedItem, CultureInfo.CurrentCulture); }
        }
        public SettingOutSurfaceChoice GroundChoice
        {
            get { return _ground.SelectedItem as SettingOutSurfaceChoice; }
        }
        public SettingOutSurfaceChoice DesignChoice
        {
            get { return _design.SelectedItem as SettingOutSurfaceChoice; }
        }

        private static void AddRow(Grid grid, int row, string label, Control control)
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 10, 4)
            };
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
    }
}