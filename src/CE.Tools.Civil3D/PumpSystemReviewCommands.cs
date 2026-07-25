using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PumpSystemReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary pump/system-curve screening for sewer rising mains, water and
    /// bulk-water systems. Manufacturer CSV data remains the user's responsibility.
    /// The workflow does not replace transient analysis, motor/electrical checks,
    /// manufacturer selection or professional hydraulic design.
    /// </summary>
    public sealed class PumpSystemReviewCommands
    {
        private const int MaximumCurveFiles = 100;
        private const int MaximumCurveRows = 10000;

        [CommandMethod("CE_TOOLS", "CE_PUMPSYSTEMTOOLS", CommandFlags.Modal)]
        public void PumpSystemTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nPump/system curve tools [Template/Single/Folder] <Single>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Template");
            options.Keywords.Add("Single");
            options.Keywords.Add("Folder");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Single";
            string command = Equal(choice, "Template")
                ? "CE_PUMPCURVETEMPLATE "
                : Equal(choice, "Folder")
                    ? "CE_PUMPFOLDERREVIEW "
                    : "CE_PUMPSYSTEMREVIEW ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PUMPCURVETEMPLATE", CommandFlags.Modal)]
        public void CreatePumpCurveTemplate()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var saveOptions = new PromptSaveFileOptions(
                "\nChoose the pump-curve CSV template path: ")
            {
                Filter = "Comma-separated values (*.csv)|*.csv",
                DialogCaption = "Create CE Tools Pump Curve Template",
                InitialFileName = "Pump-Manufacturer-Curve.csv"
            };
            PromptFileNameResult result = document.Editor.GetFileNameForSave(saveOptions);
            if (result.Status != PromptStatus.OK) return;
            string path = EnsureExtension(result.StringResult, ".csv");
            if (File.Exists(path))
            {
                document.Editor.WriteMessage(
                    "\nCE_PUMPCURVETEMPLATE stopped. Existing files are not overwritten.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(
                path,
                "FlowLps,HeadM,EfficiencyPercent,PowerKw,NpshRequiredM\r\n" +
                "0,35,0,0,2.0\r\n" +
                "10,30,65,5.5,2.5\r\n" +
                "20,22,78,8.0,3.2\r\n" +
                "30,10,70,11.0,4.5\r\n",
                new UTF8Encoding(false));
            document.Editor.WriteMessage(
                "\nCE_PUMPCURVETEMPLATE complete. Required columns: FlowLps and HeadM. Optional: EfficiencyPercent, PowerKw and NpshRequiredM. File={0}",
                path);
        }

        [CommandMethod("CE_TOOLS", "CE_PUMPSYSTEMREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReviewSinglePump()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            string curvePath;
            if (!PromptCurveFile(editor, out curvePath)) return;
            SystemReviewInput input;
            if (!PromptSystemInput(editor, out input)) return;

            try
            {
                PumpCurveData curve = ReadPumpCurve(curvePath);
                PumpSuitabilityReview review = PumpSystemCurve.Review(
                    curve.Points,
                    input.Definition,
                    input.NpshAvailableMetres,
                    input.MinimumNpshMarginMetres);
                IReadOnlyList<SystemCurvePoint> system = PumpSystemCurve.BuildSystemCurve(
                    curve.Points.Select(point => point.FlowLitresPerSecond),
                    input.Definition);
                List<IList<string>> rows = BuildSingleReviewRows(curve, input, review, system);
                string subtitle = review.DutyPoint == null
                    ? "No pump/system intersection was found inside the supplied curve. The source curve, system inputs and units require review."
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        "Duty point={0:N3} L/s at {1:N3} m. NPSH screening={2}. This is preliminary selection assistance only.",
                        review.DutyPoint.FlowLitresPerSecond,
                        review.DutyPoint.SystemHeadMetres,
                        review.NpshPass ? "Pass" : "Review");

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Pump and System Curve Review",
                    subtitle,
                    rows,
                    "CE TOOLS PUMP SYSTEM CURVE REVIEW");

                if (PromptYesNo(editor, "Export the pump/system review to Excel", true))
                {
                    string path;
                    if (PromptExcelPath(editor, "CE-Tools-Pump-System-Review.xlsx", out path))
                    {
                        SimpleXlsxWriter.Write(path, "Pump Review", rows);
                        editor.WriteMessage(
                            "\nCE_PUMPSYSTEMREVIEW workbook created: {0}",
                            path);
                    }
                }

                editor.WriteMessage(
                    "\nCE_PUMPSYSTEMREVIEW complete. Pump={0}; curve points={1}; status={2}.",
                    Path.GetFileName(curvePath),
                    curve.Points.Count,
                    review.DutyPoint == null ? "No intersection" : review.NpshPass ? "Screening pass" : "Review required");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_PUMPSYSTEMREVIEW failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PUMPFOLDERREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReviewPumpFolder()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            string selectedFile;
            if (!PromptCurveFile(editor, out selectedFile)) return;
            string folder = Path.GetDirectoryName(selectedFile) ?? Environment.CurrentDirectory;
            string[] files = Directory.GetFiles(folder, "*.csv")
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumCurveFiles + 1)
                .ToArray();
            if (files.Length == 0)
            {
                editor.WriteMessage("\nCE_PUMPFOLDERREVIEW stopped. No CSV files were found.");
                return;
            }
            if (files.Length > MaximumCurveFiles)
            {
                editor.WriteMessage(
                    "\nCE_PUMPFOLDERREVIEW stopped. The folder exceeds the {0}-file safety limit.",
                    MaximumCurveFiles);
                return;
            }

            SystemReviewInput input;
            if (!PromptSystemInput(editor, out input)) return;
            double targetFlow;
            if (!PromptNonNegativeDouble(
                    editor,
                    "Target design flow for ranking (L/s) <0 = no target>: ",
                    0.0,
                    out targetFlow))
                return;

            var candidates = new List<PumpCandidateReview>();
            foreach (string file in files)
            {
                try
                {
                    PumpCurveData curve = ReadPumpCurve(file);
                    PumpSuitabilityReview review = PumpSystemCurve.Review(
                        curve.Points,
                        input.Definition,
                        input.NpshAvailableMetres,
                        input.MinimumNpshMarginMetres);
                    candidates.Add(new PumpCandidateReview(
                        file,
                        curve.Points.Count,
                        review,
                        targetFlow > 0.0 && review.DutyPoint != null
                            ? Math.Abs(review.DutyPoint.FlowLitresPerSecond - targetFlow)
                            : (double?)null,
                        null));
                }
                catch (System.Exception exception)
                {
                    candidates.Add(new PumpCandidateReview(
                        file,
                        0,
                        null,
                        null,
                        exception.Message));
                }
            }

            List<PumpCandidateReview> ranked = candidates
                .OrderBy(item => item.Review == null || item.Review.DutyPoint == null ? 1 : 0)
                .ThenBy(item => item.Review != null && item.Review.NpshPass ? 0 : 1)
                .ThenBy(item => item.TargetFlowDifferenceLitresPerSecond ?? double.MaxValue)
                .ThenByDescending(item => item.Review == null || item.Review.DutyPoint == null
                    ? -1.0
                    : item.Review.DutyPoint.EfficiencyPercent ?? -1.0)
                .ThenBy(item => Path.GetFileName(item.FilePath), StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            List<IList<string>> rows = BuildFolderRows(ranked, input, targetFlow);
            PumpCandidateReview best = ranked.FirstOrDefault(item =>
                item.Review != null && item.Review.DutyPoint != null);
            string subtitle = best == null
                ? "No supplied curve intersects the system curve. Review manufacturer data and system assumptions."
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Highest-ranked candidate={0}; duty={1:N3} L/s at {2:N3} m; NPSH={3}. Ranking is preliminary and must not replace manufacturer/engineer selection.",
                    Path.GetFileName(best.FilePath),
                    best.Review.DutyPoint.FlowLitresPerSecond,
                    best.Review.DutyPoint.SystemHeadMetres,
                    best.Review.NpshPass ? "Pass" : "Review");

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Pump Candidate Folder Review",
                subtitle,
                rows,
                "CE TOOLS PUMP CANDIDATE REVIEW");

            if (PromptYesNo(editor, "Export the candidate ranking to Excel", true))
            {
                string path;
                if (PromptExcelPath(editor, "CE-Tools-Pump-Candidate-Ranking.xlsx", out path))
                {
                    SimpleXlsxWriter.Write(path, "Pump Candidates", rows);
                    editor.WriteMessage(
                        "\nCE_PUMPFOLDERREVIEW workbook created: {0}",
                        path);
                }
            }

            editor.WriteMessage(
                "\nCE_PUMPFOLDERREVIEW complete. Files={0}; valid intersections={1}; highest-ranked={2}.",
                ranked.Count,
                ranked.Count(item => item.Review != null && item.Review.DutyPoint != null),
                best == null ? "None" : Path.GetFileName(best.FilePath));
        }

        private static List<IList<string>> BuildSingleReviewRows(
            PumpCurveData curve,
            SystemReviewInput input,
            PumpSuitabilityReview review,
            IReadOnlyList<SystemCurvePoint> system)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "PUMP FILE", Path.GetFileName(curve.FilePath), "SYSTEM INPUT", "VALUE", "UNIT", "STATUS" },
                new List<string> { "Static head", Format(input.Definition.StaticHeadMetres), "Pipe length", Format(input.Definition.PipeLengthMetres), "m", string.Empty },
                new List<string> { "Internal diameter", Format(input.Definition.InternalDiameterMetres * 1000.0), "Hazen-Williams C", Format(input.Definition.HazenWilliamsC), "mm / -", string.Empty },
                new List<string> { "Minor-loss K", Format(input.Definition.MinorLossCoefficient), "NPSHa", FormatOptional(input.NpshAvailableMetres), "m", string.Empty },
                new List<string> { "Minimum NPSH margin", FormatOptional(input.MinimumNpshMarginMetres), "Curve points", curve.Points.Count.ToString(CultureInfo.InvariantCulture), "m / count", string.Empty }
            };

            if (review.DutyPoint == null)
            {
                rows.Add(new List<string> { "DUTY POINT", "No intersection", string.Empty, string.Empty, string.Empty, review.Message });
            }
            else
            {
                PumpDutyPoint duty = review.DutyPoint;
                rows.Add(new List<string> { "DUTY FLOW", Format(duty.FlowLitresPerSecond), "DUTY HEAD", Format(duty.SystemHeadMetres), "L/s / m", "Intersection inside supplied curve" });
                rows.Add(new List<string> { "Efficiency", FormatOptional(duty.EfficiencyPercent), "Power", FormatOptional(duty.PowerKilowatts), "% / kW", "Manufacturer interpolation" });
                rows.Add(new List<string> { "NPSHr", FormatOptional(duty.NpshRequiredMetres), "NPSH margin", FormatOptional(review.NpshMarginMetres), "m", review.NpshPass ? "Pass" : "Review" });
                rows.Add(new List<string> { "Head residual", Format(duty.HeadResidualMetres), "Review", review.Message, "m", string.Empty });
            }

            rows.Add(new List<string> { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty });
            rows.Add(new List<string> { "FLOW (L/s)", "PUMP HEAD (m)", "SYSTEM HEAD (m)", "EFFICIENCY (%)", "POWER (kW)", "NPSHr (m)" });
            for (int index = 0; index < curve.Points.Count; index++)
            {
                PumpCurvePoint pump = curve.Points[index];
                SystemCurvePoint systemPoint = system[index];
                rows.Add(new List<string>
                {
                    Format(pump.FlowLitresPerSecond),
                    Format(pump.HeadMetres),
                    Format(systemPoint.HeadMetres),
                    FormatOptional(pump.EfficiencyPercent),
                    FormatOptional(pump.PowerKilowatts),
                    FormatOptional(pump.NpshRequiredMetres)
                });
            }
            rows.Add(new List<string>
            {
                "BOUNDARY",
                "Preliminary hydraulic screening only",
                "Verify manufacturer curve/version, units, speed, impeller, motor, VFD, NPSH, transients and operating envelope.",
                string.Empty,
                string.Empty,
                string.Empty
            });
            return rows;
        }

        private static List<IList<string>> BuildFolderRows(
            IList<PumpCandidateReview> candidates,
            SystemReviewInput input,
            double targetFlow)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "RANK", "PUMP FILE", "STATUS", "DUTY FLOW (L/s)",
                    "DUTY HEAD (m)", "EFFICIENCY (%)", "POWER (kW)",
                    "NPSHr (m)", "NPSH MARGIN (m)", "TARGET FLOW DIFF (L/s)", "NOTES"
                }
            };
            for (int index = 0; index < candidates.Count; index++)
            {
                PumpCandidateReview candidate = candidates[index];
                PumpDutyPoint duty = candidate.Review == null ? null : candidate.Review.DutyPoint;
                rows.Add(new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    Path.GetFileName(candidate.FilePath),
                    candidate.Error != null
                        ? "Rejected"
                        : duty == null
                            ? "No intersection"
                            : candidate.Review.NpshPass ? "Screening pass" : "NPSH review",
                    duty == null ? string.Empty : Format(duty.FlowLitresPerSecond),
                    duty == null ? string.Empty : Format(duty.SystemHeadMetres),
                    duty == null ? string.Empty : FormatOptional(duty.EfficiencyPercent),
                    duty == null ? string.Empty : FormatOptional(duty.PowerKilowatts),
                    duty == null ? string.Empty : FormatOptional(duty.NpshRequiredMetres),
                    candidate.Review == null ? string.Empty : FormatOptional(candidate.Review.NpshMarginMetres),
                    FormatOptional(candidate.TargetFlowDifferenceLitresPerSecond),
                    candidate.Error ?? (candidate.Review == null ? string.Empty : candidate.Review.Message)
                });
            }
            rows.Add(new List<string>
            {
                "SYSTEM",
                "Static head=" + Format(input.Definition.StaticHeadMetres) + " m",
                "Length=" + Format(input.Definition.PipeLengthMetres) + " m",
                "Diameter=" + Format(input.Definition.InternalDiameterMetres * 1000.0) + " mm",
                "C=" + Format(input.Definition.HazenWilliamsC),
                "K=" + Format(input.Definition.MinorLossCoefficient),
                "NPSHa=" + FormatOptional(input.NpshAvailableMetres),
                "Margin=" + FormatOptional(input.MinimumNpshMarginMetres),
                targetFlow > 0.0 ? "Target=" + Format(targetFlow) + " L/s" : "No target flow",
                string.Empty,
                "Ranking is preliminary; verify complete manufacturer operating envelopes."
            });
            return rows;
        }

        private static PumpCurveData ReadPumpCurve(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3)
                throw new InvalidOperationException("Pump curve requires a header and at least two data rows.");
            if (lines.Length - 1 > MaximumCurveRows)
                throw new InvalidOperationException(
                    "Pump curve exceeds the " + MaximumCurveRows.ToString("N0", CultureInfo.InvariantCulture) + "-row safety limit.");

            List<string> headings = ParseCsvLine(lines[0]);
            var columns = headings
                .Select((name, index) => new { Name = NormalizeHeading(name), Index = index })
                .GroupBy(item => item.Name)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
            if (!columns.ContainsKey("FLOWLPS") || !columns.ContainsKey("HEADM"))
                throw new InvalidOperationException("Pump curve must contain FlowLps and HeadM columns.");

            var points = new List<PumpCurvePoint>();
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex])) continue;
                List<string> values = ParseCsvLine(lines[lineIndex]);
                double flow = Required(values, columns, "FLOWLPS", lineIndex + 1);
                double head = Required(values, columns, "HEADM", lineIndex + 1);
                points.Add(new PumpCurvePoint(
                    flow,
                    head,
                    Optional(values, columns, "EFFICIENCYPERCENT"),
                    Optional(values, columns, "POWERKW"),
                    Optional(values, columns, "NPSHREQUIREDM")));
            }
            if (points.Count < 2)
                throw new InvalidOperationException("Pump curve contains fewer than two valid points.");
            foreach (PumpCurvePoint point in points) point.Validate(points.IndexOf(point));
            points = points.OrderBy(point => point.FlowLitresPerSecond).ToList();
            for (int index = 1; index < points.Count; index++)
            {
                if (points[index].FlowLitresPerSecond <= points[index - 1].FlowLitresPerSecond)
                    throw new InvalidOperationException("Pump curve contains duplicate or decreasing flow values.");
            }
            return new PumpCurveData(path, points);
        }

        private static bool PromptCurveFile(Editor editor, out string path)
        {
            var options = new PromptOpenFileOptions(
                "\nSelect a manufacturer pump-curve CSV: ")
            {
                Filter = "Comma-separated values (*.csv)|*.csv",
                DialogCaption = "Select Pump Curve CSV"
            };
            PromptFileNameResult result = editor.GetFileNameForOpen(options);
            path = result.Status == PromptStatus.OK ? result.StringResult : string.Empty;
            return result.Status == PromptStatus.OK && File.Exists(path);
        }

        private static bool PromptSystemInput(Editor editor, out SystemReviewInput input)
        {
            double staticHead;
            double length;
            double diameterMillimetres;
            double c;
            double k;
            double npsha;
            double npshMargin;
            if (!PromptNonNegativeDouble(editor, "Static head (m) <10>: ", 10.0, out staticHead) ||
                !PromptNonNegativeDouble(editor, "Pipe length (m) <1000>: ", 1000.0, out length) ||
                !PromptPositiveDouble(editor, "Internal pipe diameter (mm) <200>: ", 200.0, out diameterMillimetres) ||
                !PromptPositiveDouble(editor, "Hazen-Williams C <130>: ", 130.0, out c) ||
                !PromptNonNegativeDouble(editor, "Total minor-loss K <2>: ", 2.0, out k) ||
                !PromptNonNegativeDouble(editor, "NPSH available (m) <0 = not supplied>: ", 0.0, out npsha) ||
                !PromptNonNegativeDouble(editor, "Minimum NPSH margin (m) <0.5>: ", 0.5, out npshMargin))
            {
                input = null;
                return false;
            }
            input = new SystemReviewInput(
                new SystemCurveDefinition(
                    staticHead,
                    length,
                    diameterMillimetres / 1000.0,
                    c,
                    k),
                npsha > 0.0 ? (double?)npsha : null,
                npsha > 0.0 ? (double?)npshMargin : null);
            return true;
        }

        private static bool PromptPositiveDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && IsFinite(value) && value > 0.0;
        }

        private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && IsFinite(value) && value >= 0.0;
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : Equal(result.StringResult, "Yes");
        }

        private static bool PromptExcelPath(Editor editor, string initialName, out string path)
        {
            var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Pump Review",
                InitialFileName = initialName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            path = result.Status == PromptStatus.OK
                ? EnsureExtension(result.StringResult, ".xlsx")
                : string.Empty;
            return result.Status == PromptStatus.OK;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else quoted = !quoted;
                }
                else if (character == ',' && !quoted)
                {
                    values.Add(current.ToString().Trim());
                    current.Clear();
                }
                else current.Append(character);
            }
            values.Add(current.ToString().Trim());
            return values;
        }

        private static string NormalizeHeading(string value)
        {
            return new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static double Required(
            IList<string> values,
            IDictionary<string, int> columns,
            string name,
            int row)
        {
            string text = Read(values, columns, name);
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !IsFinite(value))
                throw new InvalidOperationException("Invalid " + name + " at CSV row " + row + ".");
            return value;
        }

        private static double? Optional(
            IList<string> values,
            IDictionary<string, int> columns,
            string name)
        {
            string text = Read(values, columns, name);
            if (string.IsNullOrWhiteSpace(text)) return null;
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && IsFinite(value)
                ? (double?)value
                : null;
        }

        private static string Read(
            IList<string> values,
            IDictionary<string, int> columns,
            string name)
        {
            int index;
            return columns.TryGetValue(name, out index) && index >= 0 && index < values.Count
                ? values[index].Trim()
                : string.Empty;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static string FormatOptional(double? value)
        {
            return value.HasValue ? Format(value.Value) : string.Empty;
        }

        private static string EnsureExtension(string path, string extension)
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class PumpCurveData
    {
        public PumpCurveData(string filePath, List<PumpCurvePoint> points)
        {
            FilePath = filePath;
            Points = points;
        }

        public string FilePath { get; private set; }
        public List<PumpCurvePoint> Points { get; private set; }
    }

    internal sealed class SystemReviewInput
    {
        public SystemReviewInput(
            SystemCurveDefinition definition,
            double? npshAvailableMetres,
            double? minimumNpshMarginMetres)
        {
            Definition = definition;
            NpshAvailableMetres = npshAvailableMetres;
            MinimumNpshMarginMetres = minimumNpshMarginMetres;
        }

        public SystemCurveDefinition Definition { get; private set; }
        public double? NpshAvailableMetres { get; private set; }
        public double? MinimumNpshMarginMetres { get; private set; }
    }

    internal sealed class PumpCandidateReview
    {
        public PumpCandidateReview(
            string filePath,
            int pointCount,
            PumpSuitabilityReview review,
            double? targetFlowDifferenceLitresPerSecond,
            string error)
        {
            FilePath = filePath;
            PointCount = pointCount;
            Review = review;
            TargetFlowDifferenceLitresPerSecond = targetFlowDifferenceLitresPerSecond;
            Error = error;
        }

        public string FilePath { get; private set; }
        public int PointCount { get; private set; }
        public PumpSuitabilityReview Review { get; private set; }
        public double? TargetFlowDifferenceLitresPerSecond { get; private set; }
        public string Error { get; private set; }
    }
}
