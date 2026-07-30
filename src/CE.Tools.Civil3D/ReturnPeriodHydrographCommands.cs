using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ReturnPeriodHydrographCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary pre/post modified-rational hydrographs for the standard return
    /// periods requested by the CE Tools master list. Rainfall intensities remain
    /// project inputs and results are screening values, not calibrated hydrology.
    /// </summary>
    public sealed class ReturnPeriodHydrographCommands
    {
        private static readonly int[] ReturnPeriods =
        {
            2, 5, 10, 20, 25, 50, 100
        };

        private static readonly double[] DefaultIntensities =
        {
            25.0, 35.0, 45.0, 55.0, 60.0, 75.0, 90.0
        };

        private const double Tolerance = 1e-9;

        [CommandMethod(
            "CE_TOOLS",
            "CE_HYDROGRAPHPERIODS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReturnPeriodHydrographs()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double area;
            double preCoefficient;
            double postCoefficient;
            double preTc;
            double postTc;
            double duration;
            double timeStep;
            if (!PromptPositiveDouble(editor, "Catchment area (ha)", 10.0, out area) ||
                !PromptRatio(editor, "Pre-development runoff coefficient", 0.35, out preCoefficient) ||
                !PromptRatio(editor, "Post-development runoff coefficient", 0.75, out postCoefficient) ||
                !PromptPositiveDouble(editor, "Pre-development time of concentration (minutes)", 30.0, out preTc) ||
                !PromptPositiveDouble(editor, "Post-development time of concentration (minutes)", 20.0, out postTc) ||
                !PromptPositiveDouble(editor, "Storm duration (minutes)", 30.0, out duration) ||
                !PromptPositiveDouble(editor, "Hydrograph time step (minutes)", 2.0, out timeStep))
                return;

            var intensities = new Dictionary<int, double>();
            for (int index = 0; index < ReturnPeriods.Length; index++)
            {
                double intensity;
                if (!PromptPositiveDouble(
                        editor,
                        "1:" + ReturnPeriods[index] + " rainfall intensity (mm/h)",
                        DefaultIntensities[index],
                        out intensity))
                    return;
                intensities[ReturnPeriods[index]] = intensity;
            }

            int detailPeriod;
            if (!PromptDetailPeriod(editor, out detailPeriod)) return;

            try
            {
                var scenarios = new List<ReturnPeriodHydrographScenario>();
                foreach (int period in ReturnPeriods)
                {
                    double intensity = intensities[period];
                    HydrographSeries pre = ModifiedRationalHydrograph.Create(
                        area,
                        preCoefficient,
                        intensity,
                        preTc,
                        duration,
                        timeStep);
                    HydrographSeries post = ModifiedRationalHydrograph.Create(
                        area,
                        postCoefficient,
                        intensity,
                        postTc,
                        duration,
                        timeStep);
                    scenarios.Add(new ReturnPeriodHydrographScenario(
                        period,
                        intensity,
                        pre,
                        post,
                        IntegrateVolume(pre.Points),
                        IntegrateVolume(post.Points)));
                }

                List<IList<string>> rows = BuildRows(
                    scenarios,
                    detailPeriod);
                ReturnPeriodHydrographScenario maximum = scenarios
                    .OrderByDescending(item => item.Post.PeakFlowCubicMetresPerSecond)
                    .First();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Return-Period Pre/Post Hydrographs",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Modified-rational screening for 1:2 through 1:100. Area={0:N3} ha; maximum post peak={1:N3} m³/s at 1:{2}. Intensities are user-entered project inputs; results are not calibrated hydrographs.",
                        area,
                        maximum.Post.PeakFlowCubicMetresPerSecond,
                        maximum.ReturnPeriod),
                    rows,
                    "CE TOOLS RETURN PERIOD HYDROGRAPH REVIEW");

                if (PromptYesNo(editor, "Export the return-period hydrographs to Excel", true))
                {
                    string path;
                    if (PromptExcelPath(
                            editor,
                            "CE-Tools-Return-Period-Hydrographs.xlsx",
                            out path))
                    {
                        SimpleXlsxWriter.Write(
                            path,
                            "Return Periods",
                            rows);
                        editor.WriteMessage(
                            "\nCE_HYDROGRAPHPERIODS workbook created: {0}",
                            path);
                    }
                }

                editor.WriteMessage(
                    "\nCE_HYDROGRAPHPERIODS complete. Scenarios={0}; detailed period={1}; maximum post peak={2:N3} m3/s.",
                    scenarios.Count,
                    detailPeriod > 0 ? "1:" + detailPeriod : "None",
                    maximum.Post.PeakFlowCubicMetresPerSecond);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HYDROGRAPHPERIODS failed. {0}",
                    exception.Message);
            }
        }

        private static List<IList<string>> BuildRows(
            IList<ReturnPeriodHydrographScenario> scenarios,
            int detailPeriod)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "RETURN PERIOD", "INTENSITY (mm/h)",
                    "PRE PEAK (m3/s)", "POST PEAK (m3/s)",
                    "PEAK INCREASE (m3/s)", "PRE VOLUME (m3)",
                    "POST VOLUME (m3)", "VOLUME INCREASE (m3)"
                }
            };
            foreach (ReturnPeriodHydrographScenario scenario in scenarios)
            {
                rows.Add(new List<string>
                {
                    "1:" + scenario.ReturnPeriod,
                    Format(scenario.Intensity),
                    Format(scenario.Pre.PeakFlowCubicMetresPerSecond),
                    Format(scenario.Post.PeakFlowCubicMetresPerSecond),
                    Format(scenario.Post.PeakFlowCubicMetresPerSecond -
                        scenario.Pre.PeakFlowCubicMetresPerSecond),
                    Format(scenario.PreVolumeCubicMetres),
                    Format(scenario.PostVolumeCubicMetres),
                    Format(scenario.PostVolumeCubicMetres -
                        scenario.PreVolumeCubicMetres)
                });
            }

            ReturnPeriodHydrographScenario detail = scenarios.FirstOrDefault(
                item => item.ReturnPeriod == detailPeriod);
            if (detail == null) return rows;

            rows.Add(new List<string>
            {
                string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty
            });
            rows.Add(new List<string>
            {
                "1:" + detail.ReturnPeriod + " DETAILED TIME SERIES",
                "TIME (MIN)", "PRE Q (m3/s)", "POST Q (m3/s)",
                "INCREASE (m3/s)", string.Empty, string.Empty, string.Empty
            });
            foreach (double time in CombinedTimes(
                detail.Pre.Points,
                detail.Post.Points))
            {
                double pre = Interpolate(detail.Pre.Points, time);
                double post = Interpolate(detail.Post.Points, time);
                rows.Add(new List<string>
                {
                    "1:" + detail.ReturnPeriod,
                    Format(time),
                    Format(pre),
                    Format(post),
                    Format(post - pre),
                    string.Empty,
                    string.Empty,
                    string.Empty
                });
            }
            return rows;
        }

        private static double IntegrateVolume(
            IReadOnlyList<HydrographPoint> points)
        {
            double volume = 0.0;
            for (int index = 1; index < points.Count; index++)
            {
                HydrographPoint first = points[index - 1];
                HydrographPoint second = points[index];
                double seconds = (second.TimeMinutes - first.TimeMinutes) * 60.0;
                volume += (first.FlowCubicMetresPerSecond +
                    second.FlowCubicMetresPerSecond) * 0.5 * seconds;
            }
            return volume;
        }

        private static IEnumerable<double> CombinedTimes(
            IReadOnlyList<HydrographPoint> first,
            IReadOnlyList<HydrographPoint> second)
        {
            return first.Select(item => item.TimeMinutes)
                .Concat(second.Select(item => item.TimeMinutes))
                .Distinct()
                .OrderBy(item => item);
        }

        private static double Interpolate(
            IReadOnlyList<HydrographPoint> points,
            double time)
        {
            if (time <= points[0].TimeMinutes)
                return points[0].FlowCubicMetresPerSecond;
            for (int index = 1; index < points.Count; index++)
            {
                HydrographPoint current = points[index];
                if (time > current.TimeMinutes) continue;
                HydrographPoint previous = points[index - 1];
                double duration = current.TimeMinutes - previous.TimeMinutes;
                if (duration <= Tolerance)
                    return current.FlowCubicMetresPerSecond;
                double fraction = (time - previous.TimeMinutes) / duration;
                return previous.FlowCubicMetresPerSecond +
                    (current.FlowCubicMetresPerSecond -
                        previous.FlowCubicMetresPerSecond) * fraction;
            }
            return points[points.Count - 1].FlowCubicMetresPerSecond;
        }

        private static bool PromptDetailPeriod(
            Editor editor,
            out int period)
        {
            period = 100;
            var options = new PromptKeywordOptions(
                "\nDetailed time-series return period [P2/P5/P10/P20/P25/P50/P100/None] <P100>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("P2");
            options.Keywords.Add("P5");
            options.Keywords.Add("P10");
            options.Keywords.Add("P20");
            options.Keywords.Add("P25");
            options.Keywords.Add("P50");
            options.Keywords.Add("P100");
            options.Keywords.Add("None");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            if (result.Status == PromptStatus.None) return true;
            if (string.Equals(
                    result.StringResult,
                    "None",
                    StringComparison.OrdinalIgnoreCase))
            {
                period = 0;
                return true;
            }
            return int.TryParse(
                result.StringResult.Substring(1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out period);
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = defaultValue;
                return false;
            }
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK ||
                   result.Status == PromptStatus.None;
        }

        private static bool PromptRatio(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            if (!PromptPositiveDouble(editor, label, defaultValue, out value))
                return false;
            if (value <= 1.0) return true;
            editor.WriteMessage(
                "\n{0} must be greater than zero and no more than 1.0.",
                label);
            return false;
        }

        private static bool PromptYesNo(
            Editor editor,
            string question,
            bool defaultYes)
        {
            var options = new PromptKeywordOptions(
                "\n" + question + " [Yes/No] <" +
                (defaultYes ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultYes
                : string.Equals(
                    result.StringResult,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            path = string.Empty;
            var options = new PromptSaveFileOptions(
                "\nChoose the return-period hydrograph Excel workbook path: ")
            {
                DialogCaption = "Export CE Tools Return-Period Hydrographs",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialFileName = defaultName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return false;
            path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return true;
        }

        private static string Format(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ReturnPeriodHydrographScenario
    {
        public ReturnPeriodHydrographScenario(
            int returnPeriod,
            double intensity,
            HydrographSeries pre,
            HydrographSeries post,
            double preVolumeCubicMetres,
            double postVolumeCubicMetres)
        {
            ReturnPeriod = returnPeriod;
            Intensity = intensity;
            Pre = pre;
            Post = post;
            PreVolumeCubicMetres = preVolumeCubicMetres;
            PostVolumeCubicMetres = postVolumeCubicMetres;
        }

        public int ReturnPeriod { get; private set; }
        public double Intensity { get; private set; }
        public HydrographSeries Pre { get; private set; }
        public HydrographSeries Post { get; private set; }
        public double PreVolumeCubicMetres { get; private set; }
        public double PostVolumeCubicMetres { get; private set; }
    }
}
